Set-StrictMode -Version Latest

function ConvertTo-TermLinkNormalizedFingerprint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][ValidateSet(40, 64)][int]$Length,
        [Parameter(Mandatory)][string]$Description
    )

    $normalized = ($Value.Trim().ToLowerInvariant() -replace '[:\-\s]', '')
    if ($normalized -notmatch "^[0-9a-f]{$Length}$") {
        throw "$Description must be exactly $Length hexadecimal characters."
    }
    return $normalized
}

function Assert-TermLinkFingerprintMatch {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Actual,
        [Parameter(Mandatory)][string]$Expected,
        [Parameter(Mandatory)][ValidateSet(40, 64)][int]$Length,
        [Parameter(Mandatory)][string]$Description
    )

    $actualNormalized = ConvertTo-TermLinkNormalizedFingerprint -Value $Actual -Length $Length `
        -Description "$Description actual fingerprint"
    $expectedNormalized = ConvertTo-TermLinkNormalizedFingerprint -Value $Expected -Length $Length `
        -Description "$Description expected fingerprint"
    if (-not [string]::Equals($actualNormalized, $expectedNormalized, [StringComparison]::Ordinal)) {
        throw "$Description does not match the explicitly approved continuity fingerprint."
    }
    return $actualNormalized
}

function Assert-TermLinkAndroidSignerOutput {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$Output,
        [Parameter(Mandatory)][string]$ExpectedSha256
    )

    $text = $Output -join "`n"
    $verifiedSchemes = @(
        [regex]::Matches(
            $text,
            '(?im)^Verified using v([2-9][0-9]*) scheme[^:]*:\s*true\s*$') |
            ForEach-Object { [int]$_.Groups[1].Value } |
            Sort-Object -Unique
    )
    if ($verifiedSchemes.Count -eq 0) {
        throw 'Android APK is not verified by APK Signature Scheme v2 or newer.'
    }

    $signerCountMatches = @([regex]::Matches($text, '(?im)^Number of signers:\s*([0-9]+)\s*$'))
    if ($signerCountMatches.Count -ne 1 -or [int]$signerCountMatches[0].Groups[1].Value -ne 1) {
        $reportedCount = if ($signerCountMatches.Count -eq 1) {
            $signerCountMatches[0].Groups[1].Value
        }
        else {
            'missing or ambiguous'
        }
        throw "Android APK must contain exactly one signer; apksigner reported $reportedCount."
    }

    $signerIndexes = @(
        [regex]::Matches(
            $text,
            '(?im)^Signer #([0-9]+) certificate SHA-256 digest:\s*[0-9a-f:\-\s]+?\s*$') |
            ForEach-Object { [int]$_.Groups[1].Value } |
            Sort-Object -Unique
    )
    if ($signerIndexes.Count -ne 1 -or $signerIndexes[0] -ne 1) {
        throw "Android APK must expose certificate evidence for signer #1 only; found signer indexes: $($signerIndexes -join ', ')."
    }

    $signerFingerprints = @(
        [regex]::Matches(
            $text,
            '(?im)^Signer #[0-9]+ certificate SHA-256 digest:\s*([0-9a-f:\-\s]+?)\s*$') |
            ForEach-Object {
                ConvertTo-TermLinkNormalizedFingerprint -Value $_.Groups[1].Value -Length 64 `
                    -Description 'Android signer certificate SHA-256 fingerprint'
            }
    )
    if ($signerFingerprints.Count -ne 1) {
        throw "Android APK must expose exactly one signer certificate SHA-256 evidence line; found $($signerFingerprints.Count)."
    }

    $fingerprint = Assert-TermLinkFingerprintMatch -Actual $signerFingerprints[0] -Expected $ExpectedSha256 `
        -Length 64 -Description 'Android signer certificate SHA-256 fingerprint'
    return [pscustomobject][ordered]@{
        signer_sha256 = $fingerprint
        verified_schemes = $verifiedSchemes
    }
}

function Assert-TermLinkWindowsVersionEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProductVersion,
        [Parameter(Mandatory)][string]$FileVersion,
        [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$ExpectedVersion
    )

    $escapedVersion = [regex]::Escape($ExpectedVersion)
    if ($ProductVersion -notmatch "^$escapedVersion(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$") {
        throw 'Windows Setup ProductVersion does not match the release version.'
    }
    if ($FileVersion -cne $ExpectedVersion) {
        throw 'Windows Setup FileVersion does not exactly match the release version.'
    }
    return [pscustomobject][ordered]@{
        product_version = $ProductVersion
        file_version = $FileVersion
    }
}

function Assert-TermLinkRfc3161TimestampEvidence {
    [CmdletBinding()]
    param(
        [AllowEmptyCollection()][string[]]$UnsignedAttributeOids = @(),
        [Parameter(Mandatory)][bool]$HasTimeStamperCertificate
    )

    # SignTool /tr records an RFC 3161 timestamp through Microsoft's
    # szOID_RFC3161_counterSign unsigned attribute. This is deliberately
    # distinct from the legacy PKCS #9 counterSignature emitted by /t.
    $rfc3161Oid = '1.3.6.1.4.1.311.3.3.1'
    $legacyCounterSignatureOid = '1.2.840.113549.1.9.6'
    $oids = @($UnsignedAttributeOids | ForEach-Object { ([string]$_).Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($oids -contains $legacyCounterSignatureOid) {
        throw 'Windows Setup uses a legacy Authenticode countersignature instead of an RFC 3161 timestamp.'
    }
    $rfc3161Attributes = @($oids | Where-Object { $_ -ceq $rfc3161Oid })
    if ($rfc3161Attributes.Count -ne 1) {
        throw "Windows Setup must contain exactly one RFC 3161 timestamp-token unsigned attribute; found $($rfc3161Attributes.Count)."
    }
    if (-not $HasTimeStamperCertificate) {
        throw 'Windows did not validate an RFC 3161 timestamp signer certificate.'
    }
    return [pscustomobject][ordered]@{
        timestamp_oid = $rfc3161Oid
        timestamped = $true
    }
}

function Get-TermLinkPeAuthenticodeEvidence {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "Windows Setup not found: $resolvedPath"
    }

    if ($null -eq ('System.Security.Cryptography.Pkcs.SignedCms' -as [type])) {
        try { Add-Type -AssemblyName System.Security -ErrorAction Stop }
        catch {
            try { Add-Type -AssemblyName System.Security.Cryptography.Pkcs -ErrorAction Stop }
            catch { throw 'The SignedCms implementation required for RFC 3161 inspection is unavailable.' }
        }
    }

    $stream = [IO.File]::Open($resolvedPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $reader = New-Object IO.BinaryReader($stream)
    try {
        if ($stream.Length -lt 256) { throw 'Windows Setup is too small to be a PE image.' }
        $stream.Position = 0x3c
        $peOffset = [int64]$reader.ReadUInt32()
        if ($peOffset -lt 0x40 -or $peOffset + 256 -gt $stream.Length) {
            throw 'Windows Setup has an invalid PE header offset.'
        }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw 'Windows Setup has no valid PE signature.' }
        $optionalHeaderOffset = $peOffset + 24
        $stream.Position = $optionalHeaderOffset
        $magic = $reader.ReadUInt16()
        $dataDirectoryOffset = switch ($magic) {
            0x10b { $optionalHeaderOffset + 96 }
            0x20b { $optionalHeaderOffset + 112 }
            default { throw "Windows Setup has an unsupported PE optional-header magic: 0x$('{0:x}' -f $magic)." }
        }
        $certificateDirectoryOffset = $dataDirectoryOffset + (4 * 8)
        if ($certificateDirectoryOffset + 8 -gt $stream.Length) {
            throw 'Windows Setup has a truncated PE certificate directory.'
        }
        $stream.Position = $certificateDirectoryOffset
        $certificateTableOffset = [int64]$reader.ReadUInt32()
        $certificateTableSize = [int64]$reader.ReadUInt32()
        if ($certificateTableOffset -le 0 -or $certificateTableSize -lt 8 -or
            $certificateTableOffset + $certificateTableSize -gt $stream.Length) {
            throw 'Windows Setup has no valid Authenticode certificate table.'
        }

        $tableEnd = $certificateTableOffset + $certificateTableSize
        $cursor = $certificateTableOffset
        $signatureCount = 0
        $unsignedAttributeOids = New-Object 'System.Collections.Generic.List[string]'
        while ($cursor -lt $tableEnd) {
            if ($cursor + 8 -gt $tableEnd) { throw 'Windows Setup has a truncated WIN_CERTIFICATE header.' }
            $stream.Position = $cursor
            $entryLength = [int64]$reader.ReadUInt32()
            $null = $reader.ReadUInt16()
            $certificateType = $reader.ReadUInt16()
            if ($entryLength -lt 8 -or $cursor + $entryLength -gt $tableEnd) {
                throw 'Windows Setup has an invalid WIN_CERTIFICATE length.'
            }
            if ($certificateType -eq 2) {
                $contentLength = [int]($entryLength - 8)
                $content = $reader.ReadBytes($contentLength)
                if ($content.Length -ne $contentLength) { throw 'Windows Setup has truncated Authenticode SignedData.' }
                $cms = New-Object 'System.Security.Cryptography.Pkcs.SignedCms'
                try { $cms.Decode($content) }
                catch { throw 'Windows Setup contains invalid Authenticode SignedData.' }
                foreach ($signer in @($cms.SignerInfos)) {
                    $signatureCount++
                    foreach ($attribute in @($signer.UnsignedAttributes)) {
                        $unsignedAttributeOids.Add([string]$attribute.Oid.Value)
                    }
                }
            }
            $alignedLength = ([int64]$entryLength + 7) -band (-8)
            if ($alignedLength -le 0) { throw 'Windows Setup has an invalid aligned WIN_CERTIFICATE length.' }
            $cursor += $alignedLength
        }
        if ($cursor -ne $tableEnd) { throw 'Windows Setup certificate table alignment is invalid.' }
        if ($signatureCount -ne 1) {
            throw "Windows Setup must contain exactly one Authenticode signer; found $signatureCount."
        }
        return [pscustomobject][ordered]@{
            signature_count = $signatureCount
            unsigned_attribute_oids = @($unsignedAttributeOids)
        }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Assert-TermLinkWindowsSignatureEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Status,
        [Parameter(Mandatory)][string]$ActualSha1,
        [Parameter(Mandatory)][string]$ExpectedSha1
    )

    if ($Status -cne 'Valid') {
        throw "Windows Setup Authenticode status is '$Status', expected 'Valid'."
    }
    return Assert-TermLinkFingerprintMatch -Actual $ActualSha1 -Expected $ExpectedSha1 -Length 40 `
        -Description 'Windows Setup Authenticode signer SHA-1 fingerprint'
}

Export-ModuleMember -Function @(
    'ConvertTo-TermLinkNormalizedFingerprint',
    'Assert-TermLinkFingerprintMatch',
    'Assert-TermLinkAndroidSignerOutput',
    'Assert-TermLinkWindowsSignatureEvidence',
    'Assert-TermLinkWindowsVersionEvidence',
    'Assert-TermLinkRfc3161TimestampEvidence',
    'Get-TermLinkPeAuthenticodeEvidence'
)
