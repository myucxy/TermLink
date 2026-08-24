package dev.termlink.product

import android.content.ContentProvider
import android.content.ContentValues
import android.content.Context
import android.content.pm.ProviderInfo
import android.database.Cursor
import android.net.Uri
import android.os.Bundle
import android.os.ParcelFileDescriptor
import androidx.test.core.app.ApplicationProvider
import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import org.robolectric.shadows.ShadowContentResolver

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class RemoteSafCleanupTest {
    @Test
    fun providerWithoutDeleteSupportStillGetsTruncated() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val target = File(context.cacheDir, "interrupted-download.bin").apply {
            writeBytes(ByteArray(4096) { 7 })
        }
        val provider = TruncatingDocumentProvider(target)
        provider.attachInfo(
            context,
            ProviderInfo().apply {
                authority = TEST_AUTHORITY
                exported = false
                grantUriPermissions = true
            },
        )
        ShadowContentResolver.registerProviderInternal(TEST_AUTHORITY, provider)

        cleanupIncompleteRemoteDocument(
            context.contentResolver,
            Uri.parse("content://$TEST_AUTHORITY/document/download"),
        )

        assertEquals(0L, target.length())
    }

    private class TruncatingDocumentProvider(private val target: File) : ContentProvider() {
        override fun onCreate(): Boolean = true

        override fun call(method: String, arg: String?, extras: Bundle?): Bundle? {
            throw UnsupportedOperationException("delete is not supported")
        }

        override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor =
            ParcelFileDescriptor.open(target, ParcelFileDescriptor.parseMode(mode))

        override fun query(
            uri: Uri,
            projection: Array<out String>?,
            selection: String?,
            selectionArgs: Array<out String>?,
            sortOrder: String?,
        ): Cursor? = null

        override fun getType(uri: Uri): String = "application/octet-stream"
        override fun insert(uri: Uri, values: ContentValues?): Uri? = null
        override fun delete(uri: Uri, selection: String?, selectionArgs: Array<out String>?): Int = 0
        override fun update(
            uri: Uri,
            values: ContentValues?,
            selection: String?,
            selectionArgs: Array<out String>?,
        ): Int = 0
    }

    private companion object {
        const val TEST_AUTHORITY = "dev.termlink.test.downloads"
    }
}
