[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BundlePath
)

$ErrorActionPreference = 'Stop'
$bundle = [System.IO.Path]::GetFullPath($BundlePath)
$manifestPath = Join-Path $bundle 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Golden Stream manifest not found: $manifestPath"
}

$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
if ($manifest.format -ne 'termlink.golden-stream' -or $manifest.format_version -ne 1) {
    throw "Unsupported Golden Stream format: $($manifest.format) v$($manifest.format_version)"
}

$requiredFiles = [ordered]@{
    events       = 'events.jsonl'
    output       = 'output.bin'
    expectations = 'expectations.json'
}

foreach ($entry in $requiredFiles.GetEnumerator()) {
    $declaredName = $manifest.files.($entry.Key)
    if ($declaredName -ne $entry.Value) {
        throw "V1 requires $($entry.Key) file '$($entry.Value)', got '$declaredName'."
    }

    $path = Join-Path $bundle $declaredName
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Golden Stream file not found: $path"
    }

    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
    $expectedHash = $manifest.sha256.($entry.Key)
    if ($actualHash -cne $expectedHash) {
        throw "SHA-256 mismatch for $declaredName."
    }
}

$eventsPath = Join-Path $bundle $requiredFiles.events
$outputPath = Join-Path $bundle $requiredFiles.output
$expectationsPath = Join-Path $bundle $requiredFiles.expectations
$eventLines = @([System.IO.File]::ReadAllLines($eventsPath) | Where-Object { $_.Trim().Length -gt 0 })
$events = @($eventLines | ForEach-Object { $_ | ConvertFrom-Json })
$outputBytes = [System.IO.File]::ReadAllBytes($outputPath)

if ($events.Count -eq 0) {
    throw 'Golden Stream contains no events.'
}

$expectedSequence = 1L
$lastElapsed = -1L
$nextOutputOffset = 0L
$outputEventCount = 0
$startedCount = 0
$endedCount = 0
$lastSize = $null
$observedExitCode = $null

foreach ($event in $events) {
    if ($event.seq -ne $expectedSequence) {
        throw "Event sequence gap: expected $expectedSequence, got $($event.seq)."
    }
    if ($event.elapsed_us -lt $lastElapsed) {
        throw "Event time moved backwards at seq $($event.seq)."
    }

    switch ($event.type) {
        'output' {
            if ($event.data_offset -ne $nextOutputOffset -or $event.data_length -le 0) {
                throw "Invalid output range at seq $($event.seq)."
            }
            $nextOutputOffset += $event.data_length
            if ($nextOutputOffset -gt $outputBytes.LongLength) {
                throw "Output range exceeds output.bin at seq $($event.seq)."
            }
            $outputEventCount++
        }
        'resize' {
            if ($event.columns -le 0 -or $event.rows -le 0) {
                throw "Invalid terminal size at seq $($event.seq)."
            }
            $lastSize = $event
        }
        'lifecycle' {
            switch ($event.state) {
                'started' { $startedCount++ }
                'ended' {
                    $endedCount++
                    $observedExitCode = $event.exit_code
                }
                default { throw "Unsupported lifecycle state '$($event.state)' in V1." }
            }
        }
        default { throw "Unsupported event type '$($event.type)' at seq $($event.seq)." }
    }

    $expectedSequence++
    $lastElapsed = $event.elapsed_us
}

if ($events[0].type -ne 'lifecycle' -or $events[0].state -ne 'started' -or $startedCount -ne 1) {
    throw 'Golden Stream must contain exactly one starting lifecycle event.'
}
$lastEvent = $events[-1]
if ($lastEvent.type -ne 'lifecycle' -or $lastEvent.state -ne 'ended' -or $endedCount -ne 1) {
    throw 'Golden Stream must end with exactly one ended lifecycle event.'
}
if ($nextOutputOffset -ne $outputBytes.LongLength) {
    throw 'Output events do not cover output.bin exactly.'
}
if ($manifest.statistics.event_count -ne $events.Count -or
    $manifest.statistics.output_event_count -ne $outputEventCount -or
    $manifest.statistics.output_bytes -ne $outputBytes.LongLength) {
    throw 'Manifest statistics do not match Bundle contents.'
}

$expectations = Get-Content -Raw -LiteralPath $expectationsPath | ConvertFrom-Json
if ($observedExitCode -ne $expectations.exit_code) {
    throw "Exit code mismatch: expected $($expectations.exit_code), got $observedExitCode."
}
if ($null -eq $lastSize -or
    $lastSize.columns -ne $expectations.final_size.columns -or
    $lastSize.rows -ne $expectations.final_size.rows) {
    throw 'Final terminal size does not match expectations.'
}

$decodedOutput = [System.Text.Encoding]::UTF8.GetString($outputBytes)
foreach ($marker in $expectations.output_contains_utf8) {
    if (-not $decodedOutput.Contains($marker)) {
        throw "Expected UTF-8 output marker not found: $marker"
    }
}

[PSCustomObject]@{
    Bundle            = $bundle
    CaseName          = $manifest.case_name
    EventCount        = $events.Count
    OutputEventCount  = $outputEventCount
    OutputBytes       = $outputBytes.LongLength
    FinalColumns      = $lastSize.columns
    FinalRows         = $lastSize.rows
    ExitCode          = $observedExitCode
    DurationUs        = $lastElapsed
    Valid             = $true
} | Format-List
