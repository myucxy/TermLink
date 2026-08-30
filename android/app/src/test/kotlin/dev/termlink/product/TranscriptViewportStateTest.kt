package dev.termlink.product

import dev.termlink.history.EncryptedTranscriptPage
import dev.termlink.history.HistoryRepository
import dev.termlink.history.TranscriptPageMetadata
import dev.termlink.terminal.api.TerminalPaletteSnapshotV1
import dev.termlink.terminal.api.TerminalRowSnapshotV1
import dev.termlink.terminal.api.TerminalTranscriptPageV1
import java.security.MessageDigest
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class TranscriptViewportStateTest {
    @Test
    fun concurrentRequestsForTheSameAnchorAndCursorShareOneLoad() = runBlocking {
        val calls = java.util.concurrent.atomic.AtomicInteger()
        val entered = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        val active = ConcurrentHashMap<String, kotlinx.coroutines.Deferred<Unit>>()
        val request: suspend () -> Unit = {
            awaitCoalescedTranscriptLoad(CoroutineScope(coroutineContext), active, "session:80:61") {
                calls.incrementAndGet()
                entered.complete(Unit)
                release.await()
            }
        }

        val first = async { request() }
        entered.await()
        val second = async { request() }
        release.complete(Unit)
        awaitAll(first, second)

        assertEquals(1, calls.get())
        assertTrue(active.isEmpty())
    }

    @Test
    fun firstPageUsesSnapshotCursorAndFollowingPageUsesAuthenticatedCursorChain() {
        val firstObject = UUID.randomUUID()
        val secondObject = UUID.randomUUID()
        val initial = TranscriptViewportState(
            sessionId = UUID.randomUUID(),
            snapshotAnchorSequence = 80u,
            initialBeforeLineId = 61u,
        )

        val recent = mergeTranscriptPage(
            initial, 61u, null, firstObject, true, "signed-page-2",
            page(80, 41, 60, hasMore = true, next = 41),
        )
        val older = mergeTranscriptPage(
            recent, 41u, "signed-page-2", secondObject, false, null,
            page(80, 21, 40, hasMore = false, next = 0),
        )

        assertEquals(listOf(21L, 41L), older.pages.map { it.firstLineId })
        assertEquals(null, older.nextBeforeLineId)
        assertEquals(null, older.nextCursor)
        assertEquals(setOf(firstObject, secondObject), older.loadedObjectIds)
        assertFalse(older.hasMoreBefore)
        assertFalse(older.loading)
    }

    @Test
    fun mergeRejectsAnchorDriftOverlapAndStaleCursor() {
        val initial = TranscriptViewportState(
            sessionId = UUID.randomUUID(),
            snapshotAnchorSequence = 80u,
            initialBeforeLineId = 61u,
        )
        val firstObject = UUID.randomUUID()
        val recent = mergeTranscriptPage(
            initial, 61u, null, firstObject, true, "signed-page-2",
            page(80, 41, 60, hasMore = true, next = 41),
        )

        assertThrows(IllegalArgumentException::class.java) {
            mergeTranscriptPage(
                recent, 41u, "signed-page-2", UUID.randomUUID(), false, null,
                page(81, 21, 40, hasMore = false, next = 0),
            )
        }
        assertThrows(IllegalArgumentException::class.java) {
            val inconsistentServerCursor = recent.copy(nextBeforeLineId = 61u, hasMoreBefore = true)
            mergeTranscriptPage(
                inconsistentServerCursor, 61u, "signed-page-2", UUID.randomUUID(), false, null,
                page(80, 40, 50, hasMore = false, next = 0),
            )
        }
        assertThrows(IllegalArgumentException::class.java) {
            mergeTranscriptPage(
                recent, 61u, null, UUID.randomUUID(), false, null,
                page(80, 21, 40, hasMore = false, next = 0),
            )
        }
        assertTrue(recent.hasMoreBefore)
    }

    @Test
    fun expectedTranscriptPageWaitsForSnapshotFirstPublicationToCatchUp() = runBlocking {
        var fetches = 0
        val pauses = mutableListOf<Long>()

        val selected = awaitExpectedTranscriptPage(
            retryDelaysMillis = listOf(10L, 20L, 30L),
            pause = { pauses.add(it); Unit },
        ) {
            fetches += 1
            if (fetches == 3) TranscriptSelection.ReadyPage("page", false, null)
            else TranscriptSelection.Pending
        }

        assertEquals(TranscriptSelection.ReadyPage("page", false, null), selected)
        assertEquals(3, fetches)
        assertEquals(listOf(10L, 20L), pauses)
    }

    @Test
    fun defaultTranscriptPublicationPollPlanCoversTheFullReadinessWindow() {
        assertTrue(
            TRANSCRIPT_PUBLICATION_RETRY_DELAYS_MILLIS.sum() >
                TRANSCRIPT_PUBLICATION_READINESS_TIMEOUT_MILLIS,
        )
    }

    @Test
    fun pendingTranscriptStopsAtItsAbsoluteReadinessDeadline() = runBlocking {
        var fetches = 0

        val selected = awaitExpectedTranscriptPage<String>(
            retryDelaysMillis = listOf(5_000L),
            readinessTimeoutMillis = 20L,
            pause = { delay(it) },
        ) {
            fetches += 1
            TranscriptSelection.Pending
        }

        assertEquals(TranscriptSelection.Pending, selected)
        assertEquals(1, fetches)
    }

    @Test
    fun callerCancellationIsNotConvertedIntoTranscriptPending() {
        assertThrows(TimeoutCancellationException::class.java) {
            runBlocking {
                withTimeout(20L) {
                    awaitExpectedTranscriptPage<String>(
                        retryDelaysMillis = listOf(5_000L),
                        readinessTimeoutMillis = 5_000L,
                        pause = { delay(it) },
                    ) {
                        TranscriptSelection.Pending
                    }
                }
            }
        }
    }

    @Test
    fun pendingTranscriptCanRebaseOnlyToANewerDurablyRenderedSnapshot() {
        val objectId = UUID.randomUUID()
        val consumed = mergeTranscriptPage(
            viewport = TranscriptViewportState(UUID.randomUUID(), 41u, 840u),
            requestedBeforeLineId = 840u,
            requestedCursor = null,
            objectId = objectId,
            outerHasMore = true,
            outerNextCursor = "old-cursor",
            page = page(41, 640, 839, hasMore = true, next = 640),
        ).copy(error = "pending old anchor")

        assertEquals(null, rebaseTranscriptViewportToNewerAnchor(consumed, 50u, 977u, 49u))
        assertEquals(null, rebaseTranscriptViewportToNewerAnchor(consumed, 41u, 977u, 50u))
        val rebased = requireNotNull(
            rebaseTranscriptViewportToNewerAnchor(consumed, 50u, 977u, 50u),
        )

        assertEquals(50uL, rebased.snapshotAnchorSequence)
        assertEquals(977uL, rebased.initialBeforeLineId)
        assertEquals(977uL, rebased.nextBeforeLineId)
        assertEquals(null, rebased.nextCursor)
        assertTrue(rebased.pages.isEmpty())
        assertTrue(rebased.loadedObjectIds.isEmpty())
        assertTrue(rebased.consumedCursors.isEmpty())
        assertEquals(null, rebased.error)
        assertFalse(rebased.loading)
        assertEquals(consumed.viewportGeneration + 1, rebased.viewportGeneration)
    }

    @Test
    fun readyEmptyTranscriptEndsImmediatelyEvenAboveLineOne() = runBlocking {
        var fetches = 0
        val pauses = mutableListOf<Long>()

        val selected = awaitExpectedTranscriptPage<String>(
            retryDelaysMillis = listOf(10L, 20L),
            pause = { pauses.add(it); Unit },
        ) {
            fetches += 1
            TranscriptSelection.Complete
        }

        assertEquals(TranscriptSelection.Complete, selected)
        assertEquals(1, fetches)
        assertTrue(pauses.isEmpty())
    }

    @Test
    fun parserDistinguishesPendingCompleteAndLegacyPublicationResponses() {
        val pending = parseTranscriptSelectorResponse(
            response("\"ready\":false,\"items\":[],\"has_more\":false,\"next_cursor\":null"), 80u, 1001u,
        )
        val complete = parseTranscriptSelectorResponse(
            response("\"ready\":true,\"items\":[],\"has_more\":false,\"next_cursor\":null"), 80u, 1001u,
        )
        val legacyPending = parseTranscriptSelectorResponse(
            response("\"items\":[],\"has_more\":false,\"next_cursor\":null"), 80u, 1001u,
        )
        val legacyComplete = parseTranscriptSelectorResponse(
            response("\"items\":[],\"has_more\":false,\"next_cursor\":null"), 80u, 1u,
        )
        val legacyPage = parseTranscriptSelectorResponse(
            response("\"items\":[{}],\"has_more\":true,\"next_cursor\":\"signed-next\""), 80u, 1001u,
        )

        assertEquals(TranscriptSelection.Pending, pending)
        assertEquals(TranscriptSelection.Complete, complete)
        assertEquals(TranscriptSelection.Pending, legacyPending)
        assertEquals(TranscriptSelection.Complete, legacyComplete)
        assertTrue(legacyPage is TranscriptSelection.ReadyPage)
        assertEquals("signed-next", (legacyPage as TranscriptSelection.ReadyPage).nextCursor)
    }

    @Test
    fun fivePagesUseOneExplicitSelectorThenOnlyOpaqueCursorRequests() {
        val sessionId = UUID.randomUUID()
        var viewport = TranscriptViewportState(sessionId, 50u, 1001u)
        val ranges = listOf(825L to 1000L, 625L to 824L, 425L to 624L, 225L to 424L, 25L to 224L)
        val cursors = listOf("signed/825=", "signed/625=", "signed/425=", "signed/225=")
        val requests = mutableListOf<String>()

        ranges.forEachIndexed { index, (first, last) ->
            val requestedBefore = viewport.nextBeforeLineId!!
            val requestedCursor = viewport.nextCursor
            requests += transcriptHistoryRequestPath(sessionId, 50u, requestedBefore, requestedCursor)
            val hasMore = index < ranges.lastIndex
            viewport = mergeTranscriptPage(
                viewport = viewport,
                requestedBeforeLineId = requestedBefore,
                requestedCursor = requestedCursor,
                objectId = UUID.randomUUID(),
                outerHasMore = hasMore,
                outerNextCursor = cursors.getOrNull(index),
                page = page(50, first, last, hasMore, if (hasMore) first else 0),
            )
        }

        assertTrue(requests.first().contains("kind=transcript&as_of_snapshot_seq=50&before_line_id=1001"))
        requests.drop(1).forEachIndexed { index, request ->
            assertTrue(request.contains("cursor=signed%2F${listOf(825, 625, 425, 225)[index]}%3D"))
            assertFalse(request.contains("kind="))
            assertFalse(request.contains("before_line_id="))
            assertFalse(request.contains("as_of_snapshot_seq="))
        }
        assertEquals(listOf(25L, 225L, 425L, 625L, 825L), viewport.pages.map { it.firstLineId })
        assertFalse(viewport.hasMoreBefore)
    }

    @Test
    fun sixthFullPageEvictsNewestRowsButKeepsWalkingTheOlderCursor() {
        val sessionId = UUID.randomUUID()
        var viewport = TranscriptViewportState(sessionId, 50u, 1201u)
        val ranges = listOf(
            1001L to 1200L,
            801L to 1000L,
            601L to 800L,
            401L to 600L,
            201L to 400L,
            1L to 200L,
        )

        ranges.forEachIndexed { index, (first, last) ->
            val requestedBefore = requireNotNull(viewport.nextBeforeLineId)
            val requestedCursor = viewport.nextCursor
            val hasMore = index < ranges.lastIndex
            viewport = mergeTranscriptPage(
                viewport = viewport,
                requestedBeforeLineId = requestedBefore,
                requestedCursor = requestedCursor,
                objectId = UUID.randomUUID(),
                outerHasMore = hasMore,
                outerNextCursor = if (hasMore) "signed-${first}" else null,
                page = page(
                    anchor = 50,
                    first = first,
                    last = last,
                    hasMore = hasMore,
                    next = if (hasMore) first else 0,
                    rowCount = 200,
                ),
            )
        }

        assertEquals(MAX_TRANSCRIPT_VIEWPORT_ROWS, viewport.pages.sumOf { it.rows.size })
        assertEquals(listOf(1L, 201L, 401L, 601L, 801L), viewport.pages.map { it.firstLineId })
        assertEquals(null, viewport.nextBeforeLineId)
        assertEquals(null, viewport.nextCursor)
        assertFalse(viewport.hasMoreBefore)
    }

    @Test
    fun inconsistentOuterContractAndPlaintextReplayAreRejected() {
        assertThrows(IllegalArgumentException::class.java) {
            parseTranscriptSelectorResponse(
                response("\"ready\":true,\"items\":[{}],\"has_more\":true,\"next_cursor\":null"), 80u, 61u,
            )
        }
        assertThrows(IllegalArgumentException::class.java) {
            parseTranscriptSelectorResponse(
                response("\"ready\":false,\"items\":[{}],\"has_more\":false,\"next_cursor\":null"), 80u, 61u,
            )
        }

        val objectId = UUID.randomUUID()
        val initial = TranscriptViewportState(UUID.randomUUID(), 80u, 61u)
        assertThrows(IllegalArgumentException::class.java) {
            mergeTranscriptPage(
                initial, 61u, null, objectId, false, null,
                page(80, 41, 60, hasMore = true, next = 41),
            )
        }
        val recent = mergeTranscriptPage(
            initial, 61u, null, objectId, true, "next",
            page(80, 41, 60, hasMore = true, next = 41),
        )
        assertThrows(IllegalArgumentException::class.java) {
            mergeTranscriptPage(
                recent, 41u, "next", objectId, false, null,
                page(80, 21, 40, hasMore = false, next = 0),
            )
        }
        assertThrows(IllegalArgumentException::class.java) {
            mergeTranscriptPage(
                recent, 41u, "next", UUID.randomUUID(), false, null,
                page(80, 40, 50, hasMore = false, next = 0),
            )
        }
        assertThrows(IllegalArgumentException::class.java) {
            mergeTranscriptPage(
                recent, 41u, "next", UUID.randomUUID(), true, "next",
                page(80, 21, 40, hasMore = true, next = 21),
            )
        }
    }

    @Test
    fun authenticatedSelectorMetadataCanReuseCiphertextWithoutRedownload() = runBlocking {
        val ciphertext = byteArrayOf(1, 2, 3, 4)
        val expected = metadata(ciphertext = ciphertext)
        val cached = EncryptedTranscriptPage(expected.copyDigest(), ciphertext.copyOf())
        var downloads = 0

        val selected = reuseOrLoadTranscriptCiphertext(cached, expected) {
            downloads += 1
            error("cache should have been reused")
        }

        assertEquals(expected.id, selected.metadata.id)
        assertEquals(0, downloads)
        val tampered = expected.copy(id = UUID.randomUUID())
        assertThrows(IllegalArgumentException::class.java) {
            runBlocking { reuseOrLoadTranscriptCiphertext(cached, tampered) { cached } }
        }
        Unit
    }

    private fun response(fields: String) = Json.parseToJsonElement(
        """{"as_of_snapshot_seq":80,$fields}""",
    ).jsonObject

    private fun metadata(ciphertext: ByteArray): TranscriptPageMetadata {
        val sessionId = UUID.randomUUID()
        return TranscriptPageMetadata(
            id = UUID.randomUUID(),
            sessionId = sessionId,
            snapshotSequence = 80u,
            requestBeforeLineId = 61u,
            firstLineId = 41u,
            lastLineId = 60u,
            objectKey = "server/$sessionId/transcript/object",
            keyId = "transcript-v1:key:00000001",
            ciphertextSha256 = MessageDigest.getInstance("SHA-256").digest(ciphertext),
            sizeBytes = ciphertext.size.toLong(),
            formatName = HistoryRepository.TERMINAL_TRANSCRIPT_FORMAT,
            formatVersion = 1,
        )
    }

    private fun page(
        anchor: Long,
        first: Long,
        last: Long,
        hasMore: Boolean,
        next: Long,
        rowCount: Int = 0,
    ) = TerminalTranscriptPageV1(
        snapshotAnchorSequence = anchor,
        firstLineId = first,
        lastLineId = last,
        hasMoreBefore = hasMore,
        nextBeforeLineId = next,
        columns = 1,
        rows = List(rowCount) { index ->
            TerminalRowSnapshotV1(
                cells = emptyList(),
                lineId = first + index,
                sealedAtSequence = anchor,
            )
        },
        styleTable = emptyList(),
        palette = TerminalPaletteSnapshotV1(emptyList(), 0, 0, 0),
    )
}
