using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Stramp.App.ViewModels;

/// <summary>
/// Step navigation for the first-run setup wizard. Every control the wizard shows binds straight
/// to the real setting - MainWindowViewModel.Theme's properties, the library/playlist commands
/// the Settings panel already uses - so this only tracks which page is showing; nothing here
/// duplicates state that lives elsewhere.
/// </summary>
public partial class SetupWizardViewModel : ViewModelBase
{
    private static readonly SetupStep[] Steps = Enum.GetValues<SetupStep>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFirstStep))]
    [NotifyPropertyChangedFor(nameof(IsLastStep))]
    [NotifyPropertyChangedFor(nameof(NextButtonText))]
    [NotifyPropertyChangedFor(nameof(StepNumberLabel))]
    [NotifyPropertyChangedFor(nameof(StepTitle))]
    public partial SetupStep CurrentStep { get; set; } = SetupStep.Welcome;

    public string InstallDirectory { get; init; } = "";

    public bool IsFirstStep => CurrentStep == Steps[0];
    public bool IsLastStep => CurrentStep == Steps[^1];
    public string NextButtonText => IsLastStep ? "Finish" : "Next";
    public string StepNumberLabel => $"Step {Array.IndexOf(Steps, CurrentStep) + 1} of {Steps.Length}";

    public string StepTitle => CurrentStep switch
    {
        SetupStep.Welcome => "Welcome to str-amp",
        SetupStep.Library => "Your Library",
        SetupStep.Playlists => "Playlists",
        SetupStep.Audio => "Audio",
        SetupStep.VisualsArt => "Album Art",
        SetupStep.VisualsTheme => "Visualizers & Theme",
        SetupStep.LyricsAndDiscord => "Lyrics & Discord",
        SetupStep.Shortcuts => "Shortcuts",
        SetupStep.ListeningStats => "Listening Stats",
        _ => "Setup",
    };

    /// <summary>Raised once, when Finish is clicked on the last step.</summary>
    public event Action? Finished;

    [RelayCommand]
    private void Next()
    {
        if (IsLastStep)
        {
            Finished?.Invoke();
            return;
        }

        CurrentStep = Steps[Array.IndexOf(Steps, CurrentStep) + 1];
    }

    [RelayCommand]
    private void Back()
    {
        var index = Array.IndexOf(Steps, CurrentStep);
        if (index > 0)
            CurrentStep = Steps[index - 1];
    }
}
