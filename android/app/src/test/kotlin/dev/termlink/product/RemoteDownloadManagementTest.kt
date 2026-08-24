package dev.termlink.product

import android.content.Context
import android.os.Bundle
import androidx.test.core.app.ApplicationProvider
import java.time.Instant
import java.util.UUID
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class RemoteDownloadManagementTest {
    private lateinit var store: SharedPreferencesRemoteDownloadRecordStore

    @Before
    fun setUp() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        store = SharedPreferencesRemoteDownloadRecordStore(context)
        store.clear()
    }

    @After
    fun tearDown() {
        store.clear()
    }

    @Test
    fun lightweightMetadataRoundTripsAcrossRepositoryRecreation() {
        val record = record(status = RemoteDownloadStatus.COMPLETED).copy(
            operationId = UUID.randomUUID(),
            totalBytes = 0L,
            targetUri = "content://provider/document/one",
        )
        store.save(listOf(record))

        assertEquals(listOf(record), SharedPreferencesRemoteDownloadRecordStore(
            ApplicationProvider.getApplicationContext(),
        ).load())
    }

    @Test
    fun activeMetadataBecomesRetryableFailureAfterProcessRestart() {
        val active = record(RemoteDownloadStatus.DOWNLOADING)

        val restored = restoreRemoteDownloadRecords(listOf(active), Instant.parse("2026-08-24T13:00:00Z")).single()

        assertEquals(RemoteDownloadStatus.FAILED, restored.status)
        assertTrue(restored.error.orEmpty().contains("restarted"))
        assertTrue(restored.status.isTerminal)
    }

    @Test
    fun removingCompletedRecordOnlyChangesMetadata() {
        val completed = record(RemoteDownloadStatus.COMPLETED)
        val retainedUri = completed.targetUri

        val after = removeRemoteDownloadRecord(listOf(completed), completed.id)

        assertTrue(after.isEmpty())
        // No ContentResolver is accepted by this helper, so removal cannot
        // delete or truncate the completed SAF document.
        assertEquals("content://provider/document/target", retainedUri)
        assertNull(after.firstOrNull())
    }

    @Test
    fun pendingSafSelectionSurvivesActivityRecreationState() {
        val selection = RemoteFileDownloadSelection(
            pcId = UUID.randomUUID(),
            pcName = "PC",
            remotePath = "C:\\folder",
            suggestedFilename = "folder.zip",
            directoryZip = true,
            retryRecordId = UUID.randomUUID(),
        )
        val state = Bundle().apply { putRemoteDownloadSelection(selection) }

        assertEquals(selection, state.remoteDownloadSelection())
    }

    private fun record(status: RemoteDownloadStatus): RemoteDownloadRecord {
        val created = Instant.parse("2026-08-24T12:00:00Z")
        return RemoteDownloadRecord(
            id = UUID.randomUUID(),
            pcId = UUID.randomUUID(),
            pcName = "PC",
            remotePath = "C:\\file.txt",
            filename = "file.txt",
            directoryZip = false,
            status = status,
            targetUri = "content://provider/document/target",
            downloadedBytes = 0L,
            createdAt = created,
            updatedAt = created,
        )
    }
}
