using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Stramp.App.Services;
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

    // Lyrics auto-scroll. The panel eases toward the active line a fraction of the remaining
    // distance each frame, which glides into place and absorbs a run of quick line changes.
    private const double LyricsScrollEase = 0.16;
    private const double LyricsSettleDistance = 0.5;

    /// <summary>Seconds the auto-scroll keeps out of the way after a scroll by hand (~4s).</summary>
    private const double LyricsManualScrollDurationSeconds = 4.0;

    /// <summary>Share of the panel left blank top and bottom, so the end lines can still centre.</summary>
    private const double LyricsEdgePaddingFraction = 0.42;

    /// <summary>Type size as a share of the panel's width, clamped to stay readable either way.</summary>
    private const double LyricsFontScale = 0.085;
    private const double MinLyricsFontSize = 16;
    private const double MaxLyricsFontSize = 30;

    /// <summary>Distance from the right edge that reveals the otherwise hidden lyric scrollbar.</summary>
    private const double LyricsScrollbarRevealDistance = 44;

    /// <summary>Volume change per mouse wheel notch when scrolling over the volume slider.</summary>
    private const double VolumeScrollStep = 5;

    private readonly BeatDetector _kickDetector = new(sensitivity: 1.9, refractorySeconds: 7.0 / 60.0);
    private readonly BeatDetector _hihatDetector = new(sensitivity: 2.2, refractorySeconds: 3.0 / 60.0);

    private readonly ScaleTransform _artScale = new();
    private readonly RotateTransform _artRotate = new();
    private readonly TranslateTransform _artTranslate = new();

    private double _driftPhase;
    private float _kickPulse;
    private float _hihatPulse;

    private double? _lyricsScrollTarget;
    private double _lyricsEdgePadding;
    private double _lyricsManualScrollSecondsRemaining;

    private bool _isDraggingSplitter;
    private bool _isColumnAnimating;
    private double _colAnimStartLeft;
    private double _colAnimTargetLeft;
    private double _colAnimStartRight;
    private double _colAnimTargetRight;
    private DateTime _colAnimStartTime;

    private bool _isLyricsAnimating;
    private double _lyricsAnimStartPixels;
    private double _lyricsAnimTargetPixels;
    private DateTime _lyricsAnimStartTime;
    private bool _lyricsAnimShouldBeOpen;

    private bool _isFrameLoopRunning;
    private TimeSpan? _lastFrameTimestamp;

    private ThumbnailToolbar? _thumbnailToolbar;
    private WindowsWindowIcon? _windowIcon;
    private readonly MediaKeyController _mediaKeys;
    private MainWindowViewModel? _observedViewModel;
    private ThemeSettingsViewModel? _observedTheme;
    private double _lastStageShortSide;

    public MainWindow()
    {
        InitializeComponent();

        // Transform the whole framed artwork, not the image inside the clipping frame —
        // transforming the inner image just slides the picture around behind a fixed window.
        ArtFrame.RenderTransform = new TransformGroup
        {
            Children = { _artScale, _artRotate, _artTranslate },
        };

        // The Slider handles pointer events internally and marks them handled, so a normal
        // XAML event hookup never sees the press/release that bracket a scrub.
        ProgressSlider.AddHandler(PointerPressedEvent, OnProgressPointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        ProgressSlider.AddHandler(PointerReleasedEvent, OnProgressPointerReleased,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        ProgressSlider.AddHandler(PointerCaptureLostEvent, OnProgressCaptureLost,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        LyricsScroller.AddHandler(PointerWheelChangedEvent, OnLyricsWheelChanged,
            RoutingStrategies.Tunnel, handledEventsToo: true);

        VolumeSlider.AddHandler(PointerWheelChangedEvent, OnVolumeWheelChanged,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        CompactVolumeSlider.AddHandler(PointerWheelChangedEvent, OnVolumeWheelChanged,
            RoutingStrategies.Tunnel, handledEventsToo: true);

        WaveformBar.Scrubbing += fraction => ViewModel?.ScrubTo(fraction);
        WaveformBar.ScrubCompleted += fraction => ViewModel?.CompleteScrub(fraction);

        _mediaKeys = new MediaKeyController(
            this,
            () => ViewModel?.PlayPauseCommand.Execute(null),
            () => ViewModel?.NextCommand.Execute(null),
            () => ViewModel?.PreviousCommand.Execute(null),
            () => ViewModel?.Volume ?? 0,
            volume =>
            {
                if (ViewModel is { } vm)
                    vm.Volume = volume;
            });

        LeftSplitter.DragStarted += (_, _) => _isDraggingSplitter = true;
        LeftSplitter.DragDelta += OnLeftSplitterDragDelta;
        LeftSplitter.DragCompleted += (sender, e) =>
        {
            _isDraggingSplitter = false;
            OnLeftSplitterDragCompleted(sender, e);
        };
        RightSplitter.DragStarted += (_, _) => _isDraggingSplitter = true;
        RightSplitter.DragDelta += OnRightSplitterDragDelta;
        RightSplitter.DragCompleted += (sender, e) =>
        {
            _isDraggingSplitter = false;
            OnRightSplitterDragCompleted(sender, e);
        };

        Opened += (_, _) =>
        {
            StartFrameLoop();
            SetUpThumbnailToolbar();
            UpdateColumnWidths(animate: false);
            UpdateLyricsSplit(animate: false);
        };
        SizeChanged += (_, _) => UpdateColumnWidths(animate: false);
        Closed += (_, _) =>
        {
            StopFrameLoop();
            _thumbnailToolbar?.Dispose();
            _windowIcon?.Dispose();
            _mediaKeys.Dispose();
            ViewModel?.Dispose();
        };
        PropertyChanged += OnWindowPropertyChanged;
        DataContextChanged += OnDataContextChanged;
    }

    private void StartFrameLoop()
    {
        if (_isFrameLoopRunning)
            return;

        _isFrameLoopRunning = true;
        _lastFrameTimestamp = null;
        RequestAnimationFrame(OnAnimationFrame);
    }

    private void StopFrameLoop()
    {
        _isFrameLoopRunning = false;
    }

    private void OnAnimationFrame(TimeSpan timestamp)
    {
        if (!_isFrameLoopRunning)
            return;

        var dt = _lastFrameTimestamp is { } last
            ? (timestamp - last).TotalSeconds
            : 1.0 / 60.0;
        _lastFrameTimestamp = timestamp;

        // Guard against spikes from suspension, modal dialogs, or window dragging
        dt = Math.Clamp(dt, 0.0001, 0.1);

        OnFrameTick(dt);

        if (_isFrameLoopRunning)
            RequestAnimationFrame(OnAnimationFrame);
    }

    // ── Taskbar thumbnail buttons (Windows only) ─────────────────────────────

    private void SetUpThumbnailToolbar()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var handle = TryGetPlatformHandle()?.Handle;
        if (handle is null || handle == IntPtr.Zero)
            return;

        _windowIcon = new WindowsWindowIcon();
        _windowIcon.TryApply(handle.Value,
            Path.Combine(AppContext.BaseDirectory, "Assets", "Stramp.ico"));

        _thumbnailToolbar = new ThumbnailToolbar();
        _thumbnailToolbar.TryInitialize(handle.Value);
        Win32Properties.AddWndProcHookCallback(this, OnWndProc);

        _mediaKeys.AttachWindowsMediaKeys(handle.Value, ViewModel?.IsPlaying ?? false);
    }

    private IntPtr OnWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const uint wmCommand = 0x0111;
        const int thbnClicked = 0x1800;

        if (_windowIcon?.TryHandleMessage(msg, wParam, out var icon) == true)
        {
            handled = true;
            return icon;
        }

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
        {
            _observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _observedViewModel.LyricLines.CollectionChanged -= OnLyricLinesChanged;
        }

        if (_observedTheme is not null)
        {
            _observedTheme.PropertyChanged -= OnThemePropertyChanged;
        }

        _observedViewModel = ViewModel;
        if (_observedViewModel is null)
            return;

        _observedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _observedViewModel.VisualizerFeed.WaveformReady += OnWaveformReady;
        _observedViewModel.LyricLines.CollectionChanged += OnLyricLinesChanged;

        _observedTheme = _observedViewModel.Theme;
        if (_observedTheme is not null)
            _observedTheme.PropertyChanged += OnThemePropertyChanged;

        if (_isImmersiveFullScreen)
        {
            _savedIsLibraryOpen = _observedViewModel.IsLibraryOpen;
            _savedIsUpNextOpen = _observedViewModel.IsUpNextOpen;
            _observedViewModel.IsLibraryOpen = false;
            _observedViewModel.IsUpNextOpen = false;
        }

        UpdateColumnWidths(animate: false);
        UpdateLyricsSplit(animate: false);
        UpdateStageArtworkAndVisualizer();
    }

    private void OnThemePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ThemeSettingsViewModel.CircularAlbumArt))
        {
            UpdateStageArtworkAndVisualizer();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsPlaying))
        {
            _thumbnailToolbar?.SetPlaying(ViewModel?.IsPlaying ?? false);
            _mediaKeys.SetPlaybackState(ViewModel?.IsPlaying ?? false);
        }
        else if (e.PropertyName is nameof(MainWindowViewModel.IsLibraryOpen) or
                 nameof(MainWindowViewModel.IsUpNextOpen) or
                 nameof(MainWindowViewModel.LeftPanelWidth) or
                 nameof(MainWindowViewModel.RightPanelWidth))
        {
            UpdateColumnWidths(animate: true);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsLyricsOpen))
        {
            UpdateLyricsSplit(animate: true);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsLyricsSynced))
        {
            Dispatcher.UIThread.Post(UpdateLyricsPadding, DispatcherPriority.Background);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.ActiveLyricLine))
        {
            // Posted rather than called: the container for a line that just scrolled into the
            // list may not have been measured yet, and its bounds are what we scroll by.
            Dispatcher.UIThread.Post(ScrollActiveLyricIntoView, DispatcherPriority.Background);
        }
    }

    // ── Lyrics ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Gives the lyrics column half the card, which is what pushes the cover and visualizer into
    /// the left half. Closed, the column collapses to nothing and the stage fills the card again.
    /// </summary>
    private void UpdateLyricsSplit(bool animate = true)
    {
        if (ViewModel is not { } vm || NowPlayingSplit.ColumnDefinitions.Count < 2)
            return;

        var lyricsCol = NowPlayingSplit.ColumnDefinitions[1];
        bool shouldBeOpen = vm.IsLyricsOpen;
        bool disableAnimations = vm.Theme.DisableAnimations || !animate;

        if (disableAnimations)
        {
            StopLyricsAnimation();
            lyricsCol.Width = shouldBeOpen ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            return;
        }

        double totalWidth = NowPlayingSplit.Bounds.Width;
        if (totalWidth <= 0)
            totalWidth = NowPlayingCard.Bounds.Width;
        if (totalWidth <= 0)
            totalWidth = 600;

        double targetPixels = shouldBeOpen ? totalWidth * 0.5 : 0;
        double startPixels;

        if (lyricsCol.Width.IsStar)
        {
            startPixels = lyricsCol.Width.Value * (totalWidth * 0.5);
        }
        else if (lyricsCol.Width.IsAbsolute)
        {
            startPixels = lyricsCol.Width.Value;
        }
        else
        {
            startPixels = 0;
        }

        if (Math.Abs(startPixels - targetPixels) < 1)
        {
            StopLyricsAnimation();
            lyricsCol.Width = shouldBeOpen ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            return;
        }

        StopLyricsAnimation();

        _lyricsAnimStartPixels = startPixels;
        _lyricsAnimTargetPixels = targetPixels;
        _lyricsAnimStartTime = DateTime.UtcNow;
        _lyricsAnimShouldBeOpen = shouldBeOpen;
        _isLyricsAnimating = true;
    }

    private bool IsPanelAnimating => _isColumnAnimating || _isLyricsAnimating;

    private void StopLyricsAnimation()
    {
        if (_isLyricsAnimating)
        {
            _isLyricsAnimating = false;
            RefreshLyricsLayoutAfterAnimation();
        }
    }

    private void StepLyricsPanelAnimation()
    {
        if (!_isLyricsAnimating || NowPlayingSplit.ColumnDefinitions.Count < 2)
            return;

        var lyricsCol = NowPlayingSplit.ColumnDefinitions[1];

        const double durationMs = 250.0;
        var elapsed = (DateTime.UtcNow - _lyricsAnimStartTime).TotalMilliseconds;
        double t = Math.Clamp(elapsed / durationMs, 0.0, 1.0);
        double easedT = t * t * (3 - 2 * t);

        double currPixels = _lyricsAnimStartPixels + (_lyricsAnimTargetPixels - _lyricsAnimStartPixels) * easedT;

        if (currPixels > 1)
            lyricsCol.Width = new GridLength(currPixels, GridUnitType.Pixel);
        else
            lyricsCol.Width = new GridLength(0);

        if (t >= 1.0)
        {
            var shouldBeOpen = _lyricsAnimShouldBeOpen;
            StopLyricsAnimation();
            lyricsCol.Width = shouldBeOpen ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        }
    }

    private void RefreshLyricsLayoutAfterAnimation()
    {
        if (LyricsScroller is null || LyricsItems is null)
            return;

        var width = LyricsScroller.Bounds.Width;
        if (width > 0)
        {
            LyricsItems.FontSize = Math.Clamp(
                width * LyricsFontScale, MinLyricsFontSize, MaxLyricsFontSize);
        }
        UpdateLyricsPadding();
        Dispatcher.UIThread.Post(ScrollActiveLyricIntoView, DispatcherPriority.Background);
    }

    /// <summary>A new track clears the collection, so the panel starts reading from the top again.</summary>
    private void OnLyricLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Reset)
            return;

        LyricsScroller.Offset = default;
        _lyricsScrollTarget = null;
        _lyricsManualScrollSecondsRemaining = 0;
    }

    /// <summary>
    /// Type size follows the panel, because the lyrics only ever get half the card and big text in
    /// a narrow column would wrap every couple of words.
    /// </summary>
    private void OnLyricsViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (IsPanelAnimating)
            return;

        LyricsItems.FontSize = Math.Clamp(
            e.NewSize.Width * LyricsFontScale, MinLyricsFontSize, MaxLyricsFontSize);
        UpdateLyricsPadding();
    }

    /// <summary>
    /// Blank space above the first line and below the last, so they can reach the middle of the
    /// panel like every other line. Only a synced list scrolls itself, so only it gets the padding;
    /// an unsynced sheet would just open on a screenful of nothing.
    /// </summary>
    private void UpdateLyricsPadding()
    {
        var padding = ViewModel?.IsLyricsSynced == true
            ? LyricsScroller.Bounds.Height * LyricsEdgePaddingFraction
            : 0;

        if (Math.Abs(padding - _lyricsEdgePadding) > 0.5)
        {
            _lyricsEdgePadding = padding;
            LyricsItems.Margin = new Thickness(0, padding, 0, padding);
        }

        // Posted: the new padding changes the scrollable extent, which the target is clamped to.
        Dispatcher.UIThread.Post(ScrollActiveLyricIntoView, DispatcherPriority.Background);
    }

    /// <summary>
    /// Aims the panel at the active line. The move itself is eased over the following frames by
    /// StepLyricsScroll rather than applied here, so the list glides instead of snapping.
    /// </summary>
    private void ScrollActiveLyricIntoView()
    {
        if (_lyricsManualScrollSecondsRemaining > 0 || IsPanelAnimating)
            return;

        if (ViewModel is not { IsLyricsSynced: true } vm || vm.ActiveLyricLine is not { } active)
            return;

        var index = vm.LyricLines.IndexOf(active);
        if (index < 0 || LyricsItems.ContainerFromIndex(index) is not { } container)
            return;

        var viewport = LyricsScroller.Viewport.Height;
        if (viewport <= 0)
            return;

        // Bounds are relative to the items panel, which the padding offsets and scrolling never
        // moves — so this target stays correct even while an earlier glide is still running.
        var lineCentre = _lyricsEdgePadding + container.Bounds.Top + container.Bounds.Height / 2;
        var maxOffset = Math.Max(0, LyricsScroller.Extent.Height - viewport);
        _lyricsScrollTarget = Math.Clamp(lineCentre - viewport / 2, 0, maxOffset);
    }

    /// <summary>Moves the panel a fraction of the way to its target, once per animation frame.</summary>
    private void StepLyricsScroll(double dt)
    {
        if (IsPanelAnimating)
            return;

        // Scrolling by hand takes over; the panel only takes itself back once the user has stopped.
        if (_lyricsManualScrollSecondsRemaining > 0)
        {
            _lyricsManualScrollSecondsRemaining -= dt;
            if (_lyricsManualScrollSecondsRemaining <= 0)
            {
                _lyricsManualScrollSecondsRemaining = 0;
                ScrollActiveLyricIntoView();
            }
        }

        if (_lyricsScrollTarget is not { } target)
            return;

        var current = LyricsScroller.Offset.Y;
        var remaining = target - current;
        if (Math.Abs(remaining) < LyricsSettleDistance)
        {
            LyricsScroller.Offset = LyricsScroller.Offset.WithY(target);
            _lyricsScrollTarget = null;
            return;
        }

        var dtRatio = dt * 60.0;
        var ease = 1.0 - Math.Pow(1.0 - LyricsScrollEase, dtRatio);
        LyricsScroller.Offset = LyricsScroller.Offset.WithY(current + remaining * ease);
    }

    /// <summary>A scroll by hand parks the auto-scroll, so the two never fight over the panel.</summary>
    private void OnLyricsWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        _lyricsManualScrollSecondsRemaining = LyricsManualScrollDurationSeconds;
        _lyricsScrollTarget = null;
    }

    private void OnVolumeWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        var delta = e.Delta.Y * VolumeScrollStep;
        vm.Volume = Math.Clamp(vm.Volume + delta, 0, 100);
        e.Handled = true;
    }

    private void OnLyricsPointerMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetCurrentPoint(LyricsScroller);
        var nearScrollbar = point.Position.X >=
            Math.Max(0, LyricsScroller.Bounds.Width - LyricsScrollbarRevealDistance);
        var keepVisibleWhileDragging =
            LyricsScroller.Classes.Contains("scrollbar-near") && point.Properties.IsLeftButtonPressed;
        SetLyricsScrollbarVisible(nearScrollbar || keepVisibleWhileDragging);
    }

    private void OnLyricsPointerExited(object? sender, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(LyricsScroller).Properties.IsLeftButtonPressed)
            SetLyricsScrollbarVisible(false);
    }

    private void OnLyricsPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var position = e.GetPosition(LyricsScroller);
        SetLyricsScrollbarVisible(position.X >=
            Math.Max(0, LyricsScroller.Bounds.Width - LyricsScrollbarRevealDistance));
    }

    private void OnLyricsPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!LyricsScroller.IsPointerOver)
            SetLyricsScrollbarVisible(false);
    }

    private void SetLyricsScrollbarVisible(bool visible) =>
        LyricsScroller.Classes.Set("scrollbar-near", visible);

    /// <summary>Clicking a timed line jumps playback to it; unsynced lines carry no time to jump to.</summary>
    private void OnLyricTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as StyledElement)?.DataContext is LyricLineRow line)
            ViewModel?.SeekToLyricCommand.Execute(line);
    }

    private ColumnDefinition? LeftColumn =>
        MainContentGrid?.ColumnDefinitions.Count > 0 ? MainContentGrid.ColumnDefinitions[0] : null;

    private ColumnDefinition? RightColumn =>
        MainContentGrid?.ColumnDefinitions.Count > 4 ? MainContentGrid.ColumnDefinitions[4] : null;

    private void UpdateColumnWidths(bool animate = true)
    {
        if (ViewModel is not { } vm || LeftColumn is not { } leftCol || RightColumn is not { } rightCol)
            return;

        var availableWidth = Bounds.Width > 0 ? Bounds.Width - 32 : Width - 32;
        const double minCenterWidth = 260;
        const double splitterWidth = 8;

        double leftRequested = vm.IsLibraryOpen ? Math.Max(160, vm.LeftPanelWidth) : 0;
        double rightRequested = vm.IsUpNextOpen ? Math.Max(160, vm.RightPanelWidth) : 0;

        double splittersTotal = (vm.IsLibraryOpen ? splitterWidth : 0) + (vm.IsUpNextOpen ? splitterWidth : 0);
        double availableForSides = Math.Max(0, availableWidth - minCenterWidth - splittersTotal);
        double totalRequestedSides = leftRequested + rightRequested;

        double leftTarget = leftRequested;
        double rightTarget = rightRequested;

        if (totalRequestedSides > availableForSides && totalRequestedSides > 0)
        {
            double scale = availableForSides / totalRequestedSides;
            leftTarget *= scale;
            rightTarget *= scale;
        }

        bool disableAnimations = vm.Theme.DisableAnimations || _isDraggingSplitter || !animate;

        if (disableAnimations)
        {
            StopColumnAnimation();
            ApplyLeftColumnWidth(leftTarget);
            ApplyRightColumnWidth(rightTarget);
            return;
        }

        double startLeft = leftCol.Width.IsAbsolute ? leftCol.Width.Value : 0;
        double startRight = rightCol.Width.IsAbsolute ? rightCol.Width.Value : 0;

        if (Math.Abs(startLeft - leftTarget) < 1 && Math.Abs(startRight - rightTarget) < 1)
        {
            StopColumnAnimation();
            ApplyLeftColumnWidth(leftTarget);
            ApplyRightColumnWidth(rightTarget);
            return;
        }

        StopColumnAnimation();

        _colAnimStartLeft = startLeft;
        _colAnimTargetLeft = leftTarget;
        _colAnimStartRight = startRight;
        _colAnimTargetRight = rightTarget;
        _colAnimStartTime = DateTime.UtcNow;
        _isColumnAnimating = true;
    }

    private void StopColumnAnimation()
    {
        if (_isColumnAnimating)
        {
            _isColumnAnimating = false;
            RefreshLyricsLayoutAfterAnimation();
        }
    }

    private void StepColumnAnimation()
    {
        if (!_isColumnAnimating)
            return;

        const double durationMs = 250.0;
        var elapsed = (DateTime.UtcNow - _colAnimStartTime).TotalMilliseconds;
        double t = Math.Clamp(elapsed / durationMs, 0.0, 1.0);
        double easedT = t * t * (3 - 2 * t);

        double currLeft = _colAnimStartLeft + (_colAnimTargetLeft - _colAnimStartLeft) * easedT;
        double currRight = _colAnimStartRight + (_colAnimTargetRight - _colAnimStartRight) * easedT;

        ApplyLeftColumnWidth(currLeft);
        ApplyRightColumnWidth(currRight);

        if (t >= 1.0)
        {
            var leftTarget = _colAnimTargetLeft;
            var rightTarget = _colAnimTargetRight;
            StopColumnAnimation();
            ApplyLeftColumnWidth(leftTarget);
            ApplyRightColumnWidth(rightTarget);
        }
    }

    private void ApplyLeftColumnWidth(double width)
    {
        if (LeftColumn is not { } leftCol) return;
        if (width > 5)
        {
            leftCol.Width = new GridLength(width);
            LeftSplitter.IsVisible = true;
        }
        else
        {
            leftCol.Width = new GridLength(0);
            LeftSplitter.IsVisible = false;
        }
    }

    private void ApplyRightColumnWidth(double width)
    {
        if (RightColumn is not { } rightCol) return;
        if (width > 5)
        {
            rightCol.Width = new GridLength(width);
            RightSplitter.IsVisible = true;
        }
        else
        {
            rightCol.Width = new GridLength(0);
            RightSplitter.IsVisible = false;
        }
    }

    private void OnLeftSplitterDragDelta(object? sender, VectorEventArgs e)
    {
        if (ViewModel is { } vm && LeftColumn is { Width: { IsAbsolute: true, Value: > 0 } w })
            vm.LeftPanelWidth = w.Value;
    }

    private void OnLeftSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        if (ViewModel is { } vm && LeftColumn is { Width: { IsAbsolute: true, Value: > 0 } w })
            vm.LeftPanelWidth = w.Value;
    }

    private void OnRightSplitterDragDelta(object? sender, VectorEventArgs e)
    {
        if (ViewModel is { } vm && RightColumn is { Width: { IsAbsolute: true, Value: > 0 } w })
            vm.RightPanelWidth = w.Value;
    }

    private void OnRightSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        if (ViewModel is { } vm && RightColumn is { Width: { IsAbsolute: true, Value: > 0 } w })
            vm.RightPanelWidth = w.Value;
    }

    private void OnWaveformReady() => WaveformBar.SetWaveform(ViewModel?.VisualizerFeed.Waveform);

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    // ── Window chrome ────────────────────────────────────────────────────────

    private WindowState _previousWindowState = WindowState.Normal;
    private bool _savedIsLibraryOpen;
    private bool _savedIsUpNextOpen;
    private bool _isImmersiveFullScreen;

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximized();

    private void OnMinimizeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ToggleMaximized();

    private void OnToggleFullScreenClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ToggleFullScreen();

    private void OnCloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void ToggleMaximized()
    {
        if (WindowState == WindowState.FullScreen)
            WindowState = WindowState.Maximized;
        else
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void ToggleFullScreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _previousWindowState == WindowState.FullScreen ? WindowState.Normal : _previousWindowState;
        }
        else
        {
            _previousWindowState = WindowState;
            WindowState = WindowState.FullScreen;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.F11 || (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Alt)))
        {
            ToggleFullScreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && WindowState == WindowState.FullScreen)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty)
        {
            var isMaximized = WindowState == WindowState.Maximized;
            MaximizeIcon.Data = isMaximized ? Icons.WindowRestore : Icons.WindowMaximize;
            ResizeGrips.IsVisible = WindowState == WindowState.Normal;

            UpdateImmersiveFullScreenState();
        }
    }

    private void UpdateImmersiveFullScreenState()
    {
        bool isFullScreen = WindowState == WindowState.FullScreen;
        LyricsScroller.Classes.Set("immersive", isFullScreen);

        if (isFullScreen && !_isImmersiveFullScreen)
        {
            _isImmersiveFullScreen = true;

            ExtendClientAreaToDecorationsHint = false;
            TitleBar.IsVisible = false;
            MainContentGrid.Margin = new Thickness(0);

            if (ViewModel is { } vm)
            {
                _savedIsLibraryOpen = vm.IsLibraryOpen;
                _savedIsUpNextOpen = vm.IsUpNextOpen;

                vm.IsLibraryOpen = false;
                vm.IsUpNextOpen = false;
            }

            UpdateColumnWidths(animate: false);
        }
        else if (!isFullScreen && _isImmersiveFullScreen)
        {
            _isImmersiveFullScreen = false;

            ExtendClientAreaToDecorationsHint = true;
            TitleBar.IsVisible = true;
            MainContentGrid.Margin = new Thickness(16, 6, 16, 16);

            if (ViewModel is { } vm)
            {
                vm.IsLibraryOpen = _savedIsLibraryOpen;
                vm.IsUpNextOpen = _savedIsUpNextOpen;
            }

            UpdateColumnWidths(animate: false);
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
        if (ViewModel is { } vm)
            vm.IsCompactControlBar = e.NewSize.Width < 420;
    }

    /// <summary>
    /// The cover and ring are sized against the stage rather than the whole card, so opening the
    /// lyrics panel shrinks them to fit the left half instead of spilling under the text.
    /// </summary>
    private void OnStageSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        _lastStageShortSide = Math.Min(e.NewSize.Width, e.NewSize.Height);
        UpdateStageArtworkAndVisualizer();
    }

    private void UpdateStageArtworkAndVisualizer()
    {
        if (_lastStageShortSide <= 0)
            return;

        var baseArtSize = Math.Clamp(_lastStageShortSide * 0.26, 64, 260);
        var targetRingRadius = baseArtSize * 0.5 * 1.30 + 12;
        var isCircular = ViewModel?.Theme.CircularAlbumArt ?? true;

        Visualizer.InnerRadius = targetRingRadius;

        if (isCircular)
        {
            var circleDiameter = Math.Max(32, (targetRingRadius - 18) * 2);
            ArtFrame.Width = circleDiameter;
            ArtFrame.Height = circleDiameter;
        }
        else
        {
            ArtFrame.Width = baseArtSize;
            ArtFrame.Height = baseArtSize;
        }
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only a click on the backdrop itself dismisses; clicks inside the card bubble up here too.
        if (ReferenceEquals(e.Source, SettingsScrim))
            ViewModel?.CloseSettingsCommand.Execute(null);
    }

    // ── Library / queue ──────────────────────────────────────────────────────

    private void OnSongContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container.DataContext is SongRow row)
            ViewModel?.LoadRowArt(row);
    }

    private void OnSongContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        if (e.Container.DataContext is SongRow row)
            ViewModel?.ReleaseRowArt(row);
    }

    private async void OnOpenLibraryClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add a music library folder",
            AllowMultiple = false,
        });

        var folder = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (folder is not null)
            ViewModel?.AddLibraryLocation(folder);
    }

    private async void OnImportPlaylistClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import playlist",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Playlist")
                {
                    Patterns = ["*.m3u", "*.m3u8"],
                    MimeTypes = ["audio/x-mpegurl", "application/vnd.apple.mpegurl"],
                },
            ],
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is null || ViewModel is null)
            return;

        var error = ViewModel.ImportPlaylist(path);
        if (error is not null)
            ViewModel.StatusText = error;
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

    private void OnFrameTick(double dt)
    {
        var vm = ViewModel;
        if (vm is null)
            return;

        vm.TickProgress();
        StepColumnAnimation();
        StepLyricsPanelAnimation();
        StepLyricsScroll(dt);

        var frame = vm.VisualizerFeed.GetFrame(vm.ProgressSeconds, SpectrumBins);
        if (frame is not null && vm.IsPlaying)
        {
            Visualizer.UpdateSpectrum(frame.DisplayBins, dt);

            // Pulses are sized by how hard the hit was, so the cover tracks the track's dynamics
            // instead of punching identically on every detected onset.
            var kick = _kickDetector.Update(frame.LowBandEnergy, dt);
            if (kick.IsHit)
                _kickPulse = Math.Max(_kickPulse, kick.Strength);

            var hihat = _hihatDetector.Update(frame.HighBandEnergy, dt);
            if (hihat.IsHit)
                _hihatPulse = Math.Max(_hihatPulse, hihat.Strength);
        }

        var dtRatio = dt * 60.0;
        var pulseDecay = (float)Math.Pow(PulseDecayPerTick, dtRatio);
        _kickPulse *= pulseDecay;
        _hihatPulse *= pulseDecay;

        if (!vm.Theme.AnimateAlbumArt)
        {
            _artTranslate.X = 0;
            _artTranslate.Y = 0;
            _artRotate.Angle = 0;
            _artScale.ScaleX = 1;
            _artScale.ScaleY = 1;
            return;
        }

        _driftPhase += 0.018 * dtRatio;
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
