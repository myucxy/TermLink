package dev.termlink.product

import android.app.Activity
import android.content.Context
import android.content.ContextWrapper
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.ime
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.layout.positionInWindow
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.repeatOnLifecycle
import androidx.window.layout.FoldingFeature
import androidx.window.layout.WindowInfoTracker

internal data class ProductDisplayArea(val left: Int, val top: Int, val right: Int, val bottom: Int) {
    val width: Int get() = right - left
    val height: Int get() = bottom - top
}

internal data class ProductDisplayFold(val bounds: ProductDisplayArea, val vertical: Boolean)

/** Window-relative fold coordinates are clipped to this app, including freeform and split screen. */
internal fun productDisplayArea(
    width: Int,
    height: Int,
    originX: Int,
    originY: Int,
    folds: List<ProductDisplayFold>,
    rightToLeft: Boolean = false,
): ProductDisplayArea {
    var area = ProductDisplayArea(0, 0, width.coerceAtLeast(0), height.coerceAtLeast(0))
    for (fold in folds) {
        val bounds = fold.bounds
        if (fold.vertical) {
            val start = bounds.left - originX
            val end = bounds.right - originX
            if (bounds.top > originY + area.top || bounds.bottom < originY + area.bottom ||
                start <= area.left || end >= area.right || start > end) continue
            val before = start - area.left
            val after = area.right - end
            area = if (before > after || before == after && !rightToLeft) area.copy(right = start)
                   else area.copy(left = end)
        } else {
            val start = bounds.top - originY
            val end = bounds.bottom - originY
            if (bounds.left > originX + area.left || bounds.right < originX + area.right ||
                start <= area.top || end >= area.bottom || start > end) continue
            // Keep the upright surface for tabletop posture; controls and text stay above the crease.
            area = area.copy(bottom = start)
        }
    }
    return area
}

internal fun productPaneImeBottom(area: ProductDisplayArea, windowHeight: Int, imeBottom: Int): Int =
    (area.bottom - (windowHeight - imeBottom.coerceAtLeast(0)))
        .coerceIn(0, area.height.coerceAtLeast(0))

private val LocalProductPane = staticCompositionLocalOf<Pair<ProductDisplayArea, Int>?> { null }

@Composable
internal fun productImeInsets(): WindowInsets {
    val pane = LocalProductPane.current ?: return WindowInsets.ime
    return WindowInsets(bottom = productPaneImeBottom(pane.first, pane.second, WindowInsets.ime.getBottom(LocalDensity.current)))
}

private fun Context.productActivity(): Activity? = when (this) {
    is Activity -> this
    is ContextWrapper -> baseContext.takeUnless { it === this }?.productActivity()
    else -> null
}

@Composable
internal fun ProductAdaptiveFrame(content: @Composable () -> Unit) {
    val context = LocalContext.current
    val activity = remember(context) { context.productActivity() }
    val folds by produceState(emptyList<ProductDisplayFold>(), activity) {
        val owner = activity as? androidx.lifecycle.LifecycleOwner ?: return@produceState
        owner.lifecycle.repeatOnLifecycle(Lifecycle.State.STARTED) {
            WindowInfoTracker.getOrCreate(activity).windowLayoutInfo(activity).collect { info ->
                value = info.displayFeatures.filterIsInstance<FoldingFeature>()
                    .filter { it.isSeparating || it.occlusionType == FoldingFeature.OcclusionType.FULL }
                    .map {
                        ProductDisplayFold(ProductDisplayArea(it.bounds.left, it.bounds.top, it.bounds.right, it.bounds.bottom),
                            it.orientation == FoldingFeature.Orientation.VERTICAL)
                    }
            }
        }
    }
    var origin by remember { mutableStateOf(Offset.Zero) }
    val density = LocalDensity.current
    val rightToLeft = LocalLayoutDirection.current == LayoutDirection.Rtl
    BoxWithConstraints(Modifier.fillMaxSize().background(MaterialTheme.colorScheme.background)
        .onGloballyPositioned { origin = it.positionInWindow() }) {
        val width = constraints.maxWidth
        val height = constraints.maxHeight
        val area = productDisplayArea(width, height, origin.x.toInt(), origin.y.toInt(), folds, rightToLeft)
        val bottom = height - area.bottom
        // Physical left/right coordinates must not be mirrored a second time in RTL.
        val padding = with(density) { PaddingValues(
            start = (if (rightToLeft) width - area.right else area.left).toDp(),
            top = area.top.toDp(), end = (if (rightToLeft) area.left else width - area.right).toDp(),
            bottom = bottom.toDp()) }
        CompositionLocalProvider(LocalProductPane provides (area to height)) {
            Box(Modifier.fillMaxSize().padding(padding)) {
                Box(Modifier.fillMaxSize().testTag("product-display-area")) { content() }
            }
        }
    }
}
