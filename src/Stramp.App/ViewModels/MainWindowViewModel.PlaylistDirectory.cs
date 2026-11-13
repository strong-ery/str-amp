using CommunityToolkit.Mvvm.Input;
using Stramp.Core.Library;
using Stramp.Core.Settings;

namespace Stramp.App.ViewModels;
public partial class MainWindowViewModel
{
    public string EffectivePlaylistsDirectory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_settings.PlaylistsDirectory))
                return _settings.PlaylistsDirectory;

            var libraryRoot = _libraryPaths.FirstOrDefault(Directory.Exists);
            if (!string.IsNullOrEmpty(libraryRoot))
                return Path.Combine(libraryRoot, "Playlists");

            return Path.Combine(SettingsService.ConfigDirectory, "playlists");
        }
    }

    public bool HasCustomPlaylistsDirectory => !string.IsNullOrWhiteSpace(_settings.PlaylistsDirectory);

    public void SetPlaylistsDirectory(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            _settings.PlaylistsDirectory = null;
        else
            _settings.PlaylistsDirectory = Path.GetFullPath(folderPath);

        SettingsService.Save(_settings);
        OnPropertyChanged(nameof(EffectivePlaylistsDirectory));
        OnPropertyChanged(nameof(HasCustomPlaylistsDirectory));

        EnsurePlaylistsOnDisk();
        PopulateSources();
    }

    [RelayCommand]
    public void ResetPlaylistsDirectory() => SetPlaylistsDirectory(null);

    public void EnsurePlaylistsOnDisk()
    {
        try
        {
            var dir = EffectivePlaylistsDirectory;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            bool changed = false;
            foreach (var saved in _settings.Playlists)
            {
                if (string.IsNullOrWhiteSpace(saved.FilePath) || !File.Exists(saved.FilePath))
                {
                    saved.FilePath = Path.Combine(dir, $"{SanitizeFileName(saved.Name)}.m3u8");
                    changed = true;
                }

                if (!File.Exists(saved.FilePath))
                {
                    var resolvedSongs = PlaylistScanner.ResolveSongs(saved.SongPaths, _library);
                    PlaylistWriter.Write(saved.FilePath, resolvedSongs);
                }
            }

            if (changed)
                SettingsService.Save(_settings);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not write playlist files to disk: {ex.Message}";
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var clean = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "Playlist" : clean;
    }

    private string? DetermineM3u8Path(string playlistName)
    {
        try
        {
            var dir = EffectivePlaylistsDirectory;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var safeName = SanitizeFileName(playlistName);
            return Path.Combine(dir, $"{safeName}.m3u8");
        }
        catch
        {
            return null;
        }
    }

}
