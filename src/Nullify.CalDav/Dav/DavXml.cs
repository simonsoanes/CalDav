// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.WebUtilities;

namespace Nullify.CalDav.Dav;

public static class Ns
{
    public static readonly XNamespace D = "DAV:";
    public static readonly XNamespace C = "urn:ietf:params:xml:ns:caldav";
    public static readonly XNamespace CR = "urn:ietf:params:xml:ns:carddav";
    public static readonly XNamespace CS = "http://calendarserver.org/ns/";
    public static readonly XNamespace A = "http://apple.com/ns/ical/";
    public static readonly XNamespace P = "https://bitfire.at/webdav-push";

    public static XAttribute[] Declarations() =>
    [
        new(XNamespace.Xmlns + "d", D.NamespaceName),
        new(XNamespace.Xmlns + "cal", C.NamespaceName),
        new(XNamespace.Xmlns + "card", CR.NamespaceName),
        new(XNamespace.Xmlns + "cs", CS.NamespaceName),
        new(XNamespace.Xmlns + "ical", A.NamespaceName),
        new(XNamespace.Xmlns + "push", P.NamespaceName),
    ];
}

/// <summary>Raised to abort a DAV request with a status code and optional precondition element.</summary>
public sealed class DavException(int status, XName? precondition = null, string? message = null, XElement? detail = null)
    : Exception(message ?? $"HTTP {status}")
{
    public int Status { get; } = status;
    public XName? Precondition { get; } = precondition;
    public XElement? Detail { get; } = detail;
}

public static class DavXml
{
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        Async = true,
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        MaxCharactersInDocument = 20_000_000,
    };

    /// <summary>Reads the request body as XML. Returns null for an empty body.</summary>
    public static async Task<XDocument?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms, ct);
        if (ms.Length == 0) return null;
        ms.Position = 0;
        if (ms.ToArray().All(b => b is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t')) return null;
        try
        {
            using var reader = XmlReader.Create(ms, ReaderSettings);
            return await XDocument.LoadAsync(reader, LoadOptions.None, ct);
        }
        catch (XmlException ex)
        {
            throw new DavException(StatusCodes.Status400BadRequest, message: "Malformed XML: " + ex.Message);
        }
    }

    public static string StatusLine(int code) => $"HTTP/1.1 {code} {ReasonPhrases.GetReasonPhrase(code)}";

    public static XElement Href(string href) => new(Ns.D + "href", href);

    public static async Task WriteAsync(HttpResponse response, int status, XElement root, CancellationToken ct)
    {
        foreach (var a in Ns.Declarations())
            if (root.Attribute(a.Name) == null) root.Add(a);
        response.StatusCode = status;
        response.ContentType = "application/xml; charset=utf-8";
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Async = true, Indent = false };
        using var ms = new MemoryStream();
        await using (var writer = XmlWriter.Create(ms, settings))
        {
            new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(writer);
        }
        response.ContentLength = ms.Length;
        ms.Position = 0;
        await ms.CopyToAsync(response.Body, ct);
    }

    public static XElement Error(XName precondition, XElement? detail = null)
    {
        var el = new XElement(precondition);
        if (detail != null) el.Add(detail);
        return new XElement(Ns.D + "error", el);
    }
}

/// <summary>Accumulates DAV:response elements for a 207 Multi-Status reply.</summary>
public sealed class MultiStatus
{
    private readonly List<XElement> _responses = [];
    public XElement? SyncToken { get; set; }

    public int Count => _responses.Count;

    public void AddPropStat(string href, IEnumerable<(XElement Prop, int Status)> props)
    {
        var resp = new XElement(Ns.D + "response", DavXml.Href(href));
        foreach (var group in props.GroupBy(p => p.Status).OrderBy(g => g.Key))
        {
            resp.Add(new XElement(Ns.D + "propstat",
                new XElement(Ns.D + "prop", group.Select(g => g.Prop)),
                new XElement(Ns.D + "status", DavXml.StatusLine(group.Key))));
        }
        if (!resp.Elements(Ns.D + "propstat").Any())
        {
            resp.Add(new XElement(Ns.D + "propstat", new XElement(Ns.D + "prop"),
                new XElement(Ns.D + "status", DavXml.StatusLine(200))));
        }
        _responses.Add(resp);
    }

    public void AddStatus(string href, int status, XElement? error = null)
    {
        var resp = new XElement(Ns.D + "response", DavXml.Href(href), new XElement(Ns.D + "status", DavXml.StatusLine(status)));
        if (error != null) resp.Add(error);
        _responses.Add(resp);
    }

    public void AddRaw(XElement response) => _responses.Add(response);

    public XElement ToXml()
    {
        var root = new XElement(Ns.D + "multistatus", _responses);
        if (SyncToken != null) root.Add(SyncToken);
        return root;
    }
}
