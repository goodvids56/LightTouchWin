// XML property lists, the text libplist and C# trade: dictionaries as Dictionary<string, object>, arrays as
// List<object>, and strings, longs, doubles, bools, byte[] and DateTime.

using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace LightTouch.Native;

internal static class Plist
{
    public static object? Read(ReadOnlySpan<byte> xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(new MemoryStream(xml.ToArray()), settings);
        var root = XDocument.Load(reader).Root;
        return root?.Elements().FirstOrDefault() is { } value ? Value(value) : null;
    }

    private static object? Value(XElement e) => e.Name.LocalName switch
    {
        "dict" => Dict(e),
        "array" => e.Elements().Select(Value).Where(v => v != null).Cast<object>().ToList(),
        "string" => e.Value,
        "integer" => long.TryParse(e.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
            ? l
            : (object)ulong.Parse(e.Value, CultureInfo.InvariantCulture),
        "real" => double.Parse(e.Value, CultureInfo.InvariantCulture),
        "true" => true,
        "false" => false,
        "data" => Convert.FromBase64String(string.Concat(e.Value.Where(c => !char.IsWhiteSpace(c)))),
        "date" => DateTime.Parse(e.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
        _ => null,
    };

    private static Dictionary<string, object> Dict(XElement e)
    {
        var result = new Dictionary<string, object>();
        string? key = null;
        foreach (var child in e.Elements())
        {
            if (child.Name.LocalName == "key")
            {
                key = child.Value;
                continue;
            }
            if (key != null && Value(child) is { } value) result[key] = value;
            key = null;
        }
        return result;
    }

    public static byte[] Write(object value)
    {
        var body = Element(value);
        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), body));
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
        {
            doc.Save(writer);
        }
        return stream.ToArray();
    }

    private static XElement Element(object value) => value switch
    {
        string s => new XElement("string", s),
        bool b => new XElement(b ? "true" : "false"),
        int or long or uint or ulong => new XElement("integer", Convert.ToString(value, CultureInfo.InvariantCulture)),
        double d => new XElement("real", d.ToString("R", CultureInfo.InvariantCulture)),
        byte[] data => new XElement("data", Convert.ToBase64String(data)),
        DateTime date => new XElement("date", date.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)),
        IDictionary<string, object> dict => new XElement("dict",
            dict.SelectMany(p => new[] { new XElement("key", p.Key), Element(p.Value) })),
        System.Collections.IEnumerable list => new XElement("array",
            list.Cast<object>().Select(Element)),
        _ => throw new ArgumentException($"Not a plist value: {value.GetType()}"),
    };
}
