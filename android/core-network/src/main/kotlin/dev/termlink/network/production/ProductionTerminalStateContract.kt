package dev.termlink.network.production

import dev.termlink.protocol.realtime.v1.TerminalAttachResultPayload

/** The profile selected by an authenticated Windows attachment, never by history metadata. */
enum class ProductionTerminalProfile(
    val state: String,
    internal val maxHostHistoryObjectBytes: Int,
    val widthPolicy: Int,
) {
    LEGACY("terminal_state_2_21_v2", 64 * 1024 * 1024, 1),
    GRAPHICS("terminal_state_2_22_v1", 128 * 1024 * 1024 + 4096, 1),
    GRAPHICS_UNICODE16("terminal_state_2_22_v2", 128 * 1024 * 1024 + 4096, 2);

    val isGraphics: Boolean get() = this != LEGACY
    val stateContract: String get() = state
    val outputProfile: Int get() = 0
    val resourceProfile: Int get() = 2

    internal val maxHostHistoryChunks: Int
        get() = (maxHostHistoryObjectBytes + HOST_HISTORY_CHUNK_BYTES - 1) / HOST_HISTORY_CHUNK_BYTES

    internal fun hostHistoryChunkCount(objectBytes: Long): Int {
        require(objectBytes in 1..maxHostHistoryObjectBytes.toLong()) { "Host history object size is invalid" }
        return ((objectBytes + HOST_HISTORY_CHUNK_BYTES - 1) / HOST_HISTORY_CHUNK_BYTES).toInt()
    }
}

internal const val HOST_HISTORY_CHUNK_BYTES = 512 * 1024

/** A decoder contract; this never selects or changes a running session's width profile. */
object ProductionTerminalStateContract {
    const val STATE = "terminal_state_2_21_v2"
    const val GRAPHICS_STATE = "terminal_state_2_22_v1"
    const val GRAPHICS_UNICODE16_STATE = "terminal_state_2_22_v2"
    val SUPPORTED_STATES: List<String> = listOf(GRAPHICS_UNICODE16_STATE, GRAPHICS_STATE, STATE)
    const val ATTACH = "terminal_attach_v1"
    const val THEME = "terminal_host_theme_v1"
    const val HOST_RESIZE = "terminal_host_resize_v1"
    const val GEOMETRY = "terminal_host_geometry_v1"

    internal fun requireRelay(negotiated: ProductionRealtimeNegotiatedCapabilities, generation: Int) {
        if (generation <= 0 || negotiated.generation != generation ||
            ATTACH !in negotiated.capabilities || GEOMETRY !in negotiated.capabilities || THEME !in negotiated.capabilities || HOST_RESIZE !in negotiated.capabilities
        ) throw TerminalCompatibilityException("TERMINAL_RELAY_UPGRADE_REQUIRED")
    }

    internal fun requireAccepted(result: TerminalAttachResultPayload): ProductionTerminalProfile {
        if (result.requiredState.length > 64 || result.errorCode.length > 64 ||
            result.errorCode.any { it !in 'A'..'Z' && it !in '0'..'9' && it != '_' })
            throw TerminalCompatibilityException("TERMINAL_CAPABILITIES_INVALID")
        if (result.errorCode.isNotEmpty()) throw TerminalCompatibilityException(result.errorCode)
        return ProductionTerminalProfile.entries.firstOrNull { it.state == result.requiredState }
            ?: throw TerminalCompatibilityException("TERMINAL_STATE_UNSUPPORTED")
    }
}

class TerminalCompatibilityException(val code: String) : IllegalStateException(
    when (code) {
        "TERMINAL_RELAY_UPGRADE_REQUIRED" -> "请升级服务端后重新打开会话 / Upgrade the server ($code)"
        "TERMINAL_HOST_UPGRADE_REQUIRED" -> "Windows 未协商终端状态就发送了输出，请升级 Windows 客户端 / Windows sent output before terminal negotiation ($code)"
        "TERMINAL_LOCAL_PROTOCOL_MISMATCH" -> "Windows 会话进程与 Agent 终端协议不一致，请更新后新建会话 / SessionHost and Agent terminal protocols differ ($code)"
        "TERMINAL_HISTORY_PROFILE_MISSING" -> "历史缺少终端协议标记，已保留原文件并停止重放，请新建会话 / Preserved history has no terminal profile ($code)"
        "TERMINAL_HISTORY_PROFILE_INVALID" -> "历史终端协议标记校验失败，已保留原文件并停止重放，请新建会话 / Preserved history has an invalid terminal profile ($code)"
        "TERMINAL_HISTORY_PROFILE_MISMATCH" -> "历史终端协议与当前版本不兼容，已保留原文件并停止重放，请新建会话 / Preserved history uses an incompatible terminal profile ($code)"
        "TERMINAL_NEGOTIATION_TIMEOUT" -> "等待 Windows 确认终端协议超时，请检查连接及客户端版本 / Terminal negotiation timed out ($code)"
        "TERMINAL_STATE_MISMATCH" -> "会话终端协议已改变，请重新打开会话 / Terminal profile changed; reopen the session ($code)"
        "TERMINAL_CLIENT_UPGRADE_REQUIRED", "TERMINAL_STATE_UNSUPPORTED" ->
            "此会话需要新版终端能力，请升级安卓客户端 / Upgrade Android for this terminal state ($code)"
        else -> "终端能力协商失败 / Terminal capability negotiation failed ($code)"
    },
)
