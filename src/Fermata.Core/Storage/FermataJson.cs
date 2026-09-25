using System.Text.Json;
using System.Text.Json.Serialization;
using Fermata.Library;

namespace Fermata.Storage;

/// <summary>Source-generated JSON for everything Fermata saves (no reflection, so NativeAOT-safe).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(SessionState))]
[JsonSerializable(typeof(UserDataDocument))]
[JsonSerializable(typeof(PlaylistDocument))]
public sealed partial class FermataJson : JsonSerializerContext
{
    /// <summary>Reads a JSON file, returning null when it is missing or malformed.</summary>
    public static T? Load<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, type);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        AtomicFile.Write(path, stream => JsonSerializer.Serialize(stream, value, type));
}
