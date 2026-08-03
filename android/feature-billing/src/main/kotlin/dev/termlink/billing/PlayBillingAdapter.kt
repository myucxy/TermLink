package dev.termlink.billing

import android.app.Activity
import android.content.Context
import com.android.billingclient.api.AcknowledgePurchaseParams
import com.android.billingclient.api.BillingClient
import com.android.billingclient.api.BillingClientStateListener
import com.android.billingclient.api.BillingFlowParams
import com.android.billingclient.api.BillingResult
import com.android.billingclient.api.ProductDetails
import com.android.billingclient.api.Purchase
import com.android.billingclient.api.QueryProductDetailsParams
import com.android.billingclient.api.QueryPurchasesParams
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

/** Thin official Play Billing adapter. It contains no entitlement or account policy. */
class PlayBillingAdapter(
    context: Context,
    private val activity: Activity,
) : BillingAdapter {
    private val mutableEvents = MutableSharedFlow<BillingEvent>(replay = 1, extraBufferCapacity = 16)
    private val connectionMutex = Mutex()
    private val detailsById = ConcurrentHashMap<String, ProductDetails>()
    private val client = BillingClient.newBuilder(context.applicationContext)
        .setListener(::onPurchasesUpdated)
        .enablePendingPurchases()
        .build()
    private var closed = false

    override val events: SharedFlow<BillingEvent> = mutableEvents.asSharedFlow()

    override suspend fun connect() {
        check(!closed) { "Billing is closed" }
        connectionMutex.withLock {
            if (client.isReady) return
            suspendCancellableCoroutine<Unit> { continuation ->
                client.startConnection(object : BillingClientStateListener {
                    override fun onBillingSetupFinished(result: BillingResult) {
                        if (result.responseCode == BillingClient.BillingResponseCode.OK) {
                            mutableEvents.tryEmit(BillingEvent.Connected)
                            continuation.resume(Unit)
                        } else {
                            val message = result.debugMessage.ifBlank { "Google Play billing setup failed" }
                            mutableEvents.tryEmit(BillingEvent.Error(message))
                            continuation.resumeWithException(IllegalStateException(message))
                        }
                    }

                    override fun onBillingServiceDisconnected() {
                        mutableEvents.tryEmit(BillingEvent.Disconnected("Google Play billing disconnected"))
                        if (continuation.isActive) continuation.resumeWithException(IllegalStateException("Google Play billing disconnected"))
                    }
                })
                continuation.invokeOnCancellation { client.endConnection() }
            }
        }
    }

    override suspend fun queryProducts(productIds: List<String>): List<StoreProduct> {
        if (productIds.isEmpty()) return emptyList()
        connect()
        val requested = productIds.distinct().map { productId ->
            QueryProductDetailsParams.Product.newBuilder()
                .setProductId(productId)
                .setProductType(BillingClient.ProductType.SUBS)
                .build()
        }
        val result = suspendCancellableCoroutine<Pair<BillingResult, List<ProductDetails>>> { continuation ->
            client.queryProductDetailsAsync(
                QueryProductDetailsParams.newBuilder().setProductList(requested).build(),
            ) { billingResult, products ->
                if (continuation.isActive) continuation.resume(billingResult to products)
            }
        }
        if (result.first.responseCode != BillingClient.BillingResponseCode.OK) {
            val message = result.first.debugMessage.ifBlank { "Google Play products are unavailable" }
            mutableEvents.tryEmit(BillingEvent.Error(message))
            throw IllegalStateException(message)
        }
        detailsById.clear()
        result.second.forEach { detailsById[it.productId] = it }
        return result.second.mapNotNull(::toStoreProduct)
    }

    override fun launchPurchase(productId: String, offerToken: String?): BillingLaunchResult {
        if (closed || !client.isReady) return BillingLaunchResult.Failed("Google Play billing is not connected")
        val details = detailsById[productId] ?: return BillingLaunchResult.Failed("Google Play product details are unavailable")
        val productParams = BillingFlowParams.ProductDetailsParams.newBuilder()
            .setProductDetails(details)
        if (!offerToken.isNullOrBlank()) productParams.setOfferToken(offerToken)
        val result = client.launchBillingFlow(
            activity,
            BillingFlowParams.newBuilder()
                .setProductDetailsParamsList(listOf(productParams.build()))
                .build(),
        )
        return when (result.responseCode) {
            BillingClient.BillingResponseCode.OK -> BillingLaunchResult.Launched
            BillingClient.BillingResponseCode.USER_CANCELED -> BillingLaunchResult.Canceled
            BillingClient.BillingResponseCode.ITEM_ALREADY_OWNED -> BillingLaunchResult.AlreadyOwned(productId)
            else -> BillingLaunchResult.Failed(result.debugMessage.ifBlank { "Google Play purchase could not start" })
        }
    }

    override suspend fun queryExistingPurchases(): List<PurchaseRecord> {
        connect()
        val all = buildList {
            addAll(queryPurchases(BillingClient.ProductType.SUBS))
            addAll(queryPurchases(BillingClient.ProductType.INAPP))
        }
        return all.distinctBy(PurchaseRecord::purchaseToken)
    }

    private suspend fun queryPurchases(type: String): List<PurchaseRecord> {
        val result = suspendCancellableCoroutine<Pair<BillingResult, List<Purchase>>> { continuation ->
            client.queryPurchasesAsync(
                QueryPurchasesParams.newBuilder().setProductType(type).build(),
            ) { billingResult, purchases ->
                if (continuation.isActive) continuation.resume(billingResult to purchases)
            }
        }
        if (result.first.responseCode != BillingClient.BillingResponseCode.OK) {
            if (result.first.responseCode == BillingClient.BillingResponseCode.ITEM_NOT_OWNED) return emptyList()
            throw IllegalStateException(result.first.debugMessage.ifBlank { "Google Play purchases are unavailable" })
        }
        return result.second.mapNotNull(::toPurchaseRecord)
    }

    override suspend fun acknowledge(purchase: PurchaseRecord) {
        if (purchase.acknowledged) return
        val result = suspendCancellableCoroutine<BillingResult> { continuation ->
            client.acknowledgePurchase(
                AcknowledgePurchaseParams.newBuilder().setPurchaseToken(purchase.purchaseToken).build(),
            ) { billingResult ->
                if (continuation.isActive) continuation.resume(billingResult)
            }
        }
        if (result.responseCode != BillingClient.BillingResponseCode.OK) {
            throw IllegalStateException(result.debugMessage.ifBlank { "Google Play acknowledgement failed" })
        }
    }

    private fun onPurchasesUpdated(result: BillingResult, purchases: MutableList<Purchase>?) {
        when (result.responseCode) {
            BillingClient.BillingResponseCode.OK -> {
                val records = purchases.orEmpty().mapNotNull(::toPurchaseRecord)
                if (records.isEmpty()) mutableEvents.tryEmit(BillingEvent.Canceled())
                else mutableEvents.tryEmit(BillingEvent.PurchasesUpdated(records))
            }
            BillingClient.BillingResponseCode.USER_CANCELED -> mutableEvents.tryEmit(BillingEvent.Canceled())
            BillingClient.BillingResponseCode.ITEM_ALREADY_OWNED -> {
                mutableEvents.tryEmit(BillingEvent.AlreadyOwned(purchases?.firstOrNull()?.products?.firstOrNull().orEmpty()))
            }
            BillingClient.BillingResponseCode.SERVICE_DISCONNECTED -> {
                mutableEvents.tryEmit(BillingEvent.Disconnected("Google Play billing disconnected"))
            }
            else -> mutableEvents.tryEmit(BillingEvent.Error(result.debugMessage.ifBlank { "Google Play purchase failed" }))
        }
    }

    private fun toPurchaseRecord(purchase: Purchase): PurchaseRecord? {
        val productId = purchase.products.firstOrNull()?.takeIf(String::isNotBlank) ?: return null
        val status = when (purchase.purchaseState) {
            Purchase.PurchaseState.PURCHASED -> PurchaseStatus.PURCHASED
            Purchase.PurchaseState.PENDING -> PurchaseStatus.PENDING
            else -> PurchaseStatus.UNSPECIFIED
        }
        return PurchaseRecord(productId, purchase.purchaseToken, status, purchase.isAcknowledged)
    }

    private fun toStoreProduct(details: ProductDetails): StoreProduct {
        val offer = details.subscriptionOfferDetails?.firstOrNull()
        val phase = offer?.pricingPhases?.pricingPhaseList?.firstOrNull()
        return StoreProduct(
            productId = details.productId,
            title = details.title,
            description = details.description,
            formattedPrice = phase?.formattedPrice.orEmpty(),
            billingPeriod = phase?.billingPeriod,
            offerToken = offer?.offerToken,
        )
    }

    override fun close() {
        if (closed) return
        closed = true
        detailsById.clear()
        if (client.isReady) client.endConnection()
    }
}
