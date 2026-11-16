param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\.tools\whisper")
)

$ErrorActionPreference = "Stop"
$binaryAssetUrl = "https://api.github.com/repos/ggml-org/whisper.cpp/releases/assets/616096933"
$binarySha256 = "6ba69e3482d7826214f90a6a9c84ca07782aec1e1d0c6a7c30c994fd5d816ccb"
$modelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin?download=true"
$modelSha256 = "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb"
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "accesslearn-whisper-$([Guid]::NewGuid().ToString('N'))"
$archivePath = Join-Path $temporaryRoot "whisper.zip"
$modelPath = Join-Path $temporaryRoot "ggml-small-q5_1.bin"

try {
    New-Item -ItemType Directory -Force -Path $temporaryRoot, "$Destination\bin", "$Destination\models" | Out-Null
    Invoke-WebRequest `
        -Uri $binaryAssetUrl `
        -Headers @{ Accept = "application/octet-stream"; "User-Agent" = "AccessLearn-Setup" } `
        -OutFile $archivePath
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash.ToLowerInvariant() -ne $binarySha256) {
        throw "whisper.cpp archive checksum mismatch."
    }

    $extractPath = Join-Path $temporaryRoot "extract"
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractPath
    $executable = Get-ChildItem -LiteralPath $extractPath -Filter "whisper-cli.exe" -File -Recurse |
        Select-Object -First 1
    if ($null -eq $executable) { throw "The verified archive does not contain whisper-cli.exe." }
    Copy-Item -Path (Join-Path $executable.Directory.FullName "*") -Destination "$Destination\bin" -Recurse -Force

    Invoke-WebRequest -Uri $modelUrl -OutFile $modelPath
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $modelPath).Hash.ToLowerInvariant() -ne $modelSha256) {
        throw "Whisper multilingual model checksum mismatch."
    }
    Copy-Item -LiteralPath $modelPath -Destination "$Destination\models\ggml-small-q5_1.bin" -Force
    "whisper.cpp installed at $Destination"
}
finally {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}
