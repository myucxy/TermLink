package dev.termlink.product

import dev.termlink.terminal.nativecore.NativeHistoryPage
import java.util.UUID

/** A detached V5 window. Capture tokens stay in the repository, never in UI state. */
data class FrozenHistoryViewportState(
    val sessionId: UUID,
    val snapshotSequence: ULong,
    val generation: Long,
    val captureId: ULong? = null,
    val revision: ULong? = null,
    val pages: List<NativeHistoryPage> = emptyList(),
    val consumedAnchors: Set<ULong> = emptySet(),
    val nextBeforeLineId: ULong? = null,
    val loading: Boolean = false,
    val error: String? = null,
) {
    val hasMoreBefore: Boolean get() = nextBeforeLineId != null
}

internal fun nextFrozenSnapshotSequence(
    requested: ULong,
    available: ULong?,
    refreshCount: Int,
): ULong? = available?.takeIf {
    refreshCount < 2 && it > requested && it < Long.MAX_VALUE.toULong()
}

/** Line IDs are opaque identities: only equality, not numeric order, is meaningful. */
internal fun validateFrozenPageCursor(
    requestedAnchor: ULong,
    captureRevision: ULong,
    pageRevision: Long,
    pageAnchor: Long,
    hasMoreBefore: Boolean,
    nextBeforeLineId: Long,
    firstLineId: ULong,
    lastLineId: ULong,
    lineIds: List<Long>,
    previouslyLoadedLineIds: Set<Long>,
    previouslyConsumedAnchors: Set<ULong> = emptySet(),
): ULong? {
    require(pageRevision.toULong() == captureRevision && pageAnchor.toULong() == requestedAnchor) {
        "Frozen page changed its capture revision or requested anchor"
    }
    require(lineIds.distinct().size == lineIds.size && lineIds.none(previouslyLoadedLineIds::contains)) {
        "Frozen page overlaps the displayed window"
    }
    require(firstLineId == (lineIds.firstOrNull()?.toULong() ?: 0uL) &&
        lastLineId == (lineIds.lastOrNull()?.toULong() ?: 0uL)) {
        "Frozen page line identities disagree with the authenticated object"
    }
    val next = nextBeforeLineId.toULong()
    require(next == (lineIds.firstOrNull()?.toULong() ?: requestedAnchor)) {
        "Frozen page continuation disagrees with its lines"
    }
    require(!hasMoreBefore || (lineIds.isNotEmpty() && next != 0uL && next != requestedAnchor)) {
        "Frozen page did not advance its cursor"
    }
    require(!hasMoreBefore || next !in previouslyConsumedAnchors) {
        "Frozen page continuation repeats an earlier cursor"
    }
    return next.takeIf { hasMoreBefore }
}
