# Verifies private diagnostics from the real Windows Agent, not Android's send count.
function Assert-HostHistoryPeerAck {
    param(
        [Parameter(Mandatory)][string]$LogPath,
        [Parameter(Mandatory)][ValidateRange(1, 2147483647)][int]$AgentPid,
        [Parameter(Mandatory)][guid]$SessionId,
        [Parameter(Mandatory)][guid]$RequestId,
        [Parameter(Mandatory)][ValidateRange(1, 16)][int]$ExpectedChunkCount
    )

    if ($SessionId -eq [guid]::Empty -or $RequestId -eq [guid]::Empty -or
        -not [IO.Path]::IsPathFullyQualified($LogPath) -or
        -not (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
        throw 'PEER_ACK_LOG_INVALID'
    }
    $prefix = '^\S+ host-history-diag peer_ack pid=(?<pid>[1-9][0-9]*) event=(?<event>request_received|ack_accepted|all_chunks_acked|request_released) session=(?<session>[0-9a-f]{32}) sender=(?<sender>[0-9a-f]{32}) request_id=(?<request>[0-9a-f]{32}) kind=StateDelta'
    $session = $SessionId.ToString('N')
    $request = $RequestId.ToString('N')
    $received = @()
    $accepted = @()
    $completed = @()
    $released = @()
    $stream = $null
    try {
        $stream = [IO.FileStream]::new($LogPath, [IO.FileMode]::Open,
            [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        $length = $stream.Length
        if ($length -gt 4MB) { throw 'PEER_ACK_LOG_INVALID' }
        $bytes = [byte[]]::new([int]$length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($read -eq 0) { throw [IO.IOException]::new('Peer ACK log changed while reading') }
            $offset += $read
        }
    }
    catch [IO.IOException] { throw 'PEER_ACK_LOG_READ_TRANSIENT' }
    finally { if ($null -ne $stream) { $stream.Dispose() } }
    $lastNewline = $bytes.Length - 1
    while ($lastNewline -ge 0 -and $bytes[$lastNewline] -ne 10) { $lastNewline-- }
    try { $complete = [Text.UTF8Encoding]::new($false, $true).GetString($bytes, 0, $lastNewline + 1) }
    catch [Text.DecoderFallbackException] { throw 'PEER_ACK_LOG_MALFORMED' }
    $reader = [IO.StringReader]::new($complete)
    while ($null -ne ($line = $reader.ReadLine())) {
        $match = [regex]::Match($line, $prefix)
        if (-not $match.Success) {
            if ($line.Contains('host-history-diag peer_ack') -and
                $line.Contains("pid=$AgentPid") -and $line.Contains("session=$session") -and
                $line.Contains("request_id=$request")) { throw 'PEER_ACK_LOG_MALFORMED' }
            continue
        }
        if ([int]$match.Groups['pid'].Value -ne $AgentPid -or
            $match.Groups['session'].Value -cne $session -or
            $match.Groups['request'].Value -cne $request) { continue }
        $sender = $match.Groups['sender'].Value
        if ($sender -ceq ('0' * 32)) { throw 'PEER_ACK_SENDER_INVALID' }
        $tail = $line.Substring($match.Length)
        switch ($match.Groups['event'].Value) {
            'request_received' {
                if ($tail.Length -ne 0) { throw 'PEER_ACK_LOG_MALFORMED' }
                $received += $sender
            }
            'ack_accepted' {
                if ($tail -cnotmatch '^ object_id=([0-9a-f]{32}) chunk_index=(0|[1-9][0-9]*)$') {
                    throw 'PEER_ACK_LOG_MALFORMED'
                }
                $accepted += [pscustomobject]@{ Sender = $sender; Object = $Matches[1]; Index = [int]$Matches[2] }
            }
            'all_chunks_acked' {
                if ($tail -cnotmatch '^ object_id=([0-9a-f]{32}) count=(0|[1-9][0-9]*)$') {
                    throw 'PEER_ACK_LOG_MALFORMED'
                }
                $completed += [pscustomobject]@{ Sender = $sender; Object = $Matches[1]; Count = [int]$Matches[2] }
            }
            'request_released' {
                if ($tail -cnotmatch '^ fully_delivered=(true|false) request_removed=(true|false) delta_removed=(true|false) gate_released=(true|false) disposed=(true|false)$') {
                    throw 'PEER_ACK_LOG_MALFORMED'
                }
                $released += [pscustomobject]@{ Sender = $sender; Flags = @($Matches[1], $Matches[2], $Matches[3], $Matches[4], $Matches[5]) }
            }
        }
    }
    if ($received.Count -ne 1 -or $completed.Count -ne 1 -or $released.Count -ne 1 -or
        $accepted.Count -ne $ExpectedChunkCount) { throw 'PEER_ACK_EVIDENCE_INCOMPLETE' }
    $sender = $received[0]
    $objectId = $completed[0].Object
    if ($objectId -ceq ('0' * 32) -or $completed[0].Count -ne $ExpectedChunkCount -or
        $completed[0].Sender -cne $sender -or $released[0].Sender -cne $sender -or
        @($released[0].Flags | Where-Object { $_ -cne 'true' }).Count -ne 0) {
        throw 'PEER_ACK_COMPLETION_INVALID'
    }
    $indices = @{}
    foreach ($item in $accepted) {
        if ($item.Sender -cne $sender -or $item.Object -cne $objectId -or
            $item.Index -lt 0 -or $item.Index -ge $ExpectedChunkCount -or
            $indices.ContainsKey($item.Index)) { throw 'PEER_ACK_CHUNK_INVALID' }
        $indices[$item.Index] = $true
    }
    return [pscustomobject]@{
        agent_pid = $AgentPid
        session_id = $SessionId.ToString('D')
        request_id = $RequestId.ToString('D')
        sender_id = ([guid]::ParseExact($sender, 'N')).ToString('D')
        object_id = ([guid]::ParseExact($objectId, 'N')).ToString('D')
        chunk_count = $ExpectedChunkCount
        peer_ack_verified = $true
        request_released = $true
        agent_request_budget_released = $true
    }
}
