package dev.termlink.network.testrelay

import dev.termlink.session.EventAcceptance
import dev.termlink.session.RemoteInput
import dev.termlink.session.RemoteLifecycle
import dev.termlink.session.RemoteSessionEvent
import dev.termlink.session.RemoteSessionOutbound
import dev.termlink.session.RemoteSessionState
import dev.termlink.session.RemoteTerminalSession
import dev.termlink.terminal.api.TerminalEngine
import dev.termlink.terminal.api.TerminalSize
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeout
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import java.io.Closeable
import java.net.URI
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

enum class Phase1TestScenario(val queryValue: String) {
    STEADY("steady"),
    DISCONNECT_ONCE("disconnect-once"),
    HISTORY_GAP("history-gap"),
    SLOW_CONSUMER("slow-consumer"),
    EXITED("exited"),
}

data class Phase1TestSessionConfig(
    val rootAddress: String,
    val token: String,
    val caseName: String = "smoke-powershell-v1",
    val scenario: Phase1TestScenario = Phase1TestScenario.DISCONNECT_ONCE,
) {
    init {
        require(token.isNotBlank()) { "test token is required" }
        require(CASE_NAME.matches(caseName)) { "invalid phase 1 fixture case name" }
    }

    internal fun webSocketUrl(): String {
        var normalized = rootAddress.trim()
        if (!normalized.contains("://")) normalized = "ws://$normalized"
        val uri = URI(normalized)
        require(uri.scheme == "ws" || uri.scheme == "wss") { "address must use ws:// or wss://" }
        require(!uri.host.isNullOrBlank()) { "address is missing a host" }
        require(uri.rawQuery == null && uri.rawFragment == null) { "address cannot contain query or fragment" }
        require(uri.path.isNullOrEmpty() || uri.path == "/") { "enter the test Relay root address" }
        return normalized.trimEnd('/') + "/v1/test/session/$caseName?scenario=${scenario.queryValue}"
    }

    private companion object {
        val CASE_NAME = Regex("^[a-z0-9][a-z0-9._-]{0,127}$")
    }
}

sealed interface Phase1TransportState {
    data object Connecting : Phase1TransportState
    data object Connected : Phase1TransportState
    data class Reconnecting(val attempt: Int, val reason: String?) : Phase1TransportState
    data class Failed(val reason: String) : Phase1TransportState
    data object Closed : Phase1TransportState
}

data class AcceptedInputStatus(
    val count: Int = 0,
    val lastInputId: UUID? = null,
)

class Phase1TestSessionClient(
    engine: TerminalEngine,
    val config: Phase1TestSessionConfig,
    parentScope: CoroutineScope,
    private val httpClient: OkHttpClient = defaultHttpClient,
    private val inputAcceptanceTimeoutMillis: Long = 5_000,
    private val reconnectDelaysMillis: List<Long> = listOf(100, 250, 500, 1_000, 2_000),
    disposeEngineOnClose: Boolean = true,
) : RemoteSessionOutbound, Closeable {
    private val clientJob = SupervisorJob(parentScope.coroutineContext[Job])
    private val scope = CoroutineScope(parentScope.coroutineContext + clientJob)
    private val closed = AtomicBoolean(false)
    private val started = AtomicBoolean(false)
    private val generation = AtomicInteger(0)
    private val updates = Channel<SocketUpdate>(capacity = Channel.UNLIMITED)
    private val pendingInputs = ConcurrentHashMap<UUID, CompletableDeferred<Unit>>()
    private val mutableTransportState = MutableStateFlow<Phase1TransportState>(Phase1TransportState.Connecting)
    private val mutableAcceptedInputs = MutableStateFlow(AcceptedInputStatus())
    private val mutableFlowControlWindow = MutableStateFlow<Int?>(null)
    private var reconnectAttempt = 0
    private var reconnectJob: Job? = null
    private var activeSocket: WebSocket? = null
    private var disconnectedGeneration = 0

    val session = RemoteTerminalSession(
        engine = engine,
        outbound = this,
        parentScope = scope,
        disposeEngineOnClose = disposeEngineOnClose,
    )
    val transportState: StateFlow<Phase1TransportState> = mutableTransportState.asStateFlow()
    val acceptedInputs: StateFlow<AcceptedInputStatus> = mutableAcceptedInputs.asStateFlow()
    val flowControlWindow: StateFlow<Int?> = mutableFlowControlWindow.asStateFlow()
    val sessionState: StateFlow<RemoteSessionState> = session.state

    init {
        config.webSocketUrl()
        require(inputAcceptanceTimeoutMillis > 0) { "input acceptance timeout must be positive" }
        require(reconnectDelaysMillis.isNotEmpty() && reconnectDelaysMillis.all { it >= 0 }) {
            "at least one non-negative reconnect delay is required"
        }
    }

    fun start() {
        check(!closed.get()) { "phase 1 test session client is closed" }
        check(started.compareAndSet(false, true)) { "phase 1 test session client is already started" }
        session.start()
        scope.launch {
            for (update in updates) process(update)
        }
        connect()
    }

    suspend fun requestControl() {
        check(session.state.value !is RemoteSessionState.Exited) { "session has exited" }
        sendFrame(Phase1TestFrame(type = Phase1FrameType.LEASE_REQUEST))
    }

    suspend fun releaseControl() {
        val current = session.state.value as? RemoteSessionState.Controlling ?: return
        sendFrame(
            Phase1TestFrame(
                type = Phase1FrameType.LEASE_RELEASE,
                payload = current.leaseId.encodeToByteArray(),
            ),
        )
    }

    override suspend fun resume(lastAckedSequence: Long) {
        sendFrame(
            Phase1TestFrame(
                type = Phase1FrameType.RESUME,
                acknowledgement = lastAckedSequence,
            ),
        )
    }

    override suspend fun acknowledge(highestContiguousSequence: Long) {
        sendFrame(
            Phase1TestFrame(
                type = Phase1FrameType.CUMULATIVE_ACK,
                acknowledgement = highestContiguousSequence,
            ),
        )
    }

    override suspend fun sendInput(input: RemoteInput) {
        val accepted = CompletableDeferred<Unit>()
        check(pendingInputs.putIfAbsent(input.inputId, accepted) == null) { "input ID is already pending" }
        try {
            sendFrame(
                Phase1TestFrame(
                    type = Phase1FrameType.INPUT,
                    payload = uuidBytes(input.inputId) + input.bytes,
                ),
            )
            withTimeout(inputAcceptanceTimeoutMillis) { accepted.await() }
        } catch (error: CancellationException) {
            throw error
        } catch (error: Exception) {
            activeSocket?.cancel()
            throw error
        } finally {
            pendingInputs.remove(input.inputId, accepted)
        }
    }

    override suspend fun sendResize(size: TerminalSize) {
        sendFrame(
            Phase1TestFrame(
                type = Phase1FrameType.RESIZE_REQUEST,
                payload = uint16Pair(size.columns, size.rows),
            ),
        )
    }

    override fun close() {
        if (!closed.compareAndSet(false, true)) return
        reconnectJob?.cancel()
        pendingInputs.values.forEach { it.completeExceptionally(TestTransportException("connection closed")) }
        pendingInputs.clear()
        activeSocket?.close(NORMAL_CLOSURE, "test client closed")
        activeSocket = null
        mutableTransportState.value = Phase1TransportState.Closed
        updates.close()
        session.close()
        scope.cancel()
    }

    private fun connect() {
        if (closed.get()) return
        val currentGeneration = generation.incrementAndGet()
        mutableTransportState.value = if (reconnectAttempt == 0) {
            Phase1TransportState.Connecting
        } else {
            Phase1TransportState.Reconnecting(reconnectAttempt, null)
        }
        val request = Request.Builder()
            .url(config.webSocketUrl())
            .header("Authorization", "Bearer ${config.token}")
            .build()
        activeSocket = httpClient.newWebSocket(request, Listener(currentGeneration))
    }

    private suspend fun process(update: SocketUpdate) {
        if (closed.get() || update.generation != generation.get()) return
        when (update) {
            is SocketUpdate.Open -> {
                activeSocket = update.socket
                disconnectedGeneration = 0
                reconnectJob?.cancel()
                reconnectAttempt = 0
                mutableTransportState.value = Phase1TransportState.Connected
                if (!session.onConnected()) {
                    activeSocket?.close(NORMAL_CLOSURE, "session exited")
                }
            }
            is SocketUpdate.Binary -> try {
                handleServerFrame(Phase1TestFrameCodec.decode(update.bytes))
            } catch (error: Exception) {
                mutableTransportState.value = Phase1TransportState.Failed(
                    error.message ?: "invalid test Relay frame",
                )
                activeSocket?.close(PROTOCOL_ERROR, "invalid phase 1 test frame")
            }
            is SocketUpdate.Disconnected -> handleDisconnect(update.generation, update.reason)
        }
    }

    private suspend fun handleServerFrame(frame: Phase1TestFrame) {
        when (frame.type) {
            Phase1FrameType.WELCOME -> {
                requireControlFrame(frame)
                require(decodeUtf8(frame.payload) == config.caseName) { "test Relay fixture mismatch" }
                session.markReadOnly("control lease required")
            }
            Phase1FrameType.OUTPUT -> {
                requireServerEvent(frame)
                require(frame.payload.isNotEmpty()) { "empty output event" }
                requireAccepted(session.accept(RemoteSessionEvent.Output(frame.sequence, frame.payload)))
            }
            Phase1FrameType.RESIZE -> {
                requireServerEvent(frame)
                require(frame.payload.size == 4) { "invalid resize payload" }
                val payload = ByteBuffer.wrap(frame.payload).order(ByteOrder.BIG_ENDIAN)
                requireAccepted(
                    session.accept(
                        RemoteSessionEvent.Resize(
                            frame.sequence,
                            TerminalSize(payload.short.toInt() and 0xffff, payload.short.toInt() and 0xffff),
                        ),
                    ),
                )
            }
            Phase1FrameType.LIFECYCLE -> {
                requireServerEvent(frame)
                require(frame.payload.size == 5) { "invalid lifecycle payload" }
                val exitCode = ByteBuffer.wrap(frame.payload, 1, 4).order(ByteOrder.BIG_ENDIAN).int
                val lifecycle = when (frame.payload[0].toInt() and 0xff) {
                    Phase1Lifecycle.RUNNING -> RemoteLifecycle.RUNNING
                    Phase1Lifecycle.INTERRUPTED -> RemoteLifecycle.INTERRUPTED
                    Phase1Lifecycle.ENDED -> RemoteLifecycle.ENDED
                    else -> error("unknown phase 1 lifecycle state")
                }
                requireAccepted(
                    session.accept(RemoteSessionEvent.Lifecycle(frame.sequence, lifecycle, exitCode)),
                )
            }
            Phase1FrameType.GAP -> {
                requireServerEvent(frame)
                require(frame.payload.size == 16) { "invalid gap payload" }
                val payload = ByteBuffer.wrap(frame.payload).order(ByteOrder.BIG_ENDIAN)
                val acceptance = session.accept(
                    RemoteSessionEvent.Gap(frame.sequence, payload.long, payload.long),
                )
                require(acceptance is EventAcceptance.HistoryGap) { "gap frame did not enter History Gap" }
            }
            Phase1FrameType.INPUT_ACCEPTED -> {
                requireControlFrame(frame)
                val inputId = decodeUuid(frame.payload)
                val pending = pendingInputs.remove(inputId)
                    ?: error("InputAccepted does not match a pending input")
                pending.complete(Unit)
                mutableAcceptedInputs.value = AcceptedInputStatus(
                    count = mutableAcceptedInputs.value.count + 1,
                    lastInputId = inputId,
                )
            }
            Phase1FrameType.LEASE_GRANTED -> {
                requireControlFrame(frame)
                require(frame.payload.size > 8) { "invalid LeaseGranted payload" }
                val expiresAt = ByteBuffer.wrap(frame.payload, 0, 8).order(ByteOrder.BIG_ENDIAN).long
                val leaseId = decodeUtf8(frame.payload.copyOfRange(8, frame.payload.size))
                session.grantControl(leaseId, expiresAt)
            }
            Phase1FrameType.LEASE_REVOKED -> {
                requireControlFrame(frame)
                session.revokeControl(decodeUtf8(frame.payload))
            }
            Phase1FrameType.FLOW_CONTROL -> {
                requireControlFrame(frame)
                require(frame.payload.size == 4) { "invalid FlowControl payload" }
                val window = ByteBuffer.wrap(frame.payload).order(ByteOrder.BIG_ENDIAN).int
                require(window > 0) { "invalid FlowControl window" }
                mutableFlowControlWindow.value = window
            }
            Phase1FrameType.ERROR -> {
                requireControlFrame(frame)
                throw TestTransportException("test Relay error: ${decodeUtf8(frame.payload)}")
            }
            Phase1FrameType.TITLE -> {
                requireServerEvent(frame)
                requireAccepted(session.accept(RemoteSessionEvent.Title(frame.sequence, decodeUtf8(frame.payload))))
            }
            else -> error("unknown phase 1 server frame type ${frame.type}")
        }
    }

    private suspend fun handleDisconnect(disconnected: Int, reason: String?) {
        if (disconnectedGeneration == disconnected || closed.get()) return
        disconnectedGeneration = disconnected
        activeSocket = null
        pendingInputs.values.forEach {
            it.completeExceptionally(TestTransportException(reason ?: "test Relay disconnected"))
        }
        pendingInputs.clear()
        session.onDisconnected(reason)
        if (session.state.value is RemoteSessionState.Exited ||
            mutableTransportState.value is Phase1TransportState.Failed
        ) {
            mutableTransportState.value = if (session.state.value is RemoteSessionState.Exited) {
                Phase1TransportState.Closed
            } else {
                mutableTransportState.value
            }
            return
        }
        reconnectAttempt++
        mutableTransportState.value = Phase1TransportState.Reconnecting(reconnectAttempt, reason)
        val delayMillis = reconnectDelaysMillis[
            (reconnectAttempt - 1).coerceAtMost(reconnectDelaysMillis.lastIndex)
        ]
        reconnectJob?.cancel()
        reconnectJob = scope.launch {
            delay(delayMillis)
            connect()
        }
    }

    private fun sendFrame(frame: Phase1TestFrame) {
        check(!closed.get()) { "phase 1 test session client is closed" }
        val socket = activeSocket ?: throw TestTransportException("test Relay is not connected")
        if (!socket.send(ByteString.of(*Phase1TestFrameCodec.encode(frame)))) {
            throw TestTransportException("test Relay send queue rejected the frame")
        }
    }

    private fun requireServerEvent(frame: Phase1TestFrame) {
        require(frame.sequence > 0 && frame.acknowledgement == 0L) { "invalid sequenced server event" }
    }

    private fun requireControlFrame(frame: Phase1TestFrame) {
        require(frame.sequence == 0L && frame.acknowledgement == 0L) { "invalid server control frame" }
    }

    private fun requireAccepted(acceptance: EventAcceptance) {
        require(
            acceptance is EventAcceptance.Applied ||
                acceptance is EventAcceptance.Duplicate ||
                acceptance is EventAcceptance.IgnoredAfterExit,
        ) { "server event caused an implicit History Gap" }
    }

    private inner class Listener(private val listenerGeneration: Int) : WebSocketListener() {
        override fun onOpen(webSocket: WebSocket, response: Response) {
            updates.trySend(SocketUpdate.Open(listenerGeneration, webSocket))
        }

        override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
            updates.trySend(SocketUpdate.Binary(listenerGeneration, bytes.toByteArray()))
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            webSocket.close(PROTOCOL_ERROR, "binary frames required")
            updates.trySend(SocketUpdate.Disconnected(listenerGeneration, "text frame rejected"))
        }

        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
            webSocket.close(code, reason)
        }

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
            updates.trySend(SocketUpdate.Disconnected(listenerGeneration, reason.ifBlank { "closed $code" }))
        }

        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
            updates.trySend(
                SocketUpdate.Disconnected(
                    listenerGeneration,
                    t.message ?: t::class.java.simpleName,
                ),
            )
        }
    }

    private sealed interface SocketUpdate {
        val generation: Int

        data class Open(override val generation: Int, val socket: WebSocket) : SocketUpdate
        data class Binary(override val generation: Int, val bytes: ByteArray) : SocketUpdate
        data class Disconnected(override val generation: Int, val reason: String?) : SocketUpdate
    }

    private class TestTransportException(message: String) : IllegalStateException(message)

    private companion object {
        const val NORMAL_CLOSURE = 1000
        const val PROTOCOL_ERROR = 1002

        val defaultHttpClient: OkHttpClient = OkHttpClient.Builder()
            .connectTimeout(10, TimeUnit.SECONDS)
            .readTimeout(0, TimeUnit.MILLISECONDS)
            .pingInterval(15, TimeUnit.SECONDS)
            .build()
    }
}
