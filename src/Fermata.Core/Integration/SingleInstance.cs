using System.Net.Sockets;
using System.Text;

namespace Fermata.Integration;

/// <summary>
/// Keeps one Fermata per user. The first instance listens on a Unix socket; later launches send it
/// their command line (files to play, or a command such as "next") and exit.
/// </summary>
/// <remarks>Messages are lines of UTF-8 text; the connection's end marks the end of a message.</remarks>
public sealed class SingleInstance : IDisposable
{
    private readonly Socket listener;
    private readonly string path;
    private readonly CancellationTokenSource stopping = new();

    private SingleInstance(Socket listener, string path)
    {
        this.listener = listener;
        this.path = path;
    }

    /// <summary>Raised on a background thread with the lines another launch sent.</summary>
    public event Action<IReadOnlyList<string>>? MessageReceived;

    /// <summary>
    /// Becomes the primary instance, or hands <paramref name="lines"/> to the running one. Returns null
    /// when the message was delivered to another instance (which should then be left to handle it).
    /// </summary>
    public static SingleInstance? Acquire(string socketPath, IReadOnlyList<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (TrySend(socketPath, lines))
                return null;
            // Nobody answered: any socket file left behind belongs to an instance that has exited.
            try
            {
                File.Delete(socketPath);
            }
            catch (IOException)
            {
            }
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                listener.Bind(new UnixDomainSocketEndPoint(socketPath));
                listener.Listen(8);
                File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var instance = new SingleInstance(listener, socketPath);
                instance.Listen();
                return instance;
            }
            catch (SocketException)
            {
                // Another instance bound the socket between our attempts; send to it instead.
                listener.Dispose();
                Thread.Sleep(50);
            }
        }
        throw new IOException($"Could not listen on {socketPath}.");
    }

    /// <summary>Sends lines to a running instance; false if none is listening.</summary>
    public static bool TrySend(string socketPath, IReadOnlyList<string> lines)
    {
        if (!File.Exists(socketPath))
            return false;
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            byte[] payload = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
            socket.Send(payload);
            socket.Shutdown(SocketShutdown.Send);
            // Wait for the acknowledgement so the sender does not exit before the message is read.
            var ack = new byte[1];
            socket.ReceiveTimeout = 2000;
            socket.Receive(ack);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private void Listen()
    {
        var thread = new Thread(() =>
        {
            while (!stopping.IsCancellationRequested)
            {
                Socket client;
                try
                {
                    client = listener.Accept();
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                using (client)
                {
                    try
                    {
                        client.ReceiveTimeout = 2000;
                        using var stream = new NetworkStream(client, ownsSocket: false);
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        var lines = new List<string>();
                        while (reader.ReadLine() is { } line && lines.Count < 10_000)
                        {
                            if (line.Length > 0)
                                lines.Add(line);
                        }
                        client.Send([1]);
                        if (lines.Count > 0)
                            MessageReceived?.Invoke(lines);
                    }
                    catch (IOException)
                    {
                    }
                    catch (SocketException)
                    {
                    }
                }
            }
        })
        {
            IsBackground = true,
            Name = "Fermata single instance",
        };
        thread.Start();
    }

    public void Dispose()
    {
        stopping.Cancel();
        listener.Dispose();
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
