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
        override suspend fun activeAgentUpdate(pcId: UUID) = active[pcId]
        override suspend fun agentUpdateEligibility(pcId: UUID) = AgentUpdateEligibility(pcId, true,
            currentVersion = "0.3.10.140", targetVersion = "0.3.11", targetBuild = 141)
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
}
