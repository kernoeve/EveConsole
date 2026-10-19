using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace EveConsole.Services;

/// <summary>
/// SDE YAML read as JSON: any node — a value, a block, a list — as a <see cref="JsonNode"/>, so a
/// field the import has no class for can be kept whole, in a column, without knowing its shape.
///
/// <para>Scalars follow YAML's own rules rather than a guess at the text: a quoted scalar is a
/// string whatever it looks like (CCP writes a star's spectral class as <c>'0.0'</c>), and a plain
/// one is null, a boolean, a whole number or a decimal where it reads as one, and a string
/// otherwise.</para>
/// </summary>
public static class SdeYamlJson
{
    /// <summary>The languages the SDE writes localised text in. A block whose keys are all of
    /// these, English among them, is one piece of text in several languages.</summary>
    public static readonly IReadOnlySet<string> Languages =
        new HashSet<string>(StringComparer.Ordinal) { "en", "de", "es", "fr", "ja", "ko", "ru", "zh" };

    /// <summary>
    /// How JSON is written to a column: as the text reads. The default escapes every non-ASCII
    /// character and every angle bracket, so Japanese, Korean and Chinese text, accents, and the
    /// SDE's own showinfo links all came out as runs of \u codes.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A node as JSON text, written as <see cref="Json"/> says.</summary>
    public static string Text(JsonNode node) => node.ToJsonString(Json);

    public static bool IsLocalized(JsonNode? node) =>
        node is JsonObject o && o.Count > 0 && o.ContainsKey("en") && o.All(kv => Languages.Contains(kv.Key));

    /// <summary>The node the parser is at, consumed.</summary>
    public static JsonNode? ReadNode(IParser parser)
    {
        if (parser.TryConsume<Scalar>(out var scalar)) return FromScalar(scalar);

        if (parser.TryConsume<MappingStart>(out _))
        {
            var o = new JsonObject();
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                var key = parser.Consume<Scalar>().Value;
                o[key] = ReadNode(parser);
            }
            return o;
        }

        if (parser.TryConsume<SequenceStart>(out _))
        {
            var a = new JsonArray();
            while (!parser.TryConsume<SequenceEnd>(out _)) a.Add(ReadNode(parser));
            return a;
        }

        // Aliases: the SDE has none. Stepped over rather than failing the file.
        parser.SkipThisAndNestedEvents();
        return null;
    }

    /// <summary>
    /// The top-level mapping of a file, one record at a time: its key and its value. Streamed, so a
    /// file of a third of a million records is never held whole.
    /// </summary>
    public static IEnumerable<(JsonNode Key, JsonNode? Value)> Records(TextReader reader)
    {
        var parser = new Parser(reader);
        parser.Consume<StreamStart>();
        if (!parser.TryConsume<DocumentStart>(out _)) yield break;
        if (!parser.TryConsume<MappingStart>(out _)) yield break;

        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = FromScalar(parser.Consume<Scalar>()) ?? JsonValue.Create("");
            yield return (key, ReadNode(parser));
        }
    }

    private static JsonNode? FromScalar(Scalar s)
    {
        if (s.Style is not ScalarStyle.Plain) return JsonValue.Create(s.Value);

        var v = s.Value;
        switch (v)
        {
            case "" or "~" or "null" or "Null" or "NULL": return null;
            case "true" or "True" or "TRUE":    return JsonValue.Create(true);
            case "false" or "False" or "FALSE": return JsonValue.Create(false);
        }
        if (long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)) return JsonValue.Create(d);
        return JsonValue.Create(v);
    }
}

/// <summary>A YAML field taken whole as JSON, for a class to declare when it keeps the field
/// without needing its parts. See <see cref="SdeJsonConverter"/>.</summary>
public sealed class SdeJson
{
    public JsonNode? Node { get; init; }

    /// <summary>The value as JSON text, for a column; null when the field was null.</summary>
    public string? Text => Node is null ? null : SdeYamlJson.Text(Node);
}

/// <summary>Reads an <see cref="SdeJson"/> field: whatever the node is, as JSON.</summary>
public sealed class SdeJsonConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(SdeJson);

    public object? ReadYaml(IParser parser, Type type) => new SdeJson { Node = SdeYamlJson.ReadNode(parser) };

    public void WriteYaml(IEmitter emitter, object? value, Type type) => throw new NotSupportedException();
}
