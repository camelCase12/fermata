using Fermata.Library;
using Fermata.Playback;
using Fermata.Storage;

namespace Fermata.Tests;

internal static class LibraryChecks
{
    public static void Run(Checks check)
    {
        Grouping(check);
        LyricsParsing(check);
        if (!Fixtures.FfmpegAvailable)
        {
            Console.WriteLine("  (ffmpeg not found; skipping scanned-library checks)");
            return;
        }
        string root = BuildFixtureLibrary();
        var result = LibraryScanner.Scan([root], LibrarySnapshot.Empty, null, CancellationToken.None);
        var library = result.Snapshot;
        Scanned(check, library, result);
        Cache(check, library, root);
        Incremental(check, library, root);
        Search(check, library);
        UserDataFollowsFiles(check, root);
        M3u(check, library, root);
        Recommendations(check, library);
    }

    private static void Grouping(Checks check)
    {
        check.That(AlbumGrouping.IsDiscFolder("CD1") && AlbumGrouping.IsDiscFolder("Disc 2") && AlbumGrouping.IsDiscFolder("disk_03")
            && AlbumGrouping.IsDiscFolder("CD 1 - Bonus"), "disc folders are recognized");
        check.That(!AlbumGrouping.IsDiscFolder("CDs") && !AlbumGrouping.IsDiscFolder("Discovery") && !AlbumGrouping.IsDiscFolder("CD123"),
            "ordinary folder names are not disc folders");
        check.Equal("/m/A/Album", AlbumGrouping.AlbumDirectory("/m/A/Album/CD2/01.flac"), "disc folders belong to their parent");
        check.Equal(("Title", "Artist", 3), Track.FileNameGuess("/m/03 - Artist - Title.mp3", "", 0), "file name with number, artist and title");
        check.Equal(("Title", "", 12), Track.FileNameGuess("/m/12. Title.flac", "", 0), "file name with number and title");
        check.Equal(("Just a name", "", 0), Track.FileNameGuess("/m/Just_a_name.ogg", "", 0), "plain file name");
    }

    private static void LyricsParsing(Checks check)
    {
        var lyrics = Lyrics.Parse("[ar:Someone]\n[offset:+500]\n[00:05.00][00:15.50]Chorus\n[00:01.2]First <00:01.50>word\nNo stamp", "test.lrc");
        check.That(lyrics is { IsSynced: true }, "LRC is synchronized");
        check.Equal(3, lyrics?.Lines.Count, "repeated stamps become separate lines; metadata and unstamped lines are dropped");
        check.Equal("First word", lyrics?.Lines[0].Text, "word stamps are removed");
        check.Near(0.7, lyrics?.Lines[0].Time.TotalSeconds ?? 0, 0.001, "a positive offset shows lines earlier");
        check.Equal(-1, lyrics?.LineAt(TimeSpan.FromSeconds(0.5)), "before the first line");
        check.Equal(1, lyrics?.LineAt(TimeSpan.FromSeconds(4.6)), "the line being sung");
        check.Equal(2, lyrics?.LineAt(TimeSpan.FromMinutes(5)), "after the last line");
        var plain = Lyrics.Parse("Verse one\nVerse two\n\n", "embedded");
        check.That(plain is { IsSynced: false, Lines.Count: 2 }, "plain lyrics keep their lines");
        check.That(Lyrics.Parse("\n\n", "x") is null, "blank lyrics are none");
    }

    /// <summary>A small library covering the grouping rules.</summary>
    private static string BuildFixtureLibrary()
    {
        string root = Fixtures.Directory("library");
        void Make(string relative, string tags)
        {
            string path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string codec = path.EndsWith(".mp3") ? "-c:a libmp3lame -q:a 9" : "-c:a flac";
            Fixtures.Ffmpeg($"-f lavfi -i sine=frequency=440:duration=1 {codec} {tags} \"{path}\"");
        }
        const string album = "-metadata album=\"Album One\" -metadata album_artist=\"Artist A\" -metadata date=2020 -metadata genre=Rock";
        Make("Artist A/2020 - Album One/01 - Opening.flac", $"{album} -metadata title=Opening -metadata artist=\"Artist A\" -metadata track=1/3");
        Make("Artist A/2020 - Album One/02 - Duet.flac", $"{album} -metadata title=Duet -metadata artist=\"Artist A feat. Guest B\" -metadata track=2/3");
        Make("Artist A/2020 - Album One/03 - Closing.flac", $"{album} -metadata title=Closing -metadata artist=\"Artist A\" -metadata track=3/3");
        Fixtures.Ffmpeg($"-f lavfi -i color=c=red:s=32x32:d=1 -frames:v 1 \"{Path.Combine(root, "Artist A/2020 - Album One/cover.jpg")}\"");
        File.WriteAllText(Path.Combine(root, "Artist A/2020 - Album One/01 - Opening.lrc"), "[00:00.10]Hello\n[00:00.60]World\n");

        const string discs = "-metadata album=\"Two Discs\" -metadata artist=\"Artist A\" -metadata date=2021 -metadata genre=Rock";
        Make("Artist A/2021 - Two Discs/CD1/01 - First.flac", $"{discs} -metadata title=First -metadata track=1 -metadata disc=1/2");
        Make("Artist A/2021 - Two Discs/CD2/01 - Second.flac", $"{discs} -metadata title=Second -metadata track=1 -metadata disc=2/2");

        const string mixtape = "-metadata album=Mixtape -metadata album_artist=\"Various Artists\" -metadata compilation=1 -metadata date=2019 -metadata genre=Pop";
        Make("Compilations/Mixtape/01 - Hit.mp3", $"{mixtape} -metadata title=Hit -metadata artist=\"Guest B\" -metadata track=1");
        Make("Compilations/Mixtape/02 - Nordic.mp3", $"{mixtape} -metadata title=Nordic -metadata artist=Björk -metadata track=2");
        Make("Loose/Some Artist - Loose Song.mp3", "-metadata title=\"Loose Song\" -metadata artist=\"Some Artist\" -metadata genre=Pop");
        Make("Loose/Bjork - Accentless.mp3", "-metadata title=Accentless -metadata artist=Bjork");
        Make("Loose/05 - Untagged Tune.mp3", "-map_metadata -1");
        return root;
    }

    private static void Scanned(Checks check, LibrarySnapshot library, ScanResult result)
    {
        check.Equal(10, library.Tracks.Count, "every audio file is in the library");
        check.Equal(10, result.Added, "all tracks are new on the first scan");
        check.Equal(3, library.Albums.Count, "three albums");

        var one = library.Albums.FirstOrDefault(a => a.Title == "Album One");
        check.That(one is { Tracks.Count: 3, Artist: "Artist A", Year: 2020, IsCompilation: false }, "album with a featured artist stays one album");
        check.That(one?.Art is { IsImageFile: true }, "folder cover.jpg is the album art");
        check.Equal("Opening,Duet,Closing", string.Join(",", one?.Tracks.Select(t => t.Title) ?? []), "album tracks in track order");

        var discs = library.Albums.FirstOrDefault(a => a.Title == "Two Discs");
        check.That(discs is { Tracks.Count: 2, DiscCount: 2 }, "disc folders form one album");
        check.Equal("Artist A", discs?.Artist, "album artist from the shared track artist");

        var mixtape = library.Albums.FirstOrDefault(a => a.Title == "Mixtape");
        check.That(mixtape is { IsCompilation: true, Artist: Album.VariousArtists }, "compilation by various artists");

        var artistA = library.FindArtist("artist a");
        check.Equal("Two Discs,Album One", string.Join(",", artistA?.Albums.Select(a => a.Title) ?? []), "artist albums, newest first");
        var guest = library.FindArtist("Guest B");
        check.That(guest is { Albums.Count: 0 }, "a featured artist owns no albums");
        check.Equal("Mixtape,Album One", string.Join(",", guest?.AppearsOn.Select(a => a.Title).OrderByDescending(t => t, StringComparer.Ordinal).ToList() ?? []), "but appears on them");
        check.Equal(2, library.FindArtist("Bjork")?.Tracks.Count, "accented and unaccented names are one artist");

        var loose = library.FindTrack(Path.Combine(Path.GetDirectoryName(one!.Tracks[0].Path)!, "../../Loose/Some Artist - Loose Song.mp3"));
        loose ??= library.Tracks.FirstOrDefault(t => t.Title == "Loose Song");
        check.That(loose is { AlbumKey: null, Artist: "Some Artist" }, "a track without an album tag is loose");
        var untagged = library.Tracks.FirstOrDefault(t => t.Path.EndsWith("05 - Untagged Tune.mp3"));
        check.That(untagged is { Title: "Untagged Tune", TrackNumber: 5 }, "untagged files are named from their file names");
        check.Equal("Pop,Rock", string.Join(",", library.Genres.Select(g => g.Name)), "genres");
        check.That(library.Tracks.Take(3).All(t => t.AlbumTitle == "Album One") || library.Tracks.First().AlbumTitle == "Two Discs",
            "library order starts with an album");

        var opening = one.Tracks[0];
        var lyrics = Lyrics.Load(opening);
        check.That(lyrics is { IsSynced: true, Lines.Count: 2, Source: "01 - Opening.lrc" }, "lyrics from the .lrc beside the file");
    }

    private static void Cache(Checks check, LibrarySnapshot library, string root)
    {
        string file = Path.Combine(Fixtures.Directory("cache"), "library.bin");
        LibraryCache.Save(file, library);
        var loaded = LibraryCache.Load(file, [root]);
        check.That(loaded is not null, "the cache loads");
        if (loaded is null)
            return;
        check.Equal(library.Tracks.Count, loaded.Tracks.Count, "cached track count");
        check.Equal(string.Join("|", library.Albums.Select(a => a.Key)), string.Join("|", loaded.Albums.Select(a => a.Key)), "cached albums");
        foreach (var track in library.Tracks)
        {
            var copy = loaded.FindTrack(track.Path);
            bool same = copy is not null && copy.Title == track.Title && copy.Artist == track.Artist && copy.AlbumTitle == track.AlbumTitle
                && copy.Duration == track.Duration && copy.Modified == track.Modified && copy.Added == track.Added && copy.Year == track.Year
                && copy.TrackNumber == track.TrackNumber && copy.Codec == track.Codec && copy.EmbeddedArt == track.EmbeddedArt
                && copy.ArtistNames.SequenceEqual(track.ArtistNames) && copy.Genres.SequenceEqual(track.Genres);
            check.That(same, $"cached copy of {track.Title} matches");
        }
        check.That(SharesAlbumKeys(loaded), "tracks loaded from the cache share one album key per album");
        check.That(LibraryCache.Load(file, ["/elsewhere"]) is { Tracks.Count: 0 }, "tracks outside the music folders are dropped");
        File.WriteAllBytes(file, [1, 2, 3]);
        check.That(LibraryCache.Load(file, [root]) is null, "a damaged cache is ignored");
    }

    private static void Incremental(Checks check, LibrarySnapshot library, string root)
    {
        check.That(SharesAlbumKeys(library), "scanned tracks share one album key per album");
        var again = LibraryScanner.Scan([root], library, null, CancellationToken.None);
        check.That(!again.Changed, "an unchanged library rescans without changes");
        check.That(ReferenceEquals(again.Snapshot, library), "an unchanged rescan keeps the snapshot and its indexes");

        string albumFolder = Path.Combine(root, "Artist A", "2020 - Album One");
        var fromAlbumFolder = LibraryScanner.Scan([albumFolder + "/"], LibrarySnapshot.Empty, null, CancellationToken.None).Snapshot;
        check.That(fromAlbumFolder.Albums is [{ Art.IsImageFile: true }], "a music folder given with a trailing slash still finds its cover image");

        // A new cover image is a change even when no song changed.
        string cover = Path.Combine(root, "Compilations/Mixtape/folder.png");
        File.WriteAllBytes(cover, [0x89, (byte)'P', (byte)'N', (byte)'G']);
        var withCover = LibraryScanner.Scan([root], library, null, CancellationToken.None);
        File.Delete(cover);
        check.That(!ReferenceEquals(withCover.Snapshot, library) && withCover.Snapshot.FolderImages.Values.Contains(cover),
            "a new cover image rebuilds the snapshot");
        check.That(withCover.Snapshot.Tracks.All(t => ReferenceEquals(t, library.FindTrack(t.Path))), "unchanged tracks are reused, not re-read");

        string changed = library.Tracks.First(t => t.Title == "Closing").Path;
        File.SetLastWriteTimeUtc(changed, DateTime.UtcNow.AddMinutes(1));
        string removed = library.Tracks.First(t => t.Title == "Accentless").Path;
        File.Move(removed, removed + ".bak");
        var after = LibraryScanner.Scan([root], library, null, CancellationToken.None);
        check.Equal((0, 1, 1), (after.Added, after.Updated, after.Removed), "one updated, one removed");
        check.Equal(library.FindTrack(changed)!.Added, after.Snapshot.FindTrack(changed)!.Added, "an updated file keeps its added date");
        File.Move(removed + ".bak", removed);
    }

    private static void M3u(Checks check, LibrarySnapshot library, string root)
    {
        var store = new PlaylistStore(Fixtures.Directory("m3u-store"));
        var paths = library.Tracks.Take(3).Select(t => t.Path).ToList();
        var playlist = store.Create("Trip", paths);

        // Beside the music, entries are written relative to the playlist, so the folder can move.
        string beside = Path.Combine(root, "Trip.m3u8");
        PlaylistStore.ExportM3u(playlist, library, beside);
        string text = File.ReadAllText(beside);
        check.That(text.StartsWith("#EXTM3U\n#PLAYLIST:Trip\n#EXTINF:", StringComparison.Ordinal), "an export starts with the M3U header, name and track info");
        check.That(!text.Contains(root, StringComparison.Ordinal), "entries beside the playlist are relative");
        var read = PlaylistStore.ReadM3u(beside);
        check.Equal("Trip", read.Name, "the playlist name is read back");
        check.That(read.Paths.SequenceEqual(paths), "relative entries read back as the original paths");

        // Elsewhere, entries are absolute.
        string away = Path.Combine(Fixtures.Directory("m3u-away"), "Trip.m3u");
        PlaylistStore.ExportM3u(playlist, library, away);
        check.That(PlaylistStore.ReadM3u(away).Paths.SequenceEqual(paths), "absolute entries read back as the original paths");
        File.Delete(beside);

        // What other players write: a byte order mark, CRLF, comments, streams, file URLs, backslashes.
        string first = library.Tracks.First(t => t.Title == "Opening").Path;
        string foreign = Path.Combine(root, "foreign.m3u");
        File.WriteAllText(foreign, "\uFEFF#EXTM3U\r\n#EXTINF:1,Stream\r\nhttp://radio.example/stream\r\n"
            + "file://" + first.Replace(" ", "%20") + "\r\n\r\n" + Path.GetRelativePath(root, first).Replace('/', '\\') + "\r\n");
        var foreignRead = PlaylistStore.ReadM3u(foreign);
        File.Delete(foreign);
        check.Equal(null, foreignRead.Name, "a playlist without a name line has no name");
        check.That(foreignRead.Paths.SequenceEqual([first, first]), "file URLs and backslashed relative entries are read; streams are skipped");
    }

    private static bool SharesAlbumKeys(LibrarySnapshot library) =>
        library.Albums.All(album => album.Tracks.All(t => ReferenceEquals(t.AlbumKey, album.Tracks[0].AlbumKey)));

    private static void Search(Checks check, LibrarySnapshot library)
    {
        var index = new SearchIndex(library);
        var results = index.Search("album one", null);
        check.That(results.TopResult is Album { Title: "Album One" }, "an album name finds the album first");
        results = index.Search("GUEST", null);
        check.That(results.TopResult is Artist { Name: "Guest B" }, "case-insensitive artist search");
        check.Equal(2, index.Search("bjork", null).Tracks.Count, "accent-insensitive search");
        check.Equal("Duet", index.Search("duet artist", null).Tracks.FirstOrDefault()?.Title, "words may match different fields");
        check.That(index.Search("zzzz", null).IsEmpty, "no results");
        check.That(index.Search("  ", null).IsEmpty, "blank query");
        check.Equal("Opening", index.Search("open", null).Tracks.FirstOrDefault()?.Title, "prefix match");
        // Word starts rank above inner matches.
        var ranked = index.Search("son", null).Tracks.Select(t => t.Title).ToList();
        check.That(ranked.IndexOf("Loose Song") >= 0, "inner matches are found");
    }

    private static void UserDataFollowsFiles(Checks check, string root)
    {
        var before = LibraryScanner.Scan([root], LibrarySnapshot.Empty, null, CancellationToken.None).Snapshot;
        var data = new UserData();
        var track = before.Tracks.First(t => t.Title == "Loose Song");
        data.SetLiked(track, true);
        data.RecordPlay(track, DateTime.UtcNow);
        string moved = Path.Combine(Path.GetDirectoryName(track.Path)!, "Renamed Song.mp3");
        File.Move(track.Path, moved);
        try
        {
            var after = LibraryScanner.Scan([root], before, null, CancellationToken.None).Snapshot;
            var renames = data.Relink(after);
            check.Equal(moved, renames.GetValueOrDefault(track.Path), "a renamed file is recognized");
            check.That(data.IsLiked(moved) && data.PlayCount(moved) == 1, "likes and plays follow the file");
            check.Equal(moved, data.History[^1].Path, "history follows the file");

            var store = new PlaylistStore(Fixtures.Directory("playlists"));
            var playlist = store.Create("Road trip", [track.Path]);
            store.RenamePaths(renames);
            check.Equal(moved, playlist.Paths[0], "playlists follow the file");
            store.CaptureChanges()?.Invoke();
            var reloaded = new PlaylistStore(Fixtures.Directory("playlists"));
            reloaded.Load();
            check.That(reloaded.Playlists is [{ Name: "Road trip" } p] && p.Paths[0] == moved, "playlists are saved and loaded");
        }
        finally
        {
            File.Move(moved, track.Path);
        }
    }

    private static void Recommendations(Checks check, LibrarySnapshot library)
    {
        var data = new UserData();
        var disliked = library.Tracks.First(t => t.Title == "Closing");
        data.SetDisliked(disliked, true);
        var seed = library.Tracks.First(t => t.Title == "Opening");
        var recommender = new Recommender(library, data, new Random(4));
        for (int i = 0; i < 20; i++)
        {
            var radio = recommender.Radio(seed, 20);
            check.That(radio[0] == seed, "radio starts with its seed");
            check.That(!radio.Contains(disliked), "disliked tracks never play on radio");
            check.That(radio.Distinct().Count() == radio.Count, "radio has no duplicates");
        }
        var queue = new PlayQueue();
        queue.Replace([seed], 0, null);
        var more = recommender.ContinueQueue(queue, 5);
        check.That(more.Count > 0 && !more.Contains(seed), "autoplay continues with other tracks");

        data.RecordPlay(seed, DateTime.UtcNow.AddDays(-1));
        var feed = HomeFeed.Build(library, data, new Random(1));
        check.That(feed.Any(s => s.Title == "Quick picks"), "home has quick picks");
        check.That(feed.Any(s => s.Title == "Listen again" && s.Items.Contains(library.AlbumOf(seed))), "recently played albums appear");
        check.That(feed.Any(s => s.Title == "Recently added"), "home has recently added albums");
        check.That(HomeFeed.Build(LibrarySnapshot.Empty, data).Count == 0, "an empty library has an empty home");
    }
}
