#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ZCode 手机控制接入演示 (TermLink Win 客户端集成前置验证)

流程:
  1. 检测 ZCode 桌面进程 (ZCode.exe) 与安装目录
  2. ZCode 已运行时, 定位 codex CLI
  3. codex remote-control start   -- 自动开启远程控制守护进程
  4. codex remote-control pair    -- 获取控制 URL 并打印

已知约束 (2026-09-18 逆向结论):
  - `start` 拒绝在管理员提权上下文运行 (守护进程不得继承管理员令牌)
  - URL 不落盘, 只能通过 `pair` 实时获取; 配对凭证是短时效的
"""

import json
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

# 任务计划/GBK 控制台下 print Unicode 符号会崩, 统一 stdout 为 UTF-8
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

CODEX_HOME = Path.home() / ".codex"
DAEMON_DIR = CODEX_HOME / "app-server-control"
DAEMON_SOCK = DAEMON_DIR / "app-server-control.sock"
URL_PATTERN = re.compile(r"https://[^\s\"']*remote/v4[^\s\"']*")
ANY_URL_PATTERN = re.compile(r"https?://[^\s\"'<>()]+")


def run(cmd, timeout=40):
    """执行命令, 返回 (returncode, stdout, stderr)。cmd 为列表, 首元素须为可执行文件全路径。"""
    try:
        p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout,
                           errors="replace", shell=False)
        return p.returncode, p.stdout or "", p.stderr or ""
    except subprocess.TimeoutExpired:
        return 124, "", "命令超时"
    except FileNotFoundError as e:
        return 127, "", f"找不到可执行文件: {e}"


def is_elevated():
    try:
        import ctypes
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except Exception:
        return False


def detect_zcode_process():
    """返回 (是否运行, 可执行文件路径或 None)。"""
    rc, out, _ = run([
        "powershell", "-NoProfile", "-Command",
        "Get-Process ZCode -ErrorAction SilentlyContinue | "
        "Select-Object -First 1 -ExpandProperty Path",
    ], timeout=20)
    exe = out.strip()
    if rc == 0 and exe and Path(exe).exists():
        return True, exe
    # 进程路径读不到 (权限), 退回 tasklist 只判存在性
    rc, out, _ = run(["tasklist", "/FI", "IMAGENAME eq ZCode.exe"], timeout=15)
    return (rc == 0 and "ZCode.exe" in out), None


def detect_install_dir(process_path):
    """按优先级: 进程路径 → 注册表卸载项 → 常见路径。"""
    candidates = []
    if process_path:
        candidates.append(Path(process_path).parent)
    try:
        import winreg
        for root in (winreg.HKEY_LOCAL_MACHINE, winreg.HKEY_CURRENT_USER):
            for wow in (0, winreg.KEY_WOW64_64KEY, winreg.KEY_WOW64_32KEY):
                try:
                    key = winreg.OpenKey(root,
                                         r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                                         0, winreg.KEY_READ | wow)
                except OSError:
                    continue
                with key:
                    i = 0
                    while True:
                        try:
                            sub = winreg.EnumKey(key, i)
                        except OSError:
                            break
                        i += 1
                        try:
                            with winreg.OpenKey(key, sub) as s:
                                name, _ = winreg.QueryValueEx(s, "DisplayName")
                                if "zcode" in str(name).lower():
                                    loc, _ = winreg.QueryValueEx(s, "InstallLocation")
                                    if loc:
                                        candidates.append(Path(loc))
                        except OSError:
                            continue
    except Exception:
        pass
    for d in (r"D:\Program Files\ZCode", r"C:\Program Files\ZCode"):
        candidates.append(Path(d))
    for c in candidates:
        if c.exists() and (c / "ZCode.exe").exists():
            return c
    return candidates[0] if candidates else None


def find_codex_cli():
    """codex.cmd (npm 全局垫片) 优先; 返回全路径或 None。"""
    for name in ("codex.cmd", "codex"):
        p = shutil.which(name)
        if p:
            return p
    return None


STANDALONE_ROOT = CODEX_HOME / "packages" / "standalone" / "current"


def ensure_standalone_install(codex_cmd):
    """补齐 daemon 要求的固定路径布局:
        ~/.codex/packages/standalone/current/codex-package.json
        ~/.codex/packages/standalone/current/bin/codex.exe
    `remote-control start` 只认这个路径 (守护进程从它启动/自更新), npm 安装
    不会被自动识别。这里从本机 npm 包的 vendor 二进制自举补齐, 零网络依赖。
    返回 (ok, message)。
    """
    exe = STANDALONE_ROOT / "bin" / "codex.exe"
    manifest = STANDALONE_ROOT / "codex-package.json"
    if exe.exists() and manifest.exists():
        return True, "独立安装布局已存在"

    # 版本号取自本机 codex 自报 (codex-cli 0.154.0 → 0.154.0)
    rc, out, _ = run([codex_cmd, "--version"], timeout=20)
    m = re.search(r"(\d+\.\d+\.\d+)", out or "")
    version = m.group(1) if m else "0.0.0"

    # 从 codex.cmd 所在前缀目录定位 npm vendor 二进制
    shim_dir = Path(codex_cmd).resolve().parent
    vendor_exe = None
    for base in (shim_dir, *shim_dir.parents[:3]):
        for cand in base.glob(
            "node_modules/@openai/codex/node_modules/@openai/"
            "codex-win32-x64/vendor/*/bin/codex.exe"
        ):
            vendor_exe = cand
            break
        if vendor_exe:
            break
    if not vendor_exe:
        return False, (
            "未找到 npm 包内的 codex.exe。备选: 在 PowerShell 执行官方安装器\n"
            "        irm https://chatgpt.com/codex/install.ps1 | iex"
        )

    try:
        (STANDALONE_ROOT / "bin").mkdir(parents=True, exist_ok=True)
        (STANDALONE_ROOT / "codex-resources").mkdir(exist_ok=True)
        shutil.copy2(vendor_exe, exe)
        host_src = vendor_exe.parent / "codex-code-mode-host.exe"
        if host_src.exists():
            shutil.copy2(host_src, STANDALONE_ROOT / "bin" / "codex-code-mode-host.exe")
        manifest.write_text(json.dumps({"version": version}), encoding="utf-8")
        return True, f"已自举独立安装布局 (version={version}, 源={vendor_exe})"
    except OSError as e:
        return False, f"补齐布局失败: {e}"


def wait_for_daemon(seconds=10):
    deadline = time.time() + seconds
    while time.time() < deadline:
        if DAEMON_SOCK.exists():
            return True
        time.sleep(1)
    return DAEMON_SOCK.exists()


def extract_urls(payload):
    """从任意 JSON/文本里捞 URL 与疑似配对码, 输出 schema 未知时的兜底。"""
    urls = sorted(set(URL_PATTERN.findall(payload)) or set(ANY_URL_PATTERN.findall(payload)))
    return urls


def print_url_analysis(url):
    """解析并展示控制 URL 的构成 (sid/hash 是 bearer 凭证, 短时效)。"""
    print("手机控制 URL:")
    print(f"  {url}")
    from urllib.parse import parse_qs, urlparse
    q = parse_qs(urlparse(url).query)
    for k in ("sid", "hash", "t", "mid", "name", "app_version"):
        if k in q:
            print(f"    {k:12}= {q[k][0]}")
    ts = q.get("t", [None])[0]
    if ts and ts.isdigit():
        print(f"    {'签发时间':12}= {time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(int(ts) / 1000))}")


def main():
    # 兜底模式: 从剪贴板或命令行参数读取桌面 App 复制的控制 URL
    # (CLI 守护进程路径不可用时的粘贴路径)。URL 含 &, 剪贴板读取最稳妥。
    arg = sys.argv[1] if len(sys.argv) > 1 else ""
    url = None
    if arg == "--clip":
        rc, out, _ = run(["powershell", "-NoProfile", "-Command",
                          "Get-Clipboard"], timeout=20)
        url = out.strip()
        if not url.startswith("http"):
            print("剪贴板里没有 URL。请先在 ZCode 桌面端复制手机控制链接。")
            return 1
    elif arg.startswith("http"):
        url = arg.strip()

    if url:
        print_url_analysis(url)
        return 0

    print("=" * 64)
    print("ZCode 手机控制接入演示")
    print("=" * 64)

    # ── 1. 进程与安装目录检测 ──────────────────────────────────
    running, exe = detect_zcode_process()
    if not running:
        print("\n[1] ZCode 进程: 未检测到 ZCode.exe 运行 —— 按需求终止。")
        print("    请先启动 ZCode 桌面端后重试。")
        return 1
    print(f"\n[1] ZCode 进程: 运行中 ✓  ({exe or '路径不可读, 跳过'})")

    install_dir = detect_install_dir(exe)
    print(f"[2] 安装目录:   {install_dir or '未找到'}")

    # ── 2. codex CLI 定位 ─────────────────────────────────────
    codex = find_codex_cli()
    if not codex:
        print("\n[3] codex CLI:  未在 PATH 找到 codex / codex.cmd —— 无法继续。")
        print("    请确认 npm 全局包 @openai/codex 已安装, 或将其路径加入 PATH。")
        return 1
    print(f"[3] codex CLI:  {codex}")

    ok, msg = ensure_standalone_install(codex)
    print(f"[3b] 独立安装布局: {'✓' if ok else '✗'} {msg}")
    if not ok:
        return 1

    elevated = is_elevated()
    if elevated:
        print("[!] 当前终端具有管理员权限 —— `remote-control start` 会拒绝执行")
        print("    (守护进程不得继承管理员令牌)。若下方 start 失败, 请在")
        print("    普通 (非管理员) 终端重新运行本脚本。")

    # ── 3. 开启远程控制守护进程 ────────────────────────────────
    print("\n[4] 开启远程控制: codex remote-control start --json")
    rc, out, err = run([codex, "remote-control", "start", "--json"], timeout=60)
    combined = (out + err).strip()
    if "already" in combined.lower() and "run" in combined.lower():
        print("    守护进程已在运行 ✓")
    elif "must not inherit administrator" in combined or "elevat" in combined.lower():
        print("    ✗ 被拒绝: 守护进程禁止在管理员上下文启动。")
        print("    解决: 在普通 (非管理员) 终端运行本脚本, 或手动执行:")
        print("          codex remote-control start")
    elif rc != 0:
        print(f"    ✗ 失败 (rc={rc}): {combined[:400]}")
    else:
        print(f"    ✓ {combined[:300] or '(无输出)'}")

    sock = wait_for_daemon(8)
    print(f"    守护进程套接字: {DAEMON_SOCK} → {'存在' if sock else '未出现'}")

    # ── 4. 获取控制 URL ───────────────────────────────────────
    print("\n[5] 获取控制 URL: codex remote-control pair --json")
    rc, out, err = run([codex, "remote-control", "pair", "--json"], timeout=30)
    payload = (out + err).strip()
    if rc != 0 and not payload:
        print(f"    ✗ 失败 (rc={rc})")
        return 1
    print(f"    原始输出: {payload[:600]}")

    parsed = None
    try:
        parsed = json.loads(out)
    except Exception:
        pass
    if parsed is not None:
        print(f"    JSON 结构: {json.dumps(parsed, ensure_ascii=False, indent=2)[:800]}")

    urls = extract_urls(payload)
    if urls:
        print("\n" + "=" * 64)
        print("手机控制 URL:")
        for u in urls:
            print(f"  {u}")
        print("=" * 64)
    else:
        print("\n    (输出中未发现完整 URL —— 可能是短配对码, 需兑换步骤;"
              "\n     原始输出已打印在上方供分析)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
