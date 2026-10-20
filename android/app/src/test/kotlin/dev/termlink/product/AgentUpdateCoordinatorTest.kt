package dev.termlink.product

import dev.termlink.model.DeviceSummary
import dev.termlink.model.DeviceTrustState
import dev.termlink.model.DeviceType
import dev.termlink.model.PcCapabilityIds
import dev.termlink.model.PcSummary
import java.time.Instant
import java.io.IOException
import java.util.UUID
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.withTimeout
import org.junit.Assert.*
import org.junit.Test

class AgentUpdateCoordinatorTest {
    private val first = pc("First PC")
    private val second = pc("Second PC")
    private fun pc(name: String) = PcSummary(UUID.randomUUID(), UUID.randomUUID(), name, "0.3.10.140",
        remoteRoutingEnabled = true, online = true, lastSeenAt = Instant.EPOCH,
        capabilities = setOf(PcCapabilityIds.REMOTE_AGENT_UPDATE_V1))
    private fun progress(pc: PcSummary, status: String = "PROCESSING", phase: String = "DOWNLOADING") =
        AgentUpdateProgress(UUID.randomUUID(), pc.id, pc.agentVersion, 140, "0.3.11", 141, status, phase, 35)

    private class Repository : ProductRepository by UnavailableProductRepository() {
        val active = mutableMapOf<UUID, AgentUpdateProgress>()
        val events = mutableMapOf<UUID, Channel<AgentUpdateProgress>>()
        var starts = 0
        var cancels = 0
        override suspend fun activeAgentUpdate(pcId: UUID) = active[pcId]?.takeUnless { it.terminal }
        override suspend fun agentUpdateEligibility(pcId: UUID) = AgentUpdateEligibility(pcId, true,
            currentVersion = "0.3.10.140", currentBuild = 140, targetVersion = "0.3.11", targetBuild = 141)
        override suspend fun runAgentUpdate(pcId: UUID, expectedTargetBuild: Long, onProgress: (AgentUpdateProgress) -> Unit): AgentUpdateProgress {
            starts++
            return monitorAgentUpdate(pcId, active.getValue(pcId).id, onProgress)
        }
        override suspend fun monitorAgentUpdate(pcId: UUID, updateId: UUID, onProgress: (AgentUpdateProgress) -> Unit): AgentUpdateProgress {
            var value = active.getValue(pcId)
            assertEquals(updateId, value.id)
            onProgress(value)
            while (!value.terminal) {
                value = events.getOrPut(pcId) { Channel(Channel.UNLIMITED) }.receive()
                active[pcId] = value
                onProgress(value)
            }
            return value
        }
        override suspend fun cancelAgentUpdate(pcId: UUID, updateId: UUID): AgentUpdateProgress {
            cancels++
            return active.getValue(pcId).copy(status = "CANCELLED", phase = "FAILED")
        }
    }

    @Test fun minimizedProgressAndCompletionNeverReopenAndOtherPcKeepsItsOwnTask() = runBlocking {
        val scope = CoroutineScope(Dispatchers.Unconfined + SupervisorJob())
        val repository = Repository()
        val a = progress(first); val b = progress(second)
        repository.active[first.id] = a; repository.active[second.id] = b
        val saved = mutableMapOf<UUID, UUID>()
        val coordinator = AgentUpdateCoordinator(repository, scope, saveTasks = { saved.clear(); saved.putAll(it) })
        try {
            coordinator.open(first)
            assertEquals(first.id, coordinator.state.value.selectedPcId)
            coordinator.dismiss()
            repository.events.getValue(first.id).send(a.copy(progressPercent = 75))
            assertNull(coordinator.state.value.dialog)
            coordinator.open(second)
            repository.events.getValue(first.id).send(a.copy(status = "SUCCEEDED", phase = "SUCCEEDED", progressPercent = 100))
            assertEquals(second.id, coordinator.state.value.dialog?.pc?.id)
            assertTrue((coordinator.state.value.devices[first.id] as AgentUpdateDialogState.Running).progress.terminal)
            assertFalse((coordinator.state.value.devices[second.id] as AgentUpdateDialogState.Running).progress.terminal)
            assertEquals(mapOf(first.id to a.id, second.id to b.id), saved)
            coordinator.dismiss()
            repository.events.getValue(second.id).send(b.copy(status = "SUCCEEDED", phase = "SUCCEEDED"))
            assertNull(coordinator.state.value.dialog)
            assertEquals(0, repository.starts)
            assertEquals(0, repository.cancels)
        } finally { coordinator.close(); scope.cancel() }
    }

    @Test fun restartRestoresBothExactTasksIncludingFinishedResultWithoutDialog() {
        val scope = CoroutineScope(Dispatchers.Unconfined + SupervisorJob())
        val repository = Repository()
        val a = progress(first); val b = progress(second, "SUCCEEDED", "SUCCEEDED")
        repository.active[first.id] = a; repository.active[second.id] = b
        val coordinator = AgentUpdateCoordinator(repository, scope, mapOf(first.id to a.id, second.id to b.id))
        try {
            coordinator.recover(listOf(first.copy(online = false), second))
            assertEquals(2, coordinator.state.value.devices.size)
            assertNull(coordinator.state.value.dialog)
            assertEquals(b.id, (coordinator.state.value.devices[second.id] as AgentUpdateDialogState.Running).progress.id)
            coordinator.recover(listOf(first, second))
            assertEquals(0, repository.starts)
            assertNull(coordinator.state.value.dialog)
        } finally { coordinator.close(); scope.cancel() }
    }

    @Test fun cancellationRespectsPhaseAndDoesNotCancelAnotherPcMonitor() = runBlocking {
        val scope = CoroutineScope(Dispatchers.Unconfined + SupervisorJob())
        val repository = Repository()
        val a = progress(first); val b = progress(second, phase = "INSTALLING")
        repository.active[first.id] = a; repository.active[second.id] = b
        val coordinator = AgentUpdateCoordinator(repository, scope)
        try {
            coordinator.recover(listOf(first, second))
            coordinator.cancel(coordinator.state.value.devices[second.id] as AgentUpdateDialogState.Running)
            assertEquals(0, repository.cancels)
            coordinator.cancel(coordinator.state.value.devices[first.id] as AgentUpdateDialogState.Running)
            assertEquals(1, repository.cancels)
            assertEquals("CANCELLED", (coordinator.state.value.devices[first.id] as AgentUpdateDialogState.Running).progress.status)
            repository.events.getValue(second.id).send(b.copy(status = "SUCCEEDED", phase = "SUCCEEDED"))
            assertEquals("SUCCEEDED", (coordinator.state.value.devices[second.id] as AgentUpdateDialogState.Running).progress.status)
            assertNull(coordinator.state.value.dialog)
        } finally { coordinator.close(); scope.cancel() }
    }

    @Test fun temporaryQueryFailureRetainsProgressAndRecoversWithoutReopening() = runBlocking {
        val scope = CoroutineScope(Dispatchers.Unconfined + SupervisorJob())
        val initial = progress(first)
        var attempts = 0
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun activeAgentUpdate(pcId: UUID) = initial
            override suspend fun monitorAgentUpdate(pcId: UUID, updateId: UUID, onProgress: (AgentUpdateProgress) -> Unit): AgentUpdateProgress {
                attempts++
                onProgress(initial)
                if (attempts == 1) throw IOException("temporary fixture outage")
                return initial.copy(status = "SUCCEEDED", phase = "SUCCEEDED", progressPercent = 100)
            }
        }
        val coordinator = AgentUpdateCoordinator(repository, scope)
        try {
            coordinator.recover(listOf(first))
            val waiting = coordinator.state.value.devices[first.id] as AgentUpdateDialogState.Running
            assertTrue(waiting.refreshing)
            assertEquals(initial.id, waiting.progress.id)
            assertFalse(waiting.progress.terminal)
            withTimeout(8_000) { coordinator.state.first { (it.devices[first.id] as? AgentUpdateDialogState.Running)?.progress?.terminal == true } }
            assertEquals(2, attempts)
            assertNull(coordinator.state.value.dialog)
        } finally { coordinator.close(); scope.cancel() }
    }

    @Test fun updatingOfflineCardRemainsVisibleWithoutResurrectingRemovedDeviceOrBlockingPeers() {
        val a = first.copy(online = false)
        val device = DeviceSummary(a.deviceId, DeviceType.WINDOWS_PC, a.name, "fixture", ByteArray(32),
            trustState = DeviceTrustState.TRUSTED, createdAt = Instant.EPOCH)
        val peerDevice = device.copy(id = second.deviceId)
        val running = AgentUpdateDialogState.Running(a, progress(a, phase = "INSTALLING"))
        val snapshot = ProductSnapshot(pcs = listOf(a, second), devices = listOf(device, peerDevice))
        val updates = mapOf(a.id to running)
        assertEquals(listOf(a.id, second.id), visibleAgentUpdatePcs(snapshot, updates).map { it.id })
        assertTrue(running.blocksPcActions())
        assertFalse(AgentUpdateDialogState.Running(second, progress(second, "SUCCEEDED", "SUCCEEDED")).blocksPcActions())
        assertEquals(listOf(second.id), visibleAgentUpdatePcs(snapshot.copy(devices = listOf(peerDevice)), updates).map { it.id })
        assertFalse(visibleAgentUpdatePcs(snapshot, emptyMap()).any { it.id == a.id })
    }

    @Test fun dismissingTerminalResultForgetsOnlyThatTaskAcrossRestart() {
        val scope = CoroutineScope(Dispatchers.Unconfined + SupervisorJob())
        val repository = Repository()
        val a = progress(first, "SUCCEEDED", "SUCCEEDED"); val b = progress(second)
        repository.active[first.id] = a; repository.active[second.id] = b
        var saved = mapOf(first.id to a.id, second.id to b.id)
        val coordinator = AgentUpdateCoordinator(repository, scope, saved, { saved = it })
        try {
            coordinator.recover(listOf(first, second))
            coordinator.open(first)
            coordinator.dismissNotice(second.id)
            assertFalse(second.id in coordinator.state.value.hiddenNotices)
            assertEquals(2, saved.size)
            coordinator.dismissNotice(first.id)
            assertNull(coordinator.state.value.dialog)
            assertFalse(first.id in coordinator.state.value.devices)
            assertEquals(mapOf(second.id to b.id), saved)
            coordinator.close()
            val restored = AgentUpdateCoordinator(repository, scope, saved)
            try {
                restored.recover(listOf(first, second))
                assertFalse(first.id in restored.state.value.devices)
                assertTrue(second.id in restored.state.value.devices)
                assertEquals(0, repository.cancels)
            } finally { restored.close() }
        } finally { coordinator.close(); scope.cancel() }
    }

    @Test fun dismissedUnknownStatusKeepsTaskAndRevealsNewResultAfterRestart() {
        val scope = CoroutineScope(Dispatchers.Unconfined + SupervisorJob())
        val update = progress(first)
        var offline = true
        var saved = mapOf(first.id to update.id)
        var hidden = emptySet<UUID>()
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun monitorAgentUpdate(pcId: UUID, updateId: UUID, onProgress: (AgentUpdateProgress) -> Unit): AgentUpdateProgress {
                if (offline) throw IOException("unknown task status")
                return update.copy(status = "SUCCEEDED", phase = "SUCCEEDED").also(onProgress)
            }
        }
        val coordinator = AgentUpdateCoordinator(repository, scope, saved, { saved = it }, hidden, { hidden = it })
        try {
            coordinator.recover(listOf(first))
            assertTrue(coordinator.state.value.devices[first.id] is AgentUpdateDialogState.Failure)
            coordinator.dismissNotice(first.id)
            assertEquals(setOf(first.id), hidden)
            assertEquals(mapOf(first.id to update.id), saved)
            coordinator.close()
            val restored = AgentUpdateCoordinator(repository, scope, saved, { saved = it }, hidden, { hidden = it })
            try {
                restored.recover(listOf(first))
                assertTrue(first.id in restored.state.value.hiddenNotices)
            } finally { restored.close() }
            offline = false
            val connected = AgentUpdateCoordinator(repository, scope, saved, { saved = it }, hidden, { hidden = it })
            try {
                connected.recover(listOf(first))
                assertFalse(first.id in connected.state.value.hiddenNotices)
                assertTrue((connected.state.value.devices[first.id] as AgentUpdateDialogState.Running).progress.terminal)
                assertNull(connected.state.value.dialog)
            } finally { connected.close() }
        } finally { coordinator.close(); scope.cancel() }
    }

    @Test fun updateBadgeUsesConfirmedBuildAndSilentlyCachesChecks() {
        val scope = CoroutineScope(Dispatchers.Unconfined + SupervisorJob())
        var time = 0L
        var checks = 0
        var fail = false
        val offer = AgentUpdateEligibility(first.id, true, currentVersion = first.agentVersion,
            currentBuild = 140, targetVersion = "0.3.11", targetBuild = 141)
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun agentUpdateEligibility(pcId: UUID): AgentUpdateEligibility {
                checks++
                if (fail) throw IOException("catalog unavailable")
                return offer
            }
        }
        val coordinator = AgentUpdateCoordinator(repository, scope, nowMillis = { time })
        try {
            coordinator.refreshOffers(listOf(first, second.copy(platform = "linux")))
            assertEquals(1, checks)
            assertTrue(coordinator.state.value.offers.getValue(first.id).isUpdateAvailableFor(first, null))
            coordinator.refreshOffers(listOf(first))
            assertEquals(1, checks)
            time = 300_000
            fail = true
            coordinator.refreshOffers(listOf(first))
            assertEquals(2, checks)
            assertTrue(coordinator.state.value.offers.isEmpty())
            assertTrue(coordinator.state.value.devices.isEmpty())
            assertNull(coordinator.state.value.dialog)
            fail = false
            coordinator.refreshOffers(listOf(first), force = true)
            assertEquals(3, checks)
            coordinator.refreshOffers(listOf(first.copy(online = false)))
            assertTrue(coordinator.state.value.offers.isEmpty())
        } finally { coordinator.close(); scope.cancel() }
        assertFalse(offer.copy(targetBuild = 140).isUpdateAvailableFor(first, null))
        assertFalse(offer.copy(eligible = false).isUpdateAvailableFor(first, null))
        assertFalse(offer.copy(pcId = second.id).isUpdateAvailableFor(first, null))
        assertFalse(offer.isUpdateAvailableFor(first.copy(agentVersion = "0.3.12.142"), null))
        assertFalse(offer.isUpdateAvailableFor(first, AgentUpdateDialogState.Running(first, progress(first))))
        assertFalse(offer.isUpdateAvailableFor(first, AgentUpdateDialogState.Running(first,
            progress(first, "SUCCEEDED", "SUCCEEDED").copy(observedAgentBuild = 142))))
    }
}
