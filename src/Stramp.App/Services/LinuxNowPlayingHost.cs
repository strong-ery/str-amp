#if !STRAMP_WINDOWS
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Stramp.App.ViewModels;
using Stramp.Integrations.Linux;

namespace Stramp.App.Services;

/// <summary>
/// Publishes what's playing to the desktop over MPRIS (media keys, the shell's media widget,
/// playerctl, KDE Connect...) and routes the desktop's transport requests back into the view model.
/// </summary>
public sealed class LinuxNowPlayingHost : IDisposable
{
    private readonly MprisNowPlaying _mpris = new();
    private readonly Window _window;
    private MainWindowViewModel? _vm;

    public LinuxNowPlayingHost(Window window) => _window = window;

    public void Attach(MainWindowViewModel? vm)
    {
        if (vm is null || _vm is not null)
            return;

        _vm = vm;
        _mpris.PlayPauseRequested += () => Post(() => vm.PlayPauseCommand.Execute(null));
        _mpris.NextRequested += () => Post(() => vm.NextCommand.Execute(null));
        _mpris.PreviousRequested += () => Post(() => vm.PreviousCommand.Execute(null));
        _mpris.VolumeRequested += volume => Post(() => vm.Volume = Math.Clamp(volume, 0, 100));
        _mpris.SeekRequested += seconds => Post(() =>
        {
            if (vm.HasCurrentSong)
                vm.EndSeek(Math.Clamp(seconds, 0, vm.DurationSeconds));
        });
        _mpris.RaiseRequested += () => Post(() =>
        {
            if (_window.WindowState == WindowState.Minimized)
                _window.WindowState = WindowState.Normal;
            _window.Activate();
        });
        _mpris.QuitRequested += () => Post(_window.Close);

        _mpris.Start();
        vm.PropertyChanged += OnViewModelPropertyChanged;
        Push();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.IsPlaying):
            case nameof(MainWindowViewModel.CurrentSong):
            case nameof(MainWindowViewModel.Volume):
                Push();
                break;
            case nameof(MainWindowViewModel.ProgressSeconds):
                if (_vm is { } vm)
                    _mpris.SetPosition(vm.ProgressSeconds);
                break;
        }
    }

    private void Push()
    {
        if (_vm is not { } vm)
            return;

        if (vm.CurrentSong is { } song)
        {
            _mpris.SetPosition(vm.ProgressSeconds);
            _mpris.Update(song, paused: !vm.IsPlaying, vm.Volume);
        }
        else
        {
            _mpris.Clear();
        }
    }

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    public void Dispose()
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _mpris.Dispose();
    }
}
#endif
