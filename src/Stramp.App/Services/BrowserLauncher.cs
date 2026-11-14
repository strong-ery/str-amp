using System.Diagnostics;

namespace Stramp.App.Services;

/// <summary>Opens a web address in the user's default browser.</summary>
/// <remarks>
/// Hands the address to the shell, which picks the default browser on Windows and goes through
/// xdg-open on Linux. Only absolute http and https addresses are opened; anything else is refused with
/// an <see cref="ArgumentException"/> so a malformed address never reaches the shell.
/// </remarks>
public static class BrowserLauncher
{
    public static void Open(Uri address)
    {
        if (!address.IsAbsoluteUri || address.Scheme is not ("http" or "https"))
            throw new ArgumentException($"Not a web address: {address}", nameof(address));

        Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
    }
}
