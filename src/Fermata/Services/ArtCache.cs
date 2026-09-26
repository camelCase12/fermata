using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Fermata.Imaging;
using Fermata.Library;
using Fermata.Metadata;
using SkiaSharp;

namespace Fermata.Services;

/// <summary>A cache of cover art decoded at the size it is shown.</summary>
/// <remarks>
/// Images are handed out as <see cref="ArtLease"/>s. An image stays in memory while any lease is held,
/// and released images are kept, least recently used first, up to a byte budget. Sizes are rounded up
/// to a few buckets. The cache is used from the UI thread only, and decoding runs on background workers.
/// </remarks>
public sealed class ArtCache
{
    private const long UnusedBudgetBytes = 48L * 1024 * 1024;
    private static readonly int[] Buckets = [48, 96, 160, 240, 320, 480, 640, 960, 1280];

    private readonly Dictionary<Key, Entry> entries = [];
    private readonly LinkedList<Entry> unused = [];
    private readonly Dictionary<ArtSource, byte[]?> colorGrids = [];
    private readonly Dictionary<ArtSource, CoverPalette?> palettes = [];
    private readonly SemaphoreSlim decoders = new(3);
    private long unusedBytes;

    private readonly record struct Key(ArtSource Source, int Size);

    private sealed class Entry(Key key)
    {
        public readonly Key Key = key;
        public Bitmap? Bitmap;
        public bool Failed;
        public int Leases;
        public List<Action<ArtLease?>>? Waiting;
        public LinkedListNode<Entry>? UnusedNode;
        public long Bytes;
    }

    /// <summary>Rounds a physical pixel size up to a decode bucket.</summary>
    public static int BucketFor(double pixels)
    {
        foreach (int bucket in Buckets)
        {
            if (bucket >= pixels)
                return bucket;
        }
        return Buckets[^1];
    }

    /// <summary>Returns a lease at once if the image is already decoded.</summary>
    public ArtLease? TryGet(ArtSource source, int size)
    {
        if (entries.TryGetValue(new Key(source, size), out var entry) && entry.Bitmap is not null)
            return Lease(entry);
        return null;
    }

    /// <summary>Requests a lease on an image.</summary>
    /// <remarks>
    /// The lease is delivered to <paramref name="done"/> on the UI thread, and is null if the image cannot
    /// be read. The receiver must dispose it when it no longer shows the image.
    /// </remarks>
    public void Request(ArtSource source, int size, Action<ArtLease?> done)
    {
        var key = new Key(source, size);
        if (entries.TryGetValue(key, out var entry))
        {
            if (entry.Bitmap is not null)
                done(Lease(entry));
            else if (entry.Failed)
                done(null);
            else
                entry.Waiting!.Add(done);
            return;
        }
        entry = new Entry(key) { Waiting = [done] };
        entries[key] = entry;
        _ = DecodeAsync(entry);
    }

    private async Task DecodeAsync(Entry entry)
    {
        Bitmap? bitmap = null;
        await decoders.WaitAsync().ConfigureAwait(false);
        try
        {
            bitmap = await Task.Run(() => Decode(entry.Key.Source, entry.Key.Size)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            // Unreadable or undecodable images simply show the placeholder.
        }
        finally
        {
            decoders.Release();
        }
        Dispatcher.UIThread.Post(() => Complete(entry, bitmap));
    }

    private void Complete(Entry entry, Bitmap? bitmap)
    {
        var waiting = entry.Waiting!;
        entry.Waiting = null;
        if (bitmap is null)
        {
            entry.Failed = true;
            foreach (var done in waiting)
                done(null);
            return;
        }
        entry.Bitmap = bitmap;
        entry.Bytes = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;
        if (waiting.Count == 0)
            MakeUnused(entry);
        foreach (var done in waiting)
            done(Lease(entry));
    }

    private ArtLease Lease(Entry entry)
    {
        if (entry.UnusedNode is not null)
        {
            unused.Remove(entry.UnusedNode);
            entry.UnusedNode = null;
            unusedBytes -= entry.Bytes;
        }
        entry.Leases++;
        return new ArtLease(this, entry.Bitmap!, entry);
    }

    internal void Release(object token)
    {
        var entry = (Entry)token;
        if (--entry.Leases == 0)
            MakeUnused(entry);
    }

    private void MakeUnused(Entry entry)
    {
        entry.UnusedNode = unused.AddLast(entry);
        unusedBytes += entry.Bytes;
        while (unusedBytes > UnusedBudgetBytes && unused.First is { } oldest)
        {
            var evicted = oldest.Value;
            unused.RemoveFirst();
            evicted.UnusedNode = null;
            unusedBytes -= evicted.Bytes;
            entries.Remove(evicted.Key);
            evicted.Bitmap?.Dispose();
        }
    }

    private static Bitmap? Decode(ArtSource source, int size)
    {
        byte[]? bytes;
        if (source.IsImageFile)
            bytes = File.ReadAllBytes(source.Path);
        else if (source.Offset >= 0)
        {
            using var file = File.OpenHandle(source.Path);
            bytes = new byte[source.Length];
            if (RandomAccess.Read(file, bytes, source.Offset) != bytes.Length)
                return null;
        }
        else
        {
            bytes = TagReader.ReadPicture(source.Path);
        }
        if (bytes is null || bytes.Length == 0)
            return null;
        // Covers are cropped to a square, so the shorter side is decoded at the display size.
        using var stream = new MemoryStream(bytes, writable: false);
        return IsWide(bytes)
            ? Bitmap.DecodeToHeight(stream, size, BitmapInterpolationMode.MediumQuality)
            : Bitmap.DecodeToWidth(stream, size, BitmapInterpolationMode.MediumQuality);
    }

    /// <summary>Reads just the image header.</summary>
    private static bool IsWide(byte[] bytes)
    {
        using var codec = SKCodec.Create(new MemoryStream(bytes, writable: false));
        return codec is not null && codec.Info.Width > codec.Info.Height;
    }

    /// <summary>Gets a grid of a cover's average colours.</summary>
    /// <remarks>
    /// The grid is <see cref="ColorGridSize"/> cells square, as row-major RGBA bytes, over the cover's
    /// centred square. It is delivered on the UI thread, and is null when the cover cannot be read.
    /// </remarks>
    public void RequestColorGrid(ArtSource source, Action<byte[]?> done)
    {
        if (colorGrids.TryGetValue(source, out var known))
        {
            done(known);
            return;
        }
        Request(source, 48, lease =>
        {
            byte[]? grid = null;
            if (lease is not null)
            {
                grid = ColorGrid(Square(lease.Bitmap, out int side), side);
                lease.Dispose();
            }
            colorGrids[source] = grid;
            done(grid);
        });
    }

    /// <summary>Gets the palette of a cover.</summary>
    /// <remarks>It is delivered on the UI thread, and is null when the cover cannot be read.</remarks>
    public void RequestPalette(ArtSource source, Action<CoverPalette?> done)
    {
        if (palettes.TryGetValue(source, out var known))
        {
            done(known);
            return;
        }
        Request(source, PaletteSize, lease =>
        {
            CoverPalette? palette = null;
            if (lease is not null)
            {
                palette = CoverAnalysis.Analyze(Square(lease.Bitmap, out int side), side);
                lease.Dispose();
            }
            palettes[source] = palette;
            done(palette);
        });
    }

    public const int ColorGridSize = 8;
    public const int PaletteSize = 48;

    private static readonly float[] ToLinear = Enumerable.Range(0, 256).Select(v =>
    {
        double c = v / 255.0;
        return (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
    }).ToArray();

    private static byte ToSrgb(double linear)
    {
        double c = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        return (byte)Math.Clamp(Math.Round(c * 255), 0, 255);
    }

    /// <summary>Copies the centred square of a bitmap out as row-major RGBA bytes.</summary>
    private static byte[] Square(Bitmap bitmap, out int side)
    {
        var size = bitmap.PixelSize;
        var pixels = new byte[size.Width * size.Height * 4];
        unsafe
        {
            fixed (byte* buffer = pixels)
                bitmap.CopyPixels(new Avalonia.PixelRect(size), (nint)buffer, pixels.Length, size.Width * 4);
        }
        // Decoded bitmaps are BGRA unless the platform says otherwise.
        bool rgba = bitmap.Format == Avalonia.Platform.PixelFormat.Rgba8888;
        int red = rgba ? 0 : 2, blue = rgba ? 2 : 0;
        side = Math.Min(size.Width, size.Height);
        int left = (size.Width - side) / 2, top = (size.Height - side) / 2;
        var square = new byte[side * side * 4];
        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                int i = ((top + y) * size.Width + left + x) * 4, o = (y * side + x) * 4;
                square[o] = pixels[i + red];
                square[o + 1] = pixels[i + 1];
                square[o + 2] = pixels[i + blue];
                square[o + 3] = 255;
            }
        }
        return square;
    }

    private static byte[] ColorGrid(byte[] square, int side)
    {
        const int n = ColorGridSize;
        var grid = new byte[n * n * 4];
        for (int cy = 0; cy < n; cy++)
        {
            for (int cx = 0; cx < n; cx++)
            {
                double r = 0, g = 0, b = 0;
                int count = 0;
                for (int y = cy * side / n; y < (cy + 1) * side / n; y++)
                {
                    for (int x = cx * side / n; x < (cx + 1) * side / n; x++)
                    {
                        int i = (y * side + x) * 4;
                        r += ToLinear[square[i]];
                        g += ToLinear[square[i + 1]];
                        b += ToLinear[square[i + 2]];
                        count++;
                    }
                }
                int o = (cy * n + cx) * 4;
                count = Math.Max(count, 1);
                grid[o] = ToSrgb(r / count);
                grid[o + 1] = ToSrgb(g / count);
                grid[o + 2] = ToSrgb(b / count);
                grid[o + 3] = 255;
            }
        }
        return grid;
    }
}


/// <summary>A decoded cover in use.</summary>
/// <remarks>Dispose it when the image is no longer shown.</remarks>
public sealed class ArtLease : IDisposable
{
    private ArtCache? cache;
    private readonly object token;

    internal ArtLease(ArtCache cache, Bitmap bitmap, object token)
    {
        this.cache = cache;
        this.token = token;
        Bitmap = bitmap;
    }

    public Bitmap Bitmap { get; }

    public void Dispose()
    {
        cache?.Release(token);
        cache = null;
    }
}
