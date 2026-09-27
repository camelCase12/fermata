# How Fermata works

This is a map of the code and the reasons behind its shape. The README covers using and building it.

## Layout

| Project | Contents |
| --- | --- |
| `src/Fermata.Core` | Everything that is not UI, with no Avalonia dependency. `Metadata`: tag readers. `Library`: tracks, the library index, scanning, the index cache, search, lyrics, recommendations, home sections, likes and history, playlists. `Playback`: the queue, the player and the libmpv engine. `Integration`: MPRIS, the single-instance socket and the launcher entry's ID. `Storage`: paths, settings, atomic and deferred writes. `Text`: accent folding and artist credits. |
| `src/Fermata` | The Avalonia application. `Views` are XAML with small code-behind files; `ViewModels` use CommunityToolkit.Mvvm's source generators; `Controls` holds the few custom-drawn controls (cover art, mosaics, the seek bar) and row dragging; `Services` holds the composition root (`AppServices`), the cover art cache, the MPRIS bridge and the Wayland requests Avalonia does not make (`WaylandWindow`); `Styles` holds the theme tokens, icons and control themes. |
| `tests/Fermata.Tests` | A console program of checks (no test framework), including randomized tests of the player. |
| `tests/Fermata.Ui` | Renders every page, or the ambient backdrop for given covers, headlessly to PNG files. |
| `tools` | Sample-library and table generators, and the isolated test session with its input tools. |

Everything is compiled ahead of time (NativeAOT) with trimming, so there is no reflection-based
serialization or binding: JSON uses source-generated contexts, XAML bindings are compiled, and view
lookup is a switch.

## Threads

Application state (the queue, the player, the current library snapshot, playlists, likes, settings)
belongs to the UI thread. Anything slow runs elsewhere and hands back an immutable result:

- A library scan builds a complete new `LibrarySnapshot` on a worker thread. The UI swaps one
  reference to publish it, so no reader ever sees a half-updated index.
- Tags are read in parallel (up to four workers), each with its own string pool.
- Cover art is decoded by up to three background tasks and delivered on the UI thread.
- Saves capture their data on the UI thread, then write on a background thread (`DeferredSave`).
- mpv runs its own threads. Its wakeup callback posts a drain of its events to the UI thread, so
  engine events arrive in the same place as clicks and key presses.

As a result there are no locks around application state, and no question of which thread changed
the queue.

## Playback, and why shuffle cannot quietly not work

A shuffle button that lights up while songs keep playing in order is a race. Two things decide what
plays next, and they disagree: the app's idea of the order, and the audio engine's already-loaded
next song. Fermata closes that race structurally rather than by timing.

**One authority.** `PlayQueue` makes every decision about order, on one thread. It holds the entries
in the order they were queued and, while shuffle is on, the order they will play; `Entries` is always
the order that plays, so the queue on screen is what you will hear. Shuffle is that second list, not
a flag that other code has to remember to check. Turning shuffle on keeps what has played in place
and shuffles only what is left. Turning it off continues from the current song in the original order.
With repeat-all, each new cycle is a fresh permutation that never starts with the song that just
ended. `PeekNext()` (what the engine should preload) is defined to match `Advance()` (what happens
when a song ends), and randomized tests check that they always agree.

**The engine follows the queue.** For gapless playback mpv holds the playing file and one
preloaded next file. After every queue change (shuffle, repeat, reorder, play next, remove), the
`Player` immediately replaces the preloaded file with `PeekNext()`. So the engine always holds the
song the queue says comes next, whatever was toggled a moment ago.

**Moments are not instants.** A song can end at the moment you press shuffle, and mpv may already
have started the old preloaded file before the replacement reaches it. Every file handed to mpv is
remembered with its queue entry and mpv's playlist id. When mpv reports that an item started:

- if it is the expected next entry, the queue advances;
- if it is a preload that was just replaced, the queue is reconciled to the entry that is really
  playing (`AdvanceTo`), rather than showing one song while another plays;
- if it is older than what is already current, it is ignored. mpv's ids only grow, so "older" is
  well defined.

The end of the queue has its own race: mpv going idle while a preload is on its way. The player
tracks mpv's `idle-active` property and restarts playback explicitly when needed. A file that fails
to play is skipped explicitly, never left waiting for an end-of-file event that will not come.

**Tested by interleaving.** `FakeEngine` models mpv's playlist, idle state and event delivery,
including events that arrive late. The randomized test in `PlayerChecks` performs 14 kinds of
listener action (next, previous, shuffle, repeat, play next, add, remove, move, jump, play/pause,
start something new) interleaved with songs ending. One step in three leaves the engine's events
undelivered, so the next action races with them. After events settle it checks four things: the
track shown is the file playing, the preload is the queue's next entry, and playing and paused
agree between player and engine. The suite runs 60,000 steps; development runs covered 3 million
across seeds. That testing found five real races, all fixed:

1. A file failing to load while a preload was pending left playback waiting.
2. A preload appended just after mpv went idle at the end of the queue was never started.
3. A replaced preload that mpv had already started was ignored, so the wrong song was shown.
4. Removing songs while paused left a stale preload in the engine.
5. Adopting a started preload forgot newer preloads as well as older ones.

`MpvChecks` then runs the real libmpv (with silent output) through gapless transitions, a broken
file and the end of the queue.

Plays are counted after half a song or four minutes, whichever comes first. Skips are counted only
when you press Next.

## Library

**Tags.** Fermata's own readers cover ID3v1, ID3v2.2–2.4 (including unsynchronization, compressed
frames and the common non-standard frame sizes), APEv2, Vorbis comments (FLAC, Ogg Vorbis, Opus,
Speex, FLAC in Ogg), MP4 atoms, RIFF INFO, AIFF, ASF and Matroska. Durations come from stream headers
(Xing/VBRI, FLAC STREAMINFO, Ogg granule positions, MP4 `mdhd`), never from decoding. Files are read
through a 16 KB window, and cover art is located by offset but not loaded until a cover is shown.
`--compare-ffprobe` checks tags and durations against ffprobe for a whole folder.

**Scanning.** The scanner enumerates folders with `FileSystemEnumerable`. A file whose size and
modification time match the previous snapshot is matched to its track through a path built on the
stack, so the track object is reused and no string is allocated. When nothing changed, the previous
snapshot is returned unchanged and nothing is rebuilt. For 50,000 files that rescan takes 0.16 s and
allocates 8 MB. New and changed files are read in parallel.

**The index.** `LibrarySnapshot` is immutable. Albums are grouped by folded album title, album
folder (disc folders such as `CD2` count as their parent) and MusicBrainz album id, so two albums
with the same name stay apart and one album split across disc folders stays together. Artists are
merged by folded name, and featured artists ("feat.", "×", "vs.") get "appears on" credits. Each
album's key string exists once however many tracks share it. The whole index for 50,000 tracks
takes about 41 MB.

**Cache.** A binary copy of the index, with every repeated string stored once, lets the library
appear about 0.2 s after start at 50,000 tracks. A verifying scan then runs in the background.

**Text.** Invariant globalization, which keeps the executable small, makes
`string.Normalize` a no-op, so accent folding uses tables generated from Unicode data
(`tools/generate-folding.py`). Search, sorting and grouping all compare folded text: Björk matches
Bjork, and "The Beatles" sorts under B.

**Following files.** A folder watcher triggers a rescan two seconds after changes settle. Likes and
play counts carry a folded title/artist/album identity. When a file disappears and exactly one new
file has the same identity, its history moves to that file, and playlists are updated the same way.

## UI and performance

**Nothing polls.** While paused, minimized or idle, no timer runs and CPU use is effectively zero.
(Wayland does not tell a window that it is minimized or hidden, so there the position timer keeps
running while a song plays. The compositor stops asking for frames, so nothing is drawn.) While
playing, the one timer is the position timer. Its interval is the time the progress bar takes to
move one pixel (every ~150 ms for a four-minute song on a wide window, clamped to 40 ms–1 s). The
seek bar invalidates only when its fill has moved by at least one device pixel, and text is updated
only when the displayed second changes.

**Only visible things exist.** Song lists are virtualized list boxes inside the page's scroll
viewer. They have no scroll viewer of their own, so virtualization follows the page's viewport and
Page Up/Down reach the page. Album and artist grids are virtualized rows of cards, rebuilt only when
the number of columns changes. Scrolling 50,000 songs as fast as a wheel goes costs about 17% of
one core.

**Cover art.** Covers are decoded off the UI thread at the size they are shown, rounded up to one of
a few bucket sizes. Every control showing the same cover shares one decoded image through a lease.
Released images stay in a 48 MB least-recently-used pool, so scrolling back is instant. Songs
without art get a gradient placeholder whose colour comes from the album name, cached as one shared
brush per hue.

**Styles.** Every visual state of a row or control (playing, liked, hovered, selected, dragging) is
a class set in code and styled in XAML. Code never sets local visual values, which would override
styles and produce the mismatched hover states that inconsistent UIs are made of. Colours, shadows
and focus rings are named tokens in `Styles/Theme.axaml`, each control theme draws a focus ring
matching its own shape, and Fluent's built-in controls (drop-downs, text fields) are restyled
through the same tokens. Text fields are translucent white washes rather than grey fills, so over a
cover's colours they take on its tint instead of sitting on it as dark boxes. Buttons are keys: a flat
face on a solid lip two pixels deep, in a darker shade of the face, and pressing moves the face down
onto the lip. Icon buttons show the key only while hovered.

**Ambient backdrop.** Now Playing, and album, artist and playlist pages, sit on a dimmed field made
from the cover (`AmbientBackdrop`). It has two styles, and Settings chooses between them.

The soft style is the cover blurred far out of focus. Each cover is reduced to an 8×8 grid of average
colours, averaged in linear light. A playlist's four covers make one grid in quadrants, as its mosaic
shows them. A cubic B-spline interpolates the grid, which is smooth and never overshoots, using four
bilinear lookups instead of sixteen.

The generated style, which is the default, paints the cover's colour families instead of its average
colours, so two hues are never blended into a third colour the cover does not have. `CoverAnalysis`
reads a 48-pixel copy of the cover in OKLab:

- A hue histogram, weighted towards rich colours, gives up to four peaks. Each chromatic pixel joins
  the nearest peak within 45°. The family's colour takes the mean lightness and hue of the pixels
  near its peak and the 80th percentile of their chroma, so it is a colour the cover really shows.
- The remaining pixels form a dark and a light neutral family, so black and white are not averaged
  into grey.
- Each family records where its pixels are (their centroid and covariance) and what fraction of the
  cover they cover.
- The structure tensor of the lightness gives the direction the cover's shapes run in, how
  consistently they follow it, and how busy the cover is.
- The accent is the family with the highest (chroma-weighted mass)^0.6 × chroma, which favours a rich
  hue that is also common, and ignores a speck of colour.

The shader treats the families as a Gaussian mixture. Each pixel scores every family by its area, its
density at that point, and a noise field of its own, and the scores are sharpened so families meet at
soft edges instead of mixing. Neutral families score a little lower, so the cover's colours show
against them. Before scoring, the position is displaced by domain-warped value noise, stretched along
the cover's structure direction, and finer for busier covers. Colours are mixed in OKLab. Analysing a
cover takes under a millisecond, once per cover. The shader is too slow to draw on the CPU, so when frames are drawn
without a GPU (the software renderer, or the headless page renderer) backdrops use the soft style.

Both shaders then work alike:

- saturation goes up a little and bright colours are compressed toward a low luminance ceiling in
  linear light, so hues stay vivid and white text keeps better than 10:1 contrast;
- the field fades into the page background.

All of that is floating-point arithmetic. Only the final colour is rounded to 8 bits, and just before
that the shader adds triangular noise of up to one 8-bit step, fixed per pixel. Neighbouring pixels
then round to adjacent shades instead of forming bands. Along a line through a dark fade, the longest
run of identical pixels drops from 168 px without the noise to 13 px with it, on the CPU renderer and
on NVIDIA's OpenGL alike.

The backdrop crossfades over 650 ms when the art changes, by drawing the old field and then the new
one at rising opacity, and it never animates otherwise. Its Skia objects are built once per recording
and reused on every replay. On pages it stops where the fade reaches the plain background, so the
progress bar's edge redrawing below it never touches it. With a song playing, its cost is lost in the
noise of the measurement, in either style.

**Renderer.** OpenGL. Measured while playing on an RTX 4080, Vulkan used about 40% more memory
(it loads both GPUs' drivers) and more CPU. The software renderer uses less memory but about four
times the CPU. `FERMATA_RENDERING` overrides the choice. On Wayland, OpenGL goes through EGL and
Vulkan is not available. For `FERMATA_RENDERING=software` the Wayland backend is given no OpenGL
versions to try, so it draws on the CPU into shared memory.

**Memory.** With a small library the process is about 300–340 MB resident, and most of that is the
GPU driver: its libraries (shared with every other GL program) and its allocations. Fermata's own
heap is about 8 MB. At 50,000 tracks the heap is about 41 MB after the first scan compacts.

**Startup.** About 50 ms to a window: ahead-of-time compilation, compiled bindings, pages created
when first visited, and the library shown from the cache before any folder is read.

## Saving

Settings, playlists, likes and history, and the session (queue, shuffle order, position) are JSON
files in the XDG directories. Every write goes to a temporary file that is flushed and renamed over
the old one, so a crash leaves either the old file or the new one. Changes are written a moment
after they settle (`DeferredSave`), never on the UI thread, and all pending writes are flushed on
exit. SIGTERM and SIGINT close the window the normal way, so logging out or `kill` loses nothing. A
second signal exits at once.

## Desktop integration

- **MPRIS** (`org.mpris.MediaPlayer2.fermata`) over Tmds.DBus.Protocol, which works under NativeAOT:
  play/pause, next, previous, seek, volume, shuffle, loop status, metadata and cover art. Embedded
  covers are exported to the cache so other programs can show them. Media keys on most desktops go
  through this. Its `DesktopEntry` is the launcher entry's ID, `io.github.camelcase12.Fermata`, so
  desktops show Fermata's name and icon beside the controls, and GNOME brings the window forward
  itself when they are clicked.
- **One instance.** A second launch sends its files or command (`--play-pause`, `--next`, …) over a
  Unix socket in `$XDG_RUNTIME_DIR` and exits, in about 6 ms. When the request brings the window
  forward, it also passes on the activation token that its launcher set (see below).
- **Wayland.** In a Wayland session (`WAYLAND_DISPLAY` set), Fermata opens a Wayland window through
  Avalonia's Wayland backend, which Avalonia 12.1 still calls experimental. If the compositor cannot
  be used, it falls back to an X11 window through XWayland. `FERMATA_PLATFORM=x11` or `wayland`
  decides instead, without the fallback. A Wayland window is sharp at fractional scales, and while a
  song plays it takes less CPU than the XWayland window (see Performance).

  Avalonia.Wayland 12.1.3 sends no app ID, and its `Window.Activate` does nothing. `WaylandWindow`
  makes both requests itself, on the backend's own protocol objects and on its Wayland thread. It
  reaches them through members that are internal to Avalonia, with `UnsafeAccessor`, which the native
  build resolves at compile time. If an update renames them, the requests are skipped with one line
  on standard error, which fails CI (`tools/check-platforms.sh`).

  - The app ID is the launcher entry's ID, so desktops match the window to the launcher, its name and
    its icon. It is sent before the window first appears, but after its first empty commit, so KWin
    window rules that apply only when a window is created do not see it. Avalonia has approved an app
    ID option (pull request 22209); once a release has it, the app ID can come from there.
  - The activation token that a launcher sets in `XDG_ACTIVATION_TOKEN` is used when the window first
    opens, which ends the launch feedback and lets the new window take focus. A second launch forwards
    its token, so opening a song from a file manager brings the running window forward. Without a
    token (MPRIS Raise, a launch from a terminal), Fermata asks the compositor for one, and the
    compositor then usually just marks the window as wanting attention.
  - The window's icon comes from the installed launcher entry; the window does not send one.
- **X11.** The window class is `fermata`, matching `StartupWMClass` in the launcher entry.

## Testing

`tests/Fermata.Tests` is a console program of checks in nine groups:

- **Text folding:** accents, ligatures and sort keys.
- **Imaging:** OKLab conversions and cover analysis, on synthetic covers with known families,
  accents and structure.
- **Storage:** damaged settings, likes and playlists are set aside rather than saved over, and files from a newer version are read but not rewritten.
- **Metadata:** the tag readers, against files ffmpeg writes in every supported format.
- **Library:** grouping, scanning, the cache, incremental rescans, search, following renamed files,
  M3U import and export, and recommendations.
- **Play queue:** shuffle, repeat and editing semantics.
- **Player:** against `FakeEngine`, including the randomized interleaving test.
- **libmpv:** real playback with silent output.
- **MPRIS:** driven by `playerctl` on a private D-Bus daemon.

Checks that need ffmpeg, libmpv or a D-Bus daemon skip themselves when those are missing.

`tests/Fermata.Ui` renders every page with Avalonia's headless platform and real Skia. It includes a
keyboard-focus page and a Page Down page. It also checks that every visible button has a name for
screen readers and that the seek and volume bars present themselves as named sliders.

With `--backdrops OUTPUT IMAGE...` it instead renders the ambient backdrop in both styles for each
image, prints each cover's analysis, and times the analysis.

`tools/isolated-session.sh` runs the real executable in a private headless sway session with
XWayland, D-Bus, XDG directories and silent audio, with `FERMATA_GPU` choosing the GPU. Fermata opens
a Wayland window there, or an X11 one with `FERMATA_PLATFORM=x11`. That is how the measurements
above were made, and how dragging, typing and resizing were exercised with `tools/pointer.c` (the
compositor's virtual pointer), `tools/keyboard.c` (its virtual keyboard) and `tools/xinput.c` (XTEST
keys, for X11 windows only), without touching the real desktop.

`tools/check-platforms.sh`, which CI runs in that session, opens Fermata as a Wayland client and as
an X11 client. It checks that each window carries the identity desktops match to the launcher entry
(the app ID on Wayland, the window class on X11), that the Wayland requests Fermata makes itself
report no failure, and that Fermata then quits cleanly on SIGTERM.

## Choices

- **libmpv for audio.** Decoding every format well, gapless playback, ReplayGain and output device
  selection are mpv's strengths; Fermata controls it through its C API with a small P/Invoke layer.
  The player's logic sits behind `IAudioEngine`, so it is tested against a model of the engine as
  well as the real one.
- **Own tag readers** rather than a tag library: they are allocation-light, AOT-safe, read only the
  bytes needed, and are checked against ffprobe.
- **Plain checks instead of a test framework:** the tests are an ordinary program that starts
  instantly and has no reflection-based discovery.

## Reference

### Files

| What | Where |
| --- | --- |
| Settings | `~/.config/fermata/settings.json` |
| Playlists | `~/.local/share/fermata/playlists/*.json` |
| Likes, dislikes, play counts, history | `~/.local/share/fermata/library-data.json` |
| Queue and position to resume | `~/.local/state/fermata/session.json` |
| Library index, cover art shared over MPRIS | `~/.cache/fermata/` (safe to delete) |

Fermata never writes to your music files. All files are written atomically (a new file renamed
over the old one), so a crash or power cut cannot leave a half-written playlist. Quitting, logging
out or `kill` (SIGTERM) saves everything; a second SIGTERM exits at once.

Every saved file records the version of its format. An older file is brought up to date as it
loads. A file written by a newer Fermata is read as well as possible and never saved over, and
Fermata says so at startup, so going back to an older version cannot quietly lose data.

### Performance

Measured on a Ryzen 9 7900X with an RTX 4080 (NVIDIA 610, OpenGL) unless noted, in a headless
compositor session:

| | |
| --- | --- |
| Start to window shown | ~50 ms |
| Handing a file to the running instance | ~6 ms |
| CPU while paused, minimized or idle | effectively none (under 0.1% of one core) |
| CPU while playing with the window visible | ~1% of one core |
| Memory while playing, 100-song library | ~340 MB resident, mostly the GPU driver; the app's own heap is ~8 MB |
| 50,000-song library: first scan / rescan with nothing changed | 0.6 s / 0.16 s |
| 50,000-song library: start to library shown (from the index) | ~0.2 s |
| 50,000-song library: heap / resident after browsing | ~41 MB / ~400 MB |
| Scrolling 50,000 songs as fast as the wheel goes | ~17% of one core while scrolling |

The table was measured with the X11 window. On a Ryzen 7 8845H laptop (Radeon 780M, OpenGL), in the
same headless session with 20–40 s songs (so the position timer ran at its fastest), the Wayland
window compared with the X11 one as follows. Start to window shown (until sway listed the window):
~220 ms against ~265 ms. Playing with the window visible: 2.4% against 3.5% of one core, the render
thread taking half as much. Idle and paused: at most 0.1% for both. Resident memory: ~220 MB for
both. Playing with the window hidden on another workspace: ~2% against ~1.2%, because the Wayland
window cannot tell that it is hidden.


### Development

```sh
./build.sh test                                             # build, then run the checks
dotnet run --project tests/Fermata.Tests -c Release         # all the checks, about 8 s
FERMATA_FUZZ_RUNS=20000 dotnet run --project tests/Fermata.Tests -c Release   # a longer queue fuzz
dotnet run --project tests/Fermata.Tests -c Release -- --compare-ffprobe DIR  # tags and durations against ffprobe
dotnet run --project tests/Fermata.Tests -c Release -- --make-large-library DIR 50000
dotnet run --project tests/Fermata.Tests -c Release -- --measure-scan DIR
dotnet run --project tests/Fermata.Ui -c Release -- --screenshots OUT_DIR MUSIC_DIR  # every page, headless
tools/make-sample-library.py DIR                            # 105 tagged songs in 12 formats, with covers and lyrics
dotnet run --project tools/icons [PREVIEW.png]                # regenerates the icons in Styles/Icons.axaml
tools/isolated-session.sh tools/check-platforms.sh           # opens Wayland and X11 windows and checks their identity
.venv/bin/python tools/font/build.py                         # rebuilds Fermata Sans into Assets/Fonts (see requirements.txt)
```

The checks cover the tag readers (against files written by ffmpeg), the library and its index,
the queue, the player against a simulated engine (including a randomized test that interleaves
listener actions with engine events), real playback through libmpv (with silent output) and MPRIS
through `playerctl` on a private bus. Tests that need ffmpeg, libmpv or a D-Bus daemon skip
themselves when those are missing.

`tools/isolated-session.sh COMMAND` runs a command in a private headless compositor (sway with
XWayland), with its own D-Bus, XDG directories and silent audio, so Fermata can be driven and
screenshotted without touching your desktop, audio or files. `tools/pointer.c` (the compositor's
virtual pointer), `tools/keyboard.c` (its virtual keyboard) and `tools/xinput.c` (XTEST keys, for
X11 windows) drive it; build instructions are at the top of each file.

Environment variables for troubleshooting: `FERMATA_PLATFORM=wayland|x11` picks the window system
(a Wayland session uses Wayland, falling back to XWayland), `FERMATA_RENDERING=gl|vulkan|software`
picks the renderer, `FERMATA_AUDIO_OUTPUT` sets mpv's audio output (`null` for silence),
`FERMATA_MPV_LOG=FILE` writes mpv's detailed log to a file, and `FERMATA_TRACE_STARTUP=1` prints
startup timings.

### Not included

- Streaming services, podcasts and internet radio: Fermata plays files you have.
- Editing tags. Fermata only reads them.
- Covers that ffmpeg stores as a video stream in WMA or MKA files. Covers in the tags or folder images
  are read.
