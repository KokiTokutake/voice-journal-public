using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoiceJournal.Core.Management;

/// <summary>
/// The canonical JSON of detail spec 17 (v1, fixed): object keys in ordinal order, array order kept, no whitespace,
/// characters as they are with only quotes, backslashes and control characters escaped, integers only, lowercase literals.
/// </summary>
public static class CanonicalJson
{
    public static string Serialize(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(sb, node);
        return sb.ToString();
    }

    public static string Sha256(JsonNode? node) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(node))));

    private static void Write(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var (key, value) in o.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    String(sb, key);
                    sb.Append(':');
                    Write(sb, value);
                }
                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(sb, a[i]);
                }
                sb.Append(']');
                break;
            case JsonValue v:
                switch (v.GetValueKind())
                {
                    case JsonValueKind.String:
                        String(sb, v.GetValue<string>());
                        break;
                    case JsonValueKind.Number:
                        sb.Append(Integer(v).ToString(CultureInfo.InvariantCulture));
                        break;
                    case JsonValueKind.True:
                        sb.Append("true");
                        break;
                    case JsonValueKind.False:
                        sb.Append("false");
                        break;
                    case JsonValueKind.Null:
                        sb.Append("null");
                        break;
                    default:
                        throw new InvalidOperationException($"Unexpected JSON value {v.GetValueKind()}.");
                }
                break;
        }
    }

    private static long Integer(JsonValue v)
    {
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<JsonElement>(out var e) && e.TryGetInt64(out var n)) return n;
        throw new InvalidOperationException("The plan allows integers only (no floating point).");
    }

    private static void String(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
