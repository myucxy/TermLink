package dev.termlink.product

import android.content.Context
import android.net.Uri
import androidx.test.core.app.ApplicationProvider
import java.io.File
import java.security.MessageDigest
import java.util.UUID
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class FileTransferCacheTest {
    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val cache = FileTransferCache(context)

    @Test
    fun commitsOnlyAContiguousDurablePrefixAndValidatesRepeatedChunks() = runBlocking {
        val transferId = UUID.randomUUID()
        val first = ByteArray(31) { it.toByte() }
        val second = ByteArray(17) { (it + first.size).toByte() }
        val expected = first + second
        try {
            assertEquals(0L, cache.committedOffset(transferId, expected.size.toLong()))

            val gap = cache.commitChunk(transferId, expected.size.toLong(), first.size.toLong(), second)
            assertEquals(FileTransferCommit(0L, acknowledged = false), gap)

            assertEquals(
                FileTransferCommit(first.size.toLong(), acknowledged = true),
                cache.commitChunk(transferId, expected.size.toLong(), 0L, first),
            )
            assertEquals(
                FileTransferCommit(first.size.toLong(), acknowledged = true),
                cache.commitChunk(transferId, expected.size.toLong(), 0L, first),
            )
            assertRejected {
                cache.commitChunk(
                    transferId,
                    expected.size.toLong(),
                    0L,
                    first.clone().also { it[0] = 99 },
                )
            }
            assertEquals(
                FileTransferCommit(expected.size.toLong(), acknowledged = true),
                cache.commitChunk(transferId, expected.size.toLong(), first.size.toLong(), second),
            )
            assertEquals(expected.size.toLong(), cache.committedOffset(transferId, expected.size.toLong()))
            cache.verify(transferId, expected.size.toLong(), MessageDigest.getInstance("SHA-256").digest(expected))
            assertRejected {
                cache.verify(transferId, expected.size.toLong(), ByteArray(32) { 7 })
            }
            assertEquals(0L, cache.committedOffset(transferId, expected.size.toLong()))
        } finally {
            cache.cleanup(transferId)
        }
    }

    @Test
    fun resetsAnImpossibleOversizedPrefixAndSupportsZeroByteFiles() = runBlocking {
        val oversized = UUID.randomUUID()
        val empty = UUID.randomUUID()
        try {
            cache.partFile(oversized).writeBytes(ByteArray(9))
            assertEquals(0L, cache.committedOffset(oversized, 8L))
            assertEquals(0L, cache.partFile(oversized).length())

            assertEquals(0L, cache.committedOffset(empty, 0L))
            assertTrue(cache.partFile(empty).isFile)
            cache.verify(
                empty,
                0L,
                MessageDigest.getInstance("SHA-256").digest(ByteArray(0)),
            )
        } finally {
            cache.cleanup(oversized)
            cache.cleanup(empty)
        }
    }

    @Test
    fun committedDocumentMarkerSurvivesClientRecreationAndIsBoundToDestinationAndHash() = runBlocking {
        val transferId = UUID.randomUUID()
        val destination = Uri.parse("content://dev.termlink.test/document/result")
        val hash = MessageDigest.getInstance("SHA-256").digest("payload".encodeToByteArray())
        try {
            assertFalse(cache.isDocumentCommitted(transferId, destination, 7L, hash))
            cache.markDocumentCommitted(transferId, destination, 7L, hash)

            val recreated = FileTransferCache(context)
            assertTrue(recreated.isDocumentCommitted(transferId, destination, 7L, hash))
            assertFalse(recreated.isDocumentCommitted(transferId, Uri.parse("content://other/result"), 7L, hash))
            assertFalse(recreated.isDocumentCommitted(transferId, destination, 8L, hash))
            assertFalse(recreated.isDocumentCommitted(transferId, destination, 7L, ByteArray(32)))

            val taskDirectory = cache.partFile(transferId).parentFile!!
            assertTrue(taskDirectory.canonicalFile.parentFile == cache.root)
            assertTrue(taskDirectory.listFiles().orEmpty().all {
                it.parentFile?.canonicalFile == taskDirectory.canonicalFile
            })
            cache.cleanup(transferId)
            assertFalse(taskDirectory.exists())
        } finally {
            cache.cleanup(transferId)
        }
    }

    @Test
    fun cleanupRefusesUnexpectedEntriesInsteadOfRecursivelyDeletingThem() {
        val transferId = UUID.randomUUID()
        val taskDirectory = cache.partFile(transferId).parentFile!!
        val unexpected = File(taskDirectory, "unowned.data").apply { writeText("keep") }
        try {
            assertRejectedBlocking { cache.cleanup(transferId) }
            assertTrue(unexpected.exists())
        } finally {
            unexpected.delete()
            cache.cleanup(transferId)
        }
    }

    private suspend fun assertRejected(block: suspend () -> Unit) {
        try {
            block()
            fail("Invalid file-transfer cache operation was accepted")
        } catch (_: IllegalArgumentException) {
            // Expected fail-closed result.
        }
    }

    private fun assertRejectedBlocking(block: () -> Unit) {
        try {
            block()
            fail("Unsafe file-transfer cache cleanup was accepted")
        } catch (_: IllegalArgumentException) {
            // Expected fail-closed result.
        }
    }
}
