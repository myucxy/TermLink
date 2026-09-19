#!/usr/bin/env python3
"""模拟器联合测试驱动: 清空字段 → 登录 → 报告结果。"""
import re
import subprocess
import sys
import time

ADB = r"D:\Dev\Env\Android\Sdk\platform-tools\adb.exe"
PKG = "dev.termlink.harness"
USER, PWD = "admin", "TermLinkTest2026"


def adb(*args, timeout=30):
    p = subprocess.run([ADB, "-e", *args], capture_output=True, text=True,
                       timeout=timeout, errors="replace")
    return p.stdout or ""


def dump():
    adb("shell", "uiautomator", "dump", "/data/local/tmp/drive.xml")
    adb("pull", "/data/local/tmp/drive.xml", "C:\\Temp\\drive.xml")
    try:
        return open(r"C:\Temp\drive.xml", encoding="utf-8").read()
    except OSError:
        return ""


def bounds_center(xml, pattern):
    m = re.search(pattern + r'[^>]*bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"', xml)
    if not m:
        return None
    l, t, r, b = map(int, m.groups())
    return (l + r) // 2, (t + b) // 2


def texts(xml):
    return sorted(set(re.findall(r'text="([^"]{1,40})"', xml)))


def main():
    subprocess.run([ADB, "-e", "shell", "am", "force-stop", PKG], timeout=20)
    adb("shell", "monkey", "-p", PKG, "-c", "android.intent.category.LAUNCHER", "1")
    time.sleep(6)
    xml = dump()
    later = bounds_center(xml, r'text="Later"')
    if later:
        adb("shell", "input", "tap", *map(str, later))
        time.sleep(3)
        xml = dump()

    user_field = bounds_center(xml, r'class="android.widget.EditText"')
    if not user_field:
        print("ERROR: 找不到用户名输入框; 当前文本:", texts(xml))
        return 1
    adb("shell", "input", "tap", *map(str, user_field))
    time.sleep(1)
    adb("shell", "input", "keyevent", "123")
    adb("shell", "input", "keyevent", *["67"] * 45)
    time.sleep(1)
    adb("shell", "input", "text", USER)
    time.sleep(1)

    xml = dump()
    pass_field = bounds_center(xml, r'class="android.widget.EditText"[^>]*password="true"')
    if not pass_field:
        adb("shell", "input", "keyevent", "111")
        time.sleep(1)
        xml = dump()
        pass_field = bounds_center(xml, r'class="android.widget.EditText"[^>]*password="true"')
    if not pass_field:
        print("ERROR: 找不到密码输入框; 当前文本:", texts(xml))
        return 1
    adb("shell", "input", "tap", *map(str, pass_field))
    time.sleep(1)
    adb("shell", "input", "keyevent", "123")
    adb("shell", "input", "keyevent", *["67"] * 45)
    time.sleep(1)
    adb("shell", "input", "text", PWD)
    time.sleep(1)

    adb("shell", "input", "keyevent", "111")
    time.sleep(1)
    xml = dump()
    signin = bounds_center(xml, r'text="Sign in"')
    if not signin:
        print("ERROR: 找不到 Sign in 按钮; 当前文本:", texts(xml))
        return 1
    adb("shell", "input", "tap", *map(str, signin))
    time.sleep(12)
    xml = dump()
    print("登录后界面文本:")
    for t in texts(xml):
        print("  ", t)
    if "Sign in" in xml and "Username or email" in xml:
        print("RESULT: 仍在登录页 (登录可能失败)")
        return 1
    print("RESULT: 已离开登录页")
    return 0


if __name__ == "__main__":
    sys.exit(main())
