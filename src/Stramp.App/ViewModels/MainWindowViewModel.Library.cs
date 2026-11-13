using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stramp.App.Services;
using Stramp.Audio;
using Stramp.Core.Library;
using Stramp.Core.Lyrics;
using Stramp.Core.Models;
using Stramp.Core.Playback;
using Stramp.Core.Settings;
using Stramp.Integrations.Windows;

namespace Stramp.App.ViewModels;
public partial class MainWindowViewModel
{
    /// <summary>Legacy single-folder entry point. Adds the folder to the combined library.</summary>
    public void LoadLibrary(string directory)
    {
        AddLibraryLocation(directory);
    }

    public void AddLibraryLocation(string directory)
    {
        var normalized = NormalizeDirectory(directory);
        if (normalized is null || !Directory.Exists(normalized))
        {
            StatusText = "That library folder is not available.";
            return;
        }

        if (_libraryPaths.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            StatusText = "That folder is already in your library sources.";
            return;
        }

        _libraryPaths.Add(normalized);
        LibraryPath = _libraryPaths[0];
        PersistLibraryLocations();
        RefreshLibraryLocations();
        OnPropertyChanged(nameof(EffectivePlaylistsDirectory));
        _ = ReloadLibraryAsync(restorePlaybackState: false, preservePlayback: true);
    }

    [RelayCommand]
    private void RemoveLibraryLocation(LibraryLocationRow location)
    {
        var removed = _libraryPaths.RemoveAll(path => string.Equals(
            path, location.Path, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
            return;

        LibraryPath = _libraryPaths.FirstOrDefault() ?? "";
        PersistLibraryLocations();
        RefreshLibraryLocations();
        OnPropertyChanged(nameof(EffectivePlaylistsDirectory));
        _ = ReloadLibraryAsync(restorePlaybackState: false, preservePlayback: true);
    }

    private void PersistLibraryLocations()
    {
        _settings.LibraryPaths = [.. _libraryPaths];
        _settings.LibraryPath = _libraryPaths.FirstOrDefault();
        SettingsService.Save(_settings);
    }

    private void RefreshLibraryLocations()
    {
        LibraryLocations.Clear();
        foreach (var path in _libraryPaths)
            LibraryLocations.Add(new LibraryLocationRow(path));
    }

    private static string? NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var fullPath = Path.GetFullPath(path.Trim());
            var root = Path.GetPathRoot(fullPath);
            return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                          or PathTooLongException)
        {
            return null;
        }
    }

}
