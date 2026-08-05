package dev.termlink.session

import dev.termlink.terminal.api.TerminalEngine
import dev.termlink.terminal.api.TerminalSize
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.FlowPreview
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.debounce
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.nio.ByteBuffer
import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean

class RemoteTerminalSession(
    private val engine: TerminalEngine,
    private val outbound: RemoteSessionOutbound,
    parentScope: CoroutineScope,
    private val resizeDebounceMillis: Long = 150,
    private val leaseRenewalIntervalMillis: Long = 10_000,
    private val clockMillis: () -> Long = System::currentTimeMillis,
    private val inputIdFactory: () -> UUID = UUID::randomUUID,
    private val disposeEngineOnClose: Boolean = true,
) : AutoCloseable {
    private val sessionJob = SupervisorJob(parentScope.coroutineContext[Job])
    private val scope = CoroutineScope(parentScope.coroutineContext + sessionJob)
    private val mutex = Mutex()
    private val started = AtomicBoolean(false)
    private val closed = AtomicBoolean(false)
    private val mutableState = MutableStateFlow<RemoteSessionState>(
        RemoteSessionState.Reconnecting(lastAckedSequence = 0),
    )
    private val mutableTitle = MutableStateFlow("")
    private var lastAckedSequence = 0L
    private var latestTerminalSize: TerminalSize? = null
    private var leaseExpiryJob: Job? = null
    private var leaseRenewalJob: Job? = null

    val state: StateFlow<RemoteSessionState> = mutableState.asStateFlow()
    val title: StateFlow<String> = mutableTitle.asStateFlow()

    init {
        require(resizeDebounceMillis in 100..200) {
            "resizeDebounceMillis must be from 100 through 200"
        }
        require(leaseRenewalIntervalMillis > 0) { "leaseRenewalIntervalMillis must be positive" }
    }

    @OptIn(FlowPreview::class)
    fun start() {
        check(!closed.get()) { "RemoteTerminalSession is closed" }
        check(started.compareAndSet(false, true)) { "RemoteTerminalSession is already started" }
        scope.launch {
            engine.inputEvents.collect { bytes ->
                submitInput(bytes)
            }
        }
        scope.launch {
            engine.sizeEvents
                .distinctUntilChanged()
                .debounce(resizeDebounceMillis)
                .collect(::observeResize)
        }
    }

    suspend fun onConnected(): Boolean {
        mutex.withLock {
            if (mutableState.value is RemoteSessionState.Exited) return false
            if (mutableState.value !is RemoteSessionState.HistoryGap) {
                mutableState.value = RemoteSessionState.Live(lastAckedSequence)
            }
            engine.setReadOnly(true)
        }
        return try {
            outbound.resume(lastAckedSequence)
            true
        } catch (error: CancellationException) {
            throw error
        } catch (_: Exception) {
            markReconnecting("resume failed")
            false
        }
    }

    suspend fun onDisconnected(reason: String? = null) {
        markReconnecting(reason)
    }

    suspend fun markReadOnly(reason: String) {
        mutex.withLock {
            if (mutableState.value is RemoteSessionState.Exited ||
                mutableState.value is RemoteSessionState.HistoryGap
            ) return
            leaseExpiryJob?.cancel()
            leaseExpiryJob = null
            leaseRenewalJob?.cancel()
            leaseRenewalJob = null
            mutableState.value = RemoteSessionState.ReadOnly(lastAckedSequence, reason)
            engine.setReadOnly(true)
        }
    }

    suspend fun grantControl(
        leaseId: String,
        expiresAtEpochMillis: Long,
        leaseVersion: Long = 0,
    ) {
        require(leaseId.isNotBlank()) { "leaseId is required" }
        require(expiresAtEpochMillis > clockMillis()) { "control lease is already expired" }
        val terminalSize = mutex.withLock {
            check(mutableState.value !is RemoteSessionState.Exited) { "session has exited" }
            check(mutableState.value !is RemoteSessionState.HistoryGap) {
                "session has an unrecoverable history gap"
            }
            leaseExpiryJob?.cancel()
            leaseRenewalJob?.cancel()
            mutableState.value = RemoteSessionState.Controlling(
                lastAckedSequence,
                leaseId,
                expiresAtEpochMillis,
                leaseVersion,
            )
            engine.setReadOnly(false)
            scheduleLeaseExpiry(leaseId, expiresAtEpochMillis)
            leaseRenewalJob = scope.launch {
                while (true) {
                    delay(leaseRenewalIntervalMillis)
                    val lease = mutex.withLock {
                        val current = mutableState.value
                        if (current is RemoteSessionState.Controlling && current.leaseId == leaseId &&
                            current.expiresAtEpochMillis > clockMillis()
                        ) current else null
                    } ?: break
                    try {
                        val renewed = outbound.renewLease(lease.leaseId, lease.leaseVersion)
                        if (renewed != null) {
                            require(renewed.leaseId == lease.leaseId) { "renewed lease ID changed" }
                            require(renewed.version >= lease.leaseVersion) { "renewed lease version regressed" }
                            require(renewed.expiresAtEpochMillis > clockMillis()) { "renewed lease is expired" }
                            mutex.withLock {
                                val current = mutableState.value
                                if (current is RemoteSessionState.Controlling && current.leaseId == renewed.leaseId) {
                                    mutableState.value = current.copy(
                                        expiresAtEpochMillis = renewed.expiresAtEpochMillis,
                                        leaseVersion = renewed.version,
                                    )
                                    scheduleLeaseExpiry(renewed.leaseId, renewed.expiresAtEpochMillis)
                                }
                            }
                        }
                    } catch (error: CancellationException) {
                        throw error
                    } catch (_: Exception) {
                        markReadOnly("control lease renewal failed")
                        break
                    }
                }
            }
            latestTerminalSize
        }
        if (terminalSize != null) {
            sendResize(terminalSize)
        }
    }

    suspend fun revokeControl(reason: String = "control lease revoked") {
        markReadOnly(reason)
    }

    suspend fun accept(event: RemoteSessionEvent): EventAcceptance = mutex.withLock {
        require(event.sequence > 0) { "event sequence must be positive" }
        if (mutableState.value is RemoteSessionState.Exited) return EventAcceptance.IgnoredAfterExit
        if (event.sequence <= lastAckedSequence) return EventAcceptance.Duplicate(event.sequence)

        val expected = lastAckedSequence + 1
        if (event is RemoteSessionEvent.Gap) {
            require(event.missingEndSequence >= event.missingStartSequence) { "gap range is inverted" }
            require(event.missingStartSequence == expected) { "gap start does not match the next sequence" }
            require(event.missingEndSequence == event.sequence - 1) { "gap end does not precede its frame" }
            enterHistoryGap(expected, event.sequence)
            return EventAcceptance.HistoryGap(expected, event.sequence)
        }
        if (event.sequence != expected) {
            enterHistoryGap(expected, event.sequence)
            return EventAcceptance.HistoryGap(expected, event.sequence)
        }

        when (event) {
            is RemoteSessionEvent.Output -> {
                require(event.bytes.isNotEmpty()) { "output event is empty" }
                engine.feed(event.sequence, ByteBuffer.wrap(event.bytes).asReadOnlyBuffer())
            }
            is RemoteSessionEvent.Resize -> engine.resize(event.size.columns, event.size.rows)
            is RemoteSessionEvent.Title -> mutableTitle.value = event.value
            is RemoteSessionEvent.InputResult -> Unit
            is RemoteSessionEvent.ControlGranted -> Unit
            is RemoteSessionEvent.ControlRevoked -> {
                leaseExpiryJob?.cancel()
                leaseExpiryJob = null
                leaseRenewalJob?.cancel()
                leaseRenewalJob = null
                engine.setReadOnly(true)
            }
            is RemoteSessionEvent.Lifecycle -> Unit
            is RemoteSessionEvent.Gap -> error("handled above")
        }

        lastAckedSequence = event.sequence
        mutableState.value = stateAfterAppliedEvent(event)
        try {
            outbound.acknowledge(lastAckedSequence)
        } catch (error: CancellationException) {
            throw error
        } catch (_: Exception) {
            engine.setReadOnly(true)
            if (mutableState.value !is RemoteSessionState.Exited) {
                mutableState.value = RemoteSessionState.Reconnecting(lastAckedSequence, "ACK failed")
            }
        }
        EventAcceptance.Applied(event.sequence)
    }

    suspend fun resetAndReplay(events: List<RemoteSessionEvent>): Long {
        require(events.isNotEmpty()) { "raw replay must not be empty" }
        require(events.first().sequence == 1L) { "raw replay must start at sequence 1 without a Snapshot" }
        mutex.withLock {
            leaseExpiryJob?.cancel()
            leaseExpiryJob = null
            leaseRenewalJob?.cancel()
            leaseRenewalJob = null
            engine.setReadOnly(true)
            engine.clear()
            lastAckedSequence = 0
            mutableTitle.value = ""
            mutableState.value = RemoteSessionState.Live(lastAckedSequence)
        }
        for (event in events) {
            val result = accept(event)
            check(result is EventAcceptance.Applied) { "raw replay is not a complete contiguous stream" }
        }
        return lastAckedSequence
    }

    suspend fun submitInput(bytes: ByteArray): InputSubmission {
        val input = mutex.withLock {
            if (bytes.isEmpty()) return InputSubmission.RejectedEmpty
            val current = mutableState.value
            if (current is RemoteSessionState.Exited) return InputSubmission.RejectedExited
            if (current !is RemoteSessionState.Controlling || current.expiresAtEpochMillis <= clockMillis()) {
                if (current is RemoteSessionState.Controlling) {
                    mutableState.value = RemoteSessionState.ReadOnly(lastAckedSequence, "control lease expired")
                    engine.setReadOnly(true)
                }
                return InputSubmission.RejectedNoLease
            }
            RemoteInput(inputIdFactory(), bytes.copyOf())
        }
        return try {
            // Do not hold the session mutex while waiting for InputAccepted;
            // the ordered result must enter accept() on the receive coroutine.
            outbound.sendInput(input)
            InputSubmission.Sent(input.inputId)
        } catch (error: CancellationException) {
            throw error
        } catch (_: Exception) {
            mutex.withLock {
                if (mutableState.value !is RemoteSessionState.Exited) {
                    mutableState.value = RemoteSessionState.Reconnecting(lastAckedSequence, "input transport failed")
                    engine.setReadOnly(true)
                }
            }
            InputSubmission.TransportFailed(input.inputId)
        }
    }

    private suspend fun observeResize(size: TerminalSize) {
        val shouldSend = mutex.withLock {
            latestTerminalSize = size
            mutableState.value is RemoteSessionState.Controlling
        }
        if (shouldSend) sendResize(size)
    }

    private suspend fun sendResize(size: TerminalSize) {
        try {
            outbound.sendResize(size)
        } catch (error: CancellationException) {
            throw error
        } catch (_: Exception) {
            markReconnecting("resize failed")
        }
    }

    private fun stateAfterAppliedEvent(event: RemoteSessionEvent): RemoteSessionState {
        if (event is RemoteSessionEvent.ControlRevoked) {
            return RemoteSessionState.ReadOnly(lastAckedSequence, event.reason)
        }
        if (event is RemoteSessionEvent.Lifecycle) {
            return when (event.state) {
                RemoteLifecycle.RUNNING -> withUpdatedAck(mutableState.value)
                RemoteLifecycle.INTERRUPTED -> {
                    engine.setReadOnly(true)
                    RemoteSessionState.Reconnecting(lastAckedSequence, "remote session interrupted")
                }
                RemoteLifecycle.ENDED -> {
                    leaseExpiryJob?.cancel()
                    leaseExpiryJob = null
                    leaseRenewalJob?.cancel()
                    leaseRenewalJob = null
                    engine.setReadOnly(true)
                    RemoteSessionState.Exited(lastAckedSequence, event.exitCode)
                }
            }
        }
        return withUpdatedAck(mutableState.value)
    }

    private fun scheduleLeaseExpiry(leaseId: String, expiresAtEpochMillis: Long) {
        leaseExpiryJob?.cancel()
        leaseExpiryJob = scope.launch {
            delay((expiresAtEpochMillis - clockMillis()).coerceAtLeast(0))
            expireLease(leaseId)
        }
    }

    private fun withUpdatedAck(current: RemoteSessionState): RemoteSessionState = when (current) {
        is RemoteSessionState.Live -> current.copy(lastAckedSequence = lastAckedSequence)
        is RemoteSessionState.Reconnecting -> current.copy(lastAckedSequence = lastAckedSequence)
        is RemoteSessionState.ReadOnly -> current.copy(lastAckedSequence = lastAckedSequence)
        is RemoteSessionState.Controlling -> current.copy(lastAckedSequence = lastAckedSequence)
        is RemoteSessionState.Exited -> current.copy(lastAckedSequence = lastAckedSequence)
        is RemoteSessionState.HistoryGap -> current.copy(lastAckedSequence = lastAckedSequence)
    }

    private fun enterHistoryGap(expected: Long, received: Long) {
        leaseExpiryJob?.cancel()
        leaseExpiryJob = null
        leaseRenewalJob?.cancel()
        leaseRenewalJob = null
        engine.setReadOnly(true)
        mutableState.value = RemoteSessionState.HistoryGap(lastAckedSequence, expected, received)
    }

    private suspend fun markReconnecting(reason: String?) {
        mutex.withLock {
            if (mutableState.value is RemoteSessionState.Exited ||
                mutableState.value is RemoteSessionState.HistoryGap
            ) return
            leaseExpiryJob?.cancel()
            leaseExpiryJob = null
            leaseRenewalJob?.cancel()
            leaseRenewalJob = null
            mutableState.value = RemoteSessionState.Reconnecting(lastAckedSequence, reason)
            engine.setReadOnly(true)
        }
    }

    private suspend fun expireLease(leaseId: String) {
        mutex.withLock {
            val current = mutableState.value
            if (current is RemoteSessionState.Controlling && current.leaseId == leaseId &&
                current.expiresAtEpochMillis <= clockMillis()
            ) {
                mutableState.value = RemoteSessionState.ReadOnly(lastAckedSequence, "control lease expired")
                engine.setReadOnly(true)
                leaseExpiryJob = null
                leaseRenewalJob?.cancel()
                leaseRenewalJob = null
            }
        }
    }

    override fun close() {
        if (!closed.compareAndSet(false, true)) return
        leaseExpiryJob?.cancel()
        leaseRenewalJob?.cancel()
        scope.cancel()
        engine.setReadOnly(true)
        if (disposeEngineOnClose) engine.dispose()
    }
}
