[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ReleaseDirectory,
    [string]$ManifestPath,
    [ValidateSet('Production', 'Development')]
    [string]$ExpectedReleaseMode = 'Production',
    [string]$AndroidSdkPath,
    [string]$ExpectedAndroidSignerSha256,
    [string]$ExpectedWindowsSignerSha1,
    [switch]$RequireWindowsTimestamp
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'ReleaseVerification.psm1') -Force

function Assert-Release {
    param([bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Test-StrictChildPath {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Candidate
    )

    $separator = [IO.Path]::DirectorySeparatorChar
    $rootPrefix = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + $separator
    $fullCandidate = [IO.Path]::GetFullPath($Candidate)
    return $fullCandidate.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)
}

function Get-Sha256Hex {
    param([Parameter(Mandatory)][string]$Path)

    $stream = [IO.File]::OpenRead($Path)
    try {
        $sha256 = [Security.Cryptography.SHA256]::Create()
        try { $hashBytes = $sha256.ComputeHash($stream) }
        finally { $sha256.Dispose() }
    }
    finally { $stream.Dispose() }

    return ([BitConverter]::ToString($hashBytes)).Replace('-', '').ToLowerInvariant()
}

function Invoke-NativeCapture {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Description
    )

    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $FilePath @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    Assert-Release ($exitCode -eq 0) "$Description failed with exit code $exitCode."
    return @($output | ForEach-Object { [string]$_ })
}

function Read-BoundedBundleString {
    param(
        [Parameter(Mandatory)][IO.BinaryReader]$Reader,
        [Parameter(Mandatory)][int]$MaximumBytes,
        [Parameter(Mandatory)][string]$Description
    )

    [int64]$length = 0
    $shift = 0
    $terminated = $false
    for ($index = 0; $index -lt 5; $index++) {
        $value = $Reader.ReadByte()
        if ($index -eq 4 -and ($value -band 0xF0) -ne 0) {
            throw "$Description has an invalid length prefix."
        }
        $length = $length -bor ([int64]($value -band 0x7F) -shl $shift)
        if (($value -band 0x80) -eq 0) {
            $terminated = $true
            break
        }
        $shift += 7
    }
    Assert-Release $terminated "$Description has an unterminated length prefix."
    Assert-Release ($length -ge 0 -and $length -le $MaximumBytes) `
        "$Description exceeds the allowed UTF-8 length."
    $bytes = $Reader.ReadBytes([int]$length)
    Assert-Release ($bytes.Length -eq $length) "$Description was truncated."
    try {
        return [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
    }
    catch {
        throw "$Description is not valid UTF-8."
    }
}

function Resolve-SafeArchiveTarget {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)][string]$Description
    )

    Assert-Release (-not [string]::IsNullOrWhiteSpace($RelativePath)) "$Description is empty."
    Assert-Release ($RelativePath.IndexOf([char]0) -lt 0) "$Description contains a NUL character."
    Assert-Release (-not [IO.Path]::IsPathRooted($RelativePath)) "$Description is rooted."
    Assert-Release (-not $RelativePath.Contains(':')) "$Description contains a drive or alternate-data-stream separator."
    $trimmed = $RelativePath.TrimEnd([char[]]@('\', '/'))
    Assert-Release (-not [string]::IsNullOrWhiteSpace($trimmed)) "$Description resolves to the archive root."
    $segments = @($trimmed -split '[\\/]')
    Assert-Release (@($segments | Where-Object { $_ -in @('', '.', '..') }).Count -eq 0) `
        "$Description contains an empty or traversal segment."
    $normalized = $segments -join [IO.Path]::DirectorySeparatorChar
    try { $target = [IO.Path]::GetFullPath((Join-Path $Root $normalized)) }
    catch { throw "$Description is not a valid local path." }
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    Assert-Release ($target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) `
        "$Description escapes the extraction root."
    return $target
}

function Assert-NoReparsePoint {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Description)

    $item = Get-Item -LiteralPath $Path -Force
    Assert-Release (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) `
        "$Description is a symbolic link or reparse point."
}

function Copy-BoundedFileRange {
    param(
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][int64]$Offset,
        [Parameter(Mandatory)][int64]$Length,
        [Parameter(Mandatory)][string]$DestinationPath,
        [Parameter(Mandatory)][string]$Description
    )

    $source = [IO.File]::Open($SourcePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        Assert-Release ($Offset -ge 0 -and $Length -ge 0 -and $Offset -le $source.Length -and
            $Length -le ($source.Length - $Offset)) "$Description range is outside its source file."
        $source.Position = $Offset
        $destination = [IO.File]::Open(
            $DestinationPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $buffer = New-Object byte[] (128 * 1024)
            [int64]$remaining = $Length
            while ($remaining -gt 0) {
                $requested = [int][Math]::Min([int64]$buffer.Length, $remaining)
                $read = $source.Read($buffer, 0, $requested)
                Assert-Release ($read -gt 0) "$Description ended before its declared length."
                $destination.Write($buffer, 0, $read)
                $remaining -= $read
            }
        }
        finally { $destination.Dispose() }
    }
    finally { $source.Dispose() }
}

function Export-TermLinkSetupPayloadArchive {
    param(
        [Parameter(Mandatory)][string]$SetupPath,
        [Parameter(Mandatory)][string]$WorkingDirectory
    )

    # This is the fixed .NET apphost bundle signature. The preceding Int64 is
    # the bundle-manifest offset patched by Microsoft.NET.HostModel.
    $bundleSignature = [byte[]](
        0x8B, 0x12, 0x02, 0xB9, 0x6A, 0x61, 0x20, 0x38,
        0x72, 0x7B, 0x93, 0x02, 0x14, 0xD7, 0xA0, 0x32,
        0x13, 0xF5, 0xB9, 0xE6, 0xEF, 0xAE, 0x33, 0x18,
        0xEE, 0x3B, 0x2D, 0xCE, 0x24, 0xB3, 0x6A, 0xAE)
    $setupStream = [IO.File]::Open($SetupPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $appHostProbeLength = [int][Math]::Min([int64](32 * 1024 * 1024), $setupStream.Length)
        $appHostBytes = New-Object byte[] $appHostProbeLength
        $bytesRead = $setupStream.Read($appHostBytes, 0, $appHostBytes.Length)
        Assert-Release ($bytesRead -eq $appHostBytes.Length) 'Windows Setup apphost probe was truncated.'
        $signatureOffsets = New-Object 'System.Collections.Generic.List[int]'
        $candidate = 0
        while (($candidate = [Array]::IndexOf($appHostBytes, $bundleSignature[0], $candidate)) -ge 0) {
            $matches = $candidate -le ($appHostBytes.Length - $bundleSignature.Length)
            if ($matches) {
                for ($index = 1; $index -lt $bundleSignature.Length; $index++) {
                    if ($appHostBytes[$candidate + $index] -ne $bundleSignature[$index]) {
                        $matches = $false
                        break
                    }
                }
            }
            if ($matches) { $signatureOffsets.Add($candidate) }
            $candidate++
        }
        Assert-Release ($signatureOffsets.Count -eq 1 -and $signatureOffsets[0] -ge 8) `
            'Windows Setup does not contain exactly one valid .NET bundle marker.'
        $headerOffset = [BitConverter]::ToInt64($appHostBytes, $signatureOffsets[0] - 8)
        Assert-Release ($headerOffset -gt 0 -and $headerOffset -lt $setupStream.Length) `
            'Windows Setup bundle header offset is invalid.'

        $setupStream.Position = $headerOffset
        $reader = [IO.BinaryReader]::new($setupStream, [Text.Encoding]::UTF8, $true)
        try {
            $majorVersion = $reader.ReadUInt32()
            $minorVersion = $reader.ReadUInt32()
            Assert-Release ($majorVersion -eq 6 -and $minorVersion -eq 0) `
                "Windows Setup uses unsupported .NET bundle format $majorVersion.$minorVersion."
            $fileCount = $reader.ReadInt32()
            Assert-Release ($fileCount -gt 0 -and $fileCount -le 4096) `
                'Windows Setup bundle file count is invalid.'
            $bundleId = Read-BoundedBundleString -Reader $reader -MaximumBytes 256 `
                -Description 'Windows Setup bundle ID'
            Assert-Release (-not [string]::IsNullOrWhiteSpace($bundleId)) 'Windows Setup bundle ID is empty.'
            # Bundle format 6 carries locations for deps/runtimeconfig followed by header flags.
            $null = $reader.ReadInt64()
            $null = $reader.ReadInt64()
            $null = $reader.ReadInt64()
            $null = $reader.ReadInt64()
            $null = $reader.ReadUInt64()

            $bundlePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            $setupAssemblyEntries = New-Object 'System.Collections.Generic.List[object]'
            for ($fileIndex = 0; $fileIndex -lt $fileCount; $fileIndex++) {
                $entryOffset = $reader.ReadInt64()
                $entrySize = $reader.ReadInt64()
                $compressedSize = $reader.ReadInt64()
                $entryType = $reader.ReadByte()
                $relativePath = Read-BoundedBundleString -Reader $reader -MaximumBytes 4096 `
                    -Description "Windows Setup bundle path $fileIndex"
                $null = Resolve-SafeArchiveTarget -Root $WorkingDirectory -RelativePath $relativePath `
                    -Description "Windows Setup bundle path '$relativePath'"
                Assert-Release ($bundlePaths.Add($relativePath)) `
                    "Windows Setup bundle contains a duplicate path: $relativePath"
                Assert-Release ($entryOffset -ge 0 -and $entrySize -ge 0 -and $compressedSize -ge 0) `
                    "Windows Setup bundle entry has invalid sizes: $relativePath"
                $storedSize = if ($compressedSize -gt 0) { $compressedSize } else { $entrySize }
                Assert-Release ($entryOffset -le $headerOffset -and $storedSize -le ($headerOffset - $entryOffset)) `
                    "Windows Setup bundle entry escapes the bundle data: $relativePath"
                if ($relativePath -ceq 'TermLink.Setup.dll') {
                    $setupAssemblyEntries.Add([pscustomobject]@{
                        Offset = $entryOffset
                        Size = $entrySize
                        CompressedSize = $compressedSize
                        Type = $entryType
                    })
                }
            }
            Assert-Release ($setupAssemblyEntries.Count -eq 1) `
                'Windows Setup bundle does not contain exactly one TermLink.Setup.dll.'
            $setupAssemblyEntry = $setupAssemblyEntries[0]
            Assert-Release ($setupAssemblyEntry.Type -eq 1 -and $setupAssemblyEntry.CompressedSize -eq 0) `
                'TermLink.Setup.dll is not an uncompressed managed bundle entry.'
        }
        finally { $reader.Dispose() }
    }
    finally { $setupStream.Dispose() }

    $setupAssemblyPath = Join-Path $WorkingDirectory 'TermLink.Setup.dll'
    Copy-BoundedFileRange -SourcePath $SetupPath -Offset $setupAssemblyEntry.Offset `
        -Length $setupAssemblyEntry.Size -DestinationPath $setupAssemblyPath `
        -Description 'TermLink.Setup.dll bundle entry'

    $payloadArchivePath = Join-Path $WorkingDirectory 'TermLink.Setup.Payload.zip'
    $metadataAvailable = $true
    try { Add-Type -AssemblyName System.Reflection.Metadata -ErrorAction Stop }
    catch { $metadataAvailable = $false }
    if (-not $metadataAvailable) {
        Assert-Release ($PSVersionTable.PSEdition -eq 'Desktop') `
            'System.Reflection.Metadata is required to inspect the embedded Setup payload.'
        # Windows PowerShell 5.1 has no System.Reflection.Metadata assembly.
        # ReflectionOnlyLoadFrom reads metadata/resources without executing code.
        $setupAssemblyFile = Get-Item -LiteralPath $setupAssemblyPath
        Assert-Release ($setupAssemblyFile.Length -gt 0 -and $setupAssemblyFile.Length -le 512MB) `
            'TermLink.Setup.dll is too large for Windows PowerShell metadata inspection.'
        $setupAssemblyBytes = [IO.File]::ReadAllBytes($setupAssemblyPath)
        $reflectionAssembly = [Reflection.Assembly]::ReflectionOnlyLoad($setupAssemblyBytes)
        $setupAssemblyBytes = $null
        $resourceNames = @($reflectionAssembly.GetManifestResourceNames() | Where-Object {
            $_ -ceq 'TermLink.Setup.Payload.zip'
        })
        Assert-Release ($resourceNames.Count -eq 1) `
            'TermLink.Setup.dll does not contain exactly one embedded payload resource.'
        $resourceStream = $reflectionAssembly.GetManifestResourceStream('TermLink.Setup.Payload.zip')
        Assert-Release ($null -ne $resourceStream -and $resourceStream.CanRead -and
            $resourceStream.Length -gt 0 -and $resourceStream.Length -le 2GB) `
            'TermLink Setup payload resource length is invalid.'
        try {
            $payloadOutput = [IO.File]::Open(
                $payloadArchivePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try {
                $buffer = New-Object byte[] (128 * 1024)
                [int64]$written = 0
                while (($read = $resourceStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    Assert-Release ($written -le (2GB - $read)) `
                        'TermLink Setup payload resource exceeded the extraction size limit.'
                    $payloadOutput.Write($buffer, 0, $read)
                    $written += $read
                }
                Assert-Release ($written -eq $resourceStream.Length) `
                    'TermLink Setup payload resource was truncated.'
            }
            finally { $payloadOutput.Dispose() }
        }
        finally { $resourceStream.Dispose() }
        return $payloadArchivePath
    }

    $assemblyStream = [IO.File]::Open(
        $setupAssemblyPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $peReader = $null
    try {
        $peReader = [Reflection.PortableExecutable.PEReader]::new(
            $assemblyStream, [Reflection.PortableExecutable.PEStreamOptions]::LeaveOpen)
        Assert-Release $peReader.HasMetadata 'TermLink.Setup.dll has no managed metadata.'
        $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($peReader)
        $payloadResources = @(
            foreach ($handle in $metadata.ManifestResources) {
                $resource = $metadata.GetManifestResource($handle)
                $name = $metadata.GetString($resource.Name)
                if ($name -ceq 'TermLink.Setup.Payload.zip') {
                    [pscustomobject]@{
                        Offset = [int64]$resource.Offset
                        Embedded = $resource.Implementation.IsNil
                    }
                }
            }
        )
        Assert-Release ($payloadResources.Count -eq 1 -and $payloadResources[0].Embedded) `
            'TermLink.Setup.dll does not contain exactly one embedded payload resource.'
        $resourceDirectoryOffset = 0
        Assert-Release ($peReader.PEHeaders.TryGetDirectoryOffset(
                $peReader.PEHeaders.CorHeader.ResourcesDirectory,
                [ref]$resourceDirectoryOffset)) `
            'TermLink.Setup.dll managed resource directory is invalid.'
        $resourcePrefixOffset = [int64]$resourceDirectoryOffset + $payloadResources[0].Offset
        Assert-Release ($resourcePrefixOffset -ge 0 -and $resourcePrefixOffset -le ($assemblyStream.Length - 4)) `
            'TermLink Setup payload resource offset is outside the managed assembly.'
        $assemblyStream.Position = $resourcePrefixOffset
        $lengthReader = [IO.BinaryReader]::new($assemblyStream, [Text.Encoding]::UTF8, $true)
        try { [int64]$payloadLength = $lengthReader.ReadUInt32() }
        finally { $lengthReader.Dispose() }
        Assert-Release ($payloadLength -gt 0 -and $payloadLength -le ($assemblyStream.Length - $assemblyStream.Position)) `
            'TermLink Setup payload resource length is invalid.'
        $payloadOffset = $assemblyStream.Position
    }
    finally {
        if ($null -ne $peReader) { $peReader.Dispose() }
        $assemblyStream.Dispose()
    }

    Copy-BoundedFileRange -SourcePath $setupAssemblyPath -Offset $payloadOffset -Length $payloadLength `
        -DestinationPath $payloadArchivePath -Description 'TermLink Setup embedded payload'
    return $payloadArchivePath
}

function Expand-SafePayloadArchive {
    param(
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string]$Destination
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    Assert-NoReparsePoint -Path $Destination -Description 'Payload extraction root'
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        Assert-Release ($archive.Entries.Count -gt 0 -and $archive.Entries.Count -le 4096) `
            'Setup payload ZIP entry count is invalid.'
        $targets = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        [int64]$declaredTotal = 0
        [int64]$writtenTotal = 0
        [int64]$maximumExtractedBytes = 2GB
        foreach ($entry in $archive.Entries) {
            $target = Resolve-SafeArchiveTarget -Root $Destination -RelativePath $entry.FullName `
                -Description "Setup payload ZIP entry '$($entry.FullName)'"
            Assert-Release ($targets.Add($target)) `
                "Setup payload ZIP contains a duplicate target: $($entry.FullName)"
            [int64]$externalAttributes = $entry.ExternalAttributes
            if ($externalAttributes -lt 0) { $externalAttributes += 4294967296 }
            $unixFileType = ($externalAttributes -shr 16) -band 0xF000
            $windowsAttributes = $externalAttributes -band 0xFFFF
            Assert-Release ($unixFileType -ne 0xA000 -and
                ($windowsAttributes -band [int][IO.FileAttributes]::ReparsePoint) -eq 0) `
                "Setup payload ZIP contains a symbolic link or reparse point: $($entry.FullName)"

            if ([string]::IsNullOrEmpty($entry.Name)) {
                [IO.Directory]::CreateDirectory($target) | Out-Null
                Assert-NoReparsePoint -Path $target -Description "Setup payload directory '$($entry.FullName)'"
                continue
            }
            Assert-Release ($entry.Length -ge 0 -and $entry.Length -le $maximumExtractedBytes -and
                $declaredTotal -le ($maximumExtractedBytes - $entry.Length)) `
                'Setup payload ZIP exceeds the extraction size limit.'
            $declaredTotal += $entry.Length
            $parent = [IO.Path]::GetDirectoryName($target)
            [IO.Directory]::CreateDirectory($parent) | Out-Null
            Assert-NoReparsePoint -Path $parent -Description "Setup payload parent for '$($entry.FullName)'"
            $input = $entry.Open()
            $output = [IO.File]::Open(
                $target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try {
                $buffer = New-Object byte[] (128 * 1024)
                [int64]$entryWritten = 0
                while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    Assert-Release ($writtenTotal -le ($maximumExtractedBytes - $read)) `
                        'Setup payload ZIP exceeded the extraction size limit while expanding.'
                    $output.Write($buffer, 0, $read)
                    $entryWritten += $read
                    $writtenTotal += $read
                }
                Assert-Release ($entryWritten -eq $entry.Length) `
                    "Setup payload ZIP entry length changed while expanding: $($entry.FullName)"
            }
            finally {
                $output.Dispose()
                $input.Dispose()
            }
            Assert-NoReparsePoint -Path $target -Description "Extracted Setup payload file '$($entry.FullName)'"
        }
        Assert-Release ($writtenTotal -eq $declaredTotal) 'Setup payload ZIP expanded length is inconsistent.'
    }
    finally { $archive.Dispose() }

    $unexpectedLinks = @(Get-ChildItem -LiteralPath $Destination -Force -Recurse | Where-Object {
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    })
    Assert-Release ($unexpectedLinks.Count -eq 0) `
        'Extracted Setup payload contains a symbolic link or reparse point.'
}

function Resolve-AndroidBuildTools {
    param([Parameter(Mandatory)][string]$SdkPath)

    $resolvedSdk = [IO.Path]::GetFullPath($SdkPath)
    Assert-Release (Test-Path -LiteralPath $resolvedSdk -PathType Container) `
        "AndroidSdkPath not found: $resolvedSdk"
    $buildToolsRoot = Join-Path $resolvedSdk 'build-tools'
    Assert-Release (Test-Path -LiteralPath $buildToolsRoot -PathType Container) `
        "Android SDK build-tools directory not found: $buildToolsRoot"
    $candidates = @(Get-ChildItem -LiteralPath $buildToolsRoot -Directory | Where-Object {
        (Test-Path -LiteralPath (Join-Path $_.FullName 'aapt2.exe') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $_.FullName 'apksigner.bat') -PathType Leaf)
    } | Sort-Object {
        try { [Version]$_.Name }
        catch { [Version]'0.0' }
    } -Descending)
    Assert-Release ($candidates.Count -gt 0) `
        "No Android build-tools installation contains both aapt2.exe and apksigner.bat under: $buildToolsRoot"
    return $candidates[0]
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRoot = [IO.Path]::GetFullPath($ReleaseDirectory)
Assert-Release (Test-StrictChildPath -Root (Join-Path $repositoryRoot 'artifacts\release') -Candidate $releaseRoot) `
    'ReleaseDirectory must be a child of the repository artifacts/release directory.'
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $resolvedManifestPath = Join-Path $releaseRoot 'artifact-manifest.json'
}
else {
    $manifestCandidate = $ManifestPath
    if (-not [IO.Path]::IsPathRooted($manifestCandidate)) {
        $manifestCandidate = Join-Path $repositoryRoot $manifestCandidate
    }
    $resolvedManifestPath = [IO.Path]::GetFullPath($manifestCandidate)
}
Assert-Release (Test-StrictChildPath -Root $releaseRoot -Candidate $resolvedManifestPath) `
    'ManifestPath must be a file inside ReleaseDirectory.'
Assert-Release (Test-Path -LiteralPath $resolvedManifestPath -PathType Leaf) `
    "Artifact manifest not found: $resolvedManifestPath"

$manifest = Get-Content -LiteralPath $resolvedManifestPath -Raw | ConvertFrom-Json
$expectedMode = $ExpectedReleaseMode.ToLowerInvariant()
Assert-Release ([int]$manifest.schema_version -eq 1) 'Artifact manifest schema_version must be 1.'
Assert-Release ([string]$manifest.version -match '^\d+\.\d+\.\d+$') `
    'Artifact manifest version must contain three numeric components.'
Assert-Release ([int]$manifest.build_number -ge 1 -and [int]$manifest.build_number -le 65535) `
    'Artifact manifest build_number is outside 1..65535.'
Assert-Release ([string]$manifest.release_mode -ceq $expectedMode) `
    "Artifact manifest release_mode is '$($manifest.release_mode)', expected '$expectedMode'."
$artifactRecords = @($manifest.artifacts)
Assert-Release ($artifactRecords.Count -gt 0) 'Artifact manifest must contain at least one artifact.'
if ($expectedMode -ceq 'production') {
    $unsupportedProductionRecords = @($artifactRecords | Where-Object { $_.type -eq 'msix' })
    Assert-Release ($unsupportedProductionRecords.Count -eq 0) `
        'Production verification rejects MSIX until complete post-build identity, version, signer continuity, and timestamp checks are implemented.'
}

$resolvedArtifactPaths = @{}
$hashResults = @(
    foreach ($artifact in $artifactRecords) {
        $recordPath = [string]$artifact.path
        Assert-Release ($recordPath -match '^artifacts/release/') `
            "Artifact manifest path is not repository release-relative: $recordPath"
        $path = [IO.Path]::GetFullPath((Join-Path $repositoryRoot ($recordPath.Replace('/', '\'))))
        Assert-Release (Test-StrictChildPath -Root $releaseRoot -Candidate $path) `
            "Artifact path escaped ReleaseDirectory: $recordPath"
        Assert-Release (-not $resolvedArtifactPaths.ContainsKey($recordPath)) `
            "Artifact manifest contains a duplicate path: $recordPath"
        Assert-Release (Test-Path -LiteralPath $path -PathType Leaf) "Artifact not found: $path"
        Assert-Release ([string]$artifact.version -ceq [string]$manifest.version) `
            "Artifact version does not match the release manifest: $recordPath"
        $file = Get-Item -LiteralPath $path
        $sha256 = Get-Sha256Hex -Path $path
        Assert-Release ($file.Length -eq [int64]$artifact.size) "Artifact size mismatch: $path"
        Assert-Release ($sha256 -ceq [string]$artifact.sha256) "Artifact SHA-256 mismatch: $path"
        $resolvedArtifactPaths[$recordPath] = $path
        [pscustomobject][ordered]@{
            name = $file.Name
            size = [int64]$file.Length
            sha256 = $sha256
            type = [string]$artifact.type
        }
    }
)

$androidResult = $null
$apkRecords = @($artifactRecords | Where-Object { $_.type -eq 'apk' })
Assert-Release ($apkRecords.Count -le 1) 'Release must not contain more than one APK.'
if ($apkRecords.Count -eq 1) {
    Assert-Release (-not [string]::IsNullOrWhiteSpace($ExpectedAndroidSignerSha256)) `
        'ExpectedAndroidSignerSha256 is required to verify an APK.'
    Assert-Release (-not [string]::IsNullOrWhiteSpace($AndroidSdkPath)) `
        'AndroidSdkPath is required to verify an APK.'

    $apkPath = $resolvedArtifactPaths[[string]$apkRecords[0].path]
    $buildTools = Resolve-AndroidBuildTools -SdkPath $AndroidSdkPath
    $aapt2 = Join-Path $buildTools.FullName 'aapt2.exe'
    $apksigner = Join-Path $buildTools.FullName 'apksigner.bat'
    $badgingOutput = Invoke-NativeCapture -FilePath $aapt2 -Arguments @('dump', 'badging', $apkPath) `
        -Description 'APK metadata inspection'
    $badging = $badgingOutput -join "`n"
    $packageMatch = [regex]::Match($badging, "package: name='([^']+)' versionCode='([^']+)' versionName='([^']+)'")
    Assert-Release $packageMatch.Success 'APK package metadata is missing.'
    Assert-Release ($packageMatch.Groups[1].Value -ceq 'dev.termlink.harness') `
        'APK applicationId does not match dev.termlink.harness.'
    Assert-Release ($packageMatch.Groups[3].Value -ceq [string]$manifest.version) `
        'APK versionName does not match the release manifest.'
    Assert-Release ($packageMatch.Groups[2].Value -ceq [string]$manifest.build_number) `
        'APK versionCode does not match the release manifest build number.'

    $signatureOutput = Invoke-NativeCapture -FilePath $apksigner `
        -Arguments @('verify', '--verbose', '--print-certs', $apkPath) `
        -Description 'APK signature verification'
    $signerEvidence = Assert-TermLinkAndroidSignerOutput -Output $signatureOutput `
        -ExpectedSha256 $ExpectedAndroidSignerSha256
    $androidResult = [pscustomobject][ordered]@{
        application_id = $packageMatch.Groups[1].Value
        version_name = $packageMatch.Groups[3].Value
        version_code = [int]$packageMatch.Groups[2].Value
        signer_sha256 = $signerEvidence.signer_sha256
        verified_schemes = @($signerEvidence.verified_schemes)
    }
}

$androidAabResult = $null
$aabRecords = @($artifactRecords | Where-Object { $_.type -eq 'aab' })
Assert-Release ($aabRecords.Count -le 1) 'Release must not contain more than one Android App Bundle.'
if ($aabRecords.Count -eq 1) {
    Assert-Release (-not [string]::IsNullOrWhiteSpace($ExpectedAndroidSignerSha256)) `
        'ExpectedAndroidSignerSha256 is required to verify an AAB.'
    $aabPath = $resolvedArtifactPaths[[string]$aabRecords[0].path]
    $androidAabResult = Get-TermLinkAabVerificationEvidence -Path $aabPath `
        -ExpectedSha256 $ExpectedAndroidSignerSha256
    Assert-Release ($androidAabResult.application_id -ceq 'dev.termlink.harness') `
        'AAB applicationId does not match dev.termlink.harness.'
    Assert-Release ($androidAabResult.version_name -ceq [string]$manifest.version) `
        'AAB versionName does not match the release manifest.'
    Assert-Release ($androidAabResult.version_code -eq [int]$manifest.build_number) `
        'AAB versionCode does not match the release manifest build number.'
}

$windowsResult = $null
$setupRecords = @($artifactRecords | Where-Object { $_.type -eq 'setup' })
Assert-Release ($setupRecords.Count -le 1) 'Release must not contain more than one Windows Setup executable.'
if ($setupRecords.Count -eq 1) {
    Assert-Release (-not [string]::IsNullOrWhiteSpace($ExpectedWindowsSignerSha1)) `
        'ExpectedWindowsSignerSha1 is required to verify a Windows Setup.'
    $setupPath = $resolvedArtifactPaths[[string]$setupRecords[0].path]
    $setupFile = Get-Item -LiteralPath $setupPath
    $setupSignature = Get-AuthenticodeSignature -LiteralPath $setupPath
    Assert-Release ($null -ne $setupSignature.SignerCertificate) 'Windows Setup has no Authenticode signer.'
    $signerSha1 = Assert-TermLinkWindowsSignatureEvidence -Status ([string]$setupSignature.Status) `
        -ActualSha1 ([string]$setupSignature.SignerCertificate.Thumbprint) `
        -ExpectedSha1 $ExpectedWindowsSignerSha1
    $expectedWindowsVersion = "$($manifest.version).$($manifest.build_number)"
    $versionEvidence = Assert-TermLinkWindowsVersionEvidence `
        -ProductVersion ([string]$setupFile.VersionInfo.ProductVersion) `
        -FileVersion ([string]$setupFile.VersionInfo.FileVersion) `
        -ExpectedVersion $expectedWindowsVersion
    $timestampEvidence = $null
    if ($RequireWindowsTimestamp) {
        $peEvidence = Get-TermLinkPeAuthenticodeEvidence -Path $setupPath
        $timestampEvidence = Assert-TermLinkRfc3161TimestampEvidence `
            -UnsignedAttributeOids @($peEvidence.unsigned_attribute_oids) `
            -HasTimeStamperCertificate ($null -ne $setupSignature.TimeStamperCertificate)
    }
    $temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
    $payloadVerificationRoot = [IO.Path]::GetFullPath((Join-Path $temporaryBase `
        ('TermLink-Release-Payload-' + [guid]::NewGuid().ToString('N'))))
    $temporaryPrefix = $temporaryBase + [IO.Path]::DirectorySeparatorChar
    Assert-Release ($payloadVerificationRoot.StartsWith(
            $temporaryPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($payloadVerificationRoot).StartsWith(
            'TermLink-Release-Payload-', [StringComparison]::Ordinal)) `
        'Setup payload verification directory escaped the system temporary directory.'
    $payloadSignatureEvidence = @()
    try {
        [IO.Directory]::CreateDirectory($payloadVerificationRoot) | Out-Null
        Assert-NoReparsePoint -Path $payloadVerificationRoot -Description 'Setup payload verification directory'
        $payloadArchivePath = Export-TermLinkSetupPayloadArchive -SetupPath $setupPath `
            -WorkingDirectory $payloadVerificationRoot
        $payloadRoot = Join-Path $payloadVerificationRoot 'payload'
        Expand-SafePayloadArchive -ArchivePath $payloadArchivePath -Destination $payloadRoot
        foreach ($requiredWindowsRuntime in @(
            'TermLink.TerminalCore.dll',
            'termlink_terminal_ffi.dll',
            'termlink_direct_ffi.dll')) {
            Assert-Release (Test-Path -LiteralPath (Join-Path $payloadRoot $requiredWindowsRuntime) -PathType Leaf) `
                "Windows Setup payload is missing required runtime binary: $requiredWindowsRuntime"
        }
        $ownedPayloadBinaries = @(Get-ChildItem -LiteralPath $payloadRoot -File -Recurse | Where-Object {
            $_.Name -match '^TermLink(?:\..+)?\.(exe|dll)$' -or
            $_.Name -ceq 'termlink_terminal_ffi.dll' -or
            $_.Name -ceq 'termlink_direct_ffi.dll'
        } | Sort-Object FullName)
        Assert-Release ($ownedPayloadBinaries.Count -gt 0) `
            'Windows Setup payload contains no TermLink-owned binaries.'
        $payloadPrefix = [IO.Path]::GetFullPath($payloadRoot).TrimEnd('\', '/') + `
            [IO.Path]::DirectorySeparatorChar
        $payloadSignatureEvidence = @(
            foreach ($binary in $ownedPayloadBinaries) {
                $binaryPath = [IO.Path]::GetFullPath($binary.FullName)
                Assert-Release ($binaryPath.StartsWith($payloadPrefix, [StringComparison]::OrdinalIgnoreCase)) `
                    'A TermLink-owned payload binary escaped the extraction root.'
                $binarySignature = Get-AuthenticodeSignature -LiteralPath $binaryPath
                Assert-Release ($null -ne $binarySignature.SignerCertificate) `
                    "Windows Setup payload binary has no Authenticode signer: $($binary.Name)"
                $binarySignerSha1 = Assert-TermLinkWindowsSignatureEvidence `
                    -Status ([string]$binarySignature.Status) `
                    -ActualSha1 ([string]$binarySignature.SignerCertificate.Thumbprint) `
                    -ExpectedSha1 $ExpectedWindowsSignerSha1
                [pscustomobject][ordered]@{
                    path = $binaryPath.Substring($payloadPrefix.Length).Replace('\', '/')
                    signature_status = [string]$binarySignature.Status
                    signer_sha1 = $binarySignerSha1
                }
            }
        )
    }
    finally {
        if ($payloadVerificationRoot.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($payloadVerificationRoot).StartsWith(
                'TermLink-Release-Payload-', [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $payloadVerificationRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    $windowsResult = [pscustomobject][ordered]@{
        product_version = $versionEvidence.product_version
        file_version = $versionEvidence.file_version
        signature_status = [string]$setupSignature.Status
        signer_sha1 = $signerSha1
        signer_subject = $setupSignature.SignerCertificate.Subject
        timestamped = $null -ne $timestampEvidence
        timestamp_oid = if ($null -eq $timestampEvidence) { $null } else { $timestampEvidence.timestamp_oid }
        payload_verified = $true
        signed_payload_binary_count = $payloadSignatureEvidence.Count
        signed_payload_binaries = $payloadSignatureEvidence
    }
}

$platformResults = @()
$ociRecords = @($artifactRecords | Where-Object { $_.type -eq 'oci-image' })
Assert-Release ($ociRecords.Count -le 1) 'Release must not contain more than one OCI image archive.'
if ($ociRecords.Count -eq 1) {
    $ociPath = $resolvedArtifactPaths[[string]$ociRecords[0].path]
    $temporaryRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'TermLink-Release-Verify'))
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    $temporaryDirectory = [IO.Path]::GetFullPath((Join-Path $temporaryRoot ([guid]::NewGuid().ToString('N'))))
    $temporaryPrefix = $temporaryRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    Assert-Release ($temporaryDirectory.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase)) `
        'Temporary verification directory escaped its root.'
    [IO.Directory]::CreateDirectory($temporaryDirectory) | Out-Null

    try {
        $null = Invoke-NativeCapture -FilePath 'tar' -Arguments @('-xf', $ociPath, '-C', $temporaryDirectory) `
            -Description 'OCI archive extraction'
        $index = Get-Content -LiteralPath (Join-Path $temporaryDirectory 'index.json') -Raw | ConvertFrom-Json
        $candidateDescriptors = @(
            foreach ($rootDescriptor in @($index.manifests)) {
                if ($rootDescriptor.mediaType -eq 'application/vnd.oci.image.index.v1+json') {
                    $nestedDigest = ([string]$rootDescriptor.digest).Split(':')[1]
                    $nestedPath = Join-Path $temporaryDirectory "blobs\sha256\$nestedDigest"
                    $nestedIndex = Get-Content -LiteralPath $nestedPath -Raw | ConvertFrom-Json
                    $nestedIndex.manifests
                }
                else { $rootDescriptor }
            }
        )
        $imageDescriptors = @($candidateDescriptors | Where-Object {
            $platformProperty = $_.PSObject.Properties['platform']
            $null -ne $platformProperty -and $null -ne $platformProperty.Value -and
            $platformProperty.Value.os -eq 'linux' -and
            $platformProperty.Value.architecture -in @('amd64', 'arm64')
        })
        Assert-Release ($imageDescriptors.Count -eq 2) `
            'OCI archive must contain linux/amd64 and linux/arm64 images.'

        $platformResults = @(
            foreach ($descriptor in $imageDescriptors) {
                $architecture = [string]$descriptor.platform.architecture
                $manifestDigest = ([string]$descriptor.digest).Split(':')[1]
                $imageManifestPath = Join-Path $temporaryDirectory "blobs\sha256\$manifestDigest"
                $imageManifest = Get-Content -LiteralPath $imageManifestPath -Raw | ConvertFrom-Json
                $configDigest = ([string]$imageManifest.config.digest).Split(':')[1]
                $configPath = Join-Path $temporaryDirectory "blobs\sha256\$configDigest"
                $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
                $labels = $config.config.Labels
                Assert-Release ($labels.'org.opencontainers.image.version' -ceq "$($manifest.version)+$($manifest.build_number)") `
                    "OCI version label mismatch for linux/$architecture."
                Assert-Release ($labels.'org.opencontainers.image.revision' -match '^[0-9a-f]{40}(?:-dirty)?$') `
                    "OCI revision label is invalid for linux/$architecture."

                $lastLayer = @($imageManifest.layers)[-1]
                $layerDigest = ([string]$lastLayer.digest).Split(':')[1]
                $platformRoot = Join-Path $temporaryDirectory $architecture
                [IO.Directory]::CreateDirectory($platformRoot) | Out-Null
                $null = Invoke-NativeCapture -FilePath 'tar' `
                    -Arguments @('-xf', (Join-Path $temporaryDirectory "blobs\sha256\$layerDigest"), '-C', $platformRoot) `
                    -Description "linux/$architecture application layer extraction"
                $binaryPath = Join-Path $platformRoot 'termlink-server'
                Assert-Release (Test-Path -LiteralPath $binaryPath -PathType Leaf) `
                    "Server binary missing from linux/$architecture image."
                $stream = [IO.File]::OpenRead($binaryPath)
                try {
                    $header = New-Object byte[] 20
                    $bytesRead = $stream.Read($header, 0, $header.Length)
                }
                finally { $stream.Dispose() }
                Assert-Release ($bytesRead -eq 20 -and $header[0] -eq 0x7f -and
                    $header[1] -eq 0x45 -and $header[2] -eq 0x4c -and $header[3] -eq 0x46) `
                    "Server binary is not ELF for linux/$architecture."
                $machine = [BitConverter]::ToUInt16($header, 18)
                $expectedMachine = if ($architecture -eq 'amd64') { 62 } else { 183 }
                Assert-Release ($machine -eq $expectedMachine) `
                    "ELF architecture mismatch for linux/$architecture (machine $machine)."
                [pscustomobject][ordered]@{
                    platform = "linux/$architecture"
                    elf_machine = $machine
                    revision = $labels.'org.opencontainers.image.revision'
                    user = $config.config.User
                }
            }
        )
    }
    finally {
        $resolvedTemporary = [IO.Path]::GetFullPath($temporaryDirectory)
        if ($resolvedTemporary.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

[pscustomobject][ordered]@{
    verified = $true
    version = [string]$manifest.version
    build_number = [int]$manifest.build_number
    release_mode = [string]$manifest.release_mode
    artifacts = $hashResults
    android = $androidResult
    android_aab = $androidAabResult
    windows = $windowsResult
    server = $platformResults
} | ConvertTo-Json -Depth 8
