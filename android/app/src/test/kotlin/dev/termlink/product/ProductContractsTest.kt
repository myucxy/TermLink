package dev.termlink.product

import dev.termlink.model.DeviceTrustState
import dev.termlink.model.SessionState
import dev.termlink.model.SessionSummary
import dev.termlink.network.production.AccessTokenProvider
import dev.termlink.network.production.TermLinkApiException
import dev.termlink.protocol.realtime.v1.SessionUpsertPayload
import dev.termlink.session.InputSubmission
import dev.termlink.session.RemoteLifecycle
import dev.termlink.session.RemoteSessionEvent
import dev.termlink.session.RemoteSessionState
import java.io.IOException
import java.net.URI
import java.time.Instant
import java.util.Base64
import java.util.UUID
import java.util.concurrent.CompletableFuture
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put
import kotlinx.serialization.json.putJsonArray
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotSame
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import okhttp3.OkHttpClient

class ProductContractsTest {
    @Test
    fun directShadowUsesAnIndependentHttpResourceDomainFromRelay() {
        val relay = OkHttpClient()
        val direct = createIsolatedDirectShadowHttpClient(relay)
        try {
            assertNotSame(relay.dispatcher, direct.dispatcher)
            assertNotSame(relay.connectionPool, direct.connectionPool)
        } finally {
            relay.dispatcher.executorService.shutdownNow()
            relay.connectionPool.evictAll()
            direct.dispatcher.executorService.shutdownNow()
            direct.connectionPool.evictAll()
        }
    }

    @Test
    fun realtimeForegroundLifecycleKeepsEarliestBackgroundAndDeduplicatesGeneration() {
        val initial = observeRealtimeAppForeground(RealtimeAppLifecycleState(), 1, 1_000)
        assertTrue(initial.accepted)
        assertEquals(null, initial.backgroundDurationMillis)

        val firstStop = recordRealtimeAppBackground(initial.state, 2_000)
        val duplicateStop = recordRealtimeAppBackground(firstStop, 5_000)
        assertEquals(2_000L, duplicateStop.earliestBackgroundAtMillis)

        val resumed = observeRealtimeAppForeground(duplicateStop, 2, 18_250)
        assertTrue(resumed.accepted)
        assertEquals(16_250L, resumed.backgroundDurationMillis)
        assertEquals(null, resumed.state.earliestBackgroundAtMillis)

        val duplicateForeground = observeRealtimeAppForeground(resumed.state, 2, 19_000)
        assertFalse(duplicateForeground.accepted)
        assertEquals(resumed.state, duplicateForeground.state)
    }

    @Test
    fun delayedBackgroundCannotMoveLifecycleBehindLatestForeground() {
        val foreground = observeRealtimeAppForeground(RealtimeAppLifecycleState(), 4, 10_000).state
        assertEquals(foreground, recordRealtimeAppBackground(foreground, 9_999))
    }

    @Test
    fun nativeCheckpointCanResumeBehindAckOnlyByReplayingItsSuffix() {
        assertTrue(snapshotCanResumeThroughDurableAcknowledgement(42uL, 42uL))
        assertTrue(snapshotCanResumeThroughDurableAcknowledgement(41uL, 42uL))
        assertFalse(snapshotCanResumeThroughDurableAcknowledgement(43uL, 42uL))
        assertTrue(snapshotMatchesDurableAcknowledgement(42uL, 42uL))
        assertFalse(snapshotMatchesDurableAcknowledgement(41uL, 42uL))
    }

    @Test
    fun snapshotSuffixMustReachOriginalAckBeforeRealtimeSubscribe() {
        assertFalse(snapshotSuffixRestoreReady(8_999uL, 9_000uL, 10_000uL))
        assertTrue(snapshotSuffixRestoreReady(9_000uL, 9_000uL, 10_000uL))
        assertTrue(snapshotSuffixRestoreReady(9_001uL, 9_000uL, 10_000uL))
        assertFalse(snapshotSuffixRestoreReady(6_000uL, 9_000uL, 10_000uL))
        assertFalse(snapshotSuffixRestoreReady(9_000uL, 9_000uL, 20_000uL))
        assertTrue(snapshotSuffixRestoreReady(15_904uL, 9_000uL, 20_000uL))
        assertFalse(snapshotSuffixRestoreReady(15_903uL, 9_000uL, 20_000uL))
        assertTrue(snapshotSuffixRestoreIsBounded(5_904uL, 9_000uL, 10_000uL))
        assertFalse(snapshotSuffixRestoreIsBounded(5_903uL, 9_000uL, 10_000uL))
        assertFalse(snapshotSuffixRestoreIsBounded(9_001uL, 9_000uL, 10_000uL))
        assertFalse(snapshotSuffixRestoreIsBounded(9_000uL, 10_001uL, 10_000uL))
        assertTrue(fullPrefixReplayCanPreserveDurableAcknowledgement(0uL))
        assertFalse(fullPrefixReplayCanPreserveDurableAcknowledgement(1uL))
    }

    @Test
    fun durableNonOutputEventExtendsTheHostHistoryRestoreTarget() {
        assertEquals(46uL, restorableSessionEventTail(45uL, 46uL))
        assertEquals(46uL, restorableSessionEventTail(46uL, 45uL))
    }

    @Test
    fun authoritativeTailLookupIncludesEndedSessions() {
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        assertEquals(
            "/v1/pcs/$pcId/sessions?include_ended=true",
            authoritativeSessionCatalogPath(pcId),
        )
    }

    @Test
    fun snapshotSuffixBoundIsRecheckedAgainstMovingAuthorityAndReplayTargets() {
        assertTrue(snapshotSuffixRestoreIsBounded(100uL, 200uL, 4_100uL))
        assertFalse(snapshotSuffixRestoreIsBounded(100uL, 200uL, 6_000uL))
        assertTrue(snapshotSuffixReplayTargetIsBounded(100uL, 4_196uL))
        assertFalse(snapshotSuffixReplayTargetIsBounded(100uL, 4_197uL))
    }

    @Test
    fun realtimeNetworkCapabilityFallbackDeduplicatesAndReconnectsAfterLoss() {
        assertEquals(
            RealtimeNetworkObservationTransition.INITIAL,
            realtimeNetworkObservationTransition(false, null, "network-1"),
        )
        assertEquals(
            RealtimeNetworkObservationTransition.UNCHANGED,
            realtimeNetworkObservationTransition(true, "network-1", "network-1"),
        )
        assertEquals(
            RealtimeNetworkObservationTransition.RECONNECTED,
            realtimeNetworkObservationTransition(true, null, "network-1"),
        )
        assertEquals(
            RealtimeNetworkObservationTransition.RECONNECTED,
            realtimeNetworkObservationTransition(true, "network-1", "network-2"),
        )
    }

    @Test
    fun encryptedHistorySessionUpsertPreservesTitlePresenceAndLegacyLifecycle() {
        val title = productSessionUpsertEvent(
            41,
            SessionUpsertPayload.newBuilder()
                .setLifecycle("CHANGED")
                .setTitle("Codex — 构建")
                .build(),
        )
        assertTrue(title is RemoteSessionEvent.Title)
        assertEquals("Codex — 构建", (title as RemoteSessionEvent.Title).value)

        val cleared = productSessionUpsertEvent(
            42,
            SessionUpsertPayload.newBuilder()
                .setLifecycle("CHANGED")
                .setTitle("")
                .build(),
        )
        assertTrue(cleared is RemoteSessionEvent.Title)
        assertEquals("", (cleared as RemoteSessionEvent.Title).value)

        val legacy = productSessionUpsertEvent(
            43,
            SessionUpsertPayload.newBuilder().setLifecycle("INTERRUPTED").build(),
        )
        assertTrue(legacy is RemoteSessionEvent.Lifecycle)
        assertEquals(RemoteLifecycle.INTERRUPTED, (legacy as RemoteSessionEvent.Lifecycle).state)
    }

    @Test
    fun sessionHistoryCatalogUsesOneBoundedChunkPageAndRejectsSelectorDrift() {
        val sessionId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        assertEquals(
            "/v1/sessions/$sessionId/history?kind=chunk&limit=50",
            sessionChunkHistoryRequestPath(sessionId),
        )
        assertEquals(
            "/v1/sessions/$sessionId/history?cursor=page%2B2%2F%3D&limit=50",
            sessionChunkHistoryRequestPath(sessionId, "page+2/="),
        )
        assertThrows(IllegalArgumentException::class.java) {
            sessionChunkHistoryRequestPath(sessionId, " ")
        }

        val page = buildJsonObject {
            putJsonArray("items") {
                repeat(SESSION_CHUNK_HISTORY_PAGE_LIMIT) {
                    add(buildJsonObject { put("kind", "chunk") })
                }
            }
        }
        assertEquals(SESSION_CHUNK_HISTORY_PAGE_LIMIT, boundedChunkHistoryItems(page).size)

        val wrongKind = buildJsonObject {
            putJsonArray("items") { add(buildJsonObject { put("kind", "snapshot") }) }
        }
        assertThrows(IllegalArgumentException::class.java) { boundedChunkHistoryItems(wrongKind) }

        val oversized = buildJsonObject {
            putJsonArray("items") {
                repeat(SESSION_CHUNK_HISTORY_PAGE_LIMIT + 1) {
                    add(buildJsonObject { put("kind", "chunk") })
                }
            }
        }
        assertThrows(IllegalArgumentException::class.java) { boundedChunkHistoryItems(oversized) }
    }

    @Test
    fun streamedHistoryCachesMoreThanOneHundredThousandEventsAcrossBoundedPages() = runBlocking {
        val totalEvents = 100_123uL
        val chunkEvents = 127uL
        val chunks = buildList {
            var start = 1uL
            while (start <= totalEvents) {
                val end = minOf(totalEvents, start + chunkEvents - 1uL)
                add(HistorySequenceRange(start, end))
                start = end + 1uL
            }
        }.asReversed()
        var largestPage = 0
        var pageLoads = 0
        val cached = mutableListOf<HistorySequenceRange>()

        fun page(offset: Int): StreamedHistoryCatalogPage<HistorySequenceRange> {
            val items = chunks.drop(offset).take(SESSION_CHUNK_HISTORY_PAGE_LIMIT)
            largestPage = maxOf(largestPage, items.size)
            val nextOffset = offset + items.size
            return StreamedHistoryCatalogPage(
                items = items,
                hasMore = nextOffset < chunks.size,
                nextCursor = nextOffset.toString().takeIf { nextOffset < chunks.size },
            )
        }

        val restoredThrough = cacheContiguousHistoryRange(
            minimumTarget = totalEvents,
            stopAfterSequence = 0uL,
            firstPage = page(0),
            loadNextPage = { cursor ->
                pageLoads += 1
                page(cursor.toInt())
            },
            sequenceRange = { it },
            cacheCandidate = { cached += it },
        )

        assertEquals(totalEvents, restoredThrough)
        assertEquals(chunks, cached)
        assertEquals(totalEvents, cached.sumOf { it.endInclusive - it.start + 1uL })
        assertTrue(pageLoads > 1)
        assertTrue(largestPage <= SESSION_CHUNK_HISTORY_PAGE_LIMIT)
    }

    @Test
    fun streamedHistoryCatalogProgressHasNoTotalOrStallTimeout() = runBlocking {
        val ends = ArrayDeque(listOf(100uL, 200uL, 300uL, 400uL, 500uL))
        var elapsed = 0L

        val ready = awaitStreamedHistoryCatalogHead(
            minimumTarget = 500uL,
            loadPage = { ends.removeFirst() },
            newestEndSequence = { it },
            pollMillis = 1L,
            pause = { elapsed += 10_000L },
        )

        assertEquals(500uL, ready)
        assertEquals(40_000L, elapsed)
    }

    @Test
    fun streamedHistoryCatalogWaitsPastFormerStallLimitUntilCancelled() = runBlocking {
        var elapsed = 0L
        val cancellation = CancellationException("streamed history wait cancelled")

        val failure = try {
            awaitStreamedHistoryCatalogHead(
                minimumTarget = 500uL,
                loadPage = { 100uL },
                newestEndSequence = { it },
                pollMillis = 1L,
                pause = {
                    elapsed += 10_000L
                    if (elapsed >= 60_000L) throw cancellation
                },
            )
            error("Expected streamed history readiness cancellation")
        } catch (error: CancellationException) {
            error
        }

        assertEquals(cancellation, failure)
        assertEquals(60_000L, elapsed)
    }

    @Test
    fun streamedHistoryCanRestoreAvailablePrefixBeforeAuthoritativeTail() = runBlocking {
        val published = listOf(
            HistorySequenceRange(51uL, 100uL),
            HistorySequenceRange(1uL, 50uL),
        )
        val cached = mutableListOf<HistorySequenceRange>()
        val head = awaitStreamedHistoryCatalogHead(
            minimumTarget = 1uL,
            loadPage = { published },
            newestEndSequence = { it.firstOrNull()?.endInclusive },
            pollMillis = 0L,
        )

        val restoredThrough = cacheContiguousHistoryRange(
            minimumTarget = 1uL,
            stopAfterSequence = 0uL,
            firstPage = StreamedHistoryCatalogPage(
                items = head,
                hasMore = false,
                nextCursor = null,
            ),
            loadNextPage = { error("available prefix fits one bounded page") },
            sequenceRange = { it },
            cacheCandidate = { cached += it },
        )

        assertEquals(100uL, restoredThrough)
        assertEquals(published, cached)
    }

    @Test
    fun streamedHistoryRejectsAGapWithoutCachingPastIt() = runBlocking {
        val cached = mutableListOf<HistorySequenceRange>()
        val error = assertThrows(SessionOpenException::class.java) {
            runBlocking {
                cacheContiguousHistoryRange(
                    minimumTarget = 100uL,
                    stopAfterSequence = 0uL,
                    firstPage = StreamedHistoryCatalogPage(
                        items = listOf(
                            HistorySequenceRange(91uL, 100uL),
                            HistorySequenceRange(71uL, 80uL),
                        ),
                        hasMore = false,
                        nextCursor = null,
                    ),
                    loadNextPage = { error("a terminal page must not load another page") },
                    sequenceRange = { it },
                    cacheCandidate = { cached += it },
                )
            }
        }

        assertEquals(SessionOpenFailureCode.HISTORY_GAP, error.failure.code)
        assertEquals(listOf(HistorySequenceRange(91uL, 100uL)), cached)
    }

    @Test
    fun snapshotSuffixCachesExactlyFromAnchorThroughDurableAck() = runBlocking {
        val cached = mutableListOf<HistorySequenceRange>()

        val restoredThrough = cacheContiguousHistoryRange(
            minimumTarget = 600uL,
            stopAfterSequence = 500uL,
            firstPage = StreamedHistoryCatalogPage(
                items = listOf(
                    HistorySequenceRange(576uL, 600uL),
                    HistorySequenceRange(551uL, 575uL),
                    HistorySequenceRange(526uL, 550uL),
                    HistorySequenceRange(501uL, 525uL),
                ),
                hasMore = false,
                nextCursor = null,
            ),
            loadNextPage = { error("snapshot suffix fits one page") },
            sequenceRange = { it },
            cacheCandidate = { cached += it },
        )

        assertEquals(600uL, restoredThrough)
        assertEquals(
            listOf(
                HistorySequenceRange(576uL, 600uL),
                HistorySequenceRange(551uL, 575uL),
                HistorySequenceRange(526uL, 550uL),
                HistorySequenceRange(501uL, 525uL),
            ),
            cached,
        )
        assertTrue(snapshotSuffixRestoreReady(restoredThrough, 600uL, 700uL))
    }

    @Test
    fun snapshotSuffixAcceptsAChunkStraddlingItsAnchor() = runBlocking {
        val cached = mutableListOf<HistorySequenceRange>()

        val restoredThrough = cacheContiguousHistoryRange(
            minimumTarget = 1629uL,
            stopAfterSequence = 7uL,
            firstPage = StreamedHistoryCatalogPage(
                items = listOf(
                    HistorySequenceRange(1626uL, 1629uL),
                    HistorySequenceRange(1187uL, 1625uL),
                    HistorySequenceRange(675uL, 1186uL),
                    HistorySequenceRange(163uL, 674uL),
                    HistorySequenceRange(10uL, 162uL),
                    HistorySequenceRange(9uL, 9uL),
                    HistorySequenceRange(1uL, 8uL),
                ),
                hasMore = false,
                nextCursor = null,
            ),
            loadNextPage = { error("complete suffix fits one page") },
            sequenceRange = { it },
            cacheCandidate = { cached += it },
        )

        assertEquals(1629uL, restoredThrough)
        assertEquals(HistorySequenceRange(1uL, 8uL), cached.last())
        assertEquals(7, cached.size)
    }

    @Test
    fun snapshotSuffixRejectsTwoChunksStraddlingItsAnchorOnOnePage() {
        val cached = mutableListOf<HistorySequenceRange>()
        val failure = assertThrows(SessionOpenException::class.java) {
            runBlocking {
                cacheContiguousHistoryRange(
                    minimumTarget = 8uL,
                    stopAfterSequence = 7uL,
                    firstPage = StreamedHistoryCatalogPage(
                        items = listOf(
                            HistorySequenceRange(5uL, 8uL),
                            HistorySequenceRange(1uL, 8uL),
                        ),
                        hasMore = false,
                        nextCursor = null,
                    ),
                    loadNextPage = { error("a terminal page must not load another page") },
                    sequenceRange = { it },
                    cacheCandidate = { cached += it },
                )
            }
        }

        assertEquals(SessionOpenFailureCode.HISTORY_GAP, failure.failure.code)
        assertEquals(listOf(HistorySequenceRange(5uL, 8uL)), cached)
    }

    @Test
    fun snapshotSuffixRejectsASecondAnchorOverlapOnTheNextPage() {
        val cached = mutableListOf<HistorySequenceRange>()
        val requestedCursors = mutableListOf<String>()
        val failure = assertThrows(SessionOpenException::class.java) {
            runBlocking {
                cacheContiguousHistoryRange(
                    minimumTarget = 8uL,
                    stopAfterSequence = 7uL,
                    firstPage = StreamedHistoryCatalogPage(
                        items = listOf(HistorySequenceRange(5uL, 8uL)),
                        hasMore = true,
                        nextCursor = "anchor-check",
                    ),
                    loadNextPage = { cursor ->
                        requestedCursors += cursor
                        StreamedHistoryCatalogPage(
                            items = listOf(HistorySequenceRange(1uL, 8uL)),
                            hasMore = false,
                            nextCursor = null,
                        )
                    },
                    sequenceRange = { it },
                    cacheCandidate = { cached += it },
                )
            }
        }

        assertEquals(SessionOpenFailureCode.HISTORY_GAP, failure.failure.code)
        assertEquals(listOf("anchor-check"), requestedCursors)
        assertEquals(listOf(HistorySequenceRange(5uL, 8uL)), cached)
    }

    @Test
    fun snapshotSuffixAcceptsAnOlderSentinelOnTheNextPageWithoutCachingIt() = runBlocking {
        val cached = mutableListOf<HistorySequenceRange>()
        val requestedCursors = mutableListOf<String>()

        val restoredThrough = cacheContiguousHistoryRange(
            minimumTarget = 8uL,
            stopAfterSequence = 7uL,
            firstPage = StreamedHistoryCatalogPage(
                items = listOf(HistorySequenceRange(5uL, 8uL)),
                hasMore = true,
                nextCursor = "anchor-check",
            ),
            loadNextPage = { cursor ->
                requestedCursors += cursor
                StreamedHistoryCatalogPage(
                    items = listOf(HistorySequenceRange(1uL, 7uL)),
                    hasMore = false,
                    nextCursor = null,
                )
            },
            sequenceRange = { it },
            cacheCandidate = { cached += it },
        )

        assertEquals(8uL, restoredThrough)
        assertEquals(listOf("anchor-check"), requestedCursors)
        assertEquals(listOf(HistorySequenceRange(5uL, 8uL)), cached)
    }

    @Test
    fun snapshotSuffixValidatesTheBoundaryChunkButAppliesOnlyNewEvents() = runBlocking {
        val applied = mutableListOf<ULong>()

        val localInputEpoch = applyContiguousHistoryChunkSuffix(
            startSequence = 1uL,
            endSequence = 8uL,
            nextSequence = 8uL,
            events = (1uL..8uL).toList(),
            eventSequence = { it },
            initialLocalInputEpoch = 3L,
            applyEvent = { sequence ->
                applied += sequence
                11L
            },
        )

        assertEquals(listOf(8uL), applied)
        assertEquals(11L, localInputEpoch)
    }

    @Test
    fun snapshotSuffixRejectsACorruptSkippedPrefixBeforeApplyingAnything() {
        val applied = mutableListOf<ULong>()
        val failure = assertThrows(SessionOpenException::class.java) {
            runBlocking {
                applyContiguousHistoryChunkSuffix(
                    startSequence = 1uL,
                    endSequence = 8uL,
                    nextSequence = 8uL,
                    events = listOf(1uL, 2uL, 4uL, 5uL, 6uL, 7uL, 8uL),
                    eventSequence = { it },
                    initialLocalInputEpoch = 3L,
                    applyEvent = { sequence ->
                        applied += sequence
                        11L
                    },
                )
            }
        }

        assertEquals(SessionOpenFailureCode.HISTORY_GAP, failure.failure.code)
        assertTrue(applied.isEmpty())
    }

    @Test
    fun snapshotSuffixTargetCeilingRejectsBeforeAnyCandidateIsCached() {
        val cached = mutableListOf<HistorySequenceRange>()
        val error = assertThrows(IllegalStateException::class.java) {
            runBlocking {
                cacheContiguousHistoryRange(
                    minimumTarget = 200uL,
                    stopAfterSequence = 100uL,
                    firstPage = StreamedHistoryCatalogPage(
                        items = listOf(HistorySequenceRange(4_176uL, 4_200uL)),
                        hasMore = true,
                        nextCursor = "page-2",
                    ),
                    loadNextPage = { error("target admission must stop catalog paging") },
                    sequenceRange = { it },
                    validateTargetBeforeCaching = { target ->
                        check(target <= 4_196uL) { "snapshot suffix exceeds its hard limit" }
                    },
                    cacheCandidate = { cached += it },
                )
            }
        }

        assertEquals("snapshot suffix exceeds its hard limit", error.message)
        assertTrue(cached.isEmpty())
    }

    @Test
    fun snapshotSuffixGapFailsBeforeDurableAckCanBeAdvertised() {
        val cached = mutableListOf<HistorySequenceRange>()
        val error = assertThrows(SessionOpenException::class.java) {
            runBlocking {
                cacheContiguousHistoryRange(
                    minimumTarget = 600uL,
                    stopAfterSequence = 500uL,
                    firstPage = StreamedHistoryCatalogPage(
                        items = listOf(
                            HistorySequenceRange(576uL, 600uL),
                            HistorySequenceRange(526uL, 550uL),
                            HistorySequenceRange(501uL, 525uL),
                        ),
                        hasMore = false,
                        nextCursor = null,
                    ),
                    loadNextPage = { error("a terminal page must not load another page") },
                    sequenceRange = { it },
                    cacheCandidate = { cached += it },
                )
            }
        }

        assertEquals(SessionOpenFailureCode.HISTORY_GAP, error.failure.code)
        assertEquals(listOf(HistorySequenceRange(576uL, 600uL)), cached)
        assertFalse(snapshotSuffixRestoreReady(550uL, 600uL, 700uL))
    }

    @Test
    fun streamedHistoryPropagatesCancellationBeforeTheRangeCompletes() {
        val cancellation = CancellationException("streamed history cancelled")
        var cached = 0
        val error = assertThrows(CancellationException::class.java) {
            runBlocking {
                cacheContiguousHistoryRange(
                    minimumTarget = 100uL,
                    stopAfterSequence = 0uL,
                    firstPage = StreamedHistoryCatalogPage(
                        items = listOf(
                            HistorySequenceRange(91uL, 100uL),
                            HistorySequenceRange(81uL, 90uL),
                        ),
                        hasMore = true,
                        nextCursor = "page-2",
                    ),
                    loadNextPage = { error("cancellation must stop paging") },
                    sequenceRange = { it },
                    cacheCandidate = {
                        cached += 1
                        if (cached == 2) throw cancellation
                    },
                )
            }
        }

        assertEquals("streamed history cancelled", error.message)
        assertEquals(2, cached)
    }

    @Test
    fun commandSubmissionIsOneOrderedInputEndingInCarriageReturn() {
        assertEquals("ni TLFINAL123\r", commandInputBytes("ni TLFINAL123").decodeToString())
        assertEquals(null, commandSubmissionFailure(InputSubmission.Sent(UUID.randomUUID())))
        assertEquals(
            "Request control before sending a command",
            commandSubmissionFailure(InputSubmission.RejectedNoLease),
        )
        assertThrows(IllegalArgumentException::class.java) { commandInputBytes("") }
    }

    @Test
    fun selectedRunningSessionRequestsControlInitiallyAndAfterRealtimeReconnect() {
        assertEquals(
            SessionControlRequestTrigger.INITIAL_OPEN,
            sessionControlRequestTrigger(
                foreground = true,
                online = true,
                remoteState = null,
            ),
        )
        assertEquals(
            SessionControlRequestTrigger.NONE,
            sessionControlRequestTrigger(
                foreground = true,
                online = true,
                remoteState = RemoteSessionState.Reconnecting(lastAckedSequence = 40),
            ),
        )
        assertEquals(
            SessionControlRequestTrigger.REALTIME_LIVE,
            sessionControlRequestTrigger(
                foreground = true,
                online = true,
                remoteState = RemoteSessionState.Live(lastAckedSequence = 40),
            ),
        )
        // ACK progress keeps the same stable effect key, so terminal output
        // cannot create a lease-request loop.
        assertEquals(
            SessionControlRequestTrigger.REALTIME_LIVE,
            sessionControlRequestTrigger(
                foreground = true,
                online = true,
                remoteState = RemoteSessionState.Live(lastAckedSequence = 41),
            ),
        )
        assertEquals(
            SessionControlRequestTrigger.NONE,
            sessionControlRequestTrigger(
                foreground = true,
                online = true,
                remoteState = RemoteSessionState.Controlling(
                    lastAckedSequence = 41,
                    leaseId = UUID.randomUUID().toString(),
                    expiresAtEpochMillis = Long.MAX_VALUE,
                ),
            ),
        )
        assertEquals(
            SessionControlRequestTrigger.FOREGROUND_RESUME,
            sessionControlRequestTrigger(
                foreground = true,
                online = true,
                remoteState = RemoteSessionState.ReadOnly(
                    lastAckedSequence = 41,
                    reason = "transport reconnected without a live lease",
                ),
            ),
        )
        assertEquals(
            SessionControlRequestTrigger.NONE,
            sessionControlRequestTrigger(
                foreground = true,
                online = false,
                remoteState = null,
            ),
        )
        assertEquals(
            SessionControlRequestTrigger.NONE,
            sessionControlRequestTrigger(
                foreground = false,
                online = true,
                remoteState = RemoteSessionState.Live(lastAckedSequence = 41),
            ),
        )
        assertEquals(
            SessionControlRequestTrigger.FOREGROUND_RESUME,
            sessionControlRequestTrigger(
                foreground = true,
                online = true,
                remoteState = RemoteSessionState.ReadOnly(
                    lastAckedSequence = 41,
                    reason = "control released",
                ),
            ),
        )
        assertEquals(
            SessionControlRequestTrigger.FOREGROUND_RESUME,
            sessionControlRequestTrigger(
                foreground = true,
                online = true,
                remoteState = RemoteSessionState.ReadOnly(
                    lastAckedSequence = 41,
                    reason = "LOCAL_INPUT_WON",
                ),
            ),
        )
    }

    @Test
    fun controlRequestEffectIsStableAcrossAckButAdvancesForEveryForegroundEntry() {
        val initial = sessionControlRequestEffectKey(
            foreground = true,
            foregroundGeneration = 1,
            online = true,
            remoteState = null,
        )
        assertEquals(
            SessionControlRequestEffectKey(1, SessionControlRequestTrigger.INITIAL_OPEN),
            initial,
        )

        val liveAt40 = sessionControlRequestEffectKey(
            foreground = true,
            foregroundGeneration = 1,
            online = true,
            remoteState = RemoteSessionState.Live(lastAckedSequence = 40),
        )
        val liveAt41 = sessionControlRequestEffectKey(
            foreground = true,
            foregroundGeneration = 1,
            online = true,
            remoteState = RemoteSessionState.Live(lastAckedSequence = 41),
        )
        assertEquals(liveAt40, liveAt41)
        assertEquals(
            null,
            sessionControlRequestEffectKey(
                foreground = false,
                foregroundGeneration = 1,
                online = true,
                remoteState = RemoteSessionState.Live(lastAckedSequence = 41),
            ),
        )

        val secondForegroundGeneration = nextForegroundGeneration(1)
        assertEquals(2L, secondForegroundGeneration)
        val resumed = sessionControlRequestEffectKey(
            foreground = true,
            foregroundGeneration = secondForegroundGeneration,
            online = true,
            remoteState = RemoteSessionState.Live(lastAckedSequence = 41),
        )
        assertTrue(resumed != liveAt41)
        assertEquals(3L, nextForegroundGeneration(secondForegroundGeneration))
    }

    @Test
    fun failedBootstrapRetryReplacesOnlyTheInvalidInstalledClient() {
        val failed = RemoteSessionState.Reconnecting(
            lastAckedSequence = 40,
            reason = SESSION_BOOTSTRAP_FAILED_REASON,
        )
        assertTrue(shouldReplaceLiveSessionForOpen(liveSessionExists = true, remoteState = failed))
        assertFalse(shouldReplaceLiveSessionForOpen(liveSessionExists = false, remoteState = failed))
        assertFalse(
            shouldReplaceLiveSessionForOpen(
                liveSessionExists = true,
                remoteState = RemoteSessionState.Reconnecting(
                    lastAckedSequence = 40,
                    reason = SESSION_BOOTSTRAP_IN_PROGRESS_REASON,
                ),
            ),
        )
        assertFalse(
            shouldReplaceLiveSessionForOpen(
                liveSessionExists = true,
                remoteState = RemoteSessionState.Live(lastAckedSequence = 40),
            ),
        )
    }

    @Test
    fun initialBootstrapIsEstablishingAndDeviceBackRequiresASecondPress() {
        assertTrue(
            isEstablishingSessionConnection(
                RemoteSessionState.Reconnecting(
                    lastAckedSequence = 0,
                    reason = SESSION_BOOTSTRAP_IN_PROGRESS_REASON,
                ),
            ),
        )
        assertTrue(
            isEstablishingSessionConnection(
                RemoteSessionState.Reconnecting(
                    lastAckedSequence = 0,
                    reason = SESSION_BOOTSTRAP_FAILED_REASON,
                ),
            ),
        )
        assertFalse(
            isEstablishingSessionConnection(
                RemoteSessionState.Reconnecting(lastAckedSequence = 4, reason = "remote session interrupted"),
            ),
        )
        assertFalse(shouldExitAfterDeviceBack(nowMillis = 10_000L, armedAtMillis = null))
        assertTrue(shouldExitAfterDeviceBack(nowMillis = 11_999L, armedAtMillis = 10_000L))
        assertTrue(shouldExitAfterDeviceBack(nowMillis = 12_000L, armedAtMillis = 10_000L))
        assertFalse(shouldExitAfterDeviceBack(nowMillis = 12_001L, armedAtMillis = 10_000L))
    }

    @Test
    fun leaseHeldRetryOutlivesServerTtlButStopsAfterItsBoundedSchedule() = runBlocking {
        assertTrue(CONTROL_LEASE_HELD_RETRY_DELAYS_MILLIS.sum() > 30_000L)
        var requests = 0
        val pauses = mutableListOf<Long>()

        val acquired = acquireControlLeaseWithRetry(
            remoteState = { RemoteSessionState.Live(lastAckedSequence = 50) },
            request = {
                requests += 1
                if (requests < 3) throw TermLinkApiException(409, "LEASE_HELD", traceId = null)
            },
            retryDelaysMillis = listOf(1_000L, 2_000L),
            pause = { pauses += it },
        )

        assertTrue(acquired)
        assertEquals(3, requests)
        assertEquals(listOf(1_000L, 2_000L), pauses)
    }

    @Test
    fun leaseRetryRejectsAuthorityFailuresAndSkipsAnExistingLease() {
        var requests = 0
        val failure = assertThrows(TermLinkApiException::class.java) {
            runBlocking {
                acquireControlLeaseWithRetry(
                    remoteState = { RemoteSessionState.Live(lastAckedSequence = 50) },
                    request = {
                        requests += 1
                        throw TermLinkApiException(403, "CONTROL_NOT_GRANTED", traceId = null)
                    },
                    retryDelaysMillis = listOf(1L),
                    pause = { error("must not retry") },
                )
            }
        }
        assertEquals("CONTROL_NOT_GRANTED", failure.code)
        assertEquals(1, requests)

        val skipped = runBlocking {
            acquireControlLeaseWithRetry(
                remoteState = {
                    RemoteSessionState.Controlling(
                        lastAckedSequence = 50,
                        leaseId = UUID.randomUUID().toString(),
                        expiresAtEpochMillis = Long.MAX_VALUE,
                    )
                },
                request = { error("must not request an existing lease") },
            )
        }
        assertFalse(skipped)
    }

    @Test
    fun leaseRetryRecoversFromTransientHttpAndNetworkFailures() = runBlocking {
        var requests = 0
        val pauses = mutableListOf<Long>()

        val acquired = acquireControlLeaseWithRetry(
            remoteState = { RemoteSessionState.Live(lastAckedSequence = 50) },
            request = {
                requests += 1
                when (requests) {
                    1 -> throw TermLinkApiException(503, "LEASE_UNAVAILABLE", traceId = null)
                    2 -> throw IOException("network switched")
                }
            },
            retryDelaysMillis = emptyList(),
            transientRetryDelaysMillis = listOf(5L, 10L),
            pause = { pauses += it },
        )

        assertTrue(acquired)
        assertEquals(3, requests)
        assertEquals(listOf(5L, 10L), pauses)
    }

    @Test
    fun controllingRuntimeStateClearsAStaleSessionFailure() {
        val sessionId = UUID.randomUUID()
        val failure = SessionOpenFailure(SessionOpenFailureCode.SERVER_REJECTED)
        val failures = mapOf(sessionId to failure)

        assertEquals(
            failures,
            sessionFailuresAfterRuntimeState(
                failures,
                sessionId,
                RemoteSessionState.ReadOnly(lastAckedSequence = 1, reason = "lease expired"),
            ),
        )
        assertTrue(
            sessionFailuresAfterRuntimeState(
                failures,
                sessionId,
                RemoteSessionState.Controlling(
                    lastAckedSequence = 1,
                    leaseId = UUID.randomUUID().toString(),
                    expiresAtEpochMillis = Long.MAX_VALUE,
                ),
            ).isEmpty(),
        )
    }

    @Test
    fun readOnlyCleanupDoesNotPruneAnInFlightLiveBootstrap() {
        val bootstrappingSession = UUID.randomUUID()
        val installedReadOnlySession = UUID.randomUUID()
        val installedLiveSession = UUID.randomUUID()

        assertEquals(
            setOf(bootstrappingSession, installedReadOnlySession, installedLiveSession),
            retainedRuntimeMetadataSessionIds(
                authoritativeSessions = listOf(
                    SessionSummary(
                        id = bootstrappingSession,
                        pcId = UUID.randomUUID(),
                        shellType = "powershell-7",
                        encryptedTitle = null,
                        state = SessionState.RUNNING,
                        columns = 120,
                        rows = 30,
                        lastOutputSequence = 76uL,
                        online = true,
                        createdAt = Instant.EPOCH,
                        endedAt = null,
                    ),
                ),
                readOnlySessionIds = setOf(installedReadOnlySession),
                liveSessionIds = setOf(installedLiveSession),
            ),
        )
    }

    @Test
    fun leaseHeldRetryStopsAfterTheConfiguredAttempts() {
        var requests = 0
        val pauses = mutableListOf<Long>()
        val failure = assertThrows(TermLinkApiException::class.java) {
            runBlocking {
                acquireControlLeaseWithRetry(
                    remoteState = { RemoteSessionState.Live(lastAckedSequence = 50) },
                    request = {
                        requests += 1
                        throw TermLinkApiException(409, "LEASE_HELD", traceId = null)
                    },
                    retryDelaysMillis = listOf(1L, 2L),
                    pause = { pauses += it },
                )
            }
        }

        assertEquals("LEASE_HELD", failure.code)
        assertEquals(3, requests)
        assertEquals(listOf(1L, 2L), pauses)
    }

    @Test
    fun leaseRetryPropagatesCancellationWithoutAnotherRequest() {
        val cancellation = CancellationException("session page left")
        var requests = 0
        val thrown = assertThrows(CancellationException::class.java) {
            runBlocking {
                acquireControlLeaseWithRetry(
                    remoteState = { RemoteSessionState.Live(lastAckedSequence = 50) },
                    request = {
                        requests += 1
                        throw TermLinkApiException(409, "LEASE_HELD", traceId = null)
                    },
                    retryDelaysMillis = listOf(1L),
                    pause = { throw cancellation },
                )
            }
        }

        assertTrue(thrown === cancellation)
        assertEquals(1, requests)
    }

    @Test
    fun serverUrlPolicyRequiresHttpsUnlessExplicitlyTestingCleartext() {
        assertEquals("https://example.com:8443", ServerUrlPolicy.normalize(" HTTPS://Example.com:8443/ "))
        assertEquals("http://127.0.0.1:8080", ServerUrlPolicy.normalize("http://127.0.0.1:8080", allowCleartextForLocalTest = true))
        assertThrows(IllegalArgumentException::class.java) { ServerUrlPolicy.normalize("http://127.0.0.1:8080") }
        assertThrows(IllegalArgumentException::class.java) { ServerUrlPolicy.normalize("https://user:pass@example.com") }
        assertThrows(IllegalArgumentException::class.java) { ServerUrlPolicy.normalize("https://example.com/api") }
        assertThrows(IllegalArgumentException::class.java) { ServerUrlPolicy.normalize("https://example.com?debug=true") }
    }

    @Test
    fun historyDownloadsUseTheSameDebugOnlyCleartextGate() {
        assertTrue(historyDownloadUriAllowed(URI("https://objects.example/history"), false))
        assertTrue(historyDownloadUriAllowed(URI("http://192.168.9.28:8080/history"), true))
        assertFalse(historyDownloadUriAllowed(URI("http://192.168.9.28:8080/history"), false))
        assertFalse(historyDownloadUriAllowed(URI("ftp://192.168.9.28/history"), true))
    }

    @Test
    fun pairingParserCanonicalizesCodeAndNonce() {
        val id = UUID.randomUUID()
        val nonceBytes = ByteArray(32) { it.toByte() }
        val nonce = Base64.getUrlEncoder().withoutPadding().encodeToString(nonceBytes)
        val fullWidthId = id.toString().replace('-', '\uFF0D')
        val parsed = PairingInputParser.parse(fullWidthId, " abcd-efgh ", nonce)

        assertEquals(id, parsed.challengeId)
        assertEquals("ABCDEFGH", parsed.code)
        assertEquals(nonce, parsed.challengeNonce)
        assertThrows(IllegalArgumentException::class.java) {
            PairingInputParser.parse(id.toString(), "AAAA-AAAA", "bad")
        }
    }

    @Test
    fun pendingAndTrustedDeviceResponsesRemainServerAuthoritative() {
        val id = UUID.randomUUID()
        val pending = parseDevice(id, "PENDING")
        val trusted = parseDevice(id, "TRUSTED")

        assertEquals(DeviceTrustState.PENDING, pending.trustState)
        assertEquals(DeviceTrustState.TRUSTED, trusted.trustState)
        assertEquals(id, pending.id)
        assertEquals(id, trusted.id)
    }

    @Test
    fun pcReachabilityUsesTheServerOnlineFieldInsteadOfLastSeenTime() {
        val onlineWithOldHeartbeat = Json.parseToJsonElement(
            """{"online":true,"last_seen_at":"2000-01-01T00:00:00Z"}""",
        ).jsonObject
        val offlineWithFutureHeartbeat = Json.parseToJsonElement(
            """{"online":false,"last_seen_at":"2999-01-01T00:00:00Z"}""",
        ).jsonObject
        val missingOnline = Json.parseToJsonElement(
            """{"last_seen_at":"2999-01-01T00:00:00Z"}""",
        ).jsonObject

        assertTrue(authoritativePcOnline(onlineWithOldHeartbeat))
        assertFalse(authoritativePcOnline(offlineWithFutureHeartbeat))
        assertFalse(authoritativePcOnline(missingOnline))
    }

    @Test
    fun emptyCatalogAcceptsLegacyNullAndProductionEmptyArray() {
        val legacyNull = Json.parseToJsonElement("""{"plans":null}""").jsonObject
        val emptyArray = Json.parseToJsonElement("""{"plans":[]}""").jsonObject
        val emptyItems = Json.parseToJsonElement("""{"items":null}""").jsonObject

        assertEquals(emptyList<Any>(), ProductResponseParser.catalogPlanObjects(legacyNull))
        assertEquals(emptyList<Any>(), ProductResponseParser.catalogPlanObjects(emptyArray))
        assertEquals(emptyList<Any>(), ProductResponseParser.itemObjects(emptyItems))
    }

    @Test
    fun authStateEnvelopeUsesCipherAndDoesNotStorePlaintext() {
        val cipher = FakeCipher()
        val plaintext = "refresh-token-secret".encodeToByteArray()

        val sealed = AuthStateEnvelope.seal(plaintext, cipher)

        assertFalse(sealed.contains("refresh-token-secret"))
        assertEquals(plaintext.toList(), AuthStateEnvelope.open(sealed, cipher).toList())
        assertEquals(1, cipher.encryptions)
        assertEquals(1, cipher.decryptions)
    }

    @Test
    fun reservedInputSequenceProviderAdvancesAcrossRangesWithoutReuse() {
        val ranges = ArrayDeque(listOf(101L..103L, 201L..202L))
        val provider = ProductionProductRepository.ReservedInputSequenceProvider {
            ranges.removeFirst()
        }

        assertEquals(listOf(101L, 102L, 103L, 201L, 202L), List(5) { provider.next() })
    }

    @Test
    fun tokenRefreshCoordinatorCallsRefreshAndRejectsBlankTokens() {
        runBlocking {
            var calls = 0
            val forced = mutableListOf<Boolean>()
            val coordinator = TokenRefreshCoordinator { force ->
                calls += 1
                forced += force
                "fresh-$calls"
            }

            assertEquals("fresh-1", coordinator.freshAccessToken())
            assertEquals("fresh-2", coordinator.freshAccessToken(force = true))
            assertEquals(2, calls)
            assertEquals(listOf(false, true), forced)
            assertThrows(IllegalArgumentException::class.java) {
                runBlocking { TokenRefreshCoordinator { _ -> " " }.freshAccessToken() }
            }
        }
    }

    @Test
    fun authenticationLifecycleFenceRejectsRefreshCapturedBeforeSignOutOrClose() {
        val fence = AuthenticationLifecycleFence()
        val beforeSignOut = fence.capture()

        fence.invalidate()

        assertFalse(fence.isCurrent(beforeSignOut))
        val afterSignOut = fence.capture()
        assertTrue(fence.isCurrent(afterSignOut))

        fence.invalidate()
        assertFalse(fence.isCurrent(afterSignOut))
    }

    @Test
    fun authenticationExpiryClearsTheVisibleAccountIdentity() {
        val expired = authenticationExpiredSnapshot(
            snapshot = ProductSnapshot(
                signedIn = true,
                accountLabel = "alice@example.test",
                authenticationState = ProductAuthenticationState.AUTHENTICATED,
                connectionState = ProductConnectionState.CONNECTED,
            ),
            message = "Authentication expired",
        )

        assertFalse(expired.signedIn)
        assertEquals(null, expired.accountLabel)
        assertEquals(ProductAuthenticationState.SIGNED_OUT, expired.authenticationState)
        assertEquals(ProductConnectionState.ERROR, expired.connectionState)
        assertEquals("Authentication expired", expired.connectionError)
    }

    @Test
    fun onlyRejectedRefreshCredentialsExpireAnEstablishedAuthentication() {
        assertTrue(
            isAuthenticationRefreshRejection(
                NativeOidcException(NativeOidcFailure.INVALID_CREDENTIALS),
            ),
        )
        assertFalse(
            isAuthenticationRefreshRejection(
                NativeOidcException(NativeOidcFailure.NETWORK),
            ),
        )
        assertFalse(isAuthenticationRefreshRejection(IllegalStateException("temporary failure")))
    }

    @Test
    fun outerRefreshFailureCannotReplaceThePublishedAuthenticationExpiry() {
        val message = "Authentication expired"
        val expired = authenticationExpiredSnapshot(
            snapshot = ProductSnapshot(
                signedIn = true,
                authenticationState = ProductAuthenticationState.AUTHENTICATED,
                connectionState = ProductConnectionState.CONNECTED,
            ),
            message = message,
        )

        assertTrue(shouldPreserveAuthenticationExpiredFailure(expired, message))
        assertFalse(
            shouldPreserveAuthenticationExpiredFailure(
                expired.copy(
                    authenticationState = ProductAuthenticationState.NATIVE_CREDENTIALS,
                    connectionError = null,
                ),
                message,
            ),
        )
    }

    @Test
    fun realtimeTokenSourcePublishesTheLatestRefreshedTokenAndClearsOnSignOut() {
        val source = RealtimeAccessTokenSource()
        val provider = AccessTokenProvider(source::requireCurrent)

        assertThrows(IllegalStateException::class.java, source::requireCurrent)
        assertEquals("fresh-1", source.publish("fresh-1"))
        assertEquals("fresh-1", provider.accessToken())
        assertEquals("fresh-2", source.publish("fresh-2"))
        assertEquals("fresh-2", CompletableFuture.supplyAsync(provider::accessToken).get())

        source.clear()
        assertThrows(IllegalStateException::class.java, provider::accessToken)
    }

    private fun parseDevice(id: UUID, trustState: String) = ProductResponseParser.device(
        Json.parseToJsonElement(
            """
            {
              "id":"$id",
              "type":"ANDROID",
              "name":"Test phone",
              "key_id":"device-key",
              "exchange_public_key":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
              "signing_public_key":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
              "trust_state":"$trustState",
              "created_at":"2026-08-04T00:00:00Z"
            }
            """.trimIndent(),
        ).jsonObject,
    )

    private class FakeCipher : AuthStateCipher {
        var encryptions = 0
        var decryptions = 0

        override fun transform(input: ByteArray, encrypt: Boolean): ByteArray {
            if (encrypt) encryptions++ else decryptions++
            return if (encrypt) {
                ByteArray(12) { 0x5A } + input.map { (it.toInt() xor 0xA5).toByte() }.toByteArray()
            } else {
                input.copyOfRange(12, input.size).map { (it.toInt() xor 0xA5).toByte() }.toByteArray()
            }
        }
    }
}
