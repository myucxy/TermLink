package dev.termlink.history

import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import dev.termlink.database.DeviceEntity
import dev.termlink.database.PcEntity
import dev.termlink.database.TermLinkDatabase
import dev.termlink.model.DeviceSummary
import dev.termlink.model.DeviceTrustState
import dev.termlink.model.DeviceType
import dev.termlink.model.PcSummary
import dev.termlink.model.SessionState
import dev.termlink.model.SessionSummary
import java.time.Instant
import java.util.UUID
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class HistoryRepositorySecurityTest {
    private lateinit var database: TermLinkDatabase
    private lateinit var repository: HistoryRepository

    @Before
    fun setUp() {
        database = Room.inMemoryDatabaseBuilder(
            ApplicationProvider.getApplicationContext(),
            TermLinkDatabase::class.java,
        ).allowMainThreadQueries().build()
        repository = HistoryRepository(database, clockMillis = { 5_000 })
    }

    @After
    fun tearDown() {
        database.close()
    }

    @Test
    fun migratedEmptyKeyCanInitializeOnceButDeviceIdentityCannotBeSubstituted() = runBlocking {
        val deviceId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val pcId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        database.devices().upsert(
            DeviceEntity(
                id = deviceId.toString(),
                type = DeviceType.WINDOWS_PC.name,
                name = "Migrated PC",
                keyId = "legacy",
                exchangePublicKey = byteArrayOf(),
                trustState = DeviceTrustState.TRUSTED.name,
                createdAtEpochMillis = 1,
            ),
        )
        database.pcs().upsert(pcEntity(pcId, deviceId, keyEpoch = 1))
        val authoritativeKey = ByteArray(32) { (it + 1).toByte() }
        val authoritativeDevice = deviceSummary(deviceId, authoritativeKey)

        repository.persistAuthoritativeKeyMetadata(
            listOf(authoritativeDevice),
            listOf(pcSummary(pcId, deviceId, keyEpoch = 1)),
        )
        assertArrayEquals(authoritativeKey, database.devices().get(deviceId.toString())?.exchangePublicKey)

        val substitutedKey = ByteArray(32) { (it + 33).toByte() }
        assertThrows(IllegalArgumentException::class.java) {
            runBlocking {
                repository.persistAuthoritativeKeyMetadata(
                    listOf(authoritativeDevice.copy(exchangePublicKey = substitutedKey)),
                    listOf(pcSummary(pcId, deviceId, keyEpoch = 2)),
                )
            }
        }
        assertArrayEquals(authoritativeKey, database.devices().get(deviceId.toString())?.exchangePublicKey)
        assertEquals(1L, database.pcs().get(pcId.toString())?.keyEpoch)
    }

    @Test
    fun authoritativeEpochCannotRegressAndRejectedTransactionLeavesNewestEpoch() = runBlocking {
        val deviceId = UUID.fromString("11112222-3333-4444-5555-666677778888")
        val pcId = UUID.fromString("9999aaaa-bbbb-cccc-dddd-eeeeffffffff")
        val key = ByteArray(32) { (it + 7).toByte() }
        val device = deviceSummary(deviceId, key)
        repository.persistAuthoritativeKeyMetadata(listOf(device), listOf(pcSummary(pcId, deviceId, 3)))

        assertThrows(IllegalArgumentException::class.java) {
            runBlocking {
                repository.persistAuthoritativeKeyMetadata(listOf(device), listOf(pcSummary(pcId, deviceId, 2)))
            }
        }

        assertEquals(3L, database.pcs().get(pcId.toString())?.keyEpoch)
        assertArrayEquals(key, database.devices().get(deviceId.toString())?.exchangePublicKey)
    }

    @Test
    fun authoritativeReplacementDeviceAdvancesPcAndRetainsOldPublicKey() = runBlocking {
        val oldDeviceId = UUID.fromString("21112222-3333-4444-5555-666677778888")
        val newDeviceId = UUID.fromString("31112222-3333-4444-5555-666677778888")
        val pcId = UUID.fromString("4999aaaa-bbbb-cccc-dddd-eeeeffffffff")
        val oldKey = ByteArray(32) { (it + 11).toByte() }
        val newKey = ByteArray(32) { (it + 43).toByte() }
        val oldDevice = deviceSummary(oldDeviceId, oldKey)
        val newDevice = deviceSummary(newDeviceId, newKey)

        repository.persistAuthoritativeKeyMetadata(
            listOf(oldDevice),
            listOf(pcSummary(pcId, oldDeviceId, 9)),
        )
        repository.persistAuthoritativeKeyMetadata(
            listOf(oldDevice.copy(trustState = DeviceTrustState.REVOKED), newDevice),
            listOf(pcSummary(pcId, newDeviceId, 10)),
        )

        val storedPc = database.pcs().get(pcId.toString())
        assertEquals(newDeviceId.toString(), storedPc?.deviceId)
        assertEquals(10L, storedPc?.keyEpoch)
        assertArrayEquals(oldKey, database.devices().get(oldDeviceId.toString())?.exchangePublicKey)
        assertArrayEquals(newKey, database.devices().get(newDeviceId.toString())?.exchangePublicKey)
    }

    @Test
    fun replacementDeviceWithoutEpochAdvanceRollsBackAtomically() = runBlocking {
        val oldDeviceId = UUID.fromString("51112222-3333-4444-5555-666677778888")
        val newDeviceId = UUID.fromString("61112222-3333-4444-5555-666677778888")
        val pcId = UUID.fromString("7999aaaa-bbbb-cccc-dddd-eeeeffffffff")
        val oldKey = ByteArray(32) { (it + 17).toByte() }
        val newKey = ByteArray(32) { (it + 49).toByte() }
        val oldDevice = deviceSummary(oldDeviceId, oldKey)
        repository.persistAuthoritativeKeyMetadata(
            listOf(oldDevice),
            listOf(pcSummary(pcId, oldDeviceId, 7)),
        )

        assertThrows(IllegalArgumentException::class.java) {
            runBlocking {
                repository.persistAuthoritativeKeyMetadata(
                    listOf(oldDevice, deviceSummary(newDeviceId, newKey)),
                    listOf(pcSummary(pcId, newDeviceId, 7)),
                )
            }
        }

        val storedPc = database.pcs().get(pcId.toString())
        assertEquals(oldDeviceId.toString(), storedPc?.deviceId)
        assertEquals(7L, storedPc?.keyEpoch)
        assertEquals(null, database.devices().get(newDeviceId.toString()))
        assertArrayEquals(oldKey, database.devices().get(oldDeviceId.toString())?.exchangePublicKey)
    }

    @Test
    fun exitedSessionAndOutputCursorCannotBeRegressedByAnOlderSummary() = runBlocking {
        val sessionId = UUID.fromString("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")
        val pcId = UUID.fromString("bbbbbbbb-cccc-dddd-eeee-ffffffffffff")
        val endedAt = Instant.parse("2026-08-05T12:00:00Z")
        repository.upsertSession(
            sessionSummary(sessionId, pcId, SessionState.EXITED, 20u, endedAt),
        )

        repository.upsertSession(
            sessionSummary(sessionId, pcId, SessionState.RUNNING, 5u, null),
        )

        val retained = repository.sessions().single()
        assertEquals(SessionState.EXITED, retained.state)
        assertEquals(20uL, retained.lastOutputSequence)
        assertEquals(endedAt, retained.endedAt)
    }

    private fun deviceSummary(id: UUID, key: ByteArray) = DeviceSummary(
        id = id,
        type = DeviceType.WINDOWS_PC,
        name = "Windows PC",
        keyId = "device-key",
        exchangePublicKey = key.copyOf(),
        trustState = DeviceTrustState.TRUSTED,
        createdAt = Instant.ofEpochMilli(1),
    )

    private fun pcSummary(id: UUID, deviceId: UUID, keyEpoch: Long) = PcSummary(
        id = id,
        deviceId = deviceId,
        name = "Windows PC",
        agentVersion = "1.0.0",
        keyEpoch = keyEpoch,
        remoteRoutingEnabled = true,
        online = true,
        lastSeenAt = Instant.ofEpochMilli(2),
    )

    private fun pcEntity(id: UUID, deviceId: UUID, keyEpoch: Long) = PcEntity(
        id = id.toString(),
        deviceId = deviceId.toString(),
        name = "Windows PC",
        agentVersion = "1.0.0",
        keyEpoch = keyEpoch,
        remoteRoutingEnabled = true,
        online = true,
        lastSeenAtEpochMillis = 2,
    )

    private fun sessionSummary(
        id: UUID,
        pcId: UUID,
        state: SessionState,
        lastOutputSequence: ULong,
        endedAt: Instant?,
    ) = SessionSummary(
        id = id,
        pcId = pcId,
        shellType = "POWERSHELL",
        encryptedTitle = null,
        state = state,
        columns = 120,
        rows = 36,
        lastOutputSequence = lastOutputSequence,
        createdAt = Instant.parse("2026-08-05T10:00:00Z"),
        endedAt = endedAt,
    )
}
