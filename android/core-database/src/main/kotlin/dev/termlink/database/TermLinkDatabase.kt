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
        AckCursorEntity::class,
        HistoryPreferenceEntity::class,
    ],
    version = 2,
    exportSchema = true,
)
abstract class TermLinkDatabase : RoomDatabase() {
    abstract fun devices(): DeviceDao
    abstract fun pcs(): PcDao
    abstract fun sessions(): SessionDao
    abstract fun eventChunks(): EventChunkDao
    abstract fun snapshots(): SnapshotDao
    abstract fun ackCursors(): AckCursorDao
    abstract fun preferences(): HistoryPreferenceDao

    // Conventional DAO names keep wiring readable at call sites while the short
    // names above remain convenient for generated Room code.
    fun deviceDao(): DeviceDao = devices()
    fun pcDao(): PcDao = pcs()
    fun sessionDao(): SessionDao = sessions()
    fun eventChunkDao(): EventChunkDao = eventChunks()
    fun snapshotDao(): SnapshotDao = snapshots()
    fun ackCursorDao(): AckCursorDao = ackCursors()
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
                .addMigrations(MIGRATION_1_2)
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
    }
}
