using CommunityToolkit.Mvvm.Input;
using Stramp.Core.Library;
using Stramp.Core.Models;

namespace Stramp.App.ViewModels;
public partial class MainWindowViewModel
{
    [RelayCommand]
    private void SelectSource(LibrarySourceRow source)
    {
        // Opening a source only browses it: the queue and playback are left alone until the
        // user presses Play (or picks a song), see StartSource.
        _activeSongs = [.. source.Songs];
        CurrentSourceName = source.Name;
        IsBrowsingSources = false;
        SearchText = "";
        UpdateSortOptionsForCurrentSource();
        UpdateDisplayedSongs();
        OnPropertyChanged(nameof(IsViewingPlaylist));
        LoadSourceCover(source);
        SavePlaybackState(force: true);
    }

    /// <summary>The source row being browsed, looked up by name so it survives a rescan.</summary>
    public LibrarySourceRow? CurrentSourceRow => LibrarySources.FirstOrDefault(source =>
        string.Equals(source.Name, CurrentSourceName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Plays the source being browsed from its top (or shuffled, per the shuffle toggle).</summary>
    [RelayCommand]
    private void PlayCurrentSource()
    {
        if (!TryTogglePlayingSource(CurrentSourceName))
            StartSource(_activeSongs, CurrentSourceName, SelectedSortOption?.Option ?? LibrarySortOption.Artist);
    }

    /// <summary>Plays a source straight from the source menu without opening it.</summary>
    [RelayCommand]
    private void PlaySourceRow(LibrarySourceRow? source)
    {
        if (source is null || TryTogglePlayingSource(source.Name))
            return;

        StartSource(source.Songs, source.Name,
            source.IsPlaylist ? LibrarySortOption.Playlist : LibrarySortOption.Artist);
    }

    /// <summary>On the source that is already playing, its Play button is just play/pause (and is
    /// drawn as such); only other sources are started from the top.</summary>
    private bool TryTogglePlayingSource(string name)
    {
        if (!_playerHasCurrentTrack ||
            !string.Equals(name, _playbackSourceName, StringComparison.OrdinalIgnoreCase))
            return false;

        PlayPause();
        return true;
    }

    /// <summary>Marks the source row that is currently playing so its buttons can show Pause.</summary>
    private void UpdateSourcePlayingState()
    {
        foreach (var source in LibrarySources)
            source.IsPlayingNow = IsPlaying &&
                string.Equals(source.Name, _playbackSourceName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Makes `songs` the source the queue draws from and starts it from the top.</summary>
    private void StartSource(IReadOnlyList<Song> songs, string name, LibrarySortOption sort)
    {
        if (songs.Count == 0)
            return;

        _playbackSongs = [.. songs];
        _playbackSourceName = name;
        _playbackSort = sort;
        UpdateSourcePlayingState();
        _queue.Build(SortedPlaybackSongs, Shuffled);
        StartNormalizationWarmup();
        LoadCurrent();
    }

    /// <summary>Points playback at whatever is being browsed. Used wherever the two are meant to
    /// be the same thing (startup, restore, rescan).</summary>
    private void SnapshotPlaybackSource()
    {
        _playbackSongs = [.. _activeSongs];
        _playbackSourceName = CurrentSourceName;
        _playbackSort = SelectedSortOption?.Option ?? LibrarySortOption.Artist;
        UpdateSourcePlayingState();
    }

    /// <summary>After the browsed playlist was edited: if it is also the playing source, its
    /// songs follow the edit.</summary>
    private void SyncPlaybackSourceWithView()
    {
        if (string.Equals(CurrentSourceName, _playbackSourceName, StringComparison.OrdinalIgnoreCase))
            _playbackSongs = [.. _activeSongs];
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

    /// <summary>The playing source in the order it was started in; queue rebuilds follow this.</summary>
    private IEnumerable<Song> SortedPlaybackSongs => GetSortedSongs(_playbackSongs, _playbackSort);

    private IEnumerable<Song> GetSortedSongs(IEnumerable<Song> songs, LibrarySortOption? sort = null)
    {
        return (sort ?? SelectedSortOption?.Option) switch
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
