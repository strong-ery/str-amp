using CommunityToolkit.Mvvm.ComponentModel;

namespace Stramp.App.ViewModels;
public partial class MainWindowViewModel
{
    /// <summary>Drives the status toast's fade: true while a message is showing, false once its
    /// few seconds are up. See <see cref="OnStatusTextChanged"/>.</summary>
    [ObservableProperty]
    public partial bool IsStatusVisible { get; set; }

    private CancellationTokenSource? _statusVisibilityCts;

    /// <summary>Shows the toast for a few seconds then fades it out; a message that arrives before
    /// the previous one finished just restarts the timer instead of stacking.</summary>
    partial void OnStatusTextChanged(string value)
    {
        _statusVisibilityCts?.Cancel();

        if (string.IsNullOrEmpty(value))
        {
            IsStatusVisible = false;
            return;
        }

        IsStatusVisible = true;
        var cts = new CancellationTokenSource();
        _statusVisibilityCts = cts;
        _ = HideStatusAfterDelayAsync(value.Length, cts.Token);
    }

    private async Task HideStatusAfterDelayAsync(int messageLength, CancellationToken ct)
    {
        try
        {
            // Longer messages (mostly errors) need longer to read.
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(3.5 + messageLength * 0.04, 9)), ct);
            IsStatusVisible = false;
        }
        catch (TaskCanceledException)
        {
            // Superseded by a newer status message; that one owns the timer now.
        }
    }
}
