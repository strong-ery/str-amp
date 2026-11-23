using CommunityToolkit.Mvvm.Input;

namespace Stramp.App.ViewModels;
public partial class MainWindowViewModel
{
    [RelayCommand]
    private void AddToQueue(SongRow row)
    {
        var wasEmpty = _queue.Current is null;
        _queue.AddToQueue(row.Song);
        if (wasEmpty)
            LoadCurrent(autoPlay: false);
        else
            RefreshQueueRows();
        StartNormalizationWarmup();
        SavePlaybackState(force: true);
        StatusText = $"Added '{row.Title}' to the queue.";
    }

    [RelayCommand]
    private void JumpToQueueRow(SongRow row)
    {
        var index = _queue.Position + QueueRows.IndexOf(row);
        _queue.JumpTo(index);
        LoadCurrent();
    }

    [RelayCommand]
    private void RemoveFromQueue(SongRow row)
    {
        var index = _queue.Position + QueueRows.IndexOf(row);
        _queue.RemoveAt(index);
        RefreshQueueRows();
        SavePlaybackState(force: true);
    }
}
