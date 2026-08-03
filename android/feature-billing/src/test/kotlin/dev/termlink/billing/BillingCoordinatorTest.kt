package dev.termlink.billing

import dev.termlink.model.CatalogPlan
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.yield
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class BillingCoordinatorTest {
    @Test
    fun catalogMappingUsesServerGoogleProductIdsOnly() {
        val mapped = BillingCatalogMapper.map(
            listOf(
                plan("basic", mapOf("google_play" to "play.basic.monthly")),
                plan("web-only", mapOf("web" to "web.basic")),
                plan("empty", mapOf("google_play" to " ")),
            ),
        )

        assertEquals(1, mapped.size)
        assertEquals("basic", mapped.single().planKey)
        assertEquals("play.basic.monthly", mapped.single().productId)
    }

    @Test
    fun pendingPurchaseDoesNotCallServerOrAcknowledge() = runBlocking {
        val adapter = FakeAdapter()
        val gateway = FakeGateway()
        val scope = CoroutineScope(Dispatchers.Unconfined + SupervisorJob())
        val coordinator = BillingCoordinator(adapter, gateway, scope)
        coordinator.syncCatalog(listOf(plan("basic", mapOf("google_play" to "play.basic"))))

        adapter.emit(
            BillingEvent.PurchasesUpdated(
                listOf(PurchaseRecord("play.basic", "pending-token", PurchaseStatus.PENDING, acknowledged = false)),
            ),
        )
        yield()

        assertEquals(BillingPhase.PENDING, coordinator.snapshot.value.phase)
        assertEquals(0, gateway.verifyCalls)
        assertEquals(0, adapter.acknowledged.size)
        coordinator.close()
    }

    @Test
    fun duplicatePurchaseTokenIsVerifiedOnceAndAcknowledgedOnlyWhenNeeded() = runBlocking {
        val adapter = FakeAdapter()
        val gateway = FakeGateway()
        val coordinator = BillingCoordinator(adapter, gateway, CoroutineScope(Dispatchers.Unconfined + SupervisorJob()))
        coordinator.syncCatalog(listOf(plan("basic", mapOf("google_play" to "play.basic"))))
        val purchase = PurchaseRecord("play.basic", "same-token", PurchaseStatus.PURCHASED, acknowledged = false)

        adapter.emit(BillingEvent.PurchasesUpdated(listOf(purchase)))
        adapter.emit(BillingEvent.PurchasesUpdated(listOf(purchase)))
        yield()

        assertEquals(1, gateway.verifyCalls)
        assertEquals(listOf("same-token"), adapter.acknowledged)
        assertEquals(1, gateway.refreshCalls)
        assertEquals(BillingPhase.READY, coordinator.snapshot.value.phase)
        coordinator.close()
    }

    @Test
    fun acknowledgedPurchaseSkipsPlayAcknowledgementButRefreshesServerEntitlement() = runBlocking {
        val adapter = FakeAdapter()
        val gateway = FakeGateway()
        val coordinator = BillingCoordinator(adapter, gateway, CoroutineScope(Dispatchers.Unconfined + SupervisorJob()))
        coordinator.syncCatalog(listOf(plan("basic", mapOf("google_play" to "play.basic"))))

        adapter.emit(
            BillingEvent.PurchasesUpdated(
                listOf(PurchaseRecord("play.basic", "ack-token", PurchaseStatus.PURCHASED, acknowledged = true)),
            ),
        )
        yield()

        assertEquals(1, gateway.verifyCalls)
        assertTrue(adapter.acknowledged.isEmpty())
        assertEquals(1, gateway.refreshCalls)
        coordinator.close()
    }

    @Test
    fun rejectedPurchaseDoesNotRefreshEntitlement() = runBlocking {
        val adapter = FakeAdapter()
        val gateway = FakeGateway(verifyResult = false)
        val coordinator = BillingCoordinator(adapter, gateway, CoroutineScope(Dispatchers.Unconfined + SupervisorJob()))
        coordinator.syncCatalog(listOf(plan("basic", mapOf("google_play" to "play.basic"))))

        adapter.emit(
            BillingEvent.PurchasesUpdated(
                listOf(PurchaseRecord("play.basic", "rejected-token", PurchaseStatus.PURCHASED, acknowledged = false)),
            ),
        )
        yield()

        assertEquals(BillingPhase.ERROR, coordinator.snapshot.value.phase)
        assertEquals(1, gateway.verifyCalls)
        assertEquals(0, gateway.refreshCalls)
        assertTrue(adapter.acknowledged.isEmpty())
        coordinator.close()
    }

    @Test
    fun restoreVerifiesAndAcknowledgesEachPurchasedToken() = runBlocking {
        val adapter = FakeAdapter().apply {
            existing = listOf(
                PurchaseRecord("play.basic", "restore-one", PurchaseStatus.PURCHASED, acknowledged = false),
                PurchaseRecord("play.pro", "restore-two", PurchaseStatus.PURCHASED, acknowledged = true),
            )
        }
        val gateway = FakeGateway()
        val coordinator = BillingCoordinator(adapter, gateway, CoroutineScope(Dispatchers.Unconfined + SupervisorJob()))

        coordinator.restore()

        assertEquals(2, gateway.verifyCalls)
        assertEquals(listOf("restore-one"), adapter.acknowledged)
        assertEquals(1, gateway.refreshCalls)
        assertEquals(BillingPhase.READY, coordinator.snapshot.value.phase)
        coordinator.close()
    }

    @Test
    fun restoreSkipsPendingPurchasesAndStillRefreshesEntitlement() = runBlocking {
        val adapter = FakeAdapter().apply {
            existing = listOf(
                PurchaseRecord("play.basic", "pending-token", PurchaseStatus.PENDING, acknowledged = false),
            )
        }
        val gateway = FakeGateway()
        val coordinator = BillingCoordinator(adapter, gateway, CoroutineScope(Dispatchers.Unconfined + SupervisorJob()))

        coordinator.restore()

        assertEquals(0, gateway.verifyCalls)
        assertTrue(adapter.acknowledged.isEmpty())
        assertEquals(1, gateway.refreshCalls)
        assertEquals(BillingPhase.PENDING, coordinator.snapshot.value.phase)
        coordinator.close()
    }

    @Test
    fun emptyRestoreOnlyRefreshesEntitlement() = runBlocking {
        val adapter = FakeAdapter()
        val gateway = FakeGateway()
        val coordinator = BillingCoordinator(adapter, gateway, CoroutineScope(Dispatchers.Unconfined + SupervisorJob()))

        coordinator.restore()

        assertEquals(0, gateway.verifyCalls)
        assertEquals(1, gateway.refreshCalls)
        assertEquals(BillingPhase.READY, coordinator.snapshot.value.phase)
        coordinator.close()
    }

    @Test
    fun rejectedRestoredPurchaseIsNotAcknowledgedOrRefreshed() = runBlocking {
        val adapter = FakeAdapter().apply {
            existing = listOf(
                PurchaseRecord("play.basic", "rejected-restore", PurchaseStatus.PURCHASED, acknowledged = false),
            )
        }
        val gateway = FakeGateway(verifyResult = false)
        val coordinator = BillingCoordinator(adapter, gateway, CoroutineScope(Dispatchers.Unconfined + SupervisorJob()))

        coordinator.restore()

        assertEquals(BillingPhase.ERROR, coordinator.snapshot.value.phase)
        assertEquals(1, gateway.verifyCalls)
        assertTrue(adapter.acknowledged.isEmpty())
        assertEquals(0, gateway.refreshCalls)
        coordinator.close()
    }

    private fun plan(key: String, channelProducts: Map<String, String>) = CatalogPlan(
        key = key,
        displayName = key,
        description = "description",
        billingPeriod = "MONTHLY",
        maxRemotePcs = 1,
        maxLiveSessions = 1,
        featureFlags = emptyMap(),
        channelProducts = channelProducts,
    )

    private class FakeAdapter : BillingAdapter {
        private val mutableEvents = MutableSharedFlow<BillingEvent>(replay = 1, extraBufferCapacity = 8)
        val acknowledged = mutableListOf<String>()
        var products: List<StoreProduct> = listOf(
            StoreProduct("play.basic", "Basic (localized)", "Localized description", "$4.99", "P1M", "offer-token"),
        )
        var existing: List<PurchaseRecord> = emptyList()

        override val events = mutableEvents

        override suspend fun connect() = Unit

        override suspend fun queryProducts(productIds: List<String>): List<StoreProduct> =
            products.filter { it.productId in productIds }

        override fun launchPurchase(productId: String, offerToken: String?): BillingLaunchResult =
            BillingLaunchResult.Launched

        override suspend fun queryExistingPurchases(): List<PurchaseRecord> = existing

        override suspend fun acknowledge(purchase: PurchaseRecord) {
            acknowledged += purchase.purchaseToken
        }

        suspend fun emit(event: BillingEvent) {
            mutableEvents.emit(event)
        }

        override fun close() = Unit
    }

    private class FakeGateway(private val verifyResult: Boolean = true) : BillingServerGateway {
        var verifyCalls = 0
        var refreshCalls = 0

        override suspend fun verifyGooglePurchase(productId: String, purchaseToken: String): Boolean {
            verifyCalls++
            return verifyResult
        }

        override suspend fun refreshEntitlement() {
            refreshCalls++
        }
    }
}
