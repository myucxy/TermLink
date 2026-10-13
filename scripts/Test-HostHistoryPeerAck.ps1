$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Assert-HostHistoryPeerAck.ps1')

$session = [guid]::NewGuid()
$sender = [guid]::NewGuid()
$request = [guid]::NewGuid()
$object = [guid]::NewGuid()
$other = [guid]::NewGuid()
$pidValue = 32145
$log = Join-Path ([IO.Path]::GetTempPath()) ('termlink-peer-ack-' + [guid]::NewGuid().ToString('N') + '.log')
$identity = ' session=' + $session.ToString('N') + ' sender=' + $sender.ToString('N') +
    ' request_id=' + $request.ToString('N') + ' kind=StateDelta'
$head = '2026-10-11T00:00:00.0000000Z host-history-diag peer_ack pid=' + $pidValue + ' event='
$events = @(
    ($head + 'request_received' + $identity),
    ($head + 'ack_accepted' + $identity + ' object_id=' + $object.ToString('N') + ' chunk_index=0'),
    ($head + 'all_chunks_acked' + $identity + ' object_id=' + $object.ToString('N') + ' count=2'),
    ($head + 'request_released' + $identity +
        ' fully_delivered=true request_removed=true delta_removed=true gate_released=true disposed=true'),
    ($head + 'ack_accepted' + $identity + ' object_id=' + $object.ToString('N') + ' chunk_index=1')
)
function Write-Events([string[]]$lines) {
    [IO.File]::WriteAllLines($log, $lines, [Text.UTF8Encoding]::new($false))
}
function Check-Events([string[]]$lines) {
    Write-Events $lines
    Assert-HostHistoryPeerAck -LogPath $log -AgentPid $pidValue -SessionId $session `
        -RequestId $request -ExpectedChunkCount 2
}
function Expect-Fail([string[]]$lines) {
    $rejected = $false
    try { $null = Check-Events $lines } catch { $rejected = $true }
    if (-not $rejected) { throw 'Invalid peer ACK evidence was accepted' }
}
function Expect-AssertionCode([string]$expected) {
    try {
        $null = Assert-HostHistoryPeerAck -LogPath $log -AgentPid $pidValue -SessionId $session `
            -RequestId $request -ExpectedChunkCount 2
    } catch {
        if ($_.Exception.Message -ceq $expected) { return }
        throw
    }
    throw "Expected peer ACK assertion error $expected"
}

try {
    $result = Check-Events $events
    if (-not $result.peer_ack_verified -or -not $result.request_released -or
        -not $result.agent_request_budget_released -or
        $result.chunk_count -ne 2 -or $result.object_id -cne $object.ToString('D')) {
        throw 'Valid peer ACK evidence was not returned'
    }
    Expect-Fail @($events | Where-Object { $_ -notlike '*chunk_index=1' })
    Expect-Fail @($events | ForEach-Object { $_.Replace($request.ToString('N'), $other.ToString('N')) })
    Expect-Fail @($events | Where-Object { $_ -notlike '*event=request_released*' })
    Expect-Fail @($events | ForEach-Object { $_.Replace('delta_removed=true', 'delta_removed=false') })
    Expect-Fail @($events | ForEach-Object { $_.Replace('chunk_index=1', 'chunk_index=0') })
    Expect-Fail @($events | ForEach-Object { $_.Replace($object.ToString('N') + ' chunk_index=1',
        $other.ToString('N') + ' chunk_index=1') })
    Write-Events @($events | Where-Object { $_ -notlike '*chunk_index=1' })
    [IO.File]::AppendAllText($log, $events[-1])
    Expect-AssertionCode 'PEER_ACK_EVIDENCE_INCOMPLETE'
    [IO.File]::AppendAllText($log, [Environment]::NewLine)
    $null = Assert-HostHistoryPeerAck -LogPath $log -AgentPid $pidValue -SessionId $session `
        -RequestId $request -ExpectedChunkCount 2
    Write-Events $events
    [IO.File]::AppendAllText($log, $head + 'unknown_event' + $identity)
    $null = Assert-HostHistoryPeerAck -LogPath $log -AgentPid $pidValue -SessionId $session `
        -RequestId $request -ExpectedChunkCount 2
    [IO.File]::AppendAllText($log, [Environment]::NewLine)
    Expect-AssertionCode 'PEER_ACK_LOG_MALFORMED'
    Write-Events $events
    $writing = [IO.FileStream]::new($log, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try {
        $null = Assert-HostHistoryPeerAck -LogPath $log -AgentPid $pidValue -SessionId $session `
            -RequestId $request -ExpectedChunkCount 2
    } finally { $writing.Dispose() }
    $held = [IO.File]::Open($log, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try { Expect-AssertionCode 'PEER_ACK_LOG_READ_TRANSIENT' }
    finally { $held.Dispose() }
    [IO.File]::WriteAllText($log, 'x' * (4MB + 1))
    Expect-AssertionCode 'PEER_ACK_LOG_INVALID'
    $null = Check-Events $events
    Write-Output 'Host History peer ACK assertions passed.'
} finally {
    if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
}
