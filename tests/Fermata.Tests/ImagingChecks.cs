using Fermata.Imaging;

namespace Fermata.Tests;

internal static class ImagingChecks
{
    private const int Size = 48;

    public static void Run(Checks check)
    {
        CheckOklab(check);
        CheckTwoHues(check);
        CheckRichOverCommon(check);
        CheckGrey(check);
        CheckDark(check);
        CheckStructure(check);
        CheckMosaic(check);
    }

    private static void CheckOklab(Checks check)
    {
        var white = Oklab.FromSrgb(255, 255, 255);
        check.Near(1, white.L, 1e-3, "white has lightness 1");
        check.Near(0, white.Chroma, 1e-3, "white has no chroma");
        foreach (var (r, g, b) in new (byte, byte, byte)[] { (255, 0, 0), (12, 200, 90), (40, 40, 180), (128, 128, 128) })
            check.Equal((r, g, b), Oklab.FromSrgb(r, g, b).ToSrgb(), $"sRGB {r},{g},{b} survives a round trip");
        var (mr, mg, mb) = Oklab.FromLch(0.7, 0.4, 2.0).ToSrgb();
        var mapped = Oklab.FromSrgb(mr, mg, mb);
        check.Near(0.7, mapped.L, 0.02, "gamut mapping keeps lightness");
        check.Near(2.0, mapped.Hue, 0.05, "gamut mapping keeps hue");
    }

    /// <summary>A cover split between red and blue must give two families and no purple or brown.</summary>
    private static void CheckTwoHues(Checks check)
    {
        var palette = CoverAnalysis.Analyze(Paint((x, y) => x < Size / 2 ? (200, 30, 40) : (30, 60, 210)), Size);
        var red = Oklab.FromSrgb(200, 30, 40);
        var blue = Oklab.FromSrgb(30, 60, 210);
        check.Equal(2, palette.Blobs.Count, "a two-colour cover has two families");
        check.That(palette.Blobs.Any(b => Near(b.Color, red)), "one family is the red");
        check.That(palette.Blobs.Any(b => Near(b.Color, blue)), "one family is the blue");
        var redBlob = palette.Blobs.First(b => Near(b.Color, red));
        check.Near(0.25, redBlob.X, 0.02, "the red family sits on the left");
        check.Near(0.5, redBlob.Y, 0.02, "the red family is vertically centred");
        check.That(redBlob.Sxx < redBlob.Syy, "the red family is taller than it is wide");
    }

    /// <summary>
    /// A cover that is mostly dull olive with a vivid orange patch takes the orange as its accent,
    /// provided the patch is not tiny.
    /// </summary>
    private static void CheckRichOverCommon(Checks check)
    {
        var palette = CoverAnalysis.Analyze(Paint((x, y) =>
            x < Size / 3 && y < Size / 3 ? (250, 120, 20) : (110, 105, 70)), Size);
        var orange = Oklab.FromSrgb(250, 120, 20);
        check.That(palette.Accent is { } accent && Near(accent, orange), "the vivid patch is the accent");

        var speck = CoverAnalysis.Analyze(Paint((x, y) =>
            x < 2 && y < 2 ? (250, 20, 200) : (40, 90, 200)), Size);
        check.That(speck.Accent is { } blue && Near(blue, Oklab.FromSrgb(40, 90, 200)), "a speck of colour does not become the accent");
    }

    private static void CheckGrey(Checks check)
    {
        var palette = CoverAnalysis.Analyze(Paint((x, y) => (x * 5, x * 5, x * 5)), Size);
        check.Equal<Oklab?>(null, palette.Accent, "a grey cover has no accent");
        check.Equal(2, palette.Blobs.Count, "a grey ramp has a dark and a light neutral family");
        check.That(palette.Blobs.All(b => b.Color.Chroma < 0.01), "the neutral families are grey");
        check.That(palette.Blobs.Any(b => b.X < 0.5 && b.Color.L < 0.5) && palette.Blobs.Any(b => b.X > 0.5 && b.Color.L > 0.5),
            "the dark family is on the dark side and the light family on the light side");
    }

    /// <summary>A black cover with a small green light is mostly dark, with the green as its accent.</summary>
    private static void CheckDark(Checks check)
    {
        var palette = CoverAnalysis.Analyze(Paint((x, y) =>
            (x - 30) * (x - 30) + (y - 20) * (y - 20) < 64 ? (140, 200, 60) : (4, 5, 4)), Size);
        check.Equal(2, palette.Blobs.Count, "a dark cover with one light has two families");
        check.That(palette.Blobs[0].Color.L < 0.15 && palette.Blobs[0].Weight > 0.8, "the heaviest family is the dark background");
        check.That(palette.Accent is { } green && Near(green, Oklab.FromSrgb(140, 200, 60)), "the light is the accent");
        var light = palette.Blobs[1];
        check.Near(30.5 / Size, light.X, 0.01, "the light family is where the light is");
    }

    private static void CheckStructure(Checks check)
    {
        var stripes = CoverAnalysis.Analyze(Paint((x, y) => y / 4 % 2 == 0 ? (240, 240, 240) : (20, 20, 20)), Size);
        check.Near(0, Math.Sin(stripes.Orientation), 0.05, "horizontal stripes run horizontally");
        check.That(stripes.Coherence > 0.9, "stripes are coherent");
        check.That(stripes.Detail > 0.5, "stripes are detailed");

        var flat = CoverAnalysis.Analyze(Paint((x, y) => (90, 20, 20)), Size);
        check.Near(0, flat.Detail, 1e-6, "a flat cover has no detail");
        check.That(flat.Seed != stripes.Seed, "different covers have different seeds");
    }

    private static void CheckMosaic(Checks check)
    {
        var red = CoverAnalysis.Analyze(Paint((x, y) => (200, 30, 40)), Size);
        var blue = CoverAnalysis.Analyze(Paint((x, y) => (30, 60, 210)), Size);
        var mosaic = CoverPalette.Mosaic([red, blue, blue, red]);
        check.Equal(4, mosaic.Blobs.Count, "each quadrant keeps its family");
        check.That(mosaic.Blobs.Any(b => Math.Abs(b.X - 0.75) < 0.01 && Math.Abs(b.Y - 0.25) < 0.01 && Near(b.Color, blue.Blobs[0].Color)),
            "the second cover's family moves to the top right");
        check.Near(1, mosaic.Blobs.Sum(b => b.Weight), 1e-9, "mosaic weights still sum to one");
    }

    private static bool Near(Oklab a, Oklab b) =>
        Math.Abs(a.L - b.L) < 0.03 && Math.Abs(a.A - b.A) < 0.03 && Math.Abs(a.B - b.B) < 0.03;

    private static byte[] Paint(Func<int, int, (int R, int G, int B)> color)
    {
        var pixels = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                var (r, g, b) = color(x, y);
                int i = (y * Size + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)r, (byte)g, (byte)b, 255);
            }
        }
        return pixels;
    }
}
