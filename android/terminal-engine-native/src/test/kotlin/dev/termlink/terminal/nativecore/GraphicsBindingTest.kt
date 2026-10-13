package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalOutputProfile
import dev.termlink.terminal.api.TerminalRenderFrameV5
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalWidthProfile
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.zip.CRC32C
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertSame
import kotlin.test.assertTrue
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class GraphicsBindingTest {
    @Test
    fun explicitProfileSelectsGraphicsCreationRenderingAndExport() {
        val bridge = GraphicsTestBridge()
        graphicsHandle(bridge).use { terminal ->
            assertTrue(terminal.graphicsProfile)
            assertEquals(1, bridge.graphicsCreates)
            assertIs<TerminalRenderFrameV5>(terminal.renderTextAtScrollback(0).renderFrame)
            assertIs<TerminalRenderFrameV5>(terminal.renderV5())
            assertEquals(2, bridge.graphicsRenders)
            assertEquals(0, bridge.legacyRenders)
            assertEquals(104, terminal.exportCheckpoint().payloadByteCount)
            assertEquals(1, bridge.graphicsExports)
            assertEquals(TERMLINK_GRAPHICS_SNAPSHOT_MAX_BYTES, bridge.exportLimit)
        }
    }

    @Test
    fun defaultEngineStaysOnLegacyCreationAndRenderV4() {
        val bridge = GraphicsTestBridge()
        val engine = NativeTerminalEngine(TerminalSize(2, 1), bridge)
        try {
            assertFalse(engine.graphicsProfile)
            assertEquals(4, engine.frames.value.formatVersion)
            assertEquals(0, bridge.graphicsCreates)
            assertEquals(0, bridge.graphicsRenders)
            assertEquals(1, bridge.legacyRenders)
        } finally { engine.dispose() }
    }

    @Test
    fun missingGraphicsDiscoveryRejectsBeforeAllocation() {
        for (missing in listOf(false, true)) {
            val delegate = GraphicsTestBridge()
            val bridge = object : NativeBridge by delegate {
                override val renderFrameV5FormatVersion: Int
                    get() = if (missing) throw UnsatisfiedLinkError("old SO") else 0
            }
            assertEquals(NativeTerminalStatus.UNSUPPORTED,
                assertFailsWith<NativeTerminalException> { graphicsHandle(bridge) }.status)
            assertEquals(0, delegate.graphicsCreates)
        }
    }

    @Test
    fun snapshotProfilesMustMatchBeforeCrossingTheBridge() {
        val bridge = GraphicsTestBridge()
        assertEquals(NativeTerminalStatus.INCOMPATIBLE_SNAPSHOT,
            assertFailsWith<NativeTerminalException> { NativeTerminalHandle.importCheckpoint(bridge, graphicsSnapshot()) }.status)
        assertEquals(NativeTerminalStatus.INCOMPATIBLE_SNAPSHOT,
            assertFailsWith<NativeTerminalException> { NativeTerminalHandle.importCheckpoint(bridge, byteArrayOf(1), graphicsProfile = true) }.status)
        assertEquals(0, bridge.graphicsImports)
        NativeTerminalHandle.importCheckpoint(bridge, graphicsSnapshot(), graphicsProfile = true).use {
            assertTrue(it.graphicsProfile)
            assertEquals(2L, it.nextSourceSequence)
        }
        assertEquals(1, bridge.graphicsImports)
    }

    @Test
    fun malformedGraphicsReplacementIsClosedAndNeverInstalled() {
        val bridge = GraphicsTestBridge()
        val engine = NativeTerminalEngine(TerminalSize(2, 1), bridge, graphicsProfile = true)
        try {
            val original = engine.frames.value
            bridge.malformedImportedRender = true
            assertFailsWith<NativeTerminalContractException> { engine.installCheckpoint(graphicsSnapshot()) }
            assertSame(original, engine.frames.value)
            assertEquals(1, bridge.fake.destroyCalls)
            assertEquals(0, bridge.legacyRenders)
            bridge.malformedImportedRender = false
            assertEquals(1L, engine.installCheckpoint(graphicsSnapshot()).sequence)
            assertIs<TerminalRenderFrameV5>(engine.frames.value)
        } finally { engine.dispose() }
    }

    @Test
    fun renderFailureDoesNotFallBackToLegacyFrame() {
        val bridge = GraphicsTestBridge()
        graphicsHandle(bridge).use { terminal ->
            bridge.renderFailure = NativeTerminalStatus.RENDER_FRAME_VERSION_REQUIRED
            assertEquals(NativeTerminalStatus.RENDER_FRAME_VERSION_REQUIRED,
                assertFailsWith<NativeTerminalException> { terminal.renderTextAtScrollback(0) }.status)
            assertEquals(0, bridge.legacyRenders)
        }
    }

    @Test
    fun expandedLimitsRequireExplicitProfileAndRequestedBoundsRemainEnforced() {
        assertFailsWith<IllegalArgumentException> { NativeTerminalLimits(maxCheckpointBytes = 64 * 1024 * 1024 + 1) }
        assertFailsWith<IllegalArgumentException> { NativeTerminalLimits(maxRenderFrameBytes = 32 * 1024 * 1024 + 1) }
        assertEquals(TERMLINK_GRAPHICS_SNAPSHOT_MAX_BYTES, NativeTerminalLimits.graphics().maxCheckpointBytes)
        assertEquals(TERMLINK_RENDER_FRAME_V5_MAX_BYTES, NativeTerminalLimits.graphics().maxRenderFrameBytes)
        assertFailsWith<IllegalArgumentException> { NativeTerminalLimits.graphics().copy(maxCheckpointBytes = TERMLINK_GRAPHICS_SNAPSHOT_MAX_BYTES + 1) }
        assertFailsWith<IllegalArgumentException> { NativeTerminalHandle.create(GraphicsTestBridge(), TerminalSize(2, 1), 1, NativeTerminalLimits.graphics()) }
        graphicsHandle(GraphicsTestBridge()).use { terminal ->
            assertFailsWith<NativeTerminalContractException> { terminal.renderV5(maxPayloadBytes = 64) }
            assertFailsWith<NativeTerminalContractException> { terminal.exportCheckpoint(maxPayloadBytes = 1) }
        }
        assertFailsWith<IllegalArgumentException> {
            NativeTerminalHandle.importCheckpoint(GraphicsTestBridge(), graphicsSnapshot(),
                NativeTerminalLimits.graphics().copy(maxCheckpointBytes = 11), graphicsProfile = true)
        }
    }

    @Test
    fun graphicsJniUsesFlagsIntAndPreservesTheExistingLongBounds() {
        val methods = JniNativeMethods::class.java.declaredMethods.associateBy { it.name }
        assertEquals(listOf(Long::class.javaPrimitiveType, Int::class.javaPrimitiveType, Array<ByteArray?>::class.java),
            methods.getValue("renderV5").parameterTypes.toList())
        assertEquals(listOf(Long::class.javaPrimitiveType, Long::class.javaPrimitiveType, Int::class.javaPrimitiveType,
            LongArray::class.java, Array<ByteArray?>::class.java), methods.getValue("renderV5AtScrollback").parameterTypes.toList())
        assertEquals(Long::class.javaPrimitiveType, methods.getValue("maxGraphicsSnapshotBytes").returnType)
        assertEquals(listOf(LongArray::class.java, LongArray::class.java), methods.getValue("createGraphicsV1").parameterTypes.toList())
        assertEquals(listOf(ByteArray::class.java, LongArray::class.java), methods.getValue("importSnapshotGraphicsV1").parameterTypes.toList())
    }
}

private fun graphicsHandle(bridge: NativeBridge): NativeTerminalHandle = NativeTerminalHandle.createWithProfiles(
    bridge, TerminalSize(2, 1), 1, TerminalOutputProfile.UTF8, TerminalWidthProfile.LEGACY_V1, graphicsProfile = true,
)

internal class GraphicsTestBridge(val fake: FakeNativeBridge = FakeNativeBridge()) : NativeBridge by fake {
    override val configV4StructSize = TERMLINK_CONFIG_V4_STRUCT_SIZE
    override val renderFrameV4FormatVersion = 4
    override val maxRenderFrameV4Bytes = TERMLINK_RENDER_FRAME_V4_MAX_BYTES
    override val renderFrameV5FormatVersion = 5
    override val maxRenderFrameV5Bytes = TERMLINK_RENDER_FRAME_V5_MAX_BYTES
    override val maxGraphicsSnapshotBytes = TERMLINK_GRAPHICS_SNAPSHOT_MAX_BYTES
    var graphicsCreates = 0
    var graphicsImports = 0
    var graphicsRenders = 0
    var legacyRenders = 0
    var graphicsExports = 0
    var exportLimit = 0
    var malformedImportedRender = false
    var renderFailure: NativeTerminalStatus? = null
    private val revisions = mutableMapOf<NativeBridgeHandle, Long>()

    override fun createGraphicsV1(config: NativeTerminalConfigV4): NativeBridgeResult<NativeBridgeTerminal> {
        graphicsCreates++
        return fake.create(TerminalSize(config.columns, config.rows), config.initialSourceSequence).also {
            if (it is NativeBridgeResult.Success) revisions[it.value.handle] = it.value.nextSourceSequence
        }
    }
    override fun importSnapshotGraphicsV1(checkpoint: ByteArray): NativeBridgeResult<NativeBridgeTerminal> {
        graphicsImports++
        return fake.create(TerminalSize(2, 1), 2).also {
            if (it is NativeBridgeResult.Success) revisions[it.value.handle] = 2
        }
    }
    override fun renderV5(handle: NativeBridgeHandle, request: NativeRenderRequest): NativeBridgeResult<NativeBridgeRenderFrame> {
        graphicsRenders++
        renderFailure?.let { return NativeBridgeResult.Failure(it.code) }
        val revision = revisions.getValue(handle)
        return NativeBridgeResult.Success(NativeBridgeRenderFrame(
            if (malformedImportedRender && revision == 2L) graphicsTextFrame(revision) else graphicsFrame(revision)))
    }
    override fun renderV5AtScrollback(handle: NativeBridgeHandle, offsetRows: Long, request: NativeRenderRequest): NativeBridgeResult<NativeBridgeScrollbackFrame> =
        when (val result = renderV5(handle, request)) {
            is NativeBridgeResult.Success -> NativeBridgeResult.Success(NativeBridgeScrollbackFrame(result.value.payload, 0, 0, 0))
            is NativeBridgeResult.Failure -> result
        }
    override fun renderV4AtScrollback(handle: NativeBridgeHandle, offsetRows: Long, request: NativeRenderRequest): NativeBridgeResult<NativeBridgeScrollbackFrame> {
        legacyRenders++
        return NativeBridgeResult.Success(NativeBridgeScrollbackFrame(graphicsTextFrame(1), 0, 0, 0))
    }
    override fun exportCheckpointGraphicsV1(handle: NativeBridgeHandle, maxPayloadBytes: Int): NativeBridgeResult<NativeBridgeCheckpoint> {
        graphicsExports++
        exportLimit = maxPayloadBytes
        val revision = revisions.getValue(handle)
        return NativeBridgeResult.Success(NativeBridgeCheckpoint(revision, revision, graphicsSnapshot()))
    }
}

// This bridge stub models the fixed header only; production imports still require native validation.
internal fun graphicsSnapshot(): ByteArray = ByteArray(104).also {
    "TLSNP2S\u0000".toByteArray().copyInto(it)
    graphicsPut16(it, 8, 2); graphicsPut16(it, 10, 22); graphicsPut16(it, 12, 22); graphicsPut16(it, 14, 104)
}

internal fun graphicsFrame(revision: Long = 7): ByteArray {
    val text = graphicsTextFrame(revision)
    val resources = ByteArray(48)
    "TLGRS1\u0000\u0000".toByteArray().copyInto(resources)
    graphicsPut16(resources, 8, 1); graphicsPut16(resources, 10, 48); graphicsPut32(resources, 12, 48)
    graphicsPut64(resources, 16, 1); graphicsSeal(resources, 40)
    return ByteArray(64 + text.size + resources.size).also {
        "TLRFRM5\u0000".toByteArray().copyInto(it)
        graphicsPut16(it, 8, 5); graphicsPut16(it, 10, 64); graphicsPut32(it, 12, it.size); graphicsPut64(it, 16, revision)
        graphicsPut32(it, 24, 2); graphicsPut32(it, 28, 1); graphicsPut32(it, 32, text.size); graphicsPut32(it, 36, resources.size)
        graphicsPut32(it, 48, 7); text.copyInto(it, 64); resources.copyInto(it, 64 + text.size); graphicsSeal(it, 52)
    }
}

internal fun graphicsTextFrame(revision: Long = 7): ByteArray {
    val graphemes = ByteArray(9).also { graphicsPut32(it, 0, 8); graphicsPut16(it, 4, 1); it[6] = 1; it[8] = 32 }
    val rows = ByteArray(40).also { graphicsPut32(it, 0, 8); graphicsPut32(it, 4, 12); graphicsPut32(it, 16, 1); graphicsPut32(it, 28, 1) }
    val sections = listOf(graphemes, ByteArray(16), ByteArray(264 * 4) { -1 }, ByteArray(0), rows, ByteArray(0))
    val counts = listOf(1, 1, 264, 0, 2, 0)
    val records = listOf(8, 16, 4, 16, 0, 0)
    var length = 240
    val offsets = sections.map { data -> length.also { length = (length + data.size + 7) and -8 } }
    return ByteArray(length).also { frame ->
        "TLRFRM4\u0000".toByteArray().copyInto(frame)
        graphicsPut16(frame, 8, 4); graphicsPut16(frame, 12, 96); graphicsPut16(frame, 14, 24); graphicsPut16(frame, 16, 6)
        graphicsPut32(frame, 20, length); graphicsPut64(frame, 24, revision); graphicsPut64(frame, 32, revision)
        graphicsPut32(frame, 40, 2); graphicsPut32(frame, 44, 1); graphicsPut32(frame, 60, 1); graphicsPut32(frame, 64, 96); graphicsPut64(frame, 72, 0x7ff)
        sections.forEachIndexed { index, data ->
            val directory = 96 + index * 24
            graphicsPut32(frame, directory, index + 1); graphicsPut16(frame, directory + 4, if (index == 1) 2 else 1); graphicsPut16(frame, directory + 6, 1)
            graphicsPut32(frame, directory + 8, offsets[index]); graphicsPut32(frame, directory + 12, data.size)
            graphicsPut32(frame, directory + 16, counts[index]); graphicsPut32(frame, directory + 20, records[index])
            data.copyInto(frame, offsets[index])
        }
        graphicsPut32(frame, 88, CRC32C().apply { update(frame, 96, frame.size - 96) }.value.toInt())
        graphicsPut32(frame, 92, CRC32C().apply { update(frame, 0, 92) }.value.toInt())
    }
}

internal fun graphicsPut16(bytes: ByteArray, offset: Int, value: Int) { ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).putShort(offset, value.toShort()) }
internal fun graphicsPut32(bytes: ByteArray, offset: Int, value: Int) { ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).putInt(offset, value) }
internal fun graphicsPut64(bytes: ByteArray, offset: Int, value: Long) { ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).putLong(offset, value) }
internal fun graphicsSeal(bytes: ByteArray, checksumOffset: Int) {
    graphicsPut32(bytes, checksumOffset, CRC32C().apply { update(bytes, 0, checksumOffset); update(bytes, checksumOffset + 4, bytes.size - checksumOffset - 4) }.value.toInt())
}
