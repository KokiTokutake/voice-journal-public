using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VoiceJournal.Infrastructure.Storage;

/// <summary>How values are stored as text in the database (detail spec 15.2).</summary>
internal static class DbText
{
    private const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    private static readonly ConcurrentDictionary<Type, Dictionary<string, object>> EnumNames = new();

    /// <summary>RFC 3339 UTC with a fixed width, so text order equals time order.</summary>
    public static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString(UtcFormat, CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.ParseExact(value, UtcFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary>Enum values as in the JSON files: snake_case (<c>LoopbackSilence</c> → <c>loopback_silence</c>).</summary>
    public static string Enum<T>(T value) where T : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    public static T ParseEnum<T>(string value) where T : struct, Enum
    {
        var map = EnumNames.GetOrAdd(typeof(T), _ => System.Enum.GetValues<T>().ToDictionary(v => Enum(v), v => (object)v, StringComparer.Ordinal));
        return map.TryGetValue(value, out var parsed) ? (T)parsed : throw new InvalidDataException($"Unknown {typeof(T).Name} value '{value}' in the database.");
    }

    public static string Id(Guid id) => id.ToString("D");

    /// <summary>
    /// A stable id derived from a parent id and a name (RFC 9562 version 8, SHA-256 based), so rebuilding the
    /// index from the manifests gives every chunk and event the same id again.
    /// </summary>
    public static Guid DerivedId(Guid parent, string name)
    {
        Span<byte> input = stackalloc byte[16 + Encoding.UTF8.GetMaxByteCount(name.Length)];
        parent.TryWriteBytes(input, bigEndian: true, out _);
        var length = 16 + Encoding.UTF8.GetBytes(name, input[16..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input[..length], hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80); // version 8
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // RFC 9562 variant
        return new Guid(hash[..16], bigEndian: true);
    }
}
