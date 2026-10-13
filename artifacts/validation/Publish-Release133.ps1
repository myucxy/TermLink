param([Parameter(Mandatory)][ValidateSet('AndroidApk','WindowsSetup')][string]$Target,[switch]$Plan,[switch]$Resume)
$ErrorActionPreference='Stop'
Set-Location (Resolve-Path (Join-Path $PSScriptRoot '../..'))
$review=Get-Content artifacts/validation/release-133-review-state.json -Raw | ConvertFrom-Json
if($review.status -ne 'authorized_for_publication'){throw 'Publication authorization record missing'}
. ./artifacts/validation/Release122-Secrets.ps1
$credential=if($Plan){$null}else{Get-Release122Credential 'TermLink-AppShip-release-manager'}
$label=if($Target -eq 'AndroidApk'){'Android'}else{'Windows'}
$notes=if($Target -eq 'AndroidApk'){
 '落地 22 组终端能力计划的首批能力：冻结历史浏览、状态增量恢复、Kitty 图形与 durable 恢复、Broker 捕获主路径、Shell 命令定位入口、终端语义探针与输入诊断。修复冻结会话打开超时（open readiness 仅依赖 last marker）；Realtime 诊断增加失败原因。已通过基础协议、扩展协议、Delta 恢复、滚轮、Effects 主路径与 Frozen 打开链路联测（r388-r426）。已知限制：冻结浏览在 Host 失联切换瞬间的恢复提示、Shell 命令定位失败提示、Kitty shared 阶段与 WAL 故障恢复为后续优化；发布说明不宣传上述未完全验收能力。'
}else{
 '同步 22 组终端能力首批：原生显示协议、冻结历史与状态增量服务、Broker 捕获、ConPTY 输入转码、PowerShell Shell Integration、私有历史目录加固与冻结基线保留。沿用既有发布者证书；服务端容器与本容器同源构建（无 Cosign 签名，沿用 0.3.0 先例）。'
}
$parameters=@{
 ManifestPath='artifacts/release/0.3.4-133/artifact-manifest.json';Target=$Target;
 BaseURL='https://appship.kget.cc:8910';Credential=$credential;
 PublishScriptPath='F:/AISession/.codex/skills/appship-publish/scripts/publish.ps1';
 Title="TermLink $label 0.3.4 (133)";Channel='stable';ReleaseNotes=$notes;
 AndroidSdkPath='D:/Dev/Env/Android/Sdk';
 ExpectedAndroidSignerSha256='f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b';
 ExpectedWindowsSignerSha1='91AE60BC06D866628B8EB2C62F32BDE9DAB56466';
 SignatureApproved=$true;SignatureReviewNote=$review.signatureReviewNote;
 Resume=[bool]$Resume;Plan=[bool]$Plan
}
& ./scripts/Publish-AppShipProduction.ps1 @parameters
