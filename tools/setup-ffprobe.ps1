param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\.tools\ffmpeg\bin")
)

$ErrorActionPreference = "Stop"
$assetUrl = "https://api.github.com/repos/FFmpeg-Builds/FFmpeg-Builds/releases/assets/747493711"
$expectedSha256 = "1d70f82bcc24b9245f1feeb030855c9f94cd380e67867d5a4ac1ff78389a619a"
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "accesslearn-ffprobe-$([Guid]::NewGuid().ToString('N'))"
$archivePath = Join-Path $temporaryRoot "ffmpeg.zip"
$extractPath = Join-Path $temporaryRoot "extract"

try {
    New-Item -ItemType Directory -Force -Path $temporaryRoot, $extractPath, $Destination | Out-Null
    Invoke-WebRequest `
        -Uri $assetUrl `
        -Headers @{ Accept = "application/octet-stream"; "User-Agent" = "AccessLearn-Setup" } `
        -OutFile $archivePath

    $actualSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash.ToLowerInvariant()
    if ($actualSha256 -ne $expectedSha256) {
        throw "ffprobe archive checksum mismatch. Expected $expectedSha256 but received $actualSha256."
    }

    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractPath
    $ffprobe = Get-ChildItem -LiteralPath $extractPath -Filter "ffprobe.exe" -File -Recurse |
        Select-Object -First 1
    if ($null -eq $ffprobe) {
        throw "The verified archive does not contain ffprobe.exe."
    }

    Copy-Item -Path (Join-Path $ffprobe.Directory.FullName "*") -Destination $Destination -Recurse -Force
    "ffprobe installed at $(Join-Path $Destination 'ffprobe.exe')"
}
finally {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}
