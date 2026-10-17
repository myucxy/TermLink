[CmdletBinding()]
param(
    [string]$PowerShellPath = 'pwsh'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Condition {
    param([bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Invoke-Git {
    param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][string[]]$Arguments)
    & git -C $Repository @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Git failed: $($Arguments -join ' ')" }
}

function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Write-Utf8([string]$Path, [string]$Text) {
    $parent = Split-Path -Parent $Path
    $null = New-Item -ItemType Directory -Force -Path $parent
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function New-Fixture {
    param([Parameter(Mandatory)][string]$Root)
    $app = Join-Path $Root 'termlink'
    $core = Join-Path $Root 'terminal-core'
    $null = New-Item -ItemType Directory -Force -Path $app, $core
    foreach ($repo in @($app, $core)) {
        Invoke-Git -Repository $Root -Arguments @('init', '--quiet', $repo)
        Invoke-Git -Repository $repo -Arguments @('config', 'user.name', 'Source Archive Fixture')
        Invoke-Git -Repository $repo -Arguments @('config', 'user.email', 'fixture@example.invalid')
    }
    Write-Utf8 (Join-Path $core 'LICENSE') 'GNU GENERAL PUBLIC LICENSE\n'
    Write-Utf8 (Join-Path $core 'NOTICE') 'TermLink core fixture notice.\n'
    Write-Utf8 (Join-Path $core 'scripts/Build-ToTermLink.ps1') '# fixture build script\n'
    Write-Utf8 (Join-Path $core 'Cargo.toml') '[workspace]\nmembers = []\n'
    Write-Utf8 (Join-Path $core '.cargo/config.toml') "[target.'cfg(all(target_os = `"windows`", target_env = `"msvc`"))']`nrustflags = [`"-C`", `"target-feature=+crt-static`"]`n"
    Write-Utf8 (Join-Path $core 'src/core.rs') 'pub fn fixture() {}\n'
    Invoke-Git -Repository $core -Arguments @('add', '--all')
    Invoke-Git -Repository $core -Arguments @('update-index', '--chmod=+x', 'scripts/Build-ToTermLink.ps1')
    Invoke-Git -Repository $core -Arguments @('commit', '--quiet', '-m', 'fixture core')
    $coreRevision = (& git -C $core rev-parse HEAD).Trim()

    Write-Utf8 (Join-Path $app 'LICENSE') 'GNU GENERAL PUBLIC LICENSE\n'
    Write-Utf8 (Join-Path $app 'NOTICE') 'TermLink application fixture notice.\n'
    Write-Utf8 (Join-Path $app 'README.md') '# fixture application\n'
    Write-Utf8 (Join-Path $app 'scripts/build.ps1') '# fixture application build\n'
    # The archive must also accept this test's source while rejecting its generated secret fixtures.
    Write-Utf8 (Join-Path $app 'scripts/Test-CorrespondingSourceArchive.ps1') (Get-Content -LiteralPath $PSCommandPath -Raw)
    Write-Utf8 (Join-Path $app '.gitignore') "artifacts/`n"
    $native = @(
        @{ Path = 'native/terminal-core/windows-x64/termlink_terminal_ffi.dll'; Bytes = [byte[]](1, 2, 3, 4) },
        @{ Path = 'native/terminal-core/android/arm64-v8a/libtermlink_terminal_jni.so'; Bytes = [byte[]](5, 6, 7) },
        @{ Path = 'native/terminal-core/android/armeabi-v7a/libtermlink_terminal_jni.so'; Bytes = [byte[]](8, 9) },
        @{ Path = 'native/terminal-core/android/x86/libtermlink_terminal_jni.so'; Bytes = [byte[]](10, 11, 12) },
        @{ Path = 'native/terminal-core/android/x86_64/libtermlink_terminal_jni.so'; Bytes = [byte[]](13, 14, 15, 16) }
    )
    $records = foreach ($item in $native) {
        $path = Join-Path $app $item.Path
        $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path)
        [IO.File]::WriteAllBytes($path, $item.Bytes)
        [ordered]@{ platform = if ($item.Path -like '*windows*') { 'windows' } else { 'android' }; path = $item.Path; sha256 = Get-Sha256 $path; size = $item.Bytes.Length }
    }
    $lock = [ordered]@{ schema_version = 2; repository = 'fixture'; revision = $coreRevision; source_dirty = $false; toolchain = '1.97.1'; artifacts = @($records) }
    Write-Utf8 (Join-Path $app 'terminal-core.lock.json') (($lock | ConvertTo-Json -Depth 8) + "`n")
    # Empty and single-byte tracked blobs: a bare `return $buffer` in the
    # cat-file reader unrolls these into $null / a scalar byte, which crashed
    # the manifest builder under strict mode.
    $null = New-Item -ItemType Directory -Force -Path (Join-Path $app 'fixtures')
    [IO.File]::WriteAllBytes((Join-Path $app 'fixtures/empty-placeholder'), [byte[]]::new(0))
    [IO.File]::WriteAllBytes((Join-Path $app 'fixtures/one-byte-marker'), [byte[]](46))
    Invoke-Git -Repository $app -Arguments @('add', '--all')
    Invoke-Git -Repository $app -Arguments @('commit', '--quiet', '-m', 'fixture app')
    $applicationRevision = (& git -C $app rev-parse HEAD).Trim()
    $artifactPath = Join-Path $app 'artifacts/release/9.8.7-4321/TermLink-Fixture-9.8.7-4321.apk'
    $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $artifactPath)
    [IO.File]::WriteAllBytes($artifactPath, [byte[]](21, 22, 23, 24, 25))
    $androidArtifactRecord = [ordered]@{
        path = 'artifacts/release/9.8.7-4321/TermLink-Fixture-9.8.7-4321.apk'
        size = (Get-Item -LiteralPath $artifactPath).Length
        sha256 = Get-Sha256 $artifactPath
        platform = 'android'
        type = 'apk'
        version = '9.8.7'
    }
    $windowsArtifactPath = Join-Path $app 'artifacts/release/9.8.7-4321/TermLink-Fixture-9.8.7-4321.exe'
    [IO.File]::WriteAllBytes($windowsArtifactPath, [byte[]](31, 32, 33, 34, 35, 36))
    $windowsArtifactRecord = [ordered]@{
        path = 'artifacts/release/9.8.7-4321/TermLink-Fixture-9.8.7-4321.exe'
        size = (Get-Item -LiteralPath $windowsArtifactPath).Length
        sha256 = Get-Sha256 $windowsArtifactPath
        platform = 'windows-x64'
        type = 'setup'
        version = '9.8.7'
    }
    $artifactManifestPath = Join-Path $app 'artifacts/release/9.8.7-4321/artifact-manifest.json'
    $artifactManifest = [ordered]@{
        schema_version = 1
        version = '9.8.7'
        build_number = 4321
        release_mode = 'production'
        source_revision = $applicationRevision
        core_revision = $coreRevision
        artifacts = @($androidArtifactRecord, $windowsArtifactRecord)
    }
    Write-Utf8 $artifactManifestPath (($artifactManifest | ConvertTo-Json -Depth 8) + "`n")
    return [pscustomobject]@{ Application = $app; Core = $core; CoreRevision = $coreRevision; ApplicationRevision = $applicationRevision; ArtifactManifest = $artifactManifestPath }
}

function Invoke-ArchiveExpectFailure {
    param([Parameter(Mandatory)][hashtable]$Arguments, [Parameter(Mandatory)][string]$Name)
    $attempt = @{}
    foreach ($key in $Arguments.Keys) { $attempt[$key] = $Arguments[$key] }
    $attempt.OutputPath = Join-Path $script:root ("expected-" + ($Name -replace '[^A-Za-z0-9_-]', '_') + '.zip')
    $failed = $false
    try { & $script:NewScript @attempt | Out-Null }
    catch { $failed = $true; Write-Host "PASS ${Name}: $($_.Exception.Message)" }
    Assert-Condition $failed "Expected failure did not occur: $Name"
}

function Invoke-ArchiveExpectSuccess {
    param([Parameter(Mandatory)][hashtable]$Arguments, [Parameter(Mandatory)][string]$Name)
    $attempt = @{}
    foreach ($key in $Arguments.Keys) { $attempt[$key] = $Arguments[$key] }
    $attempt.OutputPath = Join-Path $script:root ("success-" + ($Name -replace '[^A-Za-z0-9_-]', '_') + '.zip')
    try { & $script:NewScript @attempt | Out-Null }
    catch { throw "Expected success failed for ${Name}: $($_.Exception.Message)" }
    Assert-Condition (Test-Path -LiteralPath $attempt.OutputPath -PathType Leaf) "Archive was not created for: $Name"
    Assert-Condition (Test-Path -LiteralPath ("$($attempt.OutputPath).sha256.json") -PathType Leaf) "Sidecar was not created for: $Name"
    Write-Host "PASS ${Name}: client-only archive created"
}

$script:NewScript = Join-Path $PSScriptRoot 'New-CorrespondingSourceArchive.ps1'
Assert-Condition (Test-Path -LiteralPath $script:NewScript -PathType Leaf) "Missing script: $script:NewScript"
$root = Join-Path ([IO.Path]::GetTempPath()) "termlink-source-fixture-$([Guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Force -Path $root
try {
    $fixture = New-Fixture -Root $root
    $archive = Join-Path $root 'out/TermLink-Corresponding-Source-9.8.7-4321.zip'
    $archiveArguments = @{ ApplicationRepository = $fixture.Application; CoreRepository = $fixture.Core; Version = '9.8.7'; BuildNumber = 4321; OutputPath = $archive; ArtifactManifestPath = 'artifacts/release/9.8.7-4321/artifact-manifest.json' }
    & $script:NewScript @archiveArguments | Out-Null
    Assert-Condition (Test-Path -LiteralPath $archive -PathType Leaf) 'Archive was not created.'
    $sidecarPath = "$archive.sha256.json"
    Assert-Condition (Test-Path -LiteralPath $sidecarPath -PathType Leaf) 'Archive sidecar was not created.'
    $sidecar = Get-Content -LiteralPath $sidecarPath -Raw | ConvertFrom-Json
    Assert-Condition ($sidecar.sha256 -ceq (Get-Sha256 $archive)) 'Sidecar SHA-256 does not match archive.'
    $releaseManifestSha = Get-Sha256 $fixture.ArtifactManifest
    Assert-Condition ($sidecar.artifact_manifest_sha256 -ceq $releaseManifestSha) 'Sidecar release manifest SHA does not match.'
    Add-Type -AssemblyName System.IO.Compression
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $names = @($zip.Entries | ForEach-Object FullName)
        Assert-Condition ($names -contains 'termlink/terminal-core.lock.json' -and $names -contains 'terminal-core/scripts/Build-ToTermLink.ps1') 'Tracked build/lock files are missing.'
        Assert-Condition ($names -notcontains '.git/config' -and $names -notcontains 'SOURCE-MANIFEST.json.sha256') 'Git metadata or recursive archive hash leaked.'
        Assert-Condition ($names -contains 'termlink/fixtures/empty-placeholder' -and $names -contains 'termlink/fixtures/one-byte-marker') 'Empty and single-byte tracked blobs are missing.'
        Assert-Condition ($zip.GetEntry('termlink/fixtures/empty-placeholder').Length -eq 0) 'Empty tracked blob was not preserved.'
        Assert-Condition ($zip.GetEntry('termlink/fixtures/one-byte-marker').Length -eq 1) 'Single-byte tracked blob was not preserved.'
        $manifestEntry = $zip.GetEntry('SOURCE-MANIFEST.json')
        Assert-Condition ($null -ne $manifestEntry) 'SOURCE-MANIFEST.json is missing.'
        $reader = [IO.StreamReader]::new($manifestEntry.Open(), [Text.UTF8Encoding]::new($false))
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        Assert-Condition ($null -eq $manifest.PSObject.Properties['archive_sha256'] -and $null -eq $manifest.PSObject.Properties['zip_sha256']) 'Manifest contains a circular ZIP hash.'
        Assert-Condition ($manifest.release_artifact_manifest.sha256 -ceq $releaseManifestSha -and
            $manifest.release_artifact_manifest.artifacts[0].sha256 -ceq $sidecar.artifacts[0].sha256) 'Release artifact association is missing.'
        $readmeEntry = $zip.GetEntry('README-SOURCE.md')
        $readmeReader = [IO.StreamReader]::new($readmeEntry.Open(), [Text.UTF8Encoding]::new($false))
        try { $readmePayload = $readmeReader.ReadToEnd() } finally { $readmeReader.Dispose() }
        Assert-Condition ($readmePayload.Contains($releaseManifestSha) -and $readmePayload.Contains($sidecar.artifacts[0].sha256)) 'README release artifact association is missing.'
        $modeEntry = $zip.GetEntry('terminal-core/scripts/Build-ToTermLink.ps1')
        $modeReader = [IO.StreamReader]::new($modeEntry.Open(), [Text.UTF8Encoding]::new($false))
        try { $modePayload = $modeReader.ReadToEnd() } finally { $modeReader.Dispose() }
        Assert-Condition ($modePayload -ceq '# fixture build script\n') 'Tracked blob bytes were not preserved.'
        Assert-Condition ((($modeEntry.ExternalAttributes -shr 16) -band 0xFFFF) -eq 0x81ED) 'Tracked executable mode was not preserved.'
    }
    finally { $zip.Dispose() }
    Write-Host 'PASS clean archive bytes, layout, sidecar hash, and mode'

    # Subsequent documentation commits must not change a past binary's source identity.
    Write-Utf8 (Join-Path $fixture.Core 'later-documentation.md') '# later core documentation'
    Invoke-Git -Repository $fixture.Core -Arguments @('add','--all')
    Invoke-Git -Repository $fixture.Core -Arguments @('commit','--quiet','-m','later core docs')
    Write-Utf8 (Join-Path $fixture.Application 'later-documentation.md') '# later application documentation'
    Invoke-Git -Repository $fixture.Application -Arguments @('add','--all')
    Invoke-Git -Repository $fixture.Application -Arguments @('commit','--quiet','-m','later app docs')
    $pinned=@{};foreach($key in $archiveArguments.Keys){$pinned[$key]=$archiveArguments[$key]}
    $pinned.ApplicationRevision=$fixture.ApplicationRevision
    $pinned.CoreRevision=$fixture.CoreRevision
    $pinned.OutputPath=Join-Path $root 'pinned-old-release.zip'
    & $script:NewScript @pinned | Out-Null
    $pinnedZip=[IO.Compression.ZipFile]::OpenRead($pinned.OutputPath)
    try {
        Assert-Condition ($null -eq $pinnedZip.GetEntry('termlink/later-documentation.md') -and
            $null -eq $pinnedZip.GetEntry('terminal-core/later-documentation.md')) 'Pinned archive included later checkout files.'
        $r=[IO.StreamReader]::new($pinnedZip.GetEntry('SOURCE-MANIFEST.json').Open())
        try{$pinnedManifest=$r.ReadToEnd()|ConvertFrom-Json}finally{$r.Dispose()}
        Assert-Condition ($pinnedManifest.application_revision -ceq $fixture.ApplicationRevision -and
            $pinnedManifest.core_revision -ceq $fixture.CoreRevision) 'Pinned archive provenance changed.'
    } finally { $pinnedZip.Dispose() }
    $pinned.CoreRevision='f'*40
    $pinned.OutputPath=Join-Path $root 'missing-revision.zip'
    Invoke-ArchiveExpectFailure -Arguments $pinned -Name 'unknown pinned commit'
    Invoke-Git -Repository $fixture.Application -Arguments @('reset','--hard','--quiet','HEAD~1')
    Invoke-Git -Repository $fixture.Core -Arguments @('reset','--hard','--quiet','HEAD~1')
    Write-Host 'PASS pinned application/core revisions are independent of later HEADs'

    $mockCargo=Join-Path $root 'mock-cargo.ps1'
    Write-Utf8 $mockCargo @'
param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Remaining)
$destination=$Remaining[-1]
New-Item -ItemType Directory -Path (Join-Path $destination 'fixture-1.0.0') -Force|Out-Null
[IO.File]::WriteAllText((Join-Path $destination 'fixture-1.0.0/LICENSE'),'MIT fixture notice.')
Write-Output '[source.crates-io]'
Write-Output 'replace-with = "vendored-sources"'
Write-Output '[source.vendored-sources]'
Write-Output ('directory = "'+$destination.Replace('\','/')+'"')
$global:LASTEXITCODE=0
'@
    $vendorArgs=@{};foreach($key in $archiveArguments.Keys){$vendorArgs[$key]=$archiveArguments[$key]}
    $vendorArgs.IncludeCoreVendor=$true;$vendorArgs.CargoPath=$mockCargo
    $vendorArgs.OutputPath=Join-Path $root 'vendored-source.zip'
    & $script:NewScript @vendorArgs | Out-Null
    $vendorZip=[IO.Compression.ZipFile]::OpenRead($vendorArgs.OutputPath)
    try {
        Assert-Condition ($null -ne $vendorZip.GetEntry('terminal-core/vendor/fixture-1.0.0/LICENSE')) 'Vendored source notice missing.'
        $r=[IO.StreamReader]::new($vendorZip.GetEntry('terminal-core/.cargo/config.toml').Open())
        try{$config=$r.ReadToEnd()}finally{$r.Dispose()}
        Assert-Condition ($config.Contains('+crt-static') -and $config.Contains('directory = "vendor"') -and
            -not $config.Contains($root)) 'Vendor configuration lost static CRT or leaked an absolute path.'
    } finally { $vendorZip.Dispose() }
    Write-Host 'PASS vendored source and relative Cargo config preserve static CRT'

    $extract = Join-Path $root 'rebuild'
    Expand-Archive -LiteralPath $archive -DestinationPath $extract
    foreach ($repoName in @('termlink', 'terminal-core')) {
        $rebuiltRepo = Join-Path $extract $repoName
        Invoke-Git -Repository $extract -Arguments @('init', '--quiet', $rebuiltRepo)
        Invoke-Git -Repository $rebuiltRepo -Arguments @('config', 'user.name', 'Rebuild Fixture')
        Invoke-Git -Repository $rebuiltRepo -Arguments @('config', 'user.email', 'rebuild@example.invalid')
        Invoke-Git -Repository $rebuiltRepo -Arguments @('add', '--all')
        Invoke-Git -Repository $rebuiltRepo -Arguments @('commit', '--quiet', '-m', 'rebuild source snapshot')
        $status = (& git -C $rebuiltRepo status --porcelain --untracked-files=all)
        Assert-Condition ([string]::IsNullOrWhiteSpace(($status -join "`n"))) "Rebuilt $repoName snapshot is not clean."
    }
    $rebuiltAppRevision = (& git -C (Join-Path $extract 'termlink') rev-parse HEAD).Trim()
    $rebuiltCoreRevision = (& git -C (Join-Path $extract 'terminal-core') rev-parse HEAD).Trim()
    Assert-Condition ($rebuiltAppRevision -cne $fixture.ApplicationRevision -and $rebuiltCoreRevision -cne $fixture.CoreRevision) 'Rebuilt snapshots falsely reused original Git SHAs.'
    $rebuiltLock = Get-Content -LiteralPath (Join-Path $extract 'termlink/terminal-core.lock.json') -Raw | ConvertFrom-Json
    Assert-Condition (-not $rebuiltLock.source_dirty -and $rebuiltLock.revision -ceq $fixture.CoreRevision) 'Rebuilt snapshot lost original native lock provenance.'
    $sourceManifest = Get-Content -LiteralPath (Join-Path $extract 'SOURCE-MANIFEST.json') -Raw | ConvertFrom-Json
    Assert-Condition ($sourceManifest.application_revision -ceq $fixture.ApplicationRevision -and $sourceManifest.core_revision -ceq $fixture.CoreRevision) 'SOURCE-MANIFEST provenance was not retained.'
    Write-Host 'PASS extracted source rebuild snapshots are clean and provenance remains explicit'

    $existingPairFailed = $false
    try { & $script:NewScript @archiveArguments | Out-Null }
    catch { $existingPairFailed = $true; Write-Host "PASS existing ZIP/sidecar pair: $($_.Exception.Message)" }
    Assert-Condition $existingPairFailed 'Existing ZIP/sidecar pair was unexpectedly overwritten.'
    Remove-Item -LiteralPath $archive -Force
    $existingSidecarFailed = $false
    try { & $script:NewScript @archiveArguments | Out-Null }
    catch { $existingSidecarFailed = $true; Write-Host "PASS existing sidecar: $($_.Exception.Message)" }
    Assert-Condition $existingSidecarFailed 'An existing sidecar was unexpectedly overwritten.'

    $manifestPath = $fixture.ArtifactManifest
    $originalManifestText = Get-Content -LiteralPath $manifestPath -Raw

    $androidOnlyManifest = $originalManifestText | ConvertFrom-Json
    $androidOnlyManifest.artifacts = @($androidOnlyManifest.artifacts | Where-Object { [string]$_.platform -ceq 'android' })
    Write-Utf8 $manifestPath (($androidOnlyManifest | ConvertTo-Json -Depth 8) + "`n")
    Invoke-ArchiveExpectSuccess -Arguments $archiveArguments -Name 'android-only artifact manifest'

    $windowsOnlyManifest = $originalManifestText | ConvertFrom-Json
    $windowsOnlyManifest.artifacts = @($windowsOnlyManifest.artifacts | Where-Object { [string]$_.platform -match '^windows(?:-|$)' })
    Write-Utf8 $manifestPath (($windowsOnlyManifest | ConvertTo-Json -Depth 8) + "`n")
    Invoke-ArchiveExpectSuccess -Arguments $archiveArguments -Name 'windows-only artifact manifest'
    Write-Utf8 $manifestPath $originalManifestText

    $badManifest = $originalManifestText | ConvertFrom-Json
    $badManifest.source_revision = ('0' * 40)
    Write-Utf8 $manifestPath (($badManifest | ConvertTo-Json -Depth 8) + "`n")
    Invoke-ArchiveExpectFailure -Arguments $archiveArguments -Name 'artifact manifest source revision mismatch'
    Write-Utf8 $manifestPath $originalManifestText

    $artifactFile = Join-Path $fixture.Application 'artifacts/release/9.8.7-4321/TermLink-Fixture-9.8.7-4321.apk'
    $originalArtifactBytes = [IO.File]::ReadAllBytes($artifactFile)
    [IO.File]::WriteAllBytes($artifactFile, [byte[]](99, 98, 97))
    Invoke-ArchiveExpectFailure -Arguments $archiveArguments -Name 'artifact hash mismatch'
    [IO.File]::WriteAllBytes($artifactFile, $originalArtifactBytes)

    $badManifest = $originalManifestText | ConvertFrom-Json
    $badManifest.artifacts[0].path = 'artifacts/release/../../outside.bin'
    Write-Utf8 $manifestPath (($badManifest | ConvertTo-Json -Depth 8) + "`n")
    Invoke-ArchiveExpectFailure -Arguments $archiveArguments -Name 'artifact traversal path'
    Write-Utf8 $manifestPath $originalManifestText

    $serverArtifactPath = Join-Path $fixture.Application 'artifacts/release/9.8.7-4321/TermLink-Fixture-9.8.7-4321-server.tar'
    [IO.File]::WriteAllBytes($serverArtifactPath, [byte[]](41, 42, 43))
    $serverOnlyManifest = $originalManifestText | ConvertFrom-Json
    $serverOnlyManifest.artifacts = @([ordered]@{
        path = 'artifacts/release/9.8.7-4321/TermLink-Fixture-9.8.7-4321-server.tar'
        size = (Get-Item -LiteralPath $serverArtifactPath).Length
        sha256 = Get-Sha256 $serverArtifactPath
        platform = 'linux-x64'
        type = 'oci'
        version = '9.8.7'
    })
    Write-Utf8 $manifestPath (($serverOnlyManifest | ConvertTo-Json -Depth 8) + "`n")
    Invoke-ArchiveExpectFailure -Arguments $archiveArguments -Name 'server-only artifact manifest'
    Write-Utf8 $manifestPath $originalManifestText

    $oldBatchArtifactPath = Join-Path $fixture.Application 'artifacts/release/9.8.6-4320/TermLink-Fixture-9.8.6-4320.apk'
    $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $oldBatchArtifactPath)
    [IO.File]::WriteAllBytes($oldBatchArtifactPath, [byte[]](51, 52, 53, 54))
    $crossBatchManifest = $originalManifestText | ConvertFrom-Json
    $crossBatchManifest.artifacts[0].path = 'artifacts/release/9.8.6-4320/TermLink-Fixture-9.8.6-4320.apk'
    $crossBatchManifest.artifacts[0].size = (Get-Item -LiteralPath $oldBatchArtifactPath).Length
    $crossBatchManifest.artifacts[0].sha256 = Get-Sha256 $oldBatchArtifactPath
    Write-Utf8 $manifestPath (($crossBatchManifest | ConvertTo-Json -Depth 8) + "`n")
    Invoke-ArchiveExpectFailure -Arguments $archiveArguments -Name 'cross-batch artifact path'
    Write-Utf8 $manifestPath $originalManifestText

    Write-Utf8 (Join-Path $fixture.Application 'README.md') '# dirty fixture\n'
    Invoke-ArchiveExpectFailure -Arguments $archiveArguments -Name 'dirty application'
    Invoke-Git -Repository $fixture.Application -Arguments @('reset', '--hard', '--quiet', 'HEAD')

    $lockPath = Join-Path $fixture.Application 'terminal-core.lock.json'
    $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
    $lock.revision = ('0' * 40)
    Write-Utf8 $lockPath (($lock | ConvertTo-Json -Depth 8) + "`n")
    Invoke-Git -Repository $fixture.Application -Arguments @('add', $lockPath)
    Invoke-Git -Repository $fixture.Application -Arguments @('commit', '--quiet', '-m', 'bad lock fixture')
    Invoke-ArchiveExpectFailure -Arguments $archiveArguments -Name 'lock revision mismatch'
    Invoke-Git -Repository $fixture.Application -Arguments @('reset', '--hard', '--quiet', 'HEAD~1')

    $secretPath = Join-Path $fixture.Application 'secrets/token.txt'
    $fakeToken = ('gh' + 'p' + '_') + '123456789012345678901234567890123456'
    Write-Utf8 $secretPath $fakeToken
    Invoke-Git -Repository $fixture.Application -Arguments @('add', '--all')
    Invoke-Git -Repository $fixture.Application -Arguments @('commit', '--quiet', '-m', 'secret fixture')
    $secretRevisionManifest = $originalManifestText | ConvertFrom-Json
    $secretRevisionManifest.source_revision = (& git -C $fixture.Application rev-parse HEAD).Trim()
    Write-Utf8 $manifestPath (($secretRevisionManifest | ConvertTo-Json -Depth 8) + "`n")
    Invoke-ArchiveExpectFailure -Arguments $archiveArguments -Name 'tracked credential rejection'
    Invoke-Git -Repository $fixture.Application -Arguments @('reset', '--hard', '--quiet', 'HEAD~1')
    Write-Utf8 $manifestPath $originalManifestText

    # `.env.example` 是有意跟踪的占位符模板：路径豁免放行，但内容扫描仍生效
    #（真实密钥模式依旧拒绝）。`.env` 本体仍被路径扫描拒绝。
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
    if (Test-Path -LiteralPath "$archive.sha256.json") { Remove-Item -LiteralPath "$archive.sha256.json" -Force }
    Write-Utf8 (Join-Path $fixture.Application 'server/.env.example') 'DATABASE_URL=placeholder\nAPI_KEY=your-key-here\n'
    Invoke-Git -Repository $fixture.Application -Arguments @('add', '--all')
    Invoke-Git -Repository $fixture.Application -Arguments @('commit', '--quiet', '-m', 'env example fixture')
    $exampleRevisionManifest = $originalManifestText | ConvertFrom-Json
    $exampleRevisionManifest.source_revision = (& git -C $fixture.Application rev-parse HEAD).Trim()
    Write-Utf8 $manifestPath (($exampleRevisionManifest | ConvertTo-Json -Depth 8) + "`n")
    & $script:NewScript @archiveArguments | Out-Null
    Assert-Condition (Test-Path -LiteralPath $archive -PathType Leaf) '.env.example template was wrongly rejected.'
    Write-Host 'PASS .env.example template archive accepted with content scan intact'
    Remove-Item -LiteralPath $archive -Force
    if (Test-Path -LiteralPath "$archive.sha256.json") { Remove-Item -LiteralPath "$archive.sha256.json" -Force }
    Invoke-Git -Repository $fixture.Application -Arguments @('reset', '--hard', '--quiet', 'HEAD~1')

    # 合法模板命中私钥内容时仍被内容扫描拒绝：模板例外不豁免 Test-PrivateBytes。
    Write-Utf8 (Join-Path $fixture.Application 'server/.env.example') (
        'DATABASE_URL=placeholder\n-----BEGIN RSA ' + 'PRIVATE KEY-----\nFAKE-KEY-MATERIAL\n')
    Invoke-Git -Repository $fixture.Application -Arguments @('add', '--all')
    Invoke-Git -Repository $fixture.Application -Arguments @('commit', '--quiet', '-m', 'template private key fixture')
    $templateKeyManifest = $originalManifestText | ConvertFrom-Json
    $templateKeyManifest.source_revision = (& git -C $fixture.Application rev-parse HEAD).Trim()
    Write-Utf8 $manifestPath (($templateKeyManifest | ConvertTo-Json -Depth 8) + "`n")
    Invoke-ArchiveExpectFailure -Arguments $archiveArguments -Name 'template private key content rejection'
    Invoke-Git -Repository $fixture.Application -Arguments @('reset', '--hard', '--quiet', 'HEAD~1')
    Write-Utf8 $manifestPath $originalManifestText

    # 敏感父目录下的 .env.example 不被模板例外放行：secrets/credentials/
    # private-keys 父目录拒绝必须先于叶名例外生效。
    foreach ($sensitiveParent in @('secrets', 'credentials', 'private-keys')) {
        Write-Utf8 (Join-Path $fixture.Application ($sensitiveParent + '/.env.example')) 'PLACEHOLDER=value-here\n'
    }
    Invoke-Git -Repository $fixture.Application -Arguments @('add', '--all')
    Invoke-Git -Repository $fixture.Application -Arguments @('commit', '--quiet', '-m', 'sensitive parent template fixture')
    $sensitiveParentManifest = $originalManifestText | ConvertFrom-Json
    $sensitiveParentManifest.source_revision = (& git -C $fixture.Application rev-parse HEAD).Trim()
    Write-Utf8 $manifestPath (($sensitiveParentManifest | ConvertTo-Json -Depth 8) + "`n")
    Invoke-ArchiveExpectFailure -Arguments $archiveArguments -Name 'sensitive parent .env.example rejection'
    Invoke-Git -Repository $fixture.Application -Arguments @('reset', '--hard', '--quiet', 'HEAD~1')
    Write-Utf8 $manifestPath $originalManifestText

    Write-Utf8 (Join-Path $fixture.Application 'server/.env') 'DATABASE_URL=real-secret-value\n'
    Invoke-Git -Repository $fixture.Application -Arguments @('add', '--all')
    Invoke-Git -Repository $fixture.Application -Arguments @('commit', '--quiet', '-m', 'env real fixture')
    $realEnvManifest = $originalManifestText | ConvertFrom-Json
    $realEnvManifest.source_revision = (& git -C $fixture.Application rev-parse HEAD).Trim()
    Write-Utf8 $manifestPath (($realEnvManifest | ConvertTo-Json -Depth 8) + "`n")
    $realEnvFailed = $false
    try { & $script:NewScript @archiveArguments | Out-Null }
    catch { $realEnvFailed = $true; Write-Host "PASS tracked .env rejection: $($_.Exception.Message)" }
    Assert-Condition $realEnvFailed 'A tracked .env file was unexpectedly accepted.'
    Invoke-Git -Repository $fixture.Application -Arguments @('reset', '--hard', '--quiet', 'HEAD~1')
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
    if (Test-Path -LiteralPath "$archive.sha256.json") { Remove-Item -LiteralPath "$archive.sha256.json" -Force }
    Write-Utf8 $manifestPath $originalManifestText

    Write-Utf8 (Join-Path $fixture.Core 'README.md') '# dirty core fixture\n'
    Invoke-ArchiveExpectFailure -Arguments $archiveArguments -Name 'dirty core'
    Assert-Condition (@(Get-ChildItem -LiteralPath $root -Recurse -Filter '*.tmp' -File -ErrorAction SilentlyContinue).Count -eq 0) 'Owned temporary archive files were left behind.'
    Write-Host 'Corresponding source archive self-test passed.'
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}
