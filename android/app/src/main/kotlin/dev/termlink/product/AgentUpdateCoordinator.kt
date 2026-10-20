package dev.termlink.product

import android.content.Context
import dev.termlink.model.PcCapabilityIds
import dev.termlink.model.PcSummary
import dev.termlink.model.isPcAgent
import dev.termlink.network.production.TermLinkApiException
import java.security.MessageDigest
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

internal sealed interface AgentUpdateDialogState {
    val pc: PcSummary
    data class Loading(override val pc: PcSummary) : AgentUpdateDialogState
    data class Offer(override val pc: PcSummary, val eligibility: AgentUpdateEligibility) : AgentUpdateDialogState
    data class Starting(override val pc: PcSummary, val eligibility: AgentUpdateEligibility) : AgentUpdateDialogState
    data class Running(
        override val pc: PcSummary,
        val progress: AgentUpdateProgress,
        val refreshing: Boolean = false,
        val cancelling: Boolean = false,
    ) : AgentUpdateDialogState
    data class Failure(override val pc: PcSummary, val error: Throwable) : AgentUpdateDialogState
}

internal data class AgentUpdateUiState(
    val devices: Map<UUID, AgentUpdateDialogState> = emptyMap(),
    val selectedPcId: UUID? = null,
) {
    val dialog: AgentUpdateDialogState? get() = devices[selectedPcId]
}

/** Task IDs survive process death; dialog visibility is deliberately never persisted. */
internal class AgentUpdateCoordinator(
    private val repository: ProductRepository,
    parentScope: CoroutineScope,
    restoredTasks: Map<UUID, UUID> = emptyMap(),
    private val saveTasks: (Map<UUID, UUID>) -> Unit = {},
) {
    private val scope = CoroutineScope(parentScope.coroutineContext + SupervisorJob(parentScope.coroutineContext[Job]))
    private val mutableState = MutableStateFlow(AgentUpdateUiState())
    val state = mutableState.asStateFlow()
    private val tasks = restoredTasks.toMutableMap()
    private val jobs = mutableMapOf<UUID, Job>()
    private val cancellations = mutableSetOf<UUID>()

    private fun put(pcId: UUID, value: AgentUpdateDialogState?) {
        val current = mutableState.value
        mutableState.value = current.copy(devices = if (value == null) current.devices - pcId else current.devices + (pcId to value))
    }

    private fun progress(pc: PcSummary, value: AgentUpdateProgress) {
        require(value.pcId == pc.id)
        if (tasks[pc.id] != value.id) {
            tasks[pc.id] = value.id
            saveTasks(tasks.toMap())
        }
        put(pc.id, AgentUpdateDialogState.Running(pc, value, cancelling = pc.id in cancellations))
    }

    private fun launch(pc: PcSummary, operation: suspend () -> Unit) {
        if (jobs[pc.id]?.isActive == true) return
        val job = scope.launch(start = CoroutineStart.LAZY) {
            try { operation() }
            catch (error: CancellationException) { throw error }
            catch (error: Throwable) { put(pc.id, AgentUpdateDialogState.Failure(pc, error)) }
            finally {
                if (jobs[pc.id] === currentCoroutineContext()[Job]) jobs.remove(pc.id)
            }
        }
        jobs[pc.id] = job
        job.start()
    }

    fun dismiss() { mutableState.value = mutableState.value.copy(selectedPcId = null) }

    fun open(pc: PcSummary, checkForUpdate: Boolean = false) {
        mutableState.value = mutableState.value.copy(selectedPcId = pc.id)
        val current = state.value.devices[pc.id]
        if (jobs[pc.id]?.isActive == true || current is AgentUpdateDialogState.Starting) {
            if (current == null) put(pc.id, AgentUpdateDialogState.Loading(pc))
            return
        }
        if (current is AgentUpdateDialogState.Running && (!current.progress.terminal || !checkForUpdate)) return
        put(pc.id, AgentUpdateDialogState.Loading(pc))
        launch(pc) {
            val active = repository.activeAgentUpdate(pc.id)
            if (active != null) {
                progress(pc, active)
                watch(pc, active.id)
            } else {
                put(pc.id, AgentUpdateDialogState.Offer(pc, repository.agentUpdateEligibility(pc.id)))
            }
        }
    }

    fun recover(pcs: List<PcSummary>) {
        for (pc in pcs.filter { it.platform == "windows" && PcCapabilityIds.REMOTE_AGENT_UPDATE_V1 in it.capabilities }) {
            if (state.value.devices.containsKey(pc.id) || jobs[pc.id]?.isActive == true) continue
            val retained = tasks[pc.id]
            if (retained != null) put(pc.id, AgentUpdateDialogState.Loading(pc))
            launch(pc) {
                if (retained != null) watch(pc, retained)
                else {
                    val active = repository.activeAgentUpdate(pc.id)
                    if (active != null) {
                        progress(pc, active)
                        watch(pc, active.id)
                    } else if (state.value.selectedPcId == pc.id) {
                        put(pc.id, AgentUpdateDialogState.Offer(pc, repository.agentUpdateEligibility(pc.id)))
                    } else {
                        put(pc.id, null)
                    }
                }
            }
        }
    }

    fun install(offer: AgentUpdateDialogState.Offer) {
        val pc = offer.pc
        val target = offer.eligibility.targetBuild ?: return
        if (state.value.devices[pc.id] != offer || jobs[pc.id]?.isActive == true) return
        put(pc.id, AgentUpdateDialogState.Starting(pc, offer.eligibility))
        launch(pc) {
            try {
                progress(pc, repository.runAgentUpdate(pc.id, target) { progress(pc, it) })
            } catch (error: CancellationException) {
                throw error
            } catch (error: Throwable) {
                val running = state.value.devices[pc.id] as? AgentUpdateDialogState.Running
                if (running == null) throw error
                if (!running.progress.terminal) {
                    put(pc.id, running.copy(refreshing = true))
                    delay(2_000)
                    watch(pc, running.progress.id)
                }
            }
        }
    }

    private suspend fun watch(pc: PcSummary, updateId: UUID) {
        while (true) {
            try {
                progress(pc, repository.monitorAgentUpdate(pc.id, updateId) { progress(pc, it) })
                return
            } catch (error: CancellationException) {
                throw error
            } catch (error: Throwable) {
                if (error is TermLinkApiException && error.status in setOf(401, 403, 404)) throw error
                val running = state.value.devices[pc.id] as? AgentUpdateDialogState.Running
                if (running?.progress?.terminal == true) return
                put(pc.id, running?.copy(refreshing = true) ?: AgentUpdateDialogState.Failure(pc, error))
                delay(5_000)
            }
        }
    }

    fun cancel(running: AgentUpdateDialogState.Running) {
        val pc = running.pc
        val current = state.value.devices[pc.id] as? AgentUpdateDialogState.Running ?: return
        if (current.progress.id != running.progress.id || !current.progress.cancelable || !cancellations.add(pc.id)) return
        put(pc.id, current.copy(cancelling = true))
        scope.launch {
            try {
                val result = repository.cancelAgentUpdate(pc.id, current.progress.id)
                progress(pc, result)
                if (result.terminal) jobs[pc.id]?.cancel()
            } catch (error: CancellationException) {
                throw error
            } catch (_: Throwable) {
                val latest = state.value.devices[pc.id] as? AgentUpdateDialogState.Running
                if (latest != null && !latest.progress.terminal) put(pc.id, latest.copy(refreshing = true))
            } finally {
                cancellations.remove(pc.id)
                val latest = state.value.devices[pc.id] as? AgentUpdateDialogState.Running
                if (latest != null) put(pc.id, latest.copy(cancelling = false))
            }
        }
    }

    fun close() { scope.cancel() }
}

internal class AgentUpdateTaskStore(context: Context, accountScope: String?) {
    private val preferences = context.getSharedPreferences("remote-agent-updates", Context.MODE_PRIVATE)
    private val key = accountScope?.let { value ->
        MessageDigest.getInstance("SHA-256").digest(value.toByteArray()).joinToString("") { "%02x".format(it) }
    }
    fun load(): Map<UUID, UUID> = key?.let { scope ->
        preferences.getStringSet(scope, emptySet()).orEmpty().mapNotNull { value ->
            runCatching { value.split(':').let { UUID.fromString(it[0]) to UUID.fromString(it[1]) } }.getOrNull()
        }.toMap()
    }.orEmpty()
    fun save(tasks: Map<UUID, UUID>) {
        key?.let { preferences.edit().putStringSet(it, tasks.map { (pc, task) -> "$pc:$task" }.toSet()).apply() }
    }
}

internal fun visibleAgentUpdatePcs(snapshot: ProductSnapshot, updates: Map<UUID, AgentUpdateDialogState>): List<PcSummary> {
    val allowedDevices = snapshot.devices.filter { it.type.isPcAgent() }.mapTo(hashSetOf()) { it.id }
    return (snapshot.pcs + updates.values.map { it.pc.copy(online = false) }).distinctBy { it.id }
        .filter { it.deviceId in allowedDevices && (it.online || it.id in updates) }
}

internal fun AgentUpdateDialogState.blocksPcActions(): Boolean =
    this is AgentUpdateDialogState.Starting || this is AgentUpdateDialogState.Running && !progress.terminal
