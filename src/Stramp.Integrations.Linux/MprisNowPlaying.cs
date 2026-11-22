using Stramp.Core.Models;
using Stramp.Core.Playback;
using Tmds.DBus.Protocol;

namespace Stramp.Integrations.Linux;

/// <summary>
/// Publishes str-amp as an MPRIS2 media player on the D-Bus session bus. That is what makes the
/// keyboard's media keys, the desktop's media widget (GNOME, KDE and so on) and tools like
/// playerctl control it, and what shows the current track outside the app.
/// </summary>
public sealed class MprisNowPlaying : INowPlayingIntegration, IDisposable
{
    private const string BusName = "org.mpris.MediaPlayer2.stramp";
    private const string MprisPath = "/org/mpris/MediaPlayer2";
    private const string RootInterface = "org.mpris.MediaPlayer2";
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string TrackIdPath = "/org/stramp/track/current";

    private static readonly string[] PlayerPropertyNames =
    [
        "PlaybackStatus", "LoopStatus", "Rate", "Shuffle", "Metadata", "Volume", "Position", "MinimumRate",
        "MaximumRate", "CanGoNext", "CanGoPrevious", "CanPlay", "CanPause", "CanSeek", "CanControl",
    ];

    private static readonly string[] RootPropertyNames =
        ["CanQuit", "CanRaise", "HasTrackList", "Identity", "DesktopEntry", "SupportedUriSchemes", "SupportedMimeTypes"];

    private readonly string _desktopEntry;
    private readonly Handler _handler;
    private readonly object _state = new();
    private DBusConnection? _connection;
    private Song? _song;
    private bool _paused = true;
    private bool _hasTrack;
    private double _volume = 100;
    private double _positionSeconds;
    private bool _started;

    public event Action? PlayPauseRequested;
    public event Action? NextRequested;
    public event Action? PreviousRequested;
    public event Action<double>? VolumeRequested;

    /// <summary>Raised when the desktop asks the app to come to the front.</summary>
    public event Action? RaiseRequested;

    /// <summary>Raised when the desktop asks the app to quit.</summary>
    public event Action? QuitRequested;

    /// <summary>Raised on Seek/SetPosition; the argument is the absolute position in seconds.</summary>
    public event Action<double>? SeekRequested;

    public MprisNowPlaying(string desktopEntry = "stramp")
    {
        _desktopEntry = desktopEntry;
        _handler = new Handler(this);
    }

    public void Start()
    {
        if (_started)
            return;
        _started = true;
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            var address = DBusAddress.Session;
            if (string.IsNullOrEmpty(address))
                return;

            var connection = new DBusConnection(address);
            await connection.ConnectAsync();
            connection.AddMethodHandler(_handler);
            // Not queued: if another str-amp already holds the name, this instance just stays quiet.
            await connection.RequestNameAsync(BusName, RequestNameOptions.None);
            _connection = connection;
        }
        catch
        {
            // No session bus (headless, a minimal window manager, a container): media keys are a
            // nicety, and playback is unaffected.
            _connection = null;
        }
    }

    public void Update(Song song, bool paused, double volume)
    {
        bool trackChanged, stateChanged, volumeChanged;
        lock (_state)
        {
            trackChanged = !ReferenceEquals(_song, song) || !_hasTrack;
            stateChanged = _paused != paused;
            volumeChanged = Math.Abs(_volume - volume) > 0.01;
            _song = song;
            _hasTrack = true;
            _paused = paused;
            _volume = volume;
        }

        var changed = new List<string>();
        if (trackChanged)
            changed.AddRange(["Metadata", "CanPlay", "CanPause", "CanSeek"]);
        if (stateChanged || trackChanged)
            changed.Add("PlaybackStatus");
        if (volumeChanged)
            changed.Add("Volume");
        if (changed.Count > 0)
            EmitPropertiesChanged(changed);
    }

    /// <summary>Keeps <c>Position</c> readable. Clients poll it; it is never signalled.</summary>
    public void SetPosition(double seconds)
    {
        lock (_state)
            _positionSeconds = seconds;
    }

    public void Clear()
    {
        lock (_state)
        {
            _song = null;
            _hasTrack = false;
            _paused = true;
        }

        EmitPropertiesChanged(["Metadata", "PlaybackStatus", "CanPlay", "CanPause", "CanSeek"]);
    }

    private void EmitPropertiesChanged(IReadOnlyList<string> names)
    {
        var connection = _connection;
        if (connection is null)
            return;

        try
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteSignalHeader(
                path: MprisPath, @interface: PropertiesInterface, member: "PropertiesChanged", signature: "sa{sv}as");
            writer.WriteString(PlayerInterface);

            var start = writer.WriteDictionaryStart();
            lock (_state)
            {
                foreach (var name in names)
                {
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString(name);
                    WritePlayerProperty(writer, name);
                }
            }
            writer.WriteDictionaryEnd(start);

            writer.WriteArray(Array.Empty<string>());
            connection.TrySendMessage(writer.CreateMessage());
        }
        catch
        {
            // The bus went away; nobody left to tell.
        }
    }

    // ---- property values ---------------------------------------------------------------------

    private string PlaybackStatus => !_hasTrack ? "Stopped" : _paused ? "Paused" : "Playing";

    /// <summary>Writes one Player property as a variant. The caller holds the state lock.</summary>
    private void WritePlayerProperty(MessageWriter writer, string name)
    {
        switch (name)
        {
            case "PlaybackStatus": writer.WriteVariant(PlaybackStatus); break;
            case "LoopStatus": writer.WriteVariant("None"); break;
            case "Rate": case "MinimumRate": case "MaximumRate": writer.WriteVariant(1.0); break;
            case "Shuffle": writer.WriteVariant(false); break;
            case "Volume": writer.WriteVariant(_volume / 100.0); break;
            case "Position": writer.WriteVariant((long)(_positionSeconds * 1_000_000)); break;
            case "CanGoNext": case "CanGoPrevious": case "CanControl": writer.WriteVariant(true); break;
            case "CanPlay": case "CanPause": case "CanSeek": writer.WriteVariant(_hasTrack); break;
            case "Metadata": WriteMetadataVariant(writer); break;
            default: throw new ArgumentException(name);
        }
    }

    private void WriteRootProperty(MessageWriter writer, string name)
    {
        switch (name)
        {
            case "CanQuit": writer.WriteVariant(true); break;
            case "CanRaise": writer.WriteVariant(true); break;
            case "HasTrackList": writer.WriteVariant(false); break;
            case "Identity": writer.WriteVariant("str-amp"); break;
            case "DesktopEntry": writer.WriteVariant(_desktopEntry); break;
            case "SupportedUriSchemes": writer.WriteVariant(VariantValue.Array(new[] { "file" })); break;
            case "SupportedMimeTypes":
                writer.WriteVariant(VariantValue.Array(new[] { "audio/mpeg", "audio/flac", "audio/ogg" }));
                break;
            default: throw new ArgumentException(name);
        }
    }

    /// <summary>Metadata is a variant holding a{sv}, which the high-level value type cannot build.</summary>
    private void WriteMetadataVariant(MessageWriter writer)
    {
        writer.WriteSignature("a{sv}");
        var start = writer.WriteDictionaryStart();
        if (_hasTrack && _song is { } song)
        {
            Entry(writer, "mpris:trackid", VariantValue.ObjectPath(new ObjectPath(TrackIdPath)));
            Entry(writer, "mpris:length", (long)(song.Duration.TotalMilliseconds * 1000));
            Entry(writer, "xesam:title", song.Title);
            Entry(writer, "xesam:artist", VariantValue.Array(new[] { song.Artist }));
            if (!string.IsNullOrEmpty(song.Album))
                Entry(writer, "xesam:album", song.Album);
            if (!string.IsNullOrEmpty(song.AlbumArtist))
                Entry(writer, "xesam:albumArtist", VariantValue.Array(new[] { song.AlbumArtist }));
            Entry(writer, "xesam:url", new Uri(Path.GetFullPath(song.Path)).AbsoluteUri);
        }
        writer.WriteDictionaryEnd(start);
    }

    private static void Entry(MessageWriter writer, string key, VariantValue value)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString(key);
        writer.WriteVariant(value);
    }

    // ---- method handling ---------------------------------------------------------------------

    private sealed class Handler(MprisNowPlaying owner) : IPathMethodHandler
    {
        public string Path => MprisPath;
        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            try
            {
                Handle(context);
            }
            catch (Exception)
            {
                if (!context.ReplySent)
                    context.ReplyError("org.freedesktop.DBus.Error.Failed", "internal error");
            }

            return ValueTask.CompletedTask;
        }

        private void Handle(MethodContext context)
        {
            var request = context.Request;
            var iface = request.InterfaceAsString;
            var member = request.MemberAsString;

            if (context.IsDBusIntrospectRequest)
            {
                context.ReplyIntrospectXml([System.Text.Encoding.UTF8.GetBytes(IntrospectionXml)]);
                return;
            }

            switch (iface)
            {
                case RootInterface:
                    switch (member)
                    {
                        case "Raise": owner.RaiseRequested?.Invoke(); Ok(context); return;
                        case "Quit": owner.QuitRequested?.Invoke(); Ok(context); return;
                    }
                    break;

                case PlayerInterface:
                    switch (member)
                    {
                        case "PlayPause":
                            owner.PlayPauseRequested?.Invoke();
                            Ok(context);
                            return;
                        case "Play":
                        case "Pause":
                        {
                            // The app exposes a toggle, so only toggle when the request would change state.
                            bool paused;
                            lock (owner._state) paused = owner._paused;
                            if ((member == "Play") == paused)
                                owner.PlayPauseRequested?.Invoke();
                            Ok(context);
                            return;
                        }
                        case "Stop":
                        {
                            bool paused;
                            lock (owner._state) paused = owner._paused;
                            if (!paused)
                                owner.PlayPauseRequested?.Invoke();
                            Ok(context);
                            return;
                        }
                        case "Next": owner.NextRequested?.Invoke(); Ok(context); return;
                        case "Previous": owner.PreviousRequested?.Invoke(); Ok(context); return;
                        case "Seek":
                        {
                            var reader = request.GetBodyReader();
                            var offsetMicros = reader.ReadInt64();
                            double current;
                            lock (owner._state) current = owner._positionSeconds;
                            owner.SeekRequested?.Invoke(Math.Max(0, current + offsetMicros / 1_000_000.0));
                            Ok(context);
                            return;
                        }
                        case "SetPosition":
                        {
                            var reader = request.GetBodyReader();
                            reader.ReadObjectPath();
                            var micros = reader.ReadInt64();
                            owner.SeekRequested?.Invoke(Math.Max(0, micros / 1_000_000.0));
                            Ok(context);
                            return;
                        }
                        case "OpenUri": Ok(context); return;
                    }
                    break;

                case PropertiesInterface:
                    HandleProperties(context, member);
                    return;
            }

            context.ReplyUnknownMethodError();
        }

        private void HandleProperties(MethodContext context, string? member)
        {
            var reader = context.Request.GetBodyReader();
            var iface = reader.ReadString();

            switch (member)
            {
                case "Get":
                {
                    var name = reader.ReadString();
                    using var writer = context.CreateReplyWriter("v");
                    lock (owner._state)
                    {
                        if (iface == PlayerInterface && PlayerPropertyNames.Contains(name))
                            owner.WritePlayerProperty(writer, name);
                        else if (iface == RootInterface && RootPropertyNames.Contains(name))
                            owner.WriteRootProperty(writer, name);
                        else
                        {
                            context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", name);
                            return;
                        }
                    }
                    context.Reply(writer.CreateMessage());
                    return;
                }

                case "GetAll":
                {
                    using var writer = context.CreateReplyWriter("a{sv}");
                    var start = writer.WriteDictionaryStart();
                    lock (owner._state)
                    {
                        if (iface == PlayerInterface)
                        {
                            foreach (var name in PlayerPropertyNames)
                            {
                                writer.WriteDictionaryEntryStart();
                                writer.WriteString(name);
                                owner.WritePlayerProperty(writer, name);
                            }
                        }
                        else if (iface == RootInterface)
                        {
                            foreach (var name in RootPropertyNames)
                            {
                                writer.WriteDictionaryEntryStart();
                                writer.WriteString(name);
                                owner.WriteRootProperty(writer, name);
                            }
                        }
                    }
                    writer.WriteDictionaryEnd(start);
                    context.Reply(writer.CreateMessage());
                    return;
                }

                case "Set":
                {
                    var name = reader.ReadString();
                    if (iface == PlayerInterface && name == "Volume")
                    {
                        reader.ReadSignature("d");
                        var volume = reader.ReadDouble();
                        owner.VolumeRequested?.Invoke(Math.Clamp(volume * 100.0, 0, 100));
                        Ok(context);
                        return;
                    }

                    context.ReplyError("org.freedesktop.DBus.Error.PropertyReadOnly", name);
                    return;
                }
            }

            context.ReplyUnknownMethodError();
        }

        private static void Ok(MethodContext context)
        {
            if (context.NoReplyExpected)
                return;
            using var writer = context.CreateReplyWriter(null);
            context.Reply(writer.CreateMessage());
        }
    }

    private const string IntrospectionXml = """
        <interface name="org.mpris.MediaPlayer2">
          <method name="Raise"/><method name="Quit"/>
          <property name="CanQuit" type="b" access="read"/>
          <property name="CanRaise" type="b" access="read"/>
          <property name="HasTrackList" type="b" access="read"/>
          <property name="Identity" type="s" access="read"/>
          <property name="DesktopEntry" type="s" access="read"/>
          <property name="SupportedUriSchemes" type="as" access="read"/>
          <property name="SupportedMimeTypes" type="as" access="read"/>
        </interface>
        <interface name="org.mpris.MediaPlayer2.Player">
          <method name="Next"/><method name="Previous"/><method name="Pause"/><method name="PlayPause"/>
          <method name="Stop"/><method name="Play"/>
          <method name="Seek"><arg direction="in" type="x"/></method>
          <method name="SetPosition"><arg direction="in" type="o"/><arg direction="in" type="x"/></method>
          <method name="OpenUri"><arg direction="in" type="s"/></method>
          <property name="PlaybackStatus" type="s" access="read"/>
          <property name="LoopStatus" type="s" access="read"/>
          <property name="Rate" type="d" access="read"/>
          <property name="Shuffle" type="b" access="read"/>
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
        """;

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}
