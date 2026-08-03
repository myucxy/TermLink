package dev.termlink.billing

import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.launch

/**
 * Coordinates Play callbacks with server verification. Play state never changes entitlement
 * directly: a purchase must be accepted by the server and followed by an entitlement refresh.
 */
class BillingCoordinator(
    private val adapter: BillingAdapter,
    private val gateway: BillingServerGateway,
    private val scope: CoroutineScope,
) : AutoCloseable {
    private val mutableSnapshot = MutableStateFlow(BillingSnapshot())
    private val acceptedTokens = ConcurrentHashMap.newKeySet<String>()
    private val inFlightTokens = ConcurrentHashMap.newKeySet<String>()
    private val eventJob: Job = scope.launch {
        adapter.events.collect(::handleEvent)
    }
    private var closed = false

    val snapshot: StateFlow<BillingSnapshot> = mutableSnapshot.asStateFlow()

    suspend fun syncCatalog(plans: List<dev.termlink.model.CatalogPlan>) {
        check(!closed) { "Billing is closed" }
        val catalog = BillingCatalogMapper.map(plans)
        mutableSnapshot.value = BillingSnapshot(phase = BillingPhase.CONNECTING)
        runCatching {
            adapter.connect()
            val storeProducts = adapter.queryProducts(catalog.map(BillingProduct::productId))
            val byId = storeProducts.associateBy(StoreProduct::productId)
            val products = catalog.mapNotNull { item ->
                val store = byId[item.productId] ?: return@mapNotNull null
                item.copy(
                    title = store.title.ifBlank { item.title },
                    description = store.description.ifBlank { item.description },
                    formattedPrice = store.formattedPrice,
                    billingPeriod = store.billingPeriod,
                    offerToken = store.offerToken,
                )
            }
            mutableSnapshot.value = BillingSnapshot(phase = BillingPhase.READY, products = products)
        }.onFailure { error ->
            mutableSnapshot.value = BillingSnapshot(phase = BillingPhase.ERROR, message = error.message ?: "Google Play is unavailable")
        }
    }

    suspend fun purchase(productId: String): BillingLaunchResult {
        check(!closed) { "Billing is closed" }
        val product = mutableSnapshot.value.products.firstOrNull { it.productId == productId }
            ?: return BillingLaunchResult.Failed("The selected Google Play product is unavailable")
        mutableSnapshot.value = mutableSnapshot.value.copy(
            phase = BillingPhase.PURCHASING,
            activeProductId = productId,
            message = null,
        )
        return when (val result = adapter.launchPurchase(productId, product.offerToken)) {
            BillingLaunchResult.Launched -> result
            BillingLaunchResult.Canceled -> {
                mutableSnapshot.value = mutableSnapshot.value.copy(phase = BillingPhase.CANCELED)
                result
            }
            is BillingLaunchResult.AlreadyOwned -> {
                mutableSnapshot.value = mutableSnapshot.value.copy(phase = BillingPhase.RESTORING)
                restore()
                result
            }
            is BillingLaunchResult.Failed -> {
                mutableSnapshot.value = mutableSnapshot.value.copy(phase = BillingPhase.ERROR, message = result.message)
                result
            }
        }
    }

    suspend fun restore() {
        check(!closed) { "Billing is closed" }
        mutableSnapshot.value = mutableSnapshot.value.copy(phase = BillingPhase.RESTORING, message = null)
        var pending = false
        runCatching {
            adapter.connect()
            val purchases = adapter.queryExistingPurchases().distinctBy(PurchaseRecord::purchaseToken)
            pending = purchases.any { it.status == PurchaseStatus.PENDING }

            // Restore is deliberately item-scoped. The server validates each
            // token and applies the resulting entitlement; it cannot infer a
            // purchase from an empty request body.
            var restoreFailure: Throwable? = null
            for (purchase in purchases) {
                when (purchase.status) {
                    PurchaseStatus.PENDING -> {
                        mutableSnapshot.value = mutableSnapshot.value.copy(
                            phase = BillingPhase.PENDING,
                            activeProductId = purchase.productId,
                            message = "Google Play payment is pending",
                        )
                    }
                    PurchaseStatus.PURCHASED -> {
                        if (purchase.purchaseToken.isBlank()) {
                            restoreFailure = restoreFailure ?: IllegalStateException(
                                "Google Play returned an unusable purchase state",
                            )
                            mutableSnapshot.value = mutableSnapshot.value.copy(
                                phase = BillingPhase.ERROR,
                                activeProductId = purchase.productId,
                                message = restoreFailure?.message,
                            )
                            continue
                        }
                        mutableSnapshot.value = mutableSnapshot.value.copy(
                            phase = BillingPhase.VERIFYING,
                            activeProductId = purchase.productId,
                            message = null,
                        )
                        runCatching {
                            check(gateway.verifyGooglePurchase(purchase.productId, purchase.purchaseToken)) {
                                "The server rejected the Google Play purchase during restore"
                            }
                            if (!purchase.acknowledged) adapter.acknowledge(purchase)
                            acceptedTokens.add(purchase.purchaseToken)
                        }.onFailure { error ->
                            restoreFailure = restoreFailure ?: error
                            mutableSnapshot.value = mutableSnapshot.value.copy(
                                phase = BillingPhase.ERROR,
                                activeProductId = purchase.productId,
                                message = error.message ?: "Google Play restore verification failed",
                            )
                        }
                    }
                    PurchaseStatus.CANCELED,
                    PurchaseStatus.ALREADY_OWNED,
                    PurchaseStatus.UNSPECIFIED,
                    -> Unit
                }
            }
            check(restoreFailure == null) { restoreFailure?.message ?: "Google Play restore failed" }
            gateway.refreshEntitlement()
            mutableSnapshot.value = mutableSnapshot.value.copy(
                phase = if (pending) BillingPhase.PENDING else BillingPhase.READY,
                message = null,
            )
        }.onFailure { error ->
            mutableSnapshot.value = mutableSnapshot.value.copy(
                phase = BillingPhase.ERROR,
                message = error.message ?: "Google Play restore failed",
            )
        }
    }

    fun reset() {
        if (closed) return
        acceptedTokens.clear()
        inFlightTokens.clear()
        mutableSnapshot.value = BillingSnapshot()
    }

    private suspend fun handleEvent(event: BillingEvent) {
        when (event) {
            BillingEvent.Connected -> Unit
            is BillingEvent.PurchasesUpdated -> {
                if (event.purchases.isEmpty()) {
                    mutableSnapshot.value = mutableSnapshot.value.copy(phase = BillingPhase.CANCELED)
                } else {
                    for (purchase in event.purchases) processPurchase(purchase)
                }
            }
            is BillingEvent.Canceled -> {
                mutableSnapshot.value = mutableSnapshot.value.copy(
                    phase = BillingPhase.CANCELED,
                    activeProductId = event.productId,
                )
            }
            is BillingEvent.AlreadyOwned -> {
                scope.launch { restore() }
            }
            is BillingEvent.Disconnected -> {
                mutableSnapshot.value = mutableSnapshot.value.copy(phase = BillingPhase.DISCONNECTED, message = event.message)
            }
            is BillingEvent.Error -> {
                mutableSnapshot.value = mutableSnapshot.value.copy(phase = BillingPhase.ERROR, message = event.message)
            }
        }
    }

    private suspend fun processPurchase(purchase: PurchaseRecord) {
        if (purchase.status == PurchaseStatus.PENDING) {
            mutableSnapshot.value = mutableSnapshot.value.copy(
                phase = BillingPhase.PENDING,
                activeProductId = purchase.productId,
                message = "Google Play payment is pending",
            )
            return
        }
        if (purchase.status != PurchaseStatus.PURCHASED || purchase.purchaseToken.isBlank()) {
            mutableSnapshot.value = mutableSnapshot.value.copy(
                phase = BillingPhase.ERROR,
                activeProductId = purchase.productId,
                message = "Google Play returned an unusable purchase state",
            )
            return
        }
        if (acceptedTokens.contains(purchase.purchaseToken) || !inFlightTokens.add(purchase.purchaseToken)) return
        mutableSnapshot.value = mutableSnapshot.value.copy(
            phase = BillingPhase.VERIFYING,
            activeProductId = purchase.productId,
            message = null,
        )
        try {
            check(gateway.verifyGooglePurchase(purchase.productId, purchase.purchaseToken)) {
                "The server rejected the Google Play purchase"
            }
            if (!purchase.acknowledged) adapter.acknowledge(purchase)
            acceptedTokens.add(purchase.purchaseToken)
            gateway.refreshEntitlement()
            mutableSnapshot.value = mutableSnapshot.value.copy(phase = BillingPhase.READY, message = null)
        } catch (error: Throwable) {
            mutableSnapshot.value = mutableSnapshot.value.copy(
                phase = BillingPhase.ERROR,
                message = error.message ?: "Google Play verification failed",
            )
        } finally {
            inFlightTokens.remove(purchase.purchaseToken)
        }
    }

    override fun close() {
        if (closed) return
        closed = true
        eventJob.cancel()
        adapter.close()
        acceptedTokens.clear()
        inFlightTokens.clear()
    }
}
