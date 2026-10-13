package dev.termlink.terminal.api

import java.math.BigInteger
import java.util.Collections

/** Canonical cell-unit ratio, without rounding to a platform floating-point coordinate. */
data class TerminalRenderRatioV5(val numerator: Long, val denominator: Long) {
    init {
        require(numerator in 0..(1L shl 48) && denominator in 1..(1L shl 32)) { "invalid graphics ratio bounds" }
        require(BigInteger.valueOf(numerator).gcd(BigInteger.valueOf(denominator)) == BigInteger.ONE) {
            "graphics ratio must be reduced"
        }
    }
}

data class TerminalRenderSourceV5(val x: Int, val y: Int, val width: Int, val height: Int) {
    init {
        require(x in 0..4095 && y in 0..4095 && width in 1..4096 && height in 1..4096) { "invalid source rectangle" }
    }

    fun fits(width: Int, height: Int): Boolean = x.toLong() + this.width <= width && y.toLong() + this.height <= height
}

/** Half-open cell bounds relative to the image allocation. */
data class TerminalRenderClipV5(val left: Int, val top: Int, val right: Int, val bottom: Int) {
    init {
        require(left in 0 until right && top in 0 until bottom && right <= 4096 && bottom <= 4096) {
            "invalid graphics clip"
        }
    }
}

enum class TerminalRenderImageProtocolV5 { SIXEL, KITTY }

data class TerminalRenderImageV5(
    val placementId: Long,
    val resourceId: Long,
    val column: Int,
    val row: Int,
    val z: Int,
    val imageId: Long,
    val source: TerminalRenderSourceV5,
    val x: TerminalRenderRatioV5,
    val y: TerminalRenderRatioV5,
    val width: TerminalRenderRatioV5,
    val height: TerminalRenderRatioV5,
    val allocatedColumns: Int,
    val allocatedRows: Int,
    val protocol: TerminalRenderImageProtocolV5,
    val clip: TerminalRenderClipV5,
) : Comparable<TerminalRenderImageV5> {
    init {
        require(placementId != 0L && resourceId != 0L) { "image IDs must be nonzero" }
        require(column in -4096..4096 && row in -4096..4096) { "image origin is out of range" }
        require(imageId in 0..0xffff_ffffL && (protocol != TerminalRenderImageProtocolV5.SIXEL || imageId == 0L)) {
            "invalid protocol image ID"
        }
        require(allocatedColumns in 1..4096 && allocatedRows in 1..4096 &&
            clip.right <= allocatedColumns && clip.bottom <= allocatedRows) { "clip exceeds image allocation" }
        require(width.numerator != 0L && height.numerator != 0L) { "image extent must be positive" }
        require(fitsAllocation(x, width, allocatedColumns) && fitsAllocation(y, height, allocatedRows)) {
            "image destination exceeds allocation"
        }
    }

    fun intersectsViewport(size: TerminalSize): Boolean = column + clip.right > 0 && row + clip.bottom > 0 &&
        column + clip.left < size.columns && row + clip.top < size.rows

    override fun compareTo(other: TerminalRenderImageV5): Int {
        val depth = z.compareTo(other.z)
        if (depth != 0) return depth
        val image = imageId.compareTo(other.imageId)
        return if (image != 0) image else java.lang.Long.compareUnsigned(placementId, other.placementId)
    }
}

/** DRCS atlas y is always zero; style and color come from this cell in the embedded text frame. */
data class TerminalRenderGlyphV5(
    val column: Int,
    val row: Int,
    val resourceId: Long,
    val x: Int,
    val width: Int,
    val height: Int,
) : Comparable<TerminalRenderGlyphV5> {
    init {
        require(column >= 0 && row >= 0 && resourceId != 0L && x in 0..4095 && width in 1..4096 && height in 1..4096) {
            "invalid glyph reference"
        }
    }

    override fun compareTo(other: TerminalRenderGlyphV5): Int =
        row.compareTo(other.row).takeIf { it != 0 } ?: column.compareTo(other.column)

    fun targetsSingleCell(text: TerminalRenderFrameV4): Boolean {
        val targetRow = text.rows.getOrNull(row) ?: return false
        if (column >= targetRow.lineRendition.columns(text.size.columns)) return false
        val cell = targetRow.cells.getOrNull(column) ?: return false
        val grapheme = text.graphemes.getOrNull(cell.graphemeId - 1) ?: return false
        return !cell.wideContinuation && grapheme.displayWidth == 1 &&
            grapheme.text.codePointCount(0, grapheme.text.length) == 1 && grapheme.text.codePointAt(0) !in 0xd800..0xdfff
    }
}

/** Experimental V5 presentation. This type does not enable a terminal graphics capability. */
class TerminalRenderFrameV5(
    val text: TerminalRenderFrameV4,
    val resources: TerminalGraphicsResources,
    images: List<TerminalRenderImageV5>,
    glyphs: List<TerminalRenderGlyphV5>,
) : TerminalTextFrame(5, text.widthProfile, text.presentationRevision, text.nextSourceSequence, text.size,
    text.activeScreen, text.reverseVideo, text.cursor, text.graphemes, text.renditions, text.palette,
    text.hyperlinks, text.rows, text.title) {
    init {
        require(images.size <= MAX_IMAGES && glyphs.size <= text.size.columns.toLong() * text.size.rows) { "too many graphics draws" }
    }

    val images: List<TerminalRenderImageV5> = Collections.unmodifiableList(ArrayList(images))
    val glyphs: List<TerminalRenderGlyphV5> = Collections.unmodifiableList(ArrayList(glyphs))
    init {
        require(size.columns in 1..512 && size.rows in 1..512) { "V5 viewport dimensions are out of range" }
        require(text.presentationRevision == text.nextSourceSequence) { "V5 text revisions disagree" }
        val referenced = HashSet<Long>()
        val placements = HashSet<Long>()
        var previousImage: TerminalRenderImageV5? = null
        for (image in this.images) {
            require(previousImage?.let { it < image } != false && placements.add(image.placementId)) { "invalid image order or duplicate placement" }
            require(image.intersectsViewport(size)) { "image clip misses viewport" }
            val resource = requireNotNull(resources[image.resourceId]) { "missing image resource" }
            require(resource.format == TerminalGraphicsFormat.RGBA8888 && image.source.fits(resource.width, resource.height)) {
                "invalid image resource format or source bounds"
            }
            referenced.add(image.resourceId)
            previousImage = image
        }
        var previousGlyph: TerminalRenderGlyphV5? = null
        for (glyph in this.glyphs) {
            require(previousGlyph?.let { it < glyph } != false && glyph.targetsSingleCell(text)) { "invalid glyph order or target cell" }
            val resource = requireNotNull(resources[glyph.resourceId]) { "missing glyph resource" }
            require(resource.format == TerminalGraphicsFormat.A8 && glyph.x.toLong() + glyph.width <= resource.width &&
                glyph.height <= resource.height) { "invalid glyph atlas format or bounds" }
            referenced.add(glyph.resourceId)
            previousGlyph = glyph
        }
        require(resources.entries.all { it.id in referenced }) { "unreferenced graphics resource" }
    }

    companion object {
        const val FORMAT_VERSION = 5
        const val MAX_IMAGES = 4096
        const val MAX_FRAME_BYTES = 64 * 1024 * 1024
    }
}

private fun fitsAllocation(start: TerminalRenderRatioV5, length: TerminalRenderRatioV5, limit: Int): Boolean {
    val startDenominator = BigInteger.valueOf(start.denominator)
    val lengthDenominator = BigInteger.valueOf(length.denominator)
    val extent = BigInteger.valueOf(start.numerator) * lengthDenominator + BigInteger.valueOf(length.numerator) * startDenominator
    return extent <= BigInteger.valueOf(limit.toLong()) * startDenominator * lengthDenominator
}
