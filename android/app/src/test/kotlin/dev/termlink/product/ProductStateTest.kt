package dev.termlink.product

import android.content.res.Configuration
import dev.termlink.model.DeviceTrustState
import dev.termlink.model.PcSummary
import dev.termlink.model.SessionState
import dev.termlink.model.SessionSummary
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
    fun productStatusBarOnlyHidesInLandscape() {
        assertTrue(shouldHideProductStatusBar(Configuration.ORIENTATION_LANDSCAPE))
        assertFalse(shouldHideProductStatusBar(Configuration.ORIENTATION_PORTRAIT))
        assertFalse(shouldHideProductStatusBar(Configuration.ORIENTATION_UNDEFINED))
    }

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
    fun sessionCarrierMappingUsesExactSessionIdAndFailsClosed() {
        val pcId = UUID.fromString("30000000-0000-0000-0000-000000000001")
        val deviceId = UUID.fromString("20000000-0000-0000-0000-000000000001")
        val session = testSession(pcId)
        val pc = testPc(pcId, deviceId)
        val snapshot = ProductSnapshot(
            pcs = listOf(pc),
            pcCarrierStates = mapOf(deviceId to PcCarrierState.LAN_DIRECT),
            sessionCarrierStates = mapOf(session.id to PcCarrierState.LAN_DIRECT),
        )

        assertEquals(PcCarrierState.LAN_DIRECT, snapshot.carrierForSession(session))
        assertEquals(
            PcCarrierState.RELAY,
            snapshot.copy(sessionCarrierStates = mapOf(pcId to PcCarrierState.NAT_DIRECT))
                .carrierForSession(session),
        )
        assertEquals(
            PcCarrierState.RELAY,
            snapshot.copy(sessionCarrierStates = emptyMap()).carrierForSession(session),
        )
        assertEquals(
            PcCarrierState.LAN_DIRECT,
            snapshot.copy(pcs = emptyList()).carrierForSession(session),
        )
    }

    @Test
    fun removingSessionDoesNotRemovePcCarrierState() {
        val pcId = UUID.fromString("30000000-0000-0000-0000-000000000002")
        val deviceId = UUID.fromString("20000000-0000-0000-0000-000000000002")
        val session = testSession(pcId)
        val carrierStates = mapOf(deviceId to PcCarrierState.NAT_DIRECT)
        val snapshot = ProductSnapshot(
            pcs = listOf(testPc(pcId, deviceId)),
            sessions = listOf(session),
            pcCarrierStates = carrierStates,
            sessionCarrierStates = mapOf(session.id to PcCarrierState.NAT_DIRECT),
        )

        val removed = snapshot.withoutSession(session.id)

        assertTrue(removed.sessions.isEmpty())
        assertEquals(carrierStates, removed.pcCarrierStates)
        assertEquals(PcCarrierState.NAT_DIRECT, removed.pcCarrierStates[deviceId])
        assertTrue(removed.sessionCarrierStates.isEmpty())
    }

    private fun testPc(id: UUID, deviceId: UUID): PcSummary = PcSummary(
        id = id,
        deviceId = deviceId,
        name = "test-pc",
        agentVersion = "test",
        remoteRoutingEnabled = true,
        online = true,
        lastSeenAt = Instant.EPOCH,
    )

    private fun testSession(pcId: UUID): SessionSummary = SessionSummary(
        id = UUID.nameUUIDFromBytes(pcId.toString().toByteArray()),
        pcId = pcId,
        shellType = "powershell",
        encryptedTitle = null,
        state = SessionState.RUNNING,
        columns = 80,
        rows = 24,
        lastOutputSequence = 0u,
        createdAt = Instant.EPOCH,
        endedAt = null,
        online = true,
    )

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
            fontScheme = TerminalFontScheme.CUSTOM,
            customFontDisplayName = "  My Font.ttf  ",
            customFontSha256 = "A".repeat(64),
            fontSizeSp = 2f,
            lineSpacingMultiplier = 4f,
            foregroundArgb = 0x00112233,
            backgroundArgb = 0x00445566,
        ).normalized

        assertEquals(10f, normalized.fontSizeSp)
        assertEquals(TerminalFontScheme.CUSTOM, normalized.fontScheme)
        assertEquals("My Font.ttf", normalized.customFontDisplayName)
        assertEquals("a".repeat(64), normalized.customFontSha256)
        assertEquals(1.8f, normalized.lineSpacingMultiplier)
        assertEquals(0xFF112233.toInt(), normalized.foregroundArgb)
        assertEquals(0xFF445566.toInt(), normalized.backgroundArgb)
    }

    @Test
    fun terminalFontSchemeUnknownValuesFallBackToBundledNerdFont() {
        assertEquals(
            TerminalFontScheme.BUNDLED_NERD_FONT,
            TerminalFontScheme.fromWireValue("future-font"),
        )
        assertEquals(
            TerminalFontScheme.SYSTEM_MONOSPACE,
            TerminalFontScheme.fromWireValue("system-monospace"),
        )
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
        assertEquals(fixedTerminalShortcutRows().flatten(), normalized.visibleActions)
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
            "Home",
            "Ctrl+End",
            "Shift+PageUp",
            "Alt+PageDown",
            "Insert",
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
    fun fixedBuiltInLayoutAndCustomRowsRepairLegacyOrder() {
        val first = TerminalCustomShortcut("first", "First", "Home")
        val second = TerminalCustomShortcut("second", "Second", "End")
        val third = TerminalCustomShortcut("third", "Third", "PageUp")
        val preferences = TerminalShortcutPreferences(
            visibleActions = listOf(TerminalAction.DELETE),
            customShortcuts = listOf(first, second, third),
            shortcutOrder = listOf(
                terminalBuiltInShortcutOrderKey(TerminalAction.DELETE),
                terminalCustomShortcutOrderKey(second.id),
                "unknown:item",
                terminalCustomShortcutOrderKey(first.id),
                terminalCustomShortcutOrderKey(third.id),
            ),
        )

        val normalized = preferences.normalized
        val items = orderedTerminalShortcutItems(preferences)

        assertEquals(
            listOf(
                listOf(second.id, third.id),
                listOf(first.id),
            ),
            normalized.customShortcutRows,
        )
        assertEquals(fixedTerminalShortcutRows().flatten(), normalized.visibleActions)
        assertEquals(
            fixedTerminalShortcutRows().flatten().map { TerminalShortcutItem.BuiltIn(it) },
            items.take(TerminalAction.entries.size),
        )
        assertEquals(
            listOf(
                TerminalShortcutItem.Custom(second),
                TerminalShortcutItem.Custom(first),
                TerminalShortcutItem.Custom(third),
            ),
            items.drop(TerminalAction.entries.size),
        )
        assertEquals(false, normalized.shortcutOrder.contains("unknown:item"))
    }

    @Test
    fun customShortcutsMoveWithinAndBetweenStableRows() {
        val first = TerminalCustomShortcut("first", "First", "Home")
        val second = TerminalCustomShortcut("second", "Second", "End")
        val third = TerminalCustomShortcut("third", "Third", "PageDown")
        val preferences = TerminalShortcutPreferences(
            customShortcuts = listOf(first, second, third),
            customShortcutRows = listOf(
                listOf(first.id, second.id),
                listOf(third.id),
            ),
        ).normalized

        val reordered = moveTerminalCustomShortcut(preferences, second.id, -1)
        assertEquals(listOf(second.id, first.id), reordered.customShortcutRows[TERMINAL_SHORTCUT_TOP_ROW])
        assertEquals(listOf(third.id), reordered.customShortcutRows[TERMINAL_SHORTCUT_BOTTOM_ROW])

        val editedSecond = second.copy(label = "Second edited")
        val editedInPlace = placeTerminalCustomShortcut(
            reordered.copy(
                customShortcuts = reordered.customShortcuts.map {
                    if (it.id == editedSecond.id) editedSecond else it
                },
            ),
            editedSecond.id,
            TerminalShortcutDockRow.TOP,
        )
        assertEquals(listOf(second.id, first.id), editedInPlace.customShortcutRows[TERMINAL_SHORTCUT_TOP_ROW])
        assertEquals("Second edited", editedInPlace.customShortcuts.first { it.id == second.id }.label)

        val moved = placeTerminalCustomShortcut(reordered, first.id, TerminalShortcutDockRow.BOTTOM)
        assertEquals(listOf(second.id), moved.customShortcutRows[TERMINAL_SHORTCUT_TOP_ROW])
        assertEquals(listOf(third.id, first.id), moved.customShortcutRows[TERMINAL_SHORTCUT_BOTTOM_ROW])

        val keptAtMatchingColumn = placeTerminalCustomShortcut(moved, second.id, TerminalShortcutDockRow.BOTTOM)
        assertEquals(emptyList<String>(), keptAtMatchingColumn.customShortcutRows[TERMINAL_SHORTCUT_TOP_ROW])
        assertEquals(
            listOf(second.id, third.id, first.id),
            keptAtMatchingColumn.customShortcutRows[TERMINAL_SHORTCUT_BOTTOM_ROW],
        )
    }

    @Test
    fun oneAndTwoRowShortcutOrdersMoveIndependently() {
        val first = TerminalCustomShortcut("first", "First", "Home")
        val second = TerminalCustomShortcut("second", "Second", "End")
        val third = TerminalCustomShortcut("third", "Third", "PageDown")
        val oneRow = TerminalShortcutPreferences(
            customShortcuts = listOf(first, second, third),
            dockLayout = TerminalShortcutDockLayout.ONE_ROW,
            singleRowCustomShortcutOrder = listOf(third.id, first.id, second.id),
            customShortcutRows = listOf(
                listOf(first.id, second.id),
                listOf(third.id),
            ),
        ).normalized

        val oneRowReordered = moveTerminalCustomShortcut(oneRow, first.id, -1)
        assertEquals(listOf(first.id, third.id, second.id), oneRowReordered.singleRowCustomShortcutOrder)
        assertEquals(listOf(first.id, second.id), oneRowReordered.customShortcutRows[TERMINAL_SHORTCUT_TOP_ROW])
        assertEquals(listOf(third.id), oneRowReordered.customShortcutRows[TERMINAL_SHORTCUT_BOTTOM_ROW])
        assertEquals(listOf(first, third, second), orderedTerminalCustomShortcuts(oneRowReordered))

        val twoRows = oneRowReordered.copy(dockLayout = TerminalShortcutDockLayout.TWO_ROWS).normalized
        val twoRowsReordered = moveTerminalCustomShortcut(twoRows, second.id, -1)
        assertEquals(listOf(second.id, first.id), twoRowsReordered.customShortcutRows[TERMINAL_SHORTCUT_TOP_ROW])
        assertEquals(listOf(third.id), twoRowsReordered.customShortcutRows[TERMINAL_SHORTCUT_BOTTOM_ROW])
        assertEquals(listOf(first.id, third.id, second.id), twoRowsReordered.singleRowCustomShortcutOrder)

        val restoredOneRow = twoRowsReordered.copy(dockLayout = TerminalShortcutDockLayout.ONE_ROW).normalized
        assertEquals(listOf(first, third, second), orderedTerminalCustomShortcuts(restoredOneRow))
    }

    @Test
    fun singleRowBuiltInsRetainThePreTwoRowOrder() {
        assertEquals(
            listOf(
                TerminalAction.ESCAPE,
                TerminalAction.TAB,
                TerminalAction.ENTER,
                TerminalAction.BACKSPACE,
                TerminalAction.DELETE,
                TerminalAction.ARROW_LEFT,
                TerminalAction.ARROW_UP,
                TerminalAction.ARROW_DOWN,
                TerminalAction.ARROW_RIGHT,
                TerminalAction.CTRL_C,
            ),
            fixedSingleRowTerminalShortcutActions(),
        )
    }

    @Test
    fun shortcutNormalizationRepairsBothLayoutOrdersWithoutCrossOverwriting() {
        val first = TerminalCustomShortcut("first", "First", "Home")
        val second = TerminalCustomShortcut("second", "Second", "End")
        val normalized = TerminalShortcutPreferences(
            customShortcuts = listOf(first, second),
            singleRowCustomShortcutOrder = listOf("missing", second.id, second.id),
            customShortcutRows = listOf(
                listOf(first.id, "missing"),
                listOf(first.id),
            ),
        ).normalized

        assertEquals(listOf(second.id, first.id), normalized.singleRowCustomShortcutOrder)
        assertEquals(listOf(first.id), normalized.customShortcutRows[TERMINAL_SHORTCUT_TOP_ROW])
        assertEquals(listOf(second.id), normalized.customShortcutRows[TERMINAL_SHORTCUT_BOTTOM_ROW])
    }
}
