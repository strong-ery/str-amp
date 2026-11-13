using System.IO.Compression;

namespace Stramp.Core.Updates;

/// <summary>Downloads an update's zip asset and extracts it to a fresh temp staging folder.</summary>
public static class UpdateDownloader
{
    private const string ClientId = "str-amp (https://github.com/strong-ery/str-amp)";
    private const int BufferSize = 81920;

    /// <summary>
    /// Downloads and extracts the asset, returning the folder that actually holds Stramp.App.exe:
    /// a release zip may wrap its contents in one extra folder or not, so this looks for the exe
    /// rather than assuming the zip's layout.
    /// </summary>
    public static async Task<string> DownloadAndExtractAsync(
        string downloadUrl, string version, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var stagingRoot = Path.Combine(Path.GetTempPath(), "stramp-update", version);
        if (Directory.Exists(stagingRoot))
            Directory.Delete(stagingRoot, recursive: true);
        Directory.CreateDirectory(stagingRoot);

        var zipPath = Path.Combine(Path.GetTempPath(), $"stramp-update-{version}.zip");
        await DownloadAsync(downloadUrl, zipPath, progress, ct);

        try
        {
            ZipFile.ExtractToDirectory(zipPath, stagingRoot, overwriteFiles: true);
        }
        finally
        {
            File.Delete(zipPath);
        }

        return FindAppRoot(stagingRoot) ?? stagingRoot;
    }

    private static async Task DownloadAsync(string url, string destinationPath, IProgress<double>? progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ClientId);

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength;

        await using var destination = File.Create(destinationPath);
        await using var source = await response.Content.ReadAsStreamAsync(ct);

        var buffer = new byte[BufferSize];
        long readTotal = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            readTotal += read;
            if (totalBytes is > 0)
                progress?.Report((double)readTotal / totalBytes.Value);
        }
    }

    private static string? FindAppRoot(string stagingRoot)
    {
        var exePath = Directory.EnumerateFiles(stagingRoot, "Stramp.App.exe", SearchOption.AllDirectories).FirstOrDefault();
        return exePath is null ? null : Path.GetDirectoryName(exePath);
    }
}
