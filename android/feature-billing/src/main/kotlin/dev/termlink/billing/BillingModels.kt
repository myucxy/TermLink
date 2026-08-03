package dev.termlink.billing

import dev.termlink.model.CatalogPlan
import java.io.Closeable
import kotlinx.coroutines.flow.SharedFlow

enum class BillingPhase {
    IDLE,
    CONNECTING,
    READY,
    PURCHASING,
    VERIFYING,
    RESTORING,
    PENDING,
    CANCELED,
    DISCONNECTED,
    ERROR,
}

data class BillingProduct(
    val planKey: String,
    val productId: String,
    val title: String,
    val description: String,
    val formattedPrice: String? = null,
    val billingPeriod: String? = null,
    val offerToken: String? = null,
)

data class BillingSnapshot(
    val phase: BillingPhase = BillingPhase.IDLE,
    val products: List<BillingProduct> = emptyList(),
    val activeProductId: String? = null,
    val message: String? = null,
)

enum class PurchaseStatus {
    PURCHASED,
    PENDING,
    CANCELED,
    ALREADY_OWNED,
    UNSPECIFIED,
}

data class PurchaseRecord(
    val productId: String,
    val purchaseToken: String,
    val status: PurchaseStatus,
    val acknowledged: Boolean,
)

data class StoreProduct(
    val productId: String,
    val title: String,
    val description: String,
    val formattedPrice: String,
    val billingPeriod: String?,
    val offerToken: String?,
)

sealed interface BillingEvent {
    data object Connected : BillingEvent
    data class PurchasesUpdated(val purchases: List<PurchaseRecord>) : BillingEvent
    data class Canceled(val productId: String? = null) : BillingEvent
    data class AlreadyOwned(val productId: String) : BillingEvent
    data class Disconnected(val message: String) : BillingEvent
    data class Error(val message: String) : BillingEvent
}

sealed interface BillingLaunchResult {
    data object Launched : BillingLaunchResult
    data object Canceled : BillingLaunchResult
    data class AlreadyOwned(val productId: String) : BillingLaunchResult
    data class Failed(val message: String) : BillingLaunchResult
}

interface BillingAdapter : Closeable {
    val events: SharedFlow<BillingEvent>

    suspend fun connect()

    suspend fun queryProducts(productIds: List<String>): List<StoreProduct>

    fun launchPurchase(productId: String, offerToken: String?): BillingLaunchResult

    suspend fun queryExistingPurchases(): List<PurchaseRecord>

    suspend fun acknowledge(purchase: PurchaseRecord)
}

interface BillingServerGateway {
    suspend fun verifyGooglePurchase(productId: String, purchaseToken: String): Boolean

    suspend fun refreshEntitlement()
}

object BillingCatalogMapper {
    private val channelKeys = setOf("google_play", "googleplay", "google")

    fun map(plans: List<CatalogPlan>): List<BillingProduct> = plans.mapNotNull { plan ->
        val productId = plan.channelProducts.entries
            .firstOrNull { channelKeys.contains(it.key.trim().lowercase()) }
            ?.value
            ?.trim()
            ?.takeIf(String::isNotEmpty)
            ?: return@mapNotNull null
        BillingProduct(
            planKey = plan.key,
            productId = productId,
            title = plan.displayName,
            description = plan.description,
        )
    }
}
