using CommunityToolkit.Mvvm.Input;
using Stramp.Core.Library;
using Stramp.Core.Models;

namespace Stramp.App.ViewModels;
public partial class MainWindowViewModel
{
    [RelayCommand]
    private void SelectSource(LibrarySourceRow source)
    {
        _activeSongs = [.. source.Songs];
        CurrentSourceName = source.Name;
        IsBrowsingSources = false;
        SearchText = "";
        UpdateSortOptionsForCurrentSource();
        UpdateDisplayedSongs();
        OnPropertyChanged(nameof(IsViewingPlaylist));

        if (_activeSongs.Count > 0)
        {
            if (IsPlaying && _queue.Current is { } currentSong)
            {
                _queue.PlayFromLibrary(currentSong, SortedActiveSongs, Shuffled);
                RefreshQueueRows();
            }
            else
            {
                _queue.Build(SortedActiveSongs, Shuffled);
                LoadCurrent(autoPlay: false);
            }
            StartNormalizationWarmup();
        }

        SavePlaybackState(force: true);
    }

    public bool IsViewingPlaylist => !IsBrowsingSources && CurrentSourceKind == LibrarySourceKind.Playlist;

    private void UpdateSortOptionsForCurrentSource(string? savedSortOptionName = null)
    {
        // Only playlists carry a meaningful hand-made order; folder rows sort like the library.
        var isPlaylist = CurrentSourceKind == LibrarySourceKind.Playlist;
        var targetOptions = isPlaylist
            ? new[] { SortPlaylist, SortArtist, SortAlbum, SortSong }
            : new[] { SortArtist, SortAlbum, SortSong };

        AvailableSortOptions.Clear();
        foreach (var option in targetOptions)
            AvailableSortOptions.Add(option);

        if (!string.IsNullOrWhiteSpace(savedSortOptionName) &&
            Enum.TryParse<LibrarySortOption>(savedSortOptionName, out var parsedSortOption))
        {
            var matched = AvailableSortOptions.FirstOrDefault(o => o.Option == parsedSortOption);
            if (matched is not null)
            {
                SelectedSortOption = matched;
                return;
            }
        }

        SelectedSortOption = isPlaylist ? SortPlaylist : SortArtist;
    }

    /// <summary>The active source in the order the library list shows it. Queue rebuilds follow
    /// this so "next" matches the list the user is looking at, not raw source order.</summary>
    private IEnumerable<Song> SortedActiveSongs => GetSortedSongs(_activeSongs);

    private IEnumerable<Song> GetSortedSongs(IEnumerable<Song> songs)
    {
        return SelectedSortOption?.Option switch
        {
            LibrarySortOption.Album => songs
                .OrderBy(s => s.Album, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Artist, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase),

            LibrarySortOption.Song => songs
                .OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Artist, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Album, StringComparer.OrdinalIgnoreCase),

            LibrarySortOption.Playlist => songs,

            LibrarySortOption.Artist or _ => songs
                .OrderBy(s => string.IsNullOrWhiteSpace(s.AlbumArtist) ? s.Artist : s.AlbumArtist, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Album, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase),
        };
    }

    private void UpdateDisplayedSongs()
    {
        var sorted = SortedActiveSongs;
        var q = SearchText.Trim();
        var filtered = string.IsNullOrEmpty(q)
            ? sorted
            : LibrarySearch.Rank(sorted, q);
        PopulateLibraryRows(filtered);
    }

    [RelayCommand]
    private void BackToSources()
    {
        IsBrowsingSources = true;
        OnPropertyChanged(nameof(IsViewingPlaylist));
        SavePlaybackState(force: true);
    }

    private void PopulateLibraryRows(IEnumerable<Song> songs)
    {
        LibraryRows.ReplaceAll(songs.Select(song => new SongRow(song)));
        RefreshCurrentHighlight();
    }

    /// <summary>Called when Avalonia materializes a visible virtualized song row.</summary>
    public void LoadRowArt(SongRow row)
    {
        if (row.IsArtRequested)
            return;

        row.IsArtRequested = true;
        var requestVersion = ++row.ArtRequestVersion;
        _artProvider.GetThumbnailAsync(row.Song.Path, bitmap =>
        {
            if (row.IsArtRequested && row.ArtRequestVersion == requestVersion)
                row.ArtBitmap = bitmap;
        });
    }

    /// <summary>Releases an off-screen row while retaining a small shared thumbnail cache.</summary>
    public void ReleaseRowArt(SongRow row)
    {
        if (!row.IsArtRequested)
            return;
        row.IsArtRequested = false;
        row.ArtRequestVersion++;
        row.ArtBitmap = null;
        _artProvider.ReleaseThumbnail(row.Song.Path);
    }

    partial void OnSearchTextChanged(string value)
    {
        UpdateDisplayedSongs();
    }

}
