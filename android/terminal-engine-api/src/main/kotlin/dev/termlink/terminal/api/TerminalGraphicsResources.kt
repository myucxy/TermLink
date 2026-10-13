package dev.termlink.terminal.api

import java.nio.ByteBuffer
import java.util.Collections

/** Canonical, unpremultiplied resource pixels. A8 is a single alpha mask channel. */
enum class TerminalGraphicsFormat(val bytesPerPixel: Int) {
    RGBA8888(4),
    A8(1),
}

/** An immutable resource. [id] preserves all bits of the nonzero unsigned wire ID. */
class TerminalGraphicsResource(
    val id: Long,
    val width: Int,
    val height: Int,
    val format: TerminalGraphicsFormat,
    pixels: ByteBuffer,
) {
    init {
        require(id != 0L) { "graphics resource ID must be nonzero" }
        require(width in 1..MAX_DIMENSION && height in 1..MAX_DIMENSION) {
            "graphics resource dimensions must be 1..$MAX_DIMENSION"
        }
        val expected = width.toLong() * height * format.bytesPerPixel
        require(expected <= MAX_PIXEL_BYTES && expected == pixels.remaining().toLong()) {
            "graphics pixels must match dimensions and fit the 16 MiB resource limit"
        }
    }

    private val ownedPixels = ByteArray(pixels.remaining()).also { pixels.duplicate().get(it) }

    constructor(id: Long, width: Int, height: Int, format: TerminalGraphicsFormat, pixels: ByteArray) :
        this(id, width, height, format, ByteBuffer.wrap(pixels))

    val pixelByteCount: Int get() = ownedPixels.size

    /** Compares immutable pixel data and its interpretation without copying either array. */
    fun sameContent(other: TerminalGraphicsResource): Boolean = this === other ||
        (width == other.width && height == other.height && format == other.format &&
            ownedPixels.contentEquals(other.ownedPixels))

    fun copyPixels(): ByteArray = ownedPixels.copyOf()

    /** Each view has independent position/limit; neither writes nor array access are allowed. */
    fun readOnlyPixels(): ByteBuffer = ByteBuffer.wrap(ownedPixels).asReadOnlyBuffer()

    companion object {
        const val MAX_DIMENSION = 4096
        const val MAX_PIXEL_BYTES = 16 * 1024 * 1024
    }
}

/** Immutable TLGRS1 resources; IDs use unsigned ordering and are never remapped on decode. */
class TerminalGraphicsResources(val nextId: Long, entries: List<TerminalGraphicsResource>) {
    init {
        require(nextId != 0L) { "next graphics ID must be nonzero" }
        require(entries.size <= MAX_RESOURCES) { "graphics resource count exceeds $MAX_RESOURCES" }
    }

    val entries: List<TerminalGraphicsResource> = Collections.unmodifiableList(ArrayList(entries))
    val pixelBytes: Int

    init {
        var previous = 0L
        var total = 0L
        for (entry in this.entries) {
            require(java.lang.Long.compareUnsigned(entry.id, previous) > 0 &&
                java.lang.Long.compareUnsigned(entry.id, nextId) < 0) {
                "graphics IDs must be strictly increasing and less than next ID"
            }
            total += entry.pixelByteCount
            require(total <= MAX_PIXEL_BYTES) { "graphics resources exceed the 32 MiB session limit" }
            previous = entry.id
        }
        pixelBytes = total.toInt()
    }

    operator fun get(id: Long): TerminalGraphicsResource? {
        val index = entries.binarySearch { java.lang.Long.compareUnsigned(it.id, id) }
        return if (index < 0) null else entries[index]
    }

    companion object {
        const val MAX_RESOURCES = 256
        const val MAX_PIXEL_BYTES = 32 * 1024 * 1024
        const val MAX_FRAME_BYTES = 48 + MAX_PIXEL_BYTES + MAX_RESOURCES * 35
    }
}
