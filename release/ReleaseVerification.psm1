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

    # New Build Tools label certificates by scheme instead of using Signer #1.
    $schemeCertificates = @([regex]::Matches($text,
        '(?im)^V([2-9][0-9]*(?:\.[0-9]+)?) Signer: certificate SHA-256 digest:[^\r\n]+$'))
    if ($schemeCertificates.Count -gt 0) {
        $schemeNames = @($schemeCertificates | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
        if ($schemeNames.Count -ne $schemeCertificates.Count -or
            @($schemeNames | Where-Object { $_ -notmatch '^\d+$' -or [int]$_ -notin $verifiedSchemes }).Count -gt 0 -or
            $text -match '(?im)^Signer #') {
            throw 'Android APK has ambiguous signer certificate evidence.'
        }
        $text = [regex]::Replace($text, '(?im)^V[2-9][0-9]* Signer: certificate SHA-256 digest:',
            'Signer #1 certificate SHA-256 digest:')
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
    if ($signerFingerprints.Count -eq 0 -or
        ($schemeCertificates.Count -eq 0 -and $signerFingerprints.Count -ne 1) -or
        ($schemeCertificates.Count -gt 0 -and @($signerFingerprints | Sort-Object -Unique).Count -ne 1)) {
        throw "Android APK must expose exactly one signer certificate SHA-256 evidence line; found $($signerFingerprints.Count)."
    }

    $fingerprint = Assert-TermLinkFingerprintMatch -Actual $signerFingerprints[0] -Expected $ExpectedSha256 `
        -Length 64 -Description 'Android signer certificate SHA-256 fingerprint'
    return [pscustomobject][ordered]@{
        signer_sha256 = $fingerprint
        verified_schemes = $verifiedSchemes
    }
}

function Read-TermLinkBoundedZipEntryBytes {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Entry,
        [Parameter(Mandatory)][int64]$MaximumBytes,
        [Parameter(Mandatory)][string]$Description
    )

    if ([int64]$Entry.Length -lt 0 -or [int64]$Entry.Length -gt $MaximumBytes) {
        throw "$Description exceeds the allowed length."
    }
    $input = $Entry.Open()
    $output = [IO.MemoryStream]::new()
    try {
        $buffer = New-Object byte[] (64 * 1024)
        [int64]$written = 0
        while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
            if ($written -gt ($MaximumBytes - $read)) {
                throw "$Description exceeded the allowed length while expanding."
            }
            $output.Write($buffer, 0, $read)
            $written += $read
        }
        if ($written -ne [int64]$Entry.Length) {
            throw "$Description expanded length does not match the ZIP directory."
        }
        return ,$output.ToArray()
    }
    finally {
        $output.Dispose()
        $input.Dispose()
    }
}

function Read-TermLinkProtobufVarint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][IO.BinaryReader]$Reader,
        [Parameter(Mandatory)][string]$Description
    )

    [uint64]$value = 0
    $shift = 0
    for ($index = 0; $index -lt 10; $index++) {
        try { [byte]$current = $Reader.ReadByte() }
        catch { throw "$Description contains a truncated protobuf varint." }
        if ($index -eq 9 -and ($current -band 0xFE) -ne 0) {
            throw "$Description contains an overflowing protobuf varint."
        }
        $value = $value -bor ([uint64]($current -band 0x7F) -shl $shift)
        if (($current -band 0x80) -eq 0) { return $value }
        $shift += 7
    }
    throw "$Description contains an unterminated protobuf varint."
}

function Read-TermLinkProtobufFields {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][byte[]]$Bytes,
        [Parameter(Mandatory)][string]$Description
    )

    $stream = [IO.MemoryStream]::new($Bytes, $false)
    $reader = [IO.BinaryReader]::new($stream)
    $fields = [Collections.Generic.List[object]]::new()
    try {
        while ($stream.Position -lt $stream.Length) {
            $key = Read-TermLinkProtobufVarint -Reader $reader -Description $Description
            $fieldNumber = [int]($key -shr 3)
            $wireType = [int]($key -band 7)
            if ($fieldNumber -le 0) { throw "$Description contains protobuf field number zero." }
            switch ($wireType) {
                0 { $fieldValue = Read-TermLinkProtobufVarint -Reader $reader -Description $Description }
                1 {
                    $fieldValue = $reader.ReadBytes(8)
                    if ($fieldValue.Length -ne 8) { throw "$Description contains a truncated fixed64 field." }
                }
                2 {
                    $lengthValue = Read-TermLinkProtobufVarint -Reader $reader -Description $Description
                    if ($lengthValue -gt [int]::MaxValue -or
                        [int64]$lengthValue -gt ($stream.Length - $stream.Position)) {
                        throw "$Description contains an invalid length-delimited field."
                    }
                    $fieldValue = $reader.ReadBytes([int]$lengthValue)
                    if ($fieldValue.Length -ne [int]$lengthValue) {
                        throw "$Description contains a truncated length-delimited field."
                    }
                }
                5 {
                    $fieldValue = $reader.ReadBytes(4)
                    if ($fieldValue.Length -ne 4) { throw "$Description contains a truncated fixed32 field." }
                }
                default { throw "$Description uses unsupported protobuf wire type $wireType." }
            }
            $fields.Add([pscustomobject]@{
                number = $fieldNumber
                wire_type = $wireType
                value = $fieldValue
            })
        }
        return $fields.ToArray()
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Get-TermLinkSingleProtobufField {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]]$Fields,
        [Parameter(Mandatory)][int]$Number,
        [Parameter(Mandatory)][ValidateSet(0, 1, 2, 5)][int]$WireType,
        [Parameter(Mandatory)][string]$Description,
        [switch]$Optional
    )

    $matches = @($Fields | Where-Object { $_.number -eq $Number })
    if ($Optional -and $matches.Count -eq 0) { return $null }
    if ($matches.Count -ne 1 -or [int]$matches[0].wire_type -ne $WireType) {
        throw "$Description must occur exactly once with protobuf wire type $WireType."
    }
    return $matches[0]
}

function ConvertFrom-TermLinkProtobufUtf8 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][byte[]]$Bytes,
        [Parameter(Mandatory)][string]$Description
    )

    try { return [Text.UTF8Encoding]::new($false, $true).GetString($Bytes) }
    catch { throw "$Description is not valid UTF-8." }
}

function Get-TermLinkAabManifestIdentity {
    [CmdletBinding()]
    param([Parameter(Mandatory)][byte[]]$Bytes)

    $rootFields = @(Read-TermLinkProtobufFields -Bytes $Bytes -Description 'AAB base manifest root')
    $elementField = Get-TermLinkSingleProtobufField -Fields $rootFields -Number 1 -WireType 2 `
        -Description 'AAB base manifest root element'
    $elementFields = @(Read-TermLinkProtobufFields -Bytes $elementField.value `
        -Description 'AAB base manifest element')
    $elementNameField = Get-TermLinkSingleProtobufField -Fields $elementFields -Number 3 -WireType 2 `
        -Description 'AAB base manifest element name'
    $elementName = ConvertFrom-TermLinkProtobufUtf8 -Bytes $elementNameField.value `
        -Description 'AAB base manifest element name'
    if ($elementName -cne 'manifest') { throw 'AAB base manifest root element is not manifest.' }

    $attributes = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($attributeField in @($elementFields | Where-Object { $_.number -eq 4 })) {
        if ([int]$attributeField.wire_type -ne 2) {
            throw 'AAB base manifest attribute uses an invalid protobuf wire type.'
        }
        $attributeFields = @(Read-TermLinkProtobufFields -Bytes $attributeField.value `
            -Description 'AAB base manifest attribute')
        $namespaceField = Get-TermLinkSingleProtobufField -Fields $attributeFields -Number 1 -WireType 2 `
            -Description 'AAB base manifest attribute namespace' -Optional
        $nameField = Get-TermLinkSingleProtobufField -Fields $attributeFields -Number 2 -WireType 2 `
            -Description 'AAB base manifest attribute name'
        $valueField = Get-TermLinkSingleProtobufField -Fields $attributeFields -Number 3 -WireType 2 `
            -Description 'AAB base manifest attribute value' -Optional
        $namespace = if ($null -eq $namespaceField) { '' } else {
            ConvertFrom-TermLinkProtobufUtf8 -Bytes $namespaceField.value `
                -Description 'AAB base manifest attribute namespace'
        }
        $name = ConvertFrom-TermLinkProtobufUtf8 -Bytes $nameField.value `
            -Description 'AAB base manifest attribute name'
        $value = if ($null -eq $valueField) { '' } else {
            ConvertFrom-TermLinkProtobufUtf8 -Bytes $valueField.value `
                -Description 'AAB base manifest attribute value'
        }
        $key = "$namespace`n$name"
        if ($attributes.ContainsKey($key)) {
            throw "AAB base manifest contains a duplicate attribute: $name"
        }
        $attributes.Add($key, [pscustomobject]@{
            namespace = $namespace
            name = $name
            value = $value
            fields = $attributeFields
        })
    }

    $androidNamespace = 'http://schemas.android.com/apk/res/android'
    $packageKey = "`npackage"
    $versionNameKey = "$androidNamespace`nversionName"
    $versionCodeKey = "$androidNamespace`nversionCode"
    foreach ($requiredKey in @($packageKey, $versionNameKey, $versionCodeKey)) {
        if (-not $attributes.ContainsKey($requiredKey)) {
            throw 'AAB base manifest is missing package, versionName, or versionCode identity metadata.'
        }
    }
    $applicationId = [string]$attributes[$packageKey].value
    $versionName = [string]$attributes[$versionNameKey].value
    $versionCodeText = [string]$attributes[$versionCodeKey].value
    if ($applicationId -notmatch '^[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*)+$') {
        throw 'AAB applicationId is malformed.'
    }
    if ($versionName.Length -eq 0 -or $versionName.Length -gt 128) {
        throw 'AAB versionName is missing or too long.'
    }
    if ($versionCodeText -notmatch '^[1-9][0-9]*$') { throw 'AAB versionCode is malformed.' }
    [int64]$versionCode = 0
    if (-not [int64]::TryParse($versionCodeText, [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture, [ref]$versionCode) -or
        $versionCode -gt [int]::MaxValue) {
        throw 'AAB versionCode is outside the supported Android range.'
    }

    $versionCodeFields = @($attributes[$versionCodeKey].fields)
    $resourceIdField = Get-TermLinkSingleProtobufField -Fields $versionCodeFields -Number 5 -WireType 0 `
        -Description 'AAB versionCode resource ID'
    if ([uint64]$resourceIdField.value -ne 0x0101021B) {
        throw 'AAB versionCode does not use the Android framework versionCode resource ID.'
    }
    $compiledItemField = Get-TermLinkSingleProtobufField -Fields $versionCodeFields -Number 6 -WireType 2 `
        -Description 'AAB compiled versionCode item'
    $itemFields = @(Read-TermLinkProtobufFields -Bytes $compiledItemField.value `
        -Description 'AAB compiled versionCode item')
    $primitiveField = Get-TermLinkSingleProtobufField -Fields $itemFields -Number 7 -WireType 2 `
        -Description 'AAB compiled versionCode primitive'
    $primitiveFields = @(Read-TermLinkProtobufFields -Bytes $primitiveField.value `
        -Description 'AAB compiled versionCode primitive')
    $integerField = Get-TermLinkSingleProtobufField -Fields $primitiveFields -Number 6 -WireType 0 `
        -Description 'AAB compiled decimal versionCode'
    if ([uint64]$integerField.value -ne [uint64]$versionCode) {
        throw 'AAB raw and compiled versionCode values disagree.'
    }

    return [pscustomobject][ordered]@{
        application_id = $applicationId
        version_name = $versionName
        version_code = [int]$versionCode
    }
}

function ConvertFrom-TermLinkJarAttributeFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][byte[]]$Bytes,
        [Parameter(Mandatory)][string]$Description
    )

    try { $text = [Text.UTF8Encoding]::new($false, $true).GetString($Bytes) }
    catch { throw "$Description is not valid UTF-8." }
    if ($text.IndexOf([char]0) -ge 0) { throw "$Description contains a NUL character." }
    $sections = [Collections.Generic.List[object]]::new()
    $current = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    $lastHeader = $null
    foreach ($line in @([regex]::Split($text, "\r\n|\n|\r"))) {
        if ($line.Length -eq 0) {
            if ($current.Count -gt 0) {
                $sections.Add($current)
                $current = [Collections.Generic.Dictionary[string,string]]::new(
                    [StringComparer]::OrdinalIgnoreCase)
            }
            $lastHeader = $null
            continue
        }
        if ($line[0] -eq ' ') {
            if ($null -eq $lastHeader) { throw "$Description contains an orphan continuation line." }
            $current[$lastHeader] = $current[$lastHeader] + $line.Substring(1)
            continue
        }
        $separatorIndex = $line.IndexOf(': ')
        if ($separatorIndex -le 0) { throw "$Description contains a malformed attribute line." }
        $header = $line.Substring(0, $separatorIndex)
        if ($header -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]*$') {
            throw "$Description contains an invalid attribute name."
        }
        if ($current.ContainsKey($header)) { throw "$Description contains a duplicate $header attribute." }
        $current.Add($header, $line.Substring($separatorIndex + 2))
        $lastHeader = $header
    }
    if ($current.Count -gt 0) { $sections.Add($current) }
    if ($sections.Count -eq 0) { throw "$Description contains no attribute sections." }
    return $sections.ToArray()
}

function ConvertFrom-TermLinkSha256Base64 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][string]$Description
    )

    try { $bytes = [Convert]::FromBase64String($Value) }
    catch { throw "$Description is not valid Base64." }
    if ($bytes.Length -ne 32) { throw "$Description is not a SHA-256 digest." }
    return ,$bytes
}

function Test-TermLinkFixedTimeEqual {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][byte[]]$Actual,
        [Parameter(Mandatory)][byte[]]$Expected
    )

    if ($Actual.Length -ne $Expected.Length) { return $false }
    [int]$difference = 0
    for ($index = 0; $index -lt $Actual.Length; $index++) {
        $difference = $difference -bor ($Actual[$index] -bxor $Expected[$index])
    }
    return $difference -eq 0
}

function Get-TermLinkAabVerificationEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedSha256
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "Android AAB not found: $resolvedPath"
    }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if ($null -eq ('System.Security.Cryptography.Pkcs.SignedCms' -as [type])) {
        try { Add-Type -AssemblyName System.Security.Cryptography.Pkcs -ErrorAction Stop }
        catch {
            try { Add-Type -AssemblyName System.Security -ErrorAction Stop }
            catch { throw 'The SignedCms implementation required for AAB signature inspection is unavailable.' }
        }
    }

    $archive = [IO.Compression.ZipFile]::OpenRead($resolvedPath)
    try {
        $entries = @($archive.Entries)
        if ($entries.Count -eq 0 -or $entries.Count -gt 65535) {
            throw 'AAB ZIP entry count is outside the allowed range.'
        }
        # ZIP paths are ordinarily case-sensitive, but JAR signature discovery is not
        # consistently case-sensitive across tooling. Reject case-colliding names so a
        # producer and verifier can never disagree about which security entry is active.
        $entryMap = [Collections.Generic.Dictionary[string,object]]::new(
            [StringComparer]::OrdinalIgnoreCase)
        [int64]$declaredTotal = 0
        foreach ($entry in $entries) {
            $name = [string]$entry.FullName
            if ([string]::IsNullOrWhiteSpace($name) -or $name.IndexOf([char]0) -ge 0 -or
                $name.Contains('\') -or $name.Contains(':') -or $name.StartsWith('/')) {
                throw "AAB ZIP contains an unsafe entry name: $name"
            }
            $trimmedName = $name.TrimEnd('/')
            $segments = @($trimmedName -split '/')
            if ([string]::IsNullOrWhiteSpace($trimmedName) -or
                @($segments | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0) {
                throw "AAB ZIP contains a traversal or empty path segment: $name"
            }
            if ($entryMap.ContainsKey($name)) { throw "AAB ZIP contains a duplicate entry: $name" }
            $entryMap.Add($name, $entry)
            if ([int64]$entry.Length -lt 0 -or [int64]$entry.Length -gt 1GB -or
                $declaredTotal -gt (2GB - [int64]$entry.Length)) {
                throw 'AAB ZIP exceeds the allowed expanded size.'
            }
            $declaredTotal += [int64]$entry.Length
            [int64]$externalAttributes = $entry.ExternalAttributes
            if ($externalAttributes -lt 0) { $externalAttributes += 4294967296 }
            if ((($externalAttributes -shr 16) -band 0xF000) -eq 0xA000) {
                throw "AAB ZIP contains a symbolic link: $name"
            }
        }

        $manifestEntryName = 'META-INF/MANIFEST.MF'
        $baseManifestName = 'base/manifest/AndroidManifest.xml'
        foreach ($requiredEntryName in @($manifestEntryName, $baseManifestName)) {
            if (-not $entryMap.ContainsKey($requiredEntryName)) {
                throw "AAB ZIP is missing required entry: $requiredEntryName"
            }
        }
        $signatureFileEntries = @($entries | Where-Object {
            $_.FullName -match '(?i)^META-INF/([^/]+)\.SF$'
        })
        $signatureBlockEntries = @($entries | Where-Object {
            $_.FullName -match '(?i)^META-INF/([^/]+)\.(RSA|DSA|EC)$'
        })
        $unsupportedSignatureEntries = @($entries | Where-Object {
            $_.FullName -match '(?i)^META-INF/SIG-[^/]+$'
        })
        if ($signatureFileEntries.Count -ne 1 -or $signatureBlockEntries.Count -ne 1 -or
            $unsupportedSignatureEntries.Count -ne 0) {
            throw 'AAB must contain exactly one supported JAR signature file and one signature block.'
        }
        $signatureBase = [IO.Path]::GetFileNameWithoutExtension($signatureFileEntries[0].Name)
        $blockBase = [IO.Path]::GetFileNameWithoutExtension($signatureBlockEntries[0].Name)
        if (-not [string]::Equals($signatureBase, $blockBase, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'AAB JAR signature file and signature block names do not match.'
        }

        $manifestBytes = Read-TermLinkBoundedZipEntryBytes -Entry $entryMap[$manifestEntryName] `
            -MaximumBytes 32MB -Description 'AAB JAR manifest'
        $signatureFileBytes = Read-TermLinkBoundedZipEntryBytes -Entry $signatureFileEntries[0] `
            -MaximumBytes 32MB -Description 'AAB JAR signature file'
        $signatureBlockBytes = Read-TermLinkBoundedZipEntryBytes -Entry $signatureBlockEntries[0] `
            -MaximumBytes 16MB -Description 'AAB JAR signature block'
        $baseManifestBytes = Read-TermLinkBoundedZipEntryBytes -Entry $entryMap[$baseManifestName] `
            -MaximumBytes 16MB -Description 'AAB protobuf base manifest'

        $jarSections = @(ConvertFrom-TermLinkJarAttributeFile -Bytes $manifestBytes `
            -Description 'AAB JAR manifest')
        $jarMain = $jarSections[0]
        if (-not $jarMain.ContainsKey('Manifest-Version') -or $jarMain['Manifest-Version'] -cne '1.0' -or
            $jarMain.ContainsKey('Name')) {
            throw 'AAB JAR manifest main section is invalid.'
        }
        $payloadSections = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
        for ($index = 1; $index -lt $jarSections.Count; $index++) {
            $section = $jarSections[$index]
            if (-not $section.ContainsKey('Name') -or -not $section.ContainsKey('SHA-256-Digest')) {
                throw 'AAB JAR manifest payload section is missing Name or SHA-256-Digest.'
            }
            $unsupportedDigests = @($section.Keys | Where-Object {
                $_ -match '(?i)-Digest$' -and $_ -ine 'SHA-256-Digest'
            })
            if ($unsupportedDigests.Count -gt 0) {
                throw 'AAB JAR manifest payload section contains a non-SHA-256 digest.'
            }
            $payloadName = [string]$section['Name']
            if ($payloadSections.ContainsKey($payloadName)) {
                throw "AAB JAR manifest contains a duplicate payload section: $payloadName"
            }
            $payloadSections.Add($payloadName, $section)
        }

        $signatureSections = @(ConvertFrom-TermLinkJarAttributeFile -Bytes $signatureFileBytes `
            -Description 'AAB JAR signature file')
        $signatureMain = $signatureSections[0]
        if (-not $signatureMain.ContainsKey('Signature-Version') -or
            $signatureMain['Signature-Version'] -cne '1.0' -or
            -not $signatureMain.ContainsKey('SHA-256-Digest-Manifest')) {
            throw 'AAB JAR signature file main section is invalid.'
        }
        $unsupportedManifestDigests = @($signatureMain.Keys | Where-Object {
            $_ -match '(?i)-Digest-Manifest$' -and $_ -ine 'SHA-256-Digest-Manifest'
        })
        if ($unsupportedManifestDigests.Count -gt 0) {
            throw 'AAB JAR signature file contains a non-SHA-256 manifest digest.'
        }
        $expectedManifestDigest = ConvertFrom-TermLinkSha256Base64 `
            -Value $signatureMain['SHA-256-Digest-Manifest'] `
            -Description 'AAB signed JAR manifest digest'
        $sha256 = [Security.Cryptography.SHA256]::Create()
        try { $actualManifestDigest = $sha256.ComputeHash($manifestBytes) }
        finally { $sha256.Dispose() }
        if (-not (Test-TermLinkFixedTimeEqual -Actual $actualManifestDigest -Expected $expectedManifestDigest)) {
            throw 'AAB JAR signature file does not bind the exact JAR manifest.'
        }

        $unsignedArtifacts = @(
            $manifestEntryName,
            [string]$signatureFileEntries[0].FullName,
            [string]$signatureBlockEntries[0].FullName
        )
        $payloadEntries = @($entries | Where-Object {
            -not [string]::IsNullOrEmpty($_.Name) -and $_.FullName -notin $unsignedArtifacts
        })
        if ($payloadSections.Count -ne $payloadEntries.Count) {
            throw 'AAB JAR manifest coverage count does not match the payload entry count.'
        }
        foreach ($payloadEntry in $payloadEntries) {
            $payloadName = [string]$payloadEntry.FullName
            if (-not $payloadSections.ContainsKey($payloadName)) {
                throw "AAB payload entry is not covered by the signed JAR manifest: $payloadName"
            }
            $expectedPayloadDigest = ConvertFrom-TermLinkSha256Base64 `
                -Value $payloadSections[$payloadName]['SHA-256-Digest'] `
                -Description "AAB payload digest for $payloadName"
            $payloadStream = $payloadEntry.Open()
            $sha256 = [Security.Cryptography.SHA256]::Create()
            try { $actualPayloadDigest = $sha256.ComputeHash($payloadStream) }
            finally {
                $sha256.Dispose()
                $payloadStream.Dispose()
            }
            if (-not (Test-TermLinkFixedTimeEqual -Actual $actualPayloadDigest -Expected $expectedPayloadDigest)) {
                throw "AAB payload digest does not match the signed JAR manifest: $payloadName"
            }
        }

        $contentInfo = [Security.Cryptography.Pkcs.ContentInfo]::new($signatureFileBytes)
        $signedCms = [Security.Cryptography.Pkcs.SignedCms]::new($contentInfo, $true)
        try { $signedCms.Decode($signatureBlockBytes) }
        catch { throw 'AAB contains an invalid JAR PKCS#7 signature block.' }
        if ($signedCms.SignerInfos.Count -ne 1) {
            throw "AAB JAR signature block must contain exactly one signer; found $($signedCms.SignerInfos.Count)."
        }
        try { $signedCms.CheckSignature($true) }
        catch { throw 'AAB JAR PKCS#7 signature did not validate against its signature file.' }
        $signerInfo = $signedCms.SignerInfos[0]
        if ($null -eq $signerInfo.Certificate) { throw 'AAB JAR signer certificate is missing.' }
        if ([string]$signerInfo.DigestAlgorithm.Value -cne '2.16.840.1.101.3.4.2.1') {
            throw 'AAB JAR signer does not use SHA-256 as its CMS digest algorithm.'
        }
        $now = [DateTime]::UtcNow
        if ($now -lt $signerInfo.Certificate.NotBefore.ToUniversalTime() -or
            $now -gt $signerInfo.Certificate.NotAfter.ToUniversalTime()) {
            throw 'AAB JAR signer certificate is not currently valid.'
        }
        $sha256 = [Security.Cryptography.SHA256]::Create()
        try { $signerHash = $sha256.ComputeHash($signerInfo.Certificate.RawData) }
        finally { $sha256.Dispose() }
        $actualSignerSha256 = ([BitConverter]::ToString($signerHash)).Replace('-', '').ToLowerInvariant()
        $signerSha256 = Assert-TermLinkFingerprintMatch -Actual $actualSignerSha256 `
            -Expected $ExpectedSha256 -Length 64 -Description 'Android AAB signer certificate SHA-256 fingerprint'
        $identity = Get-TermLinkAabManifestIdentity -Bytes $baseManifestBytes

        return [pscustomobject][ordered]@{
            application_id = $identity.application_id
            version_name = $identity.version_name
            version_code = $identity.version_code
            signer_sha256 = $signerSha256
            signer_subject = $signerInfo.Certificate.Subject
            signer_count = 1
            cms_digest_algorithm = 'SHA-256'
            signed_payload_entry_count = $payloadEntries.Count
            payload_sha256_coverage = $true
            jar_signature_valid = $true
        }
    }
    finally { $archive.Dispose() }
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
    'Get-TermLinkAabVerificationEvidence',
    'Assert-TermLinkWindowsSignatureEvidence',
    'Assert-TermLinkWindowsVersionEvidence',
    'Assert-TermLinkRfc3161TimestampEvidence',
    'Get-TermLinkPeAuthenticodeEvidence'
)
