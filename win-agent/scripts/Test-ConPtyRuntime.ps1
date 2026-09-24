[CmdletBinding()]
param([Parameter(Mandatory)][string]$Directory)

$ErrorActionPreference = 'Stop'
# Microsoft.Windows.Console.ConPTY 1.24.260710001. Preserve Microsoft's original
# signatures; these hashes also reject accidentally re-signed vendor binaries.
$expected = [ordered]@{
    'conpty.dll' = '39fba2713e2495117b1591ae8c32a3b904bea7aa66069cf7815e2844c76d75d8'
    'x64/OpenConsole.exe' = 'b7fd936c2668b87b9ecf7b3366dc6568afc1c6f981874cba3e955a1c35cf8160'
    'arm64/OpenConsole.exe' = 'ed7622fd0d3bedc9ab9f122f5e58edf0def9e7999224f52dd395ba9f54edbe09'
}
foreach ($entry in $expected.GetEnumerator()) {
    $path = Join-Path $Directory $entry.Key
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Bundled ConPTY runtime is missing: $($entry.Key)"
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $entry.Value) {
        throw "Bundled ConPTY runtime hash mismatch: $($entry.Key)"
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notmatch '(?:^|, )O=Microsoft Corporation(?:,|$)') {
        throw "Bundled ConPTY runtime has no valid Microsoft signature: $($entry.Key)"
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $Directory 'ConPTY-NOTICE.txt') -PathType Leaf)) {
    throw 'Bundled ConPTY runtime is missing its license notice.'
}
