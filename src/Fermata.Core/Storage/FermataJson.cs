using System.Text.Json;
using System.Text.Json.Serialization;
using Fermata.Library;

namespace Fermata.Storage;

/// <summary>Source-generated JSON serialization for everything Fermata saves.</summary>
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
    /// <summary>Reads a JSON file, returning null when it is missing, malformed or from a newer Fermata.</summary>
    public static T? Load<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class, IVersionedFile
    {
        try
        {
            using var stream = File.OpenRead(path);
            var value = JsonSerializer.Deserialize(stream, type);
            return value is not null && !IsNewer(value) ? value : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads a JSON file, setting it aside when it exists but cannot be read.</summary>
    /// <remarks>
    /// A file is set aside by renaming it to <c>NAME.unreadable-TIME</c>, which does not end in
    /// <c>.json</c>. A file from a newer Fermata is read as well as this build can and left in place.
    /// </remarks>
    public static Loaded<T> LoadOrSetAside<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type,
        System.DateTime? now = null) where T : class, IVersionedFile
    {
        if (!File.Exists(path))
            return default;
        try
        {
            T? value;
            using (var stream = File.OpenRead(path))
                value = JsonSerializer.Deserialize(stream, type);
            if (value is not null)
                return new(value, null, IsNewer(value));
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
            // The file could not be renamed and stays where it is.
            return new(null, path);
        }
    }

    /// <summary>Whether a document comes from a newer Fermata. An older one is brought up to date.</summary>
    private static bool IsNewer<T>(T value) where T : IVersionedFile
    {
        if (value.Version > T.CurrentVersion)
            return true;
        // Every format so far reads the same, so an older document only takes the current version.
        value.Version = T.CurrentVersion;
        return false;
    }

    public static void Save<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        AtomicFile.Write(path, stream => JsonSerializer.Serialize(stream, value, type));
}

/// <summary>The result of <see cref="FermataJson.LoadOrSetAside{T}"/>.</summary>
/// <param name="Value">The value read, or null when the file was missing or unreadable.</param>
/// <param name="SetAside">The path of an unreadable file after it was set aside, or null.</param>
/// <param name="IsNewer">Whether the file was written by a newer Fermata, and must not be saved over.</param>
public readonly record struct Loaded<T>(T? Value, string? SetAside, bool IsNewer = false) where T : class;
