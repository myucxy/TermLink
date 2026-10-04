package dev.termlink.product

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import java.io.IOException
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.*
import okhttp3.OkHttpClient
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class ProductUpdateCancellationTest {
    @Test fun cancellationStopsBlockedUpdateRequest(): Unit = runBlocking {
        val entered = CountDownLatch(1)
        val cancelled = CountDownLatch(1)
        val http = OkHttpClient.Builder().addInterceptor { chain ->
            entered.countDown()
            val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5)
            while (!chain.call().isCanceled() && System.nanoTime() < deadline) Thread.sleep(5)
            if (chain.call().isCanceled()) cancelled.countDown()
            throw IOException("fixture request stopped")
        }.build()
        val downloader = ProductUpdateDownloader(ApplicationProvider.getApplicationContext<Context>(), http, 127)
        val job = launch(Dispatchers.Default) {
            downloader.download(ProductUpdateStatus(ProductUpdateDecision.OPTIONAL, buildNumber = 65432,
                downloadUrl = "https://update.example.test/app.apk", artifactSizeBytes = 10, artifactSha256 = "a".repeat(64)), { _, _ -> }, {})
        }
        assertTrue(entered.await(3, TimeUnit.SECONDS))
        job.cancel(); downloader.cancel()
        withTimeout(2_000) { job.join() }
        assertEquals(0L, cancelled.count)
        assertTrue(job.isCancelled)
    }
}
