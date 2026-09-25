using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Fermata.Controls;
using Fermata.Library;
using Fermata.Services;

/// <summary>
/// Renders the ambient backdrop for each cover image in both styles, with the cover inset for comparison.
/// </summary>
internal static class Backdrops
{
    public static int Render(AppServices services, string output, string[] images)
    {
        var backdrop = new AmbientBackdrop { FadeAmount = 0 };
        var inset = new Image { Width = 200, Height = 200, Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
        var window = new Window { Width = 1280, Height = 720, Content = new Grid { Children = { backdrop, inset } } };
        window.Show();
        foreach (string image in images)
        {
            string name = Path.GetFileNameWithoutExtension(image);
            using var bitmap = new Bitmap(image);
            inset.Source = bitmap;
            services.Art.RequestPalette(ArtSource.ImageFile(Path.GetFullPath(image)), palette =>
            {
                if (palette is null)
                    return;
                Console.WriteLine($"{name}: orientation {palette.Orientation * 180 / Math.PI:0}°, coherence {palette.Coherence:0.00}, detail {palette.Detail:0.00}");
                foreach (var blob in palette.Blobs)
                {
                    var (r, g, b) = blob.Color.ToSrgb();
                    Console.WriteLine($"  #{r:x2}{g:x2}{b:x2} L {blob.Color.L:0.00} C {blob.Color.Chroma:0.000} at ({blob.X:0.00}, {blob.Y:0.00}) " +
                        $"sd ({Math.Sqrt(blob.Sxx):0.00}, {Math.Sqrt(blob.Syy):0.00}) weight {blob.Weight:0.000}");
                }
            });
            foreach (bool generated in new[] { false, true })
            {
                services.Settings.GeneratedBackdrop = generated;
                backdrop.Covers = [];
                Pump(50);
                backdrop.Covers = [ArtSource.ImageFile(Path.GetFullPath(image))];
                Pump(1200);
                var frame = window.CaptureRenderedFrame();
                string file = Path.Combine(output, $"{name}-{(generated ? "flow" : "soft")}.png");
                frame?.Save(file, PngBitmapEncoderOptions.Default);
                Console.WriteLine($"saved {file}");
            }
        }
        var pixels = new byte[ArtCache.PaletteSize * ArtCache.PaletteSize * 4];
        new Random(3).NextBytes(pixels);
        for (int i = 0; i < 20; i++)
            Fermata.Imaging.CoverAnalysis.Analyze(pixels, ArtCache.PaletteSize);
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++)
            Fermata.Imaging.CoverAnalysis.Analyze(pixels, ArtCache.PaletteSize);
        Console.WriteLine($"analysis of a {ArtCache.PaletteSize}-pixel cover of noise: {clock.Elapsed.TotalMilliseconds / 200:0.00} ms");
        if (AmbientBackdrop.ShaderErrors is { } errors)
        {
            Console.Error.WriteLine("ambient backdrop shader failed to compile:\n" + errors);
            return 1;
        }
        return 0;
    }

    private static void Pump(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.SystemIdle);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(10);
        }
    }
}
