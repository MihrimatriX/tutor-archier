using System.IO.Compression;
using System.Security.Cryptography;
using TutorArchier.Core;
using TutorArchier.Engine.Ffmpeg;

namespace TutorArchier.Tests.Ffmpeg;

public sealed class FfmpegInstallerTests : IDisposable
{
    private readonly string _work = AppPaths.CreateScratchDirectory("test-installer");

    public void Dispose() => AppPaths.TryDeleteDirectory(_work);

    [Fact]
    public async Task Verified_archive_installs_only_the_two_executables()
    {
        var (archive, hash) = CreateArchive();
        var target = Path.Combine(_work, "ffmpeg");

        await using (var stream = File.OpenRead(archive))
            await FfmpegInstaller.InstallFromArchiveAsync(stream, hash, target, CancellationToken.None);

        Assert.Equal(["ffmpeg.exe", "ffprobe.exe"], Directory.GetFiles(target).Select(Path.GetFileName).Order());
        Assert.Equal("ffmpeg", File.ReadAllText(Path.Combine(target, "ffmpeg.exe")));
    }

    [Fact]
    public async Task Checksum_mismatch_writes_nothing()
    {
        var (archive, _) = CreateArchive();
        var target = Path.Combine(_work, "ffmpeg");

        await using (var stream = File.OpenRead(archive))
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                FfmpegInstaller.InstallFromArchiveAsync(stream, new string('0', 64), target, CancellationToken.None));

        Assert.False(Directory.Exists(target));
    }

    /// <summary>Same layout as the Gyan zip: a versioned root folder with bin/ inside.</summary>
    private (string Path, string Sha256) CreateArchive()
    {
        var path = Path.Combine(_work, "ffmpeg.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "ffmpeg", "ffprobe", "ffplay" })
            {
                using var writer = new StreamWriter(zip.CreateEntry($"ffmpeg-9.0.2-essentials_build/bin/{name}.exe").Open());
                writer.Write(name);
            }
            zip.CreateEntry("ffmpeg-9.0.2-essentials_build/LICENSE");
        }

        return (path, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
    }
}
