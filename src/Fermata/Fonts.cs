using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace Fermata;

/// <summary>The fonts bundled with Fermata.</summary>
internal static class Fonts
{
    /// <summary>Registers the bundled fonts as the <c>fonts:Fermata</c> collection, with the body font as the default.</summary>
    public static AppBuilder WithFermataFonts(this AppBuilder builder) =>
        builder
            .ConfigureFonts(fonts => fonts.AddFontCollection(
                new EmbeddedFontCollection(new Uri("fonts:Fermata"), new Uri("avares://fermata/Assets/Fonts"))))
            .With(new FontManagerOptions { DefaultFamilyName = "fonts:Fermata#Fermata Sans" });
}
