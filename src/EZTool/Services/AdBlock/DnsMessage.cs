using System.Buffers.Binary;
using System.Text;

namespace EZTool.Services.AdBlock;

/// <summary>
/// Das Nötigste aus dem DNS-Protokoll (RFC 1035): Frage lesen, Antworten bauen, TTL und CNAME-Ziele auslesen.
/// Alles arbeitet direkt auf den Bytes – Anfragen werden unverändert an den Upstream weitergereicht.
/// </summary>
public static class DnsMessage
{
    public const ushort TypeA = 1, TypeCname = 5, TypeAaaa = 28, TypeOpt = 41;
    public const byte RcodeOk = 0, RcodeServFail = 2, RcodeNxDomain = 3;

    public sealed record Question(string Name, ushort Type, ushort Class, int End, ushort UdpSize);

    /// <summary>Liest die erste Frage einer Anfrage. null = kein gültiges DNS-Paket.</summary>
    public static Question? ReadQuestion(ReadOnlySpan<byte> msg)
    {
        if (msg.Length < 12) return null;
        if ((msg[2] & 0x80) != 0) return null; // ist eine Antwort, keine Anfrage
        int qd = BinaryPrimitives.ReadUInt16BigEndian(msg[4..]);
        if (qd < 1) return null;
        int pos = 12;
        var name = ReadName(msg, ref pos);
        if (name == null || pos + 4 > msg.Length) return null;
        ushort type = BinaryPrimitives.ReadUInt16BigEndian(msg[pos..]);
        ushort cls = BinaryPrimitives.ReadUInt16BigEndian(msg[(pos + 2)..]);
        int end = pos + 4;

        // EDNS: Puffergröße des Clients aus dem OPT-Eintrag im Zusatzteil
        ushort udp = 512;
        int an = BinaryPrimitives.ReadUInt16BigEndian(msg[6..]);
        int ns = BinaryPrimitives.ReadUInt16BigEndian(msg[8..]);
        int ar = BinaryPrimitives.ReadUInt16BigEndian(msg[10..]);
        int p = end;
        for (int i = 0; i < an + ns + ar && p < msg.Length; i++)
        {
            if (!SkipName(msg, ref p) || p + 10 > msg.Length) break;
            ushort rtype = BinaryPrimitives.ReadUInt16BigEndian(msg[p..]);
            ushort rclass = BinaryPrimitives.ReadUInt16BigEndian(msg[(p + 2)..]);
            int rdlen = BinaryPrimitives.ReadUInt16BigEndian(msg[(p + 8)..]);
            if (rtype == TypeOpt) udp = Math.Max((ushort)512, rclass);
            p += 10 + rdlen;
        }
        return new Question(name.ToLowerInvariant(), type, cls, end, udp);
    }

    /// <summary>
    /// Antwort für eine blockierte Domain: A → 0.0.0.0, AAAA → ::, sonst eine leere Antwort.
    /// Browser bekommen so sofort "nicht erreichbar" statt auf einen Timeout zu warten.
    /// </summary>
    public static byte[] BuildBlocked(ReadOnlySpan<byte> query, Question q)
    {
        int rdlen = q.Type switch { TypeA => 4, TypeAaaa => 16, _ => 0 };
        bool answer = rdlen > 0 && q.Class == 1;
        var res = new byte[q.End + (answer ? 12 + rdlen : 0)];
        query[..q.End].CopyTo(res);
        WriteHeader(res, query, RcodeOk, answer ? 1 : 0);
        if (answer)
        {
            int p = q.End;
            res[p] = 0xC0; res[p + 1] = 0x0C;                                   // Name = Zeiger auf die Frage
            BinaryPrimitives.WriteUInt16BigEndian(res.AsSpan(p + 2), q.Type);
            BinaryPrimitives.WriteUInt16BigEndian(res.AsSpan(p + 4), 1);         // IN
            BinaryPrimitives.WriteUInt32BigEndian(res.AsSpan(p + 6), 60);        // TTL
            BinaryPrimitives.WriteUInt16BigEndian(res.AsSpan(p + 10), (ushort)rdlen);
            // RDATA bleibt 0 → 0.0.0.0 bzw. ::
        }
        return res;
    }

    /// <summary>Antwort nur mit Statuscode (NXDOMAIN, SERVFAIL …), ohne Einträge.</summary>
    public static byte[] BuildRcode(ReadOnlySpan<byte> query, Question q, byte rcode)
    {
        var res = new byte[q.End];
        query[..q.End].CopyTo(res);
        WriteHeader(res, query, rcode, 0);
        return res;
    }

    /// <summary>Gekürzte Antwort mit TC-Bit: Der Client wiederholt die Anfrage dann per TCP.</summary>
    public static byte[] BuildTruncated(ReadOnlySpan<byte> query, Question q)
    {
        var res = BuildRcode(query, q, RcodeOk);
        res[2] |= 0x02;
        return res;
    }

    private static void WriteHeader(byte[] res, ReadOnlySpan<byte> query, byte rcode, int answers)
    {
        res[0] = query[0];
        res[1] = query[1];
        res[2] = (byte)(0x80 | (query[2] & 0x79));  // QR=1, Opcode und RD übernehmen
        res[3] = (byte)(0x80 | rcode);               // RA=1
        BinaryPrimitives.WriteUInt16BigEndian(res.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(res.AsSpan(6), (ushort)answers);
        BinaryPrimitives.WriteUInt16BigEndian(res.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt16BigEndian(res.AsSpan(10), 0);
    }

    public static byte Rcode(ReadOnlySpan<byte> msg) => msg.Length >= 4 ? (byte)(msg[3] & 0x0F) : RcodeServFail;

    /// <summary>
    /// Wertet eine Upstream-Antwort aus: kleinste TTL (für den Cache) und alle CNAME-Ziele
    /// (damit Tracker, die sich hinter einem CNAME verstecken, trotzdem erkannt werden).
    /// </summary>
    public static (uint MinTtl, List<string> Cnames) Inspect(ReadOnlySpan<byte> msg)
    {
        var cnames = new List<string>();
        uint min = uint.MaxValue;
        if (msg.Length < 12) return (0, cnames);
        int qd = BinaryPrimitives.ReadUInt16BigEndian(msg[4..]);
        int an = BinaryPrimitives.ReadUInt16BigEndian(msg[6..]);
        int ns = BinaryPrimitives.ReadUInt16BigEndian(msg[8..]);
        int p = 12;
        for (int i = 0; i < qd; i++)
        {
            if (!SkipName(msg, ref p)) return (0, cnames);
            p += 4;
        }
        for (int i = 0; i < an + ns && p < msg.Length; i++)
        {
            if (!SkipName(msg, ref p) || p + 10 > msg.Length) break;
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(msg[p..]);
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(msg[(p + 4)..]);
            int rdlen = BinaryPrimitives.ReadUInt16BigEndian(msg[(p + 8)..]);
            int rd = p + 10;
            if (type != TypeOpt) min = Math.Min(min, ttl);
            if (type == TypeCname && i < an)
            {
                int q = rd;
                var target = ReadName(msg, ref q);
                if (target != null) cnames.Add(target.ToLowerInvariant());
            }
            p = rd + rdlen;
        }
        return (min == uint.MaxValue ? 0 : min, cnames);
    }

    /// <summary>Liest einen (evtl. komprimierten) Namen ab pos und setzt pos hinter den Namen.</summary>
    public static string? ReadName(ReadOnlySpan<byte> msg, ref int pos)
    {
        var sb = new StringBuilder();
        int p = pos, jumps = 0;
        bool jumped = false;
        while (true)
        {
            if (p >= msg.Length) return null;
            int len = msg[p];
            if (len == 0)
            {
                if (!jumped) pos = p + 1;
                break;
            }
            if ((len & 0xC0) == 0xC0)
            {
                if (p + 1 >= msg.Length || ++jumps > 20) return null;
                if (!jumped) pos = p + 2;
                jumped = true;
                p = ((len & 0x3F) << 8) | msg[p + 1];
                continue;
            }
            if (len > 63 || p + 1 + len > msg.Length) return null;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(msg.Slice(p + 1, len)));
            p += 1 + len;
            if (sb.Length > 255) return null;
        }
        return sb.ToString();
    }

    private static bool SkipName(ReadOnlySpan<byte> msg, ref int pos)
    {
        while (pos < msg.Length)
        {
            int len = msg[pos];
            if (len == 0) { pos++; return true; }
            if ((len & 0xC0) == 0xC0) { pos += 2; return pos <= msg.Length; }
            pos += 1 + len;
        }
        return false;
    }

    /// <summary>Baut eine einfache A-Anfrage (für den Selbsttest).</summary>
    public static byte[] BuildQuery(string name, ushort type, ushort id)
    {
        var ms = new MemoryStream();
        Span<byte> h = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(h, id);
        h[2] = 0x01; // RD
        BinaryPrimitives.WriteUInt16BigEndian(h[4..], 1);
        ms.Write(h);
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var b = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)b.Length);
            ms.Write(b);
        }
        ms.WriteByte(0);
        Span<byte> t = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(t, type);
        BinaryPrimitives.WriteUInt16BigEndian(t[2..], 1);
        ms.Write(t);
        return ms.ToArray();
    }
}
