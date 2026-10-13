package dev.termlink.terminal.nativecore

import androidx.test.ext.junit.runners.AndroidJUnit4
import dev.termlink.terminal.api.TerminalGraphicsFormat
import dev.termlink.terminal.api.TerminalHostGeometryV1
import dev.termlink.terminal.api.TerminalOutputProfile
import dev.termlink.terminal.api.TerminalRenderFrameV5
import dev.termlink.terminal.api.TerminalRenderImageProtocolV5
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalWidthProfile
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

/** Real JNI, using repository-owned public Output vectors from the core graphics interop example. */
@RunWith(AndroidJUnit4::class)
class NativeGraphicsIntegrationTest {
    private val bridge = JniNativeBridge

    @Test
    fun kittyCropSurvivesNativeCloseAndSnapshotRestore() {
        roundtrip(TerminalSize(12, 4), TerminalHostGeometryV1(cellWidth = 2, cellHeight = 6),
            "\u001b_Ga=T,f=32,s=2,v=2,i=17,x=1,y=0,w=1,h=2,c=2,r=3,z=-7,C=1;/wAA/wD/AP8AAP//////gA==\u001b\\" +
                "\u001b[4;1H\n") { frame ->
            val image = frame.images.single()
            assertEquals(TerminalRenderImageProtocolV5.KITTY, image.protocol)
            assertEquals(-7, image.z)
            assertEquals(-1, image.row)
            assertEquals(1, image.source.x)
            assertEquals(1, image.source.width)
            assertEquals(2, image.source.height)
            val resource = requireNotNull(frame.resources[image.resourceId])
            assertEquals(TerminalGraphicsFormat.RGBA8888, resource.format)
            assertArrayEquals(byteArrayOf(-1, 0, 0, -1, 0, -1, 0, -1, 0, 0, -1, -1, -1, -1, -1, -128), resource.copyPixels())
        }
    }

    @Test
    fun sixelScrollRegionClipSurvivesNativeCloseAndSnapshotRestore() {
        roundtrip(TerminalSize(12, 4), TerminalHostGeometryV1(cellWidth = 2, cellHeight = 6),
            "\u001b[2;3r\u001b[3;1H\u001bP7;1q#1;2;100;0;0~-~-~-~-~\u001b\\") { frame ->
            val image = frame.images.single()
            assertEquals(TerminalRenderImageProtocolV5.SIXEL, image.protocol)
            assertTrue(image.row < 0 && image.clip.top > 0)
            assertEquals(1, image.row + image.clip.top)
            assertEquals(3, image.row + image.clip.bottom)
            val resource = requireNotNull(frame.resources[image.resourceId])
            assertEquals(TerminalGraphicsFormat.RGBA8888, resource.format)
            val pixels = resource.copyPixels()
            for (offset in pixels.indices step 4) {
                assertArrayEquals(byteArrayOf(-1, 0, 0, -1), pixels.copyOfRange(offset, offset + 4))
            }
        }
    }

    @Test
    fun drcsRedownloadPreservesBothNativeAtlasesAcrossSnapshotRestore() {
        roundtrip(TerminalSize(80, 24), TerminalHostGeometryV1(cellWidth = 10, cellHeight = 16),
            "\u001bP1;1;0;10;1;2;16;0{ @~~~~~~~~~~\u001b\\\u001b( @!\u001b[2b" +
                "\u001bP1;1;0;10;1;2;16;0{ @AAAAAAAAAA\u001b\\!\u001b(B") { frame ->
            assertEquals(4, frame.glyphs.size)
            assertEquals(frame.glyphs[0].resourceId, frame.glyphs[2].resourceId)
            assertTrue(frame.glyphs[0].resourceId != frame.glyphs[3].resourceId)
            frame.glyphs.forEachIndexed { index, glyph ->
                assertEquals(10, glyph.width)
                assertEquals(16, glyph.height)
                val resource = requireNotNull(frame.resources[glyph.resourceId])
                assertEquals(TerminalGraphicsFormat.A8, resource.format)
                val pixels = resource.copyPixels()
                for (row in 0 until glyph.height) {
                    val alpha = if ((index < 3 && row < 6) || (index == 3 && row == 1)) 255 else 0
                    for (column in 0 until glyph.width) {
                        assertEquals(alpha, pixels[row * resource.width + glyph.x + column].toInt() and 255)
                    }
                }
            }
        }
    }

    @Test
    fun nativeGraphicsSnapshotProfileAndLegacyGatesAreStrict() {
        assertEquals(5, bridge.renderFrameV5FormatVersion)
        assertEquals(64 * 1024 * 1024, bridge.maxRenderFrameV5Bytes)
        assertEquals(128 * 1024 * 1024, bridge.maxGraphicsSnapshotBytes)
        val graphics = success(bridge.createGraphicsV1(config()))
        val legacy = success(bridge.createV4(config()))
        try {
            apply(graphics.handle, NativeSourceEvent.HostGeometry(1, GEOMETRY), NativeSourceEvent.Output(2, KITTY))
            apply(legacy.handle, NativeSourceEvent.Output(1, "legacy".encodeToByteArray()))
            val originalFrame = renderBytes(graphics.handle)
            val snapshot = success(bridge.exportCheckpointGraphicsV1(graphics.handle, GRAPHICS_SNAPSHOT_MAX)).payload
            val header = ByteBuffer.wrap(snapshot).order(ByteOrder.LITTLE_ENDIAN)
            assertArrayEquals("TLSNP2S\u0000".encodeToByteArray(), snapshot.copyOfRange(0, 8))
            assertEquals(2, header.getShort(8).toInt())
            assertEquals(22, header.getShort(10).toInt())

            failure(NativeTerminalStatus.INCOMPATIBLE_SNAPSHOT,
                bridge.exportCheckpoint(graphics.handle, LEGACY_SNAPSHOT_MAX))
            failure(NativeTerminalStatus.INCOMPATIBLE_SNAPSHOT, bridge.importCheckpoint(snapshot))
            val oldSnapshot = success(bridge.exportCheckpoint(legacy.handle, LEGACY_SNAPSHOT_MAX)).payload
            failure(NativeTerminalStatus.INCOMPATIBLE_SNAPSHOT, bridge.importSnapshotGraphicsV1(oldSnapshot))
            failure(NativeTerminalStatus.RENDER_FRAME_VERSION_REQUIRED,
                bridge.renderV4(graphics.handle, NativeRenderRequest(TERMLINK_RENDER_FRAME_V4_MAX_BYTES)))
            failure(NativeTerminalStatus.LIMIT_EXCEEDED,
                bridge.exportCheckpointGraphicsV1(graphics.handle, snapshot.size - 1))

            val output = arrayOfNulls<ByteArray>(1)
            assertEquals(NativeTerminalStatus.INVALID_ARGUMENT.code,
                JniNativeMethods.renderV5(graphics.handle.value, 1, output))
            assertNull(output[0])
            val malformed = snapshot.copyOf().also { it[it.lastIndex] = (it.last().toInt() xor 0x80).toByte() }
            failure(NativeTerminalStatus.MALFORMED_SNAPSHOT, bridge.importSnapshotGraphicsV1(malformed))
            failure(NativeTerminalStatus.SEQUENCE_MISMATCH,
                bridge.applyBatch(graphics.handle, NativeEventBatch.of(listOf(NativeSourceEvent.Output(1, "wrong sequence".encodeToByteArray())))))
            assertArrayEquals(originalFrame, renderBytes(graphics.handle))
            assertEquals(4L, apply(graphics.handle, NativeSourceEvent.Output(3, "ok".encodeToByteArray())).nextSourceSequence)
        } finally {
            success(bridge.destroy(graphics.handle))
            success(bridge.destroy(legacy.handle))
        }
    }

    @Test
    fun frozenGraphicsHistorySurvivesResetAndSourceDestruction() {
        val allocation = success(bridge.createGraphicsV1(config(columns = 10, rows = 3, historyRows = 20)))
        var destroyed = false
        var captureInfo: NativeHistoryCaptureInfo? = null
        try {
            apply(allocation.handle,
                NativeSourceEvent.HostGeometry(1, TerminalHostGeometryV1(cellWidth = 10, cellHeight = 20)),
                NativeSourceEvent.Output(2, "\u001b_Ga=T,f=32,s=1,v=1,i=1,c=1,r=1,q=2;AQIDBA==\u001b\\one\r\ntwo\r\nthree\r\nfour\r\nfive\r\n".encodeToByteArray()))
            val info = success(bridge.historyBegin(allocation.handle))
            captureInfo = info
            NativeHistoryCapture(bridge, info, graphicsProfile = true).use { capture ->
                val pageBytes = capture.readBytes()
                val page = capture.read()
                assertEquals(info.revision, page.revision)
                assertTrue(page.lines.isNotEmpty())
                assertTrue(page.frames.all { it is TerminalRenderFrameV5 && !it.cursor.visible })
                assertTrue(page.frames.any { (it as TerminalRenderFrameV5).images.isNotEmpty() })
                failure(NativeTerminalStatus.RENDER_FRAME_VERSION_REQUIRED, bridge.historyPage(info, -1, 128, 65536, 4 * 1024 * 1024))
                failure(NativeTerminalStatus.RENDER_FRAME_VERSION_REQUIRED, bridge.historyViewport(info, MAX_OFFSET))
                val viewport = success(bridge.historyViewportV2(info, MAX_OFFSET))
                val frame = RenderFrameV5Decoder.decode(viewport.payload)
                assertFalse(frame.cursor.visible)
                assertEquals(1, frame.images.size)
                assertEquals(viewport.availableRows, viewport.appliedOffsetRows)
                assertEquals(info.revision, frame.presentationRevision)

                apply(allocation.handle, NativeSourceEvent.Output(3, "\u001bc".encodeToByteArray()))
                success(bridge.destroy(allocation.handle))
                destroyed = true
                assertArrayEquals(pageBytes, capture.readBytes())
                val afterClose = success(bridge.historyViewportV2(info, MAX_OFFSET))
                assertArrayEquals(viewport.payload, afterClose.payload)
                assertEquals(viewport.availableRows, afterClose.availableRows)
                assertEquals(viewport.generationRows, afterClose.generationRows)
                assertEquals(viewport.appliedOffsetRows, afterClose.appliedOffsetRows)
            }
        } finally {
            if (!destroyed) success(bridge.destroy(allocation.handle))
        }
        val released = requireNotNull(captureInfo)
        failure(NativeTerminalStatus.HISTORY_STALE_CAPTURE,
            bridge.historyPageV2(released, -1, 128, 65536, 4 * 1024 * 1024))
        failure(NativeTerminalStatus.HISTORY_STALE_CAPTURE, bridge.historyViewportV2(released, 0))
    }

    @Test
    fun failedNativeSnapshotImportDoesNotReplaceEngineOrConsumeNextSequence() = runBlocking {
        val validSnapshot = createGraphics().use { terminal ->
            terminal.applyWithHostEffectsV2(listOf(NativeSourceEvent.HostGeometry(1, GEOMETRY), NativeSourceEvent.Output(2, KITTY)))
            terminal.exportCheckpoint().copyPayload()
        }
        val oldSnapshot = NativeTerminalHandle.create(bridge, SIZE, 1).use { terminal ->
            terminal.feed(1, "legacy".encodeToByteArray())
            terminal.exportCheckpoint().copyPayload()
        }
        val engine = NativeTerminalEngine(initialSize = SIZE, graphicsProfile = true)
        try {
            engine.applyHostGeometry(1, GEOMETRY)
            engine.feed(2, ByteBuffer.wrap(KITTY))
            val original = engine.frames.value
            assertTrue(original is TerminalRenderFrameV5)
            assertTrue((original as TerminalRenderFrameV5).images.isNotEmpty())
            val malformed = validSnapshot.copyOf().also { it[it.lastIndex] = (it.last().toInt() xor 0x80).toByte() }
            assertNativeFailure(NativeTerminalStatus.MALFORMED_SNAPSHOT) { engine.installCheckpoint(malformed) }
            assertSame(original, engine.frames.value)
            assertNativeFailure(NativeTerminalStatus.INCOMPATIBLE_SNAPSHOT) { engine.installCheckpoint(oldSnapshot) }
            assertSame(original, engine.frames.value)
            engine.feed(3, ByteBuffer.wrap("live".encodeToByteArray()))
            assertEquals(4L, engine.frames.value.nextSourceSequence)
            assertEquals(2L, engine.installCheckpoint(validSnapshot).sequence)
            assertEquals(3L, engine.frames.value.nextSourceSequence)
            assertTrue((engine.frames.value as TerminalRenderFrameV5).images.isNotEmpty())
            engine.feed(3, ByteBuffer.wrap("restored".encodeToByteArray()))
            assertEquals(4L, engine.frames.value.nextSourceSequence)
        } finally { engine.dispose() }
    }

    @Test
    fun validUnicodeWidthSnapshotCannotChangeTheConfiguredEngine() = runBlocking {
        rejectChangedProfile(TerminalWidthProfile.UNICODE_16, TerminalOutputProfile.UTF8)
    }

    @Test
    fun validStandaloneC1SnapshotCannotChangeTheConfiguredEngine() = runBlocking {
        rejectChangedProfile(TerminalWidthProfile.LEGACY_V1, TerminalOutputProfile.UTF8_WITH_STANDALONE_C1)
    }

    private suspend fun rejectChangedProfile(width: TerminalWidthProfile, output: TerminalOutputProfile) {
        val snapshot = createGraphics(width = width, output = output).use { terminal ->
            terminal.applyWithHostEffectsV2(listOf(NativeSourceEvent.HostGeometry(1, GEOMETRY), NativeSourceEvent.Output(2, KITTY)))
            terminal.exportCheckpoint().copyPayload()
        }
        // These are valid native snapshots; rejection belongs to the engine's immutable configuration.
        NativeTerminalHandle.importCheckpoint(bridge, snapshot, graphicsProfile = true).use { imported ->
            assertEquals(width, imported.renderV5().widthProfile)
            assertArrayEquals(snapshot, imported.exportCheckpoint().copyPayload())
        }
        val engine = NativeTerminalEngine(initialSize = SIZE, graphicsProfile = true)
        try {
            engine.applyHostGeometry(1, GEOMETRY)
            engine.feed(2, ByteBuffer.wrap(KITTY))
            engine.setReadOnly(false)
            val original = engine.frames.value
            assertNativeFailure(NativeTerminalStatus.INCOMPATIBLE_SNAPSHOT) { engine.installCheckpoint(snapshot) }
            assertSame(original, engine.frames.value)
            assertTrue(engine.canAcceptInput())
            engine.feed(3, ByteBuffer.wrap("still live".encodeToByteArray()))
            assertEquals(4L, engine.frames.value.nextSourceSequence)
            assertEquals(TerminalWidthProfile.LEGACY_V1, engine.frames.value.widthProfile)
            assertTrue((engine.frames.value as TerminalRenderFrameV5).images.isNotEmpty())
        } finally { engine.dispose() }
    }

    private fun roundtrip(size: TerminalSize, geometry: TerminalHostGeometryV1, output: String,
        verify: (TerminalRenderFrameV5) -> Unit) {
        val (snapshot, original) = createGraphics(size, TerminalWidthProfile.UNICODE_16).use { terminal ->
            terminal.applyWithHostEffectsV2(listOf(NativeSourceEvent.HostGeometry(1, geometry), NativeSourceEvent.Output(2, output.encodeToByteArray())))
            val frame = terminal.renderV5()
            verify(frame)
            terminal.exportCheckpoint().copyPayload() to frame
        }
        NativeTerminalHandle.importCheckpoint(bridge, snapshot, graphicsProfile = true).use { restored ->
            val frame = restored.renderV5()
            verify(frame)
            assertEquals(original.nextSourceSequence, frame.nextSourceSequence)
            assertEquals(original.widthProfile, frame.widthProfile)
            assertEquals(frameText(original), frameText(frame))
            assertEquals(original.images, frame.images)
            assertEquals(original.glyphs, frame.glyphs)
            assertArrayEquals(snapshot, restored.exportCheckpoint().copyPayload())
            assertTrue(restored.renderTextAtScrollback(0).renderFrame is TerminalRenderFrameV5)
        }
    }

    private fun createGraphics(size: TerminalSize = SIZE, width: TerminalWidthProfile = TerminalWidthProfile.LEGACY_V1,
        output: TerminalOutputProfile = TerminalOutputProfile.UTF8): NativeTerminalHandle = NativeTerminalHandle.createWithProfiles(
        bridge, size, 1, output, width,
        historyCapacityRows = 128, graphicsProfile = true,
    )

    private fun config(columns: Int = SIZE.columns, rows: Int = SIZE.rows, historyRows: Long = 128): NativeTerminalConfigV4 = NativeTerminalConfigV4(
        columns = columns, rows = rows, initialSourceSequence = 1, historyCapacityRows = historyRows,
        outputProfile = NATIVE_OUTPUT_PROFILE_UTF8, widthProfile = TerminalWidthProfile.LEGACY_V1.wireValue,
    )

    private fun apply(handle: NativeBridgeHandle, vararg events: NativeSourceEvent): NativeBridgeApplyReceipt =
        success(bridge.applyBatchWithHostEffectsV2(handle, NativeEventBatch.of(events.toList()))).receipt

    private fun renderBytes(handle: NativeBridgeHandle): ByteArray =
        success(bridge.renderV5(handle, NativeRenderRequest(TERMLINK_RENDER_FRAME_V5_MAX_BYTES))).payload

    private fun <T : Any> success(result: NativeBridgeResult<T>): T = when (result) {
        is NativeBridgeResult.Success -> result.value
        is NativeBridgeResult.Failure -> throw AssertionError("JNI returned ${NativeTerminalStatus.fromCode(result.statusCode)} (${result.statusCode})")
    }

    private fun failure(status: NativeTerminalStatus, result: NativeBridgeResult<*>) {
        if (result is NativeBridgeResult.Success && result.value is NativeBridgeTerminal) bridge.destroy(result.value.handle)
        assertTrue("Expected native $status, received $result", result is NativeBridgeResult.Failure)
        assertEquals(status.code, (result as NativeBridgeResult.Failure).statusCode)
    }

    private fun assertNativeFailure(status: NativeTerminalStatus, block: () -> Unit) {
        try { block(); throw AssertionError("Expected native $status") }
        catch (error: NativeTerminalException) { assertEquals(status, error.status) }
    }

    private companion object {
        val SIZE = TerminalSize(8, 4)
        val GEOMETRY = TerminalHostGeometryV1(cellWidth = 8, cellHeight = 16)
        const val GRAPHICS_SNAPSHOT_MAX = 128 * 1024 * 1024
        const val LEGACY_SNAPSHOT_MAX = 64 * 1024 * 1024
        const val MAX_OFFSET = 0xffff_ffffL
        val KITTY = "\u001b_Ga=T,f=32,s=1,v=1,i=1,q=2;/wAA/w==\u001b\\".encodeToByteArray()
    }
}
