Set-StrictMode -Version Latest

function Get-TermLinkSdkPolicy {
    param([Parameter(Mandatory)][string]$ProjectRoot)

    $globalJsonPath = Join-Path $ProjectRoot 'global.json'
    if (-not (Test-Path -LiteralPath $globalJsonPath -PathType Leaf)) {
        throw ".NET SDK selection failed. global.json was not found: $globalJsonPath"
    }

    try {
        $globalJson = Get-Content -Raw -LiteralPath $globalJsonPath | ConvertFrom-Json
    }
    catch {
        throw ".NET SDK selection failed. global.json could not be read: $($_.Exception.Message)"
    }

    $versionText = [string]$globalJson.sdk.version
    $rollForward = [string]$globalJson.sdk.rollForward
    $version = $null
    if (-not [Version]::TryParse($versionText, [ref]$version)) {
        throw ".NET SDK selection failed. global.json contains an invalid SDK version: '$versionText'."
    }
    if ($rollForward -cne 'latestPatch') {
        throw ".NET SDK selection failed. TermLink requires global.json rollForward 'latestPatch', found '$rollForward'."
    }

    [pscustomobject]@{
        Version = $version
        VersionText = $versionText
        RollForward = $rollForward
        FeatureBand = [Math]::Floor($version.Build / 100) * 100
    }
}

function Get-TermLinkDotNetSdkValidationError {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ProjectRoot,
        [Parameter(Mandatory)]$Policy
    )

    if ($Path -ne 'dotnet' -and -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return "executable not found: $Path"
    }

    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        try {
            $listOutput = @(& $Path --list-sdks 2>&1)
            $listExitCode = $LASTEXITCODE
        }
        catch {
            return "could not execute '$Path --list-sdks': $($_.Exception.Message)"
        }
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($listExitCode -ne 0) {
        $detail = (($listOutput | ForEach-Object { [string]$_ }) -join ' | ').Trim()
        return "'$Path --list-sdks' failed with exit code $listExitCode$(if ($detail) { ": $detail" })"
    }

    $listedVersions = @($listOutput | ForEach-Object {
        $match = [regex]::Match([string]$_, '^\s*(\d+\.\d+\.\d+)\s+\[')
        if ($match.Success) { $match.Groups[1].Value }
    })
    if ($listedVersions.Count -eq 0) {
        return "'$Path --list-sdks' did not list any SDKs"
    }

    Push-Location -LiteralPath $ProjectRoot
    try {
        $previousPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            try {
                $versionOutput = @(& $Path --version 2>&1)
                $versionExitCode = $LASTEXITCODE
            }
            catch {
                return "could not execute '$Path --version' from '$ProjectRoot': $($_.Exception.Message)"
            }
        }
        finally {
            $ErrorActionPreference = $previousPreference
        }
    }
    finally {
        Pop-Location
    }
    if ($versionExitCode -ne 0) {
        $detail = (($versionOutput | ForEach-Object { [string]$_ }) -join ' | ').Trim()
        return "'$Path --version' could not select SDK $($Policy.VersionText) ($($Policy.RollForward)) from '$ProjectRoot' (exit code $versionExitCode)$(if ($detail) { ": $detail" })"
    }

    $selectedText = @($versionOutput | ForEach-Object {
        $match = [regex]::Match([string]$_, '^\s*(\d+\.\d+\.\d+)\s*$')
        if ($match.Success) { $match.Groups[1].Value }
    } | Select-Object -First 1)
    if ($selectedText.Count -ne 1) {
        return "'$Path --version' did not report a valid SDK version"
    }
    $selectedText = [string]$selectedText[0]
    if ($listedVersions -cnotcontains $selectedText) {
        return "selected SDK $selectedText was not reported by '$Path --list-sdks'"
    }

    $selectedVersion = [Version]$selectedText
    $selectedFeatureBand = [Math]::Floor($selectedVersion.Build / 100) * 100
    if ($selectedVersion.Major -ne $Policy.Version.Major -or
        $selectedVersion.Minor -ne $Policy.Version.Minor -or
        $selectedFeatureBand -ne $Policy.FeatureBand -or
        $selectedVersion -lt $Policy.Version) {
        return "selected SDK $selectedText is incompatible with global.json requirement $($Policy.VersionText) ($($Policy.RollForward))"
    }

    return $null
}

function Resolve-TermLinkDotNetSdk {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProjectRoot,
        [AllowEmptyString()][string]$DotNetPath,
        [switch]$DotNetPathWasExplicit,
        [string]$ManifestPath = 'D:\Dev\DEV_ENV_MANIFEST.json',
        [string]$EnsureScriptPath = 'D:\Dev\Scripts\Ensure-DevEnv.ps1'
    )

    $resolvedProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
    $policy = Get-TermLinkSdkPolicy -ProjectRoot $resolvedProjectRoot
    $requirement = "$($policy.VersionText) ($($policy.RollForward))"

    if ($DotNetPathWasExplicit) {
        if ([string]::IsNullOrWhiteSpace($DotNetPath)) {
            throw ".NET SDK selection failed. Explicit -DotNetPath cannot be empty. Required: $requirement."
        }
        $explicitError = Get-TermLinkDotNetSdkValidationError `
            -Path $DotNetPath -ProjectRoot $resolvedProjectRoot -Policy $policy
        if ($explicitError) {
            throw ".NET SDK selection failed. Explicit -DotNetPath '$DotNetPath' is invalid. Required: $requirement. $explicitError"
        }
        return $DotNetPath
    }

    if (Test-Path -LiteralPath $ManifestPath -PathType Leaf) {
        if (-not (Test-Path -LiteralPath $EnsureScriptPath -PathType Leaf)) {
            throw ".NET SDK selection failed. The D:\Dev manifest is present, but its required inspection script is missing: $EnsureScriptPath"
        }

        try {
            $manifest = Get-Content -Raw -LiteralPath $ManifestPath | ConvertFrom-Json
        }
        catch {
            throw ".NET SDK selection failed. The D:\Dev manifest could not be read: $($_.Exception.Message)"
        }
        $dotNetEntries = @($manifest.tools | Where-Object { [string]$_.id -ceq 'dotnet' })
        if ($dotNetEntries.Count -ne 1) {
            throw ".NET SDK selection failed. The D:\Dev manifest must contain exactly one 'dotnet' tool entry."
        }
        $manifestRoot = [IO.Path]::GetFullPath([string]$manifest.roots.root).TrimEnd('\')
        $manifestRootPrefix = $manifestRoot + '\'
        $declaredProbePaths = @($dotNetEntries[0].probes | ForEach-Object {
            $probePath = [IO.Path]::GetFullPath((Join-Path $manifestRoot ([string]$_.relativePath)))
            if (-not $probePath.StartsWith($manifestRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw ".NET SDK selection failed. The D:\Dev dotnet probe escaped its declared root: $($_.relativePath)"
            }
            $probePath
        })
        if ($declaredProbePaths.Count -eq 0) {
            throw ".NET SDK selection failed. The D:\Dev dotnet manifest entry has no executable probes."
        }
        $manifestVersion = $null
        $manifestVersionText = [string]$dotNetEntries[0].version
        if (-not [Version]::TryParse($manifestVersionText, [ref]$manifestVersion)) {
            throw ".NET SDK selection failed. The D:\Dev dotnet manifest entry has an invalid version: '$manifestVersionText'."
        }
        $manifestFeatureBand = [Math]::Floor($manifestVersion.Build / 100) * 100
        if ($manifestVersion.Major -ne $policy.Version.Major -or
            $manifestVersion.Minor -ne $policy.Version.Minor -or
            $manifestFeatureBand -ne $policy.FeatureBand -or
            $manifestVersion -lt $policy.Version) {
            throw ".NET SDK selection failed. D:\Dev declares SDK $manifestVersionText, which is incompatible with global.json requirement $requirement."
        }

        $previousPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            try {
                $inspectionOutput = @(& $EnsureScriptPath `
                    -Tool 'dotnet' -ManifestPath $ManifestPath -Json 2>&1)
                $inspectionExitCode = $LASTEXITCODE
            }
            catch {
                throw ".NET SDK selection failed. Ensure-DevEnv.ps1 could not inspect dotnet: $($_.Exception.Message)"
            }
        }
        finally {
            $ErrorActionPreference = $previousPreference
        }
        if ($inspectionExitCode -ne 0) {
            $detail = (($inspectionOutput | ForEach-Object { [string]$_ }) -join ' | ').Trim()
            throw ".NET SDK selection failed. Ensure-DevEnv.ps1 reported that dotnet is unavailable (exit code $inspectionExitCode)$(if ($detail) { ": $detail" })."
        }
        try {
            $inspection = @((($inspectionOutput | ForEach-Object { [string]$_ }) -join "`n") | ConvertFrom-Json)
        }
        catch {
            throw ".NET SDK selection failed. Ensure-DevEnv.ps1 returned invalid JSON: $($_.Exception.Message)"
        }
        $installed = @($inspection | Where-Object {
            [string]$_.Id -ceq 'dotnet' -and [string]$_.Status -ceq 'installed'
        })
        if ($installed.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$installed[0].Path)) {
            throw ".NET SDK selection failed. Ensure-DevEnv.ps1 did not report exactly one installed dotnet SDK."
        }

        $candidate = [string]$installed[0].Path
        $resolvedCandidate = [IO.Path]::GetFullPath($candidate)
        if (@($declaredProbePaths | Where-Object {
            [string]::Equals($_, $resolvedCandidate, [StringComparison]::OrdinalIgnoreCase)
        }).Count -ne 1) {
            throw ".NET SDK selection failed. Ensure-DevEnv.ps1 selected a dotnet path not declared by the manifest: $candidate"
        }
        $candidateError = Get-TermLinkDotNetSdkValidationError `
            -Path $candidate -ProjectRoot $resolvedProjectRoot -Policy $policy
        if ($candidateError) {
            throw ".NET SDK selection failed. D:\Dev manifest candidate '$candidate' is invalid. Required: $requirement. $candidateError"
        }
        return $candidate
    }

    $pathCandidate = 'dotnet'
    $pathError = Get-TermLinkDotNetSdkValidationError `
        -Path $pathCandidate -ProjectRoot $resolvedProjectRoot -Policy $policy
    if ($pathError) {
        throw ".NET SDK selection failed. D:\Dev is unavailable and the PATH fallback is invalid. Required: $requirement. $pathError"
    }
    return $pathCandidate
}
