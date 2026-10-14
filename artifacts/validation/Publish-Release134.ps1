param([Parameter(Mandatory)][ValidateSet('WindowsSetup')][string]$Target,[switch]$Plan,[switch]$Resume)
$ErrorActionPreference='Stop'
Set-Location (Resolve-Path (Join-Path $PSScriptRoot '../..'))
$review=Get-Content artifacts/validation/release-134-review-state.json -Raw | ConvertFrom-Json
if($review.status -ne 'authorized_for_publication'){throw 'Publication authorization record missing'}
. ./artifacts/validation/Release122-Secrets.ps1
$credential=if($Plan){$null}else{Get-Release122Credential 'TermLink-AppShip-release-manager'}
$notes='Windows 热修复：修复升级到 0.3.4 后 Agent 在加载既有历史状态时 100% CPU 空转、无法就绪的回归（历史目录 ACL 加固改为先校验后写入，可跨重启收敛）。Android 与服务端不受影响、无需更新。'
$parameters=@{
 ManifestPath='artifacts/release/0.3.4-134/artifact-manifest.json';Target=$Target;
 BaseURL='https://appship.kget.cc:8910';Credential=$credential;
 PublishScriptPath='F:/AISession/.codex/skills/appship-publish/scripts/publish.ps1';
 Title='TermLink Windows 0.3.4 (134) 热修复';Channel='stable';ReleaseNotes=$notes;
 ExpectedWindowsSignerSha1='91AE60BC06D866628B8EB2C62F32BDE9DAB56466';
 SignatureApproved=$true;SignatureReviewNote=$review.signatureReviewNote;
 Resume=[bool]$Resume;Plan=[bool]$Plan
}
& ./scripts/Publish-AppShipProduction.ps1 @parameters
