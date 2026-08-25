package dev.termlink.product

import dev.termlink.model.DeviceTrustState
import dev.termlink.model.PcSummary
import dev.termlink.harness.BuildConfig
import java.time.Instant
import java.util.UUID
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class ProductStateTest {
    @Test
    fun unavailableRepositoryStartsSignedOutAndUnconfigured() {
        val repository = UnavailableProductRepository()

        assertFalse(repository.snapshot.value.signedIn)
        assertFalse(repository.snapshot.value.configured)
        assertEquals(emptyList<Any>(), repository.snapshot.value.devices)
    }

    @Test
    fun formatterDoesNotInventAnEntitlementForUnknownLimit() {
        assertEquals("2 / unavailable", ProductFormatter.quotaLabel(2, 0))
        assertEquals("2 / 5", ProductFormatter.quotaLabel(2, 5))
        assertEquals("2 / unlimited", ProductFormatter.quotaLabel(2, -1))
        assertEquals("Pending", ProductFormatter.trustLabel(DeviceTrustState.PENDING))
    }

    @Test
    fun phonePrimaryNavigationKeepsNestedRoutesUnderTheirParent() {
        assertEquals(ProductPrimaryDestination.SESSIONS, ProductDestination.SESSIONS.primaryDestination())
        assertEquals(ProductPrimaryDestination.DEVICES, ProductDestination.HISTORY.primaryDestination())
        assertEquals(ProductPrimaryDestination.SETTINGS, ProductDestination.MEMBERSHIP.primaryDestination())
        assertEquals(ProductDestination.SESSIONS, ProductPrimaryDestination.SESSIONS.destination())
        assertEquals(ProductDestination.SETTINGS, ProductPrimaryDestination.SETTINGS.destination())
    }

    @Test
    fun removedProductSurfacesAreNotVisibleButRemainRepresentable() {
        assertTrue(ProductDestination.DEVICES.isVisibleInProductShell)
        assertTrue(ProductDestination.ACCOUNT.isVisibleInProductShell)
        assertFalse(ProductDestination.MEMBERSHIP.isVisibleInProductShell)
        assertFalse(ProductDestination.TRUSTED_DEVICES.isVisibleInProductShell)
        assertEquals("https://termlink.kget.cc:8910", PRODUCT_DEFAULT_SERVER_URL)
    }

    @Test
    fun debugBuildDefaultsToPublicServerAndNormalizesLanInjection() {
        assertEquals(
            PRODUCT_DEFAULT_SERVER_URL,
            configuredProductServerUrl(configuredUrl = null, isDebug = true),
        )
        assertEquals(
            "https://termlink.192.168.9.28.sslip.io:9443",
            configuredProductServerUrl(
                configuredUrl = " HTTPS://TermLink.192.168.9.28.sslip.io:9443/ ",
                isDebug = true,
            ),
        )
    }

    @Test
    fun activeBuildUsesItsNormalizedBuildConfigServer() {
        val configuredServerUrl = configuredProductServerUrl(
            configuredUrl = BuildConfig.TERMLINK_SERVER_URL,
            isDebug = BuildConfig.DEBUG,
            allowCleartextLocalTest = BuildConfig.TERMLINK_ALLOW_CLEARTEXT_LOCAL_TEST,
        )

        assertEquals(configuredServerUrl, PRODUCT_PUBLIC_SERVER_URL)
        if (BuildConfig.DEBUG) {
            assertEquals(
                ServerUrlPolicy.normalize(
                    BuildConfig.TERMLINK_SERVER_URL,
                    allowCleartextForLocalTest = BuildConfig.TERMLINK_ALLOW_CLEARTEXT_LOCAL_TEST,
                ),
                PRODUCT_PUBLIC_SERVER_URL,
            )
        } else {
            assertEquals(PRODUCT_DEFAULT_SERVER_URL, BuildConfig.TERMLINK_SERVER_URL)
            assertFalse(BuildConfig.TERMLINK_ALLOW_CLEARTEXT_LOCAL_TEST)
            assertEquals(PRODUCT_DEFAULT_SERVER_URL, PRODUCT_PUBLIC_SERVER_URL)
        }
    }

    @Test
    fun releaseBuildIgnoresInjectedServerAndDisallowsCleartext() {
        assertEquals(
            PRODUCT_DEFAULT_SERVER_URL,
            configuredProductServerUrl(
                configuredUrl = "https://termlink.192.168.9.28.sslip.io:9443",
                isDebug = false,
            ),
        )
        assertEquals(
            PRODUCT_DEFAULT_SERVER_URL,
            configuredProductServerUrl(
                configuredUrl = "http://192.168.9.28:8080",
                isDebug = false,
                allowCleartextLocalTest = true,
            ),
        )
    }

    @Test
    fun debugCleartextRequiresExplicitPrivateLanOptIn() {
        assertEquals(
            "http://192.168.9.28:8080",
            configuredProductServerUrl(
                configuredUrl = "http://192.168.9.28:8080/",
                isDebug = true,
                allowCleartextLocalTest = true,
            ),
        )
        assertThrows(IllegalArgumentException::class.java) {
            configuredProductServerUrl(
                configuredUrl = "http://example.com:8080",
                isDebug = true,
                allowCleartextLocalTest = true,
            )
        }
    }

    @Test
    fun sameAccountDevicePolicyNeverRequiresUserEnrollment() {
        assertEquals(SameAccountDeviceAction.REFRESH, sameAccountDeviceAction(DeviceTrustState.TRUSTED))
        assertEquals(
            SameAccountDeviceAction.ACTIVATE_LEGACY_AND_REFRESH,
            sameAccountDeviceAction(DeviceTrustState.PENDING),
        )
        assertEquals(SameAccountDeviceAction.BLOCK_REVOKED, sameAccountDeviceAction(DeviceTrustState.REVOKED))
        assertFalse(ProductSnapshot().enrollmentConfirmationRequired)
    }

    @Test
    fun registrationResultClosesOnlyAfterSuccess() = runBlocking {
        var successfulCalls = 0
        val success = submitAccountRegistration { successfulCalls += 1 }
        val failure = submitAccountRegistration { error("registration rejected") }

        assertEquals(1, successfulCalls)
        assertTrue(success is AccountRegistrationResult.Success)
        assertTrue(failure is AccountRegistrationResult.Failure)
        assertEquals("registration rejected", (failure as AccountRegistrationResult.Failure).error.message)
    }

    @Test
    fun unavailableRegistrationClearsItsPasswordArguments() = runBlocking {
        val password = "password123".toCharArray()
        val confirm = "password123".toCharArray()

        val failure = runCatching {
            UnavailableProductRepository().register(
                username = "alice",
                email = "alice@example.test",
                firstName = "Alice",
                lastName = "Example",
                password = password,
                confirm = confirm,
            )
        }

        assertTrue(failure.isFailure)
        assertTrue(password.all { it == '\u0000' })
        assertTrue(confirm.all { it == '\u0000' })
    }

    @Test
    fun themePreferenceDefaultsToSystemForUnknownStoredValue() {
        assertEquals(ProductThemePreference.SYSTEM, ProductThemePreference.fromWireValue(null))
        assertEquals(ProductThemePreference.SYSTEM, ProductThemePreference.fromWireValue("unexpected"))
        assertEquals(ProductThemePreference.DARK, ProductThemePreference.fromWireValue("dark"))
    }

    @Test
    fun pendingDeviceDoesNotOfferSelfApprovalWithoutManageablePc() {
        val waiting = ProductSnapshot(accountLabel = "Approval pending")
        assertFalse(waiting.canApprovePendingDevice())

        val manageablePc = PcSummary(
            id = UUID.randomUUID(),
            deviceId = UUID.randomUUID(),
            name = "Office",
            agentVersion = "test",
            remoteRoutingEnabled = true,
            online = true,
            lastSeenAt = Instant.EPOCH,
        )
        assertFalse(waiting.copy(pcs = listOf(manageablePc)).canApprovePendingDevice())
        assertEquals(true, ProductSnapshot(pcs = listOf(manageablePc)).canApprovePendingDevice())
    }

    @Test
    fun connectionStateDoesNotTreatConfiguredOrSignedInAsConnected() {
        assertFalse(ProductSnapshot(configured = true).isConnected())
        assertFalse(ProductSnapshot(signedIn = true).isConnected())
        assertTrue(ProductSnapshot(connectionState = ProductConnectionState.CONNECTED).isConnected())
    }

    @Test
    fun agentUpdateCancellationStopsAtInstallBoundary() {
        val base = AgentUpdateProgress(
            id = UUID.randomUUID(),
            pcId = UUID.randomUUID(),
            currentVersion = "0.1.31.37",
            currentBuild = 37,
            targetVersion = "0.1.32",
            targetBuild = 38,
            status = "PROCESSING",
            phase = "VERIFIED",
            progressPercent = 70,
        )

        assertTrue(base.cancelable)
        assertFalse(base.terminal)
        assertFalse(base.copy(phase = "DRAINING_SESSIONS", progressPercent = 75).cancelable)
        assertFalse(base.copy(phase = "INSTALLING", progressPercent = 85).cancelable)
    }

    @Test
    fun agentUpdateTerminalStatesStopMonitoring() {
        val base = AgentUpdateProgress(
            id = UUID.randomUUID(),
            pcId = UUID.randomUUID(),
            currentVersion = "0.1.31.37",
            currentBuild = 37,
            targetVersion = "0.1.32",
            targetBuild = 38,
            status = "PENDING",
            phase = "REQUESTED",
            progressPercent = 0,
        )

        assertFalse(base.terminal)
        for (status in listOf(
            "SUCCEEDED", "FAILED", "CANCELLED", "EXPIRED", "ROLLED_BACK", "ROLLBACK_FAILED",
        )) {
            assertTrue("$status must stop polling", base.copy(status = status).terminal)
            assertFalse("$status cannot remain cancelable", base.copy(status = status).cancelable)
        }
    }

    @Test
    fun localActivityIsBoundedAndMarkedDeviceLocal() {
        var snapshot = ProductSnapshot()
        repeat(MAX_DEVICE_LOCAL_ACTIVITY_ENTRIES + 7) { index ->
            snapshot = snapshot.withLocalActivity(
                ActivityEntry(
                    id = index.toString(),
                    occurredAt = Instant.EPOCH.plusSeconds(index.toLong()),
                    title = "event-$index",
                    detail = "local",
                ),
            )
        }

        assertEquals(MAX_DEVICE_LOCAL_ACTIVITY_ENTRIES, snapshot.activity.size)
        assertEquals("event-${MAX_DEVICE_LOCAL_ACTIVITY_ENTRIES + 6}", snapshot.activity.first().title)
        assertTrue(snapshot.activity.all { it.source == ActivitySource.DEVICE_LOCAL })
    }

    @Test
    fun terminalDisplayPreferencesStayWithinRendererSafeBounds() {
        val normalized = TerminalDisplayPreferences(
            fontSizeSp = 2f,
            lineSpacingMultiplier = 4f,
            foregroundArgb = 0x00112233,
            backgroundArgb = 0x00445566,
        ).normalized

        assertEquals(10f, normalized.fontSizeSp)
        assertEquals(1.8f, normalized.lineSpacingMultiplier)
        assertEquals(0xFF112233.toInt(), normalized.foregroundArgb)
        assertEquals(0xFF445566.toInt(), normalized.backgroundArgb)
    }

    @Test
    fun terminalShortcutLabelsTrimAndFallbackToDefaults() {
        val normalized = TerminalShortcutPreferences(
            labels = mapOf(TerminalAction.ESCAPE to "   ", TerminalAction.TAB to "  Next  "),
            visibleActions = listOf(TerminalAction.TAB, TerminalAction.TAB),
        ).normalized

        assertEquals("Esc", normalized.label(TerminalAction.ESCAPE))
        assertEquals("Next", normalized.label(TerminalAction.TAB))
        assertEquals("Ctrl+C", normalized.label(TerminalAction.CTRL_C))
        assertEquals(TerminalAction.TAB, normalized.visibleActions.first())
        assertEquals(TerminalAction.entries.toSet(), normalized.visibleActions.toSet())
    }

    @Test
    fun terminalKeyCombinationsNormalizeModifiersNamedKeysAndPrintableKeys() {
        assertEquals(
            TerminalKeyCombination("P", control = true, shift = true),
            TerminalKeyCombination.parse(" control + shift + P "),
        )
        assertEquals("Alt+Delete", TerminalKeyCombination.parse("option+del")?.canonical)
        assertEquals("Ctrl+F12", TerminalKeyCombination.parse("Ctrl+f12")?.canonical)
        assertEquals("Backspace", TerminalKeyCombination.parse("bksp")?.canonical)
        assertEquals("+", TerminalKeyCombination.parse("plus")?.canonical)
        assertEquals("+", TerminalKeyCombination.parse("+")?.canonical)
        assertEquals("Ctrl++", TerminalKeyCombination.parse("control+plus")?.canonical)
        assertEquals("Ctrl++", TerminalKeyCombination.parse("Ctrl++")?.canonical)
        assertEquals(null, TerminalKeyCombination.parse("Ctrl+K+P"))
        assertEquals(null, TerminalKeyCombination.parse("Ctrl"))
        assertEquals('P'.code, terminalShiftedCodePoint('p'.code))
        assertEquals('!'.code, terminalShiftedCodePoint('1'.code))
        assertEquals('?'.code, terminalShiftedCodePoint('/'.code))
    }

    @Test
    fun everyCanonicalTerminalCombinationCanBeParsedAgain() {
        listOf(
            "plus",
            "Ctrl+plus",
            "Alt+Shift+plus",
            "Backspace",
            "Ctrl+Shift+p",
            "Alt+Delete",
            "F12",
        ).forEach { raw ->
            val canonical = requireNotNull(TerminalKeyCombination.parse(raw)).canonical
            assertEquals(canonical, TerminalKeyCombination.parse(canonical)?.canonical)
        }
    }

    @Test
    fun customTerminalShortcutsRejectInvalidRowsAndRemainBounded() {
        val valid = TerminalCustomShortcut("build", "  Build  ", "control+shift+p")
        val unnamed = TerminalCustomShortcut("palette", "   ", "Ctrl+Shift+P")
        val invalid = TerminalCustomShortcut("bad id!", "Bad", "Ctrl")
        val normalized = TerminalShortcutPreferences(
            customShortcuts = listOf(valid, valid.copy(label = "duplicate"), unnamed, invalid),
        ).normalized

        assertEquals(2, normalized.customShortcuts.size)
        assertEquals("Build", normalized.customShortcuts[0].label)
        assertEquals("Build", normalized.customShortcuts[0].displayLabel)
        assertEquals("Ctrl+Shift+p", normalized.customShortcuts[0].keyCombination)
        assertEquals("", normalized.customShortcuts[1].label)
        assertEquals("Ctrl+Shift+P", normalized.customShortcuts[1].displayLabel)
        assertTrue(TerminalAction.BACKSPACE in TerminalShortcutPreferences().visibleActions)
        assertTrue(TerminalAction.DELETE in TerminalShortcutPreferences().visibleActions)
    }

    @Test
    fun quickInputShortcutsPreserveTextRejectBlankValuesAndRemainBounded() {
        val command = "codex --dangerously-bypass-approvals-and-sandbox resume"
        val quickInput = TerminalCustomShortcut(
            id = "resume-codex",
            label = "",
            value = command,
            type = TerminalCustomShortcutType.QUICK_INPUT,
        )
        val blank = quickInput.copy(id = "blank", value = "  \n")
        val long = quickInput.copy(id = "long", value = "x".repeat(MAX_TERMINAL_QUICK_INPUT_LENGTH + 20))

        val normalized = TerminalShortcutPreferences(
            customShortcuts = listOf(quickInput, blank, long),
        ).normalized

        assertEquals(2, normalized.customShortcuts.size)
        assertEquals(command, normalized.customShortcuts[0].quickInput)
        assertEquals("", normalized.customShortcuts[0].keyCombination)
        assertEquals(command, normalized.customShortcuts[0].displayLabel)
        assertEquals(MAX_TERMINAL_QUICK_INPUT_LENGTH, normalized.customShortcuts[1].value.length)
    }

    @Test
    fun quickInputDispatchesExactSavedTextWithoutUsingTheKeyPath() = runBlocking {
        val command = "codex --dangerously-bypass-approvals-and-sandbox resume"
        var keyCombination: String? = null
        var quickInput: String? = null

        dispatchTerminalCustomShortcut(
            shortcut = TerminalCustomShortcut(
                id = "resume-codex",
                label = "Codex",
                value = command,
                type = TerminalCustomShortcutType.QUICK_INPUT,
            ),
            sendKeyCombination = { keyCombination = it },
            sendQuickInput = { quickInput = it },
        )

        assertEquals(null, keyCombination)
        assertEquals(command, quickInput)
        assertFalse(quickInput!!.endsWith('\r'))
        assertFalse(quickInput!!.endsWith('\n'))
    }

    @Test
    fun unifiedShortcutOrderInterleavesBuiltInAndCustomItemsAndRepairsMissingKeys() {
        val custom = TerminalCustomShortcut(
            id = "resume-codex",
            label = "Codex",
            value = "codex resume",
            type = TerminalCustomShortcutType.QUICK_INPUT,
        )
        val preferences = TerminalShortcutPreferences(
            visibleActions = listOf(
                TerminalAction.DELETE,
                TerminalAction.TAB,
                TerminalAction.ENTER,
            ),
            customShortcuts = listOf(custom),
            shortcutOrder = listOf(
                terminalBuiltInShortcutOrderKey(TerminalAction.DELETE),
                terminalCustomShortcutOrderKey(custom.id),
                "unknown:item",
                terminalBuiltInShortcutOrderKey(TerminalAction.TAB),
            ),
        )

        val normalized = preferences.normalized
        val items = orderedTerminalShortcutItems(preferences)

        assertEquals(
            listOf(
                terminalBuiltInShortcutOrderKey(TerminalAction.DELETE),
                terminalCustomShortcutOrderKey(custom.id),
                terminalBuiltInShortcutOrderKey(TerminalAction.TAB),
            ),
            normalized.shortcutOrder.take(3),
        )
        assertEquals(TerminalShortcutItem.BuiltIn(TerminalAction.DELETE), items[0])
        assertEquals(TerminalShortcutItem.Custom(custom), items[1])
        assertEquals(TerminalShortcutItem.BuiltIn(TerminalAction.TAB), items[2])
        assertEquals(TerminalAction.entries.size + 1, items.size)
        assertEquals(false, normalized.shortcutOrder.contains("unknown:item"))
    }
}
