package dev.termlink.product

import androidx.compose.foundation.Image
import androidx.annotation.DrawableRes
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ChatBubbleOutline
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.unit.dp
import dev.termlink.harness.R
import java.util.Locale
import java.util.UUID

internal fun aiAgentId(id: String, name: String = id): String =
    when (val value = id.ifBlank { name }.trim().lowercase(Locale.ROOT)) {
        "claude code", "claude-code" -> "claude"
        "grok build", "grok-build" -> "grok"
        "open code", "open-code" -> "opencode"
        "" -> "ai"
        else -> value
    }

internal data class AiAgentBrand(val name: String, @DrawableRes val icon: Int)

internal val aiAgentBrands = mapOf(
    "codex" to AiAgentBrand("Codex", R.drawable.ai_agent_openai),
    "claude" to AiAgentBrand("Claude Code", R.drawable.ai_agent_claude),
    "zcode" to AiAgentBrand("ZCode", R.drawable.ai_agent_zcode),
    "grok" to AiAgentBrand("Grok Build", R.drawable.ai_agent_grok),
    "opencode" to AiAgentBrand("OpenCode", R.drawable.ai_agent_opencode),
)

internal fun aiAgentName(id: String): String = aiAgentBrands[aiAgentId(id)]?.name ?: id.ifBlank { "AI" }

internal fun aiEntryAgentId(entry: AiOpened, session: AiConversation?): String =
    aiAgentId(entry.agentId.ifBlank { session?.agentId.orEmpty() }, session?.agent.orEmpty())

internal fun aiIsHistorical(session: AiConversation): Boolean = session.state in setOf("exited", "interrupted", "suspended")

/** Catalog references are temporary list rows; browsing them must not open/persist phone views. */
internal fun aiCatalogEntries(pcId: UUID, catalog: AiCatalog?): List<AiOpened> = buildList {
    catalog?.sessions?.filter { it.pcId == pcId && !aiIsHistorical(it) }?.forEach { add(AiOpened(pcId, it.id, agentId = aiAgentId(it.agentId, it.agent))) }
    catalog?.agents?.filter { it.supported && it.strategy == "desktop_remote" }?.forEach {
        add(AiOpened(pcId, "remote:$pcId:${it.id}", "desktop_remote", it.id))
    }
}.distinctBy { it.sessionId }

internal fun aiListEntries(opened: List<AiOpened>, pcId: UUID?, catalog: AiCatalog?, online: Boolean,
    closedSessionIds: Set<String> = emptySet(), sessions: Map<String, AiConversation> = emptyMap()): List<AiOpened> =
    (opened + if (pcId != null && online) aiCatalogEntries(pcId, catalog) else emptyList()).distinctBy { it.sessionId }
        .filter { it.sessionId !in closedSessionIds && sessions[it.sessionId]?.let(::aiIsHistorical) != true &&
            catalog?.sessions?.firstOrNull { session -> session.id == it.sessionId }?.let(::aiIsHistorical) != true }

internal data class AiGroupTarget(val pcId: UUID, val agentId: String) {
    val key get() = "agent:$pcId:$agentId"
}

internal sealed interface AiListRow {
    val key: String
    data class Device(val pcId: UUID, val count: Int) : AiListRow { override val key = "device:$pcId" }
    data class Agent(val pcId: UUID, val agentId: String, val count: Int) : AiListRow { override val key = "agent:$pcId:$agentId" }
    data class Session(val entry: AiOpened) : AiListRow { override val key = "session:${entry.sessionId}" }
}

internal fun aiListRows(entries: List<AiOpened>, sessions: Map<String, AiConversation>, collapsed: Map<String, Boolean>): List<AiListRow> = buildList {
    entries.groupBy { it.pcId }.forEach { (pcId, deviceEntries) ->
        val device = AiListRow.Device(pcId, deviceEntries.size)
        add(device)
        if (collapsed[device.key] != true) {
            deviceEntries.groupBy { aiEntryAgentId(it, sessions[it.sessionId]) }.forEach { (agentId, agentEntries) ->
                val agent = AiListRow.Agent(pcId, agentId, agentEntries.size)
                add(agent)
                if (collapsed[agent.key] != true) agentEntries.forEach { add(AiListRow.Session(it)) }
            }
        }
    }
}

@Composable
internal fun AiAgentProductIcon(agentId: String, modifier: Modifier = Modifier.size(22.dp)) {
    val id = aiAgentId(agentId)
    val brand = aiAgentBrands[id]
    when {
        brand == null -> Icon(Icons.Default.ChatBubbleOutline, null, modifier, tint = MaterialTheme.colorScheme.primary)
        id == "zcode" -> Image(painterResource(brand.icon), null, modifier)
        else -> Icon(painterResource(brand.icon), null, modifier,
            tint = if (id == "claude") Color.Unspecified
                else if (MaterialTheme.colorScheme.surface.luminance() < .5f) Color.White else Color.Black)
    }
}
