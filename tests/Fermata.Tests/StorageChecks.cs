using Fermata.Library;
using Fermata.Storage;

namespace Fermata.Tests;

internal static class StorageChecks
{
    public static void Run(Checks check)
    {
        string root = Path.Combine(Path.GetTempPath(), "fermata-storage-" + Environment.ProcessId);
        Directory.CreateDirectory(root);
        try
        {
            CheckLoad(check, root);
            CheckPlaylists(check, root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CheckLoad(Checks check, string root)
    {
        var type = FermataJson.Default.Settings;
        var now = new DateTime(2026, 9, 26, 10, 15, 0);

        var missing = FermataJson.LoadOrSetAside(Path.Combine(root, "missing.json"), type, now);
        check.That(missing.Value is null && missing.SetAside is null, "a missing file loads as nothing and is not reported");

        string good = Path.Combine(root, "good.json");
        FermataJson.Save(good, new Settings { Volume = 0.25 }, type);
        var loaded = FermataJson.LoadOrSetAside(good, type, now);
        check.That(loaded.Value?.Volume == 0.25 && loaded.SetAside is null && File.Exists(good), "a valid file loads and stays");

        string broken = Path.Combine(root, "broken.json");
        File.WriteAllText(broken, "{ \"volume\": 0.5, ");
        var setAside = FermataJson.LoadOrSetAside(broken, type, now);
        string expected = broken + ".unreadable-20260926-101500";
        check.Equal(expected, setAside.SetAside, "a malformed file is renamed with the time");
        check.That(setAside.Value is null && !File.Exists(broken), "a malformed file loads as nothing and leaves its place");
        check.Equal("{ \"volume\": 0.5, ", File.ReadAllText(expected), "the set-aside file keeps its contents");

        File.WriteAllText(broken, "null");
        var second = FermataJson.LoadOrSetAside(broken, type, now);
        check.Equal(expected + "-2", second.SetAside, "a second unreadable file at the same time gets its own name");

        FermataJson.Save(broken, new Settings(), type);
        check.That(File.Exists(expected) && File.Exists(expected + "-2"), "saving afterwards does not touch the set-aside files");
    }

    private static void CheckPlaylists(Checks check, string root)
    {
        string directory = Path.Combine(root, "playlists");
        Directory.CreateDirectory(directory);
        var type = FermataJson.Default.PlaylistDocument;
        FermataJson.Save(Path.Combine(directory, "good.json"), new PlaylistDocument { Name = "Good" }, type);
        File.WriteAllText(Path.Combine(directory, "bad.json"), "not json");

        var store = new PlaylistStore(directory);
        store.Load();
        check.Equal(1, store.Playlists.Count, "a readable playlist loads beside an unreadable one");
        check.Equal(1, store.Unreadable.Count, "the unreadable playlist is reported");
        check.That(!File.Exists(Path.Combine(directory, "bad.json")) && File.Exists(store.Unreadable[0]), "the unreadable playlist is set aside");

        store.Load();
        check.That(store.Playlists.Count == 1 && store.Unreadable.Count == 0, "a set-aside playlist is not read or reported again");
    }
}
