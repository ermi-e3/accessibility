param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$lessonPath = Join-Path $ProjectRoot 'sample-content\sample-lesson.json'
$lesson = Get-Content -LiteralPath $lessonPath -Raw | ConvertFrom-Json
$mediaDirectory = Join-Path $ProjectRoot 'Savanna-static-pages\assets\media\accesslearn-demo'
$manifestDirectory = Join-Path $ProjectRoot ("Savanna-static-pages\api\v1\public\lessons\{0}" -f $lesson.lessonId)
$temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("accesslearn-sample-{0}" -f [guid]::NewGuid().ToString('N'))

function Format-VttTimestamp([double]$milliseconds) {
    $time = [TimeSpan]::FromMilliseconds([Math]::Max(0, $milliseconds))
    return "{0:00}:{1:00}:{2:00}.{3:000}" -f [int]$time.TotalHours, $time.Minutes, $time.Seconds, $time.Milliseconds
}

function Read-PcmWave([string]$path) {
    $stream = [System.IO.File]::OpenRead($path)
    $reader = New-Object System.IO.BinaryReader($stream)
    try {
        if ([Text.Encoding]::ASCII.GetString($reader.ReadBytes(4)) -ne 'RIFF') { throw 'Invalid RIFF wave file.' }
        [void]$reader.ReadUInt32()
        if ([Text.Encoding]::ASCII.GetString($reader.ReadBytes(4)) -ne 'WAVE') { throw 'Invalid WAVE file.' }
        $format = $null
        $data = $null
        while ($stream.Position -le $stream.Length - 8) {
            $chunk = [Text.Encoding]::ASCII.GetString($reader.ReadBytes(4))
            $size = $reader.ReadUInt32()
            if ($chunk -eq 'fmt ') {
                $code = $reader.ReadUInt16()
                $channels = $reader.ReadUInt16()
                $sampleRate = $reader.ReadUInt32()
                $byteRate = $reader.ReadUInt32()
                $blockAlign = $reader.ReadUInt16()
                $bits = $reader.ReadUInt16()
                if ($size -gt 16) { [void]$reader.ReadBytes([int]$size - 16) }
                $format = [pscustomobject]@{
                    Code = $code
                    Channels = $channels
                    SampleRate = $sampleRate
                    ByteRate = $byteRate
                    BlockAlign = $blockAlign
                    Bits = $bits
                }
            }
            elseif ($chunk -eq 'data') { $data = $reader.ReadBytes([int]$size) }
            else { [void]$reader.ReadBytes([int]$size) }
            if ($size % 2 -eq 1 -and $stream.Position -lt $stream.Length) { [void]$reader.ReadByte() }
        }
        if ($null -eq $format -or $null -eq $data -or $format.Code -ne 1) { throw 'Only PCM wave input is supported.' }
        return [pscustomobject]@{ Format = $format; Data = $data }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Write-PcmWave([string]$path, $format, [byte[]]$data) {
    $stream = [System.IO.File]::Create($path)
    $writer = New-Object System.IO.BinaryWriter($stream)
    try {
        $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF'))
        $writer.Write([uint32](36 + $data.Length))
        $writer.Write([Text.Encoding]::ASCII.GetBytes('WAVE'))
        $writer.Write([Text.Encoding]::ASCII.GetBytes('fmt '))
        $writer.Write([uint32]16)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$format.Channels)
        $writer.Write([uint32]$format.SampleRate)
        $writer.Write([uint32]$format.ByteRate)
        $writer.Write([uint16]$format.BlockAlign)
        $writer.Write([uint16]$format.Bits)
        $writer.Write([Text.Encoding]::ASCII.GetBytes('data'))
        $writer.Write([uint32]$data.Length)
        $writer.Write($data)
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

try {
    foreach ($directory in @($mediaDirectory, $manifestDirectory, $temporaryDirectory)) {
        if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory | Out-Null }
    }

    Add-Type -AssemblyName System.Speech
    $audioFormat = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo -ArgumentList @(
        16000,
        [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen,
        [System.Speech.AudioFormat.AudioChannel]::Mono
    )
    $segments = New-Object System.Collections.Generic.List[object]
    $combined = New-Object System.IO.MemoryStream
    $waveFormat = $null
    $initialBytes = [int](16000 * 2 * [double]$lesson.initialSilenceMilliseconds / 1000)
    $combined.Write((New-Object byte[] $initialBytes), 0, $initialBytes)

    for ($index = 0; $index -lt $lesson.narration.Count; $index++) {
        $segmentFile = Join-Path $temporaryDirectory ("segment-{0}.wav" -f $index)
        $synthesizer = New-Object System.Speech.Synthesis.SpeechSynthesizer
        try {
            $synthesizer.Rate = -1
            $synthesizer.SetOutputToWaveFile($segmentFile, $audioFormat)
            $synthesizer.Speak([string]$lesson.narration[$index].text)
        }
        finally { $synthesizer.Dispose() }

        $wave = Read-PcmWave $segmentFile
        if ($null -eq $waveFormat) { $waveFormat = $wave.Format }
        $start = [Math]::Round($combined.Length * 1000 / $waveFormat.ByteRate)
        $combined.Write($wave.Data, 0, $wave.Data.Length)
        $end = [Math]::Round($combined.Length * 1000 / $waveFormat.ByteRate)
        $segments.Add([pscustomobject]@{
            sequence = $index
            startMilliseconds = [long]$start
            endMilliseconds = [long]$end
            text = [string]$lesson.narration[$index].text
            speakerLabel = [string]$lesson.speakerLabel
        })
        if ($index -lt $lesson.narration.Count - 1) {
            $silenceBytes = [int]($waveFormat.ByteRate * [double]$lesson.gapMilliseconds / 1000)
            $combined.Write((New-Object byte[] $silenceBytes), 0, $silenceBytes)
        }
    }

    $audioPath = Join-Path $temporaryDirectory 'sample-narration.wav'
    $audioBytes = $combined.ToArray()
    $combined.Dispose()
    Write-PcmWave $audioPath $waveFormat $audioBytes
    $durationMilliseconds = [long][Math]::Round($audioBytes.Length * 1000 / $waveFormat.ByteRate)

    $reviewedVtt = New-Object System.Collections.Generic.List[string]
    $reviewedVtt.Add('WEBVTT')
    $reviewedVtt.Add('')
    $reviewedVtt.Add('NOTE Human-reviewed from the rights-safe sample narration source.')
    $reviewedVtt.Add('')
    foreach ($segment in $segments) {
        $reviewedVtt.Add(($segment.sequence + 1).ToString())
        $reviewedVtt.Add(("{0} --> {1}" -f (Format-VttTimestamp $segment.startMilliseconds), (Format-VttTimestamp $segment.endMilliseconds)))
        $reviewedVtt.Add($segment.text)
        $reviewedVtt.Add('')
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $mediaDirectory 'sample-captions.en.vtt'),
        ($reviewedVtt -join "`n"),
        (New-Object System.Text.UTF8Encoding($false))
    )

    $transcript = [ordered]@{
        lessonId = [string]$lesson.lessonId
        publicationId = [string]$lesson.publicationId
        language = [string]$lesson.sourceLanguage
        revision = 1
        segments = $segments.ToArray()
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $mediaDirectory 'sample-transcript.en.json'),
        ($transcript | ConvertTo-Json -Depth 10),
        (New-Object System.Text.UTF8Encoding($false))
    )

    $escapedTitle = [System.Security.SecurityElement]::Escape([string]$lesson.title)
    $poster = @"
<svg xmlns="http://www.w3.org/2000/svg" width="960" height="540" viewBox="0 0 960 540" role="img" aria-labelledby="title description">
  <title id="title">$escapedTitle</title>
  <desc id="description">AccessLearn Ethiopia sample lesson poster.</desc>
  <defs><linearGradient id="background" x2="1" y2="1"><stop stop-color="#102f3b"/><stop offset="1" stop-color="#075041"/></linearGradient></defs>
  <rect width="960" height="540" fill="url(#background)"/>
  <rect width="28" height="540" fill="#f4c95d"/>
  <circle cx="870" cy="80" r="150" fill="#fff" opacity=".08"/>
  <text x="78" y="100" fill="#f4c95d" font-family="Arial,sans-serif" font-size="25" font-weight="700">ACCESSLEARN ETHIOPIA</text>
  <text x="78" y="215" fill="#fff" font-family="Arial,sans-serif" font-size="42" font-weight="700">Accessible learning</text>
  <text x="78" y="270" fill="#fff" font-family="Arial,sans-serif" font-size="42" font-weight="700">to career opportunity</text>
  <text x="78" y="390" fill="#f4c95d" font-family="Arial,sans-serif" font-size="28" font-weight="700">Captions and transcript included</text>
</svg>
"@
    [System.IO.File]::WriteAllText((Join-Path $mediaDirectory 'sample-poster.svg'), $poster, (New-Object System.Text.UTF8Encoding($false)))

    $videoPath = Join-Path $mediaDirectory 'sample-lesson.webm'
    & node (Join-Path $PSScriptRoot 'generate-sample-video.mjs') --audio $audioPath --output $videoPath --lesson $lessonPath
    if ($LASTEXITCODE -ne 0) { throw 'Sample video generation failed.' }

    $playwrightFfmpeg = Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'ms-playwright\ffmpeg-*\ffmpeg-win64.exe') -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($null -ne $playwrightFfmpeg) {
        $remuxedVideo = Join-Path $temporaryDirectory 'sample-lesson-remuxed.webm'
        & $playwrightFfmpeg.FullName -hide_banner -loglevel warning -i $videoPath -map 0 -c copy -y $remuxedVideo
        if ($LASTEXITCODE -ne 0) { throw 'Sample WebM remux failed.' }
        Move-Item -LiteralPath $remuxedVideo -Destination $videoPath -Force
    }
    else {
        Write-Warning 'Playwright FFmpeg was not found; the sample may not expose a seekable duration.'
    }

    & (Join-Path $PSScriptRoot 'extract-captions.ps1') -InputVideo $videoPath -OutputVtt (Join-Path $mediaDirectory 'sample-captions.draft.vtt')

    $manifest = [ordered]@{
        schemaVersion = '1.0'
        lessonId = [string]$lesson.lessonId
        publicationId = [string]$lesson.publicationId
        revision = 1
        title = [string]$lesson.title
        sourceLanguage = [string]$lesson.sourceLanguage
        expiresAt = '2099-12-31T23:59:59Z'
        media = [ordered]@{
            url = '/assets/media/accesslearn-demo/sample-lesson.webm'
            contentType = 'video/webm'
            durationMilliseconds = $durationMilliseconds
            posterUrl = '/assets/media/accesslearn-demo/sample-poster.svg'
        }
        captions = @([ordered]@{
            language = 'en'
            label = 'English (reviewed)'
            url = '/assets/media/accesslearn-demo/sample-captions.en.vtt'
            isDefault = $true
            isHumanReviewed = $true
        })
        transcripts = @([ordered]@{
            language = 'en'
            label = 'English transcript'
            url = '/assets/media/accesslearn-demo/sample-transcript.en.json'
            isHumanReviewed = $true
        })
        signLanguage = $null
        audioDescription = $null
        warnings = @('missingAmharicCaptions', 'missingSignLanguage', 'missingAudioDescription')
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $manifestDirectory 'manifest'),
        ($manifest | ConvertTo-Json -Depth 10),
        (New-Object System.Text.UTF8Encoding($false))
    )

    "Sample media ready: $videoPath"
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) { Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force }
}
