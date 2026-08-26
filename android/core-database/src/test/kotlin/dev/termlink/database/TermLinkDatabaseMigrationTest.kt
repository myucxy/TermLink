package dev.termlink.database

import androidx.room.Room
import androidx.sqlite.db.SupportSQLiteDatabase
import androidx.test.core.app.ApplicationProvider
import kotlin.test.assertEquals
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class TermLinkDatabaseMigrationTest {
    @Test
    fun migrateV5ToV6PreservesPcAndInitializesLegacyPlatformMetadata() {
        createDatabaseAtVersion(5) {
            execSQL(
                """
                INSERT INTO pcs(id, deviceId, name, agentVersion, keyEpoch, remoteRoutingEnabled, online, lastSeenAtEpochMillis)
                VALUES ('pc-v5', 'device-v5', 'legacy-pc', '0.1.32.38', 7, 1, 1, 1234)
                """.trimIndent(),
            )
        }
        val migrated = openMigratedDatabase()
        try {
            migrated.openHelper.writableDatabase.query(
                "SELECT platform, architecture, platformVersion, capabilitiesCsv, keyEpoch FROM pcs WHERE id='pc-v5'",
            ).use { cursor ->
                check(cursor.moveToFirst())
                assertEquals("windows", cursor.getString(0))
                assertEquals("unknown", cursor.getString(1))
                assertEquals("legacy", cursor.getString(2))
                assertEquals("", cursor.getString(3))
                assertEquals(7L, cursor.getLong(4))
            }
        } finally {
            migrated.close()
        }
    }

    @Test
    fun migrateV4ToV5AddsEmptyEncryptedTranscriptCacheWithoutChangingExistingRows() {
        createDatabaseAtVersion(4) {
            execSQL(
                """
                INSERT INTO ack_cursors(sessionId, direction, rendererGeneration, highestContiguousSequence, updatedAtEpochMillis)
                VALUES ('session-v4', 'PC_TO_ANDROID', 3, 42, 1234)
                """.trimIndent(),
            )
        }

        val migrated = openMigratedDatabase()
        try {
            val database = migrated.openHelper.writableDatabase
            database.query(
                "SELECT rendererGeneration, highestContiguousSequence FROM ack_cursors WHERE sessionId = 'session-v4'",
            ).use { cursor ->
                check(cursor.moveToFirst())
                assertEquals(3L, cursor.getLong(0))
                assertEquals(42L, cursor.getLong(1))
            }
            database.query("SELECT COUNT(*) FROM transcript_pages").use { cursor ->
                check(cursor.moveToFirst())
                assertEquals(0L, cursor.getLong(0))
            }
        } finally {
            migrated.close()
        }
    }

    @Test
    fun migrateV3ToV4PreservesAckCursorWithoutInventingRendererCheckpoint() {
        createDatabaseAtVersion(3) {
            execSQL(
                """
                INSERT INTO ack_cursors(sessionId, direction, rendererGeneration, highestContiguousSequence, updatedAtEpochMillis)
                VALUES ('session-v3', 'PC_TO_ANDROID', 7, 42, 1234)
                """.trimIndent(),
            )
        }

        val migrated = openMigratedDatabase()
        try {
            val database = migrated.openHelper.writableDatabase
            database.query(
                "SELECT rendererGeneration, highestContiguousSequence FROM ack_cursors WHERE sessionId = 'session-v3' AND direction = 'PC_TO_ANDROID'",
            ).use { cursor ->
                check(cursor.moveToFirst())
                assertEquals(7L, cursor.getLong(0))
                assertEquals(42L, cursor.getLong(1))
            }
            database.query("SELECT COUNT(*) FROM renderer_checkpoints").use { cursor ->
                check(cursor.moveToFirst())
                assertEquals(0L, cursor.getLong(0))
            }
        } finally {
            migrated.close()
        }
    }

    @Test
    fun migrateV2ToV3PreservesAckCursorAndInitializesRendererGeneration() {
        createDatabaseAtVersion(2) {
            execSQL(
                """
                INSERT INTO ack_cursors(sessionId, direction, highestContiguousSequence, updatedAtEpochMillis)
                VALUES ('session-v2', 'PC_TO_ANDROID', 42, 1234)
                """.trimIndent(),
            )
        }

        val migrated = openMigratedDatabase()
        try {
            val database = migrated.openHelper.writableDatabase
            database.query(
                "SELECT rendererGeneration, highestContiguousSequence, updatedAtEpochMillis FROM ack_cursors WHERE sessionId = 'session-v2' AND direction = 'PC_TO_ANDROID'",
            ).use { cursor ->
                check(cursor.moveToFirst())
                assertEquals(1L, cursor.getLong(0))
                assertEquals(42L, cursor.getLong(1))
                assertEquals(1234L, cursor.getLong(2))
            }
        } finally {
            migrated.close()
        }
    }

    @Test
    fun migrateV1ToV3PreservesEmptyExchangeKeyAndInitializesPcKeyEpoch() {
        createDatabaseAtVersion(1) {
            execSQL(
                """
                INSERT INTO devices(id, type, name, keyId, trustState, createdAtEpochMillis)
                VALUES ('device-v1', 'PHONE', 'legacy', 'key-id', 'UNTRUSTED', 1000)
                """.trimIndent(),
            )
            execSQL(
                """
                INSERT INTO pcs(id, deviceId, name, agentVersion, remoteRoutingEnabled, online, lastSeenAtEpochMillis)
                VALUES ('pc-v1', 'device-v1', 'legacy-pc', '1.0', 0, 0, NULL)
                """.trimIndent(),
            )
        }

        val migrated = openMigratedDatabase()
        try {
            val database = migrated.openHelper.writableDatabase
            database.query(
                "SELECT exchangePublicKey, trustState FROM devices WHERE id = 'device-v1'",
            ).use { cursor ->
                check(cursor.moveToFirst())
                assertEquals(0, cursor.getBlob(0).size)
                assertEquals("UNTRUSTED", cursor.getString(1))
            }
            database.query("SELECT keyEpoch FROM pcs WHERE id = 'pc-v1'").use { cursor ->
                check(cursor.moveToFirst())
                assertEquals(1L, cursor.getLong(0))
            }
        } finally {
            migrated.close()
        }
    }

    /**
     * Room's MigrationTestHelper expects schemas packaged as test assets, while
     * this module exports them to the repository-level schemas directory. Seed
     * a real current Room database, rebuild only the columns introduced by later
     * versions, and then let Room run the production migrations against it.
     */
    private fun createDatabaseAtVersion(
        version: Int,
        insertRows: SupportSQLiteDatabase.() -> Unit,
    ) {
        val context = ApplicationProvider.getApplicationContext<android.content.Context>()
        context.deleteDatabase(DATABASE_NAME)
        val seed = Room.databaseBuilder(context, TermLinkDatabase::class.java, DATABASE_NAME)
            .allowMainThreadQueries()
            .build()
        val database = seed.openHelper.writableDatabase
        when (version) {
            1 -> {
                downgradeV6ToV5(database)
                downgradeV5ToV4(database)
                downgradeV4ToV1(database)
            }
            2 -> {
                downgradeV6ToV5(database)
                downgradeV5ToV4(database)
                downgradeV4ToV2(database)
            }
            3 -> {
                downgradeV6ToV5(database)
                downgradeV5ToV4(database)
                downgradeV4ToV3(database)
            }
            4 -> {
                downgradeV6ToV5(database)
                downgradeV5ToV4(database)
            }
            5 -> downgradeV6ToV5(database)
            else -> error("Unsupported test database version: $version")
        }
        database.insertRows()
        seed.close()
    }

    private fun openMigratedDatabase(): TermLinkDatabase {
        val context = ApplicationProvider.getApplicationContext<android.content.Context>()
        val migrated = Room.databaseBuilder(context, TermLinkDatabase::class.java, DATABASE_NAME)
            .addMigrations(
                TermLinkDatabase.MIGRATION_1_2,
                TermLinkDatabase.MIGRATION_2_3,
                TermLinkDatabase.MIGRATION_3_4,
                TermLinkDatabase.MIGRATION_4_5,
                TermLinkDatabase.MIGRATION_5_6,
            )
            .allowMainThreadQueries()
            .build()
        migrated.openHelper.writableDatabase
        return migrated
    }

    private fun downgradeV6ToV5(database: SupportSQLiteDatabase) {
        database.execSQL(
            """
            CREATE TABLE pcs_v5 (
                id TEXT NOT NULL,
                deviceId TEXT NOT NULL,
                name TEXT NOT NULL,
                agentVersion TEXT NOT NULL,
                keyEpoch INTEGER NOT NULL,
                remoteRoutingEnabled INTEGER NOT NULL,
                online INTEGER NOT NULL,
                lastSeenAtEpochMillis INTEGER,
                PRIMARY KEY(id)
            )
            """.trimIndent(),
        )
        database.execSQL(
            """
            INSERT INTO pcs_v5(id, deviceId, name, agentVersion, keyEpoch, remoteRoutingEnabled, online, lastSeenAtEpochMillis)
            SELECT id, deviceId, name, agentVersion, keyEpoch, remoteRoutingEnabled, online, lastSeenAtEpochMillis FROM pcs
            """.trimIndent(),
        )
        database.execSQL("DROP TABLE pcs")
        database.execSQL("ALTER TABLE pcs_v5 RENAME TO pcs")
        database.execSQL("CREATE INDEX index_pcs_deviceId ON pcs(deviceId)")
        database.execSQL("CREATE INDEX index_pcs_online_lastSeenAtEpochMillis ON pcs(online, lastSeenAtEpochMillis)")
        setRoomVersion(database, 5, V5_IDENTITY_HASH)
    }

    private fun downgradeV5ToV4(database: SupportSQLiteDatabase) {
        database.execSQL("DROP TABLE transcript_pages")
        setRoomVersion(database, 4, V4_IDENTITY_HASH)
    }

    private fun downgradeV4ToV3(database: SupportSQLiteDatabase) {
        database.execSQL("DROP TABLE renderer_checkpoints")
        setRoomVersion(database, 3, V3_IDENTITY_HASH)
    }

    private fun downgradeV4ToV2(database: SupportSQLiteDatabase) {
        downgradeV4ToV3(database)
        database.execSQL(
            """
            CREATE TABLE ack_cursors_legacy (
                sessionId TEXT NOT NULL,
                direction TEXT NOT NULL,
                highestContiguousSequence INTEGER NOT NULL,
                updatedAtEpochMillis INTEGER NOT NULL,
                PRIMARY KEY(sessionId, direction)
            )
            """.trimIndent(),
        )
        database.execSQL(
            """
            INSERT INTO ack_cursors_legacy(sessionId, direction, highestContiguousSequence, updatedAtEpochMillis)
            SELECT sessionId, direction, highestContiguousSequence, updatedAtEpochMillis FROM ack_cursors
            """.trimIndent(),
        )
        database.execSQL("DROP TABLE ack_cursors")
        database.execSQL("ALTER TABLE ack_cursors_legacy RENAME TO ack_cursors")
        database.execSQL(
            "CREATE INDEX index_ack_cursors_sessionId_highestContiguousSequence ON ack_cursors(sessionId, highestContiguousSequence)",
        )
        setRoomVersion(database, 2, V2_IDENTITY_HASH)
    }

    private fun downgradeV4ToV1(database: SupportSQLiteDatabase) {
        downgradeV4ToV2(database)
        database.execSQL(
            """
            CREATE TABLE devices_legacy (
                id TEXT NOT NULL,
                type TEXT NOT NULL,
                name TEXT NOT NULL,
                keyId TEXT NOT NULL,
                trustState TEXT NOT NULL,
                createdAtEpochMillis INTEGER NOT NULL,
                PRIMARY KEY(id)
            )
            """.trimIndent(),
        )
        database.execSQL(
            """
            INSERT INTO devices_legacy(id, type, name, keyId, trustState, createdAtEpochMillis)
            SELECT id, type, name, keyId, trustState, createdAtEpochMillis FROM devices
            """.trimIndent(),
        )
        database.execSQL("DROP TABLE devices")
        database.execSQL("ALTER TABLE devices_legacy RENAME TO devices")
        database.execSQL("CREATE INDEX index_devices_trustState ON devices(trustState)")
        database.execSQL("CREATE INDEX index_devices_createdAtEpochMillis ON devices(createdAtEpochMillis)")

        database.execSQL(
            """
            CREATE TABLE pcs_legacy (
                id TEXT NOT NULL,
                deviceId TEXT NOT NULL,
                name TEXT NOT NULL,
                agentVersion TEXT NOT NULL,
                remoteRoutingEnabled INTEGER NOT NULL,
                online INTEGER NOT NULL,
                lastSeenAtEpochMillis INTEGER,
                PRIMARY KEY(id)
            )
            """.trimIndent(),
        )
        database.execSQL(
            """
            INSERT INTO pcs_legacy(id, deviceId, name, agentVersion, remoteRoutingEnabled, online, lastSeenAtEpochMillis)
            SELECT id, deviceId, name, agentVersion, remoteRoutingEnabled, online, lastSeenAtEpochMillis FROM pcs
            """.trimIndent(),
        )
        database.execSQL("DROP TABLE pcs")
        database.execSQL("ALTER TABLE pcs_legacy RENAME TO pcs")
        database.execSQL("CREATE INDEX index_pcs_deviceId ON pcs(deviceId)")
        database.execSQL("CREATE INDEX index_pcs_online_lastSeenAtEpochMillis ON pcs(online, lastSeenAtEpochMillis)")
        setRoomVersion(database, 1, V1_IDENTITY_HASH)
    }

    private fun setRoomVersion(database: SupportSQLiteDatabase, version: Int, identityHash: String) {
        database.execSQL("PRAGMA user_version = $version")
        database.execSQL("UPDATE room_master_table SET identity_hash = '$identityHash' WHERE id = 42")
    }

    private companion object {
        const val DATABASE_NAME = "termlink-migration-test.db"
        const val V1_IDENTITY_HASH = "0e5a433e894b631b4fcd81e47a099256"
        const val V2_IDENTITY_HASH = "db77fd61744b9b1bd762519fc91e1618"
        const val V3_IDENTITY_HASH = "1ff9378e8357f8db649a79c32492baec"
        const val V4_IDENTITY_HASH = "5256cfab24318a815b71dfa8dcbdca0e"
        const val V5_IDENTITY_HASH = "67871d1456c2ccef2e9d2d23526a07c3"
    }
}
