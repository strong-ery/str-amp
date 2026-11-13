using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Stramp.Core.Playback;

namespace Stramp.App.ViewModels;

/// <summary>One EQ band: a draggable gain and an editable centre frequency.</summary>
public partial class EqualizerBandViewModel : ViewModelBase
{
    /// <summary>
    /// Range a centre frequency may be typed into. The audible band, give or take: the player
    /// separately keeps bands ordered and below what the current sample rate can represent.
    /// </summary>
    public const double MinFrequencyHz = EqualizerBandLayout.MinFrequencyHz;
    public const double MaxFrequencyHz = EqualizerBandLayout.MaxFrequencyHz;

    private readonly Action<bool> _onChanged;

    /// <summary>
    /// Swallows change notifications. Starts on, because assigning the initial values below runs
    /// the same change handlers, and the owner cannot answer questions about a band it has not
    /// finished adding to its collection yet.
    /// </summary>
    private bool _suppressNotify = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainLabel))]
    public partial double Gain { get; set; }

    [ObservableProperty]
    public partial double Frequency { get; set; }

    /// <summary>
    /// The band's gain as shown above its slider. Always carries an explicit sign so a boost reads
    /// "+3" against a cut's "-3", and only shows a decimal when the value actually has one.
    /// </summary>
    public string GainLabel => Gain.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture);

    /// <param name="onChanged">Called after a change; the argument is true when it was the
    /// centre frequency that moved rather than the gain.</param>
    public EqualizerBandViewModel(double frequencyHz, double gain, Action<bool> onChanged)
    {
        _onChanged = onChanged;
        Gain = gain;
        Frequency = frequencyHz;
        _suppressNotify = false;
    }

    /// <summary>Sets the centre without reporting it, for the owner's own bookkeeping.</summary>
    public void SetFrequencyQuietly(double value)
    {
        var wasSuppressed = _suppressNotify;
        _suppressNotify = true;
        Frequency = value;
        _suppressNotify = wasSuppressed;
    }

    partial void OnGainChanged(double value)
    {
        if (!_suppressNotify)
            _onChanged(false);
    }

    partial void OnFrequencyChanged(double value)
    {
        if (_suppressNotify)
            return;

        // A typed-in frequency can be anything, including blank or absurd. Snap it back into range
        // first; the owner then holds it between its neighbours.
        var clamped = double.IsFinite(value)
            ? Math.Clamp(value, MinFrequencyHz, MaxFrequencyHz)
            : MinFrequencyHz;

        if (Math.Abs(clamped - value) > 1e-9)
            SetFrequencyQuietly(clamped);

        _onChanged(true);
    }
}
