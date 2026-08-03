package dev.termlink.database

import java.io.File
import kotlin.test.Test
import kotlin.test.assertTrue

class DatabaseSchemaContractTest {
    @Test
    fun roomDatabaseExportsAllSensitiveIndexesAndMetadataTables() {
        val schema = File("schemas/dev.termlink.database.TermLinkDatabase/1.json")
        assertTrue(schema.isFile, "Room schema was not exported to ${schema.path}")
        val text = schema.readText()
        assertTrue("\"tableName\": \"event_chunks\"" in text)
        assertTrue("\"tableName\": \"snapshots\"" in text)
        assertTrue("\"tableName\": \"ack_cursors\"" in text)
        assertTrue("\"tableName\": \"history_preferences\"" in text)
        assertTrue("\"ciphertext\"" in text)
        assertTrue("\"ciphertextSha256\"" in text)
    }
}
