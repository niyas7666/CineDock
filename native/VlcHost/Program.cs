using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VlcHost;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var parent = GetOption(args, "--parent");
        var pipe = GetOption(args, "--pipe");
        var vlcDir = GetOption(args, "--vlc-dir");

        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(pipe) || string.IsNullOrWhiteSpace(vlcDir))
        {
            MessageBox.Show("VlcHost requires --parent, --pipe, and --vlc-dir arguments.", "CineDock");
            return;
        }

        if (!long.TryParse(parent, System.Globalization.NumberStyles.HexNumber, null, out var parentValue))
        {
            MessageBox.Show("The Electron window handle is invalid.", "CineDock");
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new PlayerHostForm((nint)parentValue, pipe, vlcDir));
    }

    private static string? GetOption(string[] args, string option)
    {
        var index = Array.IndexOf(args, option);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

internal sealed class PlayerHostForm : Form
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly nint _parentWindow;
    private readonly string _pipeName;
    private readonly string _vlcDirectory;
    private IntPtr _vlc;
    private IntPtr _media;
    private IntPtr _player;
    private StreamWriter? _pipeWriter;
    private readonly object _pipeWriterLock = new();
    private readonly System.Windows.Forms.Timer _mouseTimer = new() { Interval = 35 };
    private Rectangle _nativeVideoBounds = Rectangle.Empty;
    private bool _primaryButtonDown;
    private DateTime _lastVideoClick = DateTime.MinValue;
    private Native.NativePoint _lastVideoClickPoint;

    public PlayerHostForm(nint parentWindow, string pipeName, string vlcDirectory)
    {
        _parentWindow = parentWindow;
        _pipeName = pipeName;
        _vlcDirectory = vlcDirectory;
        AutoScaleMode = AutoScaleMode.None;
        _mouseTimer.Tick += (_, _) => DetectVideoDoubleClick();
        BackColor = Color.Black;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Load += (_, _) => InitializeVlc();
        Shown += (_, _) =>
        {
            EmbedInElectronWindow();
            SetNativeVisibility(visible: false);
            _mouseTimer.Start();
            _ = RunPipeServerAsync();
        };
        FormClosing += (_, _) => { _mouseTimer.Stop(); ReleaseVlc(); };
    }

    private void InitializeVlc()
    {
        if (!Directory.Exists(_vlcDirectory) || !File.Exists(Path.Combine(_vlcDirectory, "libvlc.dll")))
            throw new FileNotFoundException("libvlc.dll was not found in the selected VLC directory.", _vlcDirectory);

        Native.SetDllDirectory(_vlcDirectory);
        var plugins = Path.Combine(_vlcDirectory, "plugins");
        _vlc = LibVlc.New("--intf=dummy", "--quiet", "--no-video-title-show", "--no-osd", $"--plugin-path={plugins}");
        if (_vlc == IntPtr.Zero)
            throw new InvalidOperationException("libVLC could not be initialized.");
    }

    private void EmbedInElectronWindow()
    {
        Native.SetWindowLongPtr(Handle, Native.GwlStyle, Native.GetWindowLongPtr(Handle, Native.GwlStyle).ToInt64() | Native.WsChild);
        Native.SetParent(Handle, _parentWindow);
        Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 1, 1, Native.SwpNoActivate | Native.SwpNoZOrder | Native.SwpShowWindow);
    }

    // libVLC creates several vout child windows. Hiding only the host Form leaves
    // those windows painted above Electron, which is why Browser mode became black.
    private void SetNativeVisibility(bool visible)
    {
        var command = visible ? Native.SwShowNoActivate : Native.SwHide;
        Native.ShowWindow(Handle, command);
        Native.EnumChildWindows(_parentWindow, (window, _) =>
        {
            Native.GetWindowThreadProcessId(window, out var processId);
            if (processId == Environment.ProcessId)
                Native.ShowWindow(window, command);
            return true;
        }, IntPtr.Zero);
    }

    // Keep libVLC's own video-output children inside the same safe rectangle as
    // the host form. Without this, a stale vout child can cover the HTML dock.
    private void SetNativeLayout(int x, int y, int width, int height)
    {
        const uint flags = Native.SwpNoActivate | Native.SwpNoZOrder;
        _nativeVideoBounds = new Rectangle(x, y, width, height);
        Native.SetWindowPos(Handle, IntPtr.Zero, x, y, width, height, flags);
        Native.EnumChildWindows(Handle, (window, _) =>
        {
            Native.GetWindowThreadProcessId(window, out var processId);
            if (processId == Environment.ProcessId)
                Native.SetWindowPos(window, IntPtr.Zero, 0, 0, width, height, flags);
            return true;
        }, IntPtr.Zero);
    }

    // The native vout owns the click, not Chromium. Polling the button edge keeps
    // double-click fullscreen available even when a libVLC child has focus.
    private void DetectVideoDoubleClick()
    {
        var isDown = Native.IsPrimaryMouseDown();
        if (isDown && !_primaryButtonDown && _player != IntPtr.Zero && Native.GetCursorPos(out var point))
        {
            Native.ScreenToClient(_parentWindow, ref point);
            if (_nativeVideoBounds.Contains(point.X, point.Y))
            {
                var now = DateTime.UtcNow;
                var closeEnough = Math.Abs(point.X - _lastVideoClickPoint.X) < 10 &&
                                  Math.Abs(point.Y - _lastVideoClickPoint.Y) < 10;
                if (closeEnough && (now - _lastVideoClick).TotalMilliseconds <= 450)
                {
                    _lastVideoClick = DateTime.MinValue;
                    SendHostEvent("toggle-fullscreen");
                }
                else
                {
                    _lastVideoClick = now;
                    _lastVideoClickPoint = point;
                }
            }
        }
        _primaryButtonDown = isDown;
    }

    private void SendHostEvent(string type)
    {
        lock (_pipeWriterLock)
        {
            try
            {
                _pipeWriter?.WriteLine(JsonSerializer.Serialize(new HostResponse(type, null, null), JsonOptions));
            }
            catch (IOException) { /* The Electron pipe is closing. */ }
            catch (ObjectDisposedException) { /* The Electron pipe is closing. */ }
        }
    }

    private async Task RunPipeServerAsync()
    {
        while (!IsDisposed)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync();
                using var reader = new StreamReader(pipe);
                await using var writer = new StreamWriter(pipe) { AutoFlush = true };
                lock (_pipeWriterLock) _pipeWriter = writer;
                try
                {
                    string? line;
                    while (!IsDisposed && pipe.IsConnected && (line = await reader.ReadLineAsync()) is not null)
                    {
                        var command = JsonSerializer.Deserialize<PlayerCommand>(line, JsonOptions) ?? new PlayerCommand();
                        var response = await RunOnUiThreadAsync(command);
                        lock (_pipeWriterLock)
                            writer.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
                    }
                }
                finally
                {
                    lock (_pipeWriterLock)
                    {
                        if (ReferenceEquals(_pipeWriter, writer)) _pipeWriter = null;
                    }
                }
            }
            catch (ObjectDisposedException) { return; }
            catch (Exception error)
            {
                if (!IsDisposed) BeginInvoke(() => Text = $"VLC host: {error.Message}");
                await Task.Delay(250);
            }
        }
    }

    private Task<HostResponse> RunOnUiThreadAsync(PlayerCommand command)
    {
        var completion = new TaskCompletionSource<HostResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(() =>
        {
            try { completion.SetResult(HandleCommand(command)); }
            catch (Exception error) { completion.SetResult(new HostResponse("error", null, error.Message)); }
        });
        return completion.Task;
    }

    private HostResponse HandleCommand(PlayerCommand command)
    {
        switch (command.Action)
        {
            case "load": LoadMovie(command.Url); break;
            case "layout":
                SetNativeLayout(command.X ?? 0, command.Y ?? 0,
                    Math.Max(1, command.Width ?? 1), Math.Max(1, command.Height ?? 1));
                break;
            case "play-pause": TogglePause(); break;
            case "seek": Seek(command.Position ?? 0); break;
            case "seek-relative": SeekRelative(command.Seconds ?? 0); break;
            case "volume": LibVlc.AudioSetVolume(_player, Math.Clamp(command.Volume ?? 100, 0, 200)); break;
            case "mute": LibVlc.AudioSetMute(_player, command.Muted ?? false); break;
            case "rate": LibVlc.SetRate(_player, Math.Clamp(command.Rate ?? 1f, 0.25f, 4f)); break;
            case "cycle-audio": CycleTrack(isAudio: true); break;
            case "cycle-subtitle": CycleTrack(isAudio: false); break;
            case "set-audio": if (_player != IntPtr.Zero && command.TrackId is int aid) LibVlc.AudioSetTrack(_player, aid); break;
            case "set-subtitle": if (_player != IntPtr.Zero && command.TrackId is int sid) LibVlc.VideoSetSpu(_player, sid); break;
            case "next-frame": if (_player != IntPtr.Zero) LibVlc.NextFrame(_player); break;
            case "subtitle-delay": AdjustDelay(isAudio: false, command.DeltaUs ?? 0); break;
            case "audio-delay": AdjustDelay(isAudio: true, command.DeltaUs ?? 0); break;
            case "adjust": AdjustVideo(command.Property, command.Value ?? 1); break;
            case "reset-adjustments": ResetVideoAdjustments(); break;
            case "stop": StopMovie(); break;
            case "quit": Close(); break;
        }
        return new HostResponse("state", GetState(), null);
    }

    private void LoadMovie(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Only HTTP(S) media URLs are allowed.");

        StopMovie();
        _media = LibVlc.MediaNewLocation(_vlc, uri.AbsoluteUri);
        if (_media == IntPtr.Zero) throw new InvalidOperationException("libVLC could not open the media URL.");
        _player = LibVlc.MediaPlayerNewFromMedia(_media);
        if (_player == IntPtr.Zero) throw new InvalidOperationException("libVLC could not create a media player.");
        LibVlc.MediaPlayerSetHwnd(_player, Handle);
        SetNativeVisibility(visible: true);
        LibVlc.MediaPlayerPlay(_player);
    }

    private void StopMovie()
    {
        // Hide every libVLC vout child before releasing media. Otherwise a vout
        // remains over Electron after Browser mode becomes visible.
        SetNativeVisibility(visible: false);
        Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 1, 1, Native.SwpNoActivate | Native.SwpNoZOrder);
        if (_player != IntPtr.Zero)
        {
            LibVlc.MediaPlayerStop(_player);
            LibVlc.MediaPlayerRelease(_player);
            _player = IntPtr.Zero;
        }
        if (_media != IntPtr.Zero)
        {
            LibVlc.MediaRelease(_media);
            _media = IntPtr.Zero;
        }
    }

    private void TogglePause()
    {
        if (_player == IntPtr.Zero) return;
        if (LibVlc.MediaPlayerIsPlaying(_player)) LibVlc.MediaPlayerSetPause(_player, true);
        else LibVlc.MediaPlayerPlay(_player);
    }

    private void Seek(double positionSeconds)
    {
        if (_player != IntPtr.Zero) LibVlc.MediaPlayerSetTime(_player, (long)Math.Max(0, positionSeconds * 1000));
    }

    private void SeekRelative(double seconds)
    {
        if (_player == IntPtr.Zero) return;
        var now = LibVlc.MediaPlayerGetTime(_player);
        LibVlc.MediaPlayerSetTime(_player, Math.Max(0, now + (long)(seconds * 1000)));
    }

    private void AdjustDelay(bool isAudio, long deltaUs)
    {
        if (_player == IntPtr.Zero) return;
        if (isAudio)
            LibVlc.AudioSetDelay(_player, LibVlc.AudioGetDelay(_player) + deltaUs);
        else
            LibVlc.VideoSetSpuDelay(_player, LibVlc.VideoGetSpuDelay(_player) + deltaUs);
    }

    private void AdjustVideo(string? property, double value)
    {
        if (_player == IntPtr.Zero) return;
        var option = property switch
        {
            "brightness" => LibVlc.AdjustBrightness,
            "contrast" => LibVlc.AdjustContrast,
            "saturation" => LibVlc.AdjustSaturation,
            "gamma" => LibVlc.AdjustGamma,
            _ => -1
        };
        if (option < 0) return;
        LibVlc.VideoSetAdjustInt(_player, LibVlc.AdjustEnable, 1);
        LibVlc.VideoSetAdjustFloat(_player, option, (float)value);
    }

    private void ResetVideoAdjustments()
    {
        if (_player == IntPtr.Zero) return;
        LibVlc.VideoSetAdjustFloat(_player, LibVlc.AdjustBrightness, 1f);
        LibVlc.VideoSetAdjustFloat(_player, LibVlc.AdjustContrast, 1f);
        LibVlc.VideoSetAdjustFloat(_player, LibVlc.AdjustSaturation, 1f);
        LibVlc.VideoSetAdjustFloat(_player, LibVlc.AdjustGamma, 1f);
        LibVlc.VideoSetAdjustInt(_player, LibVlc.AdjustEnable, 0);
    }

    private void CycleTrack(bool isAudio)
    {
        if (_player == IntPtr.Zero) return;
        var tracks = GetTracks(isAudio)
            .Where(track => isAudio ? track.Id >= 0 : true)
            .ToList();
        if (tracks.Count == 0) return;
        var active = isAudio ? LibVlc.AudioGetTrack(_player) : LibVlc.VideoGetSpu(_player);
        var current = tracks.FindIndex(track => track.Id == active);
        var next = tracks[(current + 1) % tracks.Count].Id;
        if (isAudio) LibVlc.AudioSetTrack(_player, next);
        else LibVlc.VideoSetSpu(_player, next);
    }

    private List<Track> GetTracks(bool isAudio)
    {
        var result = new List<Track>();
        if (_player == IntPtr.Zero) return result;
        var head = isAudio ? LibVlc.AudioGetTrackDescription(_player) : LibVlc.VideoGetSpuDescription(_player);
        var current = head;
        try
        {
            while (current != IntPtr.Zero)
            {
                var item = Marshal.PtrToStructure<LibVlc.TrackDescription>(current);
                result.Add(new Track(item.Id, Marshal.PtrToStringUTF8(item.Name) ?? $"Track {item.Id}"));
                current = item.Next;
            }
        }
        finally
        {
            if (head != IntPtr.Zero) LibVlc.TrackDescriptionListRelease(head);
        }
        return result;
    }

    private PlayerState GetState()
    {
        if (_player == IntPtr.Zero) return new PlayerState(false, 0, 0, 100, false, 1, [], [], -1, -1);
        return new PlayerState(
            LibVlc.MediaPlayerIsPlaying(_player),
            LibVlc.MediaPlayerGetTime(_player),
            LibVlc.MediaPlayerGetLength(_player),
            LibVlc.AudioGetVolume(_player),
            LibVlc.AudioGetMute(_player),
            LibVlc.MediaPlayerGetRate(_player),
            GetTracks(isAudio: true),
            GetTracks(isAudio: false),
            LibVlc.AudioGetTrack(_player),
            LibVlc.VideoGetSpu(_player));
    }

    private void ReleaseVlc()
    {
        StopMovie();
        if (_vlc != IntPtr.Zero)
        {
            LibVlc.Release(_vlc);
            _vlc = IntPtr.Zero;
        }
    }
}

internal sealed record PlayerCommand(
    string? Action = null, string? Url = null, int? X = null, int? Y = null,
    int? Width = null, int? Height = null, double? Position = null,
    double? Seconds = null, int? Volume = null, bool? Muted = null, float? Rate = null,
    int? TrackId = null, long? DeltaUs = null, string? Property = null, double? Value = null);

internal sealed record HostResponse(string Type, PlayerState? State, string? Error);
internal sealed record PlayerState(bool Playing, long PositionMs, long DurationMs, int Volume, bool Muted, float Rate,
    List<Track> AudioTracks, List<Track> SubtitleTracks, int AudioTrackId, int SubtitleTrackId);
internal sealed record Track(int Id, string Name);

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint { public int X; public int Y; }

    internal delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
    internal const int GwlStyle = -16;
    internal const long WsChild = 0x40000000L;
    internal const int SwHide = 0, SwShowNoActivate = 4;
    internal const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpShowWindow = 0x0040;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetDllDirectory(string lpPathName);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool ScreenToClient(IntPtr hwnd, ref NativePoint point);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    internal static bool IsPrimaryMouseDown() => (GetAsyncKeyState(0x01) & 0x8000) != 0;
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetParent(IntPtr child, IntPtr newParent);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, long value);
}

internal static class LibVlc
{
    private const string Library = "libvlc.dll";
    internal const int AdjustEnable = 0, AdjustContrast = 1, AdjustBrightness = 2,
        AdjustSaturation = 4, AdjustGamma = 5;

    [StructLayout(LayoutKind.Sequential)]
    internal struct TrackDescription { public int Id; public IntPtr Name; public IntPtr Next; }

    internal static IntPtr New(params string[] options)
    {
        var strings = options.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        var array = Marshal.AllocHGlobal(IntPtr.Size * strings.Length);
        try
        {
            Marshal.Copy(strings, 0, array, strings.Length);
            return libvlc_new(strings.Length, array);
        }
        finally
        {
            Marshal.FreeHGlobal(array);
            foreach (var value in strings) Marshal.FreeCoTaskMem(value);
        }
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr libvlc_new(int argc, IntPtr argv);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_release(IntPtr instance);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr libvlc_media_new_location(IntPtr instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string location);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_media_release(IntPtr media);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr libvlc_media_player_new_from_media(IntPtr media);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_media_player_release(IntPtr player);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_media_player_play(IntPtr player);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_media_player_stop(IntPtr player);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern bool libvlc_media_player_is_playing(IntPtr player);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_media_player_set_pause(IntPtr player, bool pause);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern long libvlc_media_player_get_time(IntPtr player);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_media_player_set_time(IntPtr player, long time);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern long libvlc_media_player_get_length(IntPtr player);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern float libvlc_media_player_get_rate(IntPtr player);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_media_player_set_rate(IntPtr player, float rate);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_media_player_set_hwnd(IntPtr player, IntPtr hwnd);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_media_player_next_frame(IntPtr player);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_audio_get_volume(IntPtr player);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_audio_set_volume(IntPtr player, int volume);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern bool libvlc_audio_get_mute(IntPtr player);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_audio_set_mute(IntPtr player, bool mute);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_audio_get_track(IntPtr player);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_audio_set_track(IntPtr player, int track);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr libvlc_audio_get_track_description(IntPtr player);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern long libvlc_audio_get_delay(IntPtr player);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_audio_set_delay(IntPtr player, long delay);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_video_get_spu(IntPtr player);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_video_set_spu(IntPtr player, int track);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr libvlc_video_get_spu_description(IntPtr player);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern long libvlc_video_get_spu_delay(IntPtr player);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_video_set_spu_delay(IntPtr player, long delay);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_video_set_adjust_int(IntPtr player, int option, int value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_video_set_adjust_float(IntPtr player, int option, float value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_track_description_list_release(IntPtr tracks);

    internal static void Release(IntPtr value) => libvlc_release(value);
    internal static IntPtr MediaNewLocation(IntPtr instance, string value) => libvlc_media_new_location(instance, value);
    internal static void MediaRelease(IntPtr value) => libvlc_media_release(value);
    internal static IntPtr MediaPlayerNewFromMedia(IntPtr value) => libvlc_media_player_new_from_media(value);
    internal static void MediaPlayerRelease(IntPtr value) => libvlc_media_player_release(value);
    internal static void MediaPlayerPlay(IntPtr value) => libvlc_media_player_play(value);
    internal static void MediaPlayerStop(IntPtr value) => libvlc_media_player_stop(value);
    internal static bool MediaPlayerIsPlaying(IntPtr value) => libvlc_media_player_is_playing(value);
    internal static void MediaPlayerSetPause(IntPtr value, bool pause) => libvlc_media_player_set_pause(value, pause);
    internal static long MediaPlayerGetTime(IntPtr value) => libvlc_media_player_get_time(value);
    internal static void MediaPlayerSetTime(IntPtr value, long time) => libvlc_media_player_set_time(value, time);
    internal static long MediaPlayerGetLength(IntPtr value) => libvlc_media_player_get_length(value);
    internal static float MediaPlayerGetRate(IntPtr value) => libvlc_media_player_get_rate(value);
    internal static void SetRate(IntPtr value, float rate) => libvlc_media_player_set_rate(value, rate);
    internal static void MediaPlayerSetHwnd(IntPtr value, IntPtr hwnd) => libvlc_media_player_set_hwnd(value, hwnd);
    internal static void NextFrame(IntPtr value) => libvlc_media_player_next_frame(value);
    internal static int AudioGetVolume(IntPtr value) => libvlc_audio_get_volume(value);
    internal static void AudioSetVolume(IntPtr value, int volume) => libvlc_audio_set_volume(value, volume);
    internal static bool AudioGetMute(IntPtr value) => libvlc_audio_get_mute(value);
    internal static void AudioSetMute(IntPtr value, bool mute) => libvlc_audio_set_mute(value, mute);
    internal static int AudioGetTrack(IntPtr value) => libvlc_audio_get_track(value);
    internal static void AudioSetTrack(IntPtr value, int track) => libvlc_audio_set_track(value, track);
    internal static IntPtr AudioGetTrackDescription(IntPtr value) => libvlc_audio_get_track_description(value);
    internal static long AudioGetDelay(IntPtr value) => libvlc_audio_get_delay(value);
    internal static void AudioSetDelay(IntPtr value, long delay) => libvlc_audio_set_delay(value, delay);
    internal static int VideoGetSpu(IntPtr value) => libvlc_video_get_spu(value);
    internal static void VideoSetSpu(IntPtr value, int track) => libvlc_video_set_spu(value, track);
    internal static IntPtr VideoGetSpuDescription(IntPtr value) => libvlc_video_get_spu_description(value);
    internal static long VideoGetSpuDelay(IntPtr value) => libvlc_video_get_spu_delay(value);
    internal static void VideoSetSpuDelay(IntPtr value, long delay) => libvlc_video_set_spu_delay(value, delay);
    internal static void VideoSetAdjustInt(IntPtr value, int option, int setting) => libvlc_video_set_adjust_int(value, option, setting);
    internal static void VideoSetAdjustFloat(IntPtr value, int option, float setting) => libvlc_video_set_adjust_float(value, option, setting);
    internal static void TrackDescriptionListRelease(IntPtr value) => libvlc_track_description_list_release(value);
}
