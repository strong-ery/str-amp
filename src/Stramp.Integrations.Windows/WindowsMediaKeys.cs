using Windows.Media;

namespace Stramp.Integrations.Windows;

/// <summary>
/// Registers STRAMP with Windows System Media Transport Controls. This is the native media session
/// used by hardware media keys and the Windows media flyout; it does not steal ordinary keyboard
/// input from other applications.
/// </summary>
public sealed class WindowsMediaKeys : IDisposable
{
    private SystemMediaTransportControls? _controls;

    public event Action? PlayPauseRequested;
    public event Action? NextRequested;
    public event Action? PreviousRequested;

    public bool TryInitialize(IntPtr hwnd)
    {
        if (_controls is not null || hwnd == IntPtr.Zero || !OperatingSystem.IsWindows())
            return false;

        try
        {
            var controls = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
            controls.IsEnabled = true;
            controls.IsPlayEnabled = true;
            controls.IsPauseEnabled = true;
            controls.IsNextEnabled = true;
            controls.IsPreviousEnabled = true;
            controls.PlaybackStatus = MediaPlaybackStatus.Stopped;
            controls.ButtonPressed += OnButtonPressed;
            _controls = controls;
            return true;
        }
        catch
        {
            // Media keys are an integration nicety; playback must remain usable if SMTC is absent.
            return false;
        }
    }

    public void SetPlaybackState(bool isPlaying)
    {
        if (_controls is not null)
            _controls.PlaybackStatus = isPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
    }

    private void OnButtonPressed(SystemMediaTransportControls sender,
        SystemMediaTransportControlsButtonPressedEventArgs e)
    {
        switch (e.Button)
        {
            case SystemMediaTransportControlsButton.Play:
            case SystemMediaTransportControlsButton.Pause:
                PlayPauseRequested?.Invoke();
                break;
            case SystemMediaTransportControlsButton.Next:
                NextRequested?.Invoke();
                break;
            case SystemMediaTransportControlsButton.Previous:
                PreviousRequested?.Invoke();
                break;
        }
    }

    public void Dispose()
    {
        if (_controls is null)
            return;

        _controls.ButtonPressed -= OnButtonPressed;
        _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
        _controls.IsEnabled = false;
        _controls = null;
    }
}
