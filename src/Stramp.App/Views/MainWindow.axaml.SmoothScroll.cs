using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Stramp.App.Services;

namespace Stramp.App.Views;

/// <summary>
/// Replaces the default instant per-notch wheel jump with a glide on every scrollable surface:
/// the settings and stats dialogs, and the library/queue lists. Each surface gets its own
/// <see cref="SmoothScrollAnimator"/>; the ListBoxes' scroll viewers live inside their default
/// template, so they are resolved from the visual tree on first use and cached from then on.
/// </summary>
public partial class MainWindow
{
    private readonly SmoothScrollAnimator _settingsScroll = new();
    private readonly SmoothScrollAnimator _statsScroll = new();
    private readonly SmoothScrollAnimator _setupWizardScroll = new();
    private readonly SmoothScrollAnimator _sourceListScroll = new();
    private readonly SmoothScrollAnimator _libraryListScroll = new();
    private readonly SmoothScrollAnimator _queueListScroll = new();
    private ScrollViewer? _sourceListScrollViewer;
    private ScrollViewer? _libraryListScrollViewer;
    private ScrollViewer? _queueListScrollViewer;

    private void AttachSmoothScrollHandlers()
    {
        SettingsScroller.AddHandler(PointerWheelChangedEvent,
            (_, e) => OnSmoothScrollWheelChanged(e, SettingsScroller, _settingsScroll),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        StatsScroller.AddHandler(PointerWheelChangedEvent,
            (_, e) => OnSmoothScrollWheelChanged(e, StatsScroller, _statsScroll),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        SetupWizardScroller.AddHandler(PointerWheelChangedEvent,
            (_, e) => OnSmoothScrollWheelChanged(e, SetupWizardScroller, _setupWizardScroll),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        SourceList.AddHandler(PointerWheelChangedEvent,
            (_, e) => OnSmoothScrollWheelChanged(e, GetOrFindScrollViewer(SourceList, ref _sourceListScrollViewer), _sourceListScroll),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        LibraryList.AddHandler(PointerWheelChangedEvent,
            (_, e) => OnSmoothScrollWheelChanged(e, GetOrFindScrollViewer(LibraryList, ref _libraryListScrollViewer), _libraryListScroll),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        QueueList.AddHandler(PointerWheelChangedEvent,
            (_, e) => OnSmoothScrollWheelChanged(e, GetOrFindScrollViewer(QueueList, ref _queueListScrollViewer), _queueListScroll),
            RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>
    /// Replaces a scroll surface's default per-notch jump with a glide: the wheel only nudges the
    /// animator's target, and <see cref="StepSmoothScrolls"/> eases the real offset toward it every
    /// frame. Marks the event handled either way, since there is nothing left for it to do here.
    /// </summary>
    private static void OnSmoothScrollWheelChanged(PointerWheelEventArgs e, ScrollViewer? scrollViewer, SmoothScrollAnimator animator)
    {
        e.Handled = true;
        if (scrollViewer is null)
            return;

        var maxOffset = scrollViewer.Extent.Height - scrollViewer.Viewport.Height;
        animator.HandleWheel(e.Delta.Y, scrollViewer.Offset.Y, maxOffset);
    }

    /// <summary>
    /// A ListBox's scroll viewer lives inside its default template, so it only exists once the
    /// template has been applied; this resolves it on first use and remembers it from then on.
    /// </summary>
    private static ScrollViewer? GetOrFindScrollViewer(Control owner, ref ScrollViewer? cache) =>
        cache ??= owner.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private void StepSmoothScrolls(double dt)
    {
        StepSmoothScroll(SettingsScroller, _settingsScroll, dt);
        StepSmoothScroll(StatsScroller, _statsScroll, dt);
        StepSmoothScroll(SetupWizardScroller, _setupWizardScroll, dt);
        StepSmoothScroll(GetOrFindScrollViewer(SourceList, ref _sourceListScrollViewer), _sourceListScroll, dt);
        StepSmoothScroll(GetOrFindScrollViewer(LibraryList, ref _libraryListScrollViewer), _libraryListScroll, dt);
        StepSmoothScroll(GetOrFindScrollViewer(QueueList, ref _queueListScrollViewer), _queueListScroll, dt);
    }

    private static void StepSmoothScroll(ScrollViewer? scrollViewer, SmoothScrollAnimator animator, double dt)
    {
        if (scrollViewer is null)
            return;

        var newOffset = animator.Step(dt, scrollViewer.Offset.Y);
        if (newOffset is { } offset)
            scrollViewer.Offset = scrollViewer.Offset.WithY(offset);
    }
}
