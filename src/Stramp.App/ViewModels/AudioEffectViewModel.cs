using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Stramp.Core.Playback;

namespace Stramp.App.ViewModels;

/// <summary>One of the five post-equalizer enhancement effects, as a named 0-10 slider.</summary>
public partial class AudioEffectViewModel : ViewModelBase
{
    private readonly Action _onChanged;

    /// <summary>
    /// Swallows change notifications. Starts on, because assigning the initial amount runs the
    /// same change handler, and the owner cannot answer for an effect it is still constructing.
    /// </summary>
    private bool _suppressNotify = true;

    public string Name { get; }
    public string Description { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AmountLabel))]
    public partial double Amount { get; set; }

    public AudioEffectViewModel(string name, string description, double amount, Action onChanged)
    {
        Name = name;
        Description = description;
        _onChanged = onChanged;
        Amount = amount;
        _suppressNotify = false;
    }

    /// <summary>
    /// Shown beside the slider, rounded to a whole number the way FXSound shows the same value.
    /// A preset's amount can sit between two of them: "Music" asks for 2.8 ambience, and is kept
    /// at full precision underneath rather than being snapped to what the label can print.
    /// </summary>
    public string AmountLabel => Amount < AudioEffectSettings.OffThreshold
        ? "off"
        : Amount.ToString("0", CultureInfo.InvariantCulture);

    /// <summary>Sets the amount without reporting it, for the owner's own bookkeeping.</summary>
    public void SetQuietly(double value)
    {
        var wasSuppressed = _suppressNotify;
        _suppressNotify = true;
        Amount = Math.Clamp(Math.Round(value, 2), 0, AudioEffectSettings.MaxAmount);
        _suppressNotify = wasSuppressed;
    }

    partial void OnAmountChanged(double value)
    {
        if (_suppressNotify)
            return;

        // Dragging lands on arbitrary decimals. Snap to whole numbers so a hand-set slider is
        // exactly the value its label prints; presets bypass this and keep their own precision.
        var snapped = double.IsFinite(value)
            ? Math.Clamp(Math.Round(value), 0, AudioEffectSettings.MaxAmount)
            : 0;

        if (Math.Abs(snapped - value) > 1e-9)
        {
            SetQuietly(snapped);
            _onChanged();
            return;
        }

        _onChanged();
    }
}
