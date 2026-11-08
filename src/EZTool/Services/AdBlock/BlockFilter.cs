namespace EZTool.Services.AdBlock;

/// <summary>
/// Die geladenen Blocklisten. Unveränderlich – bei einem Update wird ein neuer Filter gebaut und ausgetauscht,
/// so kann der DNS-Server ohne Sperren lesen.
/// </summary>
public sealed class BlockFilter
{
    /// <summary>Nur genau diese Domain (hosts-Dateien, einfache Domainlisten).</summary>
    private readonly HashSet<string> _exact;
    /// <summary>Domain samt allen Subdomains (Adblock-Syntax "||domain^").</summary>
    private readonly HashSet<string> _wildcard;
    /// <summary>Ausnahmen aus den Listen ("@@||domain^") – die Listenbetreiber geben damit wichtige Dienste frei.</summary>
    private readonly HashSet<string> _exceptions;

    public int Count => _exact.Count + _wildcard.Count;

    public static BlockFilter Empty { get; } = new([], [], []);

    private BlockFilter(HashSet<string> exact, HashSet<string> wildcard, HashSet<string> exceptions)
    {
        _exact = exact;
        _wildcard = wildcard;
        _exceptions = exceptions;
    }

    public bool IsBlocked(string name) =>
        !MatchesSuffix(_exceptions, name) && (_exact.Contains(name) || MatchesSuffix(_wildcard, name));

    /// <summary>true, wenn die Domain selbst oder eine übergeordnete Domain in der Menge steht.</summary>
    public static bool MatchesSuffix(IReadOnlySet<string> set, string name)
    {
        if (set.Count == 0) return false;
        if (set.Contains(name)) return true;
        int i = name.IndexOf('.');
        while (i >= 0 && i < name.Length - 1)
        {
            // Spans vermeiden hier keine Allokation bei HashSet<string> – Substring ist bei ~3 Ebenen günstig genug
            if (set.Contains(name[(i + 1)..])) return true;
            i = name.IndexOf('.', i + 1);
        }
        return false;
    }

    /// <summary>Baut einen Filter aus mehreren Listen-Texten. Versteht hosts-, Domain- und Adblock-Format.</summary>
    public static BlockFilter Parse(IEnumerable<string> texts)
    {
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var wildcard = new HashSet<string>(StringComparer.Ordinal);
        var exceptions = new HashSet<string>(StringComparer.Ordinal);
        var bad = new HashSet<string>(StringComparer.Ordinal);

        foreach (var text in texts)
        {
            using var reader = new StringReader(text);
            while (reader.ReadLine() is { } raw)
            {
                var line = raw.Trim();
                if (line.Length < 4 || line[0] is '!' or '#' or '[' or '/') continue;

                if (line.StartsWith("@@||", StringComparison.Ordinal))
                {
                    if (AdblockDomain(line[4..], out var d, out _)) exceptions.Add(d);
                    continue;
                }
                if (line.StartsWith("||", StringComparison.Ordinal))
                {
                    if (AdblockDomain(line[2..], out var d, out bool badfilter))
                    {
                        if (badfilter) bad.Add(d); else wildcard.Add(d);
                    }
                    continue;
                }
                if (line.StartsWith("*.", StringComparison.Ordinal))
                {
                    var d = line[2..].ToLowerInvariant();
                    if (IsDomain(d)) wildcard.Add(d);
                    continue;
                }

                // hosts-Format: "0.0.0.0 domain" – Kommentar am Zeilenende abschneiden
                int hash = line.IndexOf('#');
                if (hash > 0) line = line[..hash].TrimEnd();
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                string? domain = parts.Length switch
                {
                    1 => parts[0],
                    >= 2 when parts[0] is "0.0.0.0" or "127.0.0.1" or "::" or "::1" => parts[1],
                    _ => null,
                };
                if (domain == null) continue;
                domain = domain.ToLowerInvariant().TrimEnd('.');
                if (domain is "localhost" or "localhost.localdomain" or "local" or "broadcasthost" or "0.0.0.0") continue;
                if (IsDomain(domain)) exact.Add(domain);
            }
        }

        foreach (var d in bad) wildcard.Remove(d);
        return new BlockFilter(exact, wildcard, exceptions);
    }

    /// <summary>"domain^", "domain^$important", "domain^|" → domain. Regeln mit anderen Modifikatoren werden übersprungen.</summary>
    private static bool AdblockDomain(string rest, out string domain, out bool badfilter)
    {
        domain = "";
        badfilter = false;
        int caret = rest.IndexOf('^');
        if (caret <= 0) return false;
        var tail = rest[(caret + 1)..].TrimStart('|');
        if (tail.Length > 0)
        {
            if (tail == "$badfilter") badfilter = true;
            else if (tail != "$important") return false;
        }
        domain = rest[..caret].ToLowerInvariant();
        return IsDomain(domain);
    }

    public static bool IsDomain(string d)
    {
        if (d.Length is < 3 or > 253 || !d.Contains('.') || d[0] == '.' || d[^1] == '.' || d[0] == '-') return false;
        bool allDigits = true;
        foreach (var c in d)
        {
            if (c is >= 'a' and <= 'z' or '-' or '_') allDigits = false;
            else if (c is not (>= '0' and <= '9' or '.')) return false;
        }
        return !allDigits; // reine IP-Adressen sind keine Domains
    }

    /// <summary>Eingabe aus der Oberfläche säubern: "https://www.Example.com/pfad" → "www.example.com".</summary>
    public static string? Normalize(string input)
    {
        var s = input.Trim().ToLowerInvariant();
        int scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        int slash = s.IndexOfAny(['/', '?', '#', ':']);
        if (slash >= 0) s = s[..slash];
        if (s.StartsWith("*.", StringComparison.Ordinal)) s = s[2..];
        s = s.Trim('.');
        return IsDomain(s) ? s : null;
    }
}
