param(
    [Parameter(Mandatory = $true)]
    [string]$InputVideo,
    [Parameter(Mandatory = $true)]
    [string]$OutputVtt
)

$ErrorActionPreference = 'Stop'
$temporaryAudio = Join-Path ([System.IO.Path]::GetTempPath()) ("accesslearn-caption-audio-{0}.wav" -f [guid]::NewGuid().ToString('N'))

function Format-VttTimestamp([double]$milliseconds) {
    $time = [TimeSpan]::FromMilliseconds([Math]::Max(0, $milliseconds))
    return "{0:00}:{1:00}:{2:00}.{3:000}" -f [int]$time.TotalHours, $time.Minutes, $time.Seconds, $time.Milliseconds
}

try {
    & node (Join-Path $PSScriptRoot 'extract-video-audio.mjs') --input $InputVideo --output $temporaryAudio
    if ($LASTEXITCODE -ne 0) { throw 'Video audio extraction failed.' }

    Add-Type -AssemblyName System.Speech
    $culture = [System.Globalization.CultureInfo]::GetCultureInfo('en-US')
    $recognizer = New-Object System.Speech.Recognition.SpeechRecognitionEngine($culture)
    $recognizer.LoadGrammar((New-Object System.Speech.Recognition.DictationGrammar))
    $recognizer.SetInputToWaveFile($temporaryAudio)

    $results = New-Object System.Collections.Generic.List[object]
    $timelineMilliseconds = 0.0
    while ($true) {
        try { $result = $recognizer.Recognize() }
        catch [System.InvalidOperationException] { break }
        if ($null -eq $result) { break }
        $duration = [Math]::Max(500, $result.Audio.Duration.TotalMilliseconds)
        $results.Add([pscustomobject]@{
            StartMilliseconds = [Math]::Round($timelineMilliseconds)
            EndMilliseconds = [Math]::Round($timelineMilliseconds + $duration)
            Text = $result.Text.Trim()
        })
        $timelineMilliseconds += $duration + 450
    }
    $recognizer.Dispose()
    if ($results.Count -eq 0) { throw 'No speech was recognized in the sample video.' }

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('WEBVTT')
    $lines.Add('')
    $lines.Add('NOTE Machine-generated offline draft. Human review is required before publication.')
    $lines.Add('')
    for ($index = 0; $index -lt $results.Count; $index++) {
        $cue = $results[$index]
        $lines.Add(($index + 1).ToString())
        $lines.Add(("{0} --> {1}" -f (Format-VttTimestamp $cue.StartMilliseconds), (Format-VttTimestamp $cue.EndMilliseconds)))
        $lines.Add($cue.Text)
        $lines.Add('')
    }

    $outputDirectory = Split-Path -Parent $OutputVtt
    if (-not (Test-Path -LiteralPath $outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory | Out-Null
    }
    [System.IO.File]::WriteAllText($OutputVtt, ($lines -join "`n"), (New-Object System.Text.UTF8Encoding($false)))
    "Extracted $($results.Count) draft caption cues to $OutputVtt. Human review is required."
}
finally {
    if (Test-Path -LiteralPath $temporaryAudio) { Remove-Item -LiteralPath $temporaryAudio -Force }
}
