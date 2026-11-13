package dev.termlink.product

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.*
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.unit.dp
import java.time.Instant
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import org.json.JSONObject

private const val runtimeAuthenticationVerificationURL = "https://auth.openai.com/codex/device"
private val runtimeAuthenticationUserCode = Regex("[A-Za-z0-9-]{4,32}")

internal fun aiRuntimeAuthenticationSummary(value: JSONObject, now: Instant = Instant.now()): String = when (value.opt("status") as? String) {
    "authenticated" -> "独立运行环境已登录；模型权限仍需实际会话确认。"
    "not_required" -> "当前独立运行环境不要求模型账号登录。"
    "pending" -> if (aiRuntimeAuthenticationChallenge(value, now) != null) "请在官方页面输入设备码，完成后查询登录状态。"
        else "设备码已过期或尚未确认，请查询授权状态后重新开始。"
    "required", "isolated_required" -> "此电脑使用独立 Agent 配置，需要单独授权，不会复用桌面账号。"
    "cancelled" -> "本次授权已取消。"
    "failed" -> "授权未完成，请取消后重新开始。"
    else -> "尚未确认模型授权；协议可用不代表已登录。"
}

internal fun aiRuntimeAuthenticationURL(value: String): String? = value.takeIf { it == runtimeAuthenticationVerificationURL }

internal fun aiRuntimeAuthenticationChallenge(value: JSONObject, now: Instant = Instant.now()): Pair<String, String>? {
    if (value.opt("status") != "pending" || value.opt("method") != "device_code") return null
    val url = (value.opt("verificationUrl") as? String)?.let(::aiRuntimeAuthenticationURL) ?: return null
    val expiry = runCatching { Instant.parse(value.opt("expiresAt") as? String ?: return null) }.getOrNull() ?: return null
    val code = value.opt("userCode") as? String ?: return null
    if (expiry <= now || !runtimeAuthenticationUserCode.matches(code)) return null
    return url to code
}

@Composable internal fun AiRuntimeAuthenticationDialog(workspace: AiWorkspace, pc: UUID, agent: AiAgentOption, close: () -> Unit) {
    val scope = rememberCoroutineScope()
    val uri = LocalUriHandler.current
    var busy by remember(pc, agent.id) { mutableStateOf(false) }
    var error by remember(pc, agent.id) { mutableStateOf("") }
    var now by remember(pc, agent.id) { mutableStateOf(Instant.now()) }
    val current = workspace.catalogs[pc]?.agents?.firstOrNull { it.id == agent.id } ?: agent
    val value = workspace.runtimeAuthentications[pc to agent.id] ?: current.runtimeAuthentication
    val deviceCode = current.runtimeAuthentication.opt("method") == "device_code"
    val canCheck = current.runtimeAuthentication.opt("canCheck") == true
    val canStart = current.runtimeAuthentication.opt("canStart") == true
    val active = !workspace.isRetired && workspace.catalogs[pc]?.agents?.any { it.id == agent.id && it.supported } == true
    val challenge = aiRuntimeAuthenticationChallenge(value, now)
    fun perform(action: String) {
        if (busy || !active) return
        busy = true; error = ""
        scope.launch {
            try {
                if (action == "authentication") workspace.runtimeAuthentication(pc, agent.id)
                else {
                    check(workspace.mutate(pc, action, extra = JSONObject().put("agentId", agent.id))?.await() == true) { workspace.error }
                    // Mutation receipts carry no challenge; read the current
                    // owner-bound state instead of replaying an old device code.
                    if (canCheck) workspace.runtimeAuthentication(pc, agent.id)
                }
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { error = "授权请求未确认，请查询原请求回执或检查电脑。" }
            finally { busy = false }
        }
    }
    LaunchedEffect(pc, agent.id) { if (canCheck) perform("authentication") }
    LaunchedEffect(pc, agent.id, value.optString("status"), value.optString("expiresAt")) {
        now = Instant.now()
        while (value.opt("status") == "pending") { delay(1_000); now = Instant.now() }
    }
    AlertDialog(onDismissRequest = close, title = { Text("模型账号授权") }, text = {
        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(aiRuntimeAuthenticationSummary(value, now))
            Text("只操作当前 TermLink 账号的独立配置，不读取或复制原桌面登录；不会自动发送模型任务。", style = MaterialTheme.typography.bodySmall)
            if (deviceCode) {
                challenge?.let { (url, code) ->
                    SelectionContainer { Text(code, style = MaterialTheme.typography.headlineSmall) }
                    TextButton(enabled = active, onClick = {
                        // Recheck expiry at the click, not only at composition time.
                        if (aiRuntimeAuthenticationChallenge(value)?.first == url) runCatching { uri.openUri(url) }
                            .onFailure { error = "暂时无法打开官方授权页面，请稍后重试。" }
                    }) { Text("打开 OpenAI 官方授权页面") }
                }
                TextButton(enabled = active && canCheck && !busy && !workspace.isBusy(pc), onClick = { perform("authentication") }) { Text("查询授权状态") }
                TextButton(enabled = active && canStart && !busy && !workspace.isBlocked(pc) && challenge == null,
                    onClick = { perform("authentication.start") }) { Text("开始设备码授权") }
                TextButton(enabled = active && canStart && !busy && !workspace.isBlocked(pc),
                    onClick = { perform("authentication.cancel") }) { Text("取消本次授权") }
                if (workspace.isUncertain(pc)) TextButton(enabled = active && !busy && !workspace.isBusy(pc),
                    onClick = { workspace.retryUncertain(pc) }) { Text("查询上次授权请求回执") }
            } else Text("此 Agent 暂无手机授权接口，请先在电脑的独立 Agent 配置中完成官方授权。")
            if (error.isNotEmpty()) Text(error, color = MaterialTheme.colorScheme.error)
        }
    }, confirmButton = { TextButton(onClick = close) { Text("关闭") } })
}
