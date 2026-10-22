# Release package: artifacts/TutorArchier-<version>-win-x64.exe
# One self-contained exe: the target PC needs neither .NET nor a download. The pinned ffmpeg zip is embedded
# and unpacked into %LOCALAPPDATA%\TutorArchier\ffmpeg on first run (FfmpegInstaller.EnsureBundledAsync).
# CI (.github/workflows/release.yml) passes the version from the git tag.
param([string]$Version = ([xml](Get-Content "$PSScriptRoot/Directory.Build.props")).Project.PropertyGroup.Version)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'  # Invoke-WebRequest is many times slower with the progress bar
$artifacts = "$PSScriptRoot/artifacts"
$out = "$artifacts/publish/win-x64"
$exe = "$artifacts/TutorArchier-$Version-win-x64.exe"

# The installer's constants decide which ffmpeg ships, so the embedded zip and the runtime check never disagree.
$installer = Get-Content "$PSScriptRoot/src/TutorArchier.Engine/Ffmpeg/FfmpegInstaller.cs" -Raw
$ffVersion = [regex]::Match($installer, 'const string Version = "([^"]+)"').Groups[1].Value
$ffSha = [regex]::Match($installer, 'const string Sha256 = "([0-9a-f]{64})"').Groups[1].Value
if (-not $ffVersion -or -not $ffSha) { throw 'ffmpeg Version/Sha256 not found in FfmpegInstaller.cs' }

$ffZip = "$artifacts/ffmpeg-$ffVersion-essentials_build.zip"
if (-not (Test-Path $ffZip)) {
    New-Item -ItemType Directory -Force $artifacts | Out-Null
    Invoke-WebRequest "https://github.com/GyanD/codexffmpeg/releases/download/$ffVersion/ffmpeg-$ffVersion-essentials_build.zip" -OutFile "$ffZip.partial"
    Move-Item "$ffZip.partial" $ffZip -Force
}
if ((Get-FileHash $ffZip -Algorithm SHA256).Hash -ne $ffSha) {
    Remove-Item $ffZip
    throw "ffmpeg $ffVersion checksum mismatch; the download was deleted"
}

dotnet test "$PSScriptRoot/TutorArchier.slnx" -c Release
if ($LASTEXITCODE) { exit $LASTEXITCODE }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
# No EnableCompressionInSingleFile: compressed assemblies are inflated into memory on every start, the
# embedded ffmpeg zip included. Uncompressed ones are memory-mapped straight from the exe.
dotnet publish "$PSScriptRoot/src/TutorArchier.UI" -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    -p:Version=$Version -p:FfmpegBundle="$ffZip" -o $out
if ($LASTEXITCODE) { exit $LASTEXITCODE }

Copy-Item "$out/TutorArchier.exe" $exe -Force
"Package: $exe ($([math]::Round((Get-Item $exe).Length / 1MB)) MB)"
