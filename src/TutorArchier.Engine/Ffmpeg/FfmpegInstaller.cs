using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using TutorArchier.Core;

namespace TutorArchier.Engine.Ffmpeg;

/// <summary>
/// Installs the pinned Gyan "essentials" build: from the copy embedded in release builds, otherwise by download.
/// Either way the zip's SHA-256 is checked before ffmpeg.exe + ffprobe.exe are unpacked.
/// </summary>
public sealed class FfmpegInstaller(ILogger<FfmpegInstaller> logger)
{
    // Bump all three together, from the release's asset list on GitHub (the onboarding and settings texts
    // quote the version and size too; publish.ps1 reads Version and Sha256 from here). Essentials has every
    // filter and encoder the app uses except SVT-AV1, which is out of scope for now.
    // Pinned to 8.1 on purpose: Gyan's 8.1.2+ builds need NVENC API 13.1 (driver 610+), which NVIDIA never
    // ships for Pascal (GTX 10xx, last branch R580), so NVENC would silently fall back to CPU on those cards.
    public const string Version = "8.1";
    public const long DownloadSize = 108_771_139;
    private const string Sha256 = "8748283d821613d930b0e7be685aaa9df4ca6f0ad4d0c42fd02622b3623463c6";

    private static readonly Uri DownloadUri =
        new($"https://github.com/GyanD/codexffmpeg/releases/download/{Version}/ffmpeg-{Version}-essentials_build.zip");

    private static readonly string[] Executables = ["ffmpeg.exe", "ffprobe.exe"];

    private static readonly HttpClient Http = new();

    // ponytail: the whole Gyan zip is embedded (ffplay included, ~36 MB of the exe) so the pinned SHA-256
    // verifies it as is; repack to ffmpeg + ffprobe in publish.ps1 if the exe size starts to matter.
    private const string BundleResource = "ffmpeg.zip";
    private const string BundleMarker = "bundled-version.txt";

    /// <summary>True in release builds: publish.ps1 embeds the pinned zip into the Engine assembly.</summary>
    public static bool IsBundled => typeof(FfmpegInstaller).Assembly.GetManifestResourceInfo(BundleResource) is not null;

    /// <summary>
    /// Unpacks the embedded ffmpeg on first run and after an ffmpeg bump, so release builds never ask for a download.
    /// No-op in builds without one. Failures are logged only: the usual search and "Otomatik kur" still follow.
    /// </summary>
    public async Task EnsureBundledAsync(CancellationToken cancellationToken = default)
    {
        if (!IsBundled)
            return;

        var target = AppPaths.UserFfmpegDirectory;
        var marker = Path.Combine(target, BundleMarker);
        try
        {
            if (File.Exists(marker) && await File.ReadAllTextAsync(marker, cancellationToken) == Version
                && Executables.All(name => File.Exists(Path.Combine(target, name))))
                return;

            await InstallBundledAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Bundled ffmpeg could not be unpacked");
        }
    }

    /// <summary>Returns the folder that now holds ffmpeg.exe and ffprobe.exe.</summary>
    /// <param name="onBytes">Bytes downloaded so far, out of <see cref="DownloadSize"/>. Not called when bundled.</param>
    public async Task<string> InstallAsync(Action<long>? onBytes = null, CancellationToken cancellationToken = default)
    {
        string? work = null;
        try
        {
            if (IsBundled)
                return await InstallBundledAsync(cancellationToken);

            work = AppPaths.CreateScratchDirectory("ffmpeg-download");
            var archive = Path.Combine(work, "ffmpeg.zip");
            await DownloadAsync(archive, onBytes, cancellationToken);
            var target = ResolveInstallDirectory();
            await using (var stream = File.OpenRead(archive))
                await InstallFromArchiveAsync(stream, Sha256, target, cancellationToken);
            logger.LogInformation("Installed ffmpeg {Version} into {Directory}", Version, target);
            return target;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "ffmpeg install failed");
            throw new InvalidOperationException(DescribeFailure(ex), ex);
        }
        finally
        {
            if (work is not null)
                AppPaths.TryDeleteDirectory(work);
        }
    }

    /// <summary>
    /// Always the per-user folder, never next to the exe: a release is one file the user may keep on the desktop,
    /// and an "ffmpeg" folder appearing beside it would undo that.
    /// </summary>
    private async Task<string> InstallBundledAsync(CancellationToken cancellationToken)
    {
        var target = AppPaths.UserFfmpegDirectory;
        await using (var stream = typeof(FfmpegInstaller).Assembly.GetManifestResourceStream(BundleResource)!)
            await InstallFromArchiveAsync(stream, Sha256, target, cancellationToken);
        // Written last: an interrupted unpack has no marker and is redone on the next start.
        await File.WriteAllTextAsync(Path.Combine(target, BundleMarker), Version, cancellationToken);
        logger.LogInformation("Unpacked bundled ffmpeg {Version} into {Directory}", Version, target);
        return target;
    }

    /// <summary>Verifies the archive, then swaps the executables in so a half-written ffmpeg is never picked up.</summary>
    /// <param name="archive">A seekable stream: it is read twice, once for the hash and once for the entries.</param>
    internal static async Task InstallFromArchiveAsync(Stream archive, string expectedSha256, string target, CancellationToken cancellationToken)
    {
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(archive, cancellationToken));
        if (actual != expectedSha256)
            throw new InvalidDataException($"Checksum mismatch: expected {expectedSha256}, got {actual}");
        archive.Position = 0;

        await Task.Run(() =>
        {
            Directory.CreateDirectory(target);
            using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var name in Executables)
            {
                var entry = zip.Entries.SingleOrDefault(e => e.FullName.EndsWith("/bin/" + name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"{name} is missing from the archive");
                var partial = Path.Combine(target, name + ".partial");
                entry.ExtractToFile(partial, overwrite: true);
                File.Move(partial, Path.Combine(target, name), overwrite: true);
            }
        }, cancellationToken);
    }

    private static async Task DownloadAsync(string path, Action<long>? onBytes, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = File.Create(path);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
            onBytes?.Invoke(total);
        }
    }

    /// <summary>The app folder when it is writable, otherwise the per-user data folder.</summary>
    private string ResolveInstallDirectory()
    {
        foreach (var directory in new[] { AppPaths.BundledFfmpegDirectory, AppPaths.UserFfmpegDirectory })
        {
            try
            {
                Directory.CreateDirectory(directory);
                var probe = Path.Combine(directory, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return directory;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                logger.LogInformation("{Directory} is not writable, trying the next location: {Error}", directory, ex.Message);
            }
        }

        throw new UnauthorizedAccessException("No writable location for ffmpeg");
    }

    /// <summary>One Turkish sentence the user can act on; the exception itself goes to the log.</summary>
    internal static string DescribeFailure(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } status } =>
            $"ffmpeg indirilemedi (sunucu {(int)status} döndürdü). Biraz sonra tekrar dene ya da ffmpeg'i elle kurup klasörünü seç.",
        HttpRequestException =>
            "ffmpeg indirilemedi. İnternet bağlantını, güvenlik duvarını ya da proxy ayarını kontrol edip tekrar dene.",
        InvalidDataException =>
            "İndirilen dosya doğrulanamadı (yarım ya da değiştirilmiş). Tekrar dene; sürerse ffmpeg'i elle kurup klasörünü seç.",
        UnauthorizedAccessException =>
            "ffmpeg'i yazacak izinli bir klasör bulunamadı. Uygulamayı kendi klasörüne taşı ya da ffmpeg'i elle kurup klasörünü seç.",
        IOException when ex.HResult == unchecked((int)0x80070070) =>
            "Diskte yer kalmadı. ffmpeg için yaklaşık 300 MB yer açıp tekrar dene.",
        _ => $"ffmpeg kurulamadı: {ex.Message}",
    };
}
