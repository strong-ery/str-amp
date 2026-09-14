using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Stramp.App.ViewModels;
using Stramp.Core.Dsp;
using Stramp.Integrations.Windows;

namespace Stramp.App.Views;

public partial class MainWindow : Window
{
    private const int SpectrumBins = 160;

    // Idle motion: a slow sway that keeps the cover alive between beats. Deliberately small so
    // beat hits stay the dominant motion rather than competing with a constant drift.
    private const double DriftPixels = 4.5;
    private const double MaxRotationDegrees = 0.9;

    // Beat response. Hits snap to full size in one frame then ease out over ~0.2s.
    private const double KickStretch = 0.17;
    private const double HihatStretch = 0.13;
    private const double KickSquash = 0.07;
    private const double KickShiftPixels = 7;
    private const double PulseDecayPerTick = 0.86;

    private readonly DispatcherTimer _frameTimer;
    private readonly BeatDetector _kickDetector = new(sensitivity: 1.9, refractoryFrames: 7);
    private readonly BeatDetector _hihatDetector = new(sensitivity: 2.2, refractoryFrames: 3);

    private readonly ScaleTransform _artScale = new();
    private readonly RotateTransform _artRotate = new();
    private readonly TranslateTransform _artTranslate = new();

    private double _driftPhase;
    private float _kickPulse;
    private float _hihatPulse;

    private ThumbnailToolbar? _thumbnailToolbar;
    private MainWindowViewModel? _observedViewModel;

    public MainWindow()
    {
        InitializeComponent();

        // Transform the whole framed artwork, not the image inside the clipping frame —
        // transforming the inner image just slides the picture around behind a fixed window.
        ArtFrame.RenderTransform = new TransformGroup
        {
            Children = { _artScale, _artRotate, _artTranslate },
        };

        _frameTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16), // ~60fps
        };
        _frameTimer.Tick += OnFrameTick;

        // The Slider handles pointer events internally and marks them handled, so a normal
        // XAML event hookup never sees the press/release that bracket a scrub.
        ProgressSlider.AddHandler(PointerPressedEvent, OnProgressPointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        ProgressSlider.AddHandler(PointerReleasedEvent, OnProgressPointerReleased,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        ProgressSlider.AddHandler(PointerCaptureLostEvent, OnProgressCaptureLost,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        WaveformBar.Scrubbing += fraction => ViewModel?.ScrubTo(fraction);
        WaveformBar.ScrubCompleted += fraction => ViewModel?.CompleteScrub(fraction);

        Opened += (_, _) =>
        {
            _frameTimer.Start();
            SetUpThumbnailToolbar();
        };
        Closed += (_, _) =>
        {
            _frameTimer.Stop();
            _thumbnailToolbar?.Dispose();
        };
        PropertyChanged += OnWindowPropertyChanged;
        DataContextChanged += OnDataContextChanged;
    }

    // ── Taskbar thumbnail buttons (Windows only) ─────────────────────────────

    private void SetUpThumbnailToolbar()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var handle = TryGetPlatformHandle()?.Handle;
        if (handle is null || handle == IntPtr.Zero)
            return;

        _thumbnailToolbar = new ThumbnailToolbar();
        if (_thumbnailToolbar.TryInitialize(handle.Value))
            Win32Properties.AddWndProcHookCallback(this, OnWndProc);
    }

    private IntPtr OnWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const uint wmCommand = 0x0111;
        const int thbnClicked = 0x1800;

        if (msg == wmCommand && (wParam.ToInt64() >> 16 & 0xFFFF) == thbnClicked)
        {
            var buttonId = (int)(wParam.ToInt64() & 0xFFFF);
            var vm = ViewModel;

            switch (buttonId)
            {
                case ThumbnailToolbar.ButtonIdPrevious:
                    vm?.PreviousCommand.Execute(null);
                    break;
                case ThumbnailToolbar.ButtonIdPlayPause:
                    vm?.PlayPauseCommand.Execute(null);
                    break;
                case ThumbnailToolbar.ButtonIdNext:
                    vm?.NextCommand.Execute(null);
                    break;
            }

            handled = true;
        }

        return IntPtr.Zero;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_observedViewModel is not null)
            _observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _observedViewModel = ViewModel;
        if (_observedViewModel is null)
            return;

        _observedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _observedViewModel.VisualizerFeed.WaveformReady += OnWaveformReady;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsPlaying))
            _thumbnailToolbar?.SetPlaying(ViewModel?.IsPlaying ?? false);
    }

    private void OnWaveformReady() => WaveformBar.SetWaveform(ViewModel?.VisualizerFeed.Waveform);

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    // ── Window chrome ────────────────────────────────────────────────────────

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximized();

    private void OnMinimizeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ToggleMaximized();

    private void OnCloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty)
        {
            var maximized = WindowState == WindowState.Maximized;
            MaximizeIcon.Data = maximized ? Icons.WindowRestore : Icons.WindowMaximize;
            ResizeGrips.IsVisible = !maximized;
        }
    }

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState != WindowState.Normal)
            return;
        if (sender is Control { Tag: string edgeName } && Enum.TryParse<WindowEdge>(edgeName, out var edge))
            BeginResizeDrag(edge, e);
    }

    /// <summary>Keeps the artwork and the ring around it proportional to the viewport.</summary>
    private void OnViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var shortSide = Math.Min(e.NewSize.Width, e.NewSize.Height);
        var artSize = Math.Clamp(shortSide * 0.26, 64, 260);

        ArtFrame.Width = artSize;
        ArtFrame.Height = artSize;

        // Half the art's diagonal plus a gap, so bars never overlap the cover as it scales.
        Visualizer.InnerRadius = artSize * 0.5 * 1.30 + 12;
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only a click on the backdrop itself dismisses; clicks inside the card bubble up here too.
        if (ReferenceEquals(e.Source, SettingsScrim))
            ViewModel?.CloseSettingsCommand.Execute(null);
    }

    // ── Library / queue ──────────────────────────────────────────────────────

    private async void OnOpenLibraryClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose your music library folder",
            AllowMultiple = false,
        });

        var folder = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (folder is not null)
            ViewModel?.LoadLibrary(folder);
    }

    private void OnLibraryTapped(object? sender, TappedEventArgs e)
    {
        if (RowFrom(e) is { } row)
            ViewModel?.PlaySongCommand.Execute(row);
    }

    private void OnQueueTapped(object? sender, TappedEventArgs e)
    {
        if (RowFrom(e) is { } row)
            ViewModel?.JumpToQueueRowCommand.Execute(row);
    }

    private void OnSourceTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as StyledElement)?.DataContext is LibrarySourceRow source)
            ViewModel?.SelectSourceCommand.Execute(source);
    }

    /// <summary>Everything inside an item template inherits the row as its DataContext; taps on
    /// empty list space resolve to the window's view model instead and are ignored.</summary>
    private static SongRow? RowFrom(TappedEventArgs e) =>
        (e.Source as StyledElement)?.DataContext as SongRow;

    private void OnProgressPointerPressed(object? sender, PointerPressedEventArgs e) =>
        ViewModel?.BeginSeek();

    private void OnProgressPointerReleased(object? sender, PointerReleasedEventArgs e) =>
        ViewModel?.EndSeek(ProgressSlider.Value);

    /// <summary>Covers the case where the drag ends without a release we can see (e.g. focus loss).</summary>
    private void OnProgressCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
        ViewModel?.EndSeek(ProgressSlider.Value);

    // ── Animation loop ───────────────────────────────────────────────────────

    private void OnFrameTick(object? sender, EventArgs e)
    {
        var vm = ViewModel;
        if (vm is null)
            return;

        vm.TickProgress();

        var frame = vm.VisualizerFeed.GetFrame(vm.ProgressSeconds, SpectrumBins);
        if (frame is not null && vm.IsPlaying)
        {
            Visualizer.UpdateSpectrum(frame.DisplayBins);

            // Pulses are sized by how hard the hit was, so the cover tracks the track's dynamics
            // instead of punching identically on every detected onset.
            var kick = _kickDetector.Update(frame.LowBandEnergy);
            if (kick.IsHit)
                _kickPulse = Math.Max(_kickPulse, kick.Strength);

            var hihat = _hihatDetector.Update(frame.HighBandEnergy);
            if (hihat.IsHit)
                _hihatPulse = Math.Max(_hihatPulse, hihat.Strength);
        }

        _kickPulse *= (float)PulseDecayPerTick;
        _hihatPulse *= (float)PulseDecayPerTick;

        if (!vm.Theme.AnimateAlbumArt)
        {
            _artTranslate.X = 0;
            _artTranslate.Y = 0;
            _artRotate.Angle = 0;
            _artScale.ScaleX = 1;
            _artScale.ScaleY = 1;
            return;
        }

        _driftPhase += 0.018;
        var driftX = Math.Sin(_driftPhase * 0.7) * DriftPixels;
        var driftY = Math.Cos(_driftPhase * 0.5) * DriftPixels;

        // A kick also shoves the cover down a touch, which sells the impact more than scale alone.
        _artTranslate.X = driftX;
        _artTranslate.Y = driftY + _kickPulse * KickShiftPixels;
        _artRotate.Angle = Math.Sin(_driftPhase * 0.33) * MaxRotationDegrees;

        // Kicks stretch horizontally (with a little vertical squash for punch), hats stretch vertically.
        _artScale.ScaleX = 1 + _kickPulse * KickStretch;
        _artScale.ScaleY = 1 + _hihatPulse * HihatStretch - _kickPulse * KickSquash;
    }
}
