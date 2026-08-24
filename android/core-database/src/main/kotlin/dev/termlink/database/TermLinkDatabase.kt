package dev.termlink.database

import android.content.Context
import androidx.room.Database
import androidx.room.Room
import androidx.room.RoomDatabase
import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase

@Database(
    entities = [
        DeviceEntity::class,
        PcEntity::class,
        SessionEntity::class,
        EventChunkEntity::class,
        SnapshotEntity::class,
        TranscriptPageEntity::class,
        AckCursorEntity::class,
        RendererCheckpointEntity::class,
        HistoryPreferenceEntity::class,
    ],
    version = 5,
    exportSchema = true,
)
abstract class TermLinkDatabase : RoomDatabase() {
    abstract fun devices(): DeviceDao
    abstract fun pcs(): PcDao
    abstract fun sessions(): SessionDao
    abstract fun eventChunks(): EventChunkDao
    abstract fun snapshots(): SnapshotDao
    abstract fun transcriptPages(): TranscriptPageDao
    abstract fun ackCursors(): AckCursorDao
    abstract fun rendererCheckpoints(): RendererCheckpointDao
    abstract fun preferences(): HistoryPreferenceDao

    // Conventional DAO names keep wiring readable at call sites while the short
    // names above remain convenient for generated Room code.
    fun deviceDao(): DeviceDao = devices()
    fun pcDao(): PcDao = pcs()
    fun sessionDao(): SessionDao = sessions()
    fun eventChunkDao(): EventChunkDao = eventChunks()
    fun snapshotDao(): SnapshotDao = snapshots()
    fun transcriptPageDao(): TranscriptPageDao = transcriptPages()
    fun ackCursorDao(): AckCursorDao = ackCursors()
    fun rendererCheckpointDao(): RendererCheckpointDao = rendererCheckpoints()
    fun preferenceDao(): HistoryPreferenceDao = preferences()

    companion object {
        /**
         * Creates a database whose rows are safe to inspect: sensitive values are
         * encrypted by the caller before insertion. The application may provide a
         * Keystore-backed SQLite factory when full-database encryption is enabled.
         */
        @JvmStatic
        fun open(context: Context, name: String = DEFAULT_NAME): TermLinkDatabase =
            Room.databaseBuilder(context.applicationContext, TermLinkDatabase::class.java, name)
                .addMigrations(MIGRATION_1_2, MIGRATION_2_3, MIGRATION_3_4, MIGRATION_4_5)
                .build()

        @JvmStatic
        fun inMemory(context: Context): TermLinkDatabase =
            Room.inMemoryDatabaseBuilder(context.applicationContext, TermLinkDatabase::class.java)
                .allowMainThreadQueries()
                .build()

        private const val DEFAULT_NAME = "termlink-history.db"

        @JvmField
        val MIGRATION_1_2 = object : Migration(1, 2) {
            override fun migrate(database: SupportSQLiteDatabase) {
                // Legacy rows predate production E2EE metadata. An empty key
                // remains unusable until the next authoritative server sync.
                database.execSQL(
                    "ALTER TABLE devices ADD COLUMN exchangePublicKey BLOB NOT NULL DEFAULT X''",
                )
                database.execSQL(
                    "ALTER TABLE pcs ADD COLUMN keyEpoch INTEGER NOT NULL DEFAULT 1",
                )
            }
        }

        @JvmField
        val MIGRATION_2_3 = object : Migration(2, 3) {
            override fun migrate(database: SupportSQLiteDatabase) {
                database.execSQL(
                    "ALTER TABLE ack_cursors ADD COLUMN rendererGeneration INTEGER NOT NULL DEFAULT 1",
                )
            }
        }

        @JvmField
        val MIGRATION_3_4 = object : Migration(3, 4) {
            override fun migrate(database: SupportSQLiteDatabase) {
                database.execSQL(
                    """
                    CREATE TABLE IF NOT EXISTS renderer_checkpoints (
                        sessionId TEXT NOT NULL,
                        direction TEXT NOT NULL,
                        rendererGeneration INTEGER NOT NULL,
                        snapshotId TEXT NOT NULL,
                        snapshotSequence INTEGER NOT NULL,
                        ciphertextSha256 BLOB NOT NULL,
                        updatedAtEpochMillis INTEGER NOT NULL,
                        PRIMARY KEY(sessionId, direction),
                        FOREIGN KEY(snapshotId) REFERENCES snapshots(id) ON UPDATE NO ACTION ON DELETE NO ACTION,
                        FOREIGN KEY(sessionId, direction) REFERENCES ack_cursors(sessionId, direction) ON UPDATE NO ACTION ON DELETE CASCADE
                    )
                    """.trimIndent(),
                )
                database.execSQL(
                    "CREATE INDEX IF NOT EXISTS index_renderer_checkpoints_snapshotId ON renderer_checkpoints(snapshotId)",
                )
            }
        }

        @JvmField
        val MIGRATION_4_5 = object : Migration(4, 5) {
            override fun migrate(database: SupportSQLiteDatabase) {
                database.execSQL(
                    """
                    CREATE TABLE IF NOT EXISTS transcript_pages (
                        id TEXT NOT NULL,
                        sessionId TEXT NOT NULL,
                        snapshotSequence INTEGER NOT NULL,
                        requestBeforeLineId INTEGER NOT NULL,
                        firstLineId INTEGER NOT NULL,
                        lastLineId INTEGER NOT NULL,
                        objectKey TEXT NOT NULL,
                        keyId TEXT NOT NULL,
                        ciphertextSha256 BLOB NOT NULL,
                        sizeBytes INTEGER NOT NULL,
                        ciphertext BLOB NOT NULL,
                        formatName TEXT NOT NULL,
                        formatVersion INTEGER NOT NULL,
                        createdAtEpochMillis INTEGER NOT NULL,
                        expiresAtEpochMillis INTEGER,
                        PRIMARY KEY(id)
                    )
                    """.trimIndent(),
                )
                database.execSQL(
                    "CREATE UNIQUE INDEX IF NOT EXISTS index_transcript_pages_sessionId_snapshotSequence_requestBeforeLineId " +
                        "ON transcript_pages(sessionId, snapshotSequence, requestBeforeLineId)",
                )
                database.execSQL(
                    "CREATE INDEX IF NOT EXISTS index_transcript_pages_sessionId_snapshotSequence_firstLineId_lastLineId " +
                        "ON transcript_pages(sessionId, snapshotSequence, firstLineId, lastLineId)",
                )
                database.execSQL(
                    "CREATE UNIQUE INDEX IF NOT EXISTS index_transcript_pages_objectKey ON transcript_pages(objectKey)",
                )
            }
        }
    }
}
