package dev.termlink.product

import dev.termlink.network.production.TermLinkApiException
import java.io.IOException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test

class SessionOpenObservationTest {
    @Test fun earlyOpenFailuresReachPersistentObserverWithoutLosingTheirSafeCategory() = runBlocking {
        for (code in listOf(SessionOpenFailureCode.HOST_HISTORY_REQUIRED, SessionOpenFailureCode.PC_UNAVAILABLE,
            SessionOpenFailureCode.PC_NOT_TRUSTED, SessionOpenFailureCode.SESSION_OFFLINE)) {
            val original = sessionOpenException(code)
            val published = mutableListOf<SessionOpenFailure>()
            val thrown = try {
                withSessionOpenFailureObservation(observeFailure = { published += it }) { throw original }
            } catch (error: SessionOpenException) { error }
            assertSame(original, thrown)
            assertEquals(listOf(original.failure), published)
        }
    }

    @Test fun connectionAndPermissionFailuresRemainDistinctAndNeverExposeRawExceptionText() = runBlocking {
        for ((error, expected) in listOf(
            IOException("private-directory/credential-looking-text") to SessionOpenFailureCode.SERVICE_UNREACHABLE,
            TermLinkApiException(403, "ACCESS_DENIED", "private response") to SessionOpenFailureCode.ACCESS_DENIED,
            IllegalStateException("an unknown local condition") to SessionOpenFailureCode.UNKNOWN,
        )) {
            val published = mutableListOf<SessionOpenFailure>()
            val thrown = try {
                withSessionOpenFailureObservation(observeFailure = { published += it }) { throw error }
            } catch (failure: SessionOpenException) { failure }
            assertEquals(expected, thrown.failure.code)
            assertEquals(listOf(thrown.failure), published)
            assertFalse(published.toString().contains("private"))
            assertFalse(published.toString().contains("unknown local condition"))
        }
    }

    @Test fun cancellationIsNotPublishedAndSuccessfulOpensDoNotInventAFailure() = runBlocking {
        val published = mutableListOf<SessionOpenFailure>()
        val value = withSessionOpenFailureObservation(observeFailure = { published += it }) { "renderer ready" }
        assertEquals("renderer ready", value)
        val original = CancellationException("view closed")
        try {
            withSessionOpenFailureObservation(observeFailure = { published += it }) { throw original }
            fail("Cancellation was swallowed")
        } catch (cancelled: CancellationException) { assertSame(original, cancelled) }
        launch {
            cancel()
            try {
                withSessionOpenFailureObservation(observeFailure = { published += it }) { throw IOException("late result") }
                fail("A cancelled context published a late result")
            } catch (_: CancellationException) { }
        }.join()
        assertTrue(published.isEmpty())
    }

    @Test fun retiredAuthenticationCannotPublishAnOlderSessionFailure() = runBlocking {
        val fence = AuthenticationLifecycleFence()
        val generation = fence.capture()
        val published = mutableListOf<SessionOpenFailure>()
        try {
            withSessionOpenFailureObservation(observeFailure = { failure ->
                fence.runIfCurrent(generation) { published += failure }
            }) {
                fence.invalidate()
                throw sessionOpenException(SessionOpenFailureCode.HOST_HISTORY_REQUIRED)
            }
            fail("Open failure was swallowed")
        } catch (_: SessionOpenException) { }
        assertTrue("A previous account must not replace the successor's UI state", published.isEmpty())
    }
}
