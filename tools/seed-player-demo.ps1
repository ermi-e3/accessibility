param(
    [Parameter(Mandatory)] [string]$Email,
    [Parameter(Mandatory)] [string]$Password,
    [string]$ApiBase = "https://localhost:7121",
    [string]$FfmpegPath = (Join-Path $PSScriptRoot "..\.tools\ffmpeg\bin\ffmpeg.exe"),
    [string]$SignVideoPath,
    [string]$AudioDescriptionPath
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $FfmpegPath)) {
    throw "ffmpeg was not found. Run tools/setup-ffprobe.ps1 first."
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "accesslearn-player-demo-$([Guid]::NewGuid().ToString('N'))"
$videoPath = Join-Path $temporaryRoot "accessible-demo.webm"
$handler = [Net.Http.HttpClientHandler]::new()
$handler.CookieContainer = [Net.CookieContainer]::new()
if ($ApiBase.StartsWith("https://localhost", [StringComparison]::OrdinalIgnoreCase)) {
    $handler.ServerCertificateCustomValidationCallback = [Net.Http.HttpClientHandler]::DangerousAcceptAnyServerCertificateValidator
}
$client = [Net.Http.HttpClient]::new($handler)
$client.BaseAddress = [Uri]($ApiBase.TrimEnd('/') + "/")

function Get-AntiforgeryToken {
    $response = $client.GetStringAsync("api/v1/auth/antiforgery").GetAwaiter().GetResult()
    return $response | ConvertFrom-Json
}

function Send-Json(
    [string]$path,
    [object]$body,
    [bool]$antiforgery = $true,
    [long]$ifMatch = 0) {
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, $path)
    if ($antiforgery) {
        $token = Get-AntiforgeryToken
        $request.Headers.Add($token.headerName, $token.requestToken)
    }
    if ($ifMatch -gt 0) { $request.Headers.TryAddWithoutValidation("If-Match", "`"$ifMatch`"") | Out-Null }
    $request.Content = [Net.Http.StringContent]::new(
        ($body | ConvertTo-Json -Depth 8),
        [Text.Encoding]::UTF8,
        "application/json")
    $response = $client.SendAsync($request).GetAwaiter().GetResult()
    $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) {
        throw "$path failed with HTTP $([int]$response.StatusCode): $content"
    }
    return $content | ConvertFrom-Json
}

function Get-Json([string]$path) {
    return $client.GetStringAsync($path).GetAwaiter().GetResult() | ConvertFrom-Json
}

function Upload-Media(
    [string]$lessonId,
    [string]$role,
    [string]$path,
    [string]$contentType,
    [string]$extraQuery = "") {
    if (-not (Test-Path -LiteralPath $path)) { throw "Media file was not found: $path" }
    $upload = [Net.Http.HttpRequestMessage]::new(
        [Net.Http.HttpMethod]::Post,
        "api/v1/lessons/$lessonId/assets?role=$role$extraQuery")
    $token = Get-AntiforgeryToken
    $upload.Headers.Add($token.headerName, $token.requestToken)
    $multipart = [Net.Http.MultipartFormDataContent]::new()
    $file = [Net.Http.ByteArrayContent]::new([IO.File]::ReadAllBytes($path))
    $file.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::Parse($contentType)
    $multipart.Add($file, "file", [IO.Path]::GetFileName($path))
    $upload.Content = $multipart
    $response = $client.SendAsync($upload).GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) {
        throw "Media upload failed: $($response.Content.ReadAsStringAsync().GetAwaiter().GetResult())"
    }
}

try {
    New-Item -ItemType Directory -Force -Path $temporaryRoot | Out-Null
    & $FfmpegPath `
        -hide_banner -loglevel error `
        -f lavfi -i "color=c=0x1b4965:s=640x360:d=6" `
        -f lavfi -i "sine=frequency=440:duration=6" `
        -c:v libvpx-vp9 -b:v 300k -pix_fmt yuv420p `
        -c:a libopus -shortest -y $videoPath
    if ($LASTEXITCODE -ne 0) { throw "Could not generate the demo WebM." }

    Send-Json "api/v1/auth/login" @{ email = $Email; password = $Password; rememberMe = $false } | Out-Null
    $lesson = Send-Json "api/v1/lessons" @{
        title = "AccessLearn Accessible Player Demo"
        description = "Rights-safe generated media for local player verification."
        sourceLanguage = "en"
    }

    Upload-Media $lesson.id "primaryVideo" $videoPath "video/webm"
    if ($SignVideoPath) {
        $signType = if ([IO.Path]::GetExtension($SignVideoPath) -eq ".webm") { "video/webm" } else { "video/mp4" }
        Upload-Media $lesson.id "signLanguage" $SignVideoPath $signType "&language=eth&reviewed=true&accessibilityMode=human-recorded"
    }
    if ($AudioDescriptionPath) {
        $extension = [IO.Path]::GetExtension($AudioDescriptionPath).ToLowerInvariant()
        $audioType = @{ ".wav" = "audio/wav"; ".mp3" = "audio/mpeg"; ".m4a" = "audio/mp4"; ".ogg" = "audio/ogg"; ".opus" = "audio/ogg" }[$extension]
        if (-not $audioType) { throw "Unsupported audio-description extension: $extension" }
        Upload-Media $lesson.id "audioDescription" $AudioDescriptionPath $audioType "&language=en&reviewed=true&accessibilityMode=full-mix"
    }

    $tracks = @(
        @{
            language = "en"
            segments = @(
                @{ sequence = 0; startMilliseconds = 0; endMilliseconds = 2800; text = "Welcome to the AccessLearn player demo."; speakerLabel = "Educator" },
                @{ sequence = 1; startMilliseconds = 3000; endMilliseconds = 5800; text = "Use captions, transcript search, and keyboard controls."; speakerLabel = "Educator" }
            )
        },
        @{
            language = "am"
            segments = @(
                @{ sequence = 0; startMilliseconds = 0; endMilliseconds = 2800; text = "ወደ AccessLearn ማጫወቻ ማሳያ እንኳን ደህና መጡ።"; speakerLabel = "አስተማሪ" },
                @{ sequence = 1; startMilliseconds = 3000; endMilliseconds = 5800; text = "ጽሑፍ መግለጫ፣ ፍለጋ እና የቁልፍ ሰሌዳ መቆጣጠሪያዎችን ይጠቀሙ።"; speakerLabel = "አስተማሪ" }
            )
        }
    )
    foreach ($track in $tracks) {
        $transcript = Send-Json "api/v1/lessons/$($lesson.id)/transcripts" $track
        Send-Json "api/v1/transcripts/$($transcript.id)/approve" @{} $true $transcript.version | Out-Null
    }

    $lessonDetail = Get-Json "api/v1/lessons/$($lesson.id)"
    $publication = Send-Json "api/v1/lessons/$($lesson.id)/publish" @{} $true $lessonDetail.lesson.version
    [pscustomobject]@{
        lessonId = $lesson.id
        publicationId = $publication.id
        playerUrl = "$($ApiBase.TrimEnd('/'))/player-demo/index.html?lesson=$($lesson.id)&locale=en"
        amharicPlayerUrl = "$($ApiBase.TrimEnd('/'))/player-demo/index.html?lesson=$($lesson.id)&locale=am"
    } | ConvertTo-Json
}
finally {
    $client.Dispose()
    $handler.Dispose()
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}
