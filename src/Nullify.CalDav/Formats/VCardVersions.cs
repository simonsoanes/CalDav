namespace Nullify.CalDav.Formats;

/// <summary>
/// Converts between vCard 3.0 (RFC 2426) and 4.0 (RFC 6350) so each CardDAV client receives the version it asks for
/// (RFC 6352 §8.6). Data is stored as uploaded; conversion happens on output.
/// </summary>
public static class VCardVersions
{
    private static readonly HashSet<string> BinaryProps = ["PHOTO", "LOGO", "SOUND", "KEY"];
    private static readonly HashSet<string> DropIn40 = ["NAME", "PROFILE", "MAILER", "AGENT", "CLASS", "SORT-STRING", "LABEL"];
    private static readonly HashSet<string> DropIn30 = ["KIND", "CLIENTPIDMAP", "XML", "MEMBER", "GENDER", "LANG"];
    private static readonly HashSet<string> DropParamsIn30 =
        ["VALUE", "PID", "ALTID", "CALSCALE", "SORT-AS", "GEO", "TZ", "LABEL", "LEVEL", "INDEX", "MEDIATYPE", "PREF", "CC"];

    /// <summary>Returns <paramref name="data"/> as vCard <paramref name="targetVersion"/> ("3.0" or "4.0").</summary>
    public static string Convert(string data, string targetVersion)
    {
        List<Component> roots;
        try { roots = ContentReader.Parse(data); }
        catch (ContentParseException) { return data; }
        if (roots.Count != 1 || roots[0].Name != "VCARD") return data;
        var card = roots[0];
        var version = card.Value("VERSION")?.Trim();
        if (version == targetVersion || version is not ("3.0" or "4.0")) return data;
        return ContentWriter.Write(targetVersion == "4.0" ? To40(card) : To30(card));
    }

    private static string MimeFor(string prop, string? type)
    {
        var t = (type ?? "").ToLowerInvariant();
        if (t.Contains('/')) return t;
        var major = prop == "SOUND" ? "audio" : prop == "KEY" ? "application" : "image";
        return t.Length == 0 ? major + (major == "image" ? "/jpeg" : "/octet-stream") : $"{major}/{t}";
    }

    private static Component To40(Component src)
    {
        var card = new Component("VCARD");
        foreach (var p in src.Properties)
        {
            if (DropIn40.Contains(p.Name)) continue;
            if (p.Name == "VERSION") { card.Properties.Add(new ContentLine("VERSION", "4.0")); continue; }

            var line = new ContentLine { Name = p.Name, Group = p.Group, Value = p.Value };
            var types = p.Parameters.Where(x => x.Name == "TYPE").SelectMany(x => x.Values).SelectMany(v => v.Split(',')).ToList();
            var isBinary = BinaryProps.Contains(p.Name) &&
                           p.Parameters.Any(x => x.Name == "ENCODING" && x.Values.Any(v => v.Equals("b", StringComparison.OrdinalIgnoreCase) || v.Equals("BASE64", StringComparison.OrdinalIgnoreCase)));
            if (isBinary)
            {
                var mime = MimeFor(p.Name, types.FirstOrDefault());
                line.Value = $"data:{mime};base64,{p.Value}";
                card.Properties.Add(line);
                continue;
            }

            var pref = types.Any(t => t.Equals("PREF", StringComparison.OrdinalIgnoreCase));
            types = types.Where(t => !t.Equals("PREF", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var param in p.Parameters)
            {
                if (param.Name == "TYPE" || param.Name == "CHARSET") continue;
                if (param.Name == "VALUE" && BinaryProps.Contains(p.Name)) continue;
                line.Parameters.Add(param);
            }
            if (types.Count > 0) line.Parameters.Insert(0, new ContentParameter("TYPE", types.Select(t => t.ToLowerInvariant()).ToList()));
            if (pref) line.Parameters.Add(new ContentParameter("PREF", ["1"]));
            card.Properties.Add(line);
        }
        return card;
    }

    private static Component To30(Component src)
    {
        var card = new Component("VCARD");
        foreach (var p in src.Properties)
        {
            if (DropIn30.Contains(p.Name)) continue;
            if (p.Name == "VERSION") { card.Properties.Add(new ContentLine("VERSION", "3.0")); continue; }

            var name = p.Name == "ANNIVERSARY" ? "X-ANNIVERSARY" : p.Name;
            var line = new ContentLine { Name = name, Group = p.Group, Value = p.Value };
            var types = p.Parameters.Where(x => x.Name == "TYPE").SelectMany(x => x.Values).SelectMany(v => v.Split(',')).ToList();
            if (p.Parameters.Any(x => x.Name == "PREF")) types.Add("PREF");

            if (BinaryProps.Contains(p.Name) && p.Value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && p.Value.Contains(";base64,"))
            {
                var mime = p.Value[5..p.Value.IndexOf(';')];
                line.Value = p.Value[(p.Value.IndexOf(',') + 1)..];
                line.Parameters.Add(new ContentParameter("ENCODING", ["b"]));
                line.Parameters.Add(new ContentParameter("TYPE", [mime.Split('/').Last().ToUpperInvariant()]));
                card.Properties.Add(line);
                continue;
            }
            if (BinaryProps.Contains(p.Name) && Uri.IsWellFormedUriString(p.Value, UriKind.Absolute))
            {
                line.Parameters.Add(new ContentParameter("VALUE", ["uri"]));
                card.Properties.Add(line);
                continue;
            }
            if (p.Name == "TEL" && line.Value.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) line.Value = line.Value[4..];
            if (p.Name == "BDAY" && line.Value.StartsWith("--", StringComparison.Ordinal) && line.Value.Length >= 6)
            {
                // vCard 3.0 cannot omit the year; use Apple's convention.
                var md = line.Value[2..].Replace("-", "");
                if (md.Length == 4)
                {
                    line.Value = $"1604-{md[..2]}-{md[2..]}";
                    line.Parameters.Add(new ContentParameter("X-APPLE-OMIT-YEAR", ["1604"]));
                    card.Properties.Add(line);
                    continue;
                }
            }

            foreach (var param in p.Parameters)
            {
                if (param.Name == "TYPE" || DropParamsIn30.Contains(param.Name)) continue;
                line.Parameters.Add(param);
            }
            if (types.Count > 0) line.Parameters.Insert(0, new ContentParameter("TYPE", types.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
            card.Properties.Add(line);
        }
        if (card.Property("N") == null)
            card.Properties.Add(new ContentLine("N", (card.Value("FN") ?? "") + ";;;;"));
        return card;
    }
}
