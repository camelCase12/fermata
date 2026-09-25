# Fermata

A music player for Linux, for the music files you already have. It's written in C# with Avalonia
and compiled to a native binary, so it starts fast and sits at basically zero CPU when you're not
doing anything.

It has a home page with mixes, radio, a queue you can edit, lyrics, playlists, likes and history.
Shuffle plays songs in the order the queue shows them.

![Now playing](docs/now-playing.png)

The background and the accent colour come from the cover of whatever's playing.

![A playlist](docs/playlist.png)

![Albums](docs/albums.png)

(The songs and covers in these screenshots are made up. `tools/make-sample-library.py` generates them.)

## Building

You need the .NET 10 SDK to build it and libmpv to run it.

```sh
./build.sh
bin/fermata/fermata
```

`./install.sh` puts it in `~/.local` with a launcher entry, and `./install.sh --uninstall` takes it
back out. By default it reads your `~/Music` folder. You can change that in Settings.

You can also control it from the command line:

```sh
fermata song.flac some-folder/   # plays them in the window that's already open
fermata --play-pause             # also --next, --previous, --stop
```

Media keys work too.

## Keys

| | |
| --- | --- |
| Play / pause | `Space` or `K` |
| Next / previous | `Ctrl+→` / `Ctrl+←` |
| Skip back / forward 10 s | `J` / `L` |
| Volume, mute | `Ctrl+↑` / `Ctrl+↓`, `M` |
| Shuffle, repeat | `S`, `R` |
| Like the current song | `F` |
| Now playing | `Q` |
| Search | `Ctrl+F` or `/` |
| Back / forward | `Alt+←` / `Alt+→` |
| Quit | `Ctrl+Q` |

## Notes

Fermata never writes to your music files. Settings, playlists and likes live in
`~/.config/fermata` and `~/.local/share/fermata`.

If you're curious how it works, or want the test and benchmark details, see [DESIGN.md](DESIGN.md).
