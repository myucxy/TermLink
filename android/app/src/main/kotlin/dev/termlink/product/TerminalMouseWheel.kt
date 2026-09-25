package dev.termlink.product

import android.content.Context
import android.content.res.ColorStateList
import android.graphics.Color
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.InsetDrawable
import android.graphics.drawable.RippleDrawable
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.MotionEvent
import android.widget.ImageButton
import android.widget.LinearLayout
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.viewinterop.AndroidView
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.findViewTreeLifecycleOwner
import dev.termlink.harness.R
import dev.termlink.terminal.api.TerminalMouseWheelDirection
import dev.termlink.terminal.api.TerminalViewportEngine
import kotlin.math.abs
import kotlin.math.roundToInt

// An 8 dp dead zone, then 4..20 detents/second. One scheduled detent, never catch up in bursts.
internal fun wheelRepeatMillis(displacementDp: Float): Long? {
    if (!displacementDp.isFinite() || abs(displacementDp) <= 8f) return null
    val speed = 4f + 16f * ((abs(displacementDp) - 8f) / 88f).coerceIn(0f, 1f)
    return (1000f / speed).toLong()
}

@Composable
internal fun TerminalMouseWheel(engine: TerminalViewportEngine, modifier: Modifier = Modifier) {
    AndroidView(
        factory = { TerminalMouseWheelView(it) },
        update = { it.sendWheel = engine::submitMouseWheel },
        onRelease = { it.stop(); it.sendWheel = { false } },
        modifier = modifier,
    )
}

/** Owns only its narrow hit area; never requests terminal focus or changes the viewport size. */
internal class TerminalMouseWheelView(context: Context) : LinearLayout(context) {
    var sendWheel: (TerminalMouseWheelDirection) -> Boolean = { false }
    private val density = resources.displayMetrics.density
    private val mainHandler = Handler(Looper.getMainLooper())
    private var originY: Float? = null
    private var displacementDp = 0f
    private var repeating = false
    private var pointerCancelled = false
    private var lifecycle: Lifecycle? = null
    private val lifecycleObserver = LifecycleEventObserver { _, event ->
        if (event == Lifecycle.Event.ON_PAUSE || event == Lifecycle.Event.ON_STOP) cancelInteraction()
    }
    // The activity uses platform Theme.Material (API 26+), not an AppCompat theme.
    @android.annotation.SuppressLint("AppCompatCustomView")
    private val handle = object : ImageButton(context) {
        override fun performClick(): Boolean {
            super.performClick()
            stop()
            return true
        }
    }.apply {
        setImageResource(R.drawable.ic_terminal_wheel_grip)
        setPadding(dp(15), dp(3), dp(15), dp(3))
        setColorFilter(0xEBFFFFFF.toInt())
        setBackgroundColor(Color.TRANSPARENT)
        isFocusable = false
        contentDescription = context.getString(R.string.session_wheel_drag)
    }
    private val repeat = object : Runnable {
        override fun run() {
            val interval = wheelRepeatMillis(displacementDp)
            if (originY == null || interval == null || !send(if (displacementDp < 0)
                    TerminalMouseWheelDirection.UP else TerminalMouseWheelDirection.DOWN)) {
                stop()
                return
            }
            mainHandler.postDelayed(this, interval)
        }
    }

    init {
        orientation = VERTICAL
        gravity = Gravity.CENTER
        // Match the Dock surface; inset only the drawing, not the arrow hit targets.
        background = InsetDrawable(GradientDrawable().apply {
            setColor(0x9E000000.toInt())
            cornerRadius = 7f * density
        }, dp(6), dp(6), dp(6), dp(6))
        // InsetDrawable contributes padding; keep the 48 dp buttons edge to edge.
        setPadding(0, 0, 0, 0)
        addView(arrow(R.drawable.ic_terminal_wheel_up, R.string.session_wheel_up, TerminalMouseWheelDirection.UP))
        addView(handle, LayoutParams(dp(48), dp(24)))
        addView(arrow(R.drawable.ic_terminal_wheel_down, R.string.session_wheel_down, TerminalMouseWheelDirection.DOWN))
        handle.setOnTouchListener { _, event ->
            when {
                event.pointerCount != 1 -> stop()
                event.actionMasked == MotionEvent.ACTION_DOWN -> {
                    stop()
                    originY = event.rawY
                    parent?.requestDisallowInterceptTouchEvent(true)
                }
                event.actionMasked == MotionEvent.ACTION_MOVE && originY != null -> {
                    displacementDp = (event.rawY - originY!!) / density
                    handle.translationY = displacementDp.coerceIn(-6f, 6f) * density
                    if (wheelRepeatMillis(displacementDp) == null) {
                        mainHandler.removeCallbacks(repeat)
                        repeating = false
                    } else if (!repeating) {
                        repeating = true
                        mainHandler.postDelayed(repeat, wheelRepeatMillis(displacementDp)!!)
                    }
                }
                event.actionMasked == MotionEvent.ACTION_UP -> handle.performClick()
                event.actionMasked == MotionEvent.ACTION_CANCEL -> stop()
            }
            true
        }
    }

    private fun arrow(icon: Int, label: Int, direction: TerminalMouseWheelDirection) = ImageButton(context).apply {
        layoutParams = LayoutParams(dp(48), dp(48))
        setImageResource(icon)
        setColorFilter(0xEBFFFFFF.toInt())
        background = InsetDrawable(RippleDrawable(
            ColorStateList.valueOf(0x33FFFFFF),
            GradientDrawable().apply {
                setColor(0x17FFFFFF)
                setStroke(dp(1).coerceAtLeast(1), 0x57FFFFFF)
                cornerRadius = 5f * density
            },
            GradientDrawable().apply { setColor(Color.WHITE); cornerRadius = 5f * density },
        ), dp(9))
        setPadding(dp(15), dp(15), dp(15), dp(15))
        contentDescription = context.getString(label)
        isFocusable = false // Leave IME focus with the terminal; accessibility clicks still work.
        setOnClickListener { stop(); send(direction) }
    }

    override fun dispatchTouchEvent(event: MotionEvent): Boolean {
        if (event.actionMasked == MotionEvent.ACTION_DOWN) pointerCancelled = false
        if (event.pointerCount > 1 && !pointerCancelled) cancelInteraction()
        return pointerCancelled || super.dispatchTouchEvent(event)
    }

    private fun cancelInteraction() {
        pointerCancelled = true
        val cancel = MotionEvent.obtain(0, 0, MotionEvent.ACTION_CANCEL, 0f, 0f, 0)
        try { super.dispatchTouchEvent(cancel) } finally { cancel.recycle() }
        stop()
    }

    private fun dp(value: Int) = (value * density).roundToInt()

    private fun send(direction: TerminalMouseWheelDirection): Boolean =
        isAttachedToWindow && isShown && hasWindowFocus() &&
            (lifecycle?.currentState?.isAtLeast(Lifecycle.State.RESUMED) != false) && sendWheel(direction)

    fun stop() {
        mainHandler.removeCallbacks(repeat)
        repeating = false
        originY = null
        displacementDp = 0f
        handle.translationY = 0f
        parent?.requestDisallowInterceptTouchEvent(false)
    }

    override fun onAttachedToWindow() {
        super.onAttachedToWindow()
        lifecycle = findViewTreeLifecycleOwner()?.lifecycle
        lifecycle?.addObserver(lifecycleObserver)
    }

    override fun onDetachedFromWindow() {
        cancelInteraction()
        lifecycle?.removeObserver(lifecycleObserver)
        lifecycle = null
        super.onDetachedFromWindow()
    }

    override fun onWindowFocusChanged(hasWindowFocus: Boolean) {
        super.onWindowFocusChanged(hasWindowFocus)
        if (!hasWindowFocus) cancelInteraction()
    }

    override fun onVisibilityAggregated(isVisible: Boolean) {
        super.onVisibilityAggregated(isVisible)
        if (!isVisible) cancelInteraction()
    }
}
