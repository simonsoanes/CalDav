using System.Text;

namespace Nullify.CalDav.Formats;

/// <summary>A single property line of an iCalendar (RFC 5545) or vCard (RFC 6350) object.</summary>
public sealed class ContentLine
{
    /// <summary>Upper-cased property name without any vCard group prefix.</summary>
    public string Name { get; set; } = "";

    /// <summary>vCard group prefix ("item1" in "item1.EMAIL"), if any.</summary>
    public string? Group { get; set; }

    public List<ContentParameter> Parameters { get; } = [];
    public string Value { get; set; } = "";

    public ContentLine() { }

    public ContentLine(string name, string value)
    {
        Name = name.ToUpperInvariant();
        Value = value;
    }

    public string? GetParameter(string name) =>
        Parameters.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Values.FirstOrDefault();

    public bool HasParameter(string name) =>
        Parameters.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Value with TEXT escaping (\n, \, \; \\) removed.</summary>
    public string TextValue => UnescapeText(Value);

    public static string UnescapeText(string value)
    {
        if (value.IndexOf('\\') < 0) return value;
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                var n = value[++i];
                sb.Append(n is 'n' or 'N' ? '\n' : n);
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    public static string EscapeText(string value) =>
        value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n");
}

public sealed class ContentParameter(string name, List<string> values)
{
    public string Name { get; } = name.ToUpperInvariant();
    public List<string> Values { get; } = values;
}

/// <summary>A BEGIN:X ... END:X block.</summary>
public sealed class Component(string name)
{
    public string Name { get; set; } = name.ToUpperInvariant();
    public List<ContentLine> Properties { get; } = [];
    public List<Component> Children { get; } = [];

    public ContentLine? Property(string name) =>
        Properties.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<ContentLine> PropertiesNamed(string name) =>
        Properties.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public string? Value(string name) => Property(name)?.Value;

    public IEnumerable<Component> ChildrenNamed(string name) =>
        Children.Where(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

public sealed class ContentParseException(string message) : Exception(message);

public static class ContentReader
{
    /// <summary>Parses the text into its top level components.</summary>
    public static List<Component> Parse(string text)
    {
        var roots = new List<Component>();
        var stack = new Stack<Component>();

        foreach (var line in Unfold(text))
        {
            if (line.Length == 0) continue;
            var cl = ParseLine(line);

            if (cl.Name == "BEGIN")
            {
                var comp = new Component(cl.Value.Trim());
                if (stack.Count > 0) stack.Peek().Children.Add(comp);
                else roots.Add(comp);
                stack.Push(comp);
            }
            else if (cl.Name == "END")
            {
                if (stack.Count == 0 || !stack.Peek().Name.Equals(cl.Value.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new ContentParseException($"Unexpected END:{cl.Value}");
                stack.Pop();
            }
            else
            {
                if (stack.Count == 0)
                    throw new ContentParseException($"Property '{cl.Name}' outside of a component");
                stack.Peek().Properties.Add(cl);
            }
        }

        if (stack.Count > 0)
            throw new ContentParseException($"Missing END:{stack.Peek().Name}");
        return roots;
    }

    public static IEnumerable<string> Unfold(string text)
    {
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        StringBuilder? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                if (current != null) current.Append(line, 1, line.Length - 1);
                continue;
            }
            if (current != null) yield return current.ToString();
            current = new StringBuilder(line);
        }
        if (current != null) yield return current.ToString();
    }

    public static ContentLine ParseLine(string line)
    {
        var result = new ContentLine();
        var i = 0;
        while (i < line.Length && line[i] != ';' && line[i] != ':') i++;
        var name = line[..i];
        var dot = name.LastIndexOf('.');
        if (dot > 0)
        {
            result.Group = name[..dot];
            name = name[(dot + 1)..];
        }
        result.Name = name.Trim().ToUpperInvariant();

        while (i < line.Length && line[i] == ';')
        {
            i++;
            var start = i;
            while (i < line.Length && line[i] != '=' && line[i] != ';' && line[i] != ':') i++;
            var pname = line[start..i];
            var values = new List<string>();
            if (i < line.Length && line[i] == '=')
            {
                i++;
                while (true)
                {
                    if (i < line.Length && line[i] == '"')
                    {
                        var end = line.IndexOf('"', i + 1);
                        if (end < 0) end = line.Length;
                        values.Add(line[(i + 1)..end]);
                        i = Math.Min(end + 1, line.Length);
                    }
                    else
                    {
                        var vs = i;
                        while (i < line.Length && line[i] != ',' && line[i] != ';' && line[i] != ':') i++;
                        values.Add(line[vs..i]);
                    }
                    if (i < line.Length && line[i] == ',') { i++; continue; }
                    break;
                }
            }
            result.Parameters.Add(new ContentParameter(pname, values));
        }

        result.Value = i < line.Length ? line[(i + 1)..] : "";
        return result;
    }
}

public static class ContentWriter
{
    public static string Write(Component component)
    {
        var sb = new StringBuilder();
        Write(sb, component);
        return sb.ToString();
    }

    public static void Write(StringBuilder sb, Component component)
    {
        AppendFolded(sb, "BEGIN:" + component.Name);
        foreach (var p in component.Properties) AppendFolded(sb, Format(p));
        foreach (var c in component.Children) Write(sb, c);
        AppendFolded(sb, "END:" + component.Name);
    }

    public static string Format(ContentLine line)
    {
        var sb = new StringBuilder();
        if (line.Group != null) sb.Append(line.Group).Append('.');
        sb.Append(line.Name);
        foreach (var p in line.Parameters)
        {
            sb.Append(';').Append(p.Name);
            if (p.Values.Count == 0) continue;
            sb.Append('=');
            for (var i = 0; i < p.Values.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var v = p.Values[i];
                if (v.IndexOfAny([':', ';', ',']) >= 0) sb.Append('"').Append(v).Append('"');
                else sb.Append(v);
            }
        }
        sb.Append(':').Append(line.Value);
        return sb.ToString();
    }

    /// <summary>Appends a line folded at 75 octets as required by RFC 5545 3.1.</summary>
    public static void AppendFolded(StringBuilder sb, string line)
    {
        var bytes = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var len = rune.Utf8SequenceLength;
            if (bytes + len > 75)
            {
                sb.Append("\r\n ");
                bytes = 1;
            }
            sb.Append(rune.ToString());
            bytes += len;
        }
        sb.Append("\r\n");
    }
}
