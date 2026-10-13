[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Join-TestByteArrays {
    param([Parameter(Mandatory)][object[]]$Arrays)

    $bytes = [Collections.Generic.List[byte]]::new()
    foreach ($array in $Arrays) { $bytes.AddRange([byte[]]$array) }
    return ,$bytes.ToArray()
}

function ConvertTo-TestProtobufVarint {
    param([Parameter(Mandatory)][uint64]$Value)

    $bytes = [Collections.Generic.List[byte]]::new()
    do {
        [byte]$current = $Value -band 0x7F
        $Value = $Value -shr 7
        if ($Value -ne 0) { $current = $current -bor 0x80 }
        $bytes.Add($current)
    } while ($Value -ne 0)
    return ,$bytes.ToArray()
}

function New-TestProtobufVarintField {
    param([Parameter(Mandatory)][int]$Number, [Parameter(Mandatory)][uint64]$Value)

    return ,(Join-TestByteArrays @(
        (ConvertTo-TestProtobufVarint ([uint64]($Number -shl 3))),
        (ConvertTo-TestProtobufVarint $Value)
    ))
}

function New-TestProtobufBytesField {
    param([Parameter(Mandatory)][int]$Number, [Parameter(Mandatory)][byte[]]$Bytes)

    return ,(Join-TestByteArrays @(
        (ConvertTo-TestProtobufVarint ([uint64](($Number -shl 3) -bor 2))),
        (ConvertTo-TestProtobufVarint ([uint64]$Bytes.Length)),
        $Bytes
    ))
}

function New-TestProtobufStringField {
    param([Parameter(Mandatory)][int]$Number, [Parameter(Mandatory)][string]$Value)

    return ,(New-TestProtobufBytesField -Number $Number -Bytes ([Text.Encoding]::UTF8.GetBytes($Value)))
}

function New-TestAabAttribute {
    param(
        [string]$Namespace,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Value,
        [switch]$VersionCode
    )

    $parts = [Collections.Generic.List[object]]::new()
    if (-not [string]::IsNullOrEmpty($Namespace)) {
        $parts.Add((New-TestProtobufStringField -Number 1 -Value $Namespace))
    }
    $parts.Add((New-TestProtobufStringField -Number 2 -Value $Name))
    $parts.Add((New-TestProtobufStringField -Number 3 -Value $Value))
    if ($VersionCode) {
        $parts.Add((New-TestProtobufVarintField -Number 5 -Value 0x0101021B))
        $primitive = New-TestProtobufVarintField -Number 6 -Value ([uint64]::Parse($Value))
        $item = New-TestProtobufBytesField -Number 7 -Bytes $primitive
        $parts.Add((New-TestProtobufBytesField -Number 6 -Bytes $item))
    }
    return ,(Join-TestByteArrays $parts.ToArray())
}

function New-TestAabManifest {
    param(
        [Parameter(Mandatory)][string]$ApplicationId,
        [Parameter(Mandatory)][string]$VersionName,
        [Parameter(Mandatory)][int]$VersionCode
    )

    $androidNamespace = 'http://schemas.android.com/apk/res/android'
    $elementParts = [Collections.Generic.List[object]]::new()
    $elementParts.Add((New-TestProtobufStringField -Number 3 -Value 'manifest'))
    foreach ($attribute in @(
        (New-TestAabAttribute -Name 'package' -Value $ApplicationId),
        (New-TestAabAttribute -Namespace $androidNamespace -Name 'versionCode' `
            -Value ([string]$VersionCode) -VersionCode),
        (New-TestAabAttribute -Namespace $androidNamespace -Name 'versionName' -Value $VersionName)
    )) {
        $elementParts.Add((New-TestProtobufBytesField -Number 4 -Bytes $attribute))
    }
    $element = Join-TestByteArrays $elementParts.ToArray()
    return ,(New-TestProtobufBytesField -Number 1 -Bytes $element)
}

function Add-TestZipBytes {
    param(
        [Parameter(Mandatory)][IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][byte[]]$Bytes
    )

    $entry = $Archive.CreateEntry($Name, [IO.Compression.CompressionLevel]::Optimal)
    $stream = $entry.Open()
    try { $stream.Write($Bytes, 0, $Bytes.Length) }
    finally { $stream.Dispose() }
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$scriptPath = Join-Path $repositoryRoot 'scripts\Build-Release.ps1'
$schemaPath = Join-Path $PSScriptRoot 'artifact-manifest.schema.json'
$verificationScriptPath = Join-Path $PSScriptRoot 'Verify-LocalRelease.ps1'
$verificationModulePath = Join-Path $PSScriptRoot 'ReleaseVerification.psm1'
$appShipWrapperPath = Join-Path $repositoryRoot 'scripts\Publish-AppShipProduction.ps1'
$setupBuilderPath = Join-Path $repositoryRoot 'win-agent\package\Build-Setup.ps1'
$msixBuilderPath = Join-Path $repositoryRoot 'win-agent\package\Build-Msix.ps1'
$installerValidatorPath = Join-Path $repositoryRoot 'win-agent\tests\Validate-WindowsInstaller.ps1'
$directNativeBuilderPath = Join-Path $repositoryRoot 'win-agent\scripts\Build-DirectCoreNative.ps1'
$agentProjectPath = Join-Path $repositoryRoot 'win-agent\src\Agent\TermLink.Agent.csproj'

$tokens = $null
$parseErrors = $null
$null = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
Assert-True ($parseErrors.Count -eq 0) `
    ("Build-Release.ps1 parser errors:`n" + (($parseErrors | ForEach-Object { $_.Message }) -join "`n"))
$null = Get-Content -LiteralPath $schemaPath -Raw | ConvertFrom-Json
$verificationTokens = $null
$verificationParseErrors = $null
$verificationAst = [Management.Automation.Language.Parser]::ParseFile(
    $verificationScriptPath,
    [ref]$verificationTokens,
    [ref]$verificationParseErrors)
Assert-True ($verificationParseErrors.Count -eq 0) `
    ("Verify-LocalRelease.ps1 parser errors:`n" + (($verificationParseErrors | ForEach-Object { $_.Message }) -join "`n"))
$moduleTokens = $null
$moduleParseErrors = $null
$null = [Management.Automation.Language.Parser]::ParseFile(
    $verificationModulePath,
    [ref]$moduleTokens,
    [ref]$moduleParseErrors)
Assert-True ($moduleParseErrors.Count -eq 0) `
    ("ReleaseVerification.psm1 parser errors:`n" + (($moduleParseErrors | ForEach-Object { $_.Message }) -join "`n"))
$wrapperTokens = $null
$wrapperParseErrors = $null
$null = [Management.Automation.Language.Parser]::ParseFile(
    $appShipWrapperPath,
    [ref]$wrapperTokens,
    [ref]$wrapperParseErrors)
Assert-True ($wrapperParseErrors.Count -eq 0) `
    ("Publish-AppShipProduction.ps1 parser errors:`n" + (($wrapperParseErrors | ForEach-Object { $_.Message }) -join "`n"))
Import-Module $verificationModulePath -Force
$releaseScriptSource = Get-Content -LiteralPath $scriptPath -Raw
$verificationScriptSource = Get-Content -LiteralPath $verificationScriptPath -Raw
$appShipWrapperSource = Get-Content -LiteralPath $appShipWrapperPath -Raw
$setupBuilderSource = Get-Content -LiteralPath $setupBuilderPath -Raw
$msixBuilderSource = Get-Content -LiteralPath $msixBuilderPath -Raw
$installerValidatorSource = Get-Content -LiteralPath $installerValidatorPath -Raw
$directNativeBuilderSource = Get-Content -LiteralPath $directNativeBuilderPath -Raw
$agentProjectSource = Get-Content -LiteralPath $agentProjectPath -Raw
& {
    $setupAst = [Management.Automation.Language.Parser]::ParseInput($setupBuilderSource, [ref]$null, [ref]$null)
    $invokeToolAst = $setupAst.Find({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-Tool'
    }, $false)
    . ([scriptblock]::Create($invokeToolAst.Extent.Text))
    $probe = @{ Calls = 0; Failures = 0; Message = ''; Sleeps = 0 }
    function signtool.exe {
        $probe.Calls++
        $global:LASTEXITCODE = if ($probe.Calls -le $probe.Failures) { 1 } else { 0 }
        if ($LASTEXITCODE -ne 0) { $probe.Message }
    }
    function Start-Sleep { param([int]$Seconds) $probe.Sleeps++ }
    $timestampError = 'SignTool Error: The specified timestamp server either could not be reached or returned an invalid response.'
    foreach ($case in @(
        @{ Failures = 2; Message = $timestampError; Args = @('sign', '/tr', 'http://timestamp.invalid'); Calls = 3; Throws = $false },
        @{ Failures = 10; Message = $timestampError; Args = @('sign', '/tr', 'http://timestamp.invalid'); Calls = 4; Throws = $true },
        @{ Failures = 10; Message = 'No certificates were found'; Args = @('sign', '/tr', 'http://timestamp.invalid'); Calls = 1; Throws = $true },
        @{ Failures = 10; Message = $timestampError; Args = @('verify', '/pa'); Calls = 1; Throws = $true },
        @{ Failures = 10; Message = $timestampError; Args = @('sign', '/tr', 'http://timestamp.invalid', '/as'); Calls = 1; Throws = $true }
    )) {
        $probe.Calls = 0; $probe.Sleeps = 0; $probe.Failures = $case.Failures; $probe.Message = $case.Message
        $threw = $false
        try { Invoke-Tool -FilePath signtool.exe -Arguments $case.Args -Quiet }
        catch { $threw = $true }
        Assert-True ($probe.Calls -eq $case.Calls -and $threw -eq $case.Throws -and $probe.Sleeps -eq ($case.Calls - 1)) `
            "Timestamp retry boundary failed: $($case.Args -join ' ') / $($case.Message)"
    }
    $global:LASTEXITCODE = 0
}
Assert-True ($releaseScriptSource.Contains('status --porcelain --untracked-files=normal')) `
    'Release source identity does not inspect the worktree status.'
Assert-True ($releaseScriptSource.Contains('"$sourceRevision-dirty"')) `
    'Release source identity does not distinguish dirty worktrees from clean HEAD.'
Assert-True ($releaseScriptSource.Contains('$finalArtifactRecords')) `
    'Release manifest does not perform a final post-build artifact digest pass.'
Assert-True ($releaseScriptSource.Contains('$pendingManifestPath')) `
    'Production release verification does not stage the manifest before promotion.'
Assert-True ($releaseScriptSource.Contains('ExpectedAndroidSignerSha256')) `
    'Production release orchestration has no Android signer continuity input.'
Assert-True ($verificationScriptSource.Contains('Get-TermLinkAabVerificationEvidence')) `
    'Production release verification has no complete AAB identity/signature inspection.'
Assert-True ($releaseScriptSource.Contains('ExpectedWindowsSignerSha1')) `
    'Production release orchestration has no Windows signer continuity input.'
Assert-True ($releaseScriptSource.Contains('[string]$CargoPath')) `
    'Production release orchestration has no explicit CargoPath input.'
Assert-True ($releaseScriptSource.Contains('Rust 1.97.1 with x86_64-pc-windows-msvc target')) `
    'Windows release plans do not record the pinned Rust toolchain requirement.'
Assert-True ($releaseScriptSource.Contains('AllowHttpWindowsTimestamp')) `
    'Production release orchestration has no explicit HTTP RFC 3161 opt-in.'
Assert-True ($setupBuilderSource.Contains('AllowHttpTimestamp')) `
    'Windows Setup builder has no explicit HTTP RFC 3161 opt-in.'
Assert-True ($msixBuilderSource.Contains('AllowHttpTimestamp')) `
    'Windows MSIX builder has no explicit HTTP RFC 3161 opt-in.'
foreach ($marker in @(
    'Export-TermLinkSetupPayloadArchive',
    'Resolve-SafeArchiveTarget',
    'TermLink.Setup.Payload.zip',
    'TermLink.TerminalCore.dll',
    'termlink_terminal_ffi.dll',
    'termlink_direct_ffi.dll',
    '[IO.FileMode]::CreateNew',
    '[IO.FileAttributes]::ReparsePoint',
    '$ownedPayloadBinaries',
    'Assert-TermLinkWindowsSignatureEvidence')) {
    Assert-True ($verificationScriptSource.Contains($marker)) `
        "Production Setup payload verification is missing required guard: $marker"
}
Assert-True ($verificationScriptSource.Contains("`$_.Name -match '^TermLink(?:\..+)?\.(exe|dll)`$'")) `
    'Production Setup verifier does not cover every TermLink-owned payload binary.'
foreach ($builder in @(
    [pscustomobject]@{ Name = 'Setup'; Source = $setupBuilderSource },
    [pscustomobject]@{ Name = 'MSIX'; Source = $msixBuilderSource })) {
    Assert-True ($builder.Source.Contains('Build-DirectCoreNative.ps1')) `
        "Windows $($builder.Name) builder does not freshly build network-core."
    Assert-True ($builder.Source.Contains('termlink_direct_ffi.dll')) `
        "Windows $($builder.Name) builder does not require the Direct native DLL."
    Assert-True ($builder.Source.Contains('differs from the native artifact built for this release')) `
        "Windows $($builder.Name) builder does not compare the packaged Direct DLL with this build."
}
foreach ($marker in @(
    "`$requiredToolchain = '1.97.1'",
    "`$targetTriple = 'x86_64-pc-windows-msvc'",
    '-p termlink-direct-ffi',
    '--release',
    '--locked',
    '--offline',
    '--target $targetTriple',
    'network-core\target\$targetTriple\release\termlink_direct_ffi.dll')) {
    Assert-True ($directNativeBuilderSource.Contains($marker)) `
        "Direct native build script is missing its pinned build contract: $marker"
}
Assert-True ($agentProjectSource.Contains('network-core\target\x86_64-pc-windows-msvc\release\termlink_direct_ffi.dll')) `
    'Agent project does not package the explicit Windows target Direct DLL.'
Assert-True (-not $agentProjectSource.Contains('network-core\target\release\termlink_direct_ffi.dll')) `
    'Agent project still permits the ambiguous non-target Direct DLL fallback.'
Assert-True ($agentProjectSource.Contains('ValidateTermLinkDirectNativeLibrary')) `
    'Agent Release builds do not fail closed when the Direct DLL is missing.'
Assert-True ($installerValidatorSource.Contains('$acceptedSignatureStatuses -contains $signature.Status')) `
    'MSIX development validation does not enforce an explicit signature-status allowlist.'
foreach ($status in @('Valid', 'UnknownError', 'NotTrusted')) {
    Assert-True ($installerValidatorSource.Contains("[Management.Automation.SignatureStatus]::$status")) `
        "MSIX development validation does not allow the intended signature status: $status"
}

$payloadHelperNames = @(
    'Assert-Release',
    'Get-Sha256Hex',
    'Invoke-NativeCapture',
    'Export-TermLinkServerBinary',
    'Resolve-SafeArchiveTarget',
    'Assert-NoReparsePoint',
    'Expand-SafePayloadArchive'
)
$payloadHelperSource = @($verificationAst.FindAll({
    $args[0] -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $payloadHelperNames -contains $args[0].Name
}, $true) | ForEach-Object { $_.Extent.Text }) -join [Environment]::NewLine
. ([scriptblock]::Create($payloadHelperSource))

$archiveSafetyRoot = Join-Path $repositoryRoot 'artifacts\release\payload-archive-safety-probe'
Assert-True (-not (Test-Path -LiteralPath $archiveSafetyRoot)) `
    "Payload archive safety probe path already exists: $archiveSafetyRoot"
[IO.Directory]::CreateDirectory($archiveSafetyRoot) | Out-Null
try {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $traversalArchivePath = Join-Path $archiveSafetyRoot 'traversal.zip'
    $traversalArchive = [IO.Compression.ZipFile]::Open(
        $traversalArchivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $traversalArchive.CreateEntry('../escaped.dll')
        $writer = [IO.StreamWriter]::new($entry.Open())
        try { $writer.Write('not allowed') } finally { $writer.Dispose() }
    }
    finally { $traversalArchive.Dispose() }
    $traversalRejected = $false
    try {
        Expand-SafePayloadArchive -ArchivePath $traversalArchivePath `
            -Destination (Join-Path $archiveSafetyRoot 'traversal-output')
    }
    catch { $traversalRejected = $_.Exception.Message.Contains('traversal segment') }
    Assert-True $traversalRejected 'Setup payload extraction accepted a parent-directory traversal entry.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $archiveSafetyRoot 'escaped.dll'))) `
        'Setup payload traversal probe wrote outside its extraction directory.'

    $linkArchivePath = Join-Path $archiveSafetyRoot 'symbolic-link.zip'
    $linkArchive = [IO.Compression.ZipFile]::Open($linkArchivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $linkArchive.CreateEntry('link.dll')
        $entry.ExternalAttributes = [BitConverter]::ToInt32(
            [BitConverter]::GetBytes([Convert]::ToUInt32('A1FF0000', 16)), 0)
        $writer = [IO.StreamWriter]::new($entry.Open())
        try { $writer.Write('target') } finally { $writer.Dispose() }
    }
    finally { $linkArchive.Dispose() }
    $linkRejected = $false
    try {
        Expand-SafePayloadArchive -ArchivePath $linkArchivePath `
            -Destination (Join-Path $archiveSafetyRoot 'link-output')
    }
    catch { $linkRejected = $_.Exception.Message.Contains('symbolic link or reparse point') }
    Assert-True $linkRejected 'Setup payload extraction accepted a symbolic-link ZIP entry.'

    $validArchivePath = Join-Path $archiveSafetyRoot 'valid.zip'
    $validArchive = [IO.Compression.ZipFile]::Open($validArchivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $validArchive.CreateEntry('TermLink.TerminalCore.dll')
        $writer = [IO.StreamWriter]::new($entry.Open())
        try { $writer.Write('managed terminal core') } finally { $writer.Dispose() }
    }
    finally { $validArchive.Dispose() }
    $validOutput = Join-Path $archiveSafetyRoot 'valid-output'
    Expand-SafePayloadArchive -ArchivePath $validArchivePath -Destination $validOutput
    Assert-True ((Get-Content -LiteralPath (Join-Path $validOutput 'TermLink.TerminalCore.dll') -Raw) -ceq
        'managed terminal core') 'Setup payload extraction rejected or changed a safe regular file.'

    $blobRoot = Join-Path $archiveSafetyRoot 'blobs'
    [IO.Directory]::CreateDirectory($blobRoot) | Out-Null
    $layers = @{}
    foreach ($fixture in @(
        @{ name = 'original'; file = 'termlink-server'; content = 'old server' },
        @{ name = 'notices'; file = 'THIRD_PARTY_NOTICES.md'; content = 'notices' },
        @{ name = 'replacement'; file = 'termlink-server'; content = 'new server' },
        @{ name = 'deleted'; file = '.wh.termlink-server'; content = '' },
        @{ name = 'opaque'; file = '.wh..wh..opq'; content = '' }
    )) {
        $layerInput = Join-Path $archiveSafetyRoot $fixture.name
        [IO.Directory]::CreateDirectory($layerInput) | Out-Null
        [IO.File]::WriteAllText((Join-Path $layerInput $fixture.file), $fixture.content)
        $layerArchive = Join-Path $archiveSafetyRoot "$($fixture.name).tar.gz"
        $null = Invoke-NativeCapture -FilePath 'tar' -Arguments @('-czf', $layerArchive, '-C', $layerInput, '.') `
            -Description 'OCI layer fixture creation'
        $hash = Get-Sha256Hex $layerArchive
        Copy-Item -LiteralPath $layerArchive -Destination (Join-Path $blobRoot $hash)
        $layers[$fixture.name] = [pscustomobject]@{ digest = "sha256:$hash" }
    }
    $extracted = Export-TermLinkServerBinary -Layers @($layers.original, $layers.notices) `
        -BlobRoot $blobRoot -Destination (Join-Path $archiveSafetyRoot 'oci-notices-output')
    Assert-True ((Get-Content -LiteralPath $extracted -Raw) -ceq 'old server') `
        'OCI verification lost the server when notices were added in a later layer.'
    $extracted = Export-TermLinkServerBinary -Layers @($layers.original, $layers.replacement, $layers.notices) `
        -BlobRoot $blobRoot -Destination (Join-Path $archiveSafetyRoot 'oci-replacement-output')
    Assert-True ((Get-Content -LiteralPath $extracted -Raw) -ceq 'new server') `
        'OCI verification selected an outdated server layer.'
    foreach ($rejectedLayer in @('deleted', 'opaque', 'notices')) {
        $candidates = if ($rejectedLayer -eq 'notices') { @($layers.notices) }
            else { @($layers.original, $layers[$rejectedLayer]) }
        $rejected = $false
        try {
            $null = Export-TermLinkServerBinary -Layers $candidates -BlobRoot $blobRoot `
                -Destination (Join-Path $archiveSafetyRoot "oci-rejected-$rejectedLayer")
        }
        catch { $rejected = $_.Exception.Message -match 'Server binary (removed|missing)' }
        Assert-True $rejected "OCI verification accepted a missing/deleted server: $rejectedLayer"
    }
}
finally {
    $releaseArtifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\release'))
    $resolvedArchiveSafetyRoot = [IO.Path]::GetFullPath($archiveSafetyRoot)
    $archiveSafetyPrefix = $releaseArtifactsRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($resolvedArchiveSafetyRoot.StartsWith($archiveSafetyPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedArchiveSafetyRoot) -ceq 'payload-archive-safety-probe') {
        Remove-Item -LiteralPath $resolvedArchiveSafetyRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
Assert-True ($releaseScriptSource.Contains("[string]::IsNullOrEmpty(`$timestampUri.UserInfo)")) `
    'Production release orchestration does not reject timestamp URLs containing userinfo.'
Assert-True ($releaseScriptSource.Contains("'ANDROID_SDK_ROOT', 'ANDROID_HOME'")) `
    'AndroidSdkPath is not propagated to the Gradle process environment.'
Assert-True ($appShipWrapperSource.Contains('& $verificationScript @verificationParameters')) `
    'AppShip production wrapper does not rerun the standalone Production verifier.'
Assert-True ($appShipWrapperSource.Contains('$finalManifestSha256 -ceq $manifestSha256')) `
    'AppShip production wrapper does not reject a manifest changed during verification.'
Assert-True ($appShipWrapperSource.Contains('$verificationParameters.RequireWindowsTimestamp = $true')) `
    'AppShip production wrapper does not require Windows timestamp verification.'
Assert-True ($appShipWrapperSource.Contains("SignatureDecision = 'approved'")) `
    'AppShip production wrapper does not bind the reviewed artifact to signature approval.'
Assert-True ($appShipWrapperSource.Contains('[IO.FileShare]::None')) `
    'AppShip production wrapper does not serialize publishing to one application/channel/version state.'

$androidSignerSha256 = 'f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b'
$androidSignatureFixture = @(
    'Verifies',
    'Verified using v1 scheme (JAR signing): false',
    'Verified using v2 scheme (APK Signature Scheme v2): true',
    "Signer #1 certificate SHA-256 digest: $androidSignerSha256",
    'Number of signers: 1'
)
$androidEvidence = Assert-TermLinkAndroidSignerOutput -Output $androidSignatureFixture `
    -ExpectedSha256 $androidSignerSha256
Assert-True ($androidEvidence.signer_sha256 -ceq $androidSignerSha256) `
    'The correct Android continuity fingerprint was not accepted.'
Assert-True (@($androidEvidence.verified_schemes) -contains 2) `
    'The Android verifier did not retain APK Signature Scheme v2 evidence.'
$wrongAndroidRejected = $false
try {
    $null = Assert-TermLinkAndroidSignerOutput -Output $androidSignatureFixture -ExpectedSha256 ('0' * 64)
}
catch {
    $wrongAndroidRejected = $_.Exception.Message.Contains('continuity fingerprint')
}
Assert-True $wrongAndroidRejected 'An incorrect Android signer continuity fingerprint was accepted.'
$v1OnlyRejected = $false
try {
    $null = Assert-TermLinkAndroidSignerOutput -Output @(
        'Verified using v1 scheme (JAR signing): true',
        "Signer #1 certificate SHA-256 digest: $androidSignerSha256"
    ) -ExpectedSha256 $androidSignerSha256
}
catch {
    $v1OnlyRejected = $_.Exception.Message.Contains('v2 or newer')
}
Assert-True $v1OnlyRejected 'An Android APK with only v1 signature evidence was accepted.'
$duplicateSignerRejected = $false
try {
    $null = Assert-TermLinkAndroidSignerOutput -Output @(
        'Verified using v2 scheme (APK Signature Scheme v2): true',
        "Signer #1 certificate SHA-256 digest: $androidSignerSha256",
        "Signer #2 certificate SHA-256 digest: $androidSignerSha256",
        'Number of signers: 2'
    ) -ExpectedSha256 $androidSignerSha256
}
catch {
    $duplicateSignerRejected = $_.Exception.Message.Contains('exactly one signer')
}
Assert-True $duplicateSignerRejected `
    'An APK with two signers using the same certificate fingerprint was accepted.'
$missingSignerCountRejected = $false
try {
    $null = Assert-TermLinkAndroidSignerOutput -Output @(
        'Verified using v2 scheme (APK Signature Scheme v2): true',
        "Signer #1 certificate SHA-256 digest: $androidSignerSha256"
    ) -ExpectedSha256 $androidSignerSha256
}
catch {
    $missingSignerCountRejected = $_.Exception.Message.Contains('missing or ambiguous')
}
Assert-True $missingSignerCountRejected 'APK signer count evidence was optional instead of fail-closed.'

$jdkCandidates = @(
    if (-not [string]::IsNullOrWhiteSpace($env:JAVA_HOME)) { Join-Path $env:JAVA_HOME 'bin' },
    'D:\Dev\Env\Jdk\jdk-21\bin',
    'D:\Dev\Env\Jdk\jdk-17.0.17\bin'
)
$jdkBin = @($jdkCandidates | Where-Object {
    (Test-Path -LiteralPath (Join-Path $_ 'keytool.exe') -PathType Leaf) -and
    (Test-Path -LiteralPath (Join-Path $_ 'jarsigner.exe') -PathType Leaf)
} | Select-Object -First 1)
Assert-True ($jdkBin.Count -eq 1) 'A manifest-approved JDK with keytool and jarsigner is required for AAB tests.'
$keytoolPath = Join-Path $jdkBin[0] 'keytool.exe'
$jarsignerPath = Join-Path $jdkBin[0] 'jarsigner.exe'
$aabFixtureName = 'aab-verification-probe-' + [guid]::NewGuid().ToString('N')
$aabFixtureRoot = Join-Path $repositoryRoot "artifacts\release\$aabFixtureName"
[IO.Directory]::CreateDirectory($aabFixtureRoot) | Out-Null
try {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $fixtureKeystorePath = Join-Path $aabFixtureRoot 'fixture.p12'
    $fixturePassword = 'TermLink-Aab-Fixture-Only-42'
    $null = & $keytoolPath -genkeypair -noprompt -storetype PKCS12 `
        -keystore $fixtureKeystorePath -storepass $fixturePassword -keypass $fixturePassword `
        -alias fixture -dname 'CN=TermLink AAB Fixture' -keyalg RSA -keysize 2048 `
        -sigalg SHA256withRSA -validity 3650 2>&1
    Assert-True ($LASTEXITCODE -eq 0) 'keytool failed to generate the first AAB fixture signer.'

    $fixtureAabPath = Join-Path $aabFixtureRoot 'TermLink-Android-0.2.10-52.aab'
    $fixtureArchive = [IO.Compression.ZipFile]::Open(
        $fixtureAabPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        Add-TestZipBytes -Archive $fixtureArchive -Name 'base/manifest/AndroidManifest.xml' `
            -Bytes (New-TestAabManifest -ApplicationId 'dev.termlink.harness' `
                -VersionName '0.2.10' -VersionCode 52)
        Add-TestZipBytes -Archive $fixtureArchive -Name 'base/dex/classes.dex' `
            -Bytes ([Text.Encoding]::UTF8.GetBytes('signed payload fixture'))
    }
    finally { $fixtureArchive.Dispose() }
    $null = & $jarsignerPath '-J-Duser.language=en' '-J-Duser.country=US' `
        -keystore $fixtureKeystorePath -storetype PKCS12 -storepass $fixturePassword `
        -keypass $fixturePassword -digestalg SHA-256 -sigalg SHA256withRSA `
        $fixtureAabPath fixture 2>&1
    Assert-True ($LASTEXITCODE -eq 0) 'jarsigner failed to sign the AAB fixture.'

    $certificateOutput = @(& $keytoolPath '-J-Duser.language=en' '-J-Duser.country=US' `
        -exportcert -rfc -keystore $fixtureKeystorePath -storetype PKCS12 `
        -storepass $fixturePassword -alias fixture 2>&1)
    Assert-True ($LASTEXITCODE -eq 0) 'keytool failed to export the AAB fixture certificate.'
    $certificatePem = $certificateOutput -join "`n"
    $certificateMatch = [regex]::Match(
        $certificatePem,
        '(?s)-----BEGIN CERTIFICATE-----\s*(.*?)\s*-----END CERTIFICATE-----')
    Assert-True $certificateMatch.Success 'The exported AAB fixture certificate was malformed.'
    $certificateBytes = [Convert]::FromBase64String(($certificateMatch.Groups[1].Value -replace '\s', ''))
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificateBytes)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try { $fixtureSignerSha256 = ([BitConverter]::ToString(
            $sha256.ComputeHash($certificate.RawData))).Replace('-', '').ToLowerInvariant() }
    finally { $sha256.Dispose() }

    $aabEvidence = Get-TermLinkAabVerificationEvidence -Path $fixtureAabPath `
        -ExpectedSha256 $fixtureSignerSha256
    Assert-True ($aabEvidence.application_id -ceq 'dev.termlink.harness' -and
        $aabEvidence.version_name -ceq '0.2.10' -and $aabEvidence.version_code -eq 52 -and
        $aabEvidence.signer_count -eq 1 -and $aabEvidence.payload_sha256_coverage -eq $true -and
        $aabEvidence.jar_signature_valid -eq $true) `
        'A correctly signed AAB did not retain complete identity, version, signer, and payload evidence.'

    $fixtureFile = Get-Item -LiteralPath $fixtureAabPath
    $fixtureHash = (Get-FileHash -LiteralPath $fixtureAabPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $fixtureManifest = [pscustomobject][ordered]@{
        schema_version = 1
        version = '0.2.10'
        build_number = 52
        release_mode = 'production'
        artifacts = @([pscustomobject][ordered]@{
            path = "artifacts/release/$aabFixtureName/$($fixtureFile.Name)"
            size = [int64]$fixtureFile.Length
            sha256 = $fixtureHash
            platform = 'android'
            type = 'aab'
            version = '0.2.10'
        })
    }
    $fixtureManifestPath = Join-Path $aabFixtureRoot 'artifact-manifest.json'
    [IO.File]::WriteAllText(
        $fixtureManifestPath,
        (($fixtureManifest | ConvertTo-Json -Depth 6) + [Environment]::NewLine),
        (New-Object Text.UTF8Encoding -ArgumentList $false))
    $fixtureVerification = @(& $verificationScriptPath -ReleaseDirectory $aabFixtureRoot `
        -ExpectedReleaseMode Production -ExpectedAndroidSignerSha256 $fixtureSignerSha256) `
        -join "`n" | ConvertFrom-Json
    Assert-True ($fixtureVerification.verified -eq $true -and
        $fixtureVerification.android_aab.application_id -ceq 'dev.termlink.harness' -and
        $fixtureVerification.android_aab.version_code -eq 52) `
        'The standalone Production verifier rejected a complete, correctly signed AAB.'

    $missingSourceRejected = $false
    try {
        $null = & $verificationScriptPath -ReleaseDirectory $aabFixtureRoot `
            -ExpectedReleaseMode Production -ExpectedAndroidSignerSha256 $fixtureSignerSha256 `
            -RequireSourceIdentity
    }
    catch { $missingSourceRejected = $_.Exception.Message.Contains('missing source_revision') }
    Assert-True $missingSourceRejected 'A new Production client manifest omitted source_revision.'
    $fixtureSourceRevision = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    $fixtureCoreLock = ((& git -C $repositoryRoot show "${fixtureSourceRevision}:terminal-core.lock.json") -join "`n") |
        ConvertFrom-Json
    $fixtureManifest | Add-Member -NotePropertyName source_revision -NotePropertyValue $fixtureSourceRevision
    $fixtureManifest | Add-Member -NotePropertyName core_revision -NotePropertyValue $fixtureCoreLock.revision
    [IO.File]::WriteAllText($fixtureManifestPath, (($fixtureManifest | ConvertTo-Json -Depth 6) + "`n"),
        (New-Object Text.UTF8Encoding -ArgumentList $false))
    $boundVerification = @(& $verificationScriptPath -ReleaseDirectory $aabFixtureRoot `
        -ExpectedReleaseMode Production -ExpectedAndroidSignerSha256 $fixtureSignerSha256 `
        -RequireSourceIdentity) -join "`n" | ConvertFrom-Json
    Assert-True ($boundVerification.source_revision -ceq $fixtureSourceRevision -and
        $boundVerification.core_revision -ceq $fixtureCoreLock.revision) `
        'The Production verifier did not bind the client source and native lock revisions.'
    $fixtureManifest.core_revision = '0' * 40
    [IO.File]::WriteAllText($fixtureManifestPath, (($fixtureManifest | ConvertTo-Json -Depth 6) + "`n"),
        (New-Object Text.UTF8Encoding -ArgumentList $false))
    $wrongCoreRejected = $false
    try {
        $null = & $verificationScriptPath -ReleaseDirectory $aabFixtureRoot `
            -ExpectedReleaseMode Production -ExpectedAndroidSignerSha256 $fixtureSignerSha256 `
            -RequireSourceIdentity
    }
    catch { $wrongCoreRejected = $_.Exception.Message.Contains('does not match the source native lock') }
    Assert-True $wrongCoreRejected 'A client manifest with the wrong core revision was accepted.'

    $tamperedAabPath = Join-Path $aabFixtureRoot 'tampered.aab'
    Copy-Item -LiteralPath $fixtureAabPath -Destination $tamperedAabPath
    $tamperedArchive = [IO.Compression.ZipFile]::Open(
        $tamperedAabPath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $tamperedManifestEntry = $tamperedArchive.GetEntry('base/manifest/AndroidManifest.xml')
        $tamperedManifestEntry.Delete()
        Add-TestZipBytes -Archive $tamperedArchive -Name 'base/manifest/AndroidManifest.xml' `
            -Bytes (New-TestAabManifest -ApplicationId 'dev.termlink.harness' `
                -VersionName '0.2.10' -VersionCode 53)
    }
    finally { $tamperedArchive.Dispose() }
    $tamperedAabRejected = $false
    try {
        $null = Get-TermLinkAabVerificationEvidence -Path $tamperedAabPath `
            -ExpectedSha256 $fixtureSignerSha256
    }
    catch { $tamperedAabRejected = $_.Exception.Message.Contains('payload digest') }
    Assert-True $tamperedAabRejected 'An AAB changed after JAR signing was accepted.'

    $null = & $keytoolPath -genkeypair -noprompt -storetype PKCS12 `
        -keystore $fixtureKeystorePath -storepass $fixturePassword -keypass $fixturePassword `
        -alias fixture2 -dname 'CN=TermLink AAB Second Fixture' -keyalg RSA -keysize 2048 `
        -sigalg SHA256withRSA -validity 3650 2>&1
    Assert-True ($LASTEXITCODE -eq 0) 'keytool failed to generate the second AAB fixture signer.'
    $multiSignerAabPath = Join-Path $aabFixtureRoot 'multi-signer.aab'
    Copy-Item -LiteralPath $fixtureAabPath -Destination $multiSignerAabPath
    $null = & $jarsignerPath '-J-Duser.language=en' '-J-Duser.country=US' `
        -keystore $fixtureKeystorePath -storetype PKCS12 -storepass $fixturePassword `
        -keypass $fixturePassword -digestalg SHA-256 -sigalg SHA256withRSA `
        $multiSignerAabPath fixture2 2>&1
    Assert-True ($LASTEXITCODE -eq 0) 'jarsigner failed to apply the second AAB fixture signer.'
    $multiSignerAabRejected = $false
    try {
        $null = Get-TermLinkAabVerificationEvidence -Path $multiSignerAabPath `
            -ExpectedSha256 $fixtureSignerSha256
    }
    catch { $multiSignerAabRejected = $_.Exception.Message.Contains('exactly one supported JAR signature') }
    Assert-True $multiSignerAabRejected 'An AAB carrying multiple JAR signers was accepted.'
}
finally {
    $releaseArtifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\release'))
    $resolvedAabFixtureRoot = [IO.Path]::GetFullPath($aabFixtureRoot)
    $aabFixturePrefix = $releaseArtifactsRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($resolvedAabFixtureRoot.StartsWith($aabFixturePrefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedAabFixtureRoot).StartsWith(
            'aab-verification-probe-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedAabFixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$windowsSignerSha1 = '91ae60bc06d866628b8eb2c62f32bde9dab56466'
$acceptedWindowsSigner = Assert-TermLinkWindowsSignatureEvidence -Status 'Valid' `
    -ActualSha1 $windowsSignerSha1.ToUpperInvariant() -ExpectedSha1 $windowsSignerSha1
Assert-True ($acceptedWindowsSigner -ceq $windowsSignerSha1) `
    'The correct Windows signer continuity fingerprint was not accepted.'
$wrongWindowsRejected = $false
try {
    $null = Assert-TermLinkWindowsSignatureEvidence -Status 'Valid' `
        -ActualSha1 $windowsSignerSha1 -ExpectedSha1 ('0' * 40)
}
catch {
    $wrongWindowsRejected = $_.Exception.Message.Contains('continuity fingerprint')
}
Assert-True $wrongWindowsRejected 'An incorrect Windows signer continuity fingerprint was accepted.'
$invalidWindowsStatusRejected = $false
try {
    $null = Assert-TermLinkWindowsSignatureEvidence -Status 'NotTrusted' `
        -ActualSha1 $windowsSignerSha1 -ExpectedSha1 $windowsSignerSha1
}
catch {
    $invalidWindowsStatusRejected = $_.Exception.Message.Contains("expected 'Valid'")
}
Assert-True $invalidWindowsStatusRejected 'A non-valid Windows Authenticode status was accepted.'
$windowsVersionEvidence = Assert-TermLinkWindowsVersionEvidence `
    -ProductVersion '0.1.20.26+commit' -FileVersion '0.1.20.26' -ExpectedVersion '0.1.20.26'
Assert-True ($windowsVersionEvidence.file_version -ceq '0.1.20.26') `
    'Correct Windows ProductVersion/FileVersion evidence was not accepted.'
$wrongFileVersionRejected = $false
try {
    $null = Assert-TermLinkWindowsVersionEvidence `
        -ProductVersion '0.1.20.26+commit' -FileVersion '0.1.20.25' -ExpectedVersion '0.1.20.26'
}
catch { $wrongFileVersionRejected = $_.Exception.Message.Contains('FileVersion') }
Assert-True $wrongFileVersionRejected 'An incorrect Windows Setup FileVersion was accepted.'
$wrongProductVersionRejected = $false
try {
    $null = Assert-TermLinkWindowsVersionEvidence `
        -ProductVersion '0.1.20.25+commit' -FileVersion '0.1.20.26' -ExpectedVersion '0.1.20.26'
}
catch { $wrongProductVersionRejected = $_.Exception.Message.Contains('ProductVersion') }
Assert-True $wrongProductVersionRejected 'An incorrect Windows Setup ProductVersion was accepted.'
$extraNumericProductVersionRejected = $false
try {
    $null = Assert-TermLinkWindowsVersionEvidence `
        -ProductVersion '0.1.20.26.27' -FileVersion '0.1.20.26' -ExpectedVersion '0.1.20.26'
}
catch { $extraNumericProductVersionRejected = $_.Exception.Message.Contains('ProductVersion') }
Assert-True $extraNumericProductVersionRejected `
    'A Windows Setup ProductVersion with an extra numeric component was accepted.'
$malformedProductMetadataRejected = $false
try {
    $null = Assert-TermLinkWindowsVersionEvidence `
        -ProductVersion '0.1.20.26+' -FileVersion '0.1.20.26' -ExpectedVersion '0.1.20.26'
}
catch { $malformedProductMetadataRejected = $_.Exception.Message.Contains('ProductVersion') }
Assert-True $malformedProductMetadataRejected `
    'A Windows Setup ProductVersion with malformed build metadata was accepted.'

$rfc3161Oid = '1.3.6.1.4.1.311.3.3.1'
$legacyCounterSignatureOid = '1.2.840.113549.1.9.6'
$timestampEvidence = Assert-TermLinkRfc3161TimestampEvidence `
    -UnsignedAttributeOids @($rfc3161Oid) -HasTimeStamperCertificate $true
Assert-True ($timestampEvidence.timestamp_oid -ceq $rfc3161Oid) `
    'Valid RFC 3161 timestamp-token evidence was not accepted.'
$legacyTimestampRejected = $false
try {
    $null = Assert-TermLinkRfc3161TimestampEvidence `
        -UnsignedAttributeOids @($legacyCounterSignatureOid) -HasTimeStamperCertificate $true
}
catch { $legacyTimestampRejected = $_.Exception.Message.Contains('legacy Authenticode countersignature') }
Assert-True $legacyTimestampRejected 'A legacy Authenticode countersignature was accepted as RFC 3161.'
$missingTimestampOidRejected = $false
try {
    $null = Assert-TermLinkRfc3161TimestampEvidence `
        -UnsignedAttributeOids @() -HasTimeStamperCertificate $true
}
catch { $missingTimestampOidRejected = $_.Exception.Message.Contains('exactly one RFC 3161') }
Assert-True $missingTimestampOidRejected `
    'A timestamp certificate without an RFC 3161 timestamp-token OID was accepted.'
$unvalidatedTimestampRejected = $false
try {
    $null = Assert-TermLinkRfc3161TimestampEvidence `
        -UnsignedAttributeOids @($rfc3161Oid) -HasTimeStamperCertificate $false
}
catch { $unvalidatedTimestampRejected = $_.Exception.Message.Contains('did not validate') }
Assert-True $unvalidatedTimestampRejected `
    'An RFC 3161 attribute without a Windows-validated timestamper certificate was accepted.'

$probeOutput = Join-Path $repositoryRoot 'artifacts\release\plan-determinism-probe'
Assert-True (-not (Test-Path -LiteralPath $probeOutput)) `
    "The side-effect probe path must not exist before the test: $probeOutput"

$commonArguments = @{
    Version = '12.34.56'
    BuildNumber = 321
    ServerImage = $true
    WindowsSetup = $true
    WindowsMsix = $true
    AndroidApk = $true
    AndroidAab = $true
    ReleaseMode = 'Development'
    OutputDirectory = $probeOutput
    CargoPath = 'D:\CargoPlanProbe\cargo.exe'
}
$planOne = (@(& $scriptPath @commonArguments -Plan) -join "`n")
$planTwo = (@(& $scriptPath @commonArguments -Plan) -join "`n")
Assert-True ($planOne -ceq $planTwo) 'Repeated -Plan output is not deterministic.'

$dryRunOne = (@(& $scriptPath @commonArguments -DryRun) -join "`n")
$dryRunTwo = (@(& $scriptPath @commonArguments -DryRun) -join "`n")
Assert-True ($dryRunOne -ceq $dryRunTwo) 'Repeated -DryRun output is not deterministic.'
Assert-True (-not (Test-Path -LiteralPath $probeOutput)) '-Plan or -DryRun created an output path.'

$planObject = $planOne | ConvertFrom-Json
Assert-True ($planObject.version -ceq '12.34.56') 'The plan did not preserve the shared version.'
Assert-True ($planObject.build_number -eq 321) 'The plan did not preserve the shared build number.'
Assert-True ($planObject.windows_version -ceq '12.34.56.321') 'The Windows version mapping is incorrect.'
Assert-True ($planObject.release_mode -ceq 'development') 'The multi-target plan mode is incorrect.'
Assert-True ($planObject.publish -eq $false) 'The plan unexpectedly enables publishing.'
Assert-True (@($planObject.steps).Count -eq 4) 'The expected target steps were not planned.'

$serverStep = @($planObject.steps | Where-Object { $_.id -eq 'server-image' })[0]
$serverArguments = @($serverStep.arguments)
Assert-True ($serverArguments -contains '--output') 'The server plan does not export a local OCI artifact.'
Assert-True (-not ($serverArguments -contains '--push')) 'The server plan contains a push argument.'
Assert-True ($serverStep.publish -eq $false) 'The server step unexpectedly enables publishing.'

foreach ($windowsStep in @($planObject.steps | Where-Object { $_.id -in @('windows-setup', 'windows-msix') })) {
    $windowsArguments = @($windowsStep.arguments)
    $cargoIndex = [Array]::IndexOf($windowsArguments, '-CargoPath')
    Assert-True ($cargoIndex -ge 0 -and $cargoIndex -lt ($windowsArguments.Count - 1) -and
        $windowsArguments[$cargoIndex + 1] -ceq '<CargoPath>') `
        "The $($windowsStep.id) plan does not expose CargoPath."
    Assert-True (@($windowsStep.requirements) -contains `
        'Rust 1.97.1 with x86_64-pc-windows-msvc target') `
        "The $($windowsStep.id) plan does not record the pinned Rust toolchain."
}

$androidStep = @($planObject.steps | Where-Object { $_.id -eq 'android-release' })[0]
Assert-True (@($androidStep.arguments) -contains '--project-dir') `
    'The Android plan does not anchor Gradle to the Android project directory.'
Assert-True (@($androidStep.arguments) -contains '-PtermlinkVersionCode=321') `
    'The Android build number is not sourced from the shared input.'
Assert-True (@($androidStep.arguments) -contains '-PtermlinkVersionName=12.34.56') `
    'The Android version is not sourced from the shared input.'

$publishRejected = $false
try {
    $null = & $scriptPath -Version '1.2.3' -BuildNumber 4 -ServerImage -Plan -Publish
}
catch {
    $publishRejected = $_.Exception.Message.Contains('Publishing is intentionally disabled')
}
Assert-True $publishRejected '-Publish was not rejected explicitly.'

$productionMsixRejected = $false
try {
    $null = & $scriptPath -Version '1.2.3' -BuildNumber 4 -WindowsMsix -Plan
}
catch {
    $productionMsixRejected = $_.Exception.Message.Contains('Production WindowsMsix is disabled')
}
Assert-True $productionMsixRejected `
    'Production MSIX was accepted without complete post-build identity/signature/timestamp verification.'
$productionAabPlan = & $scriptPath -Version '1.2.3' -BuildNumber 4 -AndroidAab -Plan | ConvertFrom-Json
$productionAabStep = @($productionAabPlan.steps | Where-Object { $_.id -eq 'android-release' })[0]
Assert-True ($productionAabPlan.release_mode -ceq 'production' -and
    @($productionAabStep.arguments) -contains ':app:bundleRelease' -and
    @($productionAabStep.requirements) -contains
        'explicit expected Android signer certificate SHA-256 continuity fingerprint') `
    'Production AAB plan does not require the complete identity/version/signer gate.'
Assert-True (-not (Test-Path -LiteralPath $probeOutput)) `
    'Unsupported production target preflight created an output path.'

$signingRejected = $false
try {
    $null = & $scriptPath -Version '1.2.3' -BuildNumber 4 -WindowsSetup -OutputDirectory $probeOutput
}
catch {
    $signingRejected = $_.Exception.Message.Contains('Specify exactly one Windows signing source')
}
Assert-True $signingRejected 'Production execution did not fail closed when signing input was absent.'
Assert-True (-not (Test-Path -LiteralPath $probeOutput)) `
    'Fail-closed production preflight created an output path.'

$storeSigningPlan = & $scriptPath -Version '1.2.3' -BuildNumber 4 -WindowsSetup -Plan `
    -WindowsCertificateThumbprint ('A' * 40) | ConvertFrom-Json
$storeSigningStep = @($storeSigningPlan.steps | Where-Object { $_.id -eq 'windows-setup' })[0]
Assert-True (@($storeSigningStep.arguments) -contains '-CertificateThumbprint') `
    'The Windows setup plan did not represent certificate-store signing.'
Assert-True (-not (@($storeSigningStep.arguments) -contains '-CertificatePassword')) `
    'The certificate-store signing plan still requested a PFX password.'

$serverSigningRejected = $false
try {
    $null = & $scriptPath -Version '1.2.3' -BuildNumber 4 -ServerImage -OutputDirectory $probeOutput
}
catch {
    $serverSigningRejected = $_.Exception.Message.Contains('ServerSigningKeyPath is required')
}
Assert-True $serverSigningRejected 'The production server image did not fail closed without a signing key.'
Assert-True (-not (Test-Path -LiteralPath $probeOutput)) `
    'Server signing preflight created an output path.'

$androidEnvironmentNames = @(
    'TERMLINK_RELEASE_STORE_FILE', 'TERMLINK_RELEASE_STORE_PASSWORD',
    'TERMLINK_RELEASE_KEY_ALIAS', 'TERMLINK_RELEASE_KEY_PASSWORD'
)
$savedAndroidEnvironment = @{}
$androidSigningRejected = $false
try {
    foreach ($name in $androidEnvironmentNames) {
        $savedAndroidEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }
    try {
        $null = & $scriptPath -Version '1.2.3' -BuildNumber 4 -AndroidApk -OutputDirectory $probeOutput
    }
    catch {
        $androidSigningRejected = $_.Exception.Message.Contains('Android release keystore is required')
    }
}
finally {
    foreach ($name in $androidEnvironmentNames) {
        [Environment]::SetEnvironmentVariable($name, $savedAndroidEnvironment[$name], 'Process')
    }
}
Assert-True $androidSigningRejected 'The production Android build did not fail closed without a keystore.'
Assert-True (-not (Test-Path -LiteralPath $probeOutput)) `
    'Android signing preflight created an output path.'

$strictPathRejected = $false
try {
    $null = & $scriptPath -Version '1.2.3' -BuildNumber 4 -ServerImage -Plan `
        -OutputDirectory (Join-Path $repositoryRoot 'release\outside-artifacts')
}
catch {
    $strictPathRejected = $_.Exception.Message.Contains('OutputDirectory must be a child')
}
Assert-True $strictPathRejected 'An output path outside artifacts/release was accepted.'

$manifestProbeName = 'manifest-verification-probe-' + [guid]::NewGuid().ToString('N')
$manifestProbeRoot = Join-Path $repositoryRoot "artifacts\release\$manifestProbeName"
[IO.Directory]::CreateDirectory($manifestProbeRoot) | Out-Null
try {
    $probeArtifactPath = Join-Path $manifestProbeRoot 'probe.sig'
    [IO.File]::WriteAllText($probeArtifactPath, 'release-verification-probe', (New-Object Text.UTF8Encoding -ArgumentList $false))
    $probeFile = Get-Item -LiteralPath $probeArtifactPath
    $probeHash = (Get-FileHash -LiteralPath $probeArtifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $probeManifest = [pscustomobject][ordered]@{
        schema_version = 1
        version = '12.34.56'
        build_number = 321
        release_mode = 'production'
        artifacts = @(
            [pscustomobject][ordered]@{
                path = "artifacts/release/$manifestProbeName/probe.sig"
                size = [int64]$probeFile.Length
                sha256 = $probeHash
                platform = 'generic'
                type = 'signature'
                version = '12.34.56'
            }
        )
    }
    $probeManifestPath = Join-Path $manifestProbeRoot 'artifact-manifest.json'
    [IO.File]::WriteAllText(
        $probeManifestPath,
        (($probeManifest | ConvertTo-Json -Depth 6) + [Environment]::NewLine),
        (New-Object Text.UTF8Encoding -ArgumentList $false))
    $verificationResult = @(& $verificationScriptPath -ReleaseDirectory $manifestProbeRoot `
        -ExpectedReleaseMode Production) -join "`n" | ConvertFrom-Json
    Assert-True ($verificationResult.verified -eq $true -and $verificationResult.release_mode -ceq 'production') `
        'A valid production artifact manifest did not pass the release verifier.'

    $missingSourceRejected = $false
    try {
        $null = & $verificationScriptPath -ReleaseDirectory $manifestProbeRoot `
            -ExpectedReleaseMode Production -RequireSourceIdentity
    }
    catch { $missingSourceRejected = $_.Exception.Message.Contains('missing source_revision') }
    Assert-True $missingSourceRejected 'Strict Production verification accepted a legacy manifest without source_revision.'
    $probeManifest | Add-Member -NotePropertyName source_revision -NotePropertyValue ((& git -C $repositoryRoot rev-parse HEAD).Trim())
    [IO.File]::WriteAllText($probeManifestPath, (($probeManifest | ConvertTo-Json -Depth 6) + "`n"),
        (New-Object Text.UTF8Encoding -ArgumentList $false))
    $boundServerVerification = @(& $verificationScriptPath -ReleaseDirectory $manifestProbeRoot `
        -ExpectedReleaseMode Production -RequireSourceIdentity) -join "`n" | ConvertFrom-Json
    Assert-True ($boundServerVerification.source_revision -ceq $probeManifest.source_revision -and
        $null -eq $boundServerVerification.core_revision) `
        'Server-only manifest required an unrelated terminal core revision.'
    $probeManifest.source_revision = 123
    [IO.File]::WriteAllText($probeManifestPath, (($probeManifest | ConvertTo-Json -Depth 6) + "`n"),
        (New-Object Text.UTF8Encoding -ArgumentList $false))
    $wrongSourceTypeRejected = $false
    try {
        $null = & $verificationScriptPath -ReleaseDirectory $manifestProbeRoot `
            -ExpectedReleaseMode Production -RequireSourceIdentity
    }
    catch { $wrongSourceTypeRejected = $_.Exception.Message.Contains('source_revision must be') }
    Assert-True $wrongSourceTypeRejected 'Production manifest accepted a non-string source revision.'
    $probeManifest.source_revision = '0' * 40 + '-dirty'
    [IO.File]::WriteAllText($probeManifestPath, (($probeManifest | ConvertTo-Json -Depth 6) + "`n"),
        (New-Object Text.UTF8Encoding -ArgumentList $false))
    $dirtySourceRejected = $false
    try {
        $null = & $verificationScriptPath -ReleaseDirectory $manifestProbeRoot `
            -ExpectedReleaseMode Production -RequireSourceIdentity
    }
    catch { $dirtySourceRejected = $_.Exception.Message.Contains('clean source_revision') }
    Assert-True $dirtySourceRejected 'Production manifest accepted a dirty source revision.'
    $probeManifest.PSObject.Properties.Remove('source_revision')
    [IO.File]::WriteAllText($probeManifestPath, (($probeManifest | ConvertTo-Json -Depth 6) + "`n"),
        (New-Object Text.UTF8Encoding -ArgumentList $false))

    $probeManifest.artifacts[0].type = 'msix'
    [IO.File]::WriteAllText(
        $probeManifestPath,
        (($probeManifest | ConvertTo-Json -Depth 6) + [Environment]::NewLine),
        (New-Object Text.UTF8Encoding -ArgumentList $false))
    $unsupportedProductionManifestRejected = $false
    try {
        $null = & $verificationScriptPath -ReleaseDirectory $manifestProbeRoot `
            -ExpectedReleaseMode Production
    }
    catch {
        $unsupportedProductionManifestRejected = $_.Exception.Message.Contains('rejects MSIX')
    }
    Assert-True $unsupportedProductionManifestRejected `
        'The standalone Production verifier accepted an incompletely verified MSIX record.'
    $probeManifest.artifacts[0].type = 'signature'

    $probeManifest.release_mode = 'development'
    [IO.File]::WriteAllText(
        $probeManifestPath,
        (($probeManifest | ConvertTo-Json -Depth 6) + [Environment]::NewLine),
        (New-Object Text.UTF8Encoding -ArgumentList $false))
    $developmentManifestRejected = $false
    try {
        $null = & $verificationScriptPath -ReleaseDirectory $manifestProbeRoot -ExpectedReleaseMode Production
    }
    catch {
        $developmentManifestRejected = $_.Exception.Message.Contains("expected 'production'")
    }
    Assert-True $developmentManifestRejected `
        'A development artifact manifest was accepted by the production release verifier.'
}
finally {
    $releaseArtifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\release'))
    $resolvedProbeRoot = [IO.Path]::GetFullPath($manifestProbeRoot)
    $probePrefix = $releaseArtifactsRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($resolvedProbeRoot.StartsWith($probePrefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedProbeRoot).StartsWith('manifest-verification-probe-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedProbeRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$appShipProbeVersion = '98.76.54'
$appShipProbeBuild = Get-Random -Minimum 40000 -Maximum 65000
$appShipProbeRoot = Join-Path $repositoryRoot `
    "artifacts\release\$appShipProbeVersion-$appShipProbeBuild"
while (Test-Path -LiteralPath $appShipProbeRoot) {
    $appShipProbeBuild = Get-Random -Minimum 40000 -Maximum 65000
    $appShipProbeRoot = Join-Path $repositoryRoot `
        "artifacts\release\$appShipProbeVersion-$appShipProbeBuild"
}
[IO.Directory]::CreateDirectory($appShipProbeRoot) | Out-Null
try {
    $appShipApkName = "TermLink-Android-$appShipProbeVersion-$appShipProbeBuild.apk"
    $appShipApkPath = Join-Path $appShipProbeRoot $appShipApkName
    [IO.File]::WriteAllText(
        $appShipApkPath,
        'appship-production-gate-probe',
        (New-Object Text.UTF8Encoding -ArgumentList $false))
    $appShipApk = Get-Item -LiteralPath $appShipApkPath
    $appShipApkHash = (Get-FileHash -LiteralPath $appShipApkPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $appShipAabName = "TermLink-Android-$appShipProbeVersion-$appShipProbeBuild.aab"
    $appShipAabPath = Join-Path $appShipProbeRoot $appShipAabName
    [IO.File]::WriteAllText(
        $appShipAabPath,
        'appship-production-aab-coexistence-gate-probe',
        (New-Object Text.UTF8Encoding -ArgumentList $false))
    $appShipAab = Get-Item -LiteralPath $appShipAabPath
    $appShipAabHash = (Get-FileHash -LiteralPath $appShipAabPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $appShipSetupName = "TermLink-Setup-$appShipProbeVersion.$appShipProbeBuild-x64.exe"
    $appShipSetupPath = Join-Path $appShipProbeRoot $appShipSetupName
    [IO.File]::WriteAllText(
        $appShipSetupPath,
        'appship-windows-production-gate-probe',
        (New-Object Text.UTF8Encoding -ArgumentList $false))
    $appShipSetup = Get-Item -LiteralPath $appShipSetupPath
    $appShipSetupHash = (Get-FileHash -LiteralPath $appShipSetupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $appShipManifest = [pscustomobject][ordered]@{
        schema_version = 1
        version = $appShipProbeVersion
        build_number = $appShipProbeBuild
        release_mode = 'production'
        artifacts = @(
            [pscustomobject][ordered]@{
                path = "artifacts/release/$appShipProbeVersion-$appShipProbeBuild/$appShipApkName"
                size = [int64]$appShipApk.Length
                sha256 = $appShipApkHash
                platform = 'android'
                type = 'apk'
                version = $appShipProbeVersion
            },
            [pscustomobject][ordered]@{
                path = "artifacts/release/$appShipProbeVersion-$appShipProbeBuild/$appShipAabName"
                size = [int64]$appShipAab.Length
                sha256 = $appShipAabHash
                platform = 'android'
                type = 'aab'
                version = $appShipProbeVersion
            },
            [pscustomobject][ordered]@{
                path = "artifacts/release/$appShipProbeVersion-$appShipProbeBuild/$appShipSetupName"
                size = [int64]$appShipSetup.Length
                sha256 = $appShipSetupHash
                platform = 'windows-x64'
                type = 'setup'
                version = $appShipProbeVersion
            }
        )
    }
    $appShipManifestPath = Join-Path $appShipProbeRoot 'artifact-manifest.json'
    [IO.File]::WriteAllText(
        $appShipManifestPath,
        (($appShipManifest | ConvertTo-Json -Depth 6) + [Environment]::NewLine),
        (New-Object Text.UTF8Encoding -ArgumentList $false))
    $appShipPlanJson = @(& $appShipWrapperPath `
        -ManifestPath $appShipManifestPath `
        -Target AndroidApk `
        -BaseURL 'https://appship.example.test:8910' `
        -AndroidSdkPath 'D:\AndroidSdkPlanProbe' `
        -ExpectedAndroidSignerSha256 $androidSignerSha256 `
        -ExpectedWindowsSignerSha1 $windowsSignerSha1 `
        -Plan) -join "`n"
    $appShipPlan = $appShipPlanJson | ConvertFrom-Json
    Assert-True ($appShipPlan.release_mode -ceq 'production' -and $appShipPlan.upload -eq $false -and
        $appShipPlan.full_verification_performed -eq $false) `
        'AppShip plan implied upload or full production verification.'
    Assert-True ($appShipPlan.application_slug -ceq 'termlink-android' -and
        $appShipPlan.platform -ceq 'android' -and $appShipPlan.file_kind -ceq 'apk' -and
        $appShipPlan.architecture -ceq 'universal') `
        'AppShip Android target mapping is incorrect.'
    Assert-True ([string]$appShipPlan.artifact_path -ceq [IO.Path]::GetFullPath($appShipApkPath)) `
        'AppShip wrapper did not select the exact manifest artifact path.'
    Assert-True ([string]$appShipPlan.artifact_sha256 -ceq $appShipApkHash) `
        'AppShip wrapper did not retain the exact manifest artifact hash.'
    Assert-True ([IO.Path]::GetFileName([string]$appShipPlan.state_path) -ceq
        "termlink-android-stable-$appShipProbeVersion-build$appShipProbeBuild-signature-approved.json") `
        'AppShip wrapper state path is not unique to application/channel/version.'

    $appShipWindowsPlanJson = @(& $appShipWrapperPath `
        -ManifestPath $appShipManifestPath `
        -Target WindowsSetup `
        -BaseURL 'https://appship.example.test:8910' `
        -AndroidSdkPath 'D:\AndroidSdkPlanProbe' `
        -ExpectedAndroidSignerSha256 $androidSignerSha256 `
        -ExpectedWindowsSignerSha1 $windowsSignerSha1 `
        -Plan) -join "`n"
    $appShipWindowsPlan = $appShipWindowsPlanJson | ConvertFrom-Json
    Assert-True ($appShipWindowsPlan.application_slug -ceq 'termlink-win64' -and
        $appShipWindowsPlan.platform -ceq 'windows' -and $appShipWindowsPlan.file_kind -ceq 'exe' -and
        $appShipWindowsPlan.architecture -ceq 'x64') `
        'AppShip Windows target mapping is incorrect.'
    Assert-True ([string]$appShipWindowsPlan.artifact_path -ceq [IO.Path]::GetFullPath($appShipSetupPath) -and
        [string]$appShipWindowsPlan.artifact_sha256 -ceq $appShipSetupHash) `
        'AppShip Windows wrapper did not select the exact manifest path/hash.'
    Assert-True ([IO.Path]::GetFileName([string]$appShipWindowsPlan.state_path) -ceq
        "termlink-win64-stable-$appShipProbeVersion-build$appShipProbeBuild-signature-approved.json") `
        'AppShip Windows state path is not unique to application/channel/version.'
    $appShipPreviewPlanJson = @(& $appShipWrapperPath `
        -ManifestPath $appShipManifestPath `
        -Target WindowsSetup `
        -BaseURL 'https://appship.example.test:8910' `
        -AndroidSdkPath 'D:\AndroidSdkPlanProbe' `
        -ExpectedAndroidSignerSha256 $androidSignerSha256 `
        -ExpectedWindowsSignerSha1 $windowsSignerSha1 `
        -Channel preview `
        -Plan) -join "`n"
    $appShipPreviewPlan = $appShipPreviewPlanJson | ConvertFrom-Json
    Assert-True ($appShipPreviewPlan.application_slug -ceq 'termlink-win64-preview' -and
        $appShipPreviewPlan.release_track -ceq 'preview' -and
        $appShipPreviewPlan.channel -ceq 'beta' -and
        $appShipPreviewPlan.create_application_if_missing -eq $true) `
        'Preview track was not mapped to the AppShip beta channel.'

    if ($PSVersionTable.PSVersion -ge [Version]'7.2') {
        # Exercise the real publish branch without network access. Missing source
        # identity must fail before the reviewed client could be invoked.
        $probePublisherPath = Join-Path $appShipProbeRoot 'never-publish.ps1'
        [IO.File]::WriteAllText($probePublisherPath, @'
#Requires -Version 7.2
# schemaVersion = 'appship.publish-state/v1'
# Idempotency-Key
# SignatureDecision
throw 'The AppShip publisher must not run in this self-test.'
'@, (New-Object Text.UTF8Encoding -ArgumentList $false))
        $probeCredential = [PSCredential]::new('fixture-only',
            (ConvertTo-SecureString 'fixture-only' -AsPlainText -Force))
        $publishProbe = @{
            ManifestPath = $appShipManifestPath
            Target = 'AndroidApk'
            BaseURL = 'https://appship.example.test:8910'
            Credential = $probeCredential
            PublishScriptPath = $probePublisherPath
            AndroidSdkPath = 'D:\AndroidSdkPlanProbe'
            ExpectedAndroidSignerSha256 = $androidSignerSha256
            ExpectedWindowsSignerSha1 = $windowsSignerSha1
            SignatureApproved = $true
            SignatureReviewNote = 'Local fixture only; no upload'
        }
        $missingPublishSourceRejected = $false
        try { $null = & $appShipWrapperPath @publishProbe }
        catch { $missingPublishSourceRejected = $_.Exception.Message.Contains('missing source_revision') }
        Assert-True $missingPublishSourceRejected `
            'The actual AppShip publish branch accepted a client manifest without source_revision.'

        $appShipManifest | Add-Member -NotePropertyName source_revision `
            -NotePropertyValue ((& git -C $repositoryRoot rev-parse HEAD).Trim())
        [IO.File]::WriteAllText($appShipManifestPath,
            (($appShipManifest | ConvertTo-Json -Depth 6) + [Environment]::NewLine),
            (New-Object Text.UTF8Encoding -ArgumentList $false))
        $missingPublishCoreRejected = $false
        try { $null = & $appShipWrapperPath @publishProbe }
        catch { $missingPublishCoreRejected = $_.Exception.Message.Contains('missing core_revision') }
        Assert-True $missingPublishCoreRejected `
            'The actual AppShip publish branch accepted a client manifest without core_revision.'
        $appShipManifest.PSObject.Properties.Remove('source_revision')
    }

    $appShipManifest.release_mode = 'development'
    [IO.File]::WriteAllText(
        $appShipManifestPath,
        (($appShipManifest | ConvertTo-Json -Depth 6) + [Environment]::NewLine),
        (New-Object Text.UTF8Encoding -ArgumentList $false))
    $developmentPublishRejected = $false
    try {
        $null = & $appShipWrapperPath `
            -ManifestPath $appShipManifestPath `
            -Target AndroidApk `
            -BaseURL 'https://appship.example.test:8910' `
            -AndroidSdkPath 'D:\AndroidSdkPlanProbe' `
            -ExpectedAndroidSignerSha256 $androidSignerSha256 `
            -ExpectedWindowsSignerSha1 $windowsSignerSha1 `
            -Plan
    }
    catch { $developmentPublishRejected = $_.Exception.Message.Contains('development artifacts are never publishable') }
    Assert-True $developmentPublishRejected `
        'AppShip wrapper accepted a development artifact manifest.'

    $appShipManifest.release_mode = 'production'
    $appShipManifest.artifacts[0].sha256 = '0' * 64
    [IO.File]::WriteAllText(
        $appShipManifestPath,
        (($appShipManifest | ConvertTo-Json -Depth 6) + [Environment]::NewLine),
        (New-Object Text.UTF8Encoding -ArgumentList $false))
    $wrongPublishHashRejected = $false
    try {
        $null = & $appShipWrapperPath `
            -ManifestPath $appShipManifestPath `
            -Target AndroidApk `
            -BaseURL 'https://appship.example.test:8910' `
            -AndroidSdkPath 'D:\AndroidSdkPlanProbe' `
            -ExpectedAndroidSignerSha256 $androidSignerSha256 `
            -ExpectedWindowsSignerSha1 $windowsSignerSha1 `
            -Plan
    }
    catch { $wrongPublishHashRejected = $_.Exception.Message.Contains('SHA-256') }
    Assert-True $wrongPublishHashRejected `
        'AppShip wrapper accepted artifact bytes that did not match the production manifest.'
}
finally {
    $releaseArtifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\release'))
    $resolvedProbeRoot = [IO.Path]::GetFullPath($appShipProbeRoot)
    $probePrefix = $releaseArtifactsRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($resolvedProbeRoot.StartsWith($probePrefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedProbeRoot).StartsWith(
            "$appShipProbeVersion-",
            [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedProbeRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host 'Build-Release parser, plan, signer-count, version, RFC3161, production-manifest, AppShip-gate, and strict-path checks passed.'
