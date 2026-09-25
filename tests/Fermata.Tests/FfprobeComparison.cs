using System.Diagnostics;
using System.Text.Json;
using Fermata.Metadata;

namespace Fermata.Tests;

/// <summary>
/// Compares <see cref="TagReader"/> with ffprobe for every audio file below a directory:
/// <c>dotnet run --project tests/Fermata.Tests -- --compare-ffprobe DIR</c>.
/// </summary>
internal static class FfprobeComparison
{
    public static int Run(string directory)
    {
        int files = 0, mismatches = 0;
        var clock = new Stopwatch();
        foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (!TagReader.AudioExtensions.Contains(Path.GetExtension(path)))
                continue;
            files++;
            clock.Start();
            AudioTags? tags;
            try
            {
                tags = TagReader.Read(path);
            }
            catch (Exception error)
            {
                Console.WriteLine($"{path}: threw {error.GetType().Name}: {error.Message}");
                mismatches++;
                continue;
            }
            finally
            {
                clock.Stop();
            }
            using var probe = Probe(path);
            var problems = Compare(tags, probe.RootElement);
            if (problems.Count > 0)
            {
                mismatches++;
                Console.WriteLine($"{Path.GetRelativePath(directory, path)}:");
                foreach (string problem in problems)
                    Console.WriteLine("    " + problem);
            }
        }
        Console.WriteLine($"{files} files, {mismatches} with differences; TagReader took {clock.Elapsed.TotalMilliseconds:0.0} ms "
            + $"({(files > 0 ? clock.Elapsed.TotalMilliseconds * 1000 / files : 0):0} µs per file).");
        return mismatches == 0 ? 0 : 1;
    }

    private static List<string> Compare(AudioTags? tags, JsonElement probe)
    {
        var problems = new List<string>();
        if (tags is null)
        {
            problems.Add("not recognized");
            return problems;
        }
        var format = probe.GetProperty("format");
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Collect(JsonElement owner)
        {
            if (owner.TryGetProperty("tags", out var list))
            {
                foreach (var tag in list.EnumerateObject())
                    fields.TryAdd(tag.Name, tag.Value.GetString() ?? "");
            }
        }
        Collect(format);
        var audio = probe.GetProperty("streams").EnumerateArray().FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "audio");
        if (audio.ValueKind == JsonValueKind.Object)
            Collect(audio);

        void Expect(string label, string? actual, params string[] keys)
        {
            foreach (string key in keys)
            {
                if (fields.TryGetValue(key, out string? expected) && expected.Length > 0)
                {
                    if (!string.Equals(expected.Trim(), actual?.Trim(), StringComparison.Ordinal))
                        problems.Add($"{label}: ffprobe '{expected}', Fermata '{actual}'");
                    return;
                }
            }
        }
        Expect("title", tags.Title, "title");
        Expect("artist", tags.Artist, "artist");
        Expect("album", tags.Album, "album");
        Expect("album artist", tags.AlbumArtist, "album_artist", "albumartist", "album artist");
        if (fields.TryGetValue("track", out string? track) && TextDecoding.LeadingInt(track) != tags.TrackNumber)
            problems.Add($"track: ffprobe '{track}', Fermata {tags.TrackNumber}");
        if (fields.TryGetValue("date", out string? date) && TextDecoding.Year(date) is > 0 and var year && year != tags.Year)
            problems.Add($"year: ffprobe '{date}', Fermata {tags.Year}");
        if (double.TryParse(format.TryGetProperty("duration", out var d) ? d.GetString() : null, System.Globalization.CultureInfo.InvariantCulture, out double seconds))
        {
            double tolerance = Math.Max(0.06, seconds * 0.01);
            if (Math.Abs(seconds - tags.Duration.TotalSeconds) > tolerance)
                problems.Add($"duration: ffprobe {seconds:0.000}s, Fermata {tags.Duration.TotalSeconds:0.000}s");
        }
        if (audio.ValueKind == JsonValueKind.Object)
        {
            if (int.TryParse(audio.GetProperty("sample_rate").GetString(), out int rate) && rate != tags.SampleRate)
                problems.Add($"sample rate: ffprobe {rate}, Fermata {tags.SampleRate}");
            if (audio.TryGetProperty("channels", out var channels) && channels.GetInt32() != tags.Channels)
                problems.Add($"channels: ffprobe {channels.GetInt32()}, Fermata {tags.Channels}");
        }
        return problems;
    }

    private static JsonDocument Probe(string path)
    {
        var start = new ProcessStartInfo("ffprobe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in (string[])["-v", "error", "-show_format", "-show_streams", "-of", "json", path])
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return JsonDocument.Parse(output);
    }
}
