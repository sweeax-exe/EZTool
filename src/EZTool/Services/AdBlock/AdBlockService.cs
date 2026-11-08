using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using EZTool.Core;

namespace EZTool.Services.AdBlock;

public enum QueryStatus { Allowed, Cached, Blocked, Local, Failed }

public sealed record QueryEntry(DateTime Time, string Domain, ushort Type, QueryStatus Status, string? Via, int Ms);

public sealed record BlockListInfo(string Id, string Name, string Url, bool DefaultOn);

public sealed record UpstreamInfo(string Id, string Name, string Host, IPAddress[] Ips);

/// <summary>
/// Systemweiter Werbeblocker: ein kleiner DNS-Server auf 127.0.0.1:53, auf den Windows umgestellt wird.
/// Anfragen an Werbe- und Tracking-Domains beantwortet er mit 0.0.0.0, alles andere fragt er
/// verschlüsselt (DNS-over-HTTPS) beim gewählten Anbieter an. Lokale Namen (fritz.box, Druckername …)
/// gehen weiter an den Router.
/// </summary>
public sealed class AdBlockService
{
    // Lazy: die statischen Listen unten müssen vor dem ersten Konstruktor-Aufruf stehen
    private static readonly Lazy<AdBlockService> LazyInstance = new(() => new AdBlockService());
    public static AdBlockService Instance => LazyInstance.Value;

    public static readonly BlockListInfo[] Lists =
    [
        new("adguard", "AdGuard DNS filter", "https://adguardteam.github.io/AdGuardSDNSFilter/Filters/filter.txt", true),
        new("stevenblack", "Steven Black Unified", "https://raw.githubusercontent.com/StevenBlack/hosts/master/hosts", true),
        new("hagezi", "HaGeZi Multi NORMAL", "https://raw.githubusercontent.com/hagezi/dns-blocklists/main/adblock/multi.txt", false),
    ];

    public static readonly UpstreamInfo[] Upstreams =
    [
        new("cloudflare", "Cloudflare", "cloudflare-dns.com", [IPAddress.Parse("1.1.1.1"), IPAddress.Parse("1.0.0.1")]),
        new("quad9", "Quad9", "dns.quad9.net", [IPAddress.Parse("9.9.9.9"), IPAddress.Parse("149.112.112.112")]),
        new("google", "Google", "dns.google", [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("8.8.4.4")]),
    ];

    private static readonly string[] LocalTlds =
        ["local", "lan", "home", "home.arpa", "internal", "localdomain", "fritz.box", "box", "router", "intranet", "corp", "localhost"];

    private static readonly bool IsAdmin = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    private static string Folder => Path.Combine(SettingsService.Folder, "AdBlock");
    private static string ListFile(string id) => Path.Combine(Folder, "lists", id + ".txt");
    private static string StatsFile => Path.Combine(Folder, "stats.json");
    private static readonly TimeSpan ListMaxAge = TimeSpan.FromDays(3);

    // ───────────── Zustand ─────────────

    public bool IsRunning { get; private set; }
    public bool IsBusy { get; private set; }
    public string? LastError { get; private set; }
    public BlockFilter Filter { get; private set; } = BlockFilter.Empty;
    public DateTime? ListsUpdated { get; private set; }

    /// <summary>Etwas hat sich geändert (Status, Listen, Statistik). Kommt aus beliebigen Threads.</summary>
    public event Action? Changed;

    // Statistik (heute)
    private DateTime _statsDay = DateTime.Today;
    private long _queries, _blocked;
    private readonly ConcurrentDictionary<string, int> _topBlocked = new();
    public long QueriesToday => Interlocked.Read(ref _queries);
    public long BlockedToday => Interlocked.Read(ref _blocked);

    // Protokoll der letzten Anfragen
    private readonly ConcurrentQueue<QueryEntry> _log = new();
    private long _logVersion;
    public long LogVersion => Interlocked.Read(ref _logVersion);
    private const int LogMax = 400;

    // Eigene Regeln (aus den Einstellungen, als Mengen für schnelles Nachschlagen)
    private HashSet<string> _allow = [];
    private HashSet<string> _deny = [];

    private readonly ConcurrentDictionary<string, (byte[] Data, DateTime Expires)> _cache = new();
    private List<IPAddress> _localResolvers = [];
    private List<string> _localSuffixes = [];

    // Server
    private CancellationTokenSource? _cts;
    private readonly List<UdpClient> _udp = [];
    private readonly List<TcpListener> _tcp = [];

    // Upstream
    private HttpClient? _doh;
    private UpstreamInfo _upstream = Upstreams[0];
    private DateTime _dohDownUntil;
    private System.Threading.Timer? _housekeeping;
    private System.Threading.Timer? _networkDebounce;

    private AdBlockService()
    {
        LoadRules();
        LoadStats();
    }

    // ───────────── Ein / Aus ─────────────

    /// <summary>Beim Programmstart: Blocker wieder einschalten oder Reste eines Absturzes aufräumen.</summary>
    public void OnAppStart()
    {
        if (SettingsService.Current.AdBlockEnabled) _ = StartWithRetryAsync();
        else if (DnsSwitcher.HasBackup) Task.Run(DnsSwitcher.Restore);
    }

    /// <summary>Nach dem Hochfahren ist das Netz oft noch nicht da – ein paar Versuche abwarten.</summary>
    private async Task StartWithRetryAsync()
    {
        for (int i = 0; i < 8; i++)
        {
            if (!SettingsService.Current.AdBlockEnabled || IsRunning) return;
            if (await EnableAsync(saveSetting: false)) return;
            // Ohne Server kein Internet – zurückstellen. Außer der Port ist belegt: Dann läuft womöglich schon ein Blocker.
            if (i == 0 && DnsSwitcher.HasBackup && LastError != Loc.T("AdBlock.ErrorPort")) await Task.Run(DnsSwitcher.Restore);
            await Task.Delay(TimeSpan.FromSeconds(15));
        }
    }

    public async Task<bool> EnableAsync(bool saveSetting = true)
    {
        if (IsRunning) return true;
        if (!IsAdmin)
        {
            LastError = Loc.T("AdBlock.NeedsAdmin");
            Raise();
            return false;
        }
        IsBusy = true;
        LastError = null;
        Raise();
        try
        {
            await Task.Run(LoadFilterFromDisk);
            if (Filter.Count == 0 || ListsNeedUpdate()) await UpdateListsAsync(force: false);
            if (Filter.Count == 0)
            {
                LastError = Loc.T("AdBlock.ErrorLists");
                return false;
            }

            SelectUpstream(SettingsService.Current.AdBlockUpstream);
            if (!StartServer(out var error))
            {
                LastError = error;
                return false;
            }
            if (!await SelfTestAsync())
            {
                StopServer();
                LastError = Loc.T("AdBlock.ErrorUpstream");
                return false;
            }

            await Task.Run(DnsSwitcher.Apply);
            RefreshLocalResolvers();
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            _housekeeping = new System.Threading.Timer(_ => Housekeeping(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

            IsRunning = true;
            if (saveSetting)
            {
                SettingsService.Current.AdBlockEnabled = true;
                SettingsService.Save();
            }
            return true;
        }
        catch (Exception ex)
        {
            StopServer();
            LastError = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
            Raise();
        }
    }

    public async Task DisableAsync()
    {
        IsBusy = true;
        Raise();
        await Task.Run(StopInternal);
        SettingsService.Current.AdBlockEnabled = false;
        SettingsService.Save();
        IsBusy = false;
        Raise();
    }

    /// <summary>Beim Beenden von EZTool: DNS zurückstellen, Einstellung bleibt "an" für den nächsten Start.</summary>
    public void OnAppExit()
    {
        if (IsRunning) StopInternal();
    }

    private void StopInternal()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _housekeeping?.Dispose();
        _housekeeping = null;
        if (!_demo) DnsSwitcher.Restore(); // zuerst zurückstellen – erst danach den Server stoppen, sonst hängt das Internet kurz
        StopServer();
        IsRunning = false;
        if (!_demo) SaveStats();
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        // Neu verbundene Adapter (z. B. WLAN) ebenfalls umstellen – kurz warten, bis Windows fertig ist
        _networkDebounce?.Dispose();
        _networkDebounce = new System.Threading.Timer(_ =>
        {
            if (!IsRunning) return;
            DnsSwitcher.Apply();
            RefreshLocalResolvers();
        }, null, TimeSpan.FromSeconds(4), Timeout.InfiniteTimeSpan);
    }

    private void RefreshLocalResolvers()
    {
        _localResolvers = DnsSwitcher.LocalResolvers();
        _localSuffixes = DnsSwitcher.LocalSuffixes();
    }

    // ───────────── Server ─────────────

    private bool StartServer(out string? error)
    {
        error = null;
        StopServer();
        _cts = new CancellationTokenSource();
        try
        {
            foreach (var ip in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
            {
                var udp = new UdpClient(ip.AddressFamily) { ExclusiveAddressUse = true };
                // Ohne das wirft ReceiveAsync, wenn ein Client schon weg ist (ICMP "Port nicht erreichbar")
                try { udp.Client.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, [0, 0, 0, 0], null); } catch { /* egal */ }
                udp.Client.Bind(new IPEndPoint(ip, 53));
                _udp.Add(udp);
                _ = UdpLoopAsync(udp, _cts.Token);

                var tcp = new TcpListener(ip, 53) { ExclusiveAddressUse = true };
                tcp.Start();
                _tcp.Add(tcp);
                _ = TcpLoopAsync(tcp, _cts.Token);
            }
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            error = Loc.T("AdBlock.ErrorPort");
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        StopServer();
        return false;
    }

    private void StopServer()
    {
        _cts?.Cancel();
        foreach (var u in _udp) u.Dispose();
        foreach (var t in _tcp) t.Stop();
        _udp.Clear();
        _tcp.Clear();
        _cts = null;
        _cache.Clear();
    }

    private async Task UdpLoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            _ = Task.Run(async () =>
            {
                var resp = await ResolveAsync(r.Buffer, udp: true);
                if (resp == null) return;
                try { await udp.SendAsync(resp, r.RemoteEndPoint, ct); } catch { /* Client weg */ }
            });
        }
    }

    private async Task TcpLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var len = new byte[2];
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(TimeSpan.FromSeconds(10));
                        while (true)
                        {
                            await stream.ReadExactlyAsync(len, timeout.Token);
                            var msg = new byte[(len[0] << 8) | len[1]];
                            await stream.ReadExactlyAsync(msg, timeout.Token);
                            var resp = await ResolveAsync(msg, udp: false);
                            if (resp == null) return;
                            await stream.WriteAsync(new[] { (byte)(resp.Length >> 8), (byte)resp.Length }, timeout.Token);
                            await stream.WriteAsync(resp, timeout.Token);
                        }
                    }
                    catch { /* Verbindung zu */ }
                }
            });
        }
    }

    // ───────────── Auflösen ─────────────

    private async Task<byte[]?> ResolveAsync(byte[] query, bool udp)
    {
        var q = DnsMessage.ReadQuestion(query);
        if (q == null) return null;
        var sw = Stopwatch.StartNew();
        var name = q.Name.TrimEnd('.');
        CountQuery();

        byte[] Finish(byte[] resp, QueryStatus status, string? via = null)
        {
            Log(new QueryEntry(DateTime.Now, name, q.Type, status, via, (int)sw.ElapsedMilliseconds));
            if (status == QueryStatus.Blocked) CountBlocked(via ?? name);
            return udp && resp.Length > q.UdpSize ? DnsMessage.BuildTruncated(query, q) : resp;
        }

        // Firefox schaltet sein eigenes DNS-over-HTTPS ab, wenn diese Domain nicht existiert – sonst würde es am Blocker vorbeigehen.
        if (name == "use-application-dns.net") return Finish(DnsMessage.BuildRcode(query, q, DnsMessage.RcodeNxDomain), QueryStatus.Local);

        bool userAllowed = BlockFilter.MatchesSuffix(_allow, name);
        if (!userAllowed && (BlockFilter.MatchesSuffix(_deny, name) || Filter.IsBlocked(name)))
            return Finish(DnsMessage.BuildBlocked(query, q), QueryStatus.Blocked);

        if (IsLocalName(name))
        {
            var local = await QueryLocalAsync(query);
            return Finish(local ?? DnsMessage.BuildRcode(query, q, DnsMessage.RcodeNxDomain), QueryStatus.Local);
        }

        var key = $"{name}|{q.Type}|{q.Class}";
        if (_cache.TryGetValue(key, out var hit) && hit.Expires > DateTime.UtcNow)
        {
            var copy = (byte[])hit.Data.Clone();
            copy[0] = query[0];
            copy[1] = query[1];
            return Finish(copy, QueryStatus.Cached);
        }

        var resp = await QueryUpstreamAsync(query);
        if (resp == null) return Finish(DnsMessage.BuildRcode(query, q, DnsMessage.RcodeServFail), QueryStatus.Failed);
        resp[0] = query[0];
        resp[1] = query[1];

        var (ttl, cnames) = DnsMessage.Inspect(resp);
        if (!userAllowed)
        {
            // Tracker, die sich per CNAME hinter einer harmlosen Subdomain verstecken
            foreach (var c in cnames)
            {
                if (BlockFilter.MatchesSuffix(_allow, c)) continue;
                if (BlockFilter.MatchesSuffix(_deny, c) || Filter.IsBlocked(c))
                    return Finish(DnsMessage.BuildBlocked(query, q), QueryStatus.Blocked, c);
            }
        }

        byte rcode = DnsMessage.Rcode(resp);
        if (rcode is DnsMessage.RcodeOk or DnsMessage.RcodeNxDomain)
        {
            var seconds = rcode == DnsMessage.RcodeNxDomain || ttl == 0 ? 60 : Math.Clamp(ttl, 10, 300);
            if (_cache.Count > 20000) _cache.Clear();
            _cache[key] = ((byte[])resp.Clone(), DateTime.UtcNow.AddSeconds(seconds));
        }
        return Finish(resp, QueryStatus.Allowed);
    }

    private bool IsLocalName(string name)
    {
        if (!name.Contains('.')) return true; // einzelne Namen (Druckername, wpad …) gehören ins lokale Netz
        foreach (var tld in LocalTlds)
            if (name.EndsWith("." + tld, StringComparison.Ordinal) || name == tld) return true;
        foreach (var s in _localSuffixes)
            if (name.EndsWith("." + s, StringComparison.Ordinal)) return true;

        // Rückwärtssuche für private Adressen (192.168.x.x → Gerätename)
        if (name.EndsWith(".in-addr.arpa", StringComparison.Ordinal))
        {
            var p = name.Split('.');
            if (p.Length >= 4)
            {
                var a = p[^3];
                var b = p.Length >= 5 ? p[^4] : "";
                if (a is "10" or "127" || (a == "192" && b == "168") || (a == "169" && b == "254")
                    || (a == "172" && int.TryParse(b, out var n) && n is >= 16 and <= 31)) return true;
            }
        }
        if (name.EndsWith("d.f.ip6.arpa", StringComparison.Ordinal) || name.EndsWith("8.e.f.ip6.arpa", StringComparison.Ordinal)) return true;
        return false;
    }

    private async Task<byte[]?> QueryLocalAsync(byte[] query)
    {
        foreach (var ip in _localResolvers.Take(3))
        {
            var resp = await UdpQueryAsync(query, new IPEndPoint(ip, 53), TimeSpan.FromMilliseconds(1500));
            if (resp != null) return resp;
        }
        return null;
    }

    private async Task<byte[]?> QueryUpstreamAsync(byte[] query)
    {
        if (DateTime.UtcNow >= _dohDownUntil && _doh != null)
        {
            try
            {
                var copy = (byte[])query.Clone();
                copy[0] = 0; // RFC 8484: ID 0 für DoH
                copy[1] = 0;
                using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{_upstream.Host}/dns-query")
                {
                    Content = new ByteArrayContent(copy),
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                };
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var res = await _doh.SendAsync(req, cts.Token);
                if (res.IsSuccessStatusCode)
                {
                    var data = await res.Content.ReadAsByteArrayAsync(cts.Token);
                    if (data.Length >= 12) return data;
                }
            }
            catch { /* unten per UDP weiter */ }
            _dohDownUntil = DateTime.UtcNow.AddSeconds(60); // DoH gesperrt (Firewall?) → eine Minute klassisch fragen
        }

        foreach (var ip in _upstream.Ips)
        {
            var resp = await UdpQueryAsync(query, new IPEndPoint(ip, 53), TimeSpan.FromSeconds(2));
            if (resp != null) return resp;
        }
        return null;
    }

    private static async Task<byte[]?> UdpQueryAsync(byte[] query, IPEndPoint server, TimeSpan timeout)
    {
        try
        {
            using var udp = new UdpClient(server.AddressFamily);
            udp.Connect(server);
            await udp.SendAsync(query);
            using var cts = new CancellationTokenSource(timeout);
            while (true)
            {
                var r = await udp.ReceiveAsync(cts.Token);
                if (r.Buffer.Length >= 12 && r.Buffer[0] == query[0] && r.Buffer[1] == query[1]) return r.Buffer;
            }
        }
        catch
        {
            return null;
        }
    }

    private void SelectUpstream(string id)
    {
        _upstream = Upstreams.FirstOrDefault(u => u.Id == id) ?? Upstreams[0];
        _dohDownUntil = DateTime.MinValue;
        _doh?.Dispose();
        var ips = _upstream.Ips;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            EnableMultipleHttp2Connections = true,
            // Direkt die IP verbinden – der Name des DoH-Servers darf ja nicht über uns selbst aufgelöst werden.
            // Das Zertifikat wird trotzdem gegen den Hostnamen geprüft.
            ConnectCallback = async (ctx, ct) =>
            {
                Exception? last = null;
                foreach (var ip in ips)
                {
                    var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        t.CancelAfter(TimeSpan.FromSeconds(2));
                        await socket.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), t.Token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex)
                    {
                        socket.Dispose();
                        last = ex;
                    }
                }
                throw last ?? new SocketException((int)SocketError.HostUnreachable);
            },
        };
        _doh = new HttpClient(handler);
        _cache.Clear();
    }

    /// <summary>Anbieter wechseln, während der Blocker läuft.</summary>
    public void SetUpstream(string id)
    {
        SettingsService.Current.AdBlockUpstream = id;
        SettingsService.Save();
        if (IsRunning) SelectUpstream(id);
        Raise();
    }

    private async Task<bool> SelfTestAsync()
    {
        var query = DnsMessage.BuildQuery("example.com", DnsMessage.TypeA, (ushort)Random.Shared.Next(1, 65535));
        for (int i = 0; i < 2; i++)
        {
            var resp = await UdpQueryAsync(query, new IPEndPoint(IPAddress.Loopback, 53), TimeSpan.FromSeconds(5));
            if (resp != null && DnsMessage.Rcode(resp) == DnsMessage.RcodeOk) return true;
        }
        return false;
    }

    // ───────────── Listen ─────────────

    public bool IsUpdatingLists { get; private set; }

    private bool ListsNeedUpdate() => EnabledLists().Any(l =>
        !File.Exists(ListFile(l.Id)) || DateTime.Now - File.GetLastWriteTime(ListFile(l.Id)) > ListMaxAge);

    private static IEnumerable<BlockListInfo> EnabledLists() =>
        Lists.Where(l => SettingsService.Current.AdBlockLists.Contains(l.Id));

    private void LoadFilterFromDisk()
    {
        var texts = new List<string>();
        DateTime? newest = null;
        foreach (var l in EnabledLists())
        {
            var f = ListFile(l.Id);
            if (!File.Exists(f)) continue;
            texts.Add(File.ReadAllText(f));
            var t = File.GetLastWriteTime(f);
            if (newest == null || t > newest) newest = t;
        }
        Filter = BlockFilter.Parse(texts);
        ListsUpdated = newest;
        _cache.Clear();
    }

    /// <summary>Listen herunterladen (force = auch wenn sie noch frisch sind) und den Filter neu bauen.</summary>
    public async Task UpdateListsAsync(bool force)
    {
        if (IsUpdatingLists) return;
        IsUpdatingLists = true;
        Raise();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EZTool-AdBlock");
            Directory.CreateDirectory(Path.Combine(Folder, "lists"));
            foreach (var l in EnabledLists())
            {
                var f = ListFile(l.Id);
                if (!force && File.Exists(f) && DateTime.Now - File.GetLastWriteTime(f) < ListMaxAge) continue;
                try
                {
                    var text = await http.GetStringAsync(l.Url);
                    if (text.Length < 1000) continue; // offensichtlich keine Liste
                    await File.WriteAllTextAsync(f + ".tmp", text);
                    File.Move(f + ".tmp", f, true);
                }
                catch { /* alte Datei behalten */ }
            }
            await Task.Run(LoadFilterFromDisk);
        }
        finally
        {
            IsUpdatingLists = false;
            Raise();
        }
    }

    public async Task SetListEnabledAsync(string id, bool on)
    {
        var lists = SettingsService.Current.AdBlockLists;
        if (on && !lists.Contains(id)) lists.Add(id);
        if (!on) lists.Remove(id);
        SettingsService.Save();
        if (on && !File.Exists(ListFile(id))) await UpdateListsAsync(force: false);
        else await Task.Run(LoadFilterFromDisk);
        Raise();
    }

    // ───────────── Eigene Regeln ─────────────

    private void LoadRules()
    {
        _allow = new HashSet<string>(SettingsService.Current.AdBlockAllow, StringComparer.Ordinal);
        _deny = new HashSet<string>(SettingsService.Current.AdBlockDeny, StringComparer.Ordinal);
    }

    /// <summary>Domain freigeben (true) oder sperren (false). Entfernt sie aus der jeweils anderen Liste.</summary>
    public void AddRule(string domain, bool allow)
    {
        var s = SettingsService.Current;
        s.AdBlockAllow.Remove(domain);
        s.AdBlockDeny.Remove(domain);
        (allow ? s.AdBlockAllow : s.AdBlockDeny).Insert(0, domain);
        SettingsService.Save();
        LoadRules();
        _cache.Clear();
        if (IsRunning) Task.Run(DnsSwitcher.FlushCache);
        Raise();
    }

    public void RemoveRule(string domain)
    {
        SettingsService.Current.AdBlockAllow.Remove(domain);
        SettingsService.Current.AdBlockDeny.Remove(domain);
        SettingsService.Save();
        LoadRules();
        _cache.Clear();
        if (IsRunning) Task.Run(DnsSwitcher.FlushCache);
        Raise();
    }

    /// <summary>Warum wird diese Domain blockiert? null = nicht blockiert.</summary>
    public bool IsBlockedNow(string domain) =>
        !BlockFilter.MatchesSuffix(_allow, domain) && (BlockFilter.MatchesSuffix(_deny, domain) || Filter.IsBlocked(domain));

    // ───────────── Statistik & Protokoll ─────────────

    public IReadOnlyList<QueryEntry> RecentQueries() => _log.Reverse().ToList();

    public IReadOnlyList<(string Domain, int Count)> TopBlocked(int n) =>
        _topBlocked.OrderByDescending(p => p.Value).Take(n).Select(p => (p.Key, p.Value)).ToList();

    private void CountQuery()
    {
        RollDay();
        Interlocked.Increment(ref _queries);
    }

    private void CountBlocked(string domain)
    {
        Interlocked.Increment(ref _blocked);
        if (_topBlocked.Count < 5000 || _topBlocked.ContainsKey(domain)) _topBlocked.AddOrUpdate(domain, 1, (_, c) => c + 1);
    }

    private void RollDay()
    {
        if (DateTime.Today == _statsDay) return;
        _statsDay = DateTime.Today;
        Interlocked.Exchange(ref _queries, 0);
        Interlocked.Exchange(ref _blocked, 0);
        _topBlocked.Clear();
    }

    private void Log(QueryEntry e)
    {
        _log.Enqueue(e);
        while (_log.Count > LogMax) _log.TryDequeue(out _);
        Interlocked.Increment(ref _logVersion);
    }

    private void Housekeeping()
    {
        SaveStats();
        foreach (var (k, v) in _cache)
            if (v.Expires < DateTime.UtcNow) _cache.TryRemove(k, out _);
        if (ListsNeedUpdate()) _ = UpdateListsAsync(force: false);
    }

    private sealed record StatsDto(DateTime Day, long Queries, long Blocked, Dictionary<string, int> Top);

    private void SaveStats()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var top = _topBlocked.OrderByDescending(p => p.Value).Take(100).ToDictionary(p => p.Key, p => p.Value);
            File.WriteAllText(StatsFile, JsonSerializer.Serialize(new StatsDto(_statsDay, QueriesToday, BlockedToday, top)));
        }
        catch { /* egal */ }
    }

    private void LoadStats()
    {
        try
        {
            if (!File.Exists(StatsFile)) return;
            var s = JsonSerializer.Deserialize<StatsDto>(File.ReadAllText(StatsFile));
            if (s == null || s.Day != DateTime.Today) return;
            _queries = s.Queries;
            _blocked = s.Blocked;
            foreach (var (k, v) in s.Top) _topBlocked[k] = v;
        }
        catch { /* egal */ }
    }

    /// <summary>Filter beim Öffnen der Seite laden, damit die Anzahl Domains auch bei ausgeschaltetem Blocker stimmt.</summary>
    public void EnsureFilterLoaded()
    {
        if (Filter.Count == 0 && !IsBusy) Task.Run(() => { LoadFilterFromDisk(); Raise(); });
    }

    private void Raise() => Changed?.Invoke();

    // ───────────── Screenshot-Modus ─────────────

    private bool _demo;

    private static readonly string[] DemoDomains =
    [
        "www.bbc.com", "static.files.bbci.co.uk", "securepubads.g.doubleclick.net", "ichef.bbci.co.uk", "pagead2.googlesyndication.com",
        "www.googletagmanager.com", "www.google-analytics.com", "c.amazon-adsystem.com", "fonts.googleapis.com", "ib.adnxs.com",
        "fonts.gstatic.com", "connect.facebook.net", "sb.scorecardresearch.com", "github.com", "api.github.com", "stats.g.doubleclick.net",
        "avatars.githubusercontent.com", "bat.bing.com", "www.wikipedia.org", "upload.wikimedia.org", "tpc.googlesyndication.com",
        "securepubads.g.doubleclick.net", "pagead2.googlesyndication.com", "www.google-analytics.com", "cdn.jsdelivr.net",
        "securepubads.g.doubleclick.net", "c.amazon-adsystem.com", "ads.pubmatic.com", "www.youtube.com", "i.ytimg.com",
        "securepubads.g.doubleclick.net", "pagead2.googlesyndication.com", "www.googletagmanager.com",
    ];

    /// <summary>
    /// Nur für die README-Bilder: Server starten, ohne Windows umzustellen, und echte Anfragen einer typischen
    /// Webseite durchschicken. Die Statistik des Nutzers bleibt unangetastet.
    /// </summary>
    public async Task RunScreenshotDemoAsync()
    {
        _demo = true;
        Interlocked.Exchange(ref _queries, 0);
        Interlocked.Exchange(ref _blocked, 0);
        _topBlocked.Clear();
        await Task.Run(LoadFilterFromDisk);
        if (Filter.Count == 0) await UpdateListsAsync(force: false);
        SelectUpstream(SettingsService.Current.AdBlockUpstream);
        if (!StartServer(out _)) return;
        IsRunning = true;
        Raise();
        foreach (var d in DemoDomains)
        {
            await UdpQueryAsync(DnsMessage.BuildQuery(d, DnsMessage.TypeA, (ushort)Random.Shared.Next(1, 65000)), new IPEndPoint(IPAddress.Loopback, 53), TimeSpan.FromSeconds(3));
            await Task.Delay(60);
        }
        Raise();
    }
}
