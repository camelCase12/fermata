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

    /// <summary>
    /// Reads a JSON file that Fermata will later save over. When the file exists but cannot be read, it is
    /// renamed so that the next save cannot replace it, and <see cref="Loaded{T}.SetAside"/> gives its new
    /// path. The new name does not end in <c>.json</c>, so folder scans do not pick it up again.
    /// </summary>
    public static Loaded<T> LoadOrSetAside<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type,
        System.DateTime? now = null) where T : class
    {
        if (!File.Exists(path))
            return default;
        try
        {
            T? value;
            using (var stream = File.OpenRead(path))
                value = JsonSerializer.Deserialize(stream, type);
            if (value is not null)
                return new(value, null);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        string stamp = (now ?? System.DateTime.Now).ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string target = $"{path}.unreadable-{stamp}";
        for (int n = 2; File.Exists(target); n++)
            target = $"{path}.unreadable-{stamp}-{n}";
        try
        {
            File.Move(path, target);
            return new(null, target);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The file stays where it is. Saving uses a rename in the same folder, which fails for the
            // same reasons, so the unreadable file is not replaced either.
            return new(null, path);
        }
    }

    public static void Save<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        AtomicFile.Write(path, stream => JsonSerializer.Serialize(stream, value, type));
}

/// <summary>
/// A value read by <see cref="FermataJson.LoadOrSetAside{T}"/>. Value is null when the file was missing or
/// unreadable, and SetAside is the unreadable file's path, after it was renamed if that was possible.
/// </summary>
public readonly record struct Loaded<T>(T? Value, string? SetAside) where T : class;
