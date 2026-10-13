package dev.termlink.network.production

import com.google.protobuf.ByteString
import dev.termlink.crypto.CryptoDirection
import dev.termlink.crypto.DeviceIdentity
import dev.termlink.crypto.FrozenHistoryCrypto
import dev.termlink.crypto.SessionCrypto
import dev.termlink.model.HeapState
import dev.termlink.model.TerminalBufferBudget
import dev.termlink.protocol.realtime.v1.Envelope
import dev.termlink.protocol.realtime.v1.FrozenHistoryMetadata
import dev.termlink.protocol.realtime.v1.HostHistoryControlActionV1
import dev.termlink.protocol.realtime.v1.HostHistoryControlPayload
import dev.termlink.protocol.realtime.v1.HostHistoryObjectKindV1
import dev.termlink.protocol.realtime.v1.HostHistoryRequestKindV1
import dev.termlink.protocol.realtime.v1.HostHistoryRequestPayload
import dev.termlink.protocol.realtime.v1.HostHistoryResponsePayload
import dev.termlink.protocol.realtime.v1.HostHistoryResponseStatusV1
import dev.termlink.protocol.realtime.v1.MessageType
import dev.termlink.protocol.realtime.v1.SubscribePayload
import dev.termlink.protocol.realtime.v1.TerminalAttachResultPayload
import dev.termlink.session.DurableAckCommit
import dev.termlink.terminal.api.TerminalEngine
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalThemeV1
import java.nio.ByteBuffer
import java.security.MessageDigest
import java.util.UUID
import java.util.concurrent.CopyOnWriteArrayList
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.yield
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.test.assertFailsWith

class FrozenHistoryRpcTest {
    private companion object { const val TEST_FROZEN_CHUNK_BYTES = 512 * 1024 }
    private val token = ByteString.copyFrom(ByteArray(32) { 7 })
    private val pageKind = HostHistoryRequestKindV1.HOST_HISTORY_REQUEST_KIND_V1_FROZEN_PAGE
    private val releaseKind = HostHistoryRequestKindV1.HOST_HISTORY_REQUEST_KIND_V1_FROZEN_RELEASE

    private fun request(kind: HostHistoryRequestKindV1 = pageKind,
        stateContract: String = ProductionTerminalStateContract.GRAPHICS_STATE) = FrozenHistoryRequestContext(
        kind, 42uL, if (kind == releaseKind) 0uL else ULong.MAX_VALUE,
        token, 17uL, 43uL, if (kind == releaseKind) 0 else 128,
        if (kind == releaseKind) 0 else 65_536, if (kind == releaseKind) 0 else 4 * 1024 * 1024,
        if (kind == releaseKind) 0 else 8 * 1024 * 1024, 9, 7, "frozen-key", stateContract,
    )

    private fun metadata(stateContract: String = ProductionTerminalStateContract.GRAPHICS_STATE) =
        FrozenHistoryMetadata.newBuilder()
        .setToken(token).setCaptureId(17).setCaptureRevision(43).setSnapshotSeq(42)
        .setLeaseRemainingMs(60_000).setStateContract(stateContract)
        .build()

    private fun response(bytes: ByteArray, index: Int = 0, chunkCount: Int = 1): HostHistoryResponsePayload {
        val start = index * TEST_FROZEN_CHUNK_BYTES
        val length = minOf(TEST_FROZEN_CHUNK_BYTES, bytes.size - start)
        return HostHistoryResponsePayload.newBuilder()
            .setStatus(HostHistoryResponseStatusV1.HOST_HISTORY_RESPONSE_STATUS_V1_OK)
            .setKind(HostHistoryObjectKindV1.HOST_HISTORY_OBJECT_KIND_V1_FROZEN_PAGE)
            .setObjectId(ByteString.copyFrom(ByteArray(16) { 3 }))
            .setObjectSha256(ByteString.copyFrom(MessageDigest.getInstance("SHA-256").digest(bytes)))
            .setSnapshotSeq(42).setRequestBeforeLineId(-1L)
            .setKeyId("frozen-key").setFormatName("TERMLINK_FROZEN_HISTORY_PAGE_V2")
            .setFormatVersion(1).setFrozenHistory(metadata())
            .setObjectBytes(bytes.size.toLong()).setChunkIndex(index).setChunkCount(chunkCount)
            .setObjectChunk(ByteString.copyFrom(bytes, start, length)).build()
    }

    @Test fun oldPeerAndLegacyProfileCannotAdvertiseFrozenHistory() {
        val both = listOf("host_history_frozen_v1", "tlhp_v2")
        assertFalse(supportsFrozenHistory(ProductionTerminalProfile.GRAPHICS, emptyList()))
        assertFalse(supportsFrozenHistory(ProductionTerminalProfile.GRAPHICS, both.take(1)))
        assertFalse(supportsFrozenHistory(ProductionTerminalProfile.LEGACY, both))
        assertTrue(supportsFrozenHistory(ProductionTerminalProfile.GRAPHICS, both))
        assertTrue(supportsFrozenHistory(ProductionTerminalProfile.GRAPHICS_UNICODE16, both))
        assertTrue(supportsFrozenHistory(ProductionTerminalProfile.GRAPHICS, both + "future_capability_v2"))
        assertFalse(supportsFrozenHistory(ProductionTerminalProfile.GRAPHICS, both + ""))
        assertFalse(supportsFrozenHistory(ProductionTerminalProfile.GRAPHICS, both + "x".repeat(65)))
    }

    @Test fun frozenMetadataMustMatchNegotiatedGraphicsWidthContract() {
        val v1 = request()
        val v2 = request(stateContract = ProductionTerminalStateContract.GRAPHICS_UNICODE16_STATE)
        val v1Response = response(byteArrayOf(1))
        val v2Response = v1Response.toBuilder()
            .setFrozenHistory(metadata(ProductionTerminalStateContract.GRAPHICS_UNICODE16_STATE)).build()
        v1.validate(v1Response)
        v2.validate(v2Response)
        assertFailsWith<IllegalArgumentException> { v1.validate(v2Response) }
        assertFailsWith<IllegalArgumentException> { v2.validate(v1Response) }
    }

    @Test fun unicode16FrozenPageKeyIdDoesNotAcceptGraphicsV1Key() {
        val recipient = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val v2KeyId = FrozenHistoryCrypto.keyId(recipient, 7,
            ProductionTerminalStateContract.GRAPHICS_UNICODE16_STATE)
        val v1KeyId = FrozenHistoryCrypto.keyId(recipient, 7,
            ProductionTerminalStateContract.GRAPHICS_STATE)
        val v2 = request(stateContract = ProductionTerminalStateContract.GRAPHICS_UNICODE16_STATE)
            .copy(keyId = v2KeyId)
        val v2Response = response(byteArrayOf(1)).toBuilder()
            .setKeyId(v2KeyId)
            .setFrozenHistory(metadata(ProductionTerminalStateContract.GRAPHICS_UNICODE16_STATE))
            .build()
        v2.validate(v2Response)
        assertFailsWith<IllegalArgumentException> { v2.copy(keyId = v1KeyId).validate(v2Response) }
        assertFailsWith<IllegalArgumentException> {
            v2.validate(v2Response.toBuilder().setKeyId(v1KeyId).build())
        }
    }

    @Test fun encryptedV2ObjectHasDedicatedSizeAnd129ChunkCeiling() {
        val max = FrozenHistoryCrypto.MAX_PAGE_V2_BYTES + FrozenHistoryCrypto.OBJECT_OVERHEAD_BYTES
        assertEquals(129, frozenHistoryChunkCount(max.toLong()))
        assertFailsWith<IllegalArgumentException> { frozenHistoryChunkCount(0) }
        assertFailsWith<IllegalArgumentException> { frozenHistoryChunkCount(max.toLong() + 1) }
    }

    @Test fun localObjectBudgetRejectsBeforeAnyReservation() {
        val budget = TerminalBufferBudget { HeapState(192L * 1024 * 1024, 0) }
        val pending = ProductionRealtimeSessionClient.HostHistoryAccumulator(
            ProductionTerminalProfile.GRAPHICS, budget, 1, request())
        try {
            val oversized = response(byteArrayOf(1)).toBuilder()
                .setObjectBytes(8L * 1024 * 1024 + 1).setChunkCount(17).build()
            assertEquals("PAGE_LIMIT", assertFailsWith<ProductionFrozenHistoryException> {
                pending.accept(oversized)
            }.code)
            assertEquals(0L, budget.reservedBytes)
        } finally {
            pending.destroy()
        }
    }

    @Test fun pageMetadataRejectsChangedTokenRevisionSequenceAnchorAndFormat() {
        val context = request()
        val baseline = response(byteArrayOf(1))
        context.validate(baseline)
        val changed = listOf(
            baseline.toBuilder().setSnapshotSeq(41).build(),
            baseline.toBuilder().setRequestBeforeLineId(0).build(),
            baseline.toBuilder().setFormatName("termlink.transcript").build(),
            baseline.toBuilder().setFormatVersion(2).build(),
            baseline.toBuilder().setFrozenHistory(metadata().toBuilder().setCaptureRevision(44)).build(),
            baseline.toBuilder().setFrozenHistory(metadata().toBuilder().setSnapshotSeq(41)).build(),
            baseline.toBuilder().setFrozenHistory(metadata().toBuilder().setToken(ByteString.copyFrom(ByteArray(32)))).build(),
            baseline.toBuilder().setFrozenHistory(metadata().toBuilder().setLeaseRemainingMs(0)).build(),
        )
        changed.forEach { assertFailsWith<IllegalArgumentException> { context.validate(it) } }
    }

    @Test fun outOfOrderChunksAssembleAndDuplicateMetadataCannotCrossCapture() {
        val bytes = ByteArray(TEST_FROZEN_CHUNK_BYTES + 3) { (it % 251).toByte() }
        val budget = TerminalBufferBudget { HeapState(512L * 1024 * 1024, 0) }
        val pending = ProductionRealtimeSessionClient.HostHistoryAccumulator(
            ProductionTerminalProfile.GRAPHICS, budget, 1, request())
        try {
            assertEquals(null, pending.accept(response(bytes, 1, 2))?.completed)
            val completed = pending.accept(response(bytes, 0, 2))?.completed
            assertTrue(completed != null)
            assertTrue(completed!!.sealedObject.contentEquals(bytes))
            completed.close()
            assertEquals(0L, budget.reservedBytes)
        } finally {
            pending.destroy()
        }
        val tampered = ProductionRealtimeSessionClient.HostHistoryAccumulator(
            ProductionTerminalProfile.GRAPHICS, budget, 1, request())
        try {
            tampered.accept(response(bytes, 1, 2))
            val otherCapture = response(bytes, 0, 2).toBuilder()
                .setFrozenHistory(metadata().toBuilder().setCaptureId(18)).build()
            val failure = assertFailsWith<IllegalArgumentException> { tampered.accept(otherCapture) }
            tampered.fail(failure) // handleHostHistoryResponse closes the failed request.
            assertEquals(0L, budget.reservedBytes)
        } finally {
            tampered.destroy()
        }
    }

    @Test fun browseGenerationAndEpochFenceLatePages() {
        val context = request()
        assertTrue(isCurrentFrozenBrowse(context, 9, 7))
        assertFalse(isCurrentFrozenBrowse(context, 10, 7))
        assertFalse(isCurrentFrozenBrowse(context, 9, 8))
    }

    @Test fun pageBudgetNeedsMatchingCaptureAndRequiredCounts() {
        val context = request()
        val error = HostHistoryResponsePayload.newBuilder()
            .setStatus(HostHistoryResponseStatusV1.HOST_HISTORY_RESPONSE_STATUS_V1_REJECTED)
            .setErrorCode("PAGE_BUDGET")
            .setFrozenHistory(metadata().toBuilder().setRequiredCells(65_537))
            .build()
        context.validateError(error)
        assertFailsWith<IllegalArgumentException> { context.validateError(error.toBuilder().clearFrozenHistory().build()) }
        assertFailsWith<IllegalArgumentException> { context.validateError(error.toBuilder()
            .setFrozenHistory(metadata().toBuilder().setRequiredCells(65_537).setCaptureId(18)).build()) }
    }

    @Test fun onlyBeginAnchorChangedMayReportANewerAvailableSnapshot() {
        val begin = request(HostHistoryRequestKindV1.HOST_HISTORY_REQUEST_KIND_V1_FROZEN_BEGIN)
        val error = HostHistoryResponsePayload.newBuilder()
            .setStatus(HostHistoryResponseStatusV1.HOST_HISTORY_RESPONSE_STATUS_V1_REJECTED)
            .setErrorCode("ANCHOR_CHANGED").setSnapshotSeq(43).build()
        assertEquals(43uL, begin.validateError(error))
        assertEquals(null, begin.validateError(error.toBuilder().setSnapshotSeq(0).build()))
        listOf(42L, 41L, -1L).forEach { stale ->
            assertFailsWith<IllegalArgumentException> {
                begin.validateError(error.toBuilder().setSnapshotSeq(stale).build())
            }
        }
        assertFailsWith<IllegalArgumentException> { request().validateError(error) }
        assertFailsWith<IllegalArgumentException> {
            begin.validateError(error.toBuilder().setErrorCode("STALE_ANCHOR").build())
        }
    }

    @Test fun replayedChunkCannotChangeBytesOrBudgetMetadata() {
        val bytes = ByteArray(TEST_FROZEN_CHUNK_BYTES + 1) { 9 }
        val budget = TerminalBufferBudget { HeapState(512L * 1024 * 1024, 0) }
        val pending = ProductionRealtimeSessionClient.HostHistoryAccumulator(
            ProductionTerminalProfile.GRAPHICS, budget, 1, request())
        try {
            val first = response(bytes, 0, 2)
            pending.accept(first)
            val changed = first.toBuilder().setObjectChunk(ByteString.copyFrom(ByteArray(TEST_FROZEN_CHUNK_BYTES) { 8 })).build()
            assertFailsWith<IllegalArgumentException> { pending.accept(changed) }
            val changedMetadata = first.toBuilder().setFrozenHistory(metadata().toBuilder().setHeadTruncated(true)).build()
            assertFailsWith<IllegalArgumentException> { pending.accept(changedMetadata) }
        } finally {
            pending.destroy()
            assertEquals(0L, budget.reservedBytes)
        }
    }

    @Test fun onlyFrozenReleaseAcceptsAnEmptyOkWithMatchingMetadata() {
        val budget = TerminalBufferBudget { HeapState(512L * 1024 * 1024, 0) }
        fun releaseResponse() = HostHistoryResponsePayload.newBuilder()
            .setStatus(HostHistoryResponseStatusV1.HOST_HISTORY_RESPONSE_STATUS_V1_OK)
            .setFrozenHistory(metadata().toBuilder().setLeaseRemainingMs(0)).build()
        val pending = ProductionRealtimeSessionClient.HostHistoryAccumulator(
            ProductionTerminalProfile.GRAPHICS, budget, 1, request(releaseKind))
        try {
            assertFailsWith<IllegalArgumentException> { pending.acceptRelease(releaseResponse().toBuilder().clearFrozenHistory().build()) }
            assertFailsWith<IllegalArgumentException> { pending.acceptRelease(releaseResponse().toBuilder()
                .setFrozenHistory(metadata().toBuilder().setSnapshotSeq(41)).build()) }
            assertTrue(pending.acceptRelease(releaseResponse()))
            assertFalse(pending.acceptRelease(releaseResponse()))
        } finally {
            pending.destroy()
        }
        val legacy = ProductionRealtimeSessionClient.HostHistoryAccumulator(
            ProductionTerminalProfile.GRAPHICS, budget, 1)
        try {
            assertFailsWith<IllegalArgumentException> { legacy.acceptRelease(releaseResponse()) }
        } finally {
            legacy.destroy()
        }
    }

    @Test fun authenticatedRelayEnvelopeRoundTripsBeginPageAndReleaseWithoutLiveAck() = runBlocking {
        val fixture = RelayFixture()
        try {
            fixture.start()
            assertTrue(fixture.client.supportsRemoteFrozenHistory())
            val beginning = fixture.scope.async { fixture.client.beginFrozenHistory(42uL) }
            val beginRequest = fixture.awaitHistoryRequest(1)
            assertEquals(HostHistoryRequestKindV1.HOST_HISTORY_REQUEST_KIND_V1_FROZEN_BEGIN, beginRequest.kind)
            assertEquals(-1L, beginRequest.beforeLineId)
            assertEquals(2, beginRequest.frozenHistory.pageFormatVersion)
            fixture.deliverPage(beginRequest, 1_001)
            val first = beginning.await()
            assertEquals(17uL, first.capture.captureId)
            assertEquals(43uL, first.capture.revision)

            val loading = fixture.scope.async { fixture.client.loadFrozenHistoryPage(first.capture, 20uL) }
            val pageRequest = fixture.awaitHistoryRequest(2)
            assertEquals(HostHistoryRequestKindV1.HOST_HISTORY_REQUEST_KIND_V1_FROZEN_PAGE, pageRequest.kind)
            assertEquals(20L, pageRequest.beforeLineId)
            assertEquals(token, pageRequest.frozenHistory.token)
            fixture.deliverPage(pageRequest, 1_002)
            val second = loading.await()
            assertEquals(20uL, second.objectData.requestBeforeLineId)

            val releasing = fixture.scope.async { fixture.client.releaseFrozenHistory(first.capture) }
            val releaseRequest = fixture.awaitHistoryRequest(3)
            assertEquals(releaseKind, releaseRequest.kind)
            assertEquals(2, releaseRequest.frozenHistory.pageFormatVersion)
            fixture.deliverRelease(releaseRequest, 1_003)
            releasing.await()
            first.close()
            second.close()
            assertEquals(0L, fixture.client.diagnostics().durableAck)
        } finally {
            fixture.close()
        }
    }

    @Test fun graphicsOldHostKeepsSessionButDoesNotEnableFrozenBrowse() = runBlocking {
        val fixture = RelayFixture(frozenCapability = false)
        try {
            fixture.start()
            assertFalse(fixture.client.supportsRemoteFrozenHistory())
            val error = assertFailsWith<ProductionFrozenHistoryException> {
                fixture.client.beginFrozenHistory(42uL)
            }
            assertEquals("UNSUPPORTED_FORMAT", error.code)
            assertTrue(fixture.historyRequests().isEmpty())
        } finally {
            fixture.close()
        }
    }

    @Test fun relayAnchorChangedExposesOnlyAuthenticatedForwardSnapshot() = runBlocking {
        val fixture = RelayFixture()
        try {
            fixture.start()
            val beginning = fixture.scope.async {
                runCatching { fixture.client.beginFrozenHistory(42uL) }.exceptionOrNull()
            }
            val request = fixture.awaitHistoryRequest(1)
            fixture.deliverAnchorChanged(request, 1_051, 43)
            val failure = beginning.await() as ProductionFrozenHistoryException
            assertEquals("ANCHOR_CHANGED", failure.code)
            assertEquals(43uL, failure.availableSnapshotSequence)
            assertEquals(null, failure.capture)
        } finally {
            fixture.close()
        }
    }

    @Test fun oversizedBeginAlwaysAttemptsReleaseAndStillReturnsCaptureIfReleaseFails() = runBlocking {
        for (releaseFails in listOf(false, true)) {
            val fixture = RelayFixture()
            try {
                fixture.start()
                val beginning = fixture.scope.async {
                    runCatching { fixture.client.beginFrozenHistory(42uL, maxObjectBytes = 1) }.exceptionOrNull()
                }
                val beginRequest = fixture.awaitHistoryRequest(1)
                fixture.deliverPage(beginRequest, 1_101, byteArrayOf(1, 2))
                val releaseRequest = fixture.awaitHistoryRequest(2)
                assertEquals(releaseKind, releaseRequest.kind)
                assertEquals(token, releaseRequest.frozenHistory.token)
                if (releaseFails) fixture.deliverRejectedRelease(releaseRequest, 1_102)
                else fixture.deliverRelease(releaseRequest, 1_102)
                val failure = beginning.await() as ProductionFrozenHistoryException
                assertEquals("PAGE_LIMIT", failure.code)
                assertEquals(17uL, failure.capture?.captureId)
                assertEquals(42uL, failure.capture?.snapshotSequence)
            } finally {
                fixture.close()
            }
        }
    }

    @Test fun beginPageBudgetWithCreatedCaptureAlsoReleasesItsToken() = runBlocking {
        val fixture = RelayFixture()
        try {
            fixture.start()
            val beginning = fixture.scope.async {
                runCatching { fixture.client.beginFrozenHistory(42uL, maxCells = 1) }.exceptionOrNull()
            }
            val beginRequest = fixture.awaitHistoryRequest(1)
            fixture.deliverPageBudget(beginRequest, 1_151)
            val releaseRequest = fixture.awaitHistoryRequest(2)
            assertEquals(releaseKind, releaseRequest.kind)
            assertEquals(token, releaseRequest.frozenHistory.token)
            fixture.deliverRelease(releaseRequest, 1_152)
            val failure = beginning.await() as ProductionFrozenHistoryException
            assertEquals("PAGE_BUDGET", failure.code)
            assertEquals(65_537uL, failure.requiredCells)
            assertEquals(17uL, failure.capture?.captureId)
        } finally {
            fixture.close()
        }
    }

    @Test fun supersededBeginCannotReleaseOrDeliverTheNewCapture() = runBlocking {
        val fixture = RelayFixture()
        try {
            fixture.start()
            val old = fixture.scope.async {
                runCatching { fixture.client.beginFrozenHistory(42uL) }.exceptionOrNull()
            }
            val oldRequest = fixture.awaitHistoryRequest(1)
            val current = fixture.scope.async { fixture.client.beginFrozenHistory(42uL) }
            val currentRequest = fixture.awaitHistoryRequest(2)
            assertEquals("STALE_CAPTURE", (old.await() as ProductionFrozenHistoryException).code)
            fixture.deliverPage(oldRequest, 1_201)
            fixture.deliverPage(currentRequest, 1_202, captureId = 18, tokenByte = 8)
            val currentPage = current.await()
            assertEquals(18uL, currentPage.capture.captureId)
            assertEquals(8, currentPage.capture.token.byteAt(0).toInt())
            assertFalse(fixture.historyRequests().any { it.kind == releaseKind })
            val releasing = fixture.scope.async { fixture.client.releaseFrozenHistory(currentPage.capture) }
            val releaseRequest = fixture.awaitHistoryRequest(3)
            fixture.deliverRelease(releaseRequest, 1_203, captureId = 18, tokenByte = 8)
            releasing.await()
            currentPage.close()
        } finally {
            fixture.close()
        }
    }

    @Test fun cancelledBeginCancelsItsHostRequestAndLatePageCannotReplaceNextCapture() = runBlocking {
        val fixture = RelayFixture()
        try {
            fixture.start()
            val cancelled = fixture.scope.async { fixture.client.beginFrozenHistory(42uL) }
            val oldRequest = fixture.awaitHistoryRequest(1)
            cancelled.cancelAndJoin()
            val control = fixture.awaitHistoryControl(1)
            assertEquals(HostHistoryControlActionV1.HOST_HISTORY_CONTROL_ACTION_V1_CANCEL, control.action)
            assertEquals(oldRequest.requestId, control.requestId)

            val current = fixture.scope.async { fixture.client.beginFrozenHistory(42uL) }
            val currentRequest = fixture.awaitHistoryRequest(2)
            fixture.deliverPage(oldRequest, 1_251)
            fixture.deliverPage(currentRequest, 1_252, captureId = 18, tokenByte = 8)
            val currentPage = withTimeout(2_000) { current.await() }
            assertEquals(18uL, currentPage.capture.captureId)
            assertEquals(8, currentPage.capture.token.byteAt(0).toInt())
            assertEquals(1, fixture.historyControls().count {
                it.action == HostHistoryControlActionV1.HOST_HISTORY_CONTROL_ACTION_V1_CANCEL
            })
            currentPage.close()
        } finally {
            fixture.close()
        }
    }

    @Test fun frozenBeginIdleTimeoutCancelsHostWorkAndKeepsNextBrowseUsable() = runBlocking {
        val fixture = RelayFixture(hostHistoryRequestTimeoutMillis = 1_000)
        try {
            fixture.start()
            val expired = fixture.scope.async { fixture.client.beginFrozenHistory(42uL) }
            val oldRequest = fixture.awaitHistoryRequest(1)
            val failure = assertFailsWith<IllegalStateException> { withTimeout(2_000) { expired.await() } }
            assertTrue(failure.message.orEmpty().contains("timed out"))
            val control = fixture.awaitHistoryControl(1)
            assertEquals(HostHistoryControlActionV1.HOST_HISTORY_CONTROL_ACTION_V1_CANCEL, control.action)
            assertEquals(oldRequest.requestId, control.requestId)

            val current = fixture.scope.async { fixture.client.beginFrozenHistory(42uL) }
            val currentRequest = fixture.awaitHistoryRequest(2)
            fixture.deliverPage(oldRequest, 1_271)
            fixture.deliverPage(currentRequest, 1_272, captureId = 19, tokenByte = 9)
            val currentPage = withTimeout(2_000) { current.await() }
            assertEquals(19uL, currentPage.capture.captureId)
            currentPage.close()
        } finally {
            fixture.close()
        }
    }

    @Test fun cleanupReleaseForOldCaptureSurvivesNewBrowseGeneration() = runBlocking {
        val fixture = RelayFixture()
        try {
            fixture.start()
            val old = fixture.scope.async {
                runCatching { fixture.client.beginFrozenHistory(42uL, maxObjectBytes = 1) }.exceptionOrNull()
            }
            val oldRequest = fixture.awaitHistoryRequest(1)
            fixture.deliverPage(oldRequest, 1_301, byteArrayOf(1, 2))
            val oldRelease = fixture.awaitHistoryRequest(2)
            assertEquals(releaseKind, oldRelease.kind)

            val current = fixture.scope.async { fixture.client.beginFrozenHistory(42uL) }
            val currentRequest = fixture.awaitHistoryRequest(3)
            fixture.deliverRelease(oldRelease, 1_302)
            fixture.deliverPage(currentRequest, 1_303, captureId = 18, tokenByte = 8)
            assertEquals("PAGE_LIMIT", (old.await() as ProductionFrozenHistoryException).code)
            val currentPage = current.await()
            assertEquals(18uL, currentPage.capture.captureId)

            val currentRelease = fixture.scope.async { fixture.client.releaseFrozenHistory(currentPage.capture) }
            val currentReleaseRequest = fixture.awaitHistoryRequest(4)
            assertEquals(18L, currentReleaseRequest.frozenHistory.captureId)
            fixture.deliverRelease(currentReleaseRequest, 1_304, captureId = 18, tokenByte = 8)
            currentRelease.await()
            currentPage.close()
        } finally {
            fixture.close()
        }
    }

    private inner class RelayFixture(
        private val frozenCapability: Boolean = true,
        hostHistoryRequestTimeoutMillis: Long = 3_000,
    ) : AutoCloseable {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        private val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        private val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        private val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        private val androidIdentity = DeviceIdentity.generate()
        private val pcIdentity = DeviceIdentity.generate()
        private val envelopeKeyId = "epoch-00000007".encodeToByteArray()
        private val transport = RelayTransport()
        val client = ProductionRealtimeSessionClient(
            engine = object : TerminalEngine {
                override val inputEvents: Flow<ByteArray> = MutableSharedFlow()
                override val sizeEvents: Flow<TerminalSize> = MutableSharedFlow()
                override suspend fun feed(seq: Long, bytes: ByteBuffer) = Unit
                override fun resize(columns: Int, rows: Int) = Unit
                override fun applyTheme(theme: TerminalThemeV1) = Unit
                override fun setReadOnly(readOnly: Boolean) = Unit
                override fun clear() = Unit
                override fun dispose() = Unit
            },
            config = ProductionRealtimeSessionConfig("https://example.test", AccessTokenProvider { "token" },
                androidId, pcId, sessionId, 7, pcIdentity.exchange.publicKey.copyOf(),
                keyId = envelopeKeyId.copyOf(), negotiateTerminalState = true),
            identity = androidIdentity,
            parentScope = scope,
            transport = transport,
            reconnectDelaysMillis = listOf(30_000),
            terminationReadinessTimeoutMillis = 3_000,
            hostHistoryRequestTimeoutMillis = hostHistoryRequestTimeoutMillis,
            acknowledgementObserver = { generation, sequence -> DurableAckCommit(sequence, generation) },
            acknowledgementResetObserver = { generation -> DurableAckCommit(0, generation + 1) },
        )

        suspend fun start() {
            client.start()
            val subscribe = decryptOutbound(awaitSent(MessageType.SUBSCRIBE, 1)) { SubscribePayload.parseFrom(it) }
            val attachment = TerminalAttachResultPayload.newBuilder()
                .setRequestId(subscribe.requestId)
                .setRequiredState(ProductionTerminalStateContract.GRAPHICS_STATE)
                .apply {
                    if (frozenCapability) addHostCapabilities("host_history_frozen_v1")
                        .addHostCapabilities("tlhp_v2")
                }
                .build()
            transport.deliver(encryptInbound(MessageType.TERMINAL_ATTACH_RESULT, attachment.toByteArray(), 900))
            assertEquals(ProductionTerminalProfile.GRAPHICS, client.awaitTerminalProfile())
        }

        suspend fun awaitHistoryRequest(count: Int): HostHistoryRequestPayload =
            decryptOutbound(awaitSent(MessageType.HOST_HISTORY_REQUEST, count)) {
                HostHistoryRequestPayload.parseFrom(it)
            }

        fun historyRequests(): List<HostHistoryRequestPayload> = transport.sent
            .filter { it.messageType == MessageType.HOST_HISTORY_REQUEST }
            .map { decryptOutbound(it) { bytes -> HostHistoryRequestPayload.parseFrom(bytes) } }

        suspend fun awaitHistoryControl(count: Int): HostHistoryControlPayload =
            decryptOutbound(awaitSent(MessageType.HOST_HISTORY_CONTROL, count)) {
                HostHistoryControlPayload.parseFrom(it)
            }

        fun historyControls(): List<HostHistoryControlPayload> = transport.sent
            .filter { it.messageType == MessageType.HOST_HISTORY_CONTROL }
            .map { decryptOutbound(it) { bytes -> HostHistoryControlPayload.parseFrom(bytes) } }

        fun deliverPage(request: HostHistoryRequestPayload, sequence: Long, bytes: ByteArray = byteArrayOf(1),
            captureId: Long = 17, tokenByte: Int = 7) {
            val page = response(bytes).toBuilder()
                .setRequestId(request.requestId)
                .setRequestBeforeLineId(request.beforeLineId)
                .setKeyId(FrozenHistoryCrypto.keyId(androidId, 7,
                    ProductionTerminalStateContract.GRAPHICS_STATE))
                .setFrozenHistory(metadata().toBuilder().setCaptureId(captureId)
                    .setToken(ByteString.copyFrom(ByteArray(32) { tokenByte.toByte() })))
                .build()
            transport.deliver(encryptInbound(MessageType.HOST_HISTORY_RESPONSE, page.toByteArray(), sequence))
        }

        fun deliverRelease(request: HostHistoryRequestPayload, sequence: Long,
            captureId: Long = 17, tokenByte: Int = 7) {
            val reply = HostHistoryResponsePayload.newBuilder()
                .setRequestId(request.requestId)
                .setStatus(HostHistoryResponseStatusV1.HOST_HISTORY_RESPONSE_STATUS_V1_OK)
                .setFrozenHistory(metadata().toBuilder().setLeaseRemainingMs(0).setCaptureId(captureId)
                    .setToken(ByteString.copyFrom(ByteArray(32) { tokenByte.toByte() })))
                .build()
            transport.deliver(encryptInbound(MessageType.HOST_HISTORY_RESPONSE, reply.toByteArray(), sequence))
        }

        fun deliverRejectedRelease(request: HostHistoryRequestPayload, sequence: Long) {
            val reply = HostHistoryResponsePayload.newBuilder()
                .setRequestId(request.requestId)
                .setStatus(HostHistoryResponseStatusV1.HOST_HISTORY_RESPONSE_STATUS_V1_REJECTED)
                .setErrorCode("STALE_CAPTURE")
                .build()
            transport.deliver(encryptInbound(MessageType.HOST_HISTORY_RESPONSE, reply.toByteArray(), sequence))
        }

        fun deliverPageBudget(request: HostHistoryRequestPayload, sequence: Long) {
            val reply = HostHistoryResponsePayload.newBuilder()
                .setRequestId(request.requestId)
                .setStatus(HostHistoryResponseStatusV1.HOST_HISTORY_RESPONSE_STATUS_V1_REJECTED)
                .setErrorCode("PAGE_BUDGET")
                .setFrozenHistory(metadata().toBuilder().setRequiredCells(65_537))
                .build()
            transport.deliver(encryptInbound(MessageType.HOST_HISTORY_RESPONSE, reply.toByteArray(), sequence))
        }

        fun deliverAnchorChanged(request: HostHistoryRequestPayload, sequence: Long, available: Long) {
            val reply = HostHistoryResponsePayload.newBuilder()
                .setRequestId(request.requestId)
                .setStatus(HostHistoryResponseStatusV1.HOST_HISTORY_RESPONSE_STATUS_V1_REJECTED)
                .setErrorCode("ANCHOR_CHANGED").setSnapshotSeq(available)
                .build()
            transport.deliver(encryptInbound(MessageType.HOST_HISTORY_RESPONSE, reply.toByteArray(), sequence))
        }

        private suspend fun awaitSent(type: MessageType, count: Int): Envelope = withTimeout(3_000) {
            while (transport.sent.count { it.messageType == type } < count) yield()
            transport.sent.filter { it.messageType == type }[count - 1]
        }

        private fun <T> decryptOutbound(envelope: Envelope, parse: (ByteArray) -> T): T {
            val pairwise = SessionCrypto.derivePairwiseKey(androidIdentity.exchange.privateKey,
                pcIdentity.exchange.publicKey, pcId, androidId, 7)
            val sessionKey = SessionCrypto.deriveSessionKey(pairwise, sessionId,
                CryptoDirection.ANDROID_TO_PC, envelopeKeyId)
            val plaintext = ProductionRealtimeClient.decryptEnvelope(envelope, sessionKey)
            return try { parse(plaintext) } finally {
                plaintext.fill(0); sessionKey.fill(0); pairwise.fill(0)
            }
        }

        private fun encryptInbound(type: MessageType, payload: ByteArray, sequence: Long): Envelope {
            val pairwise = SessionCrypto.derivePairwiseKey(pcIdentity.exchange.privateKey,
                androidIdentity.exchange.publicKey, pcId, androidId, 7)
            val sessionKey = SessionCrypto.deriveSessionKey(pairwise, sessionId,
                CryptoDirection.PC_TO_ANDROID, envelopeKeyId)
            val nonce = SessionCrypto.nonce(byteArrayOf(7, 6, 5, 4), sequence.toULong())
            return try {
                ProductionRealtimeClient.encryptedEnvelope(sessionId, pcId, type,
                    CryptoDirection.PC_TO_ANDROID, sequence, 0, envelopeKeyId, nonce,
                    sessionKey, payload, androidId)
            } finally {
                payload.fill(0); nonce.fill(0); sessionKey.fill(0); pairwise.fill(0)
            }
        }

        override fun close() {
            client.close()
            scope.cancel()
            androidIdentity.destroy()
            pcIdentity.destroy()
            envelopeKeyId.fill(0)
        }
    }

    private class RelayTransport : ProductionRealtimeSessionTransport {
        private val incoming = Channel<ProductionReceivedEnvelope>(Channel.UNLIMITED,
            onUndeliveredElement = ProductionReceivedEnvelope::close)
        private val mutableState = MutableSharedFlow<RealtimeState>(replay = 1, extraBufferCapacity = 4)
        override val receivedEnvelopes: Flow<ProductionReceivedEnvelope> = incoming.receiveAsFlow()
        override val receivePressure = MutableStateFlow(ProductionReceivePressure(0, false, 0, 0))
        override val state = mutableState
        override val negotiatedCapabilities = MutableStateFlow(ProductionRealtimeNegotiatedCapabilities(1,
            setOf(ProductionTerminalStateContract.ATTACH, ProductionTerminalStateContract.GEOMETRY,
                ProductionTerminalStateContract.THEME, ProductionTerminalStateContract.HOST_RESIZE)))
        val sent = CopyOnWriteArrayList<Envelope>()
        override fun connect() { check(mutableState.tryEmit(RealtimeState.Connected)) }
        override fun send(envelope: Envelope): Boolean { sent += envelope; return true }
        override fun sendRelayExact(envelope: Envelope, exactBytes: ByteArray): Boolean {
            sent += Envelope.parseFrom(exactBytes); return true
        }
        override fun sendRelayExactForConnectedGeneration(envelope: Envelope, exactBytes: ByteArray,
            expectedGeneration: Int): Boolean = if (expectedGeneration == 1) sendRelayExact(envelope, exactBytes) else false
        override fun disconnect(reason: String) = Unit
        override fun currentConnectedGeneration(): Int = 1
        override fun disconnectIfCurrentGeneration(expectedGeneration: Int, reason: String): Boolean = expectedGeneration == 1
        fun deliver(envelope: Envelope) {
            check(incoming.trySend(ProductionReceivedEnvelope(envelope, envelope.serializedSize, 1, {})).isSuccess)
        }
        override fun close() { incoming.close() }
    }
}
