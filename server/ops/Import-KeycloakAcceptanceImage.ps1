[CmdletBinding()]
param(
    [ValidateSet('amd64', 'arm64')]
    [string]$Architecture = $(if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [Runtime.InteropServices.Architecture]::Arm64) { 'arm64' } else { 'amd64' })
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = 'keycloak/keycloak'
$registry = 'quay.io'
$version = '26.3.2'
$manifestDigest = if ($Architecture -eq 'arm64') {
    'sha256:1f41bee20289ba0ab09bee9245b8152954537c568218a8e52cd0b6a2f5083f2'
} else {
    'sha256:031845445213387a7b252d9b0898f401a1bbf7cd8b42400f014b9dfec0825991'
}
$localTag = "termlink-acceptance/keycloak:$version-$Architecture"

& docker image inspect $localTag *> $null
if ($LASTEXITCODE -eq 0) {
    Write-Output $localTag
    return
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$staging = [IO.Path]::GetFullPath((Join-Path $tempRoot ("TermLink-Keycloak-OCI-$Architecture-" + $manifestDigest.Substring(7, 12))))
$cachedStaging = Get-ChildItem -LiteralPath $tempRoot -Directory -Filter 'TermLink-Keycloak-OCI-*' -ErrorAction SilentlyContinue |
    Where-Object {
        Test-Path -LiteralPath (Join-Path $_.FullName ('blobs\sha256\' + $manifestDigest.Substring(7))) -PathType Leaf
    } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if ($null -ne $cachedStaging) {
    $staging = [IO.Path]::GetFullPath($cachedStaging.FullName)
}
if (-not $staging.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The OCI staging path escaped the temporary directory.'
}
$archive = $staging + '.tar'
$client = [Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromMinutes(30)
$completed = $false

function Get-AuthenticatedResponse {
    param([string]$Uri, [string]$Token, [string]$Accept)
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $Uri)
    if (-not [string]::IsNullOrWhiteSpace($Token)) {
        $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $Token)
    }
    if (-not [string]::IsNullOrWhiteSpace($Accept)) {
        $request.Headers.Accept.ParseAdd($Accept)
    }
    $response = $client.Send($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead)
    $request.Dispose()
    if (-not $response.IsSuccessStatusCode) {
        $status = [int]$response.StatusCode
        $response.Dispose()
        throw "OCI registry returned HTTP $status for an authenticated request."
    }
    return $response
}

function Assert-FileDigest {
    param([string]$Path, [string]$ExpectedDigest, [Int64]$ExpectedSize)
    $item = Get-Item -LiteralPath $Path
    if ($ExpectedSize -ge 0 -and $item.Length -ne $ExpectedSize) {
        throw "OCI blob size mismatch for $ExpectedDigest."
    }
    $actual = 'sha256:' + (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $ExpectedDigest) {
        throw "OCI blob digest mismatch for $ExpectedDigest."
    }
}

try {
    New-Item -ItemType Directory -Path (Join-Path $staging 'blobs\sha256') -Force | Out-Null
    $tokenUri = "https://$registry/v2/auth?service=$registry&scope=repository:$repository`:pull"
    $tokenResponse = $client.GetAsync($tokenUri).GetAwaiter().GetResult()
    if (-not $tokenResponse.IsSuccessStatusCode) { throw 'Could not authenticate to the Keycloak OCI registry.' }
    try {
        $tokenJson = $tokenResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        $token = [string]$tokenJson.token
        if ([string]::IsNullOrWhiteSpace($token)) { throw 'The OCI registry token response was invalid.' }
    }
    finally {
        $tokenResponse.Dispose()
    }

    $manifestUri = "https://$registry/v2/$repository/manifests/$manifestDigest"
    $manifestResponse = Get-AuthenticatedResponse $manifestUri $token 'application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json'
    try {
        $manifestBytes = $manifestResponse.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
    }
    finally {
        $manifestResponse.Dispose()
    }
    $manifestPath = Join-Path $staging ('blobs\sha256\' + $manifestDigest.Substring(7))
    [IO.File]::WriteAllBytes($manifestPath, $manifestBytes)
    [Security.Cryptography.CryptographicOperations]::ZeroMemory($manifestBytes)
    Assert-FileDigest $manifestPath $manifestDigest -1
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

    $blobs = @($manifest.config) + @($manifest.layers)
    foreach ($blob in $blobs) {
        $digest = [string]$blob.digest
        $size = [Int64]$blob.size
        if ($digest -notmatch '^sha256:[0-9a-f]{64}$' -or $size -lt 1 -or $size -gt 1GB) {
            throw 'The Keycloak OCI manifest contains an invalid blob descriptor.'
        }
        $blobPath = Join-Path $staging ('blobs\sha256\' + $digest.Substring(7))
        $verified = $false
        if (Test-Path -LiteralPath $blobPath -PathType Leaf) {
            try {
                Assert-FileDigest $blobPath $digest $size
                $verified = $true
            }
            catch {
                Remove-Item -LiteralPath $blobPath -Force
            }
        }
        if (-not $verified) {
            $partialPath = $blobPath + '.partial'
            if (Test-Path -LiteralPath $partialPath -PathType Leaf) {
                Remove-Item -LiteralPath $partialPath -Force
            }
            $blobResponse = Get-AuthenticatedResponse "https://$registry/v2/$repository/blobs/$digest" $token 'application/octet-stream'
            try {
                $source = $blobResponse.Content.ReadAsStream()
                $destination = [IO.File]::Create($partialPath)
                try { $source.CopyTo($destination) }
                finally { $destination.Dispose(); $source.Dispose() }
            }
            finally {
                $blobResponse.Dispose()
            }
            Assert-FileDigest $partialPath $digest $size
            Move-Item -LiteralPath $partialPath -Destination $blobPath
        }
        Assert-FileDigest $blobPath $digest $size
    }

    [IO.File]::WriteAllText(
        (Join-Path $staging 'oci-layout'),
        '{"imageLayoutVersion":"1.0.0"}',
        [Text.UTF8Encoding]::new($false))
    $index = [ordered]@{
        schemaVersion = 2
        manifests = @([ordered]@{
            mediaType = [string]$manifest.mediaType
            digest = $manifestDigest
            size = (Get-Item -LiteralPath $manifestPath).Length
            annotations = [ordered]@{ 'org.opencontainers.image.ref.name' = $localTag }
            platform = [ordered]@{ architecture = $Architecture; os = 'linux' }
        })
    } | ConvertTo-Json -Depth 10 -Compress
    [IO.File]::WriteAllText((Join-Path $staging 'index.json'), $index, [Text.UTF8Encoding]::new($false))
    $dockerManifest = ConvertTo-Json -InputObject @([ordered]@{
        Config = 'blobs/sha256/' + ([string]$manifest.config.digest).Substring(7)
        RepoTags = @($localTag)
        Layers = @($manifest.layers | ForEach-Object { 'blobs/sha256/' + ([string]$_.digest).Substring(7) })
    }) -Depth 10 -Compress
    [IO.File]::WriteAllText((Join-Path $staging 'manifest.json'), $dockerManifest, [Text.UTF8Encoding]::new($false))
    [System.Formats.Tar.TarFile]::CreateFromDirectory($staging, $archive, $false)

    $loadOutput = (& docker load --input $archive 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Docker could not load the verified Keycloak OCI archive: $loadOutput"
    }
    & docker image inspect $localTag *> $null
    if ($LASTEXITCODE -ne 0) { throw 'The verified Keycloak image was loaded without the expected local tag.' }
    $completed = $true
    Write-Output $localTag
}
finally {
    $client.Dispose()
    if (Test-Path -LiteralPath $archive -PathType Leaf) { Remove-Item -LiteralPath $archive -Force }
    if ($completed -and (Test-Path -LiteralPath $staging -PathType Container)) {
        $resolved = [IO.Path]::GetFullPath($staging)
        if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to remove an OCI staging path outside the temporary directory.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
