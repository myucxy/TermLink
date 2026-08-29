package dev.termlink.session

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.launch
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

/**
 * A diagnostic consumer of renderer-ordered events.
 *
 * Implementations run away from the production renderer and ACK path. They must not retain an
 * [OrderedRemoteSessionNotification.Applied] event beyond [observe] unless they make another copy.
 */
interface RemoteSessionOrderedEventObserver : AutoCloseable {
    suspend fun observe(notification: OrderedRemoteSessionNotification)

    override fun close() = Unit
}

sealed interface OrderedRemoteSessionNotification {
    val rendererGeneration: Long

    /** The production renderer has been cleared and a prefix beginning at sequence 1 will follow. */
    data class RendererReset(
        override val rendererGeneration: Long,
    ) : OrderedRemoteSessionNotification

    /** The current renderer generation can no longer be compared until a later reset. */
    data class RendererInvalidated(
        override val rendererGeneration: Long,
    ) : OrderedRemoteSessionNotification

    /** The event has passed ordering validation and has already been applied to the renderer. */
    data class Applied(
        override val rendererGeneration: Long,
        val event: RemoteSessionEvent,
    ) : OrderedRemoteSessionNotification
}

/**
 * Bounded, fail-isolated dispatcher owned by one [RemoteTerminalSession].
 *
 * Production code only calls non-blocking [offerApplied] and [offerReset]. A full queue, observer
 * exception, copy failure, or close permanently retires the observer while the terminal session
 * continues normally.
 */
internal class FailIsolatedOrderedEventDispatcher(
    parentScope: CoroutineScope,
    private val observer: RemoteSessionOrderedEventObserver,
    capacity: Int,
) : AutoCloseable {
    private val retired = AtomicBoolean(false)
    private val queuedOutputBytes = AtomicLong(0)
    private val channel = Channel<QueuedNotification>(validatedCapacity(capacity))
    private val scope = CoroutineScope(parentScope.coroutineContext + SupervisorJob(parentScope.coroutineContext[Job]) + Dispatchers.Default)
    private val cleanupScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val worker = scope.launch {
        try {
            for (queued in channel) {
                try {
                    observer.observe(queued.notification)
                } finally {
                    queued.release()
                }
            }
        } catch (error: CancellationException) {
            throw error
        } catch (_: Throwable) {
            retire()
        } finally {
            if (!retired.get()) retire()
        }
    }

    val isRetired: Boolean
        get() = retired.get()

    fun offerApplied(rendererGeneration: Long, event: RemoteSessionEvent) {
        if (retired.get()) return
        val outputBytes = (event as? RemoteSessionEvent.Output)?.bytes?.size ?: 0
        if (outputBytes > MAX_OUTPUT_BYTES_PER_EVENT || !reserveOutputBytes(outputBytes)) {
            retire()
            return
        }
        val owned = try {
            QueuedNotification(
                OrderedRemoteSessionNotification.Applied(rendererGeneration, event.copyForObserver()),
                outputBytes,
            )
        } catch (_: Throwable) {
            releaseOutputBytes(outputBytes)
            retire()
            return
        }
        if (channel.trySend(owned).isFailure) {
            owned.release()
            retire()
        }
    }

    fun offerReset(rendererGeneration: Long) {
        offerControl(OrderedRemoteSessionNotification.RendererReset(rendererGeneration))
    }

    fun offerInvalidated(rendererGeneration: Long) {
        offerControl(OrderedRemoteSessionNotification.RendererInvalidated(rendererGeneration))
    }

    private fun offerControl(notification: OrderedRemoteSessionNotification) {
        if (retired.get()) return
        if (channel.trySend(QueuedNotification(notification, 0)).isFailure) {
            retire()
        }
    }

    private fun reserveOutputBytes(byteCount: Int): Boolean {
        if (byteCount == 0) return true
        while (true) {
            val current = queuedOutputBytes.get()
            val next = current + byteCount.toLong()
            if (next > MAX_QUEUED_OUTPUT_BYTES) return false
            if (queuedOutputBytes.compareAndSet(current, next)) return true
        }
    }

    private fun releaseOutputBytes(byteCount: Int) {
        if (byteCount != 0) queuedOutputBytes.addAndGet(-byteCount.toLong())
    }

    private fun retire() {
        if (!retired.compareAndSet(false, true)) return
        channel.close()
        scope.cancel()
        while (true) {
            val queued = channel.tryReceive().getOrNull() ?: break
            queued.release()
        }
        cleanupScope.launch {
            try {
                runCatching { observer.close() }
            } finally {
                cleanupScope.cancel()
            }
        }
    }

    override fun close() = retire()

    private inner class QueuedNotification(
        val notification: OrderedRemoteSessionNotification,
        private val outputByteCount: Int,
    ) {
        private val released = AtomicBoolean(false)

        fun release() {
            if (!released.compareAndSet(false, true)) return
            val event = (notification as? OrderedRemoteSessionNotification.Applied)?.event
            (event as? RemoteSessionEvent.Output)?.bytes?.fill(0)
            releaseOutputBytes(outputByteCount)
        }
    }

    private companion object {
        const val MAX_CAPACITY = 4_096
        const val MAX_OUTPUT_BYTES_PER_EVENT = 1024 * 1024
        const val MAX_QUEUED_OUTPUT_BYTES = 4L * 1024L * 1024L

        fun validatedCapacity(capacity: Int): Int {
            require(capacity in 1..MAX_CAPACITY) {
                "ordered observer capacity must be in 1..$MAX_CAPACITY"
            }
            return capacity
        }
    }
}

private fun RemoteSessionEvent.copyForObserver(): RemoteSessionEvent = when (this) {
    is RemoteSessionEvent.Output -> copy(bytes = bytes.copyOf())
    is RemoteSessionEvent.Resize -> copy()
    is RemoteSessionEvent.Title -> copy()
    is RemoteSessionEvent.InputResult -> copy()
    is RemoteSessionEvent.ControlRevoked -> copy()
    is RemoteSessionEvent.ControlGranted -> copy()
    is RemoteSessionEvent.Lifecycle -> copy()
    is RemoteSessionEvent.Gap -> copy()
}
