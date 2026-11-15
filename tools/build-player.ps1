param(
    [switch]$InstallOnly
)

$ErrorActionPreference = "Stop"
$playerDirectory = Join-Path $PSScriptRoot "..\src\accessibility.Player"
if (-not (Test-Path -LiteralPath (Join-Path $playerDirectory "package.json"))) {
    throw "Player package was not found at $playerDirectory."
}

Push-Location $playerDirectory
try {
    npm ci
    if ($LASTEXITCODE -ne 0) { throw "npm ci failed." }
    if (-not $InstallOnly) {
        npm run build
        if ($LASTEXITCODE -ne 0) { throw "Player build failed." }
    }
}
finally {
    Pop-Location
}
