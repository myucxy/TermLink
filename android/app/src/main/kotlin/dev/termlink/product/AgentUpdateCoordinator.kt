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
    val hiddenNotices: Set<UUID> = emptySet(),
    val offers: Map<UUID, AgentUpdateEligibility> = emptyMap(),
) {
    val dialog: AgentUpdateDialogState? get() = devices[selectedPcId]
}

/** Task IDs survive process death; dialog visibility is deliberately never persisted. */
internal class AgentUpdateCoordinator(
    private val repository: ProductRepository,
    parentScope: CoroutineScope,
    restoredTasks: Map<UUID, UUID> = emptyMap(),
    private val saveTasks: (Map<UUID, UUID>) -> Unit = {},
    hiddenNotices: Set<UUID> = emptySet(),
    private val saveHiddenNotices: (Set<UUID>) -> Unit = {},
    private val nowMillis: () -> Long = System::currentTimeMillis,
) {
    private val scope = CoroutineScope(parentScope.coroutineContext + SupervisorJob(parentScope.coroutineContext[Job]))
    private val mutableState = MutableStateFlow(AgentUpdateUiState(hiddenNotices = hiddenNotices))
    val state = mutableState.asStateFlow()
    private val tasks = restoredTasks.toMutableMap()
    private val jobs = mutableMapOf<UUID, Job>()
    private val cancellations = mutableSetOf<UUID>()
    private val offerJobs = mutableMapOf<UUID, Job>()
    private val offerChecks = mutableMapOf<UUID, Pair<String, Long>>()

    private fun hideNotice(pcId: UUID, hidden: Boolean) {
        val current = state.value
        val notices = if (hidden) current.hiddenNotices + pcId else current.hiddenNotices - pcId
        if (notices == current.hiddenNotices) return
        mutableState.value = current.copy(hiddenNotices = notices)
        saveHiddenNotices(notices)
    }

    private fun put(pcId: UUID, value: AgentUpdateDialogState?) {
        val current = mutableState.value
        mutableState.value = current.copy(devices = if (value == null) current.devices - pcId else current.devices + (pcId to value))
    }

    private fun progress(pc: PcSummary, value: AgentUpdateProgress) {
        require(value.pcId == pc.id)
        hideNotice(pc.id, false)
        if (value.terminal) {
            mutableState.value = state.value.copy(offers = state.value.offers - pc.id)
            offerChecks.remove(pc.id)
        }
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

    fun dismissNotice(pcId: UUID) {
        val notice = state.value.devices[pcId] ?: return
        if (!notice.canDismissNotice()) return
        if (notice is AgentUpdateDialogState.Running) {
            jobs.remove(pcId)?.cancel()
            tasks.remove(pcId)
            saveTasks(tasks.toMap())
            put(pcId, null)
        } else {
            // Hide only the error; an unconfirmed task must remain recoverable.
            hideNotice(pcId, true)
        }
        if (state.value.selectedPcId == pcId) dismiss()
    }

    fun open(pc: PcSummary, checkForUpdate: Boolean = false) {
        hideNotice(pc.id, false)
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
        refreshOffers(pcs)
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

    fun refreshOffers(pcs: List<PcSummary>, force: Boolean = false) {
        val candidates = pcs.filter { it.online && it.remoteRoutingEnabled && it.platform == "windows" &&
            PcCapabilityIds.REMOTE_AGENT_UPDATE_V1 in it.capabilities }
        val allowed = candidates.mapTo(hashSetOf()) { it.id }
        mutableState.value = state.value.copy(offers = state.value.offers.filterKeys { it in allowed })
        offerChecks.keys.retainAll(allowed)
        for (pc in candidates) {
            if (offerJobs[pc.id]?.isActive == true || state.value.devices[pc.id]?.blocksPcActions() == true) continue
            val checked = offerChecks[pc.id]
            if (!force && checked?.first == pc.agentVersion && nowMillis() - checked.second in 0 until 300_000) continue
            offerChecks[pc.id] = pc.agentVersion to nowMillis()
            val job = scope.launch(start = CoroutineStart.LAZY) {
                try {
                    val offer = repository.agentUpdateEligibility(pc.id)
                    require(offer.pcId == pc.id)
                    if (offerChecks[pc.id]?.first == pc.agentVersion && state.value.devices[pc.id]?.blocksPcActions() != true) {
                        mutableState.value = state.value.copy(offers = state.value.offers + (pc.id to offer))
                    }
                } catch (error: CancellationException) { throw error }
                catch (_: Throwable) {
                    mutableState.value = state.value.copy(offers = state.value.offers - pc.id)
                } finally { offerJobs.remove(pc.id) }
            }
            offerJobs[pc.id] = job
            job.start()
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
    fun loadHiddenNotices(): Set<UUID> = key?.let { scope ->
        preferences.getStringSet("$scope:hidden", emptySet()).orEmpty().mapNotNull {
            runCatching { UUID.fromString(it) }.getOrNull()
        }.toSet()
    }.orEmpty()
    fun saveHiddenNotices(pcs: Set<UUID>) {
        key?.let { preferences.edit().putStringSet("$it:hidden", pcs.map(UUID::toString).toSet()).apply() }
    }
}

internal fun visibleAgentUpdatePcs(snapshot: ProductSnapshot, updates: Map<UUID, AgentUpdateDialogState>): List<PcSummary> {
    val allowedDevices = snapshot.devices.filter { it.type.isPcAgent() }.mapTo(hashSetOf()) { it.id }
    return (snapshot.pcs + updates.values.map { it.pc.copy(online = false) }).distinctBy { it.id }
        .filter { it.deviceId in allowedDevices && (it.online || it.id in updates) }
}

internal fun AgentUpdateDialogState.blocksPcActions(): Boolean =
    this is AgentUpdateDialogState.Starting || this is AgentUpdateDialogState.Running && !progress.terminal

internal fun AgentUpdateDialogState.canDismissNotice(): Boolean =
    this is AgentUpdateDialogState.Failure || this is AgentUpdateDialogState.Running && progress.terminal

internal fun AgentUpdateEligibility.isUpdateAvailableFor(pc: PcSummary, state: AgentUpdateDialogState?): Boolean =
    pc.online && pc.remoteRoutingEnabled && pc.platform == "windows" && PcCapabilityIds.REMOTE_AGENT_UPDATE_V1 in pc.capabilities &&
        pcId == pc.id && currentVersion == pc.agentVersion && eligible && !targetVersion.isNullOrBlank() &&
        currentBuild != null && targetBuild != null && targetBuild > currentBuild &&
        state?.blocksPcActions() != true &&
        ((state as? AgentUpdateDialogState.Running)?.progress?.observedAgentBuild ?: -1) < targetBuild
