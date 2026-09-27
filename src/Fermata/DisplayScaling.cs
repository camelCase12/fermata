using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

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
            factors = Factors(WaylandOutputs.LogicalWidths(), X11Monitors.Widths());
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

    /// <summary>A minimal Wayland client that reads each output's name and logical width.</summary>
    private static class WaylandOutputs
    {
        private const uint Display = 1, Registry = 2;

        public static Dictionary<string, int> LogicalWidths()
        {
            string socketName = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")!;
            string path = Path.IsPathRooted(socketName)
                ? socketName
                : Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "", socketName);
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            {
                ReceiveTimeout = 1000,
                SendTimeout = 1000,
            };
            socket.Connect(new UnixDomainSocketEndPoint(path));
            var client = new Client(socket);

            // wl_display.get_registry, then a round trip to receive every global.
            client.Send(Display, 1, w => w.NewId(Registry));
            var globals = new List<(uint Name, string Interface, uint Version)>();
            client.RoundTrip((sender, opcode, args) =>
            {
                if (sender == Registry && opcode == 0)
                    globals.Add((args.Uint(), args.String(), args.Uint()));
            });

            var manager = globals.FirstOrDefault(g => g.Interface == "zxdg_output_manager_v1");
            if (manager.Interface is null || manager.Version < 2)
                return [];
            uint managerId = client.NextId();
            client.Bind(manager.Name, manager.Interface, Math.Min(manager.Version, 3), managerId);
            var widths = new Dictionary<uint, int>();
            var names = new Dictionary<uint, string>();
            foreach (var output in globals.Where(g => g.Interface == "wl_output"))
            {
                uint outputId = client.NextId();
                client.Bind(output.Name, "wl_output", 1, outputId);
                uint xdgOutputId = client.NextId();
                // zxdg_output_manager_v1.get_xdg_output
                client.Send(managerId, 1, w => w.NewId(xdgOutputId).Object(outputId));
                widths[xdgOutputId] = 0;
            }
            client.RoundTrip((sender, opcode, args) =>
            {
                if (!widths.ContainsKey(sender))
                    return;
                if (opcode == 1)
                    widths[sender] = args.Int();
                else if (opcode == 3)
                    names[sender] = args.String();
            });
            return names.Where(n => widths[n.Key] > 0).ToDictionary(n => n.Value, n => widths[n.Key]);
        }

        /// <summary>The wire protocol: requests out, events in, and new object ids.</summary>
        private sealed class Client(Socket socket)
        {
            private uint nextId = Registry + 1;
            private readonly byte[] header = new byte[8];

            public uint NextId() => nextId++;

            public void Send(uint target, ushort opcode, Action<Writer> write)
            {
                var writer = new Writer();
                write(writer);
                byte[] body = writer.ToArray();
                byte[] message = new byte[8 + body.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(message, target);
                BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4), (uint)message.Length << 16 | opcode);
                body.CopyTo(message, 8);
                socket.Send(message);
            }

            /// <summary>wl_registry.bind, whose new object carries its interface and version.</summary>
            public void Bind(uint name, string @interface, uint version, uint id) =>
                Send(Registry, 0, w => w.Uint(name).String(@interface).Uint(version).NewId(id));

            /// <summary>Sends wl_display.sync and hands every event to <paramref name="handle"/> until it completes.</summary>
            public void RoundTrip(Action<uint, ushort, Reader> handle)
            {
                uint callback = NextId();
                Send(Display, 0, w => w.NewId(callback));
                while (true)
                {
                    Receive(header);
                    uint sender = BinaryPrimitives.ReadUInt32LittleEndian(header);
                    uint sizeAndOpcode = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                    int size = (int)(sizeAndOpcode >> 16);
                    if (size < 8)
                        throw new InvalidDataException("A Wayland event is shorter than its header.");
                    byte[] body = new byte[size - 8];
                    Receive(body);
                    ushort opcode = (ushort)sizeAndOpcode;
                    if (sender == callback)
                        return;
                    if (sender == Display && opcode == 0)
                        throw new InvalidDataException("The compositor reported a protocol error.");
                    handle(sender, opcode, new Reader(body));
                }
            }

            private void Receive(byte[] buffer)
            {
                for (int read = 0; read < buffer.Length;)
                {
                    int count = socket.Receive(buffer, read, buffer.Length - read, SocketFlags.None);
                    if (count == 0)
                        throw new IOException("The compositor closed the connection.");
                    read += count;
                }
            }
        }

        private sealed class Writer
        {
            private readonly List<byte> bytes = [];

            public Writer Uint(uint value)
            {
                Span<byte> word = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(word, value);
                bytes.AddRange(word);
                return this;
            }

            public Writer NewId(uint id) => Uint(id);

            public Writer Object(uint id) => Uint(id);

            /// <summary>A string is its length with the terminating NUL, then the bytes padded to a whole word.</summary>
            public Writer String(string value)
            {
                byte[] text = Encoding.UTF8.GetBytes(value + "\0");
                Uint((uint)text.Length);
                bytes.AddRange(text);
                while (bytes.Count % 4 != 0)
                    bytes.Add(0);
                return this;
            }

            public byte[] ToArray() => [.. bytes];
        }

        private sealed class Reader(byte[] body)
        {
            private int position;

            public uint Uint()
            {
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(position));
                position += 4;
                return value;
            }

            public int Int() => (int)Uint();

            public string String()
            {
                int length = (int)Uint();
                string value = length > 0 ? Encoding.UTF8.GetString(body, position, length - 1) : "";
                position += (length + 3) & ~3;
                return value;
            }
        }
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
