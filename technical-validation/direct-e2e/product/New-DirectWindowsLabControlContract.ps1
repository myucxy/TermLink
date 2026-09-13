[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9._-]{1,180}$')][string]$ControlPipeName,
    [Parameter(Mandatory)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$driver = Join-Path $PSScriptRoot 'Invoke-DirectWindowsLabControl.ps1'
$contract = [pscustomobject][ordered]@{
    schema_version = 1
    kind = 'termlink.windows-lab-control-contract'
    driver_relative_path = [IO.Path]::GetFileName($driver)
    driver_sha256 = (Get-FileHash -LiteralPath $driver -Algorithm SHA256).Hash.ToLowerInvariant()
    driver_protocol = 'bounded_json_file_to_length_prefixed_named_pipe_v1'
    control_pipe_name = $ControlPipeName
    process_scoped = $true
    cleanup_guaranteed = $true
    supported_operations = @('probe', 'start_long_output', 'resize_window', 'stop_long_output', 'cleanup_all')
    selector = [pscustomobject][ordered]@{
        required_fields = @('session_id', 'session_role', 'target_process')
        session_id_format = 'uuid_d_non_empty'
        session_roles = @('primary', 'late')
        exact_session_and_process_required = $true
        excluded_from_evidence = $true
    }
    arbitrary_command_input = $false
}
$json = $contract | ConvertTo-Json -Depth 6
$full = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory((Split-Path -Parent $full)) | Out-Null
[IO.File]::WriteAllText($full, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
