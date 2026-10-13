[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ApplicationRepository,

    [Parameter(Mandatory)]
    [string]$CoreRepository,

    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [ValidateRange(1, 65535)]
    [int]$BuildNumber,

    [Parameter(Mandatory)]
    [string]$OutputPath,

    [Parameter(Mandatory)]
    [string]$ArtifactManifestPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.IO.Compression

function Assert-Condition {
    param([bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Resolve-FullPath {
    param([Parameter(Mandatory)][string]$Path)
    return [IO.Path]::GetFullPath($Path)
}

function Test-StrictChildPath {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Candidate
    )
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $candidateFull = [IO.Path]::GetFullPath($Candidate)
    return $candidateFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)
}

function Invoke-GitBytes {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'git'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    [void]$start.ArgumentList.Add('-C')
    [void]$start.ArgumentList.Add($Repository)
    foreach ($argument in $Arguments) { [void]$start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        Assert-Condition $process.Start() "Unable to start Git for '$Repository'."
        $stdout = [IO.MemoryStream]::new()
        $process.StandardOutput.BaseStream.CopyTo($stdout)
        $stderr = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            $detail = $stderr.Trim()
            if ([string]::IsNullOrWhiteSpace($detail)) { $detail = "exit code $($process.ExitCode)" }
            throw "Git failed in '$Repository': $detail"
        }
        return ,$stdout.ToArray()
    }
    finally {
        $process.Dispose()
    }
}

function Invoke-GitText {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string[]]$Arguments
    )
    return [Text.Encoding]::UTF8.GetString((Invoke-GitBytes -Repository $Repository -Arguments $Arguments)).Trim()
}

function Get-GitRevision {
    param([Parameter(Mandatory)][string]$Repository)
    $revision = Invoke-GitText -Repository $Repository -Arguments @('rev-parse', '--verify', 'HEAD^{commit}')
    Assert-Condition ($revision -match '^[0-9a-fA-F]{40}$') "Repository HEAD is not a commit: $Repository"
    return $revision.ToLowerInvariant()
}

function Assert-CleanRepository {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Name
    )
    Assert-Condition (Test-Path -LiteralPath (Join-Path $Repository '.git') -PathType Container) "$Name is not a Git repository: $Repository"
    $null = Get-GitRevision -Repository $Repository
    $status = Invoke-GitText -Repository $Repository -Arguments @('status', '--porcelain=v1', '--untracked-files=all')
    Assert-Condition ([string]::IsNullOrWhiteSpace($status)) "$Name repository must be clean; found: $status"
}

function Get-GitTree {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Revision
    )

    $bytes = Invoke-GitBytes -Repository $Repository -Arguments @('ls-tree', '-r', '-z', '--full-tree', $Revision)
    $raw = [Text.Encoding]::UTF8.GetString($bytes)
    $records = New-Object 'System.Collections.Generic.List[object]'
    foreach ($entry in $raw.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)) {
        $tab = $entry.IndexOf([char]9)
        Assert-Condition ($tab -gt 0) "Invalid Git tree record in $Repository."
        $metadata = $entry.Substring(0, $tab).Split(' ')
        Assert-Condition ($metadata.Count -eq 3) "Invalid Git tree metadata in $Repository."
        $mode = $metadata[0]
        $kind = $metadata[1]
        $blob = $metadata[2]
        $path = $entry.Substring($tab + 1)
        Assert-Condition ($path -notmatch '(^|/)\.git($|/)' -and
            -not [IO.Path]::IsPathRooted($path) -and $path -notmatch '(^|/)\.\.?(/|$)') `
            "Unsafe tracked path: $path"
        if ($kind -eq 'commit' -or $mode -eq '160000') {
            throw "Submodules are not permitted in corresponding source: $path"
        }
        Assert-Condition ($kind -eq 'blob') "Unsupported tracked Git object '$kind' at $path"
        Assert-Condition ($mode -in @('100644', '100755', '120000')) "Unsupported Git file mode $mode at $path"
        $records.Add([pscustomobject]@{ Path = $path; Mode = [Convert]::ToInt32($mode, 8); ModeText = $mode; Blob = $blob })
    }
    return @($records | Sort-Object Path)
}

function Read-ByteLine {
    param([Parameter(Mandatory)][IO.Stream]$Stream)
    $buffer = New-Object 'System.Collections.Generic.List[byte]'
    while ($true) {
        $value = $Stream.ReadByte()
        if ($value -lt 0) { throw 'Unexpected end of Git cat-file output.' }
        if ($value -eq 10) { return $buffer.ToArray() }
        $buffer.Add([byte]$value)
    }
}

function Read-ExactBytes {
    param(
        [Parameter(Mandatory)][IO.Stream]$Stream,
        [Parameter(Mandatory)][int64]$Length
    )
    Assert-Condition ($Length -ge 0 -and $Length -le [int32]::MaxValue) "Tracked blob is too large to archive: $Length bytes"
    $buffer = New-Object byte[] ([int]$Length)
    $offset = 0
    while ($offset -lt $buffer.Length) {
        $count = $Stream.Read($buffer, $offset, $buffer.Length - $offset)
        if ($count -le 0) { throw 'Unexpected end of Git cat-file blob.' }
        $offset += $count
    }
    return $buffer
}

function Get-GitBlobBytes {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][object[]]$Entries
    )

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'git'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    [void]$start.ArgumentList.Add('-C')
    [void]$start.ArgumentList.Add($Repository)
    [void]$start.ArgumentList.Add('cat-file')
    [void]$start.ArgumentList.Add('--batch')
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        Assert-Condition $process.Start() "Unable to start Git cat-file for '$Repository'."
        $output = $process.StandardOutput.BaseStream
        foreach ($entry in $Entries) {
            # Request one blob and consume its response before sending the next
            # request. This keeps Git's stdout pipe from filling on large trees.
            $process.StandardInput.WriteLine($entry.Blob)
            $process.StandardInput.Flush()
            $header = [Text.Encoding]::ASCII.GetString((Read-ByteLine -Stream $output)).Split(' ')
            Assert-Condition ($header.Count -eq 3 -and $header[0] -ceq $entry.Blob -and $header[1] -ceq 'blob') `
                "Git blob lookup failed for $($entry.Path)."
            $blobLength = 0L
            Assert-Condition ([int64]::TryParse($header[2], [Globalization.NumberStyles]::Integer, [Globalization.CultureInfo]::InvariantCulture, [ref]$blobLength)) `
                "Invalid Git blob length for $($entry.Path)."
            $bytes = Read-ExactBytes -Stream $output -Length $blobLength
            Assert-Condition ($output.ReadByte() -eq 10) "Invalid Git cat-file separator for $($entry.Path)."
            [pscustomobject]@{ Entry = $entry; Bytes = $bytes }
        }
        $process.StandardInput.Close()
        $process.WaitForExit()
        $stderr = $process.StandardError.ReadToEnd()
        Assert-Condition ($process.ExitCode -eq 0) "Git cat-file failed: $($stderr.Trim())"
    }
    finally {
        if (-not $process.HasExited) { $process.Kill() }
        $process.Dispose()
    }
}

function Test-PrivatePath {
    param([Parameter(Mandatory)][string]$Path)
    return $Path -match '(?i)(^|/)(?:\.env(?:\..*)?|credentials?(?:\..*)?|secrets?(?:\..*)?|tokens?(?:\..*)?|private[-_]?keys?)(/|$)' -or
        $Path -match '(?i)\.(?:pfx|p12|jks|keystore|pem|key)$' -or
        $Path -match '(?i)(^|/)(?:id_(?:rsa|dsa|ecdsa)|authorized_keys)$'
}

function Test-PrivateBytes {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][byte[]]$Bytes
    )
    $text = [Text.Encoding]::UTF8.GetString($Bytes)
    $patterns = @(
        '-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----',
        '(?:gh[pousr]_\w{20,}|github_pat_[A-Za-z0-9_]{30,})',
        'AKIA[0-9A-Z]{16}',
        'xox[baprs]-[A-Za-z0-9-]{20,}',
        '(?<![A-Za-z0-9])sk-[A-Za-z0-9]{20,}'
    )
    foreach ($pattern in $patterns) {
        if ($text -match $pattern) { throw "Tracked private credential pattern found in $Path" }
    }
}

function Get-Sha256Bytes {
    param([Parameter(Mandatory)][byte[]]$Bytes)
    return ([BitConverter]::ToString([Security.Cryptography.SHA256]::HashData($Bytes))).Replace('-', '').ToLowerInvariant()
}

function Get-Sha256File {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Add-ZipBytes {
    param(
        [Parameter(Mandatory)][IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][byte[]]$Bytes,
        [Parameter(Mandatory)][int]$Mode
    )
    $entry = $Archive.CreateEntry($Name, [IO.Compression.CompressionLevel]::Optimal)
    $entry.LastWriteTime = [DateTimeOffset]::new(2026, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    # ExternalAttributes carries the Unix file mode. The high word is read by
    # unzip/tar tooling; 0100000 marks a regular file and 0120000 a symlink.
    $entry.ExternalAttributes = [int32](($Mode -shl 16) -bor 0)
    $stream = $entry.Open()
    try { $stream.Write($Bytes, 0, $Bytes.Length) }
    finally { $stream.Dispose() }
}

function Read-LockAndVerifyNative {
    param(
        [Parameter(Mandatory)][string]$Application,
        [Parameter(Mandatory)][hashtable]$ApplicationTree
    )
    $lockPath = Join-Path $Application 'terminal-core.lock.json'
    Assert-Condition $ApplicationTree.ContainsKey('terminal-core.lock.json') 'Application Git tree must track terminal-core.lock.json.'
    $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
    Assert-Condition ($lock.schema_version -eq 2 -and $lock.revision -match '^[0-9a-f]{40}$' -and $lock.source_dirty -is [bool]) `
        'Invalid terminal-core.lock.json.'
    Assert-Condition (-not $lock.source_dirty) 'Corresponding source requires source_dirty=false in terminal-core.lock.json.'
    $expected = @(
        'native/terminal-core/windows-x64/termlink_terminal_ffi.dll',
        'native/terminal-core/android/arm64-v8a/libtermlink_terminal_jni.so',
        'native/terminal-core/android/armeabi-v7a/libtermlink_terminal_jni.so',
        'native/terminal-core/android/x86/libtermlink_terminal_jni.so',
        'native/terminal-core/android/x86_64/libtermlink_terminal_jni.so'
    )
    $records = @($lock.artifacts)
    Assert-Condition ($records.Count -eq 5) 'terminal-core.lock.json must contain exactly five native artifacts.'
    $verified = New-Object 'System.Collections.Generic.List[object]'
    foreach ($relative in $expected) {
        Assert-Condition ($ApplicationTree.ContainsKey($relative)) "Native library is not tracked: $relative"
        $matches = @($records | Where-Object { $_.path -ceq $relative })
        Assert-Condition ($matches.Count -eq 1) "Missing or duplicate native lock entry: $relative"
        $record = $matches[0]
        $path = Join-Path $Application $relative
        $file = Get-Item -LiteralPath $path -ErrorAction Stop
        $hash = Get-Sha256File -Path $path
        Assert-Condition ($record.sha256 -match '^[0-9a-f]{64}$' -and [int64]$record.size -eq $file.Length -and $record.sha256 -ceq $hash) `
            "Native library SHA-256/size mismatch: $relative"
        $verified.Add([ordered]@{ path = $relative; size = [int64]$file.Length; sha256 = $hash })
    }
    return [pscustomobject]@{ Lock = $lock; Native = $verified.ToArray() }
}

function Read-AndVerifyArtifactManifest {
    param(
        [Parameter(Mandatory)][string]$Application,
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][int]$BuildNumber,
        [Parameter(Mandatory)][string]$ApplicationRevision,
        [Parameter(Mandatory)][string]$CoreRevision
    )
    $releaseRoot = [IO.Path]::GetFullPath((Join-Path $Application "artifacts/release/$Version-$BuildNumber"))
    $resolved = [IO.Path]::GetFullPath($ManifestPath)
    $expectedManifest = Join-Path $releaseRoot 'artifact-manifest.json'
    Assert-Condition ([string]::Equals($resolved, [IO.Path]::GetFullPath($expectedManifest), [StringComparison]::OrdinalIgnoreCase)) `
        'ArtifactManifestPath must be artifacts/release/$Version-$BuildNumber/artifact-manifest.json.'
    Assert-Condition (Test-Path -LiteralPath $resolved -PathType Leaf) "Artifact manifest not found: $resolved"
    $manifest = Get-Content -LiteralPath $resolved -Raw | ConvertFrom-Json
    Assert-Condition ([int]$manifest.schema_version -eq 1 -and [string]$manifest.version -ceq $Version -and
        [int]$manifest.build_number -eq $BuildNumber -and [string]$manifest.release_mode -ceq 'production') `
        'Artifact manifest must be schema 1, the requested version/build, and release_mode=production.'
    Assert-Condition ([string]$manifest.source_revision -match '^[0-9a-f]{40}$' -and
        [string]$manifest.source_revision -ceq $ApplicationRevision) `
        'Artifact manifest source_revision does not match the clean application HEAD.'
    Assert-Condition ([string]$manifest.core_revision -match '^[0-9a-f]{40}$' -and
        [string]$manifest.core_revision -ceq $CoreRevision) `
        'Artifact manifest core_revision does not match terminal-core.lock.json and clean core HEAD.'
    $records = @($manifest.artifacts)
    Assert-Condition ($records.Count -gt 0) 'Artifact manifest must contain at least one artifact.'
    $clientRecords = @($records | Where-Object {
        ([string]$_.platform -match '^windows(?:-|$)' -and [string]$_.type -eq 'setup') -or
        ([string]$_.platform -ceq 'android' -and [string]$_.type -in @('apk', 'aab'))
    })
    $hasWindowsClient = @($records | Where-Object {
        [string]$_.platform -match '^windows(?:-|$)' -and [string]$_.type -eq 'setup'
    }).Count -gt 0
    $hasAndroidClient = @($records | Where-Object {
        [string]$_.platform -ceq 'android' -and [string]$_.type -in @('apk', 'aab')
    }).Count -gt 0
    Assert-Condition ($clientRecords.Count -eq $records.Count -and ($hasWindowsClient -or $hasAndroidClient)) `
        'Corresponding source archive accepts a batch containing Windows Setup or Android APK/AAB client artifacts; server-only or mixed server manifests are unsupported.'
    $paths = [Collections.Hashtable]::new([StringComparer]::OrdinalIgnoreCase)
    $verified = New-Object 'System.Collections.Generic.List[object]'
    foreach ($record in $records) {
        $recordPath = [string]$record.path
        $expectedArtifactPrefix = "artifacts/release/$Version-$BuildNumber/"
        Assert-Condition (-not [string]::IsNullOrWhiteSpace($recordPath) -and $recordPath.StartsWith($expectedArtifactPrefix, [StringComparison]::Ordinal) -and
            $recordPath -notmatch '\\' -and $recordPath -notmatch '(^|/)\.\.?(/|$)' -and
            -not [IO.Path]::IsPathRooted($recordPath)) "Invalid artifact path: $recordPath"
        Assert-Condition (-not $paths.ContainsKey($recordPath)) "Duplicate artifact path: $recordPath"
        $paths[$recordPath] = $true
        $path = [IO.Path]::GetFullPath((Join-Path $Application ($recordPath.Replace('/', [IO.Path]::DirectorySeparatorChar))))
        Assert-Condition (Test-StrictChildPath -Root $releaseRoot -Candidate $path) "Artifact path escaped release batch: $recordPath"
        Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Artifact not found: $recordPath"
        Assert-Condition ([int64]$record.size -ge 1 -and [string]$record.sha256 -match '^[0-9a-f]{64}$') `
            "Invalid artifact size/hash: $recordPath"
        $file = Get-Item -LiteralPath $path
        $hash = Get-Sha256File -Path $path
        Assert-Condition ($file.Length -eq [int64]$record.size -and $hash -ceq [string]$record.sha256) `
            "Artifact bytes do not match the release manifest: $recordPath"
        $versionProperty = $record.PSObject.Properties['version']
        if ($null -ne $versionProperty) {
            Assert-Condition ([string]$record.version -ceq $Version) "Artifact version does not match: $recordPath"
        }
        $verified.Add([ordered]@{ path = $recordPath; size = [int64]$file.Length; sha256 = $hash })
    }
    return [pscustomobject]@{
        Path = $resolved
        Sha256 = Get-Sha256File -Path $resolved
        Version = $Version
        BuildNumber = $BuildNumber
        Artifacts = $verified.ToArray()
    }
}

function Write-TextEntry {
    param(
        [Parameter(Mandatory)][IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Text
    )
    Add-ZipBytes -Archive $Archive -Name $Name -Bytes ([Text.UTF8Encoding]::new($false).GetBytes($Text)) -Mode 0x81A4
}

function Assert-ArchiveContents {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][object[]]$ManifestFiles
    )
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $names = @($zip.Entries | ForEach-Object FullName)
        Assert-Condition ($names.Count -eq ($ManifestFiles.Count + 2)) 'Corresponding source ZIP entry count is inconsistent.'
        Assert-Condition (($names | Select-Object -Unique).Count -eq $names.Count) 'Corresponding source ZIP contains duplicate entries.'
        Assert-Condition ($names -contains 'README-SOURCE.md' -and $names -contains 'SOURCE-MANIFEST.json') `
            'Corresponding source ZIP is missing README-SOURCE.md or SOURCE-MANIFEST.json.'
        foreach ($record in $ManifestFiles) {
            $entry = $zip.GetEntry($record.path)
            Assert-Condition ($null -ne $entry) "Corresponding source ZIP is missing $($record.path)."
            $memory = [IO.MemoryStream]::new()
            $entryStream = $entry.Open()
            try { $entryStream.CopyTo($memory); $bytes = $memory.ToArray() }
            finally { $entryStream.Dispose(); $memory.Dispose() }
            Assert-Condition ([int64]$bytes.Length -eq [int64]$record.size -and (Get-Sha256Bytes -Bytes $bytes) -ceq $record.sha256) `
                "Corresponding source ZIP bytes do not match $($record.path)."
            $mode = ($entry.ExternalAttributes -shr 16) -band 0xFFFF
            Assert-Condition ($mode -eq [Convert]::ToInt32($record.mode, 8)) "Corresponding source ZIP mode does not match $($record.path)."
        }
    }
    finally { $zip.Dispose() }
}

$application = Resolve-FullPath -Path $ApplicationRepository
$core = Resolve-FullPath -Path $CoreRepository
$output = Resolve-FullPath -Path $OutputPath
Assert-Condition ($application -ine $core) 'ApplicationRepository and CoreRepository must be separate Git checkouts.'
Assert-Condition (Test-Path -LiteralPath $application -PathType Container) "Application repository not found: $application"
Assert-Condition (Test-Path -LiteralPath $core -PathType Container) "Core repository not found: $core"
Assert-CleanRepository -Repository $application -Name 'Application'
Assert-CleanRepository -Repository $core -Name 'Core'
$applicationRevision = Get-GitRevision -Repository $application
$coreRevision = Get-GitRevision -Repository $core
$applicationEntries = @(Get-GitTree -Repository $application -Revision $applicationRevision)
$coreEntries = @(Get-GitTree -Repository $core -Revision $coreRevision)
$applicationTree = @{}
foreach ($entry in $applicationEntries) { $applicationTree[$entry.Path] = $entry }
$coreTree = @{}
foreach ($entry in $coreEntries) { $coreTree[$entry.Path] = $entry }
Assert-Condition $applicationTree.ContainsKey('LICENSE') 'Application Git tree must track LICENSE.'
Assert-Condition $applicationTree.ContainsKey('NOTICE') 'Application Git tree must track NOTICE.'
Assert-Condition $coreTree.ContainsKey('LICENSE') 'Core Git tree must track LICENSE.'
Assert-Condition $coreTree.ContainsKey('NOTICE') 'Core Git tree must track NOTICE.'
Assert-Condition (@($coreEntries | Where-Object { $_.Path -like 'scripts/*' -and $_.Path -match '(?i)(build|compile|export|package)' }).Count -gt 0) `
    'Core Git tree must track a build/export script under scripts/.'
$nativeInfo = Read-LockAndVerifyNative -Application $application -ApplicationTree $applicationTree
Assert-Condition ($nativeInfo.Lock.revision -ceq $coreRevision) `
    "terminal-core.lock.json revision $($nativeInfo.Lock.revision) does not match clean core HEAD $coreRevision."
$manifestInput = $ArtifactManifestPath
if (-not [IO.Path]::IsPathRooted($manifestInput)) {
    # A relative manifest is resolved against the application checkout. This
    # keeps callers independent of the process working directory while the
    # verifier still enforces the exact release-batch path below.
    $manifestInput = Join-Path $application $manifestInput
}
$artifactManifest = Read-AndVerifyArtifactManifest -Application $application -ManifestPath (Resolve-FullPath -Path $manifestInput) `
    -Version $Version -BuildNumber $BuildNumber -ApplicationRevision $applicationRevision -CoreRevision $coreRevision

$parent = Split-Path -Parent $output
$null = New-Item -ItemType Directory -Force -Path $parent
$sidecarPath = "$output.sha256.json"
Assert-Condition (-not (Test-Path -LiteralPath $output)) "Output already exists; refusing to overwrite: $output"
Assert-Condition (-not (Test-Path -LiteralPath $sidecarPath)) "Sidecar already exists; refusing to overwrite: $sidecarPath"
$tempZip = "$output.$([Guid]::NewGuid().ToString('N')).tmp"
$tempSidecar = "$output.sha256.json.$([Guid]::NewGuid().ToString('N')).tmp"
$stream = $null
$archive = $null
$movedZip = $false
$movedSidecar = $false
$completed = $false
try {
    $stream = [IO.File]::Open($tempZip, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
    $manifestFiles = New-Object 'System.Collections.Generic.List[object]'
    $seenNames = @{}
    foreach ($repoSpec in @(
            [pscustomobject]@{ Name = 'termlink'; Root = $application; Entries = $applicationEntries },
            [pscustomobject]@{ Name = 'terminal-core'; Root = $core; Entries = $coreEntries })) {
        foreach ($blob in Get-GitBlobBytes -Repository $repoSpec.Root -Entries $repoSpec.Entries) {
            $entry = $blob.Entry
            Test-PrivateBytes -Path "$($repoSpec.Name)/$($entry.Path)" -Bytes $blob.Bytes
            if (Test-PrivatePath -Path $entry.Path) { throw "Tracked private credential path found: $($repoSpec.Name)/$($entry.Path)" }
            $archiveName = "$($repoSpec.Name)/$($entry.Path)"
            Assert-Condition (-not $seenNames.ContainsKey($archiveName)) "Duplicate archive path: $archiveName"
            $seenNames[$archiveName] = $true
            Add-ZipBytes -Archive $archive -Name $archiveName -Bytes $blob.Bytes -Mode $entry.Mode
            $manifestFiles.Add([ordered]@{
                path = $archiveName
                repository = $repoSpec.Name
                source_path = $entry.Path
                git_blob = $entry.Blob
                mode = $entry.ModeText
                size = [int64]$blob.Bytes.Length
                sha256 = Get-Sha256Bytes -Bytes $blob.Bytes
            })
        }
    }
    $artifactLines = @($artifactManifest.Artifacts | ForEach-Object {
        "- $($_.path) ($($_.size) bytes, SHA-256 $($_.sha256))"
    }) -join "`n"
    $readme = @"
# TermLink Corresponding Source

This archive accompanies TermLink version $Version, build $BuildNumber.

- application Git revision: $applicationRevision
- terminal-core Git revision used by the native lock: $coreRevision
- release artifact manifest: $(Split-Path -Leaf $artifactManifest.Path)
- release artifact manifest SHA-256: $($artifactManifest.Sha256)
- layout: termlink/ and terminal-core/ are the two sibling source trees
- terminal-core.lock.json records the five native libraries and their hashes

This corresponding-source tool handles a client release batch only. The
manifest must contain at least one Windows Setup or Android APK/AAB artifact
from the same version/build. Android-only and Windows-only batches are valid;
every record must still be one of those supported client types. A server-only
or mixed server Docker/OCI manifest is intentionally unsupported because it
does not describe the client binaries that this archive accompanies.

The release artifact manifest was verified before this archive was created.
Its artifact records are:
$artifactLines

The archive contains every tracked blob at both clean Git HEADs, including
build scripts, lock files, LICENSE and NOTICE. Git metadata, credentials,
private keys, ignored build output and user files are deliberately excluded.
Build dependencies are resolved using the pinned files and instructions in
termlink/docs/RELEASE_RUNBOOK.md and the repository build documentation.

## Rebuild from this archive

1. Extract this ZIP. The termlink/ and terminal-core/ directories are
   independent source trees and intentionally contain no .git metadata.
2. Create clean local Git snapshots without claiming the original provenance:
   git -C terminal-core init; git -C terminal-core add --all; git -C terminal-core commit -m "rebuild core snapshot", then do the same for termlink/.
   Record the two new HEAD values. They will differ from the original
   revisions in SOURCE-MANIFEST.json; do not rewrite them to the original
   SHA values.
3. From terminal-core/, run scripts/Build-ToTermLink.ps1 with the pinned
   Rust/Android/Windows tools and -TermLinkDirectory pointing at the sibling
   termlink/ checkout. This exports the five native libraries and writes a
   new termlink/terminal-core.lock.json for the rebuilt core revision.
4. Review and commit the new lock and native binaries in the termlink/
   snapshot. A rebuilt lock must use the new core HEAD, not the archived
   binary provenance.
5. For a local development build, run termlink/scripts/Build-Release.ps1
   with explicit -Version $Version -BuildNumber $BuildNumber -ReleaseMode Development
   and the desired platform switches. Production signing keys are not needed
   for this rebuild; use your own development signing identity for any
   installable package. The Development Build-Release path does not create,
   export, or require a TermLink production private key.

## Portable Windows development build

The portable example below produces an unsigned framework-dependent
Windows publish directory for local inspection. It is not a signed Setup and
does not provide update trust. Run it from the extracted `termlink/` tree;
the project path is checked before invoking .NET and the RID is explicit:

```powershell
`$project = Join-Path (Get-Location) 'pc-client/src/Desktop/TermLink.Desktop.csproj'
if (-not (Test-Path -LiteralPath `$project -PathType Leaf)) {
    throw "Windows desktop project not found: `$project"
}
`$output = Join-Path (Get-Location) 'artifacts/portable-windows/win-x64'
dotnet publish `$project -c Release -f net8.0-windows -r win-x64 ``
    --self-contained false -o `$output
```

This portable build uses no release certificate or private key. Use the
documented `Build-Release.ps1 -ReleaseMode Development` flow when a locally
installable, development-signed package is required.

The original release provenance remains the application/core revisions,
native hashes, and artifact hashes recorded in SOURCE-MANIFEST.json and the
external sidecar. A rebuild has different Git SHAs and signatures by design;
this archive does not provide or require private signing keys.

The archive digest is written to the external .sha256.json sidecar after the
ZIP is closed. It is intentionally not copied into this README or
SOURCE-MANIFEST.json, which avoids a circular archive hash.
"@
    Write-TextEntry -Archive $archive -Name 'README-SOURCE.md' -Text $readme.TrimStart()
    $manifest = [ordered]@{
        schema_version = 1
        version = $Version
        build_number = $BuildNumber
        application_revision = $applicationRevision
        core_revision = $coreRevision
        core_lock_revision = $nativeInfo.Lock.revision
        native_artifacts = [array]$nativeInfo.Native
        release_artifact_manifest = [ordered]@{
            path = Split-Path -Leaf $artifactManifest.Path
            sha256 = $artifactManifest.Sha256
            version = $Version
            build_number = $BuildNumber
            artifacts = $artifactManifest.Artifacts
        }
        files = $manifestFiles.ToArray()
        generated_entries = @('README-SOURCE.md', 'SOURCE-MANIFEST.json')
    }
    $manifestJson = $manifest | ConvertTo-Json -Depth 12
    Write-TextEntry -Archive $archive -Name 'SOURCE-MANIFEST.json' -Text ($manifestJson + "`n")
    $archive.Dispose()
    $archive = $null
    $stream.Dispose()
    $stream = $null
    Assert-ArchiveContents -Path $tempZip -ManifestFiles $manifestFiles.ToArray()
    $sidecar = [ordered]@{
        schema_version = 1
        archive = Split-Path -Leaf $output
        size = (Get-Item -LiteralPath $tempZip).Length
        sha256 = Get-Sha256File -Path $tempZip
        version = $Version
        build_number = $BuildNumber
        application_revision = $applicationRevision
        core_revision = $coreRevision
        artifact_manifest_sha256 = $artifactManifest.Sha256
        artifacts = $artifactManifest.Artifacts
        generated_utc = [DateTime]::UtcNow.ToString('O')
    }
    [IO.File]::WriteAllText($tempSidecar, ($sidecar | ConvertTo-Json -Depth 5) + "`n", [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $tempZip -Destination $output
    $movedZip = $true
    Move-Item -LiteralPath $tempSidecar -Destination $sidecarPath
    $movedSidecar = $true
    $completed = $true
    [pscustomobject]@{
        ArchivePath = $output
        SidecarPath = $sidecarPath
        ApplicationRevision = $applicationRevision
        CoreRevision = $coreRevision
        FileCount = $manifestFiles.Count
        Sha256 = $sidecar.sha256
    }
}
finally {
    if ($null -ne $archive) { $archive.Dispose() }
    if ($null -ne $stream) { $stream.Dispose() }
    if (-not $completed) {
        if ($movedSidecar -and (Test-Path -LiteralPath $sidecarPath)) { Remove-Item -LiteralPath $sidecarPath -Force -ErrorAction SilentlyContinue }
        if ($movedZip -and (Test-Path -LiteralPath $output)) { Remove-Item -LiteralPath $output -Force -ErrorAction SilentlyContinue }
    }
    foreach ($temporary in @($tempZip, $tempSidecar)) {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
    }
}
