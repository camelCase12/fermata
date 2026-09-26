# Third-party notices

Fermata is built on the following software. Their full license texts are in [licenses](licenses).

| Component | License | Text |
| --- | --- | --- |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | MIT | [Avalonia.txt](licenses/Avalonia.txt) |
| [SkiaSharp](https://github.com/mono/SkiaSharp) and HarfBuzzSharp | MIT | [SkiaSharp-HarfBuzzSharp.txt](licenses/SkiaSharp-HarfBuzzSharp.txt) |
| Skia, HarfBuzz and the libraries built into `libSkiaSharp.so` and `libHarfBuzzSharp.so` | Various | [SkiaSharp-HarfBuzzSharp-native-third-party-notices.txt](licenses/SkiaSharp-HarfBuzzSharp-native-third-party-notices.txt) |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MIT | [CommunityToolkit.Mvvm.txt](licenses/CommunityToolkit.Mvvm.txt), [notices](licenses/CommunityToolkit.Mvvm-third-party-notices.txt) |
| [Tmds.DBus](https://github.com/tmds/Tmds.DBus) | MIT | [Tmds.DBus.txt](licenses/Tmds.DBus.txt) |
| [MicroCom](https://github.com/kekekeks/MicroCom) | MIT | [MicroCom.txt](licenses/MicroCom.txt) |
| [Atkinson Hyperlegible](https://github.com/googlefonts/atkinson-hyperlegible), the interface font | SIL Open Font License 1.1 | [AtkinsonHyperlegible-font.txt](licenses/AtkinsonHyperlegible-font.txt) |
| [Bricolage Grotesque](https://github.com/ateliertriay/bricolage), the heading font | SIL Open Font License 1.1 | [BricolageGrotesque-font.txt](licenses/BricolageGrotesque-font.txt) |
| [IBM Plex Mono](https://github.com/IBM/plex), the font for times and numbers | SIL Open Font License 1.1 | [IBMPlexMono-font.txt](licenses/IBMPlexMono-font.txt) |
| [.NET runtime](https://github.com/dotnet/runtime), compiled into the executable | MIT | [dotnet-runtime.txt](licenses/dotnet-runtime.txt), [notices](licenses/dotnet-runtime-third-party-notices.txt) |

Fermata plays audio through [libmpv](https://mpv.io), which is installed separately and loaded at run
time. It is not included with Fermata. Depending on how it was built, libmpv is licensed under the GPL
2.0 or later or the LGPL 2.1 or later.
