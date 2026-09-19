# zcodeDemo — ZCode 手机控制接入演示

TermLink Win 客户端集成 ZCode 移动端远程控制的**前置验证脚本**。

## 用法

在**普通（非管理员）终端**运行：

```text
python zcode_demo.py
```

> ⚠️ 必须用非管理员终端：ZCode 的 `codex remote-control start` 刻意拒绝管理员
> 上下文（守护进程不得继承管理员令牌）。管理员终端里运行会在第 4 步被拒。

## 流程

1. 检测 `ZCode.exe` 进程（含可执行文件路径）——未运行则终止
2. 检测安装目录（进程路径 → 注册表卸载项 → 常见路径 三级回退）
3. 定位 `codex` CLI（PATH 上的 `codex.cmd`）
4. `codex remote-control start --json` 开启远程控制守护进程
   （本地套接字 `~/.codex/app-server-control/app-server-control.sock`）
5. `codex remote-control pair --json` 获取配对凭证/URL，正则提取并打印

## 2026-09-18 本机实测结果

| 步骤 | 结果 |
|---|---|
| 进程检测 | ✓ 运行中，`D:\Program Files\ZCode\ZCode.exe` |
| 安装目录 | ✓ `D:\Program Files\ZCode` |
| CLI 定位 | ✓ `D:\Dev\Env\nvm4w\nodejs\codex.cmd` |
| start | ✗ 本测试终端为管理员权限，被其安全检查拒绝（预期内） |
| pair | 级联失败（守护进程未启动，socket 未出现） |

**待一次非管理员终端的运行**确认 `pair --json` 输出形态（完整 URL 还是短配对码）。

## 逆向结论（影响集成的关键事实）

- ZCode 桌面端 = 智谱基于开源 Codex CLI 的发行版（数据目录就是 `~/.codex`）
- 远程控制链接是 **z.ai 云端中继**页面（`https://zcode.z.ai/remote/v4?sid=..&hash=..`），
  公网 HTTPS 直达，安卓 WebView 可直接加载——无需 TermLink 做隧道
- URL 即 bearer 凭证、**短时效**、不落盘——TermLink 应"现取现用"，
  服务端只存服务可用状态，不存凭证
- 开关只能通过 `codex remote-control` CLI（桌面 App 的 `zcode:*` 命令族是内部 IPC，
  未暴露深链；`zcode://` 仅路由 workspace/open 与 oauth/callback）

## 移植到 TermLink（C#）要点

- 进程检测：`Process.GetProcessesByName("ZCode")` + `MainModule.FileName`
- 安装目录：注册表 `Uninstall` 键 `InstallLocation`，同三级回退
- CLI 调用：`Process.Start` 重定向 stdout/stderr，解析 `--json` 输出
- Agent 以普通用户身份运行，天然满足 codex 的非提权要求
