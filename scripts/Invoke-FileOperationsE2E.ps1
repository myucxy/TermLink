[CmdletBinding()]
param(
    [ValidateScript({
        $null -eq $_ -or ($_.IsAbsoluteUri -and $_.Scheme -eq 'https' -and
            [string]::IsNullOrEmpty($_.UserInfo) -and $_.AbsolutePath -eq '/' -and
            -not $_.Query -and -not $_.Fragment)
    })]
    [Uri]$AcceptanceOrigin,

    [ValidateScript({
        [string]::IsNullOrWhiteSpace($_) -or -not [IO.Path]::IsPathRooted($_) -or
            [IO.Path]::GetFullPath($_) -eq $_
    })]
    [string]$OidcIssuer,
    [string]$HistoryOrigin,
    [string]$PasswordFilePath = 'server\secrets\acceptance\acceptance-user-password',
    [string]$CaFilePath = 'server\secrets\acceptance\acceptance-root-ca.crt',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._@-]{0,127}$')]
    [string]$Username = 'acceptance-user',
    [Guid]$PcId = [Guid]::Empty,

    [ValidateRange(60, 900)]
    [int]$TimeoutSeconds = 360,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$ArtifactRunId = ('run-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' +
        [Guid]::NewGuid().ToString('N').Substring(0, 8)),
    [string]$TemporaryParentPath = [IO.Path]::GetTempPath(),
    [switch]$SkipTerminalOperations,

    # Used by the checked-in self-test. It creates and validates the controlled
    # fixture, exercises normal cleanup, and performs no network access.
    [switch]$FixtureOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:failureCode = $null
$script:failureStage = $null
$script:failureMessage = $null
$script:ownedTemporaryPath = $null
$script:ownedMarkerValue = $null

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\file-operations-e2e'))
$artifactDirectory = [IO.Path]::GetFullPath((Join-Path $artifactRoot $ArtifactRunId))
$evidencePath = Join-Path $artifactDirectory 'evidence.json'

function Stop-FileOperationsE2E {
    param(
        [Parameter(Mandatory)][ValidatePattern('^[A-Z0-9_]+$')][string]$Code,
        [Parameter(Mandatory)][ValidatePattern('^[a-z0-9_-]+$')][string]$Stage,
        [Parameter(Mandatory)][string]$PublicMessage
    )
    $script:failureCode = $Code
    $script:failureStage = $Stage
    $script:failureMessage = $PublicMessage
    throw [InvalidOperationException]::new("${Code}: $PublicMessage")
}

function Assert-FileOperationsE2E {
    param(
        [bool]$Condition,
        [Parameter(Mandatory)][string]$Code,
        [Parameter(Mandatory)][string]$Stage,
        [Parameter(Mandatory)][string]$Message
    )
    if (-not $Condition) {
        Stop-FileOperationsE2E -Code $Code -Stage $Stage -PublicMessage $Message
    }
}

function Test-StrictChildPath {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Candidate
    )
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    return [IO.Path]::GetFullPath($Candidate).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Resolve-RepositoryFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Label
    )
    $candidate = if ([IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $repositoryRoot $Path }
    $resolved = [IO.Path]::GetFullPath($candidate)
    Assert-FileOperationsE2E (Test-Path -LiteralPath $resolved -PathType Leaf) `
        'INPUT_FILE_UNAVAILABLE' 'preflight' "$Label is unavailable."
    return $resolved
}

function Remove-OwnedTemporaryTree {
    param(
        [Parameter(Mandatory)][string]$ParentPath,
        [Parameter(Mandatory)][string]$CandidatePath,
        [Parameter(Mandatory)][string]$ExpectedMarker
    )
    $resolvedParent = [IO.Path]::GetFullPath($ParentPath)
    $resolvedCandidate = [IO.Path]::GetFullPath($CandidatePath)
    if (-not (Test-StrictChildPath -Root $resolvedParent -Candidate $resolvedCandidate)) {
        throw [InvalidOperationException]::new('TEMP_CLEANUP_OUTSIDE_PARENT')
    }
    $leaf = [IO.Path]::GetFileName($resolvedCandidate.TrimEnd('\', '/'))
    if ($leaf -notmatch '^termlink-file-operations-e2e-[a-f0-9]{32}$') {
        throw [InvalidOperationException]::new('TEMP_CLEANUP_NAME_REJECTED')
    }
    $markerPath = Join-Path $resolvedCandidate '.termlink-file-operations-e2e-owner'
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw [InvalidOperationException]::new('TEMP_CLEANUP_MARKER_MISSING')
    }
    $actualMarker = [IO.File]::ReadAllText($markerPath, [Text.UTF8Encoding]::new($false))
    if ($actualMarker -cne $ExpectedMarker) {
        throw [InvalidOperationException]::new('TEMP_CLEANUP_MARKER_MISMATCH')
    }
    Remove-Item -LiteralPath $resolvedCandidate -Recurse -Force
    if (Test-Path -LiteralPath $resolvedCandidate) {
        throw [InvalidOperationException]::new('TEMP_CLEANUP_INCOMPLETE')
    }
}

function ConvertTo-Base64Url {
    param([Parameter(Mandatory)][byte[]]$Value)
    return [Convert]::ToBase64String($Value).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Get-FileSHA256Base64Url {
    param([Parameter(Mandatory)][string]$Path)
    $stream = [IO.File]::OpenRead($Path)
    try {
        $hash = [Security.Cryptography.SHA256]::HashData($stream)
        return ConvertTo-Base64Url $hash
    }
    finally {
        $stream.Dispose()
    }
}

function New-ControlledFixture {
    param([Parameter(Mandatory)][string]$Root)
    $fixtureRoot = Join-Path $Root 'fixture'
    $archiveRoot = Join-Path $fixtureRoot 'archive-tree'
    $nestedRoot = Join-Path $archiveRoot 'nested'
    [IO.Directory]::CreateDirectory($nestedRoot) | Out-Null

    $emptyFile = Join-Path $fixtureRoot 'empty.bin'
    $unicodeFile = Join-Path $fixtureRoot 'unicode-文件.txt'
    $archiveUnicodeFile = Join-Path $archiveRoot '目录.txt'
    $archiveChildFile = Join-Path $nestedRoot 'child.bin'
    $archiveEmptyFile = Join-Path $nestedRoot 'zero.dat'
    [IO.File]::WriteAllBytes($emptyFile, [byte[]]::new(0))
    [IO.File]::WriteAllBytes(
        $unicodeFile,
        [Text.UTF8Encoding]::new($false).GetBytes("TermLink 文件操作 E2E`r`n"))
    [IO.File]::WriteAllBytes(
        $archiveUnicodeFile,
        [Text.UTF8Encoding]::new($false).GetBytes("目录内容/TermLink`n"))
    [IO.File]::WriteAllBytes($archiveChildFile, [byte[]](0, 1, 2, 3, 127, 128, 254, 255))
    [IO.File]::WriteAllBytes($archiveEmptyFile, [byte[]]::new(0))

    # DirectoryListingService has a fixed 500-entry page. 502 deterministic
    # extra files plus the three named root entries force an exact two-page run.
    for ($index = 0; $index -lt 502; $index++) {
        $name = 'page-entry-{0:D4}.dat' -f $index
        [IO.File]::WriteAllBytes((Join-Path $fixtureRoot $name), [byte[]]::new(0))
    }

    return [pscustomobject][ordered]@{
        root = [IO.Path]::GetFullPath($fixtureRoot)
        empty_file = [IO.Path]::GetFullPath($emptyFile)
        unicode_file = [IO.Path]::GetFullPath($unicodeFile)
        archive_root = [IO.Path]::GetFullPath($archiveRoot)
        root_entry_count = 505
        empty_sha256 = Get-FileSHA256Base64Url $emptyFile
        unicode_sha256 = Get-FileSHA256Base64Url $unicodeFile
        archive_entry_hashes = [ordered]@{
            'archive-tree/目录.txt' = Get-FileSHA256Base64Url $archiveUnicodeFile
            'archive-tree/nested/child.bin' = Get-FileSHA256Base64Url $archiveChildFile
            'archive-tree/nested/zero.dat' = Get-FileSHA256Base64Url $archiveEmptyFile
        }
    }
}

function Get-CanonicalGoTool {
    $ensureDevEnv = 'D:\Dev\Scripts\Ensure-DevEnv.ps1'
    if (Test-Path -LiteralPath $ensureDevEnv -PathType Leaf) {
        $raw = @(& $ensureDevEnv -Tool go -Json 2>$null) -join "`n"
        Assert-FileOperationsE2E ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($raw)) `
            'GO_TOOL_UNAVAILABLE' 'preflight' 'The canonical Go toolchain is unavailable.'
        try { $metadata = $raw | ConvertFrom-Json -ErrorAction Stop }
        catch {
            Stop-FileOperationsE2E 'GO_TOOL_INVALID' 'preflight' 'The canonical Go tool metadata is invalid.'
        }
        $path = [IO.Path]::GetFullPath([string]$metadata.Path)
        Assert-FileOperationsE2E ([string]$metadata.Status -eq 'installed' -and
            (Test-Path -LiteralPath $path -PathType Leaf)) `
            'GO_TOOL_UNAVAILABLE' 'preflight' 'The canonical Go toolchain is unavailable.'
        return $path
    }
    $command = Get-Command go -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    Assert-FileOperationsE2E ($null -ne $command) 'GO_TOOL_UNAVAILABLE' 'preflight' `
        'The Go toolchain is unavailable.'
    return [string]$command.Source
}

function Invoke-GoProtocolHarness {
    param(
        [Parameter(Mandatory)][string]$GoPath,
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][int]$ProcessTimeoutSeconds
    )
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $GoPath
    $start.WorkingDirectory = $repositoryRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    $start.Environment['GOWORK'] = 'off'
    $start.ArgumentList.Add('run')
    $start.ArgumentList.Add($SourcePath)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) {
        return [pscustomobject]@{ ExitCode = -1; TimedOut = $false }
    }
    try {
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($ProcessTimeoutSeconds * 1000)) {
            try { $process.Kill($true) } catch {}
            $process.WaitForExit(5000) | Out-Null
            $null = $stdoutTask.GetAwaiter().GetResult()
            $null = $stderrTask.GetAwaiter().GetResult()
            return [pscustomobject]@{ ExitCode = -2; TimedOut = $true }
        }
        # Output is deliberately consumed but never surfaced: the public result
        # contract is the bounded JSON result file, not compiler/HTTP diagnostics.
        $null = $stdoutTask.GetAwaiter().GetResult()
        $null = $stderrTask.GetAwaiter().GetResult()
        return [pscustomobject]@{ ExitCode = $process.ExitCode; TimedOut = $false }
    }
    finally {
        $process.Dispose()
    }
}

function Stop-OwnedTerminalSessions {
    param([Parameter(Mandatory)][string]$CleanupPlanPath)
    $empty = [pscustomobject][ordered]@{
        requested_session_ids = @()
        stopped_session_ids = @()
        unmatched_session_ids = @()
    }
    if (-not (Test-Path -LiteralPath $CleanupPlanPath -PathType Leaf)) { return $empty }
    try { $plan = Get-Content -Raw -LiteralPath $CleanupPlanPath | ConvertFrom-Json -ErrorAction Stop }
    catch { return $empty }
    $sessionIds = @($plan.session_ids | ForEach-Object {
        $parsed = [Guid]::Empty
        if ([Guid]::TryParse([string]$_, [ref]$parsed) -and $parsed -ne [Guid]::Empty) {
            $parsed.ToString('D')
        }
    } | Select-Object -Unique)
    if ($sessionIds.Count -eq 0) { return $empty }

    $stopped = [Collections.Generic.List[string]]::new()
    $unmatched = [Collections.Generic.List[string]]::new()
    try {
        $processes = @(Get-CimInstance Win32_Process -Filter "Name = 'TermLink.SessionHost.exe'" -ErrorAction Stop)
    }
    catch {
        return [pscustomobject][ordered]@{
            requested_session_ids = $sessionIds
            stopped_session_ids = @()
            unmatched_session_ids = $sessionIds
        }
    }
    foreach ($sessionId in $sessionIds) {
        $matches = @($processes | Where-Object {
            $executable = [string]$_.ExecutablePath
            $commandLine = [string]$_.CommandLine
            $pattern = '(?i)(?:^|\s)--managed-session-id(?:\s+|=)"?' +
                [regex]::Escape($sessionId) + '"?(?:\s|$)'
            -not [string]::IsNullOrWhiteSpace($executable) -and
                [IO.Path]::GetFileName($executable) -ceq 'TermLink.SessionHost.exe' -and
                $commandLine -match $pattern
        })
        if ($matches.Count -ne 1) {
            $unmatched.Add($sessionId)
            continue
        }
        $process = $matches[0]
        try {
            $processId = [int]$process.ProcessId
            Stop-Process -Id $processId -Force -ErrorAction Stop
            Wait-Process -Id $processId -Timeout 5 -ErrorAction SilentlyContinue
            if ($null -eq (Get-Process -Id $processId -ErrorAction SilentlyContinue)) {
                $stopped.Add($sessionId)
            }
            else {
                $unmatched.Add($sessionId)
            }
        }
        catch {
            if ($null -eq (Get-Process -Id ([int]$process.ProcessId) -ErrorAction SilentlyContinue)) {
                $stopped.Add($sessionId)
            }
            else {
                $unmatched.Add($sessionId)
            }
        }
    }
    return [pscustomobject][ordered]@{
        requested_session_ids = $sessionIds
        stopped_session_ids = @($stopped)
        unmatched_session_ids = @($unmatched)
    }
}

$goSource = @'
package main

import (
    "archive/zip"
    "bytes"
    "context"
    "crypto/ed25519"
    "crypto/rand"
    "crypto/sha256"
    "crypto/tls"
    "crypto/x509"
    "encoding/base64"
    "encoding/hex"
    "encoding/json"
    "errors"
    "flag"
    "fmt"
    "io"
    "net/http"
    "net/url"
    "os"
    "path/filepath"
    "regexp"
    "sort"
    "strconv"
    "strings"
    "time"
)

const (
    deviceProofDomain = "TERMLINK-DEVICE-PROOF-V1"
    registerDomain = "TERMLINK-DEVICE-REGISTER-V1"
    maximumJSONBytes = 4 << 20
    maximumDownloadBytes = 32 << 20
)

type config struct {
    root string
    issuer string
    historyOrigin string
    username string
    passwordFile string
    caFile string
    pcID string
    fixtureRoot string
    emptyFile string
    unicodeFile string
    archiveRoot string
    resultFile string
    cleanupFile string
    timeout time.Duration
    skipTerminals bool
}

type identity struct {
    id string
    public ed25519.PublicKey
    private ed25519.PrivateKey
    exchange []byte
    token string
}

type stageError struct {
    Stage string
    Code string
    Message string
}

func (e *stageError) Error() string { return e.Code }

func failed(stage, code, message string) error {
    return &stageError{Stage: stage, Code: code, Message: message}
}

type resultDocument struct {
    SchemaVersion int `json:"schema_version"`
    Status string `json:"status"`
    PCID string `json:"pc_id,omitempty"`
    Checks map[string]any `json:"checks,omitempty"`
    Failure *failureDocument `json:"failure,omitempty"`
    AndroidUI uiDocument `json:"android_ui"`
}

type failureDocument struct {
    Stage string `json:"stage"`
    Code string `json:"code"`
    Message string `json:"message"`
}

type uiDocument struct {
    Executed bool `json:"executed"`
    Scope string `json:"scope"`
}

type fileOperation struct {
    ID string `json:"id"`
    PCID string `json:"pc_id"`
    Operation string `json:"operation"`
    Path string `json:"path"`
    Offset *int64 `json:"offset"`
    ShellType string `json:"shell_type"`
    Status string `json:"status"`
    Result json.RawMessage `json:"result"`
    Error string `json:"error"`
    CancelledAt string `json:"cancelled_at"`
}

type operationEnvelope struct {
    FileOperation fileOperation `json:"file_operation"`
    Download *downloadDescriptor `json:"download"`
}

type downloadDescriptor struct {
    Filename string `json:"filename"`
    SizeBytes int64 `json:"size_bytes"`
    SHA256 string `json:"sha256"`
    DirectoryZIP bool `json:"directory_zip"`
    Request presignedRequest `json:"request"`
}

type presignedRequest struct {
    URL string `json:"url"`
    Method string `json:"method"`
    Headers map[string][]string `json:"headers"`
}

type directoryListing struct {
    CurrentPath string `json:"current_path"`
    ParentPath *string `json:"parent_path"`
    Entries []directoryEntry `json:"entries"`
    NextOffset *int64 `json:"next_offset"`
}

type directoryEntry struct {
    Name string `json:"name"`
    Path string `json:"path"`
    Kind string `json:"kind"`
    SizeBytes *int64 `json:"size_bytes"`
}

type terminalResult struct {
    SessionID string `json:"session_id"`
    ShellType string `json:"shell_type"`
    CWD string `json:"cwd"`
}

type pcList struct {
    Items []struct {
        ID string `json:"id"`
        Online bool `json:"online"`
        RemoteRoutingEnabled bool `json:"remote_routing_enabled"`
    } `json:"items"`
}

type sessionList struct {
    Items []struct {
        ID string `json:"id"`
        PCID string `json:"pc_id"`
    } `json:"items"`
}

type runner struct {
    cfg config
    client *http.Client
    device *identity
    checks map[string]any
    cleanupSessions []string
}

var canonicalUUID = regexp.MustCompile(`^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$`)
var stableErrorCode = regexp.MustCompile(`^[A-Z0-9_]{1,64}$`)

func main() {
    cfg := parseFlags()
    result := resultDocument{
        SchemaVersion: 1,
        Status: "failed",
        AndroidUI: uiDocument{Executed: false, Scope: "protocol_e2e_only"},
    }
    run, err := newRunner(cfg)
    if err == nil {
        result.PCID, err = run.execute()
        result.Checks = run.checks
        run.revokeSelf()
        run.destroy()
    }
    if err == nil {
        result.Status = "passed"
    } else {
        public := &stageError{Stage: "protocol", Code: "PROTOCOL_E2E_FAILED", Message: "The protocol E2E did not complete."}
        if errors.As(err, &public) {
            result.Failure = &failureDocument{Stage: public.Stage, Code: public.Code, Message: public.Message}
        } else {
            result.Failure = &failureDocument{Stage: public.Stage, Code: public.Code, Message: public.Message}
        }
    }
    if writeErr := writeJSONFile(cfg.resultFile, result); writeErr != nil {
        os.Exit(2)
    }
    if err != nil { os.Exit(1) }
}

func parseFlags() config {
    var cfg config
    var timeoutSeconds int
    flag.StringVar(&cfg.root, "root", "", "")
    flag.StringVar(&cfg.issuer, "issuer", "", "")
    flag.StringVar(&cfg.historyOrigin, "history-origin", "", "")
    flag.StringVar(&cfg.username, "username", "", "")
    flag.StringVar(&cfg.passwordFile, "password-file", "", "")
    flag.StringVar(&cfg.caFile, "ca-file", "", "")
    flag.StringVar(&cfg.pcID, "pc-id", "", "")
    flag.StringVar(&cfg.fixtureRoot, "fixture-root", "", "")
    flag.StringVar(&cfg.emptyFile, "empty-file", "", "")
    flag.StringVar(&cfg.unicodeFile, "unicode-file", "", "")
    flag.StringVar(&cfg.archiveRoot, "archive-root", "", "")
    flag.StringVar(&cfg.resultFile, "result-file", "", "")
    flag.StringVar(&cfg.cleanupFile, "cleanup-file", "", "")
    flag.IntVar(&timeoutSeconds, "timeout-seconds", 300, "")
    flag.BoolVar(&cfg.skipTerminals, "skip-terminals", false, "")
    flag.Parse()
    cfg.root = strings.TrimRight(cfg.root, "/")
    cfg.issuer = strings.TrimRight(cfg.issuer, "/")
    cfg.historyOrigin = strings.TrimRight(cfg.historyOrigin, "/")
    cfg.timeout = time.Duration(timeoutSeconds) * time.Second
    return cfg
}

func newRunner(cfg config) (*runner, error) {
    if cfg.root == "" || cfg.issuer == "" || cfg.historyOrigin == "" || cfg.username == "" ||
        cfg.passwordFile == "" || cfg.caFile == "" || cfg.fixtureRoot == "" ||
        cfg.emptyFile == "" || cfg.unicodeFile == "" || cfg.archiveRoot == "" ||
        cfg.resultFile == "" || cfg.cleanupFile == "" || cfg.timeout < time.Minute {
        return nil, failed("preflight", "INPUT_INVALID", "The protocol runner input is incomplete.")
    }
    rootURL, rootErr := url.Parse(cfg.root)
    issuerURL, issuerErr := url.Parse(cfg.issuer)
    historyURL, historyErr := url.Parse(cfg.historyOrigin)
    if rootErr != nil || issuerErr != nil || historyErr != nil ||
        rootURL.Scheme != "https" || issuerURL.Scheme != "https" || historyURL.Scheme != "https" ||
        rootURL.User != nil || issuerURL.User != nil || historyURL.User != nil ||
        rootURL.RawQuery != "" || issuerURL.RawQuery != "" || historyURL.RawQuery != "" ||
        rootURL.Fragment != "" || issuerURL.Fragment != "" || historyURL.Fragment != "" ||
        rootURL.Path != "" || historyURL.Path != "" || rootURL.Host == "" || historyURL.Host == "" ||
        !strings.EqualFold(rootURL.Host, issuerURL.Host) {
        return nil, failed("preflight", "ORIGIN_POLICY_FAILED", "The HTTPS origin policy was not satisfied.")
    }
    caPEM, err := os.ReadFile(cfg.caFile)
    if err != nil { return nil, failed("preflight", "CA_UNAVAILABLE", "The acceptance CA is unavailable.") }
    roots, systemRootsErr := x509.SystemCertPool()
    if systemRootsErr != nil || roots == nil { roots = x509.NewCertPool() }
    if !roots.AppendCertsFromPEM(caPEM) {
        clear(caPEM)
        return nil, failed("preflight", "CA_INVALID", "The acceptance CA is invalid.")
    }
    clear(caPEM)
    transport := &http.Transport{TLSClientConfig: &tls.Config{MinVersion: tls.VersionTLS12, RootCAs: roots}}
    client := &http.Client{
        Transport: transport,
        Timeout: 45 * time.Second,
        CheckRedirect: func(*http.Request, []*http.Request) error { return errors.New("redirect disabled") },
    }
    device, err := newIdentity()
    if err != nil { return nil, failed("preflight", "IDENTITY_CREATE_FAILED", "The temporary Android identity could not be created.") }
    return &runner{cfg: cfg, client: client, device: device, checks: map[string]any{}}, nil
}

func newIdentity() (*identity, error) {
    public, private, err := ed25519.GenerateKey(rand.Reader)
    if err != nil { return nil, err }
    exchange := make([]byte, 32)
    if _, err := rand.Read(exchange); err != nil { clear(private); return nil, err }
    id, err := newUUID()
    if err != nil { clear(private); clear(exchange); return nil, err }
    return &identity{id: id, public: public, private: private, exchange: exchange}, nil
}

func newUUID() (string, error) {
    value := make([]byte, 16)
    if _, err := rand.Read(value); err != nil { return "", err }
    value[6] = (value[6] & 0x0f) | 0x40
    value[8] = (value[8] & 0x3f) | 0x80
    return fmt.Sprintf("%08x-%04x-%04x-%04x-%012x",
        value[0:4], value[4:6], value[6:8], value[8:10], value[10:16]), nil
}

func (r *runner) execute() (string, error) {
    password, err := os.ReadFile(r.cfg.passwordFile)
    if err != nil { return "", failed("oidc", "PASSWORD_FILE_UNAVAILABLE", "The acceptance password file is unavailable.") }
    password = bytes.TrimSpace(password)
    if len(password) < 16 {
        clear(password)
        return "", failed("oidc", "PASSWORD_FILE_INVALID", "The acceptance password file is invalid.")
    }
    token, tokenErr := r.acquireToken(string(password))
    clear(password)
    if tokenErr != nil { return "", tokenErr }
    r.device.token = token
    if err := r.register(); err != nil { return "", err }
    // Cleanup is also deferred here so a panic or an unforeseen return after
    // successful registration cannot skip temporary-device revocation.
    defer r.revokeSelf()
    pcID, err := r.selectPC()
    if err != nil { return "", err }
    r.cfg.pcID = pcID
    if err := r.verifyListing(); err != nil { return pcID, err }
    if err := r.verifyFileDownload("empty", r.cfg.emptyFile); err != nil { return pcID, err }
    if err := r.verifyFileDownload("unicode", r.cfg.unicodeFile); err != nil { return pcID, err }
    if err := r.verifyDirectoryDownload(); err != nil { return pcID, err }
    if !r.cfg.skipTerminals {
        if err := r.verifyTerminal("command-prompt"); err != nil { return pcID, err }
        if err := r.verifyTerminal("windows-powershell"); err != nil { return pcID, err }
    } else {
        r.checks["terminals"] = map[string]any{"executed": false, "reason": "operator_skipped"}
    }
    if err := r.verifyCancellation(); err != nil { return pcID, err }
    return pcID, nil
}

func (r *runner) acquireToken(password string) (string, error) {
    request, _ := http.NewRequest(http.MethodGet, r.cfg.issuer+"/.well-known/openid-configuration", nil)
    response, err := r.client.Do(request)
    if err != nil { return "", failed("oidc", "OIDC_DISCOVERY_FAILED", "OIDC discovery failed.") }
    raw, status, readErr := readBoundedResponse(response, 1<<20)
    if readErr != nil || status != http.StatusOK {
        clear(raw)
        return "", failed("oidc", "OIDC_DISCOVERY_FAILED", "OIDC discovery failed.")
    }
    var discovery struct { TokenEndpoint string `json:"token_endpoint"` }
    decodeErr := json.Unmarshal(raw, &discovery)
    clear(raw)
    tokenURL, parseErr := url.Parse(discovery.TokenEndpoint)
    issuerURL, _ := url.Parse(r.cfg.issuer)
    if decodeErr != nil || parseErr != nil || tokenURL.Scheme != "https" || tokenURL.User != nil ||
        tokenURL.RawQuery != "" || tokenURL.Fragment != "" || !strings.EqualFold(tokenURL.Host, issuerURL.Host) {
        return "", failed("oidc", "OIDC_DISCOVERY_INVALID", "OIDC discovery violated the endpoint policy.")
    }
    form := url.Values{
        "grant_type": {"password"}, "client_id": {"termlink-android"},
        "username": {r.cfg.username}, "password": {password}, "scope": {"openid"},
    }
    tokenRequest, _ := http.NewRequest(http.MethodPost, discovery.TokenEndpoint, strings.NewReader(form.Encode()))
    tokenRequest.Header.Set("Content-Type", "application/x-www-form-urlencoded")
    response, err = r.client.Do(tokenRequest)
    if err != nil { return "", failed("oidc", "OIDC_TOKEN_FAILED", "OIDC token acquisition failed.") }
    raw, status, readErr = readBoundedResponse(response, 1<<20)
    if readErr != nil || status != http.StatusOK {
        clear(raw)
        return "", failed("oidc", "OIDC_TOKEN_FAILED", "OIDC token acquisition failed.")
    }
    var value struct { AccessToken string `json:"access_token"` }
    decodeErr = json.Unmarshal(raw, &value)
    clear(raw)
    if decodeErr != nil || value.AccessToken == "" {
        return "", failed("oidc", "OIDC_TOKEN_INVALID", "OIDC returned an invalid token response.")
    }
    return value.AccessToken, nil
}

func (r *runner) register() error {
    timestamp := time.Now().UnixMilli()
    nonce, err := randomBase64(16)
    if err != nil { return failed("device-register", "DEVICE_REGISTER_FAILED", "The temporary Android device could not be registered.") }
    signing := base64.RawURLEncoding.EncodeToString(r.device.public)
    exchange := base64.RawURLEncoding.EncodeToString(r.device.exchange)
    name := "File operations E2E"
    keyID := "file-e2e-" + r.device.id
    canonical := strings.Join([]string{
        registerDomain, r.device.id, "ANDROID", name, signing, exchange, keyID,
        strconv.FormatInt(timestamp, 10), nonce,
    }, "\n")
    body, _ := json.Marshal(map[string]any{
        "id": r.device.id, "type": "ANDROID", "name": name,
        "signing_public_key": signing, "exchange_public_key": exchange, "key_id": keyID,
        "timestamp_ms": timestamp, "nonce": nonce,
        "signature": base64.RawURLEncoding.EncodeToString(ed25519.Sign(r.device.private, []byte(canonical))),
    })
    request, _ := http.NewRequest(http.MethodPost, r.cfg.root+"/v1/devices/register", bytes.NewReader(body))
    request.Header.Set("Authorization", "Bearer "+r.device.token)
    request.Header.Set("Content-Type", "application/json")
    response, err := r.client.Do(request)
    clear(body)
    if err != nil { return failed("device-register", "DEVICE_REGISTER_FAILED", "The temporary Android device could not be registered.") }
    raw, status, readErr := readBoundedResponse(response, 1<<20)
    clear(raw)
    if readErr != nil || status != http.StatusCreated {
        return failed("device-register", "DEVICE_REGISTER_FAILED", "The temporary Android device could not be registered.")
    }
    return nil
}

func (r *runner) selectPC() (string, error) {
    raw, err := r.signedRequest(http.MethodGet, "/v1/pcs", nil, "", http.StatusOK)
    if err != nil { return "", failed("pc-preflight", "PC_LIST_FAILED", "The account PC list could not be read.") }
    var value pcList
    if json.Unmarshal(raw, &value) != nil { clear(raw); return "", failed("pc-preflight", "PC_LIST_INVALID", "The account PC list is invalid.") }
    clear(raw)
    candidates := make([]string, 0)
    for _, item := range value.Items {
        if item.Online && item.RemoteRoutingEnabled && canonicalUUID.MatchString(item.ID) {
            candidates = append(candidates, item.ID)
        }
    }
    if r.cfg.pcID != "" {
        for _, id := range candidates { if id == r.cfg.pcID { return id, nil } }
        return "", failed("pc-preflight", "PC_NOT_ONLINE", "The requested PC is not online with remote routing enabled.")
    }
    if len(candidates) != 1 {
        return "", failed("pc-preflight", "PC_SELECTION_AMBIGUOUS", "Specify PcId when the account does not expose exactly one online PC.")
    }
    return candidates[0], nil
}

func (r *runner) createOperation(operation, path string, offset *int64, shell string) (fileOperation, error) {
    body := map[string]any{"operation": operation, "path": path}
    if offset != nil { body["offset"] = *offset }
    if shell != "" { body["shell_type"] = shell }
    raw, err := r.signedRequest(http.MethodPost, r.operationBase(), body, mustUUID(), http.StatusCreated)
    if err != nil { return fileOperation{}, err }
    var envelope operationEnvelope
    decodeErr := json.Unmarshal(raw, &envelope)
    clear(raw)
    if decodeErr != nil || !canonicalUUID.MatchString(envelope.FileOperation.ID) ||
        envelope.FileOperation.PCID != r.cfg.pcID || envelope.FileOperation.Operation != operation ||
        envelope.FileOperation.Path != path || envelope.FileOperation.Status != "PENDING" {
        return fileOperation{}, failed("operation-create", "OPERATION_CREATE_INVALID", "The server returned an invalid file operation.")
    }
    return envelope.FileOperation, nil
}

func (r *runner) waitOperation(ctx context.Context, operation fileOperation) (operationEnvelope, error) {
    path := r.operationBase()+"/"+operation.ID
    ticker := time.NewTicker(300 * time.Millisecond)
    defer ticker.Stop()
    for {
        raw, err := r.signedRequest(http.MethodGet, path, nil, "", http.StatusOK)
        if err != nil { return operationEnvelope{}, err }
        var envelope operationEnvelope
        decodeErr := json.Unmarshal(raw, &envelope)
        clear(raw)
        if decodeErr != nil || envelope.FileOperation.ID != operation.ID ||
            envelope.FileOperation.PCID != r.cfg.pcID || envelope.FileOperation.Operation != operation.Operation ||
            envelope.FileOperation.Path != operation.Path {
            return operationEnvelope{}, failed("operation-poll", "OPERATION_POLL_INVALID", "The server returned an invalid operation status.")
        }
        switch envelope.FileOperation.Status {
        case "SUCCEEDED": return envelope, nil
        case "FAILED": return operationEnvelope{}, failed("operation-poll", "AGENT_OPERATION_FAILED", "The Windows Agent reported a file-operation failure.")
        case "CANCELLED", "EXPIRED": return operationEnvelope{}, failed("operation-poll", "OPERATION_TERMINATED", "The file operation terminated before succeeding.")
        case "PENDING", "PROCESSING":
        default: return operationEnvelope{}, failed("operation-poll", "OPERATION_STATUS_INVALID", "The server returned an unknown operation status.")
        }
        select {
        case <-ctx.Done(): return operationEnvelope{}, failed("operation-poll", "OPERATION_TIMEOUT", "The Windows Agent did not complete the operation in time.")
        case <-ticker.C:
        }
    }
}

func (r *runner) verifyListing() error {
    ctx, cancel := context.WithTimeout(context.Background(), r.cfg.timeout)
    defer cancel()
    first, err := r.createOperation("LIST", r.cfg.fixtureRoot, nil, "")
    if err != nil { return failed("list", "LIST_CREATE_FAILED", "The first LIST page could not be created.") }
    firstEnvelope, err := r.waitOperation(ctx, first)
    if err != nil { return failed("list", "LIST_FIRST_PAGE_FAILED", "The Windows Agent did not return the first LIST page.") }
    firstPage, err := decodeListing(firstEnvelope.FileOperation.Result, r.cfg.fixtureRoot)
    if err != nil { return err }
    if firstPage.NextOffset == nil || *firstPage.NextOffset != 500 || len(firstPage.Entries) != 500 {
        return failed("list", "LIST_PAGINATION_INVALID", "The first LIST page did not expose the fixed 500-entry boundary.")
    }
    second, err := r.createOperation("LIST", r.cfg.fixtureRoot, firstPage.NextOffset, "")
    if err != nil { return failed("list", "LIST_CREATE_FAILED", "The second LIST page could not be created.") }
    secondEnvelope, err := r.waitOperation(ctx, second)
    if err != nil { return failed("list", "LIST_SECOND_PAGE_FAILED", "The Windows Agent did not return the second LIST page.") }
    secondPage, err := decodeListing(secondEnvelope.FileOperation.Result, r.cfg.fixtureRoot)
    if err != nil { return err }
    if secondPage.NextOffset != nil || len(secondPage.Entries) != 5 {
        return failed("list", "LIST_PAGINATION_INVALID", "The terminal LIST page did not end at the expected offset.")
    }
    all := append(append([]directoryEntry{}, firstPage.Entries...), secondPage.Entries...)
    expected, err := expectedDirectoryEntries(r.cfg.fixtureRoot)
    if err != nil { return failed("list", "FIXTURE_READ_FAILED", "The controlled fixture could not be inspected.") }
    if err := compareDirectoryEntries(expected, all); err != nil { return err }
    r.checks["list"] = map[string]any{
        "pages": 2, "entries": len(all), "first_page_entries": len(firstPage.Entries),
        "second_page_entries": len(secondPage.Entries), "next_offset": *firstPage.NextOffset,
        "result_exact": true,
    }
    return nil
}

func decodeListing(raw json.RawMessage, expectedPath string) (directoryListing, error) {
    var page directoryListing
    if len(raw) == 0 || json.Unmarshal(raw, &page) != nil || page.CurrentPath != expectedPath || page.ParentPath == nil {
        return page, failed("list", "LIST_RESULT_INVALID", "The Windows Agent returned an invalid LIST result.")
    }
    return page, nil
}

func expectedDirectoryEntries(root string) (map[string]directoryEntry, error) {
    values, err := os.ReadDir(root)
    if err != nil { return nil, err }
    result := make(map[string]directoryEntry, len(values))
    for _, value := range values {
        info, err := value.Info()
        if err != nil { return nil, err }
        entry := directoryEntry{Name: value.Name(), Path: filepath.Join(root, value.Name()), Kind: "file"}
        if value.IsDir() { entry.Kind = "directory" } else { size := info.Size(); entry.SizeBytes = &size }
        result[entry.Name] = entry
    }
    return result, nil
}

func compareDirectoryEntries(expected map[string]directoryEntry, actual []directoryEntry) error {
    if len(expected) != len(actual) { return failed("list", "LIST_RESULT_MISMATCH", "The LIST result count did not match the controlled fixture.") }
    seen := map[string]bool{}
    for _, item := range actual {
        expectedItem, ok := expected[item.Name]
        if !ok || seen[item.Name] || item.Path != expectedItem.Path || item.Kind != expectedItem.Kind {
            return failed("list", "LIST_RESULT_MISMATCH", "The LIST result did not exactly match the controlled fixture.")
        }
        if item.Kind == "file" {
            if item.SizeBytes == nil || expectedItem.SizeBytes == nil || *item.SizeBytes != *expectedItem.SizeBytes {
                return failed("list", "LIST_RESULT_MISMATCH", "A LIST file size did not match the controlled fixture.")
            }
        } else if item.SizeBytes != nil {
            return failed("list", "LIST_RESULT_MISMATCH", "A LIST directory unexpectedly exposed a file size.")
        }
        seen[item.Name] = true
    }
    return nil
}

func (r *runner) verifyFileDownload(label, sourcePath string) error {
    ctx, cancel := context.WithTimeout(context.Background(), r.cfg.timeout)
    defer cancel()
    operation, err := r.createOperation("DOWNLOAD", sourcePath, nil, "")
    if err != nil { return failed("download-"+label, "DOWNLOAD_CREATE_FAILED", "The download operation could not be created.") }
    envelope, err := r.waitOperation(ctx, operation)
    if err != nil { return failed("download-"+label, "DOWNLOAD_OPERATION_FAILED", "The Windows Agent did not prepare the file download.") }
    if envelope.Download == nil || envelope.Download.DirectoryZIP {
        return failed("download-"+label, "DOWNLOAD_DESCRIPTOR_INVALID", "The server returned an invalid file download descriptor.")
    }
    expected, err := os.ReadFile(sourcePath)
    if err != nil { return failed("download-"+label, "FIXTURE_READ_FAILED", "The controlled fixture file could not be read.") }
    downloaded, err := r.fetchDownload(*envelope.Download)
    if err != nil { clear(expected); return err }
    defer clear(downloaded)
    defer clear(expected)
    digest := sha256.Sum256(expected)
    if !bytes.Equal(downloaded, expected) || int64(len(downloaded)) != envelope.Download.SizeBytes ||
        envelope.Download.Filename != filepath.Base(sourcePath) ||
        envelope.Download.SHA256 != base64.RawURLEncoding.EncodeToString(digest[:]) {
        return failed("download-"+label, "DOWNLOAD_CONTENT_MISMATCH", "The downloaded file bytes or SHA-256 did not match the source fixture.")
    }
    r.checks["download_"+label] = map[string]any{
        "filename": envelope.Download.Filename, "size_bytes": len(downloaded),
        "sha256": envelope.Download.SHA256, "bytes_exact": true,
    }
    return nil
}

func (r *runner) verifyDirectoryDownload() error {
    ctx, cancel := context.WithTimeout(context.Background(), r.cfg.timeout)
    defer cancel()
    operation, err := r.createOperation("DOWNLOAD", r.cfg.archiveRoot, nil, "")
    if err != nil { return failed("directory-zip", "DIRECTORY_DOWNLOAD_CREATE_FAILED", "The directory download could not be created.") }
    envelope, err := r.waitOperation(ctx, operation)
    if err != nil { return failed("directory-zip", "DIRECTORY_DOWNLOAD_FAILED", "The Windows Agent did not prepare the directory ZIP.") }
    if envelope.Download == nil || !envelope.Download.DirectoryZIP || envelope.Download.Filename != filepath.Base(r.cfg.archiveRoot)+".zip" {
        return failed("directory-zip", "DIRECTORY_DESCRIPTOR_INVALID", "The server returned an invalid directory ZIP descriptor.")
    }
    downloaded, err := r.fetchDownload(*envelope.Download)
    if err != nil { return err }
    defer clear(downloaded)
    digest := sha256.Sum256(downloaded)
    if int64(len(downloaded)) != envelope.Download.SizeBytes ||
        envelope.Download.SHA256 != base64.RawURLEncoding.EncodeToString(digest[:]) {
        return failed("directory-zip", "DIRECTORY_ZIP_HASH_MISMATCH", "The directory ZIP size or SHA-256 did not match its authenticated metadata.")
    }
    entries, entryHashes, err := inspectZIP(downloaded)
    if err != nil { return err }
    expectedEntries, expectedHashes, err := expectedZIP(r.cfg.archiveRoot)
    if err != nil { return failed("directory-zip", "FIXTURE_READ_FAILED", "The directory fixture could not be inspected.") }
    if !equalStrings(entries, expectedEntries) || len(entryHashes) != len(expectedHashes) {
        return failed("directory-zip", "DIRECTORY_ZIP_ENTRIES_MISMATCH", "The directory ZIP entries did not match the controlled tree.")
    }
    for name, expectedHash := range expectedHashes {
        if entryHashes[name] != expectedHash {
            return failed("directory-zip", "DIRECTORY_ZIP_ENTRY_HASH_MISMATCH", "A directory ZIP entry SHA-256 did not match its source file.")
        }
    }
    r.checks["directory_zip"] = map[string]any{
        "filename": envelope.Download.Filename, "size_bytes": len(downloaded),
        "sha256": envelope.Download.SHA256, "entries": entries,
        "archive_hash_exact": true, "entry_hashes_exact": true,
    }
    return nil
}

func (r *runner) fetchDownload(descriptor downloadDescriptor) ([]byte, error) {
    parsed, err := url.Parse(descriptor.Request.URL)
    allowed, _ := url.Parse(r.cfg.historyOrigin)
    if err != nil || parsed.Scheme != "https" || parsed.User != nil || parsed.Fragment != "" ||
        !strings.EqualFold(parsed.Host, allowed.Host) || !strings.EqualFold(descriptor.Request.Method, http.MethodGet) ||
        descriptor.SizeBytes < 0 || descriptor.SizeBytes > maximumDownloadBytes {
        return nil, failed("download", "PRESIGNED_DOWNLOAD_POLICY_FAILED", "The presigned download violated the HTTPS origin or size policy.")
    }
    request, _ := http.NewRequest(http.MethodGet, descriptor.Request.URL, nil)
    for name, values := range descriptor.Request.Headers {
        if strings.TrimSpace(name) == "" { return nil, failed("download", "PRESIGNED_HEADERS_INVALID", "The presigned download headers are invalid.") }
        for _, value := range values { request.Header.Add(name, value) }
    }
    response, err := r.client.Do(request)
    if err != nil { return nil, failed("download", "DOWNLOAD_REQUEST_FAILED", "The presigned download request failed.") }
    raw, status, readErr := readBoundedResponse(response, maximumDownloadBytes+1)
    if readErr != nil || status != http.StatusOK || len(raw) > maximumDownloadBytes {
        clear(raw)
        return nil, failed("download", "DOWNLOAD_REQUEST_FAILED", "The presigned download request failed.")
    }
    digest := sha256.Sum256(raw)
    if int64(len(raw)) != descriptor.SizeBytes || descriptor.SHA256 != base64.RawURLEncoding.EncodeToString(digest[:]) {
        clear(raw)
        return nil, failed("download", "DOWNLOAD_HASH_MISMATCH", "The downloaded object did not match its authenticated metadata.")
    }
    return raw, nil
}

func inspectZIP(raw []byte) ([]string, map[string]string, error) {
    reader, err := zip.NewReader(bytes.NewReader(raw), int64(len(raw)))
    if err != nil { return nil, nil, failed("directory-zip", "DIRECTORY_ZIP_INVALID", "The directory download is not a valid ZIP archive.") }
    names := make([]string, 0, len(reader.File))
    hashes := map[string]string{}
    for _, entry := range reader.File {
        cleaned := strings.ReplaceAll(entry.Name, "\\", "/")
        if cleaned != entry.Name || strings.HasPrefix(cleaned, "/") || strings.Contains(cleaned, "../") {
            return nil, nil, failed("directory-zip", "DIRECTORY_ZIP_PATH_UNSAFE", "The directory ZIP contains an unsafe entry path.")
        }
        names = append(names, cleaned)
        if entry.FileInfo().IsDir() { continue }
        stream, openErr := entry.Open()
        if openErr != nil { return nil, nil, failed("directory-zip", "DIRECTORY_ZIP_INVALID", "A directory ZIP entry could not be read.") }
        content, readErr := io.ReadAll(io.LimitReader(stream, maximumDownloadBytes+1))
        stream.Close()
        if readErr != nil || len(content) > maximumDownloadBytes {
            clear(content)
            return nil, nil, failed("directory-zip", "DIRECTORY_ZIP_INVALID", "A directory ZIP entry could not be read.")
        }
        digest := sha256.Sum256(content)
        clear(content)
        hashes[cleaned] = base64.RawURLEncoding.EncodeToString(digest[:])
    }
    sort.Strings(names)
    return names, hashes, nil
}

func expectedZIP(root string) ([]string, map[string]string, error) {
    base := filepath.Base(root)
    names := []string{base + "/"}
    hashes := map[string]string{}
    err := filepath.WalkDir(root, func(path string, entry os.DirEntry, walkErr error) error {
        if walkErr != nil { return walkErr }
        if path == root { return nil }
        relative, err := filepath.Rel(root, path)
        if err != nil { return err }
        name := base + "/" + strings.ReplaceAll(relative, "\\", "/")
        if entry.IsDir() { names = append(names, name+"/"); return nil }
        content, err := os.ReadFile(path)
        if err != nil { return err }
        digest := sha256.Sum256(content)
        clear(content)
        names = append(names, name)
        hashes[name] = base64.RawURLEncoding.EncodeToString(digest[:])
        return nil
    })
    sort.Strings(names)
    return names, hashes, err
}

func (r *runner) verifyTerminal(shell string) error {
    ctx, cancel := context.WithTimeout(context.Background(), r.cfg.timeout)
    defer cancel()
    operation, err := r.createOperation("OPEN_TERMINAL", r.cfg.fixtureRoot, nil, shell)
    if err != nil { return failed("open-terminal", "OPEN_TERMINAL_CREATE_FAILED", "The terminal operation could not be created.") }
    envelope, err := r.waitOperation(ctx, operation)
    if err != nil { return failed("open-terminal", "OPEN_TERMINAL_FAILED", "The Windows Agent did not open the requested terminal.") }
    var value terminalResult
    decodeErr := json.Unmarshal(envelope.FileOperation.Result, &value)
    // Record a syntactically valid returned session ID before checking the
    // rest of the contract so wrapper cleanup still owns a launched host when
    // shell_type or cwd regresses.
    if decodeErr == nil && canonicalUUID.MatchString(value.SessionID) {
        r.cleanupSessions = append(r.cleanupSessions, value.SessionID)
        _ = writeJSONFile(r.cfg.cleanupFile, map[string]any{"session_ids": r.cleanupSessions})
    }
    if len(envelope.FileOperation.Result) == 0 || decodeErr != nil ||
        !canonicalUUID.MatchString(value.SessionID) || value.ShellType != shell || value.CWD != r.cfg.fixtureRoot {
        return failed("open-terminal", "OPEN_TERMINAL_RESULT_MISMATCH", "OPEN_TERMINAL did not return the exact session_id, shell_type, and cwd.")
    }
    if err := r.waitForSession(ctx, value.SessionID); err != nil { return err }
    deleteRaw, deleteStatus, deleteErr := r.signedRequestAny(
        http.MethodDelete, "/v1/sessions/"+value.SessionID, nil, mustUUID())
    if deleteErr != nil {
        clear(deleteRaw)
        return failed("open-terminal", "OPEN_TERMINAL_ACTIVE_DELETE_CHECK_FAILED", "The active-session deletion fence could not be checked.")
    }
    deleteCode := responseErrorCode(deleteRaw)
    clear(deleteRaw)
    if deleteStatus != http.StatusConflict || deleteCode != "SESSION_ACTIVE" {
        return failed("open-terminal", "OPEN_TERMINAL_ACTIVE_DELETE_NOT_FENCED", "The server did not reject deletion of the running session with SESSION_ACTIVE.")
    }
    existing, _ := r.checks["terminals"].([]map[string]any)
    existing = append(existing, map[string]any{
        "shell_type": shell, "session_id": value.SessionID, "cwd": value.CWD,
        "session_registered": true, "active_delete_fenced": true,
        "server_session_deleted": false, "owned_cleanup_delegated": true,
        "realtime_terminate_executed": false,
    })
    r.checks["terminals"] = existing
    return nil
}

func (r *runner) waitForSession(ctx context.Context, sessionID string) error {
    path := "/v1/pcs/"+r.cfg.pcID+"/sessions"
    ticker := time.NewTicker(400 * time.Millisecond)
    defer ticker.Stop()
    for {
        raw, err := r.signedRequest(http.MethodGet, path, nil, "", http.StatusOK)
        if err == nil {
            var list sessionList
            if json.Unmarshal(raw, &list) == nil {
                clear(raw)
                for _, item := range list.Items {
                    if item.ID == sessionID && item.PCID == r.cfg.pcID { return nil }
                }
            } else { clear(raw) }
        }
        select {
        case <-ctx.Done(): return failed("open-terminal", "OPEN_TERMINAL_SESSION_NOT_REGISTERED", "The returned session_id was not registered for the selected PC.")
        case <-ticker.C:
        }
    }
}

func (r *runner) verifyCancellation() error {
    for attempt := 1; attempt <= 3; attempt++ {
        operation, err := r.createOperation("LIST", r.cfg.fixtureRoot, nil, "")
        if err != nil { return failed("cancellation", "CANCEL_CREATE_FAILED", "The cancellable operation could not be created.") }
        path := r.operationBase()+"/"+operation.ID
        raw, status, requestErr := r.signedRequestAny(http.MethodDelete, path, nil, mustUUID())
        if requestErr != nil { clear(raw); return failed("cancellation", "CANCEL_REQUEST_FAILED", "The cancellation request failed.") }
        if status == http.StatusConflict { clear(raw); continue }
        if status != http.StatusOK { clear(raw); return failed("cancellation", "CANCEL_REQUEST_FAILED", "The cancellation request was not accepted.") }
        var envelope operationEnvelope
        decodeErr := json.Unmarshal(raw, &envelope)
        clear(raw)
        if decodeErr != nil || envelope.FileOperation.ID != operation.ID || envelope.FileOperation.Status != "CANCELLED" ||
            envelope.FileOperation.CancelledAt == "" {
            return failed("cancellation", "CANCEL_RESULT_INVALID", "The cancellation response was invalid.")
        }
        getRaw, err := r.signedRequest(http.MethodGet, path, nil, "", http.StatusOK)
        if err != nil { return failed("cancellation", "CANCEL_POLL_FAILED", "The cancelled operation could not be read back.") }
        var persisted operationEnvelope
        decodeErr = json.Unmarshal(getRaw, &persisted)
        clear(getRaw)
        if decodeErr != nil || persisted.FileOperation.Status != "CANCELLED" || persisted.FileOperation.CancelledAt == "" {
            return failed("cancellation", "CANCEL_NOT_PERSISTED", "The cancelled state was not persisted.")
        }
        r.checks["cancellation"] = map[string]any{"status": "CANCELLED", "persisted": true, "attempt": attempt}
        return nil
    }
    return failed("cancellation", "CANCEL_RACE_EXHAUSTED", "The Agent completed each operation before cancellation could be observed.")
}

func (r *runner) operationBase() string { return "/v1/pcs/"+r.cfg.pcID+"/file-operations" }

func (r *runner) signedRequest(method, path string, value any, idempotency string, expected int) ([]byte, error) {
    raw, status, err := r.signedRequestAny(method, path, value, idempotency)
    if err != nil { return nil, err }
    if status != expected {
        code := responseErrorCode(raw)
        clear(raw)
        if code != "" {
            return nil, failed("http", "HTTP_"+code, "An authenticated API request was rejected with a stable server error.")
        }
        return nil, failed("http", "HTTP_STATUS_UNEXPECTED", "An authenticated API request returned an unexpected status.")
    }
    return raw, nil
}

func (r *runner) signedRequestAny(method, path string, value any, idempotency string) ([]byte, int, error) {
    var body []byte
    var err error
    if value != nil {
        body, err = json.Marshal(value)
        if err != nil { return nil, 0, failed("http", "REQUEST_ENCODE_FAILED", "An authenticated request could not be encoded.") }
    }
    defer clear(body)
    request, _ := http.NewRequest(method, r.cfg.root+path, bytes.NewReader(body))
    request.Header.Set("Authorization", "Bearer "+r.device.token)
    if len(body) > 0 { request.Header.Set("Content-Type", "application/json") }
    if idempotency != "" { request.Header.Set("Idempotency-Key", idempotency) }
    if err := addProof(request.Header, r.device, method, path, body); err != nil {
        return nil, 0, failed("http", "DEVICE_PROOF_FAILED", "The device proof could not be created.")
    }
    response, err := r.client.Do(request)
    if err != nil { return nil, 0, failed("http", "AUTHENTICATED_REQUEST_FAILED", "An authenticated API request failed.") }
    raw, status, err := readBoundedResponse(response, maximumJSONBytes)
    if err != nil { clear(raw); return nil, status, failed("http", "RESPONSE_READ_FAILED", "An authenticated API response was invalid.") }
    return raw, status, nil
}

func addProof(headers http.Header, device *identity, method, target string, body []byte) error {
    timestamp := strconv.FormatInt(time.Now().UnixMilli(), 10)
    nonce, err := randomBase64(16)
    if err != nil { return err }
    digest := sha256.Sum256(body)
    canonical := strings.Join([]string{
        deviceProofDomain, strings.ToUpper(method), target, timestamp, nonce, hex.EncodeToString(digest[:]),
    }, "\n")
    headers.Set("X-TermLink-Device", device.id)
    headers.Set("X-TermLink-Timestamp", timestamp)
    headers.Set("X-TermLink-Nonce", nonce)
    headers.Set("X-TermLink-Signature", base64.RawURLEncoding.EncodeToString(ed25519.Sign(device.private, []byte(canonical))))
    return nil
}

func (r *runner) revokeSelf() {
    if r.device == nil || r.device.token == "" { return }
    token := r.device.token
    _, _, _ = r.signedRequestAny(http.MethodPost, "/v1/devices/"+r.device.id+"/revoke", nil, mustUUID())
    // Revoke is idempotent only from another trusted device. Avoid a second
    // self-revoke attempt after execute's deferred cleanup has run.
    if r.device.token == token { r.device.token = "" }
}

func (r *runner) destroy() {
    if r.client != nil { if transport, ok := r.client.Transport.(*http.Transport); ok { transport.CloseIdleConnections() } }
    if r.device != nil {
        clear(r.device.private)
        clear(r.device.exchange)
        r.device.token = ""
    }
}

func readBoundedResponse(response *http.Response, limit int64) ([]byte, int, error) {
    if response == nil { return nil, 0, errors.New("nil response") }
    defer response.Body.Close()
    raw, err := io.ReadAll(io.LimitReader(response.Body, limit+1))
    if err != nil || int64(len(raw)) > limit { return raw, response.StatusCode, errors.New("response too large") }
    return raw, response.StatusCode, nil
}

func responseErrorCode(raw []byte) string {
    var value struct {
        Error struct { Code string `json:"code"` } `json:"error"`
    }
    if json.Unmarshal(raw, &value) == nil && stableErrorCode.MatchString(value.Error.Code) { return value.Error.Code }
    return ""
}

func randomBase64(size int) (string, error) {
    value := make([]byte, size)
    if _, err := rand.Read(value); err != nil { return "", err }
    return base64.RawURLEncoding.EncodeToString(value), nil
}

func mustUUID() string {
    value, err := newUUID()
    if err != nil { panic(err) }
    return value
}

func writeJSONFile(path string, value any) error {
    raw, err := json.MarshalIndent(value, "", "  ")
    if err != nil { return err }
    defer clear(raw)
    return os.WriteFile(path, raw, 0600)
}

func equalStrings(left, right []string) bool {
    if len(left) != len(right) { return false }
    for index := range left { if left[index] != right[index] { return false } }
    return true
}
'@

$caught = $null
$fixture = $null
$protocolResult = $null
$fixtureCleanup = 'not_started'
$terminalCleanup = [pscustomobject][ordered]@{
    requested_session_ids = @()
    stopped_session_ids = @()
    unmatched_session_ids = @()
}
$terminalCleanupExact = $false
$resultPath = $null
$cleanupPlanPath = $null
$startedAt = [DateTime]::UtcNow

try {
    Assert-FileOperationsE2E (Test-Path -LiteralPath $TemporaryParentPath -PathType Container) `
        'TEMP_PARENT_UNAVAILABLE' 'preflight' 'The temporary parent directory is unavailable.'
    $resolvedTemporaryParent = [IO.Path]::GetFullPath($TemporaryParentPath)
    $temporaryId = [Guid]::NewGuid().ToString('N')
    $script:ownedTemporaryPath = [IO.Path]::GetFullPath((Join-Path $resolvedTemporaryParent `
        "termlink-file-operations-e2e-$temporaryId"))
    Assert-FileOperationsE2E (Test-StrictChildPath -Root $resolvedTemporaryParent -Candidate $script:ownedTemporaryPath) `
        'TEMP_PATH_UNSAFE' 'preflight' 'The generated temporary path escaped its parent.'
    [IO.Directory]::CreateDirectory($script:ownedTemporaryPath) | Out-Null
    $script:ownedMarkerValue = "TermLink.FileOperationsE2E/v1`n$temporaryId`n$ArtifactRunId"
    [IO.File]::WriteAllText(
        (Join-Path $script:ownedTemporaryPath '.termlink-file-operations-e2e-owner'),
        $script:ownedMarkerValue,
        [Text.UTF8Encoding]::new($false))
    $fixture = New-ControlledFixture -Root $script:ownedTemporaryPath

    if (-not $FixtureOnly) {
        Assert-FileOperationsE2E ($null -ne $AcceptanceOrigin) 'ACCEPTANCE_ORIGIN_REQUIRED' 'preflight' `
            'AcceptanceOrigin is required for protocol E2E.'
        $rootOrigin = $AcceptanceOrigin.GetLeftPart([UriPartial]::Authority).TrimEnd('/')
        $issuer = if ([string]::IsNullOrWhiteSpace($OidcIssuer)) {
            "$rootOrigin/realms/termlink"
        } else { $OidcIssuer.TrimEnd('/') }
        $history = if ([string]::IsNullOrWhiteSpace($HistoryOrigin)) {
            [Environment]::GetEnvironmentVariable('TERMLINK_ACCEPTANCE_HISTORY_ORIGIN')
        } else { $HistoryOrigin }
        Assert-FileOperationsE2E (-not [string]::IsNullOrWhiteSpace($history)) `
            'HISTORY_ORIGIN_REQUIRED' 'preflight' 'HistoryOrigin is required for authenticated downloads.'
        try { $issuerUri = [Uri]$issuer; $historyUri = [Uri]$history }
        catch { Stop-FileOperationsE2E 'ORIGIN_INVALID' 'preflight' 'OIDC or history origin is invalid.' }
        Assert-FileOperationsE2E ($issuerUri.IsAbsoluteUri -and $issuerUri.Scheme -eq 'https' -and
            [string]::IsNullOrEmpty($issuerUri.UserInfo) -and -not $issuerUri.Query -and -not $issuerUri.Fragment -and
            $issuerUri.GetLeftPart([UriPartial]::Authority) -ceq $AcceptanceOrigin.GetLeftPart([UriPartial]::Authority)) `
            'OIDC_ORIGIN_INVALID' 'preflight' 'OidcIssuer must use the acceptance HTTPS authority.'
        Assert-FileOperationsE2E ($historyUri.IsAbsoluteUri -and $historyUri.Scheme -eq 'https' -and
            [string]::IsNullOrEmpty($historyUri.UserInfo) -and $historyUri.AbsolutePath -eq '/' -and
            -not $historyUri.Query -and -not $historyUri.Fragment) `
            'HISTORY_ORIGIN_INVALID' 'preflight' 'HistoryOrigin must be an HTTPS origin.'
        $passwordPath = Resolve-RepositoryFile -Path $PasswordFilePath -Label 'Acceptance password file'
        $caPath = Resolve-RepositoryFile -Path $CaFilePath -Label 'Acceptance CA file'
        $goPath = Get-CanonicalGoTool
        $goSourcePath = Join-Path $script:ownedTemporaryPath 'file_operations_e2e.go'
        $resultPath = Join-Path $script:ownedTemporaryPath 'protocol-result.json'
        $cleanupPlanPath = Join-Path $script:ownedTemporaryPath 'terminal-cleanup.json'
        [IO.File]::WriteAllText($goSourcePath, $goSource, [Text.UTF8Encoding]::new($false))

        $arguments = [Collections.Generic.List[string]]::new()
        foreach ($pair in @(
            @('--root', $rootOrigin), @('--issuer', $issuer), @('--history-origin', $historyUri.GetLeftPart([UriPartial]::Authority)),
            @('--username', $Username), @('--password-file', $passwordPath), @('--ca-file', $caPath),
            @('--fixture-root', $fixture.root), @('--empty-file', $fixture.empty_file),
            @('--unicode-file', $fixture.unicode_file), @('--archive-root', $fixture.archive_root),
            @('--result-file', $resultPath), @('--cleanup-file', $cleanupPlanPath),
            @('--timeout-seconds', [string]([Math]::Max(60, $TimeoutSeconds - 20)))
        )) {
            $arguments.Add([string]$pair[0]); $arguments.Add([string]$pair[1])
        }
        if ($PcId -ne [Guid]::Empty) { $arguments.Add('--pc-id'); $arguments.Add($PcId.ToString('D')) }
        if ($SkipTerminalOperations) { $arguments.Add('--skip-terminals') }
        $native = Invoke-GoProtocolHarness -GoPath $goPath -SourcePath $goSourcePath `
            -Arguments @($arguments) -ProcessTimeoutSeconds $TimeoutSeconds
        if ($native.TimedOut) {
            Stop-FileOperationsE2E 'PROTOCOL_TIMEOUT' 'protocol' 'The protocol E2E exceeded its timeout.'
        }
        if (Test-Path -LiteralPath $resultPath -PathType Leaf) {
            try { $protocolResult = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json -ErrorAction Stop }
            catch { Stop-FileOperationsE2E 'PROTOCOL_RESULT_INVALID' 'protocol' 'The protocol E2E result is invalid.' }
        }
        Assert-FileOperationsE2E ($null -ne $protocolResult) 'PROTOCOL_RESULT_MISSING' 'protocol' `
            'The protocol E2E did not write a result.'
        if ([string]$protocolResult.status -ne 'passed' -or $native.ExitCode -ne 0) {
            $code = if ([string]$protocolResult.failure.code -match '^[A-Z0-9_]+$') {
                [string]$protocolResult.failure.code
            } else { 'PROTOCOL_E2E_FAILED' }
            $stage = if ([string]$protocolResult.failure.stage -match '^[a-z0-9_-]+$') {
                [string]$protocolResult.failure.stage
            } else { 'protocol' }
            $message = if (-not [string]::IsNullOrWhiteSpace([string]$protocolResult.failure.message)) {
                [string]$protocolResult.failure.message
            } else { 'The protocol E2E did not complete.' }
            Stop-FileOperationsE2E $code $stage $message
        }
    }
}
catch {
    $caught = $_
    if (-not $script:failureCode) {
        $script:failureCode = 'UNEXPECTED_FAILURE'
        $script:failureStage = 'runner'
        $script:failureMessage = 'An unexpected failure occurred; diagnostic text was suppressed.'
    }
}
finally {
    if ($cleanupPlanPath) {
        $terminalCleanup = Stop-OwnedTerminalSessions -CleanupPlanPath $cleanupPlanPath
    }
    if ($null -ne $protocolResult) {
        $hasChecks = @($protocolResult.PSObject.Properties.Name) -ccontains 'checks'
        $terminalChecks = if ($hasChecks -and $null -ne $protocolResult.checks -and
            @($protocolResult.checks.PSObject.Properties.Name) -ccontains 'terminals') {
            @($protocolResult.checks.terminals)
        } else { @() }
        $protocolTerminalIds = @($terminalChecks | ForEach-Object {
            $parsed = [Guid]::Empty
            if ([Guid]::TryParse([string]$_.session_id, [ref]$parsed) -and $parsed -ne [Guid]::Empty) {
                $parsed.ToString('D')
            }
        } | Select-Object -Unique)
        $requestedTerminalIds = @($terminalCleanup.requested_session_ids | Sort-Object)
        $stoppedTerminalIds = @($terminalCleanup.stopped_session_ids | Sort-Object)
        $expectedTerminalIds = @($protocolTerminalIds | Sort-Object)
        $terminalCleanupExact = $requestedTerminalIds.Count -eq $expectedTerminalIds.Count -and
            $stoppedTerminalIds.Count -eq $expectedTerminalIds.Count -and
            @($terminalCleanup.unmatched_session_ids).Count -eq 0 -and
            (($requestedTerminalIds -join '|') -ceq ($expectedTerminalIds -join '|')) -and
            (($stoppedTerminalIds -join '|') -ceq ($expectedTerminalIds -join '|'))
        if ([string]$protocolResult.status -eq 'passed' -and -not $SkipTerminalOperations -and
            ($expectedTerminalIds.Count -ne 2 -or -not $terminalCleanupExact) -and
            -not $script:failureCode) {
            $script:failureCode = 'TERMINAL_CLEANUP_INCOMPLETE'
            $script:failureStage = 'cleanup'
            $script:failureMessage = 'The two owned terminal SessionHost processes were not both stopped exactly.'
            $caught = [InvalidOperationException]::new($script:failureMessage)
        }
    }
    if ($script:ownedTemporaryPath -and (Test-Path -LiteralPath $script:ownedTemporaryPath -PathType Container)) {
        try {
            Remove-OwnedTemporaryTree -ParentPath ([IO.Path]::GetFullPath($TemporaryParentPath)) `
                -CandidatePath $script:ownedTemporaryPath -ExpectedMarker $script:ownedMarkerValue
            $fixtureCleanup = 'removed'
        }
        catch {
            $fixtureCleanup = 'failed'
            if (-not $script:failureCode) {
                $script:failureCode = 'TEMP_CLEANUP_FAILED'
                $script:failureStage = 'cleanup'
                $script:failureMessage = 'The owned temporary fixture could not be removed safely.'
                $caught = $_
            }
        }
    }
    if (-not $FixtureOnly) {
        try {
            Assert-FileOperationsE2E (Test-StrictChildPath -Root $artifactRoot -Candidate $artifactDirectory) `
                'ARTIFACT_PATH_UNSAFE' 'evidence' 'The evidence path escaped its artifact root.'
            [IO.Directory]::CreateDirectory($artifactDirectory) | Out-Null
            $evidence = [ordered]@{
                schema_version = 1
                run_id = $ArtifactRunId
                status = if ($script:failureCode) { 'failed' } else { 'passed' }
                started_at = $startedAt.ToString('o')
                completed_at = [DateTime]::UtcNow.ToString('o')
                protocol = $protocolResult
                fixture = if ($fixture) {
                    [ordered]@{
                        root_entry_count = $fixture.root_entry_count
                        empty_sha256 = $fixture.empty_sha256
                        unicode_sha256 = $fixture.unicode_sha256
                        archive_entry_hashes = $fixture.archive_entry_hashes
                    }
                } else { $null }
                cleanup = [ordered]@{
                    temporary_tree = $fixtureCleanup
                    owned_terminal_session_hosts_requested = @($terminalCleanup.requested_session_ids).Count
                    owned_terminal_session_hosts_stopped = @($terminalCleanup.stopped_session_ids).Count
                    owned_terminal_session_hosts_unmatched = @($terminalCleanup.unmatched_session_ids).Count
                    exact_owned_terminal_cleanup = $terminalCleanupExact
                    strategy = 'exact_session_id_sessionhost_process_stop'
                    realtime_terminate_executed = $false
                }
                android_ui = [ordered]@{
                    executed = $false
                    scope = 'protocol_e2e_only'
                    note = 'Android emulator UI validation is a separate manual or instrumentation run.'
                }
                failure = if ($script:failureCode) {
                    [ordered]@{
                        code = $script:failureCode
                        stage = $script:failureStage
                        message = $script:failureMessage
                    }
                } else { $null }
            }
            $evidenceJSON = $evidence | ConvertTo-Json -Depth 20
            Assert-FileOperationsE2E ($evidenceJSON -notmatch `
                '(?i)bearer\s+|access_token|refresh_token|authorization|cookie|password\s*[:=]|private_key|presigned.*url') `
                'EVIDENCE_SECRET_SHAPE' 'evidence' 'Evidence contained a credential-shaped field.'
            [IO.File]::WriteAllText($evidencePath, $evidenceJSON, [Text.UTF8Encoding]::new($false))
        }
        catch {
            if (-not $script:failureCode) {
                $script:failureCode = 'EVIDENCE_WRITE_FAILED'
                $script:failureStage = 'evidence'
                $script:failureMessage = 'Structured E2E evidence could not be written.'
                $caught = $_
            }
        }
    }
}

if ($script:failureCode) {
    throw [InvalidOperationException]::new("$($script:failureCode): $($script:failureMessage)")
}

if ($FixtureOnly) {
    [pscustomobject][ordered]@{
        status = 'passed'
        mode = 'fixture_only'
        root_entry_count = $fixture.root_entry_count
        temporary_tree = $fixtureCleanup
    }
}
else {
    [pscustomobject][ordered]@{
        status = 'passed'
        mode = 'protocol_e2e'
        pc_id = [string]$protocolResult.pc_id
        evidence_path = $evidencePath
        android_ui_executed = $false
    }
}
