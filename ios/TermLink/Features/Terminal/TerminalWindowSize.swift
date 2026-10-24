// SPDX-License-Identifier: GPL-3.0-only
import Foundation

/// Host resize error codes, with wording mirrored from Android `resizeErrorResource` (values-zh-rCN).
enum TerminalWindowSize {
    static func message(for reason: String) -> String {
        switch reason {
        case "RESIZE_UNSUPPORTED": return "请升级 Windows 客户端，并重新打开此终端会话。"
        case "RESIZE_SERVER_UPGRADE_REQUIRED": return "服务端尚未支持窗口调整，请先升级服务端。"
        case "WINDOW_RESIZE_UNSUPPORTED": return "Windows 未应用目标尺寸。请使用单标签、单窗格，退出全屏，并检查窗口大小限制。"
        case "NO_DESKTOP_SIZE": return "没有可恢复的电脑尺寸，或电脑本地调整已替代该尺寸。"
        case "CONTROL_LEASE_REJECTED", "LOCAL_INPUT_PRIORITY", "RESIZE_INTERRUPTED": return "控制权限已变化，请确认当前窗口尺寸后再试。"
        case "RESIZE_TIMEOUT": return "未收到确认，请检查窗口实际尺寸及服务端版本后再试。"
        case "RESIZE_BUSY": return "正在处理另一个窗口调整请求，请稍后再试。"
        case "RESIZE_EXPIRED", "RESIZE_EXPIRED_OR_INVALID": return "请求已过期或尺寸无效，请检查两端系统时间及网络后重试。"
        default: return "无法调整此会话窗口，请检查连接后重试。"
        }
    }
}
