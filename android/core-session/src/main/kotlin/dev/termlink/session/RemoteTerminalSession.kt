package dev.termlink.session

import dev.termlink.terminal.api.TerminalEngine
import dev.termlink.terminal.api.TerminalInput
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.VtTerminalInputEncoder
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withLock
import java.nio.ByteBuffer
import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

private const val MAX_IN_FLIGHT_INPUTS = 32

class RemoteTerminalSession(
    private val engine: TerminalEngine,
    private val outbound: RemoteSessionOutbound,
    parentScope: CoroutineScope,
    initialRendererState: RendererBootstrapState = RendererBootstrapState.Empty(),
    private val resizeDebounceMillis: Long = 150,
    private val leaseRenewalIntervalMillis: Long = 10_000,
    private val clockMillis: () -> Long = System::currentTimeMillis,
    private val inputIdFactory: () -> UUID = UUID::randomUUID,
    private val disposeEngineOnClose: Boolean = true,
    orderedEventObserver: RemoteSessionOrderedEventObserver? = null,
    orderedEventObserverCapacity: Int = 64,
) : AutoCloseable {
    private val initialAcknowledgedSequence = initialRendererState.sequence
    private val sessionJob = SupervisorJob(parentScope.coroutineContext[Job])
    private val scope = CoroutineScope(parentScope.coroutineContext + sessionJob)
    private val cleanupScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val mutex = Mutex()
    private val inputSubmissionPermits = Semaphore(MAX_IN_FLIGHT_INPUTS)
    private val acknowledgementCommitMutex = Mutex()
    private val started = AtomicBoolean(false)
    private val closed = AtomicBoolean(false)
    private val rebuildingRenderer = AtomicBoolean(false)
    private val mutableState = MutableStateFlow<RemoteSessionState>(
        RemoteSessionState.Reconnecting(lastAckedSequence = initialAcknowledgedSequence),
    )
    // Null means no ordered title event has been observed for the current
    // renderer generation. An empty string is a real, explicitly published
    // title and must remain distinguishable so callers can clear fallbacks.
    private val mutableTitle = MutableStateFlow<String?>(null)
    private val acknowledgementHighWater = AtomicLong(initialAcknowledgedSequence)
    private val acknowledgementSignal = Channel<Unit>(Channel.CONFLATED)
    private val acknowledgementJob: Job
    private var lastAppliedSequence = initialAcknowledgedSequence
    private var lastAckedSequence = initialAcknowledgedSequence
    private var lastPresentationRevision = initialAcknowledgedSequence
    private var rendererGeneration = initialRendererState.rendererGeneration
    private var gapGeneration = 0L
    private var leaseExpiryJob: Job? = null
    private var leaseRenewalJob: Job? = null
    private val orderedEventDispatcher = orderedEventObserver?.let {
        FailIsolatedOrderedEventDispatcher(parentScope, it, orderedEventObserverCapacity)
    }

    val state: StateFlow<RemoteSessionState> = mutableState.asStateFlow()
    val title: StateFlow<String?> = mutableTitle.asStateFlow()

    init {
        require(resizeDebounceMillis in 100..200) {
            "resizeDebounceMillis must be from 100 through 200"
        }
        require(leaseRenewalIntervalMillis > 0) { "leaseRenewalIntervalMillis must be positive" }
        acknowledgementJob = scope.launch(start = CoroutineStart.UNDISPATCHED) {
            for (ignored in acknowledgementSignal) {
                drainAcknowledgements()
            }
        }
    }

    fun start() {
        check(!closed.get()) { "RemoteTerminalSession is closed" }
        check(started.compareAndSet(false, true)) { "RemoteTerminalSession is already started" }
        scope.launch {
            engine.inputEvents.collect { bytes ->
                // InputAccepted is correlated per input ID, so one delayed
                // confirmation must not block later keyboard or Dock input.
                // UNDISPATCHED preserves engine emission order through the
                // transport's serialized send path before awaiting each ACK.
                inputSubmissionPermits.acquire()
                scope.launch(start = CoroutineStart.UNDISPATCHED) {
                    try {
                        submitInput(bytes)
                    } finally {
                        inputSubmissionPermits.release()
                    }
                }
            }
        }
    }

    suspend fun onConnected(): Boolean {
        var historyRecoveryRequired = false
        var durableCursor = 0L
        mutex.withLock {
            if (closed.get() || mutableState.value is RemoteSessionState.Exited) return false
            historyRecoveryRequired = mutableState.value is RemoteSessionState.HistoryGap
            durableCursor = lastAckedSequence
            if (!historyRecoveryRequired) {
                mutableState.value = RemoteSessionState.Live(durableCursor)
            }
            engine.setReadOnly(true)
        }
        // A gap cannot safely advertise any cursor until the renderer has been
        // rebuilt and the cumulative ACK has been persisted. REST history
        // recovery will publish a fresh SUBSCRIBE after that commit.
        if (historyRecoveryRequired) return true
        return try {
            outbound.resume(durableCursor)
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
            if (closed.get() || mutableState.value is RemoteSessionState.Exited ||
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
        mutex.withLock {
            check(!closed.get()) { "RemoteTerminalSession is closed" }
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
        }
    }

    suspend fun revokeControl(reason: String = "control lease revoked") {
        markReadOnly(reason)
    }

    suspend fun accept(event: RemoteSessionEvent): EventAcceptance = accept(event, sendAcknowledgement = true)

    private suspend fun accept(
        event: RemoteSessionEvent,
        sendAcknowledgement: Boolean,
    ): EventAcceptance = mutex.withLock {
        check(!closed.get()) { "RemoteTerminalSession is closed" }
        check(!rebuildingRenderer.get()) { "RemoteTerminalSession renderer is rebuilding" }
        require(event.sequence > 0) { "event sequence must be positive" }
        if (event.sequence <= lastAppliedSequence) {
            if (sendAcknowledgement && event.sequence > lastAckedSequence) {
                requestAcknowledgement(lastAppliedSequence)
            }
            return EventAcceptance.Duplicate(event.sequence)
        }
        if (mutableState.value is RemoteSessionState.Exited) return EventAcceptance.IgnoredAfterExit
        (mutableState.value as? RemoteSessionState.HistoryGap)?.let { gap ->
            return EventAcceptance.HistoryGap(gap.expectedSequence, gap.receivedSequence)
        }

        val expected = lastAppliedSequence + 1
        if (event is RemoteSessionEvent.Gap) {
            require(event.missingEndSequence >= event.missingStartSequence) { "gap range is inverted" }
            require(event.missingEndSequence == event.sequence - 1) { "gap end does not precede its frame" }
            if (event.missingEndSequence >= expected) {
                // A reconnect can legitimately overlap a prefix which reached
                // the renderer but whose cumulative ACK had not reached the PC.
                // Freeze only the still-missing suffix.
                enterHistoryGap(expected, event.sequence)
                return EventAcceptance.HistoryGap(expected, event.sequence)
            }
            // History recovery ends immediately before the ordered Gap marker.
            // When the peer replays that same marker its missing range is now
            // fully covered; consume it as a no-op sequence event and ACK it.
            check(event.sequence == expected) { "covered gap marker is not the next sequence" }
            engine.advance(event.sequence)
            lastAppliedSequence = event.sequence
            mutableState.value = withUpdatedAck(mutableState.value)
            if (sendAcknowledgement) requestAcknowledgement(lastAppliedSequence)
            orderedEventDispatcher?.offerApplied(rendererGeneration, event)
            return EventAcceptance.Applied(event.sequence)
        }
        if (event.sequence != expected) {
            enterHistoryGap(expected, event.sequence)
            return EventAcceptance.HistoryGap(expected, event.sequence)
        }
        if (event is RemoteSessionEvent.Output &&
            (event.sourceEventSequence != event.sequence ||
                event.presentationRevision <= lastPresentationRevision)
        ) {
            enterHistoryGap(expected, event.sequence)
            return EventAcceptance.HistoryGap(expected, event.sequence)
        }

        when (event) {
            is RemoteSessionEvent.Output -> {
                require(event.bytes.isNotEmpty()) { "output event is empty" }
                applyOutput(event)
            }
            is RemoteSessionEvent.Resize -> engine.resize(event.sequence, event.size.columns, event.size.rows)
            is RemoteSessionEvent.Title -> {
                engine.advance(event.sequence)
                mutableTitle.value = event.value
            }
            is RemoteSessionEvent.InputResult -> engine.advance(event.sequence)
            is RemoteSessionEvent.ControlGranted -> engine.advance(event.sequence)
            is RemoteSessionEvent.ControlRevoked -> {
                engine.advance(event.sequence)
                leaseExpiryJob?.cancel()
                leaseExpiryJob = null
                leaseRenewalJob?.cancel()
                leaseRenewalJob = null
                engine.setReadOnly(true)
            }
            is RemoteSessionEvent.Lifecycle -> engine.advance(event.sequence)
            is RemoteSessionEvent.Gap -> error("handled above")
        }

        lastAppliedSequence = event.sequence
        mutableState.value = stateAfterAppliedEvent(event)
        if (sendAcknowledgement) {
            requestAcknowledgement(lastAppliedSequence)
        }
        orderedEventDispatcher?.offerApplied(rendererGeneration, event)
        EventAcceptance.Applied(event.sequence)
    }

    private fun requestAcknowledgement(highestAppliedSequence: Long) {
        acknowledgementHighWater.accumulateAndGet(highestAppliedSequence, ::maxOf)
        acknowledgementSignal.trySend(Unit)
    }

    private suspend fun drainAcknowledgements() {
        while (!closed.get()) {
            val target = acknowledgementHighWater.get()
            try {
                val committed = acknowledgementCommitMutex.withLock {
                    val durableBefore = mutex.withLock { lastAckedSequence }
                    if (target <= durableBefore) return@withLock false
                    val persisted = outbound.persistAcknowledgement(rendererGeneration, target)
                    require(persisted.sequence == target && persisted.rendererGeneration == rendererGeneration) {
                        "durable ACK commit does not match the active renderer"
                    }
                    mutex.withLock {
                        check(target <= lastAppliedSequence) {
                            "durable ACK cannot advance beyond the applied renderer cursor"
                        }
                        if (target > lastAckedSequence) {
                            lastAckedSequence = target
                            mutableState.value = withUpdatedAck(mutableState.value)
                        }
                    }
                    outbound.acknowledge(target)
                    true
                }
                if (!committed || acknowledgementHighWater.get() <= target) return
            } catch (error: CancellationException) {
                throw error
            } catch (_: Exception) {
                markReconnecting("ACK failed")
                return
            }
        }
    }

    suspend fun resetAndReplay(
        events: List<RemoteSessionEvent>,
        acknowledgeEach: Boolean = true,
    ): Long {
        check(!closed.get()) { "RemoteTerminalSession is closed" }
        validateHistoryReplay(events, events.lastOrNull()?.sequence ?: 0)
        check(rebuildingRenderer.compareAndSet(false, true)) { "RemoteTerminalSession renderer is already rebuilding" }
        try {
            return acknowledgementCommitMutex.withLock {
                mutex.withLock {
                    check(!closed.get()) { "RemoteTerminalSession is closed" }
                    installResetCommit(outbound.resetAcknowledgement(rendererGeneration))
                    leaseExpiryJob?.cancel()
                    leaseExpiryJob = null
                    leaseRenewalJob?.cancel()
                    leaseRenewalJob = null
                    try {
                        engine.setReadOnly(true)
                        engine.clear()
                        lastAppliedSequence = 0
                        lastAckedSequence = 0
                        lastPresentationRevision = 0
                        acknowledgementHighWater.set(0)
                        mutableTitle.value = null
                        mutableState.value = RemoteSessionState.Live(lastAckedSequence)
                        orderedEventDispatcher?.offerReset(rendererGeneration)
                        for (event in events) {
                            when (event) {
                                is RemoteSessionEvent.Output -> applyOutput(event)
                                is RemoteSessionEvent.Resize -> engine.resize(
                                    event.sequence,
                                    event.size.columns,
                                    event.size.rows,
                                )
                                is RemoteSessionEvent.Title -> {
                                    engine.advance(event.sequence)
                                    mutableTitle.value = event.value
                                }
                                is RemoteSessionEvent.InputResult,
                                is RemoteSessionEvent.ControlGranted,
                                -> engine.advance(event.sequence)
                                is RemoteSessionEvent.ControlRevoked -> {
                                    engine.advance(event.sequence)
                                    engine.setReadOnly(true)
                                }
                                is RemoteSessionEvent.Lifecycle -> engine.advance(event.sequence)
                                is RemoteSessionEvent.Gap -> error("raw replay contains a permanent gap")
                            }
                            lastAppliedSequence = event.sequence
                            mutableState.value = stateAfterAppliedEvent(event)
                            if (acknowledgeEach) requestAcknowledgement(lastAppliedSequence)
                            orderedEventDispatcher?.offerApplied(rendererGeneration, event)
                        }
                        if (!acknowledgeEach) requestAcknowledgement(lastAppliedSequence)
                        lastAppliedSequence
                    } catch (error: Throwable) {
                        lastAppliedSequence = 0
                        lastAckedSequence = 0
                        lastPresentationRevision = 0
                        acknowledgementHighWater.set(0)
                        mutableTitle.value = null
                        runCatching { engine.clear() }.exceptionOrNull()?.let(error::addSuppressed)
                        orderedEventDispatcher?.offerInvalidated(rendererGeneration)
                        gapGeneration++
                        mutableState.value = RemoteSessionState.HistoryGap(
                            lastAckedSequence = 0,
                            expectedSequence = 1,
                            receivedSequence = events.last().sequence + 1,
                            generation = gapGeneration,
                        )
                        runCatching { engine.setReadOnly(true) }.exceptionOrNull()?.let(error::addSuppressed)
                        throw error
                    }
                }
            }
        } finally {
            rebuildingRenderer.set(false)
        }
    }

    /**
     * Rebuilds a specific HistoryGap generation from a verified raw prefix.
     * Replay shape and the original durable cursor are validated before the
     * renderer is cleared. Commit order is durable store, session cursor, then
     * wire publication.
     */
    suspend fun recoverFromHistory(
        events: List<RemoteSessionEvent>,
        expectedGap: RemoteSessionState.HistoryGap,
        persist: suspend (Long, Long) -> DurableAckCommit,
        publish: suspend (Long) -> Unit,
    ): Long {
        check(!closed.get()) { "RemoteTerminalSession is closed" }
        val target = expectedGap.receivedSequence - 1
        validateHistoryReplay(events, target)
        check(rebuildingRenderer.compareAndSet(false, true)) { "RemoteTerminalSession renderer is already rebuilding" }
        try {
            return acknowledgementCommitMutex.withLock {
                mutex.withLock {
                check(!closed.get()) { "RemoteTerminalSession is closed" }
                val currentGap = mutableState.value as? RemoteSessionState.HistoryGap
                    ?: error("history recovery requires an active History Gap")
                require(currentGap.generation == expectedGap.generation) {
                    "history gap generation changed before recovery"
                }
                require(currentGap.expectedSequence == expectedGap.expectedSequence &&
                    currentGap.receivedSequence == expectedGap.receivedSequence
                ) { "history gap range changed before recovery" }
                // The final ACK for the already-rendered prefix may finish
                // after the caller observes the HistoryGap. That monotonic
                // advance is safe: the generation and missing range above are
                // the recovery fence, while the current cursor remains the
                // authoritative durable value once the ACK commit mutex is
                // held. Reject only regression or an internally inconsistent
                // state.
                require(currentGap.lastAckedSequence >= expectedGap.lastAckedSequence) {
                    "durable ACK regressed before history recovery"
                }
                require(lastAckedSequence == currentGap.lastAckedSequence) {
                    "history gap durable ACK is inconsistent"
                }
                require(lastAppliedSequence == currentGap.expectedSequence - 1) {
                    "applied renderer cursor does not match the active history gap"
                }

                leaseExpiryJob?.cancel()
                leaseExpiryJob = null
                leaseRenewalJob?.cancel()
                leaseRenewalJob = null
                engine.setReadOnly(true)
                engine.clear()
                lastAppliedSequence = 0
                lastPresentationRevision = 0
                mutableTitle.value = null
                orderedEventDispatcher?.offerReset(rendererGeneration)
                var recoveredState: RemoteSessionState = RemoteSessionState.Live(lastAckedSequence)
                var durable = false
                try {
                    for (event in events) {
                        when (event) {
                            is RemoteSessionEvent.Output -> applyOutput(event)
                            is RemoteSessionEvent.Resize -> engine.resize(
                                event.sequence,
                                event.size.columns,
                                event.size.rows,
                            )
                            is RemoteSessionEvent.Title -> {
                                engine.advance(event.sequence)
                                mutableTitle.value = event.value
                            }
                            is RemoteSessionEvent.InputResult,
                            is RemoteSessionEvent.ControlGranted,
                            -> engine.advance(event.sequence)
                            is RemoteSessionEvent.ControlRevoked -> {
                                engine.advance(event.sequence)
                                engine.setReadOnly(true)
                            }
                            is RemoteSessionEvent.Lifecycle -> engine.advance(event.sequence)
                            is RemoteSessionEvent.Gap -> error("raw replay contains a permanent gap")
                        }
                        lastAppliedSequence = event.sequence
                        recoveredState = when (event) {
                            is RemoteSessionEvent.ControlRevoked ->
                                RemoteSessionState.ReadOnly(lastAckedSequence, event.reason)
                            is RemoteSessionEvent.Lifecycle -> when (event.state) {
                                RemoteLifecycle.RUNNING -> recoveredState
                                RemoteLifecycle.INTERRUPTED ->
                                    RemoteSessionState.Reconnecting(lastAckedSequence, "remote session interrupted")
                                RemoteLifecycle.ENDED ->
                                    RemoteSessionState.Exited(lastAckedSequence, event.exitCode)
                            }
                            else -> recoveredState
                        }
                        orderedEventDispatcher?.offerApplied(rendererGeneration, event)
                    }
                    val commit = persist(rendererGeneration, target)
                    require(commit.sequence == target && commit.rendererGeneration == rendererGeneration) {
                        "history recovery commit does not match the active renderer"
                    }
                    durable = true
                    lastAckedSequence = target
                    acknowledgementHighWater.set(target)
                    recoveredState = withUpdatedAck(recoveredState)
                    mutableState.value = recoveredState
                    publish(target)
                    target
                } catch (error: Throwable) {
                    if (durable) {
                        mutableState.value = if (recoveredState is RemoteSessionState.Exited) {
                            withUpdatedAck(recoveredState)
                        } else {
                            RemoteSessionState.Reconnecting(lastAckedSequence, "history recovery publication failed")
                        }
                    } else {
                        var resetFailure: Throwable? = null
                        try {
                            installResetCommit(outbound.resetAcknowledgement(rendererGeneration))
                        } catch (resetError: Throwable) {
                            resetFailure = resetError
                        }
                        lastAppliedSequence = 0
                        lastAckedSequence = 0
                        lastPresentationRevision = 0
                        acknowledgementHighWater.set(0)
                        mutableTitle.value = null
                        runCatching { engine.clear() }.exceptionOrNull()?.let(error::addSuppressed)
                        orderedEventDispatcher?.offerInvalidated(rendererGeneration)
                        mutableState.value = RemoteSessionState.HistoryGap(
                            lastAckedSequence = 0,
                            expectedSequence = 1,
                            receivedSequence = expectedGap.receivedSequence,
                            generation = expectedGap.generation,
                        )
                        resetFailure?.let(error::addSuppressed)
                    }
                    runCatching { engine.setReadOnly(true) }.exceptionOrNull()?.let(error::addSuppressed)
                    throw error
                }
                }
            }
        } finally {
            rebuildingRenderer.set(false)
        }
    }

    suspend fun recoverFromHistory(
        events: List<RemoteSessionEvent>,
        persist: suspend (Long, Long) -> DurableAckCommit,
        publish: suspend (Long) -> Unit,
    ): Long {
        val expected = state.value as? RemoteSessionState.HistoryGap
            ?: error("history recovery requires an active History Gap")
        return recoverFromHistory(events, expected, persist, publish)
    }

    private fun validateHistoryReplay(events: List<RemoteSessionEvent>, target: Long) {
        require(target > 0) { "history recovery target must be positive" }
        require(events.isNotEmpty()) { "raw replay must not be empty" }
        require(events.first().sequence == 1L) { "raw replay must start at sequence 1 without a Snapshot" }
        var expected = 1L
        var ended = false
        for (event in events) {
            require(!ended) { "raw replay contains events after the session ended" }
            require(event.sequence == expected) { "raw replay is not a complete contiguous stream" }
            require(event !is RemoteSessionEvent.Gap) { "raw replay contains a permanent gap" }
            if (event is RemoteSessionEvent.Output) {
                require(event.bytes.isNotEmpty()) { "output event is empty" }
            }
            ended = event is RemoteSessionEvent.Lifecycle && event.state == RemoteLifecycle.ENDED
            expected++
        }
        require(events.last().sequence == target) {
            "raw replay does not completely fill the active history gap"
        }
    }

    private fun installResetCommit(commit: DurableAckCommit) {
        require(commit.sequence == 0L) { "renderer reset must persist ACK zero" }
        require(commit.rendererGeneration > rendererGeneration) { "renderer reset did not advance generation" }
        rendererGeneration = commit.rendererGeneration
    }

    private suspend fun applyOutput(event: RemoteSessionEvent.Output) {
        require(event.bytes.isNotEmpty()) { "output event is empty" }
        require(event.sourceEventSequence == event.sequence) {
            "output presentation is anchored to another source event"
        }
        require(event.presentationRevision > lastPresentationRevision) {
            "output presentation revision did not advance"
        }
        engine.feed(event.sequence, ByteBuffer.wrap(event.bytes).asReadOnlyBuffer())
        lastPresentationRevision = event.presentationRevision
    }

    suspend fun submitInput(bytes: ByteArray): InputSubmission = submitInput(bytes, null)

    suspend fun submitInput(input: TerminalInput): InputSubmission {
        val bytes = VtTerminalInputEncoder.encode(input)
        return try {
            submitInput(bytes, input.copyForTransport())
        } finally {
            bytes.fill(0)
        }
    }

    private suspend fun submitInput(bytes: ByteArray, typedInput: TerminalInput?): InputSubmission {
        val input = mutex.withLock {
            check(!closed.get()) { "RemoteTerminalSession is closed" }
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
            RemoteInput(inputIdFactory(), bytes.copyOf(), typedInput)
        }
        return try {
            // Do not hold the session mutex while waiting for InputAccepted;
            // the ordered result must enter accept() on the receive coroutine.
            outbound.sendInput(input)
            InputSubmission.Sent(input.inputId)
        } catch (error: RemoteInputRejectedException) {
            InputSubmission.RejectedByRemote(input.inputId, error.reasonCode)
        } catch (_: RemoteInputDeliveryUnknownException) {
            // The frame may already have reached the PC. Keep the healthy
            // transport and lease intact and never retry ambiguous input.
            InputSubmission.DeliveryUnknown(input.inputId)
        } catch (error: CancellationException) {
            throw error
        } catch (_: Exception) {
            mutex.withLock {
                if (mutableState.value !is RemoteSessionState.Exited) {
                    mutableState.value = RemoteSessionState.Reconnecting(lastAckedSequence, "input transport failed")
                    engine.setReadOnly(true)
                }
            }
            InputSubmission.DeliveryUnknown(input.inputId)
        }
    }

    private fun TerminalInput.copyForTransport(): TerminalInput = when (this) {
        is TerminalInput.Text -> copy()
        is TerminalInput.Key -> copy()
        is TerminalInput.Paste -> TerminalInput.Paste(bytes.copyOf())
        is TerminalInput.RawBytes -> TerminalInput.RawBytes(bytes.copyOf())
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

    private suspend fun enterHistoryGap(expected: Long, received: Long) {
        leaseExpiryJob?.cancel()
        leaseExpiryJob = null
        leaseRenewalJob?.cancel()
        leaseRenewalJob = null
        engine.setReadOnly(true)
        val rendererFailure = runCatching { engine.reportGap(expected) }.exceptionOrNull()
        gapGeneration++
        mutableState.value = RemoteSessionState.HistoryGap(
            lastAckedSequence,
            expected,
            received,
            gapGeneration,
        )
        orderedEventDispatcher?.offerInvalidated(rendererGeneration)
        rendererFailure?.let { throw it }
    }

    private suspend fun markReconnecting(reason: String?) {
        mutex.withLock {
            if (closed.get() || mutableState.value is RemoteSessionState.Exited ||
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
            if (closed.get()) return
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
        acknowledgementSignal.close()
        acknowledgementJob.cancel()
        scope.cancel()
        orderedEventDispatcher?.close()
        if (mutex.tryLock()) {
            try {
                closeEngine()
            } finally {
                mutex.unlock()
                cleanupScope.cancel()
            }
        } else {
            cleanupScope.launch {
                try {
                    mutex.withLock { closeEngine() }
                } finally {
                    cleanupScope.cancel()
                }
            }
        }
    }

    private fun closeEngine() {
        engine.setReadOnly(true)
        if (disposeEngineOnClose) engine.dispose()
    }
}
