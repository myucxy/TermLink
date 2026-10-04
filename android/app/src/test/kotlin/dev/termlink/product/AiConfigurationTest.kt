package dev.termlink.product

import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.util.UUID

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class AiConfigurationTest {
    @Test fun acpAgentUsesAdvertisedCapabilitiesAndKeepsLegacyFallbackDisabled() {
        val modes = JSONArray().put(JSONObject().put("id", "ask").put("label", "默认审批"))
            .put(JSONObject().put("id", "full-access").put("label", "完全访问").put("requiresConfirmation", true))
        val advertised = JSONObject().put("modelListing", true).put("modelSwitch", true)
            .put("reasoningSwitch", true).put("reasoningScope", "session").put("permissionSwitch", true).put("permissionModes", modes)
        val create = parseAgentConfiguration(advertised, "grok", legacyModelListing = false, legacyModes = emptyList())
        assertTrue(AiAgentOption("grok", "Grok Build", "1.0.46", "available", configuration = create).supported)
        assertFalse(AiAgentOption("grok", "Grok Build", "", "missing").supported)
        assertTrue(create.modelListing)
        assertTrue(create.reasoningSwitch)
        assertTrue(create.permissionModes.last().requiresConfirmation)
        assertFalse(parseAgentConfiguration(null, "grok", legacyModelListing = true, legacyModes = listOf("full-access")).hasPermissionControl)
        val live = parseAgentConfiguration(advertised.put("permissionSwitch", false), "grok", false, emptyList())
        assertFalse(live.hasPermissionControl)
        assertTrue(live.modelSwitch)
    }
    @Test fun claudeEffortIsSelectableOnlyDuringCreation() {
        assertTrue(aiCanChooseEffort(true, false, "new_session", false))
        assertFalse(aiCanChooseEffort(true, false, "new_session", true))
        assertFalse(aiCanChooseEffort(false, true, "next_turn", true))
        assertTrue(aiCanChooseEffort(true, true, "next_turn", true))
    }
    @Test fun effortPickerHidesUnsupportedModelsButAllowsDiscoveryAndNewSessionOnlyAgents() {
        assertTrue(aiShowEffortPicker(AiReasoning(), true, "next_turn"))
        assertTrue(aiShowEffortPicker(AiReasoning(), false, "new_session"))
        assertFalse(aiShowEffortPicker(AiReasoning(), false, "session"))
        assertFalse(aiShowEffortPicker(AiReasoning("unsupported"), true, "next_turn"))
        assertFalse(aiShowEffortPicker(AiReasoning("unsupported"), false, "new_session"))
        val effort = AiReasoning("available", listOf(AiReasoningOption("custom-level")))
        assertTrue(aiShowEffortPicker(effort, false, "session"))
    }
    @Test fun defaultAndProviderSpecificModelMetadataDoNotMix() {
        val models = listOf(AiModelOption("same", "A", "a", true), AiModelOption("same", "B", "b"))
        assertEquals("a", aiSelectedModel(models, "", "")?.providerId)
        assertEquals("b", aiSelectedModel(models, "same", "b")?.providerId)
        assertNull(aiSelectedModel(models, "same", "unknown"))
        assertEquals("future-mode", aiModeLabel("future-mode"))
        assertEquals("不询问", aiModeLabel("dont-ask"))
    }
    @Test fun agentConfigurationIsScopedAndPreservesProviderLabels() {
        val config = JSONObject()
            .put("modelListing", false)
            .put("modelSwitch", true)
            .put("reasoningSwitch", true)
            .put("reasoningScope", "new_session")
            .put("permissionSwitch", true)
            .put("permissionModes", JSONArray().put(JSONObject()
                .put("id", "full-access")
                .put("label", "完全访问")
                .put("description", "由 ZCode 原生控制")
                .put("requiresConfirmation", true)))
        val value = parseAgentConfiguration(config, "zcode", legacyModelListing = true, legacyModes = listOf("ask"))
        assertFalse(value.modelListing)
        assertTrue(value.modelSwitch)
        assertEquals("new_session", value.reasoningScope)
        assertEquals("完全访问", value.permissionModes.single().label)
        assertTrue(value.permissionModes.single().requiresConfirmation)

        // A global legacy listing must not make unrelated/new agents appear configurable.
        val unsupported = parseAgentConfiguration(null, "grok", legacyModelListing = true, legacyModes = listOf("ask"))
        assertFalse(unsupported.modelListing)
        assertFalse(unsupported.modelSwitch)
        assertTrue(unsupported.permissionModes.isEmpty())
    }

    @Test fun modelReasoningUsesHostValuesAndRejectsMalformedOptions() {
        val reasoning = parseReasoning(JSONObject()
            .put("status", "available")
            .put("defaultValue", "medium")
            .put("options", JSONArray()
                .put(JSONObject().put("value", "low").put("description", "快速"))
                .put(JSONObject().put("value", "high").put("description", "更深入"))
                .put(JSONObject().put("value", "high"))
                .put(JSONObject().put("value", "bad\nvalue"))))
        assertTrue(reasoning.available)
        assertEquals(listOf("low", "high"), reasoning.options.map { it.value })
        assertEquals("medium", reasoning.defaultValue)
        assertEquals("", reasoning.effective("invalid"))
    }

    @Test fun legacyConfigurationFallbackIsLimitedToCodexAndClaude() {
        val codex = parseAgentConfiguration(null, "codex", legacyModelListing = true, legacyModes = listOf("ask", "read-only"))
        assertTrue(codex.modelListing)
        assertTrue(codex.modelSwitch)
        assertEquals(listOf("ask", "read-only"), codex.permissionModes.map { it.id })
        val claude = parseAgentConfiguration(null, "claude", legacyModelListing = false, legacyModes = listOf("plan"))
        assertFalse(claude.modelListing)
        assertEquals(listOf("plan"), claude.permissionModes.map { it.id })
    }

    @Test fun conversationCarriesReasoningAndSettingsNotice() {
        val value = JSONObject()
            .put("kind", "ai").put("id", UUID.randomUUID().toString())
            .put("agentId", "codex").put("agentName", "Codex").put("directory", "D:/work")
            .put("state", "idle").put("error", "").put("controlVersion", 1).put("canControl", true)
            .put("reasoningEffort", "high").put("settingsNotice", "仅下一轮生效")
            .put("reasoning", JSONObject().put("status", "available").put("defaultValue", "medium")
                .put("options", JSONArray().put(JSONObject().put("value", "low")).put(JSONObject().put("value", "high"))))
            .put("capabilities", JSONObject().put("configure", true).put("configuration", JSONObject()
                .put("modelSwitch", true).put("reasoningSwitch", true).put("permissionSwitch", false)))
        val session = parseConversation(UUID.randomUUID(), value)
        assertEquals("high", session.reasoningEffort)
        assertEquals("仅下一轮生效", session.settingsNotice)
        assertEquals(listOf("low", "high"), session.reasoning.options.map { it.value })
        assertTrue(session.capabilities.configuration.modelSwitch)
    }
}
