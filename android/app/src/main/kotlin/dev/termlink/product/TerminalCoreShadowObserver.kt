package dev.termlink.product

import dev.termlink.session.OrderedRemoteSessionNotification
import dev.termlink.session.RemoteSessionEvent
import dev.termlink.session.RemoteSessionOrderedEventObserver
import dev.termlink.session.RendererBootstrapState
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.nativecore.NativeSourceEvent
import dev.termlink.terminal.nativecore.JniNativeBridge
import dev.termlink.terminal.nativecore.NativeTerminalHandle

/** Narrow owner boundary so product tests never need to load the Android JNI library. */
internal interface NativeShadowTerminal : AutoCloseable {
    fun apply(event: NativeSourceEvent)
}

internal fun interface NativeShadowTerminalFactory {
    fun create(size: TerminalSize, initialNextSourceSequence: Long): NativeShadowTerminal
}

/** Returns null when the ABI library is unavailable or the initial core allocation is rejected. */
internal fun createJniTerminalCoreShadowObserver(
    initialRendererState: RendererBootstrapState,
    initialSize: TerminalSize,
): TerminalCoreShadowObserver? {
    return runCatching {
        TerminalCoreShadowObserver(initialRendererState, initialSize, JNI_SHADOW_FACTORY)
    }.getOrNull()
}

/**
 * Legacy diagnostic terminal-core comparator. The native terminal-core engine is production
 * renderer/input authority; this observer is isolated, optional, and must never advance ACKs.
 *
 * A non-empty Termux bootstrap cannot seed terminal-core, so the observer stays dormant until a
 * verified renderer reset announces a complete replay beginning at sequence 1.
 */
internal class TerminalCoreShadowObserver(
    initialRendererState: RendererBootstrapState,
    initialSize: TerminalSize,
    private val factory: NativeShadowTerminalFactory,
) : RemoteSessionOrderedEventObserver {
    private val lock = Any()
    private var closed = false
    private var rendererGeneration = initialRendererState.rendererGeneration
    private var lastKnownSize = initialSize
    private var startOnNextAppliedEvent = initialRendererState is RendererBootstrapState.Empty
    private var terminal: NativeShadowTerminal? = null

    override suspend fun observe(notification: OrderedRemoteSessionNotification) {
        synchronized(lock) {
            check(!closed) { "terminal-core shadow is closed" }
            when (notification) {
                is OrderedRemoteSessionNotification.RendererReset -> reset(notification.rendererGeneration)
                is OrderedRemoteSessionNotification.RendererInvalidated -> invalidate(notification.rendererGeneration)
                is OrderedRemoteSessionNotification.Applied -> apply(notification)
            }
        }
    }

    private fun reset(newRendererGeneration: Long) {
        require(newRendererGeneration >= rendererGeneration) {
            "terminal-core shadow renderer generation regressed"
        }
        val previous = terminal
        terminal = null
        rendererGeneration = newRendererGeneration
        // Arm the shadow only. Creating the JNI terminal remains lazy until
        // the first event of the complete replay arrives on the worker.
        startOnNextAppliedEvent = true
        previous?.close()
    }

    private fun invalidate(invalidatedGeneration: Long) {
        require(invalidatedGeneration == rendererGeneration) {
            "terminal-core shadow invalidation generation changed"
        }
        val previous = terminal
        terminal = null
        startOnNextAppliedEvent = false
        previous?.close()
    }

    private fun apply(notification: OrderedRemoteSessionNotification.Applied) {
        require(notification.rendererGeneration == rendererGeneration) {
            "terminal-core shadow event generation changed"
        }
        val event = notification.event
        if (event is RemoteSessionEvent.Resize) lastKnownSize = event.size
        val active = terminal ?: if (startOnNextAppliedEvent) {
            startOnNextAppliedEvent = false
            factory.create(lastKnownSize, FIRST_REMOTE_SEQUENCE).also { terminal = it }
        } else {
            return
        }
        active.apply(event.toNativeShadowEvent())
    }

    override fun close() {
        synchronized(lock) {
            if (closed) return
            closed = true
            startOnNextAppliedEvent = false
            val previous = terminal
            terminal = null
            previous?.close()
        }
    }

    private companion object {
        const val FIRST_REMOTE_SEQUENCE = 1L
    }
}

private fun RemoteSessionEvent.toNativeShadowEvent(): NativeSourceEvent = when (this) {
    is RemoteSessionEvent.Output -> NativeSourceEvent.Output(sequence, bytes)
    is RemoteSessionEvent.Resize -> NativeSourceEvent.Resize(sequence, size)
    // A covered Gap marker is an ordered no-op. Native Gap means a real missing source range and
    // would fail-close the core, so it must never be used for an already recovered marker.
    is RemoteSessionEvent.Gap,
    is RemoteSessionEvent.Title,
    is RemoteSessionEvent.InputResult,
    is RemoteSessionEvent.ControlRevoked,
    is RemoteSessionEvent.ControlGranted,
    is RemoteSessionEvent.Lifecycle,
    -> NativeSourceEvent.Advance(sequence, micros = 0)
}

private val JNI_SHADOW_FACTORY = NativeShadowTerminalFactory { size, initialNextSourceSequence ->
    val handle = NativeTerminalHandle.create(
        bridge = JniNativeBridge,
        size = size,
        initialNextSourceSequence = initialNextSourceSequence,
    )
    object : NativeShadowTerminal {
        override fun apply(event: NativeSourceEvent) {
            handle.apply(listOf(event))
        }

        override fun close() = handle.close()
    }
}
