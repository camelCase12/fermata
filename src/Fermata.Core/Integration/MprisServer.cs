using System.Diagnostics;
using System.Text;
using Tmds.DBus.Protocol;

namespace Fermata.Integration;

/// <summary>Everything MPRIS clients can read, captured on the UI thread.</summary>
public sealed record MprisState
{
    public string PlaybackStatus { get; init; } = "Stopped";
    public string LoopStatus { get; init; } = "None";
    public bool Shuffle { get; init; }
    public double Volume { get; init; } = 1;
    public MprisMetadata? Metadata { get; init; }
    public bool CanGoNext { get; init; }
    public bool CanGoPrevious { get; init; }
    public bool CanPlay { get; init; }
    public bool CanSeek { get; init; }

    /// <summary>Position when captured; while playing, readers extrapolate from <see cref="CapturedAt"/>.</summary>
    public TimeSpan Position { get; init; }

    public long CapturedAt { get; init; } = Stopwatch.GetTimestamp();

    public TimeSpan CurrentPosition()
    {
        if (PlaybackStatus != "Playing")
            return Position;
        var position = Position + Stopwatch.GetElapsedTime(CapturedAt);
        return Metadata is { Length: var length } && length > TimeSpan.Zero && position > length ? length : position;
    }
}

public sealed record MprisMetadata(
    string TrackId,
    string Title,
    IReadOnlyList<string> Artists,
    string Album,
    IReadOnlyList<string> AlbumArtists,
    IReadOnlyList<string> Genres,
    TimeSpan Length,
    int TrackNumber,
    int DiscNumber,
    string Url,
    string? ArtUrl,
    int UseCount,
    double? Rating);

/// <summary>Commands from MPRIS clients, run on the UI thread.</summary>
public interface IMprisTarget
{
    void Raise();
    void Quit();
    void Play();
    void Pause();
    void PlayPause();
    void Stop();
    void Next();
    void Previous();

    /// <summary>Seeks relative to the current position.</summary>
    void SeekBy(TimeSpan offset);

    /// <summary>Seeks to an absolute position if <paramref name="trackId"/> is still current.</summary>
    void SetPosition(string trackId, TimeSpan position);

    void SetShuffle(bool shuffle);
    void SetLoopStatus(string status);
    void SetVolume(double volume);
    void OpenUri(string uri);
}

/// <summary>The MPRIS server that publishes the player on the session bus.</summary>
/// <remarks>
/// Specification: https://specifications.freedesktop.org/mpris-spec/latest/. The bus name is
/// <c>org.mpris.MediaPlayer2.fermata</c>. Property reads are served from an immutable
/// <see cref="MprisState"/> on the bus thread, and commands are posted to the UI thread.
/// </remarks>
public sealed class MprisServer : IDisposable
{
    public const string ObjectPath = "/org/mpris/MediaPlayer2";
    private const string RootInterface = "org.mpris.MediaPlayer2";
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    private readonly IMprisTarget target;
    private readonly SynchronizationContext context;
    private readonly string identity;
    private readonly string desktopEntry;
    private DBusConnection? connection;
    private volatile MprisState state = new();

    public MprisServer(IMprisTarget target, SynchronizationContext context, string identity = "Fermata", string desktopEntry = DesktopEntry.Id)
    {
        this.target = target;
        this.context = context;
        this.identity = identity;
        this.desktopEntry = desktopEntry;
    }

    /// <summary>The bus name acquired, once connected.</summary>
    public string? BusName { get; private set; }

    /// <summary>Connects and claims the bus name.</summary>
    /// <returns>False when no session bus is available, in which case the server does nothing.</returns>
    public async Task<bool> StartAsync()
    {
        string? address = DBusAddress.Session;
        if (string.IsNullOrEmpty(address))
            return false;
        try
        {
            connection = new DBusConnection(address);
            await connection.ConnectAsync().ConfigureAwait(false);
            connection.AddMethodHandler(new Handler(this));
            string name = "org.mpris.MediaPlayer2.fermata";
            // A second instance registers under an instance-specific name.
            if (!await connection.TryRequestNameAsync(name, RequestNameOptions.None).ConfigureAwait(false))
            {
                name += ".instance" + Environment.ProcessId;
                await connection.RequestNameAsync(name, RequestNameOptions.None).ConfigureAwait(false);
            }
            BusName = name;
            return true;
        }
        catch (Exception)
        {
            // Without a usable session bus the player is not published.
            connection?.Dispose();
            connection = null;
            return false;
        }
    }

    /// <summary>Publishes a new state, announcing changed properties to clients.</summary>
    public void Update(MprisState next)
    {
        var previous = state;
        state = next;
        if (connection is null)
            return;
        var changed = new Dictionary<string, VariantValue>();
        if (next.PlaybackStatus != previous.PlaybackStatus)
            changed["PlaybackStatus"] = next.PlaybackStatus;
        if (next.LoopStatus != previous.LoopStatus)
            changed["LoopStatus"] = next.LoopStatus;
        if (next.Shuffle != previous.Shuffle)
            changed["Shuffle"] = next.Shuffle;
        if (Math.Abs(next.Volume - previous.Volume) > 0.0001)
            changed["Volume"] = next.Volume;
        if (next.Metadata != previous.Metadata)
            changed["Metadata"] = MetadataValue(next.Metadata);
        if (next.CanGoNext != previous.CanGoNext)
            changed["CanGoNext"] = next.CanGoNext;
        if (next.CanGoPrevious != previous.CanGoPrevious)
            changed["CanGoPrevious"] = next.CanGoPrevious;
        if (next.CanPlay != previous.CanPlay)
        {
            changed["CanPlay"] = next.CanPlay;
            changed["CanPause"] = next.CanPlay;
        }
        if (next.CanSeek != previous.CanSeek)
            changed["CanSeek"] = next.CanSeek;
        if (changed.Count == 0)
            return;
        using var writer = connection.GetMessageWriter();
        writer.WriteSignalHeader(null, ObjectPath, PropertiesInterface, "PropertiesChanged", "sa{sv}as");
        writer.WriteString(PlayerInterface);
        writer.WriteDictionary(changed);
        writer.WriteArray(Array.Empty<string>());
        connection.TrySendMessage(writer.CreateMessage());
    }

    /// <summary>Tells clients that the position jumped.</summary>
    /// <remarks>Position changes are not otherwise announced.</remarks>
    public void EmitSeeked(TimeSpan position)
    {
        if (connection is null)
            return;
        using var writer = connection.GetMessageWriter();
        writer.WriteSignalHeader(null, ObjectPath, PlayerInterface, "Seeked", "x");
        writer.WriteInt64(Microseconds(position));
        connection.TrySendMessage(writer.CreateMessage());
    }

    private static long Microseconds(TimeSpan time) => time.Ticks / 10;

    private static VariantValue MetadataValue(MprisMetadata? metadata)
    {
        var dictionary = new Dict<string, VariantValue>();
        if (metadata is null)
        {
            dictionary.Add("mpris:trackid", VariantValue.ObjectPath("/org/mpris/MediaPlayer2/TrackList/NoTrack"));
            return dictionary;
        }
        dictionary.Add("mpris:trackid", VariantValue.ObjectPath(metadata.TrackId));
        dictionary.Add("mpris:length", Microseconds(metadata.Length));
        dictionary.Add("xesam:title", metadata.Title);
        dictionary.Add("xesam:artist", VariantValue.Array(metadata.Artists.ToArray()));
        dictionary.Add("xesam:album", metadata.Album);
        dictionary.Add("xesam:albumArtist", VariantValue.Array(metadata.AlbumArtists.ToArray()));
        dictionary.Add("xesam:genre", VariantValue.Array(metadata.Genres.ToArray()));
        dictionary.Add("xesam:url", metadata.Url);
        dictionary.Add("xesam:useCount", metadata.UseCount);
        if (metadata.TrackNumber > 0)
            dictionary.Add("xesam:trackNumber", metadata.TrackNumber);
        if (metadata.DiscNumber > 0)
            dictionary.Add("xesam:discNumber", metadata.DiscNumber);
        if (metadata.ArtUrl is not null)
            dictionary.Add("mpris:artUrl", metadata.ArtUrl);
        if (metadata.Rating is { } rating)
            dictionary.Add("xesam:userRating", rating);
        return dictionary;
    }

    private VariantValue? Property(string @interface, string name)
    {
        var current = state;
        if (@interface == RootInterface)
        {
            return name switch
            {
                "CanQuit" => true,
                "CanRaise" => true,
                "CanSetFullscreen" => false,
                "Fullscreen" => false,
                "HasTrackList" => false,
                "Identity" => identity,
                "DesktopEntry" => desktopEntry,
                "SupportedUriSchemes" => VariantValue.Array(new[] { "file" }),
                "SupportedMimeTypes" => VariantValue.Array(SupportedMimeTypes),
                _ => (VariantValue?)null,
            };
        }
        if (@interface != PlayerInterface)
            return null;
        return name switch
        {
            "PlaybackStatus" => current.PlaybackStatus,
            "LoopStatus" => current.LoopStatus,
            "Rate" => 1.0,
            "MinimumRate" => 1.0,
            "MaximumRate" => 1.0,
            "Shuffle" => current.Shuffle,
            "Metadata" => MetadataValue(current.Metadata),
            "Volume" => current.Volume,
            "Position" => Microseconds(current.CurrentPosition()),
            "CanGoNext" => current.CanGoNext,
            "CanGoPrevious" => current.CanGoPrevious,
            "CanPlay" => current.CanPlay,
            "CanPause" => current.CanPlay,
            "CanSeek" => current.CanSeek,
            "CanControl" => true,
            _ => (VariantValue?)null,
        };
    }

    private static readonly string[] RootProperties =
        ["CanQuit", "CanRaise", "CanSetFullscreen", "Fullscreen", "HasTrackList", "Identity", "DesktopEntry", "SupportedUriSchemes", "SupportedMimeTypes"];

    private static readonly string[] PlayerProperties =
        ["PlaybackStatus", "LoopStatus", "Rate", "MinimumRate", "MaximumRate", "Shuffle", "Metadata", "Volume", "Position",
         "CanGoNext", "CanGoPrevious", "CanPlay", "CanPause", "CanSeek", "CanControl"];

    private static readonly string[] SupportedMimeTypes =
        ["audio/mpeg", "audio/flac", "audio/ogg", "audio/opus", "audio/mp4", "audio/aac", "audio/x-wav", "audio/aiff",
         "audio/x-wavpack", "audio/x-ape", "audio/x-ms-wma", "audio/x-matroska", "audio/webm"];

    private void Post(Action action) => context.Post(static a => ((Action)a!)(), action);

    private sealed class Handler(MprisServer server) : IPathMethodHandler
    {
        public string Path => ObjectPath;
        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;
            string @interface = request.InterfaceAsString ?? "";
            string member = request.MemberAsString ?? "";
            if (context.IsDBusIntrospectRequest)
            {
                context.ReplyIntrospectXml([RootXml, PlayerXml], ReadOnlySpan<string>.Empty);
                return ValueTask.CompletedTask;
            }
            switch (@interface)
            {
                case PropertiesInterface:
                    HandleProperties(context, member);
                    break;
                case RootInterface:
                    if (member == "Raise")
                        server.Post(server.target.Raise);
                    else if (member == "Quit")
                        server.Post(server.target.Quit);
                    else
                    {
                        context.ReplyUnknownMethodError();
                        return ValueTask.CompletedTask;
                    }
                    ReplyEmpty(context);
                    break;
                case PlayerInterface:
                    HandlePlayer(context, member);
                    break;
                default:
                    context.ReplyUnknownMethodError();
                    break;
            }
            return ValueTask.CompletedTask;
        }

        private void HandlePlayer(MethodContext context, string member)
        {
            var target = server.target;
            switch (member)
            {
                case "Next": server.Post(target.Next); break;
                case "Previous": server.Post(target.Previous); break;
                case "Pause": server.Post(target.Pause); break;
                case "PlayPause": server.Post(target.PlayPause); break;
                case "Stop": server.Post(target.Stop); break;
                case "Play": server.Post(target.Play); break;
                case "Seek":
                {
                    var reader = context.Request.GetBodyReader();
                    var offset = TimeSpan.FromTicks(reader.ReadInt64() * 10);
                    server.Post(() => target.SeekBy(offset));
                    break;
                }
                case "SetPosition":
                {
                    var reader = context.Request.GetBodyReader();
                    string trackId = reader.ReadObjectPathAsString();
                    var position = TimeSpan.FromTicks(reader.ReadInt64() * 10);
                    server.Post(() => target.SetPosition(trackId, position));
                    break;
                }
                case "OpenUri":
                {
                    var reader = context.Request.GetBodyReader();
                    string uri = reader.ReadString();
                    server.Post(() => target.OpenUri(uri));
                    break;
                }
                default:
                    context.ReplyUnknownMethodError();
                    return;
            }
            ReplyEmpty(context);
        }

        private void HandleProperties(MethodContext context, string member)
        {
            var reader = context.Request.GetBodyReader();
            switch (member)
            {
                case "Get":
                {
                    string @interface = reader.ReadString();
                    string name = reader.ReadString();
                    if (server.Property(@interface, name) is not { } value)
                    {
                        context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", $"No property {@interface}.{name}");
                        return;
                    }
                    using var writer = context.CreateReplyWriter("v");
                    writer.WriteVariant(value);
                    context.Reply(writer.CreateMessage());
                    return;
                }
                case "GetAll":
                {
                    string @interface = reader.ReadString();
                    var names = @interface == RootInterface ? RootProperties : @interface == PlayerInterface ? PlayerProperties : [];
                    var values = new Dictionary<string, VariantValue>(names.Length);
                    foreach (string name in names)
                    {
                        if (server.Property(@interface, name) is { } value)
                            values[name] = value;
                    }
                    using var writer = context.CreateReplyWriter("a{sv}");
                    writer.WriteDictionary(values);
                    context.Reply(writer.CreateMessage());
                    return;
                }
                case "Set":
                {
                    string @interface = reader.ReadString();
                    string name = reader.ReadString();
                    var value = reader.ReadVariantValue();
                    var target = server.target;
                    switch (name)
                    {
                        case "Shuffle" when value.Type == VariantValueType.Bool:
                            bool shuffle = value.GetBool();
                            server.Post(() => target.SetShuffle(shuffle));
                            break;
                        case "LoopStatus" when value.Type == VariantValueType.String:
                            string loop = value.GetString();
                            server.Post(() => target.SetLoopStatus(loop));
                            break;
                        case "Volume" when value.Type == VariantValueType.Double:
                            double volume = Math.Clamp(value.GetDouble(), 0, 1);
                            server.Post(() => target.SetVolume(volume));
                            break;
                        case "Rate" or "Fullscreen":
                            break; // The specification says to ignore requests to change these.
                        default:
                            context.ReplyError("org.freedesktop.DBus.Error.PropertyReadOnly", $"{@interface}.{name} cannot be set");
                            return;
                    }
                    ReplyEmpty(context);
                    return;
                }
                default:
                    context.ReplyUnknownMethodError();
                    return;
            }
        }

        private static void ReplyEmpty(MethodContext context)
        {
            if (context.NoReplyExpected)
                return;
            using var writer = context.CreateReplyWriter(null!);
            context.Reply(writer.CreateMessage());
        }
    }

    private static readonly ReadOnlyMemory<byte> RootXml = Encoding.UTF8.GetBytes("""
        <interface name="org.mpris.MediaPlayer2">
          <method name="Raise"/>
          <method name="Quit"/>
          <property name="CanQuit" type="b" access="read"/>
          <property name="CanRaise" type="b" access="read"/>
          <property name="CanSetFullscreen" type="b" access="read"/>
          <property name="Fullscreen" type="b" access="readwrite"/>
          <property name="HasTrackList" type="b" access="read"/>
          <property name="Identity" type="s" access="read"/>
          <property name="DesktopEntry" type="s" access="read"/>
          <property name="SupportedUriSchemes" type="as" access="read"/>
          <property name="SupportedMimeTypes" type="as" access="read"/>
        </interface>
        """);

    private static readonly ReadOnlyMemory<byte> PlayerXml = Encoding.UTF8.GetBytes("""
        <interface name="org.mpris.MediaPlayer2.Player">
          <method name="Next"/>
          <method name="Previous"/>
          <method name="Pause"/>
          <method name="PlayPause"/>
          <method name="Stop"/>
          <method name="Play"/>
          <method name="Seek"><arg name="Offset" type="x" direction="in"/></method>
          <method name="SetPosition"><arg name="TrackId" type="o" direction="in"/><arg name="Position" type="x" direction="in"/></method>
          <method name="OpenUri"><arg name="Uri" type="s" direction="in"/></method>
          <signal name="Seeked"><arg name="Position" type="x"/></signal>
          <property name="PlaybackStatus" type="s" access="read"/>
          <property name="LoopStatus" type="s" access="readwrite"/>
          <property name="Rate" type="d" access="readwrite"/>
          <property name="Shuffle" type="b" access="readwrite"/>
          <property name="Metadata" type="a{sv}" access="read"/>
          <property name="Volume" type="d" access="readwrite"/>
          <property name="Position" type="x" access="read"/>
          <property name="MinimumRate" type="d" access="read"/>
          <property name="MaximumRate" type="d" access="read"/>
          <property name="CanGoNext" type="b" access="read"/>
          <property name="CanGoPrevious" type="b" access="read"/>
          <property name="CanPlay" type="b" access="read"/>
          <property name="CanPause" type="b" access="read"/>
          <property name="CanSeek" type="b" access="read"/>
          <property name="CanControl" type="b" access="read"/>
        </interface>
        """);

    public void Dispose()
    {
        connection?.Dispose();
        connection = null;
    }
}
