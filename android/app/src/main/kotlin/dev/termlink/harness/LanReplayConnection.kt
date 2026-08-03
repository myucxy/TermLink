package dev.termlink.harness

import dev.termlink.terminal.api.TerminalEngine
import dev.termlink.terminal.replay.LanReplayFrameProcessor
import dev.termlink.terminal.replay.LanReplayProgress
import dev.termlink.terminal.replay.LanReplayReport
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.launch
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import java.io.Closeable
import java.net.URI
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

sealed interface LanConnectionUpdate {
    data object Connecting : LanConnectionUpdate
    data object Connected : LanConnectionUpdate
    data class Receiving(
        val sequence: Long,
        val eventCount: Int,
        val outputBytes: Int,
    ) : LanConnectionUpdate

    data class Verified(val report: LanReplayReport) : LanConnectionUpdate
    data class Failed(val message: String) : LanConnectionUpdate
}

class LanReplayConnection(
    address: String,
    token: String,
    engine: TerminalEngine,
    private val onUpdate: (LanConnectionUpdate) -> Unit,
) : Closeable {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val frames = Channel<ByteArray>(capacity = Channel.UNLIMITED)
    private val processor = LanReplayFrameProcessor(engine)
    private val terminalUpdateSent = AtomicBoolean(false)
    private val userClosed = AtomicBoolean(false)
    private val webSocket: WebSocket
    @Volatile private var replayComplete = false

    init {
        val replayURL = replayURL(address)
        require(token.isNotBlank()) { "测试令牌不能为空" }
        onUpdate(LanConnectionUpdate.Connecting)

        scope.launch {
            try {
                for (frame in frames) {
                    when (val progress = processor.accept(frame)) {
                        is LanReplayProgress.Started -> onUpdate(LanConnectionUpdate.Connected)
                        is LanReplayProgress.Receiving -> onUpdate(
                            LanConnectionUpdate.Receiving(
                                sequence = progress.sequence,
                                eventCount = progress.eventCount,
                                outputBytes = progress.outputBytes,
                            ),
                        )
                        is LanReplayProgress.Complete -> {
                            replayComplete = true
                            if (terminalUpdateSent.compareAndSet(false, true)) {
                                onUpdate(LanConnectionUpdate.Verified(progress.report))
                            }
                        }
                    }
                }
                if (!replayComplete && !userClosed.get()) {
                    fail("连接在 Ended 帧之前关闭")
                }
            } catch (error: Exception) {
                if (!userClosed.get()) {
                    fail(error.message ?: error::class.java.simpleName)
                }
            }
        }

        val request = Request.Builder()
            .url(replayURL)
            .header("Authorization", "Bearer $token")
            .build()
        webSocket = httpClient.newWebSocket(request, Listener())
    }

    override fun close() {
        if (!userClosed.compareAndSet(false, true)) return
        terminalUpdateSent.set(true)
        webSocket.close(NORMAL_CLOSURE, "client closed")
        frames.close()
        scope.cancel()
    }

    private fun fail(message: String) {
        if (terminalUpdateSent.compareAndSet(false, true)) {
            onUpdate(LanConnectionUpdate.Failed(message))
        }
    }

    private inner class Listener : WebSocketListener() {
        override fun onOpen(webSocket: WebSocket, response: Response) {
            onUpdate(LanConnectionUpdate.Connected)
        }

        override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
            if (!frames.trySend(bytes.toByteArray()).isSuccess) {
                fail("接收队列已经关闭")
                webSocket.close(PROTOCOL_ERROR, "receive queue closed")
            }
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            fail("LAN Replay 只允许二进制 WebSocket 帧")
            webSocket.close(PROTOCOL_ERROR, "text frame rejected")
        }

        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
            webSocket.close(code, reason)
        }

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
            frames.close()
        }

        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
            frames.close(t)
        }
    }

    private companion object {
        const val NORMAL_CLOSURE = 1000
        const val PROTOCOL_ERROR = 1002
        const val TEST_CASE = "smoke-powershell-v1"

        val httpClient: OkHttpClient = OkHttpClient.Builder()
            .connectTimeout(10, TimeUnit.SECONDS)
            .readTimeout(0, TimeUnit.MILLISECONDS)
            .pingInterval(15, TimeUnit.SECONDS)
            .build()

        fun replayURL(address: String): String {
            var normalized = address.trim()
            if (!normalized.contains("://")) normalized = "ws://$normalized"
            val uri = URI(normalized)
            require(uri.scheme == "ws" || uri.scheme == "wss") {
                "地址必须使用 ws:// 或 wss://"
            }
            require(!uri.host.isNullOrBlank()) { "Docker 地址缺少主机名或 IP" }
            require(uri.rawQuery == null && uri.rawFragment == null) {
                "Docker 地址不能包含查询参数或片段"
            }
            require(uri.path.isNullOrEmpty() || uri.path == "/") {
                "请输入 Docker 服务根地址，不要附加路径"
            }
            return normalized.trimEnd('/') + "/v1/test/replay/$TEST_CASE?timing=recorded"
        }
    }
}
