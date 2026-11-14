using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stramp.App.Services;
using Stramp.Core.Models;
using Stramp.Core.Settings;
using Stramp.Core.Spotify;

namespace Stramp.App.ViewModels;

/// <summary>What <see cref="SpotifyImportViewModel"/> needs from its owner.</summary>
/// <remarks>
/// <see cref="Library"/> is read at import time so the latest scan is matched; <see cref="SavePlaylist"/>
/// stores and opens an imported playlist; <see cref="ShowStatus"/> reports what happens outside the
/// dialog, a finished import or a dashboard that won't open; <see cref="OpenSettings"/> shows Settings.
/// </remarks>
public sealed record SpotifyImportCallbacks(
    Func<IReadOnlyList<Song>> Library,
    Action<string, IReadOnlyList<Song>> SavePlaylist,
    Action<string> ShowStatus,
    Action OpenSettings);

/// <summary>Backs Spotify playlist import: its settings, signing in, and the import dialog.</summary>
/// <remarks>
/// <see cref="IsEnabled"/> and <see cref="ClientId"/> are saved to settings as they change; turning the
/// integration off or changing the client id signs out, since a sign-in belongs to one Spotify app. The
/// dialog shows one step at a time: add a client id, sign in, then import. Opening it checks a saved
/// sign-in without ever opening the browser. Only one sign-in or import runs at a time, closing the
/// dialog cancels it (including a sign-in waiting on the browser), and every failure is shown in the dialog.
/// </remarks>
public partial class SpotifyImportViewModel : ViewModelBase
{
    private static readonly Uri Dashboard = new("https://developer.spotify.com/dashboard");

    private readonly AppSettings _settings;
    private readonly SpotifyImportCallbacks _callbacks;
    private readonly SpotifyPlaylistImporter _importer;
    private CancellationTokenSource? _running;

    public SpotifyImportViewModel(AppSettings settings, SpotifyImportCallbacks callbacks)
    {
        _settings = settings;
        _callbacks = callbacks;
        _importer = new SpotifyPlaylistImporter(() => _settings.SpotifyClientId, BrowserLauncher.Open);
        IsEnabled = settings.SpotifyIntegrationEnabled;
        ClientId = settings.SpotifyClientId ?? "";
    }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasClientId), nameof(NeedsClientId), nameof(CanSignIn), nameof(CanImport))]
    public partial string ClientId { get; set; }

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string Link { get; set; } = "";

    [ObservableProperty]
    public partial string PlaylistName { get; set; } = "";

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial string? Progress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanImport))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSignIn), nameof(CanImport), nameof(AccountText))]
    public partial bool IsSignedIn { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountText))]
    public partial string? AccountName { get; set; }

    public bool HasClientId => ClientId.Trim().Length > 0;
    public bool NeedsClientId => !HasClientId;
    public bool CanSignIn => HasClientId && !IsSignedIn;
    public bool CanImport => HasClientId && IsSignedIn && !IsBusy;
    public bool IsIdle => !IsBusy;

    public string AccountText => AccountName is null ? "Signed in to Spotify" : $"Signed in to Spotify as {AccountName}";

    partial void OnIsEnabledChanged(bool value)
    {
        if (value == _settings.SpotifyIntegrationEnabled)
            return;

        _settings.SpotifyIntegrationEnabled = value;
        SettingsService.Save(_settings);
        if (!value)
            SignOut();
    }

    partial void OnClientIdChanged(string value)
    {
        var trimmed = value.Trim();
        if (trimmed == (_settings.SpotifyClientId ?? ""))
            return;

        _settings.SpotifyClientId = trimmed.Length > 0 ? trimmed : null;
        SettingsService.Save(_settings);
        SignOut();
    }

    [RelayCommand]
    public async Task OpenAsync()
    {
        Link = "";
        PlaylistName = "";
        Error = null;
        IsSignedIn = _importer.IsSignedIn;
        IsOpen = true;
        if (IsSignedIn && AccountName is null)
            await RunAsync("Checking your Spotify sign-in…", async ct => AccountName = await _importer.GetSavedAccountNameAsync(ct));
    }

    [RelayCommand]
    public void Close()
    {
        _running?.Cancel();
        IsOpen = false;
    }

    [RelayCommand]
    public void OpenSettings()
    {
        Close();
        _callbacks.OpenSettings();
    }

    // Offered in Settings, where the dialog's error line isn't visible, so failure goes to the status toast.
    [RelayCommand]
    public void OpenDashboard()
    {
        try
        {
            BrowserLauncher.Open(Dashboard);
        }
        catch (Exception ex) when (Describe(ex) is { } message)
        {
            _callbacks.ShowStatus($"{message} The dashboard is at developer.spotify.com/dashboard.");
        }
    }

    [RelayCommand]
    public Task SignInAsync() =>
        RunAsync("Sign in to Spotify in the browser window that just opened…",
            async ct => AccountName = await _importer.SignInAsync(ct));

    // Not routed through RunAsync: signing out must happen even while an import is still unwinding.
    [RelayCommand]
    public void SignOut()
    {
        try
        {
            _importer.SignOut();
        }
        catch (Exception ex) when (Describe(ex) is { } message)
        {
            Error = message;
        }

        IsSignedIn = _importer.IsSignedIn;
        if (!IsSignedIn)
            AccountName = null;
    }

    [RelayCommand]
    public Task ImportAsync() => RunAsync("Reading the playlist from Spotify…", async ct =>
    {
        var result = await _importer.ImportAsync(Link, PlaylistName, _callbacks.Library(), ct);
        if (result.Songs.Count == 0)
        {
            Error = $"None of the playlist's {result.TotalTracks} songs are in your library.";
            return;
        }

        _callbacks.SavePlaylist(result.PlaylistName, result.Songs);
        IsOpen = false;
        _callbacks.ShowStatus(
            $"Imported '{result.PlaylistName}': {result.Songs.Count} of {result.TotalTracks} songs found in your library");
    });

    // Runs one Spotify operation with the dialog marked busy, and shows any failure in the dialog.
    private async Task RunAsync(string progress, Func<CancellationToken, Task> work)
    {
        if (IsBusy)
            return;

        using var running = _running = new CancellationTokenSource();
        IsBusy = true;
        Error = null;
        Progress = progress;
        try
        {
            await work(running.Token);
        }
        catch (OperationCanceledException) when (running.IsCancellationRequested)
        {
            // The user closed the dialog; there is nothing left to report.
        }
        catch (Exception ex) when (Describe(ex) is { } message)
        {
            Error = message;
        }
        finally
        {
            _running = null;
            Progress = null;
            IsBusy = false;
            IsSignedIn = _importer.IsSignedIn;
            if (!IsSignedIn)
                AccountName = null;
        }
    }

    // The message for every failure a Spotify operation is expected to produce; null lets others propagate.
    private static string? Describe(Exception ex) => ex switch
    {
        SpotifyException spotify => spotify.Message,
        HttpRequestException or TaskCanceledException => "Couldn't reach Spotify. Check your connection and try again.",
        Win32Exception => "Couldn't open your browser.",
        JsonException => "Spotify sent a reply Stramp couldn't read. Try again later.",
        IOException or UnauthorizedAccessException => $"Couldn't update the Spotify sign-in: {ex.Message}",
        _ => null,
    };
}
