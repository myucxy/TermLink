[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9._-]{1,180}$')][string]$ControlPipeName,
    [Parameter(Mandatory)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$driver = Join-Path $PSScriptRoot 'Invoke-DirectProcessFaultAdapter.ps1'
$matrix = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'direct-product-e2e.matrix.v1.json') -Raw -Encoding UTF8 |
    ConvertFrom-Json -ErrorAction Stop
$actions = @($matrix.scenarios.required_fault_actions | ForEach-Object { [string]$_ } | Sort-Object -Unique)
$contract = [pscustomobject][ordered]@{
    schema_version = 1
    kind = 'termlink.direct-fault-adapter-contract'
    adapter_relative_path = [IO.Path]::GetFileName($driver)
    adapter_sha256 = (Get-FileHash -LiteralPath $driver -Algorithm SHA256).Hash.ToLowerInvariant()
    adapter_protocol = 'bounded_json_file_to_length_prefixed_named_pipe_v1'
    control_pipe_name = $ControlPipeName
    process_scoped = $true
    cleanup_guaranteed = $true
    lease_scoped = $true
    isolated_channels = @('direct_quic', 'direct_udp', 'relay_wss', 'signalling_wss')
    supported_actions = $actions
    peer_targeting = [pscustomobject][ordered]@{
        actions = @('interrupt_one_peer_direct_quic', 'restore_one_peer_direct_quic')
        required_fields = @('target_peer_device_id', 'target_session_id')
        uuid_format = 'uuid_d_non_empty'
        exact_peer_and_session_required = $true
        excluded_from_evidence = $true
    }
}
$json = $contract | ConvertTo-Json -Depth 6
$full = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory((Split-Path -Parent $full)) | Out-Null
[IO.File]::WriteAllText($full, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
