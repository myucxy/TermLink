package dev.termlink.product

import android.app.Activity
import android.content.Context
import android.content.Intent
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import java.io.Closeable
import java.util.concurrent.atomic.AtomicLong

/**
 * Keeps exactly one current UI attachment without letting an older Activity
 * detach a successor created during configuration reconstruction.
 */
internal class LatestAttachmentRegistry<T> {
    private val lock = Any()
    private var nextId = 0L
    private var current: Attachment<T>? = null

    fun attach(value: T): Closeable {
        val attachment = synchronized(lock) {
            Attachment(++nextId, value).also { current = it }
        }
        return Closeable {
            synchronized(lock) {
                if (current?.id == attachment.id) current = null
            }
        }
    }

    fun current(): T? = synchronized(lock) { current?.value }

    private data class Attachment<T>(val id: Long, val value: T)
}

internal class ProductRepositoryLifetime(private val repository: Closeable) : Closeable {
    private val lock = Any()
    private var closed = false

    override fun close() {
        val closeNow = synchronized(lock) {
            if (closed) {
                false
            } else {
                closed = true
                true
            }
        }
        if (closeNow) repository.close()
    }
}

/**
 * Owns foreground generations for the same lifetime as the retained repository.
 * Activity instances are disposable during configuration reconstruction, so an
 * Activity-local counter can repeat and be rejected by the repository's
 * lifecycle fence.
 */
internal class ProductForegroundGenerationClock(initialValue: Long = 0L) {
    private val value = AtomicLong(initialValue.also {
        require(it >= 0L) { "foreground generation must not be negative" }
    })

    fun next(): Long {
        while (true) {
            val current = value.get()
            check(current < Long.MAX_VALUE) { "foreground generation exhausted" }
            val successor = current + 1L
            if (value.compareAndSet(current, successor)) return successor
        }
    }
}

internal data class ProductActivityHost(
    val activity: Activity,
    val launchAuthorization: (Intent) -> Unit,
)

/**
 * Activity configuration changes retain the device runtime and its shared WSS.
 * A real finish clears the ViewModelStore and closes the repository exactly once.
 */
internal class ProductRepositoryViewModel(context: Context) : ViewModel() {
    private val hosts = LatestAttachmentRegistry<ProductActivityHost>()
    private val foregroundGenerations = ProductForegroundGenerationClock()
    private val productionRepository = ProductionProductRepository(
        context = context.applicationContext,
        activityProvider = { hosts.current()?.activity },
        launchAuthorization = { intent ->
            val host = checkNotNull(hosts.current()) {
                "Interactive authorization requires a foreground Activity"
            }
            host.launchAuthorization(intent)
        },
    )
    val repository: ProductRepository = productionRepository
    private val lifetime = ProductRepositoryLifetime(productionRepository)

    fun attach(activity: Activity, launchAuthorization: (Intent) -> Unit): Closeable =
        hosts.attach(ProductActivityHost(activity, launchAuthorization))

    fun handleAuthorizationResult(data: Intent?) {
        productionRepository.handleAuthorizationResult(data)
    }

    fun nextForegroundGeneration(): Long = foregroundGenerations.next()

    override fun onCleared() {
        lifetime.close()
        super.onCleared()
    }

    class Factory(private val context: Context) : ViewModelProvider.Factory {
        @Suppress("UNCHECKED_CAST")
        override fun <T : ViewModel> create(modelClass: Class<T>): T {
            require(modelClass.isAssignableFrom(ProductRepositoryViewModel::class.java)) {
                "Unsupported ViewModel class: ${modelClass.name}"
            }
            return ProductRepositoryViewModel(context.applicationContext) as T
        }
    }
}
