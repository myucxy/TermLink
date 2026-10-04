package dev.termlink.product

import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ChatBubbleOutline
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.unit.dp
import dev.termlink.harness.R
import java.util.Locale
import java.util.UUID

internal fun aiAgentId(id: String, name: String = id): String =
    when (val value = id.ifBlank { name }.trim().lowercase(Locale.ROOT)) {
        "claude code", "claude-code" -> "claude"
        "" -> "ai"
        else -> value
    }

internal fun aiAgentName(id: String): String = when (id) {
    "codex" -> "Codex"
    "claude" -> "Claude Code"
    "zcode" -> "ZCode"
    else -> id.ifBlank { "AI" }
}

internal fun aiEntryAgentId(entry: AiOpened, session: AiConversation?): String =
    aiAgentId(entry.agentId.ifBlank { session?.agentId.orEmpty() }, session?.agent.orEmpty())

/** Catalog references are temporary list rows; browsing them must not open/persist phone views. */
internal fun aiCatalogEntries(pcId: UUID, catalog: AiCatalog?): List<AiOpened> = buildList {
    catalog?.sessions?.filter { it.pcId == pcId }?.forEach { add(AiOpened(pcId, it.id, agentId = aiAgentId(it.agentId, it.agent))) }
    catalog?.agents?.filter { it.supported && it.strategy == "desktop_remote" }?.forEach {
        add(AiOpened(pcId, "remote:$pcId:${it.id}", "desktop_remote", it.id))
    }
}.distinctBy { it.sessionId }

internal fun aiListEntries(opened: List<AiOpened>, pcId: UUID?, catalog: AiCatalog?, online: Boolean): List<AiOpened> =
    (opened + if (pcId != null && online) aiCatalogEntries(pcId, catalog) else emptyList()).distinctBy { it.sessionId }

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
    when (aiAgentId(agentId)) {
        "codex" -> Icon(painterResource(R.drawable.ai_agent_codex), null, modifier, tint = MaterialTheme.colorScheme.onSurface)
        "claude" -> Icon(painterResource(R.drawable.ai_agent_claude), null, modifier, tint = Color.Unspecified)
        "zcode" -> Image(painterResource(R.drawable.ai_agent_zcode), null, modifier)
        else -> Icon(Icons.Default.ChatBubbleOutline, null, modifier, tint = MaterialTheme.colorScheme.primary)
    }
}
