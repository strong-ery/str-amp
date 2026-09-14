using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Stramp.Integrations.Windows;

namespace Stramp.App.Services;

/// <summary>Owns focused-window transport shortcuts and the Windows hardware-media-key session.</summary>
public sealed class MediaKeyController : IDisposable
{
    private const double VolumeStep = 5;

    private readonly Window _window;
    private readonly Action _playPause;
    private readonly Action _next;
    private readonly Action _previous;
    private readonly Func<double> _getVolume;
    private readonly Action<double> _setVolume;
    private WindowsMediaKeys? _windowsMediaKeys;

    public MediaKeyController(
        Window window,
        Action playPause,
        Action next,
        Action previous,
        Func<double> getVolume,
        Action<double> setVolume)
    {
        _window = window;
        _playPause = playPause;
        _next = next;
        _previous = previous;
        _getVolume = getVolume;
        _setVolume = setVolume;

        _window.AddHandler(InputElement.KeyDownEvent, OnKeyDown,
            RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public void AttachWindowsMediaKeys(IntPtr hwnd, bool isPlaying)
    {
        if (!OperatingSystem.IsWindows() || _windowsMediaKeys is not null)
            return;

        var mediaKeys = new WindowsMediaKeys();
        if (!mediaKeys.TryInitialize(hwnd))
        {
            mediaKeys.Dispose();
            return;
        }

        mediaKeys.PlayPauseRequested += () => Dispatch(_playPause);
        mediaKeys.NextRequested += () => Dispatch(_next);
        mediaKeys.PreviousRequested += () => Dispatch(_previous);
        mediaKeys.SetPlaybackState(isPlaying);
        _windowsMediaKeys = mediaKeys;
    }

    public void SetPlaybackState(bool isPlaying) =>
        _windowsMediaKeys?.SetPlaybackState(isPlaying);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Modified arrows retain their normal control/text-editing meaning.
        if (e.KeyModifiers != KeyModifiers.None)
            return;

        switch (e.Key)
        {
            case Key.Space:
                _playPause();
                break;
            case Key.Left:
                _previous();
                break;
            case Key.Right:
                _next();
                break;
            case Key.Up:
                _setVolume(Math.Clamp(_getVolume() + VolumeStep, 0, 100));
                break;
            case Key.Down:
                _setVolume(Math.Clamp(_getVolume() - VolumeStep, 0, 100));
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private static void Dispatch(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    public void Dispose()
    {
        _window.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        _windowsMediaKeys?.Dispose();
        _windowsMediaKeys = null;
    }
}
