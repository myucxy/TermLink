package dev.termlink.product

import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class ProductUpdateDownloaderTest {
    @get:Rule
    val temporaryFolder = TemporaryFolder()

    @Test
    fun integrityValidationAcceptsExactSizeAndSha256() {
        val apk = temporaryFolder.newFile("update.apk")
        apk.writeBytes("verified update".toByteArray())

        validateUpdateFileIntegrity(apk, apk.length(), sha256Hex(apk).uppercase())
    }

    @Test
    fun integrityValidationRejectsWrongSizeAndDigest() {
        val apk = temporaryFolder.newFile("update.apk")
        apk.writeBytes("not trusted".toByteArray())

        assertThrows(ProductUpdateDownloadException::class.java) {
            validateUpdateFileIntegrity(apk, apk.length() + 1L, sha256Hex(apk))
        }
        assertThrows(ProductUpdateDownloadException::class.java) {
            validateUpdateFileIntegrity(apk, apk.length(), "0".repeat(64))
        }
    }

    @Test
    fun signerValidationRequiresTheExactInstalledSignerSet() {
        validateProductSignerDigests(setOf("signer-a", "signer-b"), setOf("signer-b", "signer-a"))

        val mismatch = assertThrows(ProductUpdateDownloadException::class.java) {
            validateProductSignerDigests(setOf("signer-a"), setOf("signer-b"))
        }
        assertEquals(
            "APK signature does not match this app / APK 签名与当前应用不一致",
            mismatch.message,
        )
        assertThrows(ProductUpdateDownloadException::class.java) {
            validateProductSignerDigests(emptySet(), setOf("signer-a"))
        }
        assertThrows(ProductUpdateDownloadException::class.java) {
            validateProductSignerDigests(setOf("signer-a"), emptySet())
        }
    }
}
