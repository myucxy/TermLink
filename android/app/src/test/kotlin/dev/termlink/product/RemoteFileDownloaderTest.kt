package dev.termlink.product

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.security.MessageDigest
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class RemoteFileDownloaderTest {
    @Test
    fun zeroByteDownloadIsWrittenAndVerified() = runBlocking {
        val output = ByteArrayOutputStream()
        val progress = mutableListOf<Long>()
        var verifying = false

        copyRemoteDownloadAndVerify(
            input = ByteArrayInputStream(ByteArray(0)),
            output = output,
            expectedSize = 0L,
            expectedSha256 = sha256(ByteArray(0)),
            onProgress = progress::add,
            onVerifying = { verifying = true },
        )

        assertArrayEquals(ByteArray(0), output.toByteArray())
        assertEquals(listOf(0L), progress)
        assertTrue(verifying)
    }

    @Test
    fun truncatedDownloadFailsBeforeDigestAcceptance() = runBlocking {
        val error = captureRemoteDownloadFailure {
            copyRemoteDownloadAndVerify(
                input = ByteArrayInputStream(byteArrayOf(1, 2, 3)),
                output = ByteArrayOutputStream(),
                expectedSize = 4L,
                expectedSha256 = sha256(byteArrayOf(1, 2, 3, 4)),
            )
        }
        assertTrue(error.message.orEmpty().contains("before its declared size"))
    }

    @Test
    fun incorrectSha256FailsAfterExactSize() = runBlocking {
        val bytes = byteArrayOf(1, 2, 3)
        val error = captureRemoteDownloadFailure {
            copyRemoteDownloadAndVerify(
                input = ByteArrayInputStream(bytes),
                output = ByteArrayOutputStream(),
                expectedSize = bytes.size.toLong(),
                expectedSha256 = sha256(byteArrayOf(9)),
            )
        }
        assertTrue(error.message.orEmpty().contains("SHA-256"))
    }

    private suspend fun captureRemoteDownloadFailure(block: suspend () -> Unit): RemoteFileDownloadException =
        try {
            block()
            throw AssertionError("Expected RemoteFileDownloadException")
        } catch (error: RemoteFileDownloadException) {
            error
        }

    private fun sha256(bytes: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").digest(bytes)
}
