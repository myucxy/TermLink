[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runnerPath = Join-Path $PSScriptRoot 'Invoke-FileOperationsE2E.ps1'

function Assert-True {
    param([bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw [InvalidOperationException]::new($Message) }
}

function Get-FailureMessage {
    param([Parameter(Mandatory)][scriptblock]$Action)
    try {
        & $Action | Out-Null
        return ''
    }
    catch { return $_.Exception.Message }
}

function Get-FunctionAST {
    param(
        [Parameter(Mandatory)][Management.Automation.Language.ScriptBlockAst]$AST,
        [Parameter(Mandatory)][string]$Name
    )
    $functions = @($AST.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name
    }, $true))
    Assert-True ($functions.Count -eq 1) "Runner function '$Name' is missing or duplicated."
    return $functions[0]
}

Assert-True (Test-Path -LiteralPath $runnerPath -PathType Leaf) 'The file-operations E2E runner is missing.'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    $runnerPath,
    [ref]$tokens,
    [ref]$parseErrors)
Assert-True ($parseErrors.Count -eq 0) `
    "The file-operations E2E runner has parser errors: $(@($parseErrors | ForEach-Object Message) -join '; ')"
$source = Get-Content -Raw -LiteralPath $runnerPath

foreach ($requiredFunction in @(
    'Test-StrictChildPath', 'Remove-OwnedTemporaryTree', 'New-ControlledFixture',
    'Get-CanonicalGoTool', 'Invoke-GoProtocolHarness', 'Stop-OwnedTerminalSessions'
)) {
    $null = Get-FunctionAST -AST $ast -Name $requiredFunction
}

$removeCommands = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -eq 'Remove-Item'
}, $true))
Assert-True ($removeCommands.Count -eq 1) 'Runner cleanup must have exactly one Remove-Item command.'
$cleanupFunction = Get-FunctionAST -AST $ast -Name 'Remove-OwnedTemporaryTree'
Assert-True ($removeCommands[0].Extent.StartOffset -ge $cleanupFunction.Extent.StartOffset -and
    $removeCommands[0].Extent.EndOffset -le $cleanupFunction.Extent.EndOffset) `
    'Runner Remove-Item escaped the owned temporary cleanup function.'
foreach ($cleanupGuard in @(
    'Test-StrictChildPath', '^termlink-file-operations-e2e-[a-f0-9]{32}$',
    '.termlink-file-operations-e2e-owner', 'TEMP_CLEANUP_MARKER_MISMATCH'
)) {
    Assert-True ($cleanupFunction.Extent.Text.Contains($cleanupGuard)) `
        "Owned temporary cleanup lacks guard '$cleanupGuard'."
}

foreach ($protocolProof in @(
    'deviceProofDomain', 'registerDomain', 'termlink-android',
    'LIST_PAGINATION_INVALID', 'DOWNLOAD_CONTENT_MISMATCH',
    'DIRECTORY_ZIP_ENTRY_HASH_MISMATCH', 'OPEN_TERMINAL_RESULT_MISMATCH',
    'OPEN_TERMINAL_ACTIVE_DELETE_NOT_FENCED', 'active_delete_fenced',
    'owned_cleanup_delegated', 'realtime_terminate_executed',
    'TERMINAL_CLEANUP_INCOMPLETE', 'CANCEL_NOT_PERSISTED', 'protocol_e2e_only'
)) {
    Assert-True ($source.Contains($protocolProof)) "Runner lacks protocol proof '$protocolProof'."
}
Assert-True ($source.Contains('root_entry_count = 505')) `
    'The controlled fixture no longer forces a 500 + 5 LIST page split.'
Assert-True ($source.Contains('value.CWD != r.cfg.fixtureRoot')) `
    'OPEN_TERMINAL no longer verifies the exact returned cwd.'
Assert-True ($source.Contains('value.SessionID') -and $source.Contains('waitForSession')) `
    'OPEN_TERMINAL no longer ties the returned session_id to the server session catalog.'
Assert-True ($source.Contains("Name = 'TermLink.SessionHost.exe'") -and
    $source.Contains('--managed-session-id')) `
    'Terminal cleanup is not scoped to an exact SessionHost session ID.'
Assert-True ($source.Contains('deleteStatus != http.StatusConflict') -and
    $source.Contains('deleteCode != "SESSION_ACTIVE"')) `
    'OPEN_TERMINAL no longer proves the active-session DELETE 409 fence.'
Assert-True ($source.Contains('$matches.Count -ne 1') -and
    $source.Contains('Get-Process -Id $processId')) `
    'Terminal cleanup no longer requires one exact process match and verified exit.'

Assert-True (-not [regex]::IsMatch($source,
    '(?i)Write-(Output|Host|Verbose|Debug|Information|Warning)\s+.*(token|password|private|authorization|cookie)')) `
    'Runner contains a credential-shaped logging statement.'
Assert-True ($source.Contains('Output is deliberately consumed but never surfaced')) `
    'Native compiler/HTTP diagnostic suppression is missing.'
Assert-True ($source.Contains('access_token|refresh_token|authorization|cookie|password')) `
    'Evidence credential-shape rejection is missing.'

# Import only focused path/process cleanup functions for adversarial checks.
foreach ($functionName in @(
    'Test-StrictChildPath', 'Remove-OwnedTemporaryTree', 'Stop-OwnedTerminalSessions'
)) {
    Invoke-Expression (Get-FunctionAST -AST $ast -Name $functionName).Extent.Text
}

$systemTemporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$selfTestId = [Guid]::NewGuid().ToString('N')
$selfTestRoot = [IO.Path]::GetFullPath((Join-Path $systemTemporaryRoot `
    "termlink-file-operations-e2e-selftest-$selfTestId"))
$temporaryPrefix = $systemTemporaryRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
Assert-True ($selfTestRoot.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase)) `
    'The self-test root escaped the system temporary directory.'
Assert-True ([IO.Path]::GetFileName($selfTestRoot) -cmatch `
    '^termlink-file-operations-e2e-selftest-[a-f0-9]{32}$') `
    'The self-test root name is not an owned random path.'

[IO.Directory]::CreateDirectory($selfTestRoot) | Out-Null
$sentinel = Join-Path $selfTestRoot 'sibling-sentinel.txt'
[IO.File]::WriteAllText($sentinel, 'preserve', [Text.UTF8Encoding]::new($false))
$ownedHelperProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
try {
    $goodId = [Guid]::NewGuid().ToString('N')
    $goodCandidate = Join-Path $selfTestRoot "termlink-file-operations-e2e-$goodId"
    $goodMarker = "TermLink.FileOperationsE2E/v1`n$goodId`nself-test"
    [IO.Directory]::CreateDirectory($goodCandidate) | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $goodCandidate '.termlink-file-operations-e2e-owner'),
        $goodMarker,
        [Text.UTF8Encoding]::new($false))
    Remove-OwnedTemporaryTree -ParentPath $selfTestRoot -CandidatePath $goodCandidate `
        -ExpectedMarker $goodMarker
    Assert-True (-not (Test-Path -LiteralPath $goodCandidate)) `
        'Owned temporary cleanup did not remove the exact candidate.'
    Assert-True ((Test-Path -LiteralPath $sentinel -PathType Leaf) -and
        [IO.File]::ReadAllText($sentinel) -ceq 'preserve') `
        'Owned temporary cleanup changed a sibling sentinel.'

    $badName = Join-Path $selfTestRoot "wrong-file-operations-e2e-$goodId"
    [IO.Directory]::CreateDirectory($badName) | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $badName '.termlink-file-operations-e2e-owner'),
        $goodMarker,
        [Text.UTF8Encoding]::new($false))
    $message = Get-FailureMessage {
        Remove-OwnedTemporaryTree -ParentPath $selfTestRoot -CandidatePath $badName `
            -ExpectedMarker $goodMarker
    }
    Assert-True ($message -match 'TEMP_CLEANUP_NAME_REJECTED') `
        'Cleanup accepted a candidate without the owned leaf-name pattern.'
    Assert-True (Test-Path -LiteralPath $badName -PathType Container) `
        'Rejected name cleanup changed the candidate.'

    $badMarkerId = [Guid]::NewGuid().ToString('N')
    $badMarkerCandidate = Join-Path $selfTestRoot "termlink-file-operations-e2e-$badMarkerId"
    [IO.Directory]::CreateDirectory($badMarkerCandidate) | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $badMarkerCandidate '.termlink-file-operations-e2e-owner'),
        'different-owner',
        [Text.UTF8Encoding]::new($false))
    $message = Get-FailureMessage {
        Remove-OwnedTemporaryTree -ParentPath $selfTestRoot -CandidatePath $badMarkerCandidate `
            -ExpectedMarker 'expected-owner'
    }
    Assert-True ($message -match 'TEMP_CLEANUP_MARKER_MISMATCH') `
        'Cleanup accepted a mismatched ownership marker.'
    Assert-True (Test-Path -LiteralPath $badMarkerCandidate -PathType Container) `
        'Rejected marker cleanup changed the candidate.'

    $fixtureResult = & $runnerPath -FixtureOnly -TemporaryParentPath $selfTestRoot `
        -ArtifactRunId 'fixture-self-test'
    Assert-True ([string]$fixtureResult.status -eq 'passed' -and
        [string]$fixtureResult.mode -eq 'fixture_only' -and
        [int]$fixtureResult.root_entry_count -eq 505 -and
        [string]$fixtureResult.temporary_tree -eq 'removed') `
        'Fixture-only execution did not validate and remove its controlled tree.'
    $remainingRunnerTrees = @(Get-ChildItem -LiteralPath $selfTestRoot -Directory |
        Where-Object Name -Match '^termlink-file-operations-e2e-[a-f0-9]{32}$')
    Assert-True ($remainingRunnerTrees.Count -eq 1 -and
        $remainingRunnerTrees[0].FullName -eq $badMarkerCandidate) `
        'Fixture-only execution left an unexpected owned runner tree.'
    Assert-True ((Test-Path -LiteralPath $sentinel -PathType Leaf) -and
        [IO.File]::ReadAllText($sentinel) -ceq 'preserve') `
        'Fixture-only execution changed its sibling sentinel.'

    $message = Get-FailureMessage {
        & $runnerPath -FixtureOnly -AcceptanceOrigin ([Uri]'http://example.test') `
            -TemporaryParentPath $selfTestRoot -ArtifactRunId 'bad-origin'
    }
    Assert-True ($message -match 'AcceptanceOrigin') 'Runner accepted a non-HTTPS acceptance origin.'
    $message = Get-FailureMessage {
        & $runnerPath -FixtureOnly -TemporaryParentPath $selfTestRoot -ArtifactRunId '..\escape'
    }
    Assert-True ($message -match 'ArtifactRunId') 'Runner accepted artifact path traversal.'
    $message = Get-FailureMessage {
        & $runnerPath -FixtureOnly -TemporaryParentPath $selfTestRoot -Username "bad`nname" `
            -ArtifactRunId 'bad-username'
    }
    Assert-True ($message -match 'Username') 'Runner accepted a newline in Username.'
    $message = Get-FailureMessage {
        & $runnerPath -FixtureOnly -TemporaryParentPath $selfTestRoot -TimeoutSeconds 59 `
            -ArtifactRunId 'bad-timeout'
    }
    Assert-True ($message -match 'TimeoutSeconds') 'Runner accepted a timeout below its safe minimum.'

    # Compile the embedded standard-library Go driver without touching the
    # repository or making a network request.
    $assignments = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left.Extent.Text -eq '$goSource'
    }, $true))
    Assert-True ($assignments.Count -eq 1) 'The embedded Go source assignment is missing or duplicated.'
    Assert-True ($assignments[0].Right -is [Management.Automation.Language.CommandExpressionAst] -and
        $assignments[0].Right.Expression -is [Management.Automation.Language.StringConstantExpressionAst]) `
        'The embedded Go source is no longer a literal string.'
    $embeddedGo = [string]$assignments[0].Right.Expression.Value
    Assert-True (-not [string]::IsNullOrWhiteSpace($embeddedGo)) 'The embedded Go source is empty.'
    $goSourcePath = Join-Path $selfTestRoot 'embedded-file-operations-e2e.go'
    [IO.File]::WriteAllText($goSourcePath, $embeddedGo, [Text.UTF8Encoding]::new($false))
    $ensureDevEnv = 'D:\Dev\Scripts\Ensure-DevEnv.ps1'
    if (Test-Path -LiteralPath $ensureDevEnv -PathType Leaf) {
        $goMetadata = @(& $ensureDevEnv -Tool go -Json 2>$null) -join "`n" | ConvertFrom-Json
        $goPath = [string]$goMetadata.Path
    }
    else {
        $goPath = [string](Get-Command go -CommandType Application -ErrorAction Stop |
            Select-Object -First 1).Source
    }
    & $goPath test $goSourcePath
    Assert-True ($LASTEXITCODE -eq 0) 'The embedded Go protocol driver did not compile.'

    # Build a disposable process with the exact SessionHost leaf name. Two
    # session IDs are owned by the cleanup plan; a third process is a sentinel
    # proving that exact-ID cleanup does not kill another SessionHost.
    $helperSourcePath = Join-Path $selfTestRoot 'owned-session-host-helper.go'
    $helperPath = Join-Path $selfTestRoot 'TermLink.SessionHost.exe'
    [IO.File]::WriteAllText($helperSourcePath, @'
package main

import "time"

func main() { time.Sleep(30 * time.Second) }
'@, [Text.UTF8Encoding]::new($false))
    & $goPath build -o $helperPath $helperSourcePath
    Assert-True ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $helperPath -PathType Leaf)) `
        'The disposable SessionHost cleanup helper did not build.'
    $ownedSessionIds = @([Guid]::NewGuid(), [Guid]::NewGuid())
    $sentinelSessionId = [Guid]::NewGuid()
    foreach ($sessionId in @($ownedSessionIds) + @($sentinelSessionId)) {
        $process = Start-Process -FilePath $helperPath `
            -ArgumentList @('--managed-session-id', $sessionId.ToString('D')) `
            -WindowStyle Hidden -PassThru
        $ownedHelperProcesses.Add($process)
    }
    $processDeadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $visibleHelpers = @(Get-CimInstance Win32_Process -Filter `
            "Name = 'TermLink.SessionHost.exe'" -ErrorAction Stop | Where-Object {
                [string]$_.ExecutablePath -ceq $helperPath
            })
        if ($visibleHelpers.Count -eq 3) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $processDeadline)
    Assert-True ($visibleHelpers.Count -eq 3) `
        'The disposable SessionHost helpers were not all visible to exact cleanup.'
    $cleanupPlan = Join-Path $selfTestRoot 'terminal-cleanup.json'
    [IO.File]::WriteAllText(
        $cleanupPlan,
        (@{ session_ids = @($ownedSessionIds | ForEach-Object { $_.ToString('D') }) } |
            ConvertTo-Json -Compress),
        [Text.UTF8Encoding]::new($false))
    $cleanup = Stop-OwnedTerminalSessions -CleanupPlanPath $cleanupPlan
    Assert-True (@($cleanup.requested_session_ids).Count -eq 2 -and
        @($cleanup.stopped_session_ids).Count -eq 2 -and
        @($cleanup.unmatched_session_ids).Count -eq 0) `
        'Exact terminal cleanup did not confirm both owned SessionHost exits.'
    $expectedCleanupIds = @($ownedSessionIds | ForEach-Object { $_.ToString('D') } | Sort-Object)
    Assert-True ((@($cleanup.stopped_session_ids | Sort-Object) -join '|') -ceq
        ($expectedCleanupIds -join '|')) `
        'Exact terminal cleanup reported different session IDs.'
    foreach ($process in @($ownedHelperProcesses)[0..1]) {
        $process.Refresh()
        Assert-True $process.HasExited 'An owned SessionHost helper survived exact cleanup.'
    }
    $sentinelProcess = @($ownedHelperProcesses)[2]
    $sentinelProcess.Refresh()
    Assert-True (-not $sentinelProcess.HasExited) `
        'Exact terminal cleanup killed an unlisted SessionHost sentinel.'
}
finally {
    foreach ($process in $ownedHelperProcesses) {
        try {
            $process.Refresh()
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force -ErrorAction Stop
                Wait-Process -Id $process.Id -Timeout 5 -ErrorAction SilentlyContinue
            }
        }
        catch {}
        $process.Dispose()
    }
    if (Test-Path -LiteralPath $selfTestRoot -PathType Container) {
        $resolved = [IO.Path]::GetFullPath($selfTestRoot)
        Assert-True ($resolved.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($resolved) -cmatch
                '^termlink-file-operations-e2e-selftest-[a-f0-9]{32}$') `
            'Self-test cleanup target failed its final safety check.'
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

Write-Output 'File-operations E2E parser, embedded-driver, parameter, cleanup, and safety checks passed.'
