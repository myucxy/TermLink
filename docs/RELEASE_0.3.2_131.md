# TermLink Windows 0.3.2 / 131 发布记录

## 范围与版本

用户要求发布 Windows 更新启动优化并保持版本号。本轮保留产品版本名 **0.3.2**，
将内部 build 从 130 递增至 **131**；AppShip 按 build 判断升级，已发布的 130 制品保持不可变。
仅发布 Windows x64 Setup。Android 仍为 0.3.2/130；Server 与 Docker 不涉及。

制品源码为已提交并推送的 `5f3ffc8294afe236926b84502e343ec3c123ec30`，工作树干净。
2026-10-04 15:08:24 UTC 现场核验：Windows stable 0.3.2/130 published/ready，nextBuildNumber=131；
origin/main 与此源码一致。此后发布记录提交不改变构建来源。

## 改动和验收边界

- 客户端在完整性、发布者签名复核及安装确认之后，优先直接启动 EXE，启动失败再由 ShellExecute 分派。
- ShellExecute 成功但不返回进程句柄时不再报失败；哈希与签名校验继续保留。
- 界面区分更新验证、元数据和安装器启动错误。

Windows adapter 验证与 Avalonia headless 验证通过，桌面构建 0 warning / 0 error。
已发布 130 的真实下载、哈希、签名、缓存复核和篡改拒绝此前通过。
安装生命周期对本轮源码单独验证，DEV 构建沿用当前已发布 0.3.2/130。

HT-LPC-1326 的准确原始异常尚未取得；独立下载的安装包可安装不能证明其自动更新失败的唯一根因。
本轮交付启动兼容改进，不宣称该电脑已实测修复，也没有改变自签名信任策略。
旧客户端执行的首次升级仍可能需要用户手动运行新包；本轮未部署本机或远端客户端。
仅当前 Relay 使用范围，网络/会话协议未改；LAN/NAT 不适用。

## 精确制品

- 文件：`artifacts/release/0.3.2-131/TermLink-Setup-0.3.2.131-x64.exe`
- 大小：182639104 字节
- SHA-256：`4f8baaf21b1bfa219600039aaa31ee275eacb78715e4a3e75243741b2e43a549`
- FileVersion：0.3.2.131
- ProductVersion：`0.3.2.131+5f3ffc8294afe236926b84502e343ec3c123ec30`
- Authenticode signer SHA-1：`91AE60BC06D866628B8EB2C62F32BDE9DAB56466`
- 独立签名校验 Valid，17 个签名载荷通过，RFC 3161 时间戳通过。
- 沿用既有自签发布者，不代表公开 CA 信任。

构建日志：`artifacts/validation/release-131-win-build.log`。
独立校验：`artifacts/validation/release-131-win-verification.json`。
安装生命周期：`artifacts/validation/release-131-setup-lifecycle.json`。

## 发布状态与回滚

当前为本地生产制品，上传前记录。用户已明确授权发布当前 Windows 修复并沿用版本名；
使用既有批准的发布者签名，机器验证与用户发布授权分别记录，不把机器校验冒充用户逐项核对哈希。
上传沿用仓库包装器与 appship-publish 技能，失败保留幂等状态，重试不重新加号。
Windows DEV 基线仅在 published/ready 与公开更新接口核验成功后改为 0.3.2/131。

保留 Windows 0.3.2/130 的原制品与历史发布；不删除数据降级，不覆盖已发布哈希。
若需回退，通过受控安装或更高 build 发布已知良好源码。AppShip 发布完成不等于客户端已安装。
