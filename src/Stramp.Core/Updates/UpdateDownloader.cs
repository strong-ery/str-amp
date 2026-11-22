using System.Formats.Tar;
using System.IO.Compression;

namespace Stramp.Core.Updates;

/// <summary>Downloads an update's archive (zip, or tar.gz on Linux) and extracts it to a fresh temp staging folder.</summary>
public static class UpdateDownloader
{
    private const string ClientId = "str-amp (https://github.com/strong-ery/str-amp)";
    private const int BufferSize = 81920;

    /// <summary>
    /// Downloads and extracts the asset, returning the folder that actually holds the app executable:
    /// a release archive may wrap its contents in one extra folder or not, so this looks for the
    /// executable rather than assuming the archive's layout.
    /// </summary>
    public static async Task<string> DownloadAndExtractAsync(
        string downloadUrl, string version, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var stagingRoot = Path.Combine(Path.GetTempPath(), "stramp-update", version);
        if (Directory.Exists(stagingRoot))
            Directory.Delete(stagingRoot, recursive: true);
        Directory.CreateDirectory(stagingRoot);

        var isTarball = downloadUrl.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);
        var archivePath = Path.Combine(Path.GetTempPath(), $"stramp-update-{version}{(isTarball ? ".tar.gz" : ".zip")}");
        await DownloadAsync(downloadUrl, archivePath, progress, ct);

        try
        {
            if (isTarball)
            {
                await using var file = File.OpenRead(archivePath);
                await using var gzip = new GZipStream(file, CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gzip, stagingRoot, overwriteFiles: true, ct);
            }
            else
            {
                ZipFile.ExtractToDirectory(archivePath, stagingRoot, overwriteFiles: true);
            }
        }
        finally
        {
            File.Delete(archivePath);
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
        var exeName = OperatingSystem.IsWindows() ? "Stramp.App.exe" : "Stramp.App";
        var exePath = Directory.EnumerateFiles(stagingRoot, exeName, SearchOption.AllDirectories).FirstOrDefault();
        return exePath is null ? null : Path.GetDirectoryName(exePath);
    }
}
