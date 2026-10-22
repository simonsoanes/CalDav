// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Xml.Linq;

namespace Nullify.CalDav.SyncMl;

/// <summary>
/// WBXML 1.2/1.3 codec (WAP-192) with the SyncML, MetInf and DevInf code pages
/// (OMA-TS-SyncML_RepPro / DevInf). Elements are represented without namespaces;
/// <see cref="SyncMlXml"/> adds the XML namespaces for the textual representation.
/// </summary>
public static class Wbxml
{
    // Global tokens
    private const byte SwitchPage = 0x00, End = 0x01, StrI = 0x03, StrT = 0x83, Opaque = 0xC3;
    private const byte Entity = 0x02, Literal = 0x04, LiteralC = 0x44, LiteralA = 0x84, LiteralAc = 0xC4, Pi = 0x43;

    public const int PublicIdSyncMl11 = 0x0FD3, PublicIdSyncMl12 = 0x1201, PublicIdSyncMl10 = 0x0FD1;
    public const int PublicIdDevInf11 = 0x0FD4, PublicIdDevInf12 = 0x1203, PublicIdDevInf10 = 0x0FD2;
    private const int Utf8 = 106;

    public enum Language { SyncMl, DevInf }

    private static readonly string[] SyncMlPage =
    [
        "Add", "Alert", "Archive", "Atomic", "Chal", "Cmd", "CmdID", "CmdRef", "Copy", "Cred", "Data", "Delete", "Exec",
        "Final", "Get", "Item", "Lang", "LocName", "LocURI", "Map", "MapItem", "Meta", "MsgID", "MsgRef", "NoResp",
        "NoResults", "Put", "Replace", "RespURI", "Results", "Search", "Sequence", "SessionID", "SftDel", "Source",
        "SourceRef", "Status", "Sync", "SyncBody", "SyncHdr", "SyncML", "Target", "TargetRef", "Reserved", "VerDTD",
        "VerProto", "NumberOfChanges", "MoreData", "Field", "Filter", "Record", "FilterType", "SourceParent",
        "TargetParent", "Move", "Correlator",
    ];

    private static readonly string[] MetInfPage =
    [
        "Anchor", "EMI", "Format", "FreeID", "FreeMem", "Last", "Mark", "MaxMsgSize", "Mem", "MetInf", "Next",
        "NextNonce", "SharedMem", "Size", "Type", "Version", "MaxObjSize", "FieldLevel",
    ];

    private static readonly string[] DevInfPage =
    [
        "CTCap", "CTType", "DataStore", "DataType", "DevID", "DevInf", "DevTyp", "DisplayName", "DSMem", "Ext", "FwV",
        "HwV", "Man", "MaxGUIDSize", "MaxID", "MaxMem", "Mod", "OEM", "ParamName", "PropName", "Rx", "Rx-Pref",
        "SharedMem", "Size", "SourceRef", "SwV", "SyncCap", "SyncType", "Tx", "Tx-Pref", "ValEnum", "VerCT",
        "VerDTD", "Xnam", "Xval", "UTC", "SupportNumberOfChanges", "SupportLargeObjs", "Property", "PropParam",
        "MaxOccur", "NoTruncate", null!, "Filter-Rx", "FilterCap", "FilterKeyword", "FieldLevel", "SupportHierarchicalSync",
    ];

    /// <summary>Tag names per code page for each language (page index → names, token = 5 + index).</summary>
    private static string[][] Pages(Language lang) => lang == Language.SyncMl ? [SyncMlPage, MetInfPage, DevInfPage] : [DevInfPage];

    private static readonly HashSet<string> MetInfNames = [.. MetInfPage];

    public static bool LooksLikeWbxml(ReadOnlySpan<byte> data) => data.Length > 4 && data[0] is 0x01 or 0x02 or 0x03;

    // ------------------------------------------------------------------ Decoding

    public static XElement Decode(byte[] data, out int publicId)
    {
        var r = new Reader(data);
        r.ReadByte(); // version
        publicId = r.ReadMbUInt32();
        var publicIdIndex = -1;
        if (publicId == 0) publicIdIndex = r.ReadMbUInt32();
        var charset = r.ReadMbUInt32();
        if (charset != 0 && charset != Utf8 && charset != 3 && charset != 4)
            throw new FormatException("Unsupported WBXML charset " + charset);
        var tableLength = r.ReadMbUInt32();
        var table = r.ReadBytes(tableLength);
        if (publicIdIndex >= 0)
        {
            var fpi = ReadTableString(table, publicIdIndex);
            publicId = fpi.Contains("DevInf", StringComparison.OrdinalIgnoreCase)
                ? fpi.Contains("1.2") ? PublicIdDevInf12 : PublicIdDevInf11
                : fpi.Contains("1.2") ? PublicIdSyncMl12 : PublicIdSyncMl11;
        }
        var lang = publicId is PublicIdDevInf10 or PublicIdDevInf11 or PublicIdDevInf12 ? Language.DevInf : Language.SyncMl;
        var pages = Pages(lang);
        var page = 0;

        // Skip leading processing instructions.
        while (r.Peek() == Pi) SkipPi(r);
        var root = ReadElement(r, table, pages, ref page) ?? throw new FormatException("Empty WBXML document");
        return root;
    }

    private static XElement? ReadElement(Reader r, byte[] table, string[][] pages, ref int page)
    {
        while (r.Peek() == SwitchPage)
        {
            r.ReadByte();
            page = r.ReadByte();
        }
        var token = r.ReadByte();
        var hasContent = (token & 0x40) != 0;
        var hasAttributes = (token & 0x80) != 0;
        var id = token & 0x3F;
        string name;
        if (id == Literal)
        {
            name = ReadTableString(table, r.ReadMbUInt32());
        }
        else
        {
            var names = page < pages.Length ? pages[page] : [];
            var idx = id - 5;
            name = idx >= 0 && idx < names.Length && names[idx] != null ? names[idx] : $"Unknown_{page}_{id:X2}";
        }

        var element = new XElement(name);
        if (hasAttributes) SkipAttributes(r);
        if (!hasContent) return element;

        var text = new StringBuilder();
        while (true)
        {
            var b = r.Peek();
            switch (b)
            {
                case End:
                    r.ReadByte();
                    if (text.Length > 0) element.Add(new XText(text.ToString()));
                    return element;
                case SwitchPage:
                    r.ReadByte();
                    page = r.ReadByte();
                    break;
                case StrI:
                    r.ReadByte();
                    text.Append(r.ReadCString());
                    break;
                case StrT:
                    r.ReadByte();
                    text.Append(ReadTableString(table, r.ReadMbUInt32()));
                    break;
                case Entity:
                    r.ReadByte();
                    text.Append(char.ConvertFromUtf32(r.ReadMbUInt32()));
                    break;
                case Opaque:
                {
                    r.ReadByte();
                    var bytes = r.ReadBytes(r.ReadMbUInt32());
                    // Embedded WBXML documents (e.g. DevInf) are expanded into child elements.
                    if (element.Name.LocalName == "Data" && LooksLikeWbxml(bytes) && TryDecode(bytes, out var embedded))
                    {
                        element.Add(embedded);
                    }
                    else
                    {
                        text.Append(Encoding.UTF8.GetString(bytes));
                    }
                    break;
                }
                case Pi:
                    SkipPi(r);
                    break;
                default:
                    if (b is 0x40 or 0x41 or 0x42 or 0x80 or 0x81 or 0x82 or 0xC0 or 0xC1 or 0xC2)
                        throw new FormatException("WBXML extension tokens are not supported");
                    if (text.Length > 0)
                    {
                        element.Add(new XText(text.ToString()));
                        text.Clear();
                    }
                    var child = ReadElement(r, table, pages, ref page);
                    if (child != null) element.Add(child);
                    break;
            }
        }
    }

    private static bool TryDecode(byte[] data, out XElement element)
    {
        try
        {
            element = Decode(data, out _);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException or EndOfStreamException)
        {
            element = null!;
            return false;
        }
    }

    private static void SkipAttributes(Reader r)
    {
        while (true)
        {
            var b = r.ReadByte();
            if (b == End) return;
            if (b == StrI) r.ReadCString();
            else if (b is StrT or Literal or Entity) r.ReadMbUInt32();
            else if (b == Opaque) r.ReadBytes(r.ReadMbUInt32());
            else if (b == SwitchPage) r.ReadByte();
        }
    }

    private static void SkipPi(Reader r)
    {
        r.ReadByte();
        SkipAttributes(r);
    }

    private static string ReadTableString(byte[] table, int offset)
    {
        var end = offset;
        while (end < table.Length && table[end] != 0) end++;
        return Encoding.UTF8.GetString(table, offset, end - offset);
    }

    private sealed class Reader(byte[] data)
    {
        private int _pos;

        public byte Peek() => _pos < data.Length ? data[_pos] : throw new EndOfStreamException();

        public byte ReadByte() => _pos < data.Length ? data[_pos++] : throw new EndOfStreamException();

        public int ReadMbUInt32()
        {
            var result = 0;
            for (var i = 0; i < 5; i++)
            {
                var b = ReadByte();
                result = (result << 7) | (b & 0x7F);
                if ((b & 0x80) == 0) return result;
            }
            throw new FormatException("Invalid mb_u_int32");
        }

        public byte[] ReadBytes(int count)
        {
            if (count < 0 || _pos + count > data.Length) throw new EndOfStreamException();
            var result = data.AsSpan(_pos, count).ToArray();
            _pos += count;
            return result;
        }

        public string ReadCString()
        {
            var start = _pos;
            while (ReadByte() != 0) { }
            return Encoding.UTF8.GetString(data, start, _pos - start - 1);
        }
    }

    // ------------------------------------------------------------------ Encoding

    public static byte[] Encode(XElement root, Language lang, int publicId)
    {
        var ms = new MemoryStream();
        ms.WriteByte(0x02); // WBXML 1.2
        WriteMbUInt32(ms, publicId);
        WriteMbUInt32(ms, Utf8);
        WriteMbUInt32(ms, 0); // no string table
        var page = 0;
        var pages = Pages(lang);
        WriteElement(ms, root, pages, lang, ref page, inDevInf: lang == Language.DevInf);
        return ms.ToArray();
    }

    private static void WriteElement(Stream s, XElement e, string[][] pages, Language lang, ref int page, bool inDevInf)
    {
        var name = e.Name.LocalName;

        // DevInf inside SyncML is carried as an embedded WBXML document.
        if (lang == Language.SyncMl && name == "DevInf")
        {
            var version = e.Element("VerDTD")?.Value == "1.1" ? PublicIdDevInf11 : PublicIdDevInf12;
            var bytes = Encode(e, Language.DevInf, version);
            s.WriteByte(Opaque);
            WriteMbUInt32(s, bytes.Length);
            s.Write(bytes);
            return;
        }

        int targetPage, index;
        if (lang == Language.DevInf || inDevInf)
        {
            targetPage = lang == Language.DevInf ? 0 : 2;
            index = Array.IndexOf(DevInfPage, name);
        }
        else if (MetInfNames.Contains(name) && (e.Parent?.Name.LocalName is "Meta" or "Anchor" or "Mem" || name == "Anchor" || e.Parent?.Name.LocalName == "MetInf"))
        {
            targetPage = 1;
            index = Array.IndexOf(MetInfPage, name);
        }
        else
        {
            targetPage = 0;
            index = Array.IndexOf(SyncMlPage, name);
        }
        if (index < 0) throw new InvalidOperationException($"No WBXML token for element {name}");

        if (targetPage != page)
        {
            s.WriteByte(SwitchPage);
            s.WriteByte((byte)targetPage);
            page = targetPage;
        }

        var hasContent = e.Nodes().Any();
        s.WriteByte((byte)((index + 5) | (hasContent ? 0x40 : 0)));
        if (!hasContent) return;

        foreach (var node in e.Nodes())
        {
            switch (node)
            {
                case XElement child:
                    WriteElement(s, child, pages, lang, ref page, inDevInf || name == "DevInf");
                    break;
                case XText text:
                    var bytes = Encoding.UTF8.GetBytes(text.Value);
                    if (name == "Data" && lang == Language.SyncMl)
                    {
                        s.WriteByte(Opaque);
                        WriteMbUInt32(s, bytes.Length);
                        s.Write(bytes);
                    }
                    else
                    {
                        s.WriteByte(StrI);
                        s.Write(bytes);
                        s.WriteByte(0);
                    }
                    break;
            }
        }
        s.WriteByte(End);
    }

    private static void WriteMbUInt32(Stream s, int value)
    {
        Span<byte> buf = stackalloc byte[5];
        var i = 4;
        buf[i] = (byte)(value & 0x7F);
        value >>= 7;
        while (value > 0)
        {
            buf[--i] = (byte)(0x80 | (value & 0x7F));
            value >>= 7;
        }
        s.Write(buf[i..]);
    }
}

/// <summary>Conversion between the namespace-free element tree and SyncML XML.</summary>
public static class SyncMlXml
{
    public const string MetInfNs = "syncml:metinf";
    public const string DevInfNs = "syncml:devinf";

    private static readonly HashSet<string> MetInfNames =
    [
        "Anchor", "EMI", "Format", "FreeID", "FreeMem", "Last", "Mark", "MaxMsgSize", "Mem", "MetInf", "Next",
        "NextNonce", "SharedMem", "Size", "Type", "Version", "MaxObjSize", "FieldLevel",
    ];

    /// <summary>Strips namespaces so processing code can use plain local names.</summary>
    public static XElement StripNamespaces(XElement e)
    {
        var result = new XElement(e.Name.LocalName);
        foreach (var node in e.Nodes())
        {
            if (node is XElement child) result.Add(StripNamespaces(child));
            else if (node is XText t) result.Add(new XText(t.Value));
        }
        return result;
    }

    /// <summary>Adds the SyncML, MetInf and DevInf namespaces for XML serialisation.</summary>
    public static XElement AddNamespaces(XElement e, string version)
    {
        XNamespace syncMl = "SYNCML:SYNCML" + version;
        return Convert(e, syncMl, inMeta: false, inDevInf: false);
    }

    private static XElement Convert(XElement e, XNamespace syncMl, bool inMeta, bool inDevInf)
    {
        var name = e.Name.LocalName;
        var devInf = inDevInf || name == "DevInf";
        var meta = !devInf && (inMeta || name is "Meta" or "Anchor");
        XNamespace ns = devInf ? DevInfNs : meta && MetInfNames.Contains(name) ? MetInfNs : syncMl;
        var result = new XElement(ns + name);
        foreach (var node in e.Nodes())
        {
            if (node is XElement child) result.Add(Convert(child, syncMl, meta, devInf));
            else if (node is XText t) result.Add(new XText(t.Value));
        }
        return result;
    }
}
