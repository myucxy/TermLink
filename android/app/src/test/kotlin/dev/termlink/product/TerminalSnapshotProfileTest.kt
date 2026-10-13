package dev.termlink.product

import dev.termlink.crypto.TerminalSnapshotCrypto
import dev.termlink.network.production.ProductionTerminalProfile
import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class TerminalSnapshotProfileTest {
    private val recipient = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
    private val legacyFormat = "TERMLINK_TERMINAL_CORE_SNAPSHOT"
    private val graphicsFormat = "TERMLINK_TERMINAL_CORE_GRAPHICS_SNAPSHOT"

    @Test
    fun graphicsProfilesShareContainerButKeepDistinctWidthAndKeyContracts() {
        val old = ProductionTerminalProfile.GRAPHICS
        val unicode16 = ProductionTerminalProfile.GRAPHICS_UNICODE16
        assertEquals(1, old.widthPolicy)
        assertEquals(2, unicode16.widthPolicy)
        assertFalse(old.stateContract == unicode16.stateContract)
        assertFalse(key(old, 7) == key(unicode16, 7))
    }

    @Test
    fun authenticatedProfileRequiresItsExactFormatAndVersion() {
        for (profile in ProductionTerminalProfile.entries) {
            for (format in listOf(legacyFormat, graphicsFormat, "TERMLINK_SNAPSHOT", null)) {
                for (version in listOf(1, 2, 3, null)) {
                    val expected = when (profile) {
                        ProductionTerminalProfile.LEGACY -> format == legacyFormat && version == 2
                        ProductionTerminalProfile.GRAPHICS,
                        ProductionTerminalProfile.GRAPHICS_UNICODE16 -> format == graphicsFormat && version == 1
                    }
                    assertEquals(expected, terminalSnapshotMatchesProfile(profile, format, version, 1))
                }
            }
        }
    }

    @Test
    fun objectAdmissionRejectsInvalidAndOversizedObjectsBeforeCaching() {
        for (profile in ProductionTerminalProfile.entries) {
            val graphics = profile.isGraphics
            val format = if (graphics) graphicsFormat else legacyFormat
            val version = if (graphics) 1 else 2
            val maximum = if (graphics) 128L * 1024 * 1024 + 4096 else 64L * 1024 * 1024
            assertTrue(terminalSnapshotMatchesProfile(profile, format, version, maximum))
            for (size in listOf(Long.MIN_VALUE, -1, 0, maximum + 1, Long.MAX_VALUE)) {
                assertFalse(terminalSnapshotMatchesProfile(profile, format, version, size))
            }
        }
        assertFalse(terminalSnapshotMatchesProfile(
            ProductionTerminalProfile.LEGACY, legacyFormat, 2, 128L * 1024 * 1024,
        ))
    }

    @Test
    fun keyDomainsNeverSelectOrUpgradeTheAuthenticatedProfile() {
        val legacyKey = TerminalSnapshotCrypto.terminalCoreSnapshotKeyId(recipient, 7)
        val graphicsKey = TerminalSnapshotCrypto.terminalCoreGraphicsSnapshotKeyId(recipient, 7)
        val unicode16Key = TerminalSnapshotCrypto.terminalCoreGraphicsSnapshotKeyId(recipient, 7,
            ProductionTerminalProfile.GRAPHICS_UNICODE16.stateContract)
        assertEquals(7L, terminalCoreSnapshotKeyEpoch(ProductionTerminalProfile.LEGACY, recipient, legacyKey))
        assertEquals(7L, terminalCoreSnapshotKeyEpoch(ProductionTerminalProfile.GRAPHICS, recipient, graphicsKey))
        assertEquals(7L, terminalCoreSnapshotKeyEpoch(ProductionTerminalProfile.GRAPHICS_UNICODE16, recipient, unicode16Key))
        assertNull(terminalCoreSnapshotKeyEpoch(ProductionTerminalProfile.LEGACY, recipient, graphicsKey))
        assertNull(terminalCoreSnapshotKeyEpoch(ProductionTerminalProfile.GRAPHICS, recipient, legacyKey))
        assertNull(terminalCoreSnapshotKeyEpoch(ProductionTerminalProfile.GRAPHICS_UNICODE16, recipient, legacyKey))
        assertNull(terminalCoreSnapshotKeyEpoch(ProductionTerminalProfile.GRAPHICS_UNICODE16, recipient, graphicsKey))
        assertNull(terminalCoreSnapshotKeyEpoch(ProductionTerminalProfile.GRAPHICS, recipient, unicode16Key))
        for (profile in ProductionTerminalProfile.entries) {
            assertNull(terminalCoreSnapshotKeyEpoch(profile, UUID.randomUUID(), key(profile, 7)))
            assertNull(terminalCoreSnapshotKeyEpoch(profile, recipient,
                TerminalSnapshotCrypto.transcriptKeyId(recipient, 7)))
        }
    }

    @Test
    fun canonicalPositiveKeyEpochsRoundTripAcrossTheFullLongRange() {
        for (profile in ProductionTerminalProfile.entries) {
            for (epoch in listOf(1L, 99_999_999L, 100_000_000L, Long.MAX_VALUE)) {
                assertEquals(epoch, terminalCoreSnapshotKeyEpoch(profile, recipient, key(profile, epoch)))
            }
        }
    }

    @Test
    fun malformedOrNoncanonicalEpochsCannotEnterTheCryptoDomain() {
        for (profile in ProductionTerminalProfile.entries) {
            val prefix = key(profile, 1).substringBeforeLast(':') + ":"
            for (encoded in listOf(
                "", "1", "00000000", "000000001", "-0000001", "+0000001", "0000000１",
                "00000001 ", "9223372036854775808", "99999999999999999999",
            )) {
                assertNull(terminalCoreSnapshotKeyEpoch(profile, recipient, prefix + encoded))
            }
        }
    }

    private fun key(profile: ProductionTerminalProfile, epoch: Long): String = when (profile) {
        ProductionTerminalProfile.LEGACY -> TerminalSnapshotCrypto.terminalCoreSnapshotKeyId(recipient, epoch)
        ProductionTerminalProfile.GRAPHICS,
        ProductionTerminalProfile.GRAPHICS_UNICODE16 -> TerminalSnapshotCrypto.terminalCoreGraphicsSnapshotKeyId(
            recipient, epoch, profile.stateContract)
    }
}
