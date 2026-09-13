[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WindowsAgentPath,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9._-]{1,180}$')][string]$ControlPipeName,
    [Parameter(Mandatory)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$agent = [IO.Path]::GetFullPath($WindowsAgentPath)
$driver = Join-Path $PSScriptRoot 'Invoke-DirectWindowsDiagnostics.ps1'
$contract = [pscustomobject][ordered]@{
    schema_version = 1
    kind = 'termlink.windows-direct-diagnostics-contract'
    executable_sha256 = (Get-FileHash -LiteralPath $agent -Algorithm SHA256).Hash.ToLowerInvariant()
    safe_control_interface = $true
    driver_relative_path = [IO.Path]::GetFileName($driver)
    driver_sha256 = (Get-FileHash -LiteralPath $driver -Algorithm SHA256).Hash.ToLowerInvariant()
    driver_protocol = 'bounded_json_file_to_length_prefixed_named_pipe_v1'
    length_prefix = 'uint32_little_endian'
    request_max_bytes = 65536
    response_max_bytes = 1048576
    control_pipe_name = $ControlPipeName
    request_operations = @('probe', 'inspect')
    inspect_selector = [pscustomobject][ordered]@{
        required_fields = @('session_id', 'session_role', 'peer_device_id')
        session_id_format = 'uuid_d_non_empty'
        peer_device_id_format = 'uuid_d_non_empty'
        session_roles = @('primary', 'late')
        exact_session_role_and_peer_required = $true
        unknown_or_mismatched_selector_fails_closed = $true
        probe_forbids_selector = $true
        excluded_from_evidence = $true
    }
    required_runtime_capabilities = @('direct_transport_data_v1', 'direct_session_cutover_v1')
    diagnostics = @(
        'agent_pid', 'agent_start_time', 'carrier', 'contiguous_applied_sequence',
        'digest_conflict_count', 'direct_frame_received_count', 'direct_frame_sent_count',
        'direct_receipt_count', 'duplicate_apply_count', 'duplicate_drop_count', 'durable_ack',
        'fallback_reason', 'gap_count', 'last_received_sequence', 'outbox_depth', 'path_epoch',
        'process_image_sha256', 'queue_depth', 'relay_generation', 'relay_replay_bytes',
        'relay_replay_count', 'session_host_pid', 'session_host_start_time',
        'session_host_image_sha256', 'signalling_generation', 'transport_receipt_high_watermark'
    )
}
$json = $contract | ConvertTo-Json -Depth 6
$full = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory((Split-Path -Parent $full)) | Out-Null
[IO.File]::WriteAllText($full, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
