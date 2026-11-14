using CommunityToolkit.Mvvm.ComponentModel;
using Stramp.App.Theming;
using Stramp.Core.Settings;

namespace Stramp.App.ViewModels;

/// <summary>Backs the theme panel's Style dropdown: the visual themes on offer and the one in use.</summary>
/// <remarks>
/// Starts on the theme stored in settings. Picking another saves its id to settings at once and calls
/// the change callback, which re-applies the theme; it never touches the palette itself.
/// </remarks>
public partial class VisualThemePicker : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly Action _onChanged;

    public IReadOnlyList<VisualTheme> Options { get; } = VisualThemes.All;

    [ObservableProperty]
    public partial VisualTheme Selected { get; set; }

    public VisualThemePicker(AppSettings settings, Action onChanged)
    {
        _settings = settings;
        _onChanged = onChanged;
        Selected = VisualThemes.Find(settings.VisualTheme);
    }

    partial void OnSelectedChanged(VisualTheme value)
    {
        _settings.VisualTheme = value.Id;
        SettingsService.Save(_settings);
        _onChanged();
    }
}
