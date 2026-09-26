namespace Fermata.Storage;

/// <summary>XDG base directories for Fermata's files.</summary>
public sealed class AppPaths
{
    public AppPaths(string config, string data, string cache, string state, string runtime)
    {
        Config = config;
        Data = data;
        Cache = cache;
        State = state;
        Runtime = runtime;
    }

    /// <summary>Settings: <c>$XDG_CONFIG_HOME/fermata</c>.</summary>
    public string Config { get; }

    /// <summary>Playlists, likes and play history: <c>$XDG_DATA_HOME/fermata</c>.</summary>
    public string Data { get; }

    /// <summary>Rebuildable data such as the library index and exported art: <c>$XDG_CACHE_HOME/fermata</c>.</summary>
    public string Cache { get; }

    /// <summary>The queue and position to resume: <c>$XDG_STATE_HOME/fermata</c>.</summary>
    public string State { get; }

    /// <summary>The single-instance socket: <c>$XDG_RUNTIME_DIR</c>.</summary>
    public string Runtime { get; }

    public string SettingsFile => Path.Combine(Config, "settings.json");
    public string UserDataFile => Path.Combine(Data, "library-data.json");
    public string PlaylistsDirectory => Path.Combine(Data, "playlists");
    public string LibraryCacheFile => Path.Combine(Cache, "library.bin");
    public string ArtExportDirectory => Path.Combine(Cache, "art");
    public string SessionFile => Path.Combine(State, "session.json");
    public string CrashLogFile => Path.Combine(State, "crash.log");
    public string InstanceSocket => Path.Combine(Runtime, "fermata.sock");

    public static AppPaths FromEnvironment()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Base(string variable, string fallback)
        {
            string? value = Environment.GetEnvironmentVariable(variable);
            return string.IsNullOrEmpty(value) || !Path.IsPathRooted(value) ? Path.Combine(home, fallback) : value;
        }
        string runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } dir && Path.IsPathRooted(dir)
            ? dir
            : Path.Combine(Path.GetTempPath(), "fermata-" + Environment.UserName);
        return new AppPaths(
            Path.Combine(Base("XDG_CONFIG_HOME", ".config"), "fermata"),
            Path.Combine(Base("XDG_DATA_HOME", ".local/share"), "fermata"),
            Path.Combine(Base("XDG_CACHE_HOME", ".cache"), "fermata"),
            Path.Combine(Base("XDG_STATE_HOME", ".local/state"), "fermata"),
            runtime);
    }

    /// <summary>The user's music folder (<c>XDG_MUSIC_DIR</c> from user-dirs.dirs, else ~/Music).</summary>
    public static string DefaultMusicFolder()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(home, ".config");
        try
        {
            foreach (string line in File.ReadLines(Path.Combine(configHome, "user-dirs.dirs")))
            {
                if (!line.StartsWith("XDG_MUSIC_DIR=", StringComparison.Ordinal))
                    continue;
                string value = line["XDG_MUSIC_DIR=".Length..].Trim().Trim('"').Replace("$HOME", home);
                if (value.Length > 0 && value != home)
                    return value;
            }
        }
        catch (IOException)
        {
        }
        return Path.Combine(home, "Music");
    }
}
