using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Fermata;

/// <summary>Per-monitor scale factors for running under XWayland.</summary>
/// <remarks>
/// A compositor can leave scaling of X11 windows to the application, as Hyprland does with
/// <c>force_zero_scaling</c>. Avalonia's X11 backend then draws at 1× unless it is given factors, so
/// on a monitor scaled to 1.5 the window comes out smaller than everything else. The factor for each
/// monitor is its width in X11 pixels over its logical width on the Wayland side, which is 1 when the
/// compositor already scales X11 windows itself.
/// </remarks>
internal static partial class DisplayScaling
{
    private const string FactorsVariable = "AVALONIA_SCREEN_SCALE_FACTORS";

    /// <summary>Sets Avalonia's per-monitor scale factors unless the environment already chooses a scale.</summary>
    /// <returns>The factors set, or null when none were needed or they could not be found.</returns>
    public static string? Apply()
    {
        string[] chosen = [FactorsVariable, "AVALONIA_GLOBAL_SCALE_FACTOR", "QT_SCREEN_SCALE_FACTORS", "QT_SCALE_FACTOR"];
        if (chosen.Any(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            return null;
        string? factors;
        try
        {
            factors = Factors(WaylandProbe.LogicalWidths(), X11Monitors.Widths());
        }
        catch (Exception error) when (error is SocketException or IOException or InvalidDataException
            or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        if (factors is not null)
            Environment.SetEnvironmentVariable(FactorsVariable, factors);
        return factors;
    }

    /// <summary>The factors in Avalonia's <c>NAME=FACTOR;…</c> form, or null when every monitor is at 1×.</summary>
    internal static string? Factors(IReadOnlyDictionary<string, int> logicalWidths, IReadOnlyDictionary<string, int> pixelWidths)
    {
        var parts = new List<string>();
        bool scaled = false;
        foreach (var (name, pixels) in pixelWidths)
        {
            if (!logicalWidths.TryGetValue(name, out int logical) || logical <= 0 || pixels <= 0)
                continue;
            double factor = Math.Clamp(Math.Round((double)pixels / logical, 2), 1, 4);
            scaled |= factor != 1;
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{name}={factor}"));
        }
        return scaled ? string.Join(';', parts) : null;
    }

    /// <summary>The X server's monitors and their widths in pixels, from RandR.</summary>
    private static partial class X11Monitors
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public nuint Name;
            public int Primary, Automatic, OutputCount, X, Y, Width, Height, WidthMm, HeightMm;
            public nint Outputs;
        }

        public static unsafe Dictionary<string, int> Widths()
        {
            var widths = new Dictionary<string, int>();
            nint display = XOpenDisplay(0);
            if (display == 0)
                return widths;
            try
            {
                var monitors = (MonitorInfo*)XRRGetMonitors(display, XDefaultRootWindow(display), 1, out int count);
                if (monitors is null)
                    return widths;
                for (int i = 0; i < count; i++)
                {
                    nint name = XGetAtomName(display, monitors[i].Name);
                    if (name == 0)
                        continue;
                    widths[Marshal.PtrToStringUTF8(name) ?? ""] = monitors[i].Width;
                    XFree(name);
                }
                XRRFreeMonitors((nint)monitors);
            }
            finally
            {
                XCloseDisplay(display);
            }
            return widths;
        }

        [LibraryImport("libX11.so.6")]
        private static partial nint XOpenDisplay(nint name);

        [LibraryImport("libX11.so.6")]
        private static partial int XCloseDisplay(nint display);

        [LibraryImport("libX11.so.6")]
        private static partial nuint XDefaultRootWindow(nint display);

        [LibraryImport("libX11.so.6")]
        private static partial nint XGetAtomName(nint display, nuint atom);

        [LibraryImport("libX11.so.6")]
        private static partial int XFree(nint data);

        [LibraryImport("libXrandr.so.2")]
        private static partial nint XRRGetMonitors(nint display, nuint window, int getActive, out int count);

        [LibraryImport("libXrandr.so.2")]
        private static partial void XRRFreeMonitors(nint monitors);
    }
}
