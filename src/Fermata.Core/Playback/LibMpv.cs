using System.Runtime.InteropServices;

namespace Fermata.Playback;

/// <summary>The subset of the libmpv client API (mpv/client.h) that Fermata uses.</summary>
internal static unsafe partial class LibMpv
{
    private const string Library = "libmpv.so.2";

    /// <summary>The older library name, from mpv 0.34 and earlier, which offers the same calls.</summary>
    private const string OlderLibrary = "libmpv.so.1";

    static LibMpv()
    {
        NativeLibrary.SetDllImportResolver(typeof(LibMpv).Assembly, (name, assembly, paths) =>
        {
            if (name != Library)
                return 0;
            if (NativeLibrary.TryLoad(Library, assembly, paths, out nint handle)
                || NativeLibrary.TryLoad(OlderLibrary, assembly, paths, out handle))
                return handle;
            return 0;
        });
    }

    public enum Format
    {
        None = 0,
        String = 1,
        Flag = 3,
        Int64 = 4,
        Double = 5,
        Node = 6,
        NodeArray = 7,
        NodeMap = 8,
    }

    public enum EventId
    {
        None = 0,
        Shutdown = 1,
        LogMessage = 2,
        CommandReply = 5,
        StartFile = 6,
        EndFile = 7,
        FileLoaded = 8,
        Idle = 11,
        AudioReconfig = 18,
        Seek = 20,
        PlaybackRestart = 21,
        PropertyChange = 22,
    }

    public enum EndFileReason
    {
        Eof = 0,
        Stop = 2,
        Quit = 3,
        Error = 4,
        Redirect = 5,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Event
    {
        public EventId Id;
        public int Error;
        public ulong ReplyUserData;
        public void* Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventProperty
    {
        public byte* Name;
        public Format Format;
        public void* Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventStartFile
    {
        public long PlaylistEntryId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventEndFile
    {
        public EndFileReason Reason;
        public int Error;
        public long PlaylistEntryId;
        public long PlaylistInsertId;
        public int PlaylistInsertNumEntries;
    }

    /// <summary>A value of any type, read according to <see cref="Format"/>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Node
    {
        public nint Value; // char*, int flag, int64, double bits, or NodeList*
        public Format Format;

        public readonly string? AsString() => Format == Format.String ? Marshal.PtrToStringUTF8(Value) : null;
        public readonly long AsInt64() => Format == Format.Int64 ? Value : 0;
        public readonly NodeList* AsList() => Format is Format.NodeArray or Format.NodeMap ? (NodeList*)Value : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NodeList
    {
        public int Count;
        public Node* Values;
        public byte** Keys;

        public readonly Node* Find(string key)
        {
            if (Keys == null)
                return null;
            for (int i = 0; i < Count; i++)
            {
                if (Marshal.PtrToStringUTF8((nint)Keys[i]) == key)
                    return &Values[i];
            }
            return null;
        }
    }

    [LibraryImport(Library, EntryPoint = "mpv_client_api_version")]
    public static partial uint ClientApiVersion();

    [LibraryImport(Library, EntryPoint = "mpv_create")]
    public static partial nint Create();

    [LibraryImport(Library, EntryPoint = "mpv_initialize")]
    public static partial int Initialize(nint handle);

    [LibraryImport(Library, EntryPoint = "mpv_terminate_destroy")]
    public static partial void TerminateDestroy(nint handle);

    [LibraryImport(Library, EntryPoint = "mpv_set_option_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetOptionString(nint handle, string name, string value);

    [LibraryImport(Library, EntryPoint = "mpv_set_property", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetProperty(nint handle, string name, Format format, void* data);

    [LibraryImport(Library, EntryPoint = "mpv_set_property_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetPropertyString(nint handle, string name, string value);

    [LibraryImport(Library, EntryPoint = "mpv_get_property", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int GetProperty(nint handle, string name, Format format, void* data);

    [LibraryImport(Library, EntryPoint = "mpv_observe_property", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int ObserveProperty(nint handle, ulong replyUserData, string name, Format format);

    [LibraryImport(Library, EntryPoint = "mpv_command")]
    public static partial int Command(nint handle, byte** args);

    [LibraryImport(Library, EntryPoint = "mpv_command_ret")]
    public static partial int CommandRet(nint handle, byte** args, Node* result);

    [LibraryImport(Library, EntryPoint = "mpv_free_node_contents")]
    public static partial void FreeNodeContents(Node* node);

    [LibraryImport(Library, EntryPoint = "mpv_wait_event")]
    public static partial Event* WaitEvent(nint handle, double timeout);

    [LibraryImport(Library, EntryPoint = "mpv_set_wakeup_callback")]
    public static partial void SetWakeupCallback(nint handle, delegate* unmanaged[Cdecl]<nint, void> callback, nint data);

    [LibraryImport(Library, EntryPoint = "mpv_error_string")]
    public static partial byte* ErrorString(int error);

    public static string Describe(int error) => Marshal.PtrToStringUTF8((nint)ErrorString(error)) ?? $"error {error}";

    /// <summary>Runs a command given as strings.</summary>
    /// <returns>The result node, which the caller frees, and the status.</returns>
    public static int Run(nint handle, ReadOnlySpan<string> args, Node* result = null)
    {
        // Marshal the NUL-terminated argument vector on the stack for short arguments.
        int total = 0;
        foreach (string arg in args)
            total += System.Text.Encoding.UTF8.GetMaxByteCount(arg.Length) + 1;
        byte[]? rented = total > 4096 ? System.Buffers.ArrayPool<byte>.Shared.Rent(total) : null;
        Span<byte> buffer = rented ?? stackalloc byte[total];
        byte** argv = stackalloc byte*[args.Length + 1];
        try
        {
            fixed (byte* start = buffer)
            {
                int offset = 0;
                for (int i = 0; i < args.Length; i++)
                {
                    argv[i] = start + offset;
                    offset += System.Text.Encoding.UTF8.GetBytes(args[i], buffer[offset..]);
                    buffer[offset++] = 0;
                }
                argv[args.Length] = null;
                return result == null ? Command(handle, argv) : CommandRet(handle, argv, result);
            }
        }
        finally
        {
            if (rented is not null)
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
