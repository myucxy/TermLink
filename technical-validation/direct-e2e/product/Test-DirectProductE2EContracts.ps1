[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = $PSScriptRoot
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('termlink-direct-product-e2e-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null

function Assert-Contract {
    param([bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw [InvalidOperationException]::new($Message) }
}

function New-TestScenario {
    param([Parameter(Mandatory)][string]$Id, [string]$Status = 'not_run', [bool]$Executed = $false)
    $carrierSequence = @()
    if ($Executed) { $carrierSequence = @('relay') }
    return [pscustomobject][ordered]@{
        id = $Id
        executed = $Executed
        status = $Status
        reason_code = if ($Status -in @('skipped', 'blocked', 'failed')) { 'TEST_REASON' } else { $null }
        expected_carrier_sequence = @('relay')
        carrier_sequence = $carrierSequence
        fault_actions = @()
        fault_actions_executed = @()
        assertions = @()
        samples = @()
    }
}

function New-TestEvidence {
    param([Parameter(Mandatory)][string]$Outcome, [Parameter(Mandatory)]$Scenarios)
    $zeros = '0' * 64
    return [pscustomobject][ordered]@{
        schema_version = 1
        kind = 'termlink.direct-product-e2e-evidence'
        run_id = 'contract-test'
        matrix_version = 1
        scope = if ($Outcome -ceq 'preflight_passed') { 'preflight_only' } else { 'full_matrix' }
        outcome = $Outcome
        real_network_test_executed = @($Scenarios | Where-Object executed).Count -gt 0
        started_utc = [DateTime]::UtcNow.ToString('o')
        completed_utc = [DateTime]::UtcNow.ToString('o')
        artifacts = [pscustomobject]@{
            android = [pscustomobject]@{
                sha256 = $zeros; contract_schema_version = 1; application_id_sha256 = $zeros
                receiver_class_sha256 = $zeros; data_plane_acceptance = $true
            }
            windows = [pscustomobject]@{
                sha256 = $zeros; assembly_configuration = 'DirectAcceptance'; data_plane_acceptance = $true
            }
        }
        server = [pscustomobject]@{
            origin_sha256 = $zeros; build_sha256 = $zeros; acceptance_environment = $true
            direct_transport_enabled = $true; direct_routing_permitted = $true; capabilities = @()
        }
        devices = @(
            [pscustomobject]@{ role = 'primary_android'; adapter_ready = $true; hook_ready = $true },
            [pscustomobject]@{ role = 'secondary_android'; adapter_ready = $true; hook_ready = $true }
        )
        diagnostics = [pscustomobject]@{
            android_required = @(); windows_required = @(); android_available = @(); windows_available = @()
        }
        fault_adapter = [pscustomobject]@{
            ready = $true; process_scoped = $true; cleanup_guaranteed = $true
            isolated_channels = @(); supported_actions = @()
        }
        checks = @()
        scenarios = $Scenarios
        failure = $null
    }
}

function New-TestSample {
    param([Parameter(Mandatory)][ValidateSet('primary', 'late')][string]$SessionRole)
    $zeros = '0' * 64
    $observedUtc = [DateTime]::UtcNow.ToString('o')
    $appProcess = [pscustomobject][ordered]@{
        pid = 1
        start_time_utc = $observedUtc
    }
    $process = [pscustomobject][ordered]@{
        pid = 1
        start_time_utc = $observedUtc
        image_sha256 = $zeros
    }
    return [pscustomobject][ordered]@{
        phase = 'contract_sample'
        device_role = 'primary_android'
        session_role = $SessionRole
        observed_utc = $observedUtc
        carrier = 'relay'
        candidate_kinds = @('relay')
        fallback_reason = ''
        path_epoch = 0
        relay_generation = 0
        signalling_generation = 0
        network_generation = 0
        app_process = $appProcess
        agent_process = $process
        session_host_process = $process
        last_received_sequence = 0
        contiguous_applied_sequence = 0
        durable_ack = 0
        transport_receipt_high_watermark = 0
        gap_count = 0
        duplicate_drop_count = 0
        duplicate_apply_count = 0
        digest_conflict_count = 0
        marker_digest = $zeros
        viewport_digest = $zeros
        direct_frame_received_count = 0
        direct_frame_sent_count = 0
        direct_receipt_count = 0
        outbox_depth = 0
        queue_depth = 0
        relay_replay_bytes = 0
        relay_replay_count = 0
    }
}

function New-TestSamplePair {
    $first = New-TestSample 'primary'
    $second = $first | ConvertTo-Json -Depth 12 | ConvertFrom-Json -ErrorAction Stop
    $second.phase = 'contract_sample_after'
    $second.observed_utc = ([DateTimeOffset]::Parse([string]$first.observed_utc)).AddSeconds(1).ToString('o')
    return @($first, $second)
}

function Write-TestJson {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Value
    )
    [IO.File]::WriteAllText(
        $Path,
        ($Value | ConvertTo-Json -Depth 12 -Compress),
        [Text.UTF8Encoding]::new($false)
    )
}

function Invoke-DiagnosticsContractRoundTrip {
    param(
        [Parameter(Mandatory)]$Request,
        [Parameter(Mandatory)][string[]]$ExpectedProperties,
        [Parameter(Mandatory)][ValidateSet('ready', 'observed', 'blocked')][string]$ResponseStatus,
        [Parameter(Mandatory)][bool]$ResponseOk,
        [string]$ResponseReason = ''
    )
    $pipeName = 'termlink-direct-diagnostics-contract-' + [Guid]::NewGuid().ToString('N')
    $requestPath = Join-Path $temporaryRoot (([string]$Request.request_id) + '-request.json')
    $responsePath = Join-Path $temporaryRoot (([string]$Request.request_id) + '-response.json')
    Write-TestJson $requestPath $Request
    $expectedPropertiesJson = $ExpectedProperties | ConvertTo-Json -Compress
    $backend = Start-Job -ArgumentList @(
        $pipeName, $expectedPropertiesJson, $ResponseStatus, $ResponseOk, $ResponseReason
    ) -ScriptBlock {
        param($PipeName, $ExpectedPropertiesJson, $ResponseStatus, $ResponseOk, $ResponseReason)
        $ExpectedProperties = @($ExpectedPropertiesJson | ConvertFrom-Json -ErrorAction Stop)
        $pipe = [IO.Pipes.NamedPipeServerStream]::new(
            $PipeName, [IO.Pipes.PipeDirection]::InOut, 1,
            [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::None
        )
        try {
            $pipe.WaitForConnection()
            $lengthBytes = [byte[]]::new(4)
            $offset = 0
            while ($offset -lt 4) {
                $read = $pipe.Read($lengthBytes, $offset, 4 - $offset)
                if ($read -le 0) { throw 'diagnostics request length missing' }
                $offset += $read
            }
            $length = [uint32]$lengthBytes[0] -bor
                ([uint32]$lengthBytes[1] -shl 8) -bor
                ([uint32]$lengthBytes[2] -shl 16) -bor
                ([uint32]$lengthBytes[3] -shl 24)
            if ($length -lt 2 -or $length -gt 65536) { throw 'diagnostics request length is not bounded' }
            $payload = [byte[]]::new([int]$length)
            $offset = 0
            while ($offset -lt $payload.Length) {
                $read = $pipe.Read($payload, $offset, $payload.Length - $offset)
                if ($read -le 0) { throw 'diagnostics request payload missing' }
                $offset += $read
            }
            $request = [Text.Encoding]::UTF8.GetString($payload) | ConvertFrom-Json -ErrorAction Stop
            $actualProperties = @($request.PSObject.Properties.Name)
            if ($actualProperties.Count -ne $ExpectedProperties.Count -or
                @($actualProperties | Where-Object { $_ -cnotin $ExpectedProperties }).Count -ne 0) {
                throw 'diagnostics request property set differs from the operation contract'
            }
            if ([string]$request.operation -ceq 'probe') {
                if ($actualProperties -contains 'session_id' -or $actualProperties -contains 'session_role' -or
                    $actualProperties -contains 'peer_device_id') {
                    throw 'probe leaked a session selector'
                }
            } elseif ([string]$request.operation -ceq 'inspect') {
                $parsedSessionId = [Guid]::Empty
                $parsedPeerDeviceId = [Guid]::Empty
                if (-not [Guid]::TryParseExact([string]$request.session_id, 'D', [ref]$parsedSessionId) -or
                    $parsedSessionId -eq [Guid]::Empty -or
                    [string]$request.session_role -cnotin @('primary', 'late') -or
                    -not [Guid]::TryParseExact([string]$request.peer_device_id, 'D', [ref]$parsedPeerDeviceId) -or
                    $parsedPeerDeviceId -eq [Guid]::Empty) {
                    throw 'inspect did not carry a valid exact session selector'
                }
            } else {
                throw 'unexpected diagnostics operation'
            }

            $responseValue = [pscustomobject][ordered]@{
                schema_version = 1
                request_id = [string]$request.request_id
                ok = [bool]$ResponseOk
                status = [string]$ResponseStatus
                reason_code = if ([string]::IsNullOrEmpty($ResponseReason)) { $null } else { $ResponseReason }
                diagnostics = [pscustomobject]@{}
            }
            $response = [Text.Encoding]::UTF8.GetBytes(($responseValue | ConvertTo-Json -Depth 4 -Compress))
            if ($response.Length -gt 1048576) { throw 'diagnostics response is not bounded' }
            $responseLength = [uint32]$response.Length
            $responseLengthBytes = [byte[]]@(
                ($responseLength -band 0xff),
                (($responseLength -shr 8) -band 0xff),
                (($responseLength -shr 16) -band 0xff),
                (($responseLength -shr 24) -band 0xff)
            )
            $pipe.Write($responseLengthBytes, 0, 4)
            $pipe.Write($response, 0, $response.Length)
            $pipe.Flush()
            [Array]::Clear($payload, 0, $payload.Length)
            return $request
        }
        finally { $pipe.Dispose() }
    }
    try {
        $global:LASTEXITCODE = 0
        & (Join-Path $root 'Invoke-DirectWindowsDiagnostics.ps1') `
            -RequestPath $requestPath -ResponsePath $responsePath -ControlPipeName $pipeName `
            -TimeoutMilliseconds 10000
        $driverExitCode = $LASTEXITCODE
        $driverResult = if (Test-Path -LiteralPath $responsePath -PathType Leaf) {
            Get-Content -LiteralPath $responsePath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
        } else {
            $null
        }
        $driverReason = if ($null -ne $driverResult) { [string]$driverResult.reason_code } else { 'NO_RESPONSE' }
        Wait-Job $backend -Timeout 10 | Out-Null
        $backendFailure = ''
        try { $forwardedRequest = Receive-Job $backend -ErrorAction Stop }
        catch { $backendFailure = $_.Exception.Message }
        Assert-Contract ($driverExitCode -eq 0) `
            "valid diagnostics request failed in the driver: $driverReason; backend: $backendFailure"
        Assert-Contract ([string]::IsNullOrEmpty($backendFailure)) `
            "diagnostics backend contract failed: $backendFailure"
        return [pscustomobject][ordered]@{ request = $forwardedRequest; response = $driverResult }
    }
    finally { Remove-Job $backend -Force -ErrorAction SilentlyContinue }
}

function Assert-InvalidDiagnosticsRequest {
    param(
        [Parameter(Mandatory)]$Request,
        [Parameter(Mandatory)][string]$CaseName
    )
    $requestPath = Join-Path $temporaryRoot ($CaseName + '-invalid-request.json')
    $responsePath = Join-Path $temporaryRoot ($CaseName + '-invalid-response.json')
    Write-TestJson $requestPath $Request
    $global:LASTEXITCODE = 0
    & (Join-Path $root 'Invoke-DirectWindowsDiagnostics.ps1') `
        -RequestPath $requestPath -ResponsePath $responsePath `
        -ControlPipeName ('must-not-connect-' + [Guid]::NewGuid().ToString('N')) `
        -TimeoutMilliseconds 1000
    Assert-Contract ($LASTEXITCODE -eq 2) "$CaseName was not rejected"
    $global:LASTEXITCODE = 0
    $response = Get-Content -LiteralPath $responsePath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
    Assert-Contract (-not [bool]$response.ok -and [string]$response.status -ceq 'blocked' -and
        [string]$response.reason_code -ceq 'WINDOWS_DIAGNOSTICS_REQUEST_INVALID') `
        "$CaseName did not fail closed as an invalid request: $([string]$response.reason_code)"
}

function Invoke-RunnerAssertionEvaluation {
    param(
        [Parameter(Mandatory)][string]$RunnerText,
        [Parameter(Mandatory)][object[]]$Samples,
        [Parameter(Mandatory)][string[]]$AssertionIds
    )
    $tokens = $null
    $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput(
        $RunnerText,
        [ref]$tokens,
        [ref]$parseErrors
    )
    Assert-Contract (@($parseErrors).Count -eq 0) 'runner could not be parsed for assertion contract tests'
    $requiredFunctions = @(
        'Get-ProcessIdentityKey',
        'Test-GroupedIdentityStable',
        'Test-MonotonicBySession',
        'Complete-ScenarioAssertions'
    )
    $functionAsts = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -cin $requiredFunctions
    }, $true))
    Assert-Contract ($functionAsts.Count -eq $requiredFunctions.Count) `
        'runner assertion functions are missing or duplicated'
    $definitions = ($functionAsts | ForEach-Object { $_.Extent.Text }) -join [Environment]::NewLine
    return & {
        param($Definitions, $ScenarioSamples, $Ids)
        . ([scriptblock]::Create($Definitions))
        $scenario = [pscustomobject][ordered]@{
            assertions = @($Ids | ForEach-Object {
                [pscustomobject][ordered]@{ id = [string]$_; result = 'not_run' }
            })
            samples = @($ScenarioSamples)
        }
        $evaluation = Complete-ScenarioAssertions $scenario @{}
        return [pscustomobject][ordered]@{
            complete = [bool]$evaluation.complete
            failed = [bool]$evaluation.failed
            assertions = @($scenario.assertions)
        }
    } $definitions $Samples $AssertionIds
}

try {
    $schemaPath = Join-Path $root 'direct-product-e2e.evidence.schema.v1.json'
    $matrix = Get-Content -LiteralPath (Join-Path $root 'direct-product-e2e.matrix.v1.json') -Raw -Encoding UTF8 |
        ConvertFrom-Json -ErrorAction Stop
    Assert-Contract (@($matrix.scenarios).Count -ge 11) 'late-session scenario is missing'

    $preflightScenarios = @($matrix.scenarios | ForEach-Object { New-TestScenario ([string]$_.id) })
    $preflightJson = New-TestEvidence 'preflight_passed' $preflightScenarios | ConvertTo-Json -Depth 16
    Assert-Contract ([bool]($preflightJson | Test-Json -SchemaFile $schemaPath -ErrorAction SilentlyContinue)) `
        'valid preflight evidence was rejected'

    $incompleteScenarios = @($matrix.scenarios | ForEach-Object { New-TestScenario ([string]$_.id) 'skipped' })
    $incompleteJson = New-TestEvidence 'product_e2e_incomplete' $incompleteScenarios | ConvertTo-Json -Depth 16
    Assert-Contract ([bool]($incompleteJson | Test-Json -SchemaFile $schemaPath -ErrorAction SilentlyContinue)) `
        'valid skipped evidence was rejected'

    $invalidScenarios = @($matrix.scenarios | ForEach-Object { New-TestScenario ([string]$_.id) })
    $invalidScenarios[0].status = 'passed'
    $invalidJson = New-TestEvidence 'product_e2e_incomplete' $invalidScenarios | ConvertTo-Json -Depth 16
    Assert-Contract (-not [bool]($invalidJson | Test-Json -SchemaFile $schemaPath -ErrorAction SilentlyContinue)) `
        'schema accepted a passed scenario that was not executed'

    $sampledScenarios = @($matrix.scenarios | ForEach-Object { New-TestScenario ([string]$_.id) })
    $sampledScenarios[0] = New-TestScenario ([string]$matrix.scenarios[0].id) 'passed' $true
    $sampledScenarios[0].samples = @(New-TestSample 'primary')
    $sampledJson = New-TestEvidence 'product_e2e_incomplete' $sampledScenarios | ConvertTo-Json -Depth 16
    Assert-Contract ([bool]($sampledJson | Test-Json -SchemaFile $schemaPath -ErrorAction SilentlyContinue)) `
        'valid role-labelled evidence sample was rejected'
    Assert-Contract ($sampledJson -notmatch '(?i)[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}') `
        'role-labelled final evidence unexpectedly contains a raw UUID'

    $blockedExecutedScenarios = @($matrix.scenarios | ForEach-Object { New-TestScenario ([string]$_.id) })
    $blockedExecutedScenarios[0] = New-TestScenario ([string]$matrix.scenarios[0].id) 'blocked' $true
    $blockedExecutedScenarios[0].samples = @(New-TestSample 'primary')
    $blockedExecutedJson = New-TestEvidence 'product_e2e_incomplete' $blockedExecutedScenarios |
        ConvertTo-Json -Depth 16
    Assert-Contract ([bool]($blockedExecutedJson | Test-Json -SchemaFile $schemaPath -ErrorAction SilentlyContinue)) `
        'schema rejected an executed scenario blocked by unavailable proof'

    $runnerText = Get-Content -LiteralPath (Join-Path $root 'Invoke-DirectProductE2E.ps1') -Raw -Encoding UTF8
    Assert-Contract ($runnerText -notmatch 'FULL_MATRIX_EXECUTION_ADAPTER_NOT_IMPLEMENTED') `
        'FullMatrix still contains the fixed not-implemented failure'
    foreach ($required in @('probe_topology', 'restore_all', 'expected_carrier_sequence', 'product_e2e_incomplete')) {
        Assert-Contract ($runnerText.Contains($required)) "runner omits $required"
    }

    $schema = Get-Content -LiteralPath $schemaPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
    $sampleContract = $schema.'$defs'.sample
    Assert-Contract (@($sampleContract.required) -ccontains 'session_role') `
        'each evidence sample must identify its primary or late session role'
    Assert-Contract ($null -ne $sampleContract.properties.session_role -and
        @($sampleContract.properties.session_role.enum).Count -eq 2 -and
        @($sampleContract.properties.session_role.enum) -ccontains 'primary' -and
        @($sampleContract.properties.session_role.enum) -ccontains 'late') `
        'evidence sample session_role must be restricted to primary or late'
    Assert-Contract ($null -eq $sampleContract.properties.PSObject.Properties['session_id']) `
        'raw session UUID is permitted in diagnostics requests, not evidence samples'
    Assert-Contract ($null -eq $sampleContract.properties.PSObject.Properties['peer_device_id']) `
        'raw peer device UUID is permitted in diagnostics requests, not evidence samples'
    $sampleBuilder = [regex]::Match(
        $runnerText,
        '(?ms)^function ConvertTo-ScenarioSample\s*\{(?<body>.*?)^\}'
    )
    Assert-Contract ($sampleBuilder.Success -and $sampleBuilder.Groups['body'].Value -match '\bsession_role\s*=') `
        'runner does not project the primary or late session role into each evidence sample'
    Assert-Contract ($sampleBuilder.Groups['body'].Value -notmatch '\bsession_id\s*=') `
        'runner projects raw session UUID into final evidence'
    Assert-Contract ($sampleBuilder.Groups['body'].Value -notmatch '\bpeer_device_id\s*=') `
        'runner projects raw peer device UUID into final evidence'
    Assert-Contract ($preflightJson -notmatch '(?i)[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}') `
        'final evidence unexpectedly contains a raw session UUID'

    $assertionBuilder = [regex]::Match(
        $runnerText,
        '(?ms)^function Complete-ScenarioAssertions\s*\{(?<body>.*?)^\}'
    )
    Assert-Contract ($assertionBuilder.Success) 'runner local assertion evaluator is missing'
    Assert-Contract ($runnerText -notmatch '(?i)\$verification\s*\.\s*(assertions?|assertion_results?)' -and
        $assertionBuilder.Groups['body'].Value -notmatch '(?i)faultadapter|fault_adapter|\$verification') `
        'runner still treats a fault-adapter assertion Boolean as product evidence'

    $gapSamples = New-TestSamplePair
    $gapSamples[1].gap_count = 1
    $gapEvaluation = Invoke-RunnerAssertionEvaluation $runnerText $gapSamples @('no_history_gap')
    Assert-Contract ([bool]$gapEvaluation.failed -and
        [string]$gapEvaluation.assertions[0].result -ceq 'failed') `
        'runner local assertions accepted a non-zero history gap'

    $duplicateSamples = New-TestSamplePair
    $duplicateSamples[1].duplicate_apply_count = 1
    $duplicateEvaluation = Invoke-RunnerAssertionEvaluation $runnerText $duplicateSamples @('no_duplicate_apply')
    Assert-Contract ([bool]$duplicateEvaluation.failed -and
        [string]$duplicateEvaluation.assertions[0].result -ceq 'failed') `
        'runner local assertions accepted a duplicate terminal apply'

    $agentChangeSamples = New-TestSamplePair
    $agentChangeSamples[1].agent_process.pid = 2
    $agentChangeEvaluation = Invoke-RunnerAssertionEvaluation $runnerText $agentChangeSamples `
        @('agent_process_unchanged')
    Assert-Contract ([bool]$agentChangeEvaluation.failed -and
        [string]$agentChangeEvaluation.assertions[0].result -ceq 'failed') `
        ('runner local assertions accepted an Agent process change: ' +
            ($agentChangeEvaluation | ConvertTo-Json -Depth 6 -Compress))

    $sessionHostChangeSamples = New-TestSamplePair
    $sessionHostChangeSamples[1].session_host_process.pid = 2
    $sessionHostChangeEvaluation = Invoke-RunnerAssertionEvaluation $runnerText $sessionHostChangeSamples `
        @('session_host_process_unchanged')
    Assert-Contract ([bool]$sessionHostChangeEvaluation.failed -and
        [string]$sessionHostChangeEvaluation.assertions[0].result -ceq 'failed') `
        'runner local assertions accepted a SessionHost process change'

    $appChangeSamples = New-TestSamplePair
    $appChangeSamples[1].app_process.pid = 2
    $appChangeEvaluation = Invoke-RunnerAssertionEvaluation $runnerText $appChangeSamples `
        @('session_resumes_without_recreate')
    Assert-Contract ([bool]$appChangeEvaluation.failed -and
        [string]$appChangeEvaluation.assertions[0].result -ceq 'failed') `
        'runner local assertions accepted an Android app process change'

    $unsupportedEvaluation = Invoke-RunnerAssertionEvaluation $runnerText (New-TestSamplePair) `
        @('frame_set_complete')
    Assert-Contract (-not [bool]$unsupportedEvaluation.complete -and
        -not [bool]$unsupportedEvaluation.failed -and
        [string]$unsupportedEvaluation.assertions[0].result -ceq 'not_run' -and
        $runnerText -match '(?ms)if \(-not \[bool\]\$assertionResult\.complete\).*?status = ''blocked''.*?reason_code = ''ASSERTION_EVIDENCE_UNAVAILABLE''') `
        'an assertion without product evidence does not deterministically block the scenario'

    $contractPath = Join-Path $temporaryRoot 'windows-diagnostics-contract.json'
    & (Join-Path $root 'New-DirectWindowsDiagnosticsContract.ps1') `
        -WindowsAgentPath (Get-Process -Id $PID).Path `
        -ControlPipeName 'termlink-direct-diagnostics-contract' -OutputPath $contractPath
    $windowsContract = Get-Content -LiteralPath $contractPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
    Assert-Contract ([string]$windowsContract.driver_protocol -ceq `
        'bounded_json_file_to_length_prefixed_named_pipe_v1') `
        'Windows diagnostics contract no longer declares bounded length-prefixed JSON'
    Assert-Contract ([string]$windowsContract.length_prefix -ceq 'uint32_little_endian' -and
        [long]$windowsContract.request_max_bytes -eq 65536 -and
        [long]$windowsContract.response_max_bytes -eq 1048576) `
        'Windows diagnostics contract no longer declares its little-endian bounded framing'
    Assert-Contract ([string]$windowsContract.inspect_selector.session_id_format -ceq 'uuid_d_non_empty' -and
        [string]$windowsContract.inspect_selector.peer_device_id_format -ceq 'uuid_d_non_empty' -and
        @($windowsContract.request_operations).Count -eq 2 -and
        @($windowsContract.request_operations) -ccontains 'probe' -and
        @($windowsContract.request_operations) -ccontains 'inspect' -and
        @($windowsContract.inspect_selector.required_fields).Count -eq 3 -and
        @($windowsContract.inspect_selector.required_fields) -ccontains 'session_id' -and
        @($windowsContract.inspect_selector.required_fields) -ccontains 'session_role' -and
        @($windowsContract.inspect_selector.required_fields) -ccontains 'peer_device_id' -and
        [bool]$windowsContract.inspect_selector.exact_session_role_and_peer_required -and
        [bool]$windowsContract.inspect_selector.unknown_or_mismatched_selector_fails_closed -and
        [bool]$windowsContract.inspect_selector.probe_forbids_selector -and
        [bool]$windowsContract.inspect_selector.excluded_from_evidence -and
        @($windowsContract.inspect_selector.session_roles) -ccontains 'primary' -and
        @($windowsContract.inspect_selector.session_roles) -ccontains 'late') `
        'Windows diagnostics contract does not enforce the inspect selector boundary'

    $diagnosticsBase = [ordered]@{
        schema_version = 1
        kind = 'termlink.windows-direct-diagnostics-request'
        request_id = [Guid]::NewGuid().ToString('N')
        run_id = 'contract-test'
        scenario_id = 'selector_contract'
    }
    $probeRequest = [pscustomobject]($diagnosticsBase + [ordered]@{ operation = 'probe' })
    $baseDiagnosticsProperties = @('schema_version', 'kind', 'request_id', 'run_id', 'scenario_id', 'operation')
    $probeResult = Invoke-DiagnosticsContractRoundTrip $probeRequest $baseDiagnosticsProperties `
        'ready' $true
    Assert-Contract ([bool]$probeResult.response.ok -and [string]$probeResult.response.status -ceq 'ready') `
        'valid selector-free diagnostics probe did not round-trip'

    $selectorSessionId = [Guid]::NewGuid().ToString('D')
    $selectorPeerDeviceId = [Guid]::NewGuid().ToString('D')
    $inspectRequest = [pscustomobject][ordered]@{
        schema_version = 1
        kind = 'termlink.windows-direct-diagnostics-request'
        request_id = [Guid]::NewGuid().ToString('N')
        run_id = 'contract-test'
        scenario_id = 'selector_contract'
        operation = 'inspect'
        session_id = $selectorSessionId
        session_role = 'primary'
        peer_device_id = $selectorPeerDeviceId
    }
    $inspectProperties = $baseDiagnosticsProperties + @('session_id', 'session_role', 'peer_device_id')
    $inspectResult = Invoke-DiagnosticsContractRoundTrip $inspectRequest $inspectProperties `
        'observed' $true
    Assert-Contract ([string]$inspectResult.request.session_id -ceq $selectorSessionId -and
        [string]$inspectResult.request.session_role -ceq 'primary' -and
        [string]$inspectResult.request.peer_device_id -ceq $selectorPeerDeviceId -and
        [bool]$inspectResult.response.ok -and [string]$inspectResult.response.status -ceq 'observed') `
        'inspect selector was not forwarded exactly to the named-pipe backend'

    $unknownSessionId = [Guid]::NewGuid().ToString('D')
    $unknownSessionRequest = [pscustomobject][ordered]@{
        schema_version = 1
        kind = 'termlink.windows-direct-diagnostics-request'
        request_id = [Guid]::NewGuid().ToString('N')
        run_id = 'contract-test'
        scenario_id = 'selector_contract'
        operation = 'inspect'
        session_id = $unknownSessionId
        session_role = 'late'
        peer_device_id = $selectorPeerDeviceId
    }
    $unknownSessionResult = Invoke-DiagnosticsContractRoundTrip $unknownSessionRequest $inspectProperties `
        'blocked' $false 'WINDOWS_DIAGNOSTICS_SESSION_NOT_FOUND'
    Assert-Contract (-not [bool]$unknownSessionResult.response.ok -and
        [string]$unknownSessionResult.response.status -ceq 'blocked' -and
        [string]$unknownSessionResult.response.reason_code -ceq 'WINDOWS_DIAGNOSTICS_SESSION_NOT_FOUND') `
        'unknown well-formed session UUID did not fail closed at the runtime boundary'

    $invalidBase = [ordered]@{
        schema_version = 1
        kind = 'termlink.windows-direct-diagnostics-request'
        request_id = [Guid]::NewGuid().ToString('N')
        run_id = 'contract-test'
        scenario_id = 'selector_contract'
    }
    Assert-InvalidDiagnosticsRequest ([pscustomobject]($invalidBase + [ordered]@{
        operation = 'inspect'
    })) 'inspect_missing_selector'
    $invalidBase.request_id = [Guid]::NewGuid().ToString('N')
    Assert-InvalidDiagnosticsRequest ([pscustomobject]($invalidBase + [ordered]@{
        operation = 'inspect'
        session_id = [Guid]::NewGuid().ToString('D'); session_role = 'primary'
    })) 'inspect_missing_peer_device_id'
    $invalidBase.request_id = [Guid]::NewGuid().ToString('N')
    Assert-InvalidDiagnosticsRequest ([pscustomobject]($invalidBase + [ordered]@{
        operation = 'inspect'
        session_id = 'not-a-uuid'; session_role = 'primary'; peer_device_id = $selectorPeerDeviceId
    })) 'inspect_malformed_uuid'
    $invalidBase.request_id = [Guid]::NewGuid().ToString('N')
    Assert-InvalidDiagnosticsRequest ([pscustomobject]($invalidBase + [ordered]@{
        operation = 'inspect'
        session_id = [Guid]::Empty.ToString('D'); session_role = 'primary'; peer_device_id = $selectorPeerDeviceId
    })) 'inspect_empty_uuid'
    $invalidBase.request_id = [Guid]::NewGuid().ToString('N')
    Assert-InvalidDiagnosticsRequest ([pscustomobject]($invalidBase + [ordered]@{
        operation = 'inspect'
        session_id = [Guid]::NewGuid().ToString('D'); session_role = 'secondary'; peer_device_id = $selectorPeerDeviceId
    })) 'inspect_invalid_role'
    $invalidBase.request_id = [Guid]::NewGuid().ToString('N')
    Assert-InvalidDiagnosticsRequest ([pscustomobject]($invalidBase + [ordered]@{
        operation = 'inspect'
        session_id = [Guid]::NewGuid().ToString('D'); session_role = 'primary'
        peer_device_id = 'not-a-uuid'
    })) 'inspect_malformed_peer_device_id'
    $invalidBase.request_id = [Guid]::NewGuid().ToString('N')
    Assert-InvalidDiagnosticsRequest ([pscustomobject]($invalidBase + [ordered]@{
        operation = 'probe'
        session_id = [Guid]::NewGuid().ToString('D'); session_role = 'primary'
        peer_device_id = [Guid]::NewGuid().ToString('D')
    })) 'probe_with_selector'

    $pipeName = 'termlink-direct-fault-contract-' + [Guid]::NewGuid().ToString('N')
    $requestPath = Join-Path $temporaryRoot 'restore-request.json'
    $responsePath = Join-Path $temporaryRoot 'restore-response.json'
    $requestId = [Guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText(
        $requestPath,
        ([pscustomobject][ordered]@{
            schema_version = 1; kind = 'termlink.direct-fault-adapter-request'; request_id = $requestId
            run_id = 'contract-test'; scenario_id = 'restore_contract'; operation = 'restore_all'
        } | ConvertTo-Json -Compress),
        [Text.UTF8Encoding]::new($false)
    )
    $backend = Start-Job -ArgumentList $pipeName, $requestId -ScriptBlock {
        param($PipeName, $RequestId)
        $pipe = [IO.Pipes.NamedPipeServerStream]::new(
            $PipeName, [IO.Pipes.PipeDirection]::InOut, 1,
            [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::None
        )
        try {
            $pipe.WaitForConnection()
            $lengthBytes = [byte[]]::new(4)
            if ($pipe.Read($lengthBytes, 0, 4) -ne 4) { throw 'request length missing' }
            $length = [BitConverter]::ToInt32($lengthBytes, 0)
            $payload = [byte[]]::new($length)
            $offset = 0
            while ($offset -lt $length) {
                $read = $pipe.Read($payload, $offset, $length - $offset)
                if ($read -le 0) { throw 'request payload missing' }
                $offset += $read
            }
            $request = [Text.Encoding]::UTF8.GetString($payload) | ConvertFrom-Json
            if ([string]$request.operation -cne 'restore_all') { throw 'restore_all not received' }
            $response = [Text.Encoding]::UTF8.GetBytes((
                [pscustomobject][ordered]@{
                    schema_version = 1; request_id = $RequestId; ok = $true; status = 'restored'
                    reason_code = $null; process_scoped = $true; cleanup_guaranteed = $true
                    lease_id_sha256 = ('1' * 64); observed_channels = @()
                } | ConvertTo-Json -Compress
            ))
            $responseLength = [BitConverter]::GetBytes([int]$response.Length)
            $pipe.Write($responseLength, 0, 4)
            $pipe.Write($response, 0, $response.Length)
            $pipe.Flush()
        }
        finally { $pipe.Dispose() }
    }
    try {
        & (Join-Path $root 'Invoke-DirectProcessFaultAdapter.ps1') `
            -RequestPath $requestPath -ResponsePath $responsePath -ControlPipeName $pipeName
        $restored = Get-Content -LiteralPath $responsePath -Raw -Encoding UTF8 | ConvertFrom-Json
        Assert-Contract ([bool]$restored.ok -and [string]$restored.status -ceq 'restored') `
            ("fault driver did not preserve the restore_all result: " + ($restored | ConvertTo-Json -Compress))
        Wait-Job $backend -Timeout 10 | Out-Null
        Receive-Job $backend -ErrorAction Stop | Out-Null
    }
    finally { Remove-Job $backend -Force -ErrorAction SilentlyContinue }

    $missingPipeResponse = Join-Path $temporaryRoot 'missing-pipe-response.json'
    & (Join-Path $root 'Invoke-DirectProcessFaultAdapter.ps1') `
        -RequestPath $requestPath -ResponsePath $missingPipeResponse `
        -ControlPipeName ('missing-' + [Guid]::NewGuid().ToString('N')) -TimeoutMilliseconds 1000
    Assert-Contract ($LASTEXITCODE -eq 2) 'unavailable fault backend did not fail closed'
    $global:LASTEXITCODE = 0
    $blocked = Get-Content -LiteralPath $missingPipeResponse -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-Contract (-not [bool]$blocked.ok -and [string]$blocked.status -ceq 'blocked') `
        'unavailable fault backend was not marked blocked'

    [pscustomobject][ordered]@{
        outcome = 'passed'
        schema_status_separation = $true
        windows_session_selector_contract = $true
        diagnostics_protocol_little_endian_bounded_json = $true
        session_uuid_excluded_from_evidence = $true
        blocked_executed_scenario_supported = $true
        product_assertions_evaluated_locally = $true
        unprovable_assertions_block = $true
        restore_all_protocol = $true
        unavailable_backend_fails_closed = $true
        scenario_count = @($matrix.scenarios).Count
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot -PathType Container) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
