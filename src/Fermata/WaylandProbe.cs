using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Fermata;

/// <summary>A minimal Wayland client that reads the compositor's globals and outputs.</summary>
/// <remarks>It runs before Avalonia starts, and connects to the compositor that <c>WAYLAND_DISPLAY</c> names.</remarks>
internal static class WaylandProbe
{
    private const uint Display = 1, Registry = 2;

    /// <summary>Whether the compositor offers the version of <c>xdg_wm_base</c> that Avalonia's Wayland backend needs.</summary>
    public static bool SupportsAvalonia()
    {
        using var socket = Connect();
        var (_, globals) = ReadGlobals(socket);
        return globals.Any(g => g.Interface == "xdg_wm_base" && g.Version >= 3);
    }

    /// <summary>The logical width of each output, by output name.</summary>
    public static Dictionary<string, int> LogicalWidths()
    {
        using var socket = Connect();
        var (client, globals) = ReadGlobals(socket);

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

    private static Socket Connect()
    {
        string socketName = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")!;
        string path = Path.IsPathRooted(socketName)
            ? socketName
            : Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "", socketName);
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
        {
            ReceiveTimeout = 1000,
            SendTimeout = 1000,
        };
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(path));
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return socket;
    }

    /// <summary>Sends wl_display.get_registry and reads every global in one round trip.</summary>
    private static (Client Client, List<(uint Name, string Interface, uint Version)> Globals) ReadGlobals(Socket socket)
    {
        var client = new Client(socket);
        client.Send(Display, 1, w => w.NewId(Registry));
        var globals = new List<(uint Name, string Interface, uint Version)>();
        client.RoundTrip((sender, opcode, args) =>
        {
            if (sender == Registry && opcode == 0)
                globals.Add((args.Uint(), args.String(), args.Uint()));
        });
        return (client, globals);
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
