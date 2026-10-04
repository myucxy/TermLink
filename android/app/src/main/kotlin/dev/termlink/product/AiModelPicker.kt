package dev.termlink.product

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.TimeoutCancellationException
import java.util.UUID

internal fun aiCanChooseEffort(editable: Boolean, switch: Boolean, scope: String, existingSession: Boolean): Boolean =
    editable && if (existingSession) switch && scope != "new_session" else switch || scope == "new_session"

internal fun aiSelectedModel(models: List<AiModelOption>, model: String, provider: String): AiModelOption? =
    models.firstOrNull { it.modelId == model && it.providerId == provider }
        ?: if (model.isBlank() && provider.isBlank()) models.firstOrNull { it.isDefault || it.modelId == "default" } else null

internal fun aiShowEffortPicker(reasoning: AiReasoning, switch: Boolean, scope: String): Boolean =
    reasoning.status != "unsupported" && (switch || scope == "new_session" || reasoning.available)

/**
 * Model picker shared by create and existing-session composers.
 *
 * The list and each model's reasoning options come from the selected Agent. No
 * Codex/Claude option is inferred here: old hosts are handled by the parser's
 * narrow compatibility fallback, while unknown or missing capabilities remain
 * visibly unavailable.
 */
@Composable
internal fun AiModelPicker(
    workspace: AiWorkspace,
    pcId: UUID,
    agentId: String,
    listing: Boolean,
    online: Boolean,
    editable: Boolean,
    model: String,
    provider: String,
    onSelect: (String, String) -> Unit,
    modifier: Modifier = Modifier,
    compact: Boolean = false,
    modelSwitch: Boolean = true,
    reasoning: AiReasoning = AiReasoning(),
    reasoningEffort: String = "",
    reasoningSwitch: Boolean = false,
    reasoningScope: String = "session",
    existingSession: Boolean = false,
    allowDefaultModel: Boolean = true,
    onReasoning: (AiReasoning) -> Unit = {},
    onEffortSelect: (String) -> Unit = {},
) {
    val hostId = workspace.catalogs[pcId]?.hostId
    var expanded by remember(pcId, agentId, hostId) { mutableStateOf(false) }
    var effortExpanded by remember(pcId, agentId, hostId) { mutableStateOf(false) }
    var custom by rememberSaveable(pcId, agentId, hostId) { mutableStateOf(false) }
    var requested by remember(pcId, agentId, hostId) { mutableIntStateOf(0) }
    var loading by remember(pcId, agentId, hostId) { mutableStateOf(false) }
    var result by remember(pcId, agentId, hostId) { mutableStateOf<AiModelList?>(null) }
    var error by remember(pcId, agentId, hostId) { mutableStateOf("") }
    val canChooseModel = editable && modelSwitch
    // Claude exposes effort only while creating a session. Existing sessions
    // remain read-only even though their options are still shown.
    val canChooseEffort = aiCanChooseEffort(editable, reasoningSwitch, reasoningScope, existingSession)

    LaunchedEffect(requested, online, listing, agentId, pcId, hostId) {
        if (requested == 0 || !online || !listing) {
            loading = false
            return@LaunchedEffect
        }
        loading = true
        error = ""
        try {
            result = workspace.models(pcId, agentId)
        } catch (failure: Exception) {
            if (failure is CancellationException && failure !is TimeoutCancellationException) throw failure
            error = if (failure is java.io.IOException) failure.message.orEmpty() else "读取失败，可重试或手动输入。"
        } finally {
            loading = false
        }
    }
    LaunchedEffect(pcId, agentId, online, listing, result) {
        if (!online || !listing) {
            result = null
            loading = false
            error = ""
        }
    }

    val selected = aiSelectedModel(result?.models.orEmpty(), model, provider)
    // Only an existing session has host-authoritative fallback metadata. A new
    // draft must never inherit the previous model's effort options.
    val selectedReasoning = selected?.reasoning ?: if (existingSession) reasoning else AiReasoning()
    LaunchedEffect(selectedReasoning, existingSession) { if (!existingSession) onReasoning(selectedReasoning) }
    val label = if (model.isBlank()) "电脑默认" else selected?.name ?: model
    val effortLabel = reasoningEffort.ifBlank { selectedReasoning.defaultValue }.ifBlank { "默认" }

    Row(modifier, verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(4.dp)) {
        Box(Modifier.weight(1f)) {
            Row(
                Modifier
                    .fillMaxWidth()
                    .heightIn(min = 48.dp)
                    .clickable(enabled = online && (modelSwitch || listing), role = Role.Button) {
                        effortExpanded = false
                        expanded = true
                        if (requested == 0 && listing) requested++
                    },
                verticalAlignment = Alignment.CenterVertically,
            ) {
                if (!compact) Text("模型", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Text(
                    label,
                    Modifier.weight(1f).padding(horizontal = if (compact) 4.dp else 8.dp),
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    style = if (compact) MaterialTheme.typography.labelSmall else MaterialTheme.typography.bodyMedium,
                    textAlign = if (compact) TextAlign.End else TextAlign.Start,
                )
                Icon(Icons.Default.ExpandMore, "选择模型", Modifier.size(18.dp))
            }
            DropdownMenu(
                expanded = expanded && online,
                onDismissRequest = { expanded = false },
                modifier = Modifier.heightIn(max = 420.dp).widthIn(max = 360.dp),
            ) {
                Text("模型", Modifier.padding(horizontal = 12.dp, vertical = 6.dp), style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant)
                if (allowDefaultModel) DropdownMenuItem(
                        text = { Text("电脑默认模型") },
                        leadingIcon = { if (model.isBlank()) Icon(Icons.Default.Check, "当前选择") else Spacer(Modifier.size(24.dp)) },
                        enabled = canChooseModel,
                        onClick = { onSelect("", ""); expanded = false },
                    )
                DropdownMenuItem(
                    text = { Text("手动输入模型 ID…") },
                    enabled = canChooseModel,
                    onClick = { expanded = false; custom = true },
                )
                val hint = when {
                    !listing -> "Windows Agent 未提供模型列表，可手动输入。"
                    !online -> "电脑暂时不可用，模型列表无法读取。"
                    loading -> "正在读取模型…"
                    error.isNotEmpty() -> error
                    result?.models.isNullOrEmpty() -> "暂无模型选项，可手动输入。"
                    else -> ""
                }
                if (hint.isNotEmpty()) Text(hint, Modifier.padding(horizontal = 12.dp, vertical = 6.dp), style = MaterialTheme.typography.bodySmall)
                result?.models.orEmpty().forEach { option ->
                    DropdownMenuItem(
                        text = {
                            Column {
                                Text(option.name + if (option.isDefault) " · 推荐" else "", maxLines = 2, overflow = TextOverflow.Ellipsis,
                                    style = MaterialTheme.typography.bodyMedium)
                                if (option.name != option.modelId || option.providerId.isNotEmpty())
                                    Text(listOf(option.providerId, option.modelId).filter(String::isNotBlank).joinToString(" / "),
                                        maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.labelSmall)
                            }
                        },
                        leadingIcon = { if (option.modelId == model && option.providerId == provider) Icon(Icons.Default.Check, "当前选择") else Spacer(Modifier.size(24.dp)) },
                        enabled = canChooseModel,
                        onClick = {
                            onSelect(option.modelId, option.providerId)
                            // A model choice intentionally does not send an effort. The
                            // host retains it only when valid, otherwise uses its default.
                            expanded = false
                        },
                    )
                }
                if (result?.truncated == true) Text("仅显示部分模型，其余可手动输入。", Modifier.padding(12.dp), style = MaterialTheme.typography.bodySmall)
                if (listing) DropdownMenuItem(
                    text = { Text("刷新模型列表") },
                    leadingIcon = { Icon(Icons.Default.Refresh, "刷新") },
                    enabled = online && !loading,
                    onClick = { requested++ },
                )
            }
        }
        if (aiShowEffortPicker(selectedReasoning, reasoningSwitch, reasoningScope)) {
            Box(Modifier.widthIn(min = 64.dp, max = if (compact) 100.dp else 148.dp)) {
                Row(
                    Modifier.heightIn(min = 48.dp).clickable(enabled = online, role = Role.Button) {
                        expanded = false
                        effortExpanded = true
                        if (requested == 0 && listing) requested++
                    },
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    if (!compact) Text("强度", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    Text(effortLabel, Modifier.weight(1f, fill = false).padding(horizontal = 4.dp), maxLines = 1,
                        overflow = TextOverflow.Ellipsis, style = if (compact) MaterialTheme.typography.labelSmall else MaterialTheme.typography.bodyMedium)
                    Icon(Icons.Default.ExpandMore, "选择推理强度", Modifier.size(18.dp))
                }
                DropdownMenu(expanded = effortExpanded && online, onDismissRequest = { effortExpanded = false },
                    modifier = Modifier.heightIn(max = 420.dp).widthIn(max = 320.dp)) {
                    Text("推理强度${when (reasoningScope) { "next_turn" -> " · 下次消息生效"; "new_session" -> " · 新会话生效"; else -> "" }}", Modifier.padding(horizontal = 12.dp, vertical = 6.dp), style = MaterialTheme.typography.labelMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant)
                    if (existingSession && reasoningScope == "new_session") Text("推理强度仅对新建会话生效。", Modifier.padding(horizontal = 12.dp, vertical = 6.dp), style = MaterialTheme.typography.bodySmall)
                    else if (!editable) Text("当前暂不可修改会话设置。", Modifier.padding(horizontal = 12.dp, vertical = 6.dp), style = MaterialTheme.typography.bodySmall)
                    DropdownMenuItem(
                        text = { Text("默认${selectedReasoning.defaultValue.takeIf { it.isNotBlank() }?.let { " · $it" }.orEmpty()}") },
                        leadingIcon = { if (reasoningEffort.isBlank()) Icon(Icons.Default.Check, "当前选择") else Spacer(Modifier.size(24.dp)) },
                        enabled = canChooseEffort && selectedReasoning.available,
                        onClick = { onEffortSelect(""); effortExpanded = false },
                    )
                    if (selectedReasoning.options.isEmpty()) {
                        Text(
                            when {
                                loading -> "正在读取推理强度…"
                                error.isNotEmpty() -> error
                                selectedReasoning.status == "unknown" -> "尚未获取该模型的推理能力。"
                                else -> "该模型没有可选强度。"
                            },
                            Modifier.padding(horizontal = 12.dp, vertical = 6.dp), style = MaterialTheme.typography.bodySmall,
                        )
                    } else selectedReasoning.options.forEach { option ->
                        DropdownMenuItem(
                            text = {
                                Column {
                                    Text(option.value, style = MaterialTheme.typography.bodyMedium)
                                    if (option.description.isNotBlank()) Text(option.description, maxLines = 2, overflow = TextOverflow.Ellipsis,
                                        style = MaterialTheme.typography.labelSmall)
                                }
                            },
                            leadingIcon = { if (option.value == reasoningEffort) Icon(Icons.Default.Check, "当前选择") else Spacer(Modifier.size(24.dp)) },
                            enabled = canChooseEffort && selectedReasoning.available,
                            onClick = { onEffortSelect(option.value); effortExpanded = false },
                        )
                    }
                    if (listing) DropdownMenuItem(text = { Text("刷新推理强度") }, leadingIcon = { Icon(Icons.Default.Refresh, "刷新") },
                        enabled = online && !loading, onClick = { requested++ })
                }
            }
        }
    }

    if (custom) {
        var customProvider by remember(custom, provider) { mutableStateOf(provider) }
        var customModel by remember(custom, model) { mutableStateOf(model) }
        AlertDialog(
            onDismissRequest = { custom = false },
            title = { Text("手动输入模型") },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    if (agentId == "zcode") OutlinedTextField(customProvider,
                        { if (it.length <= 200 && it.none(Char::isISOControl)) customProvider = it },
                        label = { Text("Provider ID") }, singleLine = true, modifier = Modifier.fillMaxWidth(), enabled = editable,
                        textStyle = MaterialTheme.typography.bodyMedium)
                    OutlinedTextField(customModel,
                        { if (it.length <= 200 && it.none(Char::isISOControl)) customModel = it },
                        label = { Text("模型 ID") }, singleLine = true, modifier = Modifier.fillMaxWidth(), enabled = editable,
                        textStyle = MaterialTheme.typography.bodyMedium)
                    Text(if (agentId == "zcode" && !allowDefaultModel) "Provider 和模型 ID 必须同时填写；已有 ZCode 会话不能清空当前模型。"
                    else if (agentId == "zcode") "Provider 和模型 ID 需同时填写；都留空使用默认模型。" else "留空使用电脑默认模型。",
                        style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            },
            confirmButton = {
                TextButton(enabled = editable && (agentId != "zcode" || allowDefaultModel && customModel.isBlank() && customProvider.isBlank() || customModel.isNotBlank() && customProvider.isNotBlank()), onClick = {
                    onSelect(customModel, customProvider); custom = false
                }) { Text("确定") }
            },
            dismissButton = { TextButton(onClick = { custom = false }) { Text("取消") } },
        )
    }
}
