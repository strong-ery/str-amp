using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Stramp.App.ViewModels;

/// <summary>One editable color: bound to the visual picker, with a hex box and preview swatch.</summary>
public partial class ColorChannelEditor : ViewModelBase
{
    private readonly Action _onChanged;
    private bool _updating;

    [ObservableProperty]
    public partial Color Color { get; set; }

    [ObservableProperty]
    public partial string Hex { get; set; } = "#000000";

    [ObservableProperty]
    public partial IBrush PreviewBrush { get; set; } = Brushes.Black;

    public ColorChannelEditor(Color initial, Action onChanged)
    {
        _onChanged = onChanged;
        SetColor(initial);
    }

    public void SetColor(Color color)
    {
        _updating = true;
        Color = color;
        Hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        PreviewBrush = new SolidColorBrush(color);
        _updating = false;
    }

    partial void OnColorChanged(Color value)
    {
        if (_updating)
            return;

        _updating = true;
        Hex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
        PreviewBrush = new SolidColorBrush(value);
        _updating = false;

        _onChanged();
    }

    partial void OnHexChanged(string value)
    {
        if (_updating)
            return;

        try
        {
            SetColor(Avalonia.Media.Color.Parse(value));
            _onChanged();
        }
        catch
        {
            // Invalid or partial hex while typing: ignore until it parses.
        }
    }
}
