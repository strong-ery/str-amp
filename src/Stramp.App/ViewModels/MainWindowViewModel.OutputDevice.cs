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
    partial void OnVolumeChanged(double value)
    {
        _player.Volume = value;
        _settings.Volume = value;
        SettingsService.Save(_settings);

        // Turning the level up is an implicit unmute; without this the slider (and the volume media
        // keys, which come through here too) would appear dead while muted.
        if (IsMuted && value > 0)
            IsMuted = false;
    }

    partial void OnIsMutedChanged(bool value) => _player.Muted = value;

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    /// <summary>Re-enumerates endpoints and re-selects the saved one. Runs each time the picker opens.</summary>
    [RelayCommand]
    private void RefreshOutputDevices()
    {
        var rows = new List<OutputDeviceRow> { SystemDefaultOutputDevice };
        foreach (var device in _player.GetOutputDevices())
            rows.Add(new OutputDeviceRow(device.Id, device.Name));

        var savedId = _settings.OutputDeviceId;
        var saved = rows.FirstOrDefault(row => IsSameDevice(row.Id, savedId));

        // A pinned device that is currently unplugged stays listed, so the picker still shows what
        // playback is meant to route to rather than silently reading as "system default".
        if (saved is null && !string.IsNullOrWhiteSpace(savedId))
        {
            saved = new OutputDeviceRow(savedId, "Unavailable device");
            rows.Add(saved);
        }

        _isRefreshingOutputDevices = true;
        try
        {
            OutputDevices.Clear();
            foreach (var row in rows)
                OutputDevices.Add(row);
            SelectedOutputDevice = saved ?? SystemDefaultOutputDevice;
        }
        finally
        {
            _isRefreshingOutputDevices = false;
        }
    }

    partial void OnSelectedOutputDeviceChanged(OutputDeviceRow? value)
    {
        if (_isRefreshingOutputDevices || value is null)
            return;
        ApplyOutputDevice(value.Id);
    }

    /// <summary>Routes playback to an endpoint, keeping position and playing state across the switch.</summary>
    private void ApplyOutputDevice(string? deviceId)
    {
        if (IsSameDevice(_settings.OutputDeviceId, deviceId))
            return;

        try
        {
            _player.OutputDeviceId = deviceId;
        }
        catch (Exception ex)
        {
            StatusText = $"Could not switch playback device: {ex.Message}";
            return;
        }

        _settings.OutputDeviceId = deviceId;
        SettingsService.Save(_settings);
    }

    private static bool IsSameDevice(string? left, string? right) =>
        string.Equals(
            string.IsNullOrWhiteSpace(left) ? null : left,
            string.IsNullOrWhiteSpace(right) ? null : right,
            StringComparison.OrdinalIgnoreCase);

}
