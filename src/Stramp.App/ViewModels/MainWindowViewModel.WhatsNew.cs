using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stramp.Core.Settings;
using Stramp.Core.Updates;

namespace Stramp.App.ViewModels;
public partial class MainWindowViewModel
{
    [ObservableProperty]
    public partial bool IsWhatsNewOpen { get; set; }

    [ObservableProperty]
    public partial string WhatsNewNotes { get; set; } = "";

    public string WhatsNewVersion => AppVersion.Current;

    /// <summary>
    /// Shows the CHANGELOG.md section for the running version exactly once, right after it changed
    /// from whatever version last ran. A fresh install (no previous version on record) stays quiet
    /// here - the setup wizard is where onboarding happens.
    /// </summary>
    private void CheckWhatsNew()
    {
        var lastSeenVersion = _settings.LastSeenVersion;
        _settings.LastSeenVersion = AppVersion.Current;
        SettingsService.Save(_settings);

        if (lastSeenVersion is null ||
            string.Equals(lastSeenVersion, AppVersion.Current, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var changelogPath = Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md");
            if (!File.Exists(changelogPath))
                return;

            var section = ChangelogReader.FindSection(File.ReadAllText(changelogPath), AppVersion.Current);
            if (string.IsNullOrWhiteSpace(section))
                return;

            WhatsNewNotes = section;
            IsWhatsNewOpen = true;
        }
        catch
        {
            // A missing or unreadable changelog shouldn't block startup over a nice-to-have popup.
        }
    }

    [RelayCommand]
    private void CloseWhatsNew() => IsWhatsNewOpen = false;
}
