package dev.termlink.terminal.nativecore

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.Typeface
import android.os.Bundle
import android.text.Editable
import android.text.InputType
import android.text.SpannableStringBuilder
import android.util.AttributeSet
import android.util.TypedValue
import android.view.ActionMode
import android.view.GestureDetector
import android.view.KeyCharacterMap
import android.view.KeyEvent
import android.view.Menu
import android.view.MenuItem
import android.view.MotionEvent
import android.view.ScaleGestureDetector
import android.view.View
import android.view.ViewConfiguration
import android.view.inputmethod.BaseInputConnection
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputConnection
import android.widget.OverScroller
import dev.termlink.terminal.api.TerminalInputModifiers
import dev.termlink.terminal.api.TerminalKittyKeyEventType
import dev.termlink.terminal.api.TerminalMouseButton
import dev.termlink.terminal.api.TerminalMouseButtons
import dev.termlink.terminal.api.TerminalMouseInput
import dev.termlink.terminal.api.TerminalMouseWheelDirection
import dev.termlink.terminal.api.TerminalRenderColorV1
import dev.termlink.terminal.api.TerminalRenderCursorShapeV2
import dev.termlink.terminal.api.TerminalRenderFrameV3
import dev.termlink.terminal.api.TerminalRenderGraphemeV1
import dev.termlink.terminal.api.TerminalRenderRenditionV3
import dev.termlink.terminal.api.TerminalRenderUnderlineStyleV3
import dev.termlink.terminal.api.TerminalThemeV1
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalViewportMetrics
import dev.termlink.terminal.api.TerminalTypefaceResolver
import java.util.LinkedHashMap
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import kotlin.math.abs
import kotlin.math.ceil
import kotlin.math.floor
import kotlin.math.hypot
import kotlin.math.max
import kotlin.math.roundToInt
import kotlin.math.sign

/** Canvas terminal surface for immutable terminal-core Render Frame V3 projections. */
internal class NativeTerminalView @JvmOverloads constructor(
    context: Context,
    private var engine: NativeTerminalEngine?,
    textSizeSp: Float,
    lineSpacingMultiplier: Float,
    private val onScrollPastTop: (() -> Unit)? = null,
    private val onScrollPastBottom: (() -> Unit)? = null,
    attrs: AttributeSet? = null,
    private val transcript: NativeTranscriptViewport? = null,
) : View(context, attrs) {
    private val density = resources.displayMetrics.density
    private val contentPadding = if (transcript == null) 8f * density else 0f
    private var transcriptTopInset = 0f
    private val textPaint = Paint(Paint.ANTI_ALIAS_FLAG or Paint.SUBPIXEL_TEXT_FLAG)
    private val shapePaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val imeEditable = SpannableStringBuilder()
    private var combiningAccent = 0
    private val pressedCharacterKeys = HashMap<Int, Pair<Int, Int?>>()
    private val boundedLineSpacing = lineSpacingMultiplier.coerceIn(0.8f, 1.8f)
    private var textSizePixels = TypedValue.applyDimension(
        TypedValue.COMPLEX_UNIT_SP,
        textSizeSp.coerceIn(8f, 48f),
        resources.displayMetrics,
    )
    private val preferredTextSizePixels = TypedValue.applyDimension(
        TypedValue.COMPLEX_UNIT_SP, textSizeSp, resources.displayMetrics,
    )
    private var pendingTranscriptPresentation = transcript?.let { it.presentation ?: NativeTranscriptPresentation(null) }
    private var transcriptPartialBottom = false
    private var transcriptHorizontalRemainder = 0f
    private var transcriptPinching = false
    private var cellWidth = 1f
    private var cellHeight = 1f
    private var baselineOffset = 1f
    private var viewportX = 0f
    private var viewportY = 0f
    private var followCursor = true
    private var historyDragPixels = 0f
    private var scrollAxis = ScrollAxis.UNDECIDED
    private var flingAxis = ScrollAxis.UNDECIDED
    private val scroller = OverScroller(context)
    private var lastFlingX = 0
    private var lastFlingY = 0
    private val historyQueueLock = Any()
    private var historyExecutor: ExecutorService? = null
    private var pendingHistoryRows = 0
    private var historyWorkerScheduled = false
    private var historyWorkerGeneration = 0L
    private var historyRequestGeneration = 0L
    @Volatile
    private var historyNavigationGeneration = 0L
    @Volatile
    private var lastHistoryWorkerThreadName: String? = null
    private var mouseCaptured = false
    private var selectionAnchor: CellPoint? = null
    private var selectionExtent: CellPoint? = null
    private var selectionActionMode: ActionMode? = null
    private var selectionDragActive = false
    private var selectionHandleDrag: SelectionHandle? = null
    private val selectionHandlePaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val selectionHandleHitRadius = 24f * density
    private val selectionHandleRadius = 7f * density
    private val touchSlop = ViewConfiguration.get(context).scaledTouchSlop.toFloat()
    private var touchDownX = 0f
    private var touchDownY = 0f
    private var selectionLongPressEligible = false
    private var detached = false
    private var baseTypeface: Typeface = Typeface.MONOSPACE
    private var boldTypeface: Typeface = Typeface.DEFAULT_BOLD
    private var italicTypeface: Typeface = Typeface.create(Typeface.MONOSPACE, Typeface.ITALIC)
    private var boldItalicTypeface: Typeface = Typeface.create(Typeface.MONOSPACE, Typeface.BOLD_ITALIC)
    private var textRunWidthMatchOverrideForTest: Boolean? = null
    private val textWidthCache = object : LinkedHashMap<TextMeasureKey, Float>(
        TEXT_WIDTH_CACHE_SIZE,
        0.75f,
        true,
    ) {
        override fun removeEldestEntry(eldest: MutableMap.MutableEntry<TextMeasureKey, Float>): Boolean =
            size > TEXT_WIDTH_CACHE_SIZE
    }
    private var appliedFontFamilyHint: String? = null
    private var fontFamilyInitialized = false

    private enum class SelectionHandle { START, END }

    private val gestures = GestureDetector(context, object : GestureDetector.SimpleOnGestureListener() {
        override fun onDown(event: MotionEvent): Boolean {
            scroller.forceFinished(true)
            scrollAxis = ScrollAxis.UNDECIDED
            // Selection may only be extended by the same pointer gesture that
            // activated it. A later swipe remains terminal navigation.
            selectionDragActive = false
            touchDownX = event.x
            touchDownY = event.y
            selectionLongPressEligible = true
            historyDragPixels = 0f
            transcriptHorizontalRemainder = 0f
            cancelQueuedHistoryRequests()
            engine?.let { historyNavigationGeneration = it.beginHistoryNavigation() }
            return true
        }

        override fun onSingleTapUp(event: MotionEvent): Boolean {
            if (dismissSelection()) return true
            performClick()
            return true
        }

        override fun onScroll(
            first: MotionEvent?,
            current: MotionEvent,
            distanceX: Float,
            distanceY: Float,
        ): Boolean {
            val owner = engine
            if (owner == null && transcript == null) return false
            if (mouseCaptured) return true
            // A selection belongs to the gesture that created it. If the user
            // starts a new swipe, close the old action mode first so the swipe
            // can browse history instead of extending a stale highlight.
            if (!selectionDragActive && hasSelectionState()) dismissSelection()
            val extent = selectionExtent
            if (selectionDragActive && extent != null) {
                selectionExtent = cellAt(current.x, current.y)
                postInvalidateOnAnimation()
                return true
            }
            if (scrollAxis == ScrollAxis.UNDECIDED) {
                val totalX = abs(current.x - (first?.x ?: current.x))
                val totalY = abs(current.y - (first?.y ?: current.y))
                scrollAxis = if (transcript != null) {
                    if (totalX > totalY * 1.2f && maxViewportX() > 0f) ScrollAxis.HORIZONTAL else ScrollAxis.VERTICAL
                } else if (totalX >= totalY) ScrollAxis.HORIZONTAL else ScrollAxis.VERTICAL
            }
            followCursor = false
            when (scrollAxis) {
                ScrollAxis.HORIZONTAL -> {
                    scrollHorizontally(distanceX)
                }
                ScrollAxis.VERTICAL -> scrollVertically(owner, distanceY)
                ScrollAxis.UNDECIDED -> Unit
            }
            postInvalidateOnAnimation()
            saveTranscriptPresentation()
            return true
        }

        override fun onFling(
            first: MotionEvent?,
            current: MotionEvent,
            velocityX: Float,
            velocityY: Float,
        ): Boolean {
            if (mouseCaptured) return false
            if (selectionDragActive) return true
            if (hasSelectionState()) dismissSelection()
            val axis = if (scrollAxis == ScrollAxis.UNDECIDED) {
                if (abs(velocityX) >= abs(velocityY)) ScrollAxis.HORIZONTAL else ScrollAxis.VERTICAL
            } else {
                scrollAxis
            }
            startFling(axis, velocityX, velocityY)
            return true
        }

        override fun onLongPress(event: MotionEvent) {
            if (mouseCaptured || !selectionLongPressEligible || scrollAxis != ScrollAxis.UNDECIDED) return
            dismissSelection()
            val cell = selectableCellAt(event.x, event.y) ?: return
            val word = wordSelectionAt(cell)
            selectionAnchor = word.first
            selectionExtent = word.second
            selectionDragActive = true
            val mode = startActionMode(selectionCallback, ActionMode.TYPE_FLOATING)
            if (mode == null) {
                clearSelectionState()
                return
            }
            selectionActionMode = mode
            invalidate()
        }
    })

    private val transcriptScale = if (transcript == null) null else ScaleGestureDetector(
        context,
        object : ScaleGestureDetector.SimpleOnScaleGestureListener() {
            override fun onScaleBegin(detector: ScaleGestureDetector): Boolean {
                dismissSelection()
                scroller.forceFinished(true)
                selectionLongPressEligible = false
                return true
            }

            override fun onScale(detector: ScaleGestureDetector): Boolean {
                val saved = transcriptPresentation()
                val minimum = TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_SP, 10f, resources.displayMetrics)
                transcript?.userTextSizePixels = (textSizePixels * detector.scaleFactor)
                    .coerceIn(minOf(minimum, preferredTextSizePixels), max(minimum, preferredTextSizePixels * 3f))
                updateTranscript(saved)
                return true
            }
        },
    )

    private val selectionCallback = object : ActionMode.Callback {
        override fun onCreateActionMode(mode: ActionMode, menu: Menu): Boolean {
            menu.add(Menu.NONE, android.R.id.copy, 0, android.R.string.copy)
                .setShowAsAction(MenuItem.SHOW_AS_ACTION_ALWAYS)
            menu.add(Menu.NONE, android.R.id.selectAll, 1, android.R.string.selectAll)
                .setShowAsAction(MenuItem.SHOW_AS_ACTION_NEVER)
            return true
        }

        override fun onPrepareActionMode(mode: ActionMode, menu: Menu): Boolean = false

        override fun onActionItemClicked(mode: ActionMode, item: MenuItem): Boolean = when (item.itemId) {
            android.R.id.copy -> {
                copySelection()
                mode.finish()
                true
            }
            android.R.id.selectAll -> {
                val frame = currentFrame()
                selectionAnchor = CellPoint(0, 0)
                selectionExtent = CellPoint(frame.size.columns - 1, frame.size.rows - 1)
                invalidate()
                true
            }
            else -> false
        }

        override fun onDestroyActionMode(mode: ActionMode) {
            if (selectionActionMode === mode) clearSelectionState()
        }
    }

    init {
        isFocusable = transcript == null
        isFocusableInTouchMode = transcript == null
        if (transcript != null) followCursor = false
        isClickable = true
        importantForAccessibility = IMPORTANT_FOR_ACCESSIBILITY_YES
        refreshTypeface(force = true)
    }

    override fun onAttachedToWindow() {
        super.onAttachedToWindow()
        val owner = engine
        detached = if (owner != null) !owner.attachView(this) else transcript?.attachView(this) != true
        if (!detached) onFrameChanged()
    }

    override fun onDetachedFromWindow() {
        saveTranscriptPresentation()
        detached = true
        scroller.forceFinished(true)
        engine?.beginHistoryNavigation()
        shutdownHistoryExecutor()
        selectionActionMode?.finish()
        selectionActionMode = null
        clearSelectionState()
        engine?.detachView(this)
        super.onDetachedFromWindow()
    }

    internal fun detachEngine() {
        detached = true
        scroller.forceFinished(true)
        engine?.beginHistoryNavigation()
        shutdownHistoryExecutor()
        engine = null
        selectionActionMode?.finish()
        selectionActionMode = null
        clearSelectionState()
    }

    internal fun onFrameChanged() {
        if (detached) return
        if (transcript != null) {
            updateTranscript(pendingTranscriptPresentation ?: transcriptPresentation())
            return
        }
        refreshTypeface()
        if (followCursor) ensureCursorVisible() else clampViewport()
        postInvalidateOnAnimation()
    }

    internal fun scrollToCursor() {
        scroller.forceFinished(true)
        cancelQueuedHistoryRequests()
        historyNavigationGeneration = 0L
        followCursor = true
        historyDragPixels = 0f
        ensureCursorVisible()
        postInvalidateOnAnimation()
    }

    private fun ensureCursorVisible() {
        if (engine == null || width <= 0 || height <= 0) return
        val frame = currentFrame()
        if (!frame.cursor.visible) {
            clampViewport()
            return
        }
        val cursorLeft = contentPadding + frame.cursor.column * cellWidth
        val cursorRight = cursorLeft + cellWidth
        val cursorTop = contentPadding + frame.cursor.row * cellHeight
        val cursorBottom = cursorTop + cellHeight
        // Keep a small horizontal safety area around the cursor. This avoids
        // placing the insertion point flush against an edge after an IME
        // resize or when a command advances to the first/last visible column.
        // On narrow windows the margin is reduced so the cursor can still fit.
        val horizontalBreathingRoom = minOf(
            cellWidth * CURSOR_EDGE_MARGIN_CELLS,
            max(0f, (width - cellWidth) / 2f),
        )
        viewportX = when {
            cursorLeft < viewportX + horizontalBreathingRoom ->
                cursorLeft - horizontalBreathingRoom
            cursorRight > viewportX + width - horizontalBreathingRoom ->
                cursorRight - width + horizontalBreathingRoom
            else -> viewportX
        }
        viewportY = when {
            cursorTop < viewportY -> cursorTop
            cursorBottom > viewportY + height -> cursorBottom - height
            else -> viewportY
        }
        clampViewport()
    }

    internal fun signalScrollPastTop() {
        if (viewportY <= 0f) onScrollPastTop?.invoke() else {
            viewportY = 0f
            invalidate()
        }
    }

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val frame = currentFrame()
        val desiredWidth = ceil(contentPadding * 2 + frame.size.columns * cellWidth).toInt()
        val desiredHeight = ceil(contentPadding * 2 + frame.size.rows * cellHeight).toInt()
        setMeasuredDimension(
            resolveSize(desiredWidth, widthMeasureSpec),
            resolveSize(desiredHeight, heightMeasureSpec),
        )
    }

    override fun onSizeChanged(width: Int, height: Int, oldWidth: Int, oldHeight: Int) {
        super.onSizeChanged(width, height, oldWidth, oldHeight)
        if (transcript != null) {
            updateTranscript(pendingTranscriptPresentation ?: transcript.presentation)
            return
        }
        if (followCursor) ensureCursorVisible() else clampViewport()
    }

    internal fun measuredViewport(): TerminalViewportMetrics? {
        refreshTypeface()
        val contentWidth = floor(width - 2 * contentPadding).toInt()
        val contentHeight = floor(height - 2 * contentPadding).toInt()
        val columns = floor(contentWidth / cellWidth).toInt()
        val rows = floor(contentHeight / cellHeight).toInt()
        if (detached || !isShown || columns !in 2..1000 || rows !in 2..1000) return null
        return TerminalViewportMetrics(TerminalSize(columns, rows), contentWidth, contentHeight, cellWidth, cellHeight)
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        if (detached || engine == null && transcript == null) return
        drawFrame(canvas, currentFrame(), currentTheme())
    }

    internal fun drawFrame(
        canvas: Canvas,
        frame: TerminalRenderFrameV3,
        theme: TerminalThemeV1,
    ) {
        if (transcript != null) {
            drawTranscript(canvas, frame, theme)
            return
        }
        shapePaint.style = Paint.Style.FILL
        shapePaint.color = effectiveDefaultBackground(frame, theme)
        canvas.drawRect(0f, 0f, width.toFloat(), height.toFloat(), shapePaint)

        val firstRow = floor((viewportY - contentPadding) / cellHeight).toInt().coerceAtLeast(0)
        val lastRow = ceil((viewportY + height - contentPadding) / cellHeight).toInt()
            .coerceAtMost(frame.size.rows - 1)
        if (lastRow < firstRow) return
        val firstColumn = floor((viewportX - contentPadding) / cellWidth).toInt().coerceAtLeast(0)
        val lastColumn = ceil((viewportX + width - contentPadding) / cellWidth).toInt()
            .coerceAtMost(frame.size.columns - 1)
        if (lastColumn < firstColumn) return
        val colorsByRendition = Array(frame.renditions.size) { index ->
            effectiveColors(frame, frame.renditions[index], theme)
        }

        for (rowIndex in firstRow..lastRow) {
            val row = frame.rows[rowIndex]
            val top = contentPadding + rowIndex * cellHeight - viewportY
            var runStart = firstColumn
            var runColor: Int? = null
            for (column in firstColumn..lastColumn) {
                val cell = row.cells[column]
                val color = if (isSelected(column, rowIndex, frame)) {
                    theme.selectionBackground.value
                } else {
                    colorsByRendition[cell.renditionId].background
                }
                if (runColor != null && color != runColor) {
                    drawBackgroundRun(canvas, runStart, column, top, runColor)
                    runStart = column
                }
                runColor = color
            }
            runColor?.let { drawBackgroundRun(canvas, runStart, lastColumn + 1, top, it) }
        }

        // Draw every cell background before drawing any text. A width-two grapheme is painted by
        // its lead cell across both columns; painting the continuation background afterwards would
        // erase the right half of CJK, full-width, and other wide glyphs.
        val firstTextColumn = (firstColumn - 1).coerceAtLeast(0)
        for (rowIndex in firstRow..lastRow) {
            drawTextRow(
                canvas = canvas,
                frame = frame,
                rowIndex = rowIndex,
                firstColumn = firstTextColumn,
                lastColumn = lastColumn,
                colorsByRendition = colorsByRendition,
            )
        }
        drawCursor(canvas, frame, theme)
        drawSelectionHandles(canvas, frame, theme)
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        if (detached) return false
        val owner = engine
        if (owner == null && transcript == null) return false
        transcriptScale?.let { scale ->
            if (event.actionMasked == MotionEvent.ACTION_DOWN) transcriptPinching = false
            scale.onTouchEvent(event)
            if (event.pointerCount > 1 || scale.isInProgress) {
                transcriptPinching = true
                selectionLongPressEligible = false
                scroller.forceFinished(true)
                return true
            }
            if (transcriptPinching) {
                if (event.actionMasked == MotionEvent.ACTION_UP || event.actionMasked == MotionEvent.ACTION_CANCEL) {
                    val cancel = MotionEvent.obtain(event).apply { action = MotionEvent.ACTION_CANCEL }
                    gestures.onTouchEvent(cancel)
                    cancel.recycle()
                    transcriptPinching = false
                }
                return true
            }
        }
        if (event.actionMasked == MotionEvent.ACTION_DOWN) {
            selectionHandleAt(event.x, event.y)?.let { handle ->
                selectionHandleDrag = handle
                selectionDragActive = true
                selectionLongPressEligible = false
                return true
            }
        }
        selectionHandleDrag?.let { handle ->
            when (event.actionMasked) {
                MotionEvent.ACTION_MOVE -> updateSelectionHandle(handle, event.x, event.y)
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    selectionHandleDrag = null
                    selectionDragActive = false
                    selectionLongPressEligible = false
                }
            }
            return true
        }
        if (event.actionMasked == MotionEvent.ACTION_MOVE && !selectionDragActive) {
            val travel = hypot(event.x - touchDownX, event.y - touchDownY)
            if (travel > touchSlop) {
                selectionLongPressEligible = false
                if (hasSelectionState()) dismissSelection()
            }
        }
        val position = mousePosition(event.x, event.y)
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                if (owner != null) requestFocus()
                mouseCaptured = owner?.submitMouse(
                    TerminalMouseInput.Press(
                        button = TerminalMouseButton.LEFT,
                        position = position,
                        buttonsHeld = TerminalMouseButtons(left = true),
                        modifiers = eventModifiers(event),
                    ),
                ) == true
            }
            MotionEvent.ACTION_MOVE -> if (mouseCaptured) {
                owner?.submitMouse(
                    TerminalMouseInput.Move(
                        position = position,
                        buttonsHeld = TerminalMouseButtons(left = true),
                        modifiers = eventModifiers(event),
                    ),
                )
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> if (mouseCaptured) {
                owner?.submitMouse(
                    TerminalMouseInput.Release(
                        button = TerminalMouseButton.LEFT,
                        position = position,
                        buttonsHeld = TerminalMouseButtons(),
                        modifiers = eventModifiers(event),
                    ),
                )
                mouseCaptured = false
            }
        }
        val handled = if (mouseCaptured) true else gestures.onTouchEvent(event) || super.onTouchEvent(event)
        if (event.actionMasked == MotionEvent.ACTION_UP || event.actionMasked == MotionEvent.ACTION_CANCEL) {
            scrollAxis = ScrollAxis.UNDECIDED
            selectionDragActive = false
            selectionLongPressEligible = false
        }
        return handled
    }

    override fun computeScroll() {
        if (!scroller.computeScrollOffset()) return
        val owner = engine
        if (detached || owner == null && transcript == null) {
            scroller.forceFinished(true)
            return
        }
        val nextX = scroller.currX
        val nextY = scroller.currY
        val deltaX = nextX - lastFlingX
        val deltaY = nextY - lastFlingY
        lastFlingX = nextX
        lastFlingY = nextY
        when (flingAxis) {
            ScrollAxis.HORIZONTAL -> {
                val before = viewportX
                scrollHorizontally(deltaX.toFloat())
                if (viewportX == before && deltaX != 0 && (viewportX == 0f || viewportX == maxViewportX())) {
                    scroller.forceFinished(true)
                }
            }
            ScrollAxis.VERTICAL -> scrollVertically(owner, deltaY.toFloat())
            ScrollAxis.UNDECIDED -> scroller.forceFinished(true)
        }
        postInvalidateOnAnimation()
        saveTranscriptPresentation()
    }

    override fun onGenericMotionEvent(event: MotionEvent): Boolean {
        if (event.action == MotionEvent.ACTION_SCROLL) {
            val owner = engine
            if (detached || owner == null && transcript == null) return false
            val vertical = event.getAxisValue(MotionEvent.AXIS_VSCROLL)
            if (vertical != 0f) {
                val direction = if (vertical > 0f) {
                    TerminalMouseWheelDirection.UP
                } else {
                    TerminalMouseWheelDirection.DOWN
                }
                if (owner?.submitMouse(
                        TerminalMouseInput.Wheel(
                            direction = direction,
                            position = mousePosition(event.x, event.y),
                            modifiers = eventModifiers(event),
                        ),
                    ) == true
                ) return true
                if (historyNavigationGeneration == 0L) {
                    historyNavigationGeneration = owner?.beginHistoryNavigation() ?: 0L
                }
                scrollVertically(owner, -vertical * cellHeight * 3f)
                postInvalidateOnAnimation()
                saveTranscriptPresentation()
                return true
            }
        }
        return super.onGenericMotionEvent(event)
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        val eventType = if (event.repeatCount > 0) {
            TerminalKittyKeyEventType.REPEAT
        } else {
            TerminalKittyKeyEventType.PRESS
        }
        return submitHardwareKey(keyCode, event, eventType) || super.onKeyDown(keyCode, event)
    }

    override fun onKeyUp(keyCode: Int, event: KeyEvent): Boolean =
        submitHardwareKey(keyCode, event, TerminalKittyKeyEventType.RELEASE) ||
            super.onKeyUp(keyCode, event)

    private fun submitHardwareKey(
        keyCode: Int,
        event: KeyEvent,
        eventType: TerminalKittyKeyEventType,
    ): Boolean {
        val owner = engine ?: return false
        if (safeSubmit { owner.submitPhysicalKeyCode(
                keyCode,
                controlDown = event.isCtrlPressed,
                altDown = event.isAltPressed,
                shiftDown = event.isShiftPressed,
                capsLock = event.isCapsLockOn,
                numLock = event.isNumLockOn,
                eventType = eventType,
            )
        }) return true
        val metaState = event.metaState
        val leftAltDown = metaState and KeyEvent.META_ALT_LEFT_ON != 0
        val rightAltDown = metaState and KeyEvent.META_ALT_RIGHT_ON != 0
        val terminalAltDown = leftAltDown || event.isAltPressed && !rightAltDown
        var effectiveMetaState = metaState and KeyEvent.META_CTRL_MASK.inv()
        if (!rightAltDown) effectiveMetaState = effectiveMetaState and KeyEvent.META_ALT_MASK.inv()
        if (eventType == TerminalKittyKeyEventType.RELEASE) {
            pressedCharacterKeys.remove(keyCode)?.let { (codePoint, shiftedCodePoint) ->
                return safeSubmit {
                    owner.submitPhysicalCodePoint(
                        codePoint, event.isCtrlPressed, terminalAltDown, event.isShiftPressed,
                        event.isCapsLockOn, event.isNumLockOn, shiftedCodePoint, eventType,
                    )
                }
            }
        }
        var resolvedCodePoint = event.getUnicodeChar(effectiveMetaState)
        val baseCodePoint = event.getUnicodeChar(
            effectiveMetaState and KeyEvent.META_SHIFT_MASK.inv() and KeyEvent.META_CAPS_LOCK_ON.inv(),
        )

        if (resolvedCodePoint and KeyCharacterMap.COMBINING_ACCENT != 0) {
            val accent = resolvedCodePoint and KeyCharacterMap.COMBINING_ACCENT_MASK
            if (eventType == TerminalKittyKeyEventType.RELEASE) {
                return combiningAccent == accent
            } else {
                if (combiningAccent != 0 && !safeSubmit {
                        owner.submitPhysicalCodePoint(
                            combiningAccent, false, terminalAltDown, false,
                            event.isCapsLockOn, event.isNumLockOn, null, eventType,
                        )
                    }
                ) return false
                combiningAccent = accent
                return true
            }
        } else if (combiningAccent != 0 && eventType != TerminalKittyKeyEventType.RELEASE) {
            val combined = KeyCharacterMap.getDeadChar(combiningAccent, resolvedCodePoint)
            if (combined > 0) resolvedCodePoint = combined
            combiningAccent = 0
        }
        val codePoint = when {
            (event.isCtrlPressed || terminalAltDown || event.isShiftPressed || event.isCapsLockOn) &&
                baseCodePoint > 0 -> baseCodePoint
            resolvedCodePoint > 0 -> resolvedCodePoint
            event.isCtrlPressed && keyCode in KeyEvent.KEYCODE_A..KeyEvent.KEYCODE_Z ->
                'a'.code + keyCode - KeyEvent.KEYCODE_A
            else -> 0
        }
        return if (codePoint > 0 && !Character.isISOControl(codePoint)) {
            val shiftedCodePoint = resolvedCodePoint.takeIf {
                (event.isShiftPressed || event.isCapsLockOn) &&
                    it > 0 && !Character.isISOControl(it)
            }
            safeSubmit { owner.submitPhysicalCodePoint(
                codePoint = codePoint,
                controlDown = event.isCtrlPressed,
                altDown = terminalAltDown,
                shiftDown = event.isShiftPressed,
                capsLock = event.isCapsLockOn,
                numLock = event.isNumLockOn,
                shiftedCodePoint = shiftedCodePoint,
                eventType = eventType,
            ) }.also { accepted ->
                if (accepted && eventType == TerminalKittyKeyEventType.PRESS) {
                    pressedCharacterKeys[keyCode] = codePoint to shiftedCodePoint
                }
            }
        } else {
            false
        }
    }

    override fun onCheckIsTextEditor(): Boolean = transcript == null && engine != null

    override fun onKeyPreIme(keyCode: Int, event: KeyEvent): Boolean {
        if (keyCode == KeyEvent.KEYCODE_BACK && event.action == KeyEvent.ACTION_UP && dismissSelection()) {
            return true
        }
        return super.onKeyPreIme(keyCode, event)
    }

    override fun performClick(): Boolean {
        super.performClick()
        if (transcript != null || engine == null) return true
        if (!hasFocus()) requestFocus()
        // ViewRoot commits focus ownership after the touch callback. Posting
        // prevents IMEs from rejecting this view as not yet served.
        post {
            if (!detached && isAttachedToWindow && hasFocus()) showKeyboard()
        }
        return true
    }

    override fun onCreateInputConnection(outAttrs: EditorInfo): InputConnection? {
        if (transcript != null || engine == null) return null
        outAttrs.inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_MULTI_LINE or
            InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
        outAttrs.imeOptions = EditorInfo.IME_ACTION_NONE or EditorInfo.IME_FLAG_NO_EXTRACT_UI
        outAttrs.initialSelStart = 0
        outAttrs.initialSelEnd = 0
        return object : BaseInputConnection(this, true) {
            override fun getEditable(): Editable = imeEditable

            override fun commitText(text: CharSequence?, newCursorPosition: Int): Boolean {
                if (text == null) return false
                if (!super.commitText(text, newCursorPosition)) return false
                return flushImeEditable(text.toString())
            }

            override fun setComposingText(text: CharSequence?, newCursorPosition: Int): Boolean {
                return super.setComposingText(text, newCursorPosition)
            }

            override fun finishComposingText(): Boolean {
                val committed = imeEditable.toString()
                if (!super.finishComposingText()) return false
                return flushImeEditable(committed)
            }

            override fun deleteSurroundingText(beforeLength: Int, afterLength: Int): Boolean {
                if (!super.deleteSurroundingText(beforeLength, afterLength)) return false
                return sendDeletes(beforeLength, afterLength)
            }

            override fun deleteSurroundingTextInCodePoints(
                beforeLength: Int,
                afterLength: Int,
            ): Boolean {
                if (!super.deleteSurroundingTextInCodePoints(beforeLength, afterLength)) return false
                return sendDeletes(beforeLength, afterLength)
            }

            override fun sendKeyEvent(event: KeyEvent): Boolean = when (event.action) {
                KeyEvent.ACTION_DOWN -> onKeyDown(event.keyCode, event)
                KeyEvent.ACTION_UP -> onKeyUp(event.keyCode, event)
                else -> false
            }

            override fun performEditorAction(actionCode: Int): Boolean =
                safeSubmit { engine?.submitKeyCode(KeyEvent.KEYCODE_ENTER) == true }

            override fun performPrivateCommand(action: String?, data: Bundle?): Boolean = false

            private fun flushImeEditable(fallback: String): Boolean {
                val text = imeEditable.toString().ifEmpty { fallback }
                imeEditable.clear()
                return text.isEmpty() || safeSubmit { engine?.submitText(text) == true }
            }

            private fun sendDeletes(beforeLength: Int, afterLength: Int): Boolean {
                var accepted = true
                repeat(beforeLength.coerceAtLeast(0)) {
                    accepted = safeSubmit {
                        engine?.submitKeyCode(KeyEvent.KEYCODE_DEL) == true
                    } && accepted
                }
                repeat(afterLength.coerceAtLeast(0)) {
                    accepted = safeSubmit {
                        engine?.submitKeyCode(KeyEvent.KEYCODE_FORWARD_DEL) == true
                    } && accepted
                }
                return accepted
            }
        }
    }

    override fun onFocusChanged(gainFocus: Boolean, direction: Int, previouslyFocusedRect: android.graphics.Rect?) {
        super.onFocusChanged(gainFocus, direction, previouslyFocusedRect)
        engine?.submitFocus(gainFocus)
    }

    private fun showKeyboard() {
        context.getSystemService(android.view.inputmethod.InputMethodManager::class.java)
            ?.showSoftInput(this, android.view.inputmethod.InputMethodManager.SHOW_IMPLICIT)
    }

    private inline fun safeSubmit(block: () -> Boolean): Boolean = try {
        block()
    } catch (_: Exception) {
        false
    }

    private fun updateCellMetrics() {
        textPaint.textSize = textSizePixels
        textPaint.typeface = baseTypeface
        textPaint.fontFeatureSettings = if (transcript == null) "liga=0,clig=0,calt=0,kern=0" else null
        textPaint.isFakeBoldText = false
        textPaint.textSkewX = 0f
        textPaint.isUnderlineText = false
        textPaint.isStrikeThruText = false
        boldTypeface = Typeface.create(baseTypeface, Typeface.BOLD)
        italicTypeface = Typeface.create(baseTypeface, Typeface.ITALIC)
        boldItalicTypeface = Typeface.create(baseTypeface, Typeface.BOLD_ITALIC)
        textWidthCache.clear()
        val metrics = textPaint.fontMetrics
        if (transcript != null) {
            // Match the pinned TerminalRenderer's integer pixel metrics, including its top inset.
            textPaint.isSubpixelText = false
            cellWidth = textPaint.measureText("X").coerceAtLeast(0.01f)
            cellHeight = ceil(textPaint.fontSpacing * boundedLineSpacing).coerceAtLeast(1f)
            baselineOffset = -ceil(textPaint.ascent())
            transcriptTopInset = cellHeight - baselineOffset
            return
        }
        cellWidth = max(textPaint.measureText("M"), textSizePixels * 0.55f)
        val fontHeight = metrics.descent - metrics.ascent
        cellHeight = max(1f, fontHeight * boundedLineSpacing)
        baselineOffset = (cellHeight - fontHeight) / 2f - metrics.ascent
    }

    private fun currentFrame(): TerminalRenderFrameV3 =
        engine?.frames?.value ?: transcript?.projection?.frame ?: error("Native terminal view is detached")

    private fun currentTheme(): TerminalThemeV1 = engine?.theme() ?: requireNotNull(transcript).theme

    private fun maxViewportX(): Float = max(
        0f,
        if (transcript != null) (currentFrame().size.columns - max(1, floor(width / cellWidth).toInt())) * cellWidth
        else contentPadding * 2 + currentFrame().size.columns * cellWidth - width,
    )

    private fun maxViewportY(): Float = max(
        0f,
        if (transcript != null) (currentFrame().size.rows - if (transcriptPartialBottom) 1 else transcriptVisibleRows()) * cellHeight
        else contentPadding * 2 + currentFrame().size.rows * cellHeight - height,
    )

    private fun clampViewport() {
        if (engine == null && transcript == null || width <= 0 || height <= 0) return
        viewportX = viewportX.coerceIn(0f, maxViewportX())
        viewportY = viewportY.coerceIn(0f, maxViewportY())
    }

    internal fun transcriptVisibleRows(): Int = max(1, floor((height - transcriptTopInset) / cellHeight).toInt())

    internal fun transcriptTextSizePixels(): Float = textSizePixels

    internal fun transcriptMetricsForTest(): String = "$cellWidth/$cellHeight/$transcriptTopInset"

    internal fun transcriptPresentation(): NativeTranscriptPresentation {
        val row = floor(viewportY / cellHeight).toInt()
        return pendingTranscriptPresentation ?: NativeTranscriptPresentation(
            topLineId = transcript?.projection?.lineIds?.getOrNull(row),
            rowOffset = viewportY / cellHeight - row,
            leftColumn = viewportX / cellWidth,
            atBottom = false,
            allowPartialBottom = transcriptPartialBottom,
        )
    }

    private fun saveTranscriptPresentation() {
        transcript?.savePresentation(this, transcriptPresentation())
    }

    internal fun updateTranscript(saved: NativeTranscriptPresentation?) {
        val history = transcript ?: return
        scroller.forceFinished(true)
        dismissSelection()
        refreshTypeface()
        if (width <= 0 || height <= 0) {
            pendingTranscriptPresentation = saved ?: NativeTranscriptPresentation(null)
            return
        }
        val preferred = history.userTextSizePixels ?: preferredTextSizePixels.roundToInt().toFloat()
        val floorSize = minOf(preferred, TypedValue.applyDimension(
            TypedValue.COMPLEX_UNIT_SP, 10f, resources.displayMetrics,
        ).roundToInt().toFloat())
        textSizePixels = preferred
        updateCellMetrics()
        // The old surface establishes its first visible line at the preferred font before fitting
        // the authoritative PC width. Retain that line even when fitting reveals additional rows.
        val preferredVisibleRows = transcriptVisibleRows()
        if (history.userTextSizePixels == null) {
            var low = floorSize.toInt()
            var high = preferred.toInt()
            var best = low
            while (low <= high) {
                val candidate = (low + high) / 2
                textSizePixels = candidate.toFloat()
                updateCellMetrics()
                if (currentFrame().size.columns * cellWidth <= width) {
                    best = candidate
                    low = candidate + 1
                } else high = candidate - 1
            }
            textSizePixels = best.toFloat()
            updateCellMetrics()
        }
        val initialRows = if (textSizePixels != preferred && transcriptVisibleRows() >= 4) {
            max(4, preferredVisibleRows)
        } else preferredVisibleRows
        val initialTop = (currentFrame().size.rows - initialRows).coerceAtLeast(0)
        val row = history.projection.lineIds.indexOf(saved?.topLineId).coerceAtLeast(0)
        transcriptPartialBottom = saved?.allowPartialBottom == true ||
            saved?.atBottom != false && initialTop > currentFrame().size.rows - transcriptVisibleRows()
        viewportX = (saved?.leftColumn ?: 0f) * cellWidth
        viewportY = if (saved?.atBottom != false) initialTop * cellHeight else (row + saved.rowOffset) * cellHeight
        pendingTranscriptPresentation = null
        clampViewport()
        saveTranscriptPresentation()
        invalidate()
    }

    private fun handleHistoryScrollResult(result: NativeHistoryScrollResult) {
        when (result) {
            NativeHistoryScrollResult.MOVED,
            NativeHistoryScrollResult.UNCHANGED,
            NativeHistoryScrollResult.CANCELLED -> Unit
            NativeHistoryScrollResult.OLDEST_BOUNDARY -> {
                scroller.forceFinished(true)
                onScrollPastTop?.invoke()
            }
            NativeHistoryScrollResult.LIVE_BOUNDARY -> {
                scroller.forceFinished(true)
                followCursor = true
                historyDragPixels = 0f
                ensureCursorVisible()
                onScrollPastBottom?.invoke()
            }
        }
    }

    private fun scrollVertically(owner: NativeTerminalEngine?, distanceY: Float) {
        if (transcript != null) {
            historyDragPixels += distanceY
            val rows = (historyDragPixels / cellHeight).toInt()
            historyDragPixels -= rows * cellHeight
            val before = viewportY
            viewportY = (viewportY + rows * cellHeight).coerceIn(0f, maxViewportY())
            saveTranscriptPresentation()
            if (rows != 0 && viewportY == before) {
                scroller.forceFinished(true)
                if (rows < 0) onScrollPastTop?.invoke() else onScrollPastBottom?.invoke()
            }
            return
        }
        if (distanceY != 0f) followCursor = false
        val beforeY = viewportY
        viewportY = (viewportY + distanceY).coerceIn(0f, maxViewportY())
        val overscrollPixels = distanceY - (viewportY - beforeY)
        if (overscrollPixels == 0f) {
            historyDragPixels = 0f
            return
        }
        if (historyDragPixels != 0f && historyDragPixels.sign != (-overscrollPixels).sign) {
            historyDragPixels = 0f
        }
        historyDragPixels -= overscrollPixels
        val historyRows = (historyDragPixels / cellHeight).toInt()
        if (historyRows == 0) return
        historyDragPixels -= historyRows * cellHeight
        owner?.let { enqueueHistoryScroll(it, historyRows) }
    }

    private fun scrollHorizontally(distance: Float) {
        val delta = if (transcript == null) distance else {
            transcriptHorizontalRemainder += distance
            val columns = (transcriptHorizontalRemainder / cellWidth).toInt()
            transcriptHorizontalRemainder -= columns * cellWidth
            columns * cellWidth
        }
        viewportX = (viewportX + delta).coerceIn(0f, maxViewportX())
    }

    private fun enqueueHistoryScroll(owner: NativeTerminalEngine, historyRows: Int) {
        val dispatch = synchronized(historyQueueLock) {
            pendingHistoryRows = (pendingHistoryRows.toLong() + historyRows.toLong())
                .coerceIn(Int.MIN_VALUE.toLong(), Int.MAX_VALUE.toLong())
                .toInt()
            if (historyWorkerScheduled) return
            historyWorkerScheduled = true
            HistoryWorkerDispatch(
                executor = historyExecutor ?: createHistoryExecutor().also { historyExecutor = it },
                workerGeneration = historyWorkerGeneration,
            )
        }
        runCatching {
            dispatch.executor.execute {
                drainHistoryRequests(owner, dispatch.workerGeneration)
            }
        }.onFailure {
            synchronized(historyQueueLock) {
                if (dispatch.workerGeneration == historyWorkerGeneration) {
                    historyWorkerScheduled = false
                }
            }
        }
    }

    private fun drainHistoryRequests(owner: NativeTerminalEngine, workerGeneration: Long) {
        lastHistoryWorkerThreadName = Thread.currentThread().name
        while (true) {
            val request = synchronized(historyQueueLock) {
                if (workerGeneration != historyWorkerGeneration) {
                    return
                } else if (pendingHistoryRows == 0) {
                    historyWorkerScheduled = false
                    null
                } else {
                    HistoryRequest(
                        rows = pendingHistoryRows.also { pendingHistoryRows = 0 },
                        requestGeneration = historyRequestGeneration,
                        navigationGeneration = historyNavigationGeneration,
                    )
                }
            } ?: return
            val result = runCatching {
                owner.scrollHistoryRows(request.rows, request.navigationGeneration)
            }.getOrElse {
                synchronized(historyQueueLock) {
                    if (workerGeneration == historyWorkerGeneration) {
                        historyWorkerScheduled = false
                    }
                }
                return
            }
            post {
                if (!detached && engine === owner &&
                    request.requestGeneration == historyRequestGeneration
                ) {
                    handleHistoryScrollResult(result)
                }
            }
            val shouldPrefetch = synchronized(historyQueueLock) {
                workerGeneration == historyWorkerGeneration &&
                    pendingHistoryRows == 0 &&
                    request.requestGeneration == historyRequestGeneration
            }
            if (shouldPrefetch && result == NativeHistoryScrollResult.MOVED) {
                runCatching {
                    owner.prefetchHistoryRows(
                        if (request.rows > 0) 1 else -1,
                        request.navigationGeneration,
                    )
                }
            }
        }
    }

    private fun cancelQueuedHistoryRequests() {
        synchronized(historyQueueLock) {
            historyRequestGeneration = Math.addExact(historyRequestGeneration, 1L)
            pendingHistoryRows = 0
        }
    }

    private fun shutdownHistoryExecutor() {
        val executor = synchronized(historyQueueLock) {
            historyRequestGeneration = Math.addExact(historyRequestGeneration, 1L)
            historyWorkerGeneration = Math.addExact(historyWorkerGeneration, 1L)
            pendingHistoryRows = 0
            historyWorkerScheduled = false
            historyExecutor.also { historyExecutor = null }
        }
        executor?.shutdownNow()
    }

    private fun createHistoryExecutor(): ExecutorService =
        Executors.newSingleThreadExecutor { task ->
            Thread(task, "TermLink-NativeHistory").apply { isDaemon = true }
        }

    private fun startFling(axis: ScrollAxis, velocityX: Float, velocityY: Float) {
        if (axis == ScrollAxis.UNDECIDED) return
        followCursor = false
        flingAxis = axis
        lastFlingX = 0
        lastFlingY = 0
        scroller.fling(
            0,
            0,
            if (axis == ScrollAxis.HORIZONTAL) -velocityX.roundToInt() else 0,
            if (axis == ScrollAxis.VERTICAL) -velocityY.roundToInt() else 0,
            -MAX_FLING_DISTANCE,
            MAX_FLING_DISTANCE,
            -MAX_FLING_DISTANCE,
            MAX_FLING_DISTANCE,
        )
        postInvalidateOnAnimation()
    }

    internal fun awaitHistoryRequestsForTest() {
        val executor = synchronized(historyQueueLock) { historyExecutor } ?: return
        executor.submit {}.get(5, TimeUnit.SECONDS)
    }

    private data class HistoryRequest(
        val rows: Int,
        val requestGeneration: Long,
        val navigationGeneration: Long,
    )

    private data class HistoryWorkerDispatch(
        val executor: ExecutorService,
        val workerGeneration: Long,
    )

    internal fun viewportYForTest(): Float = viewportY

    internal fun viewportXForTest(): Float = viewportX

    internal fun positionViewportForTest(column: Int, fraction: Float) {
        viewportX = contentPadding + (column + fraction) * cellWidth
    }

    internal fun isFollowingCursorForTest(): Boolean = followCursor

    internal fun hasActiveFlingForTest(): Boolean = !scroller.isFinished

    internal fun historyWorkerThreadNameForTest(): String? = lastHistoryWorkerThreadName

    internal fun hasHistoryExecutorForTest(): Boolean =
        synchronized(historyQueueLock) { historyExecutor != null }

    internal fun hasSelectionForTest(): Boolean =
        selectionAnchor != null || selectionExtent != null

    internal fun seedSelectionForTest() {
        selectionAnchor = CellPoint(0, 0)
        selectionExtent = CellPoint(0, 0)
        selectionDragActive = true
    }

    internal fun forceTextRunWidthMatchForTest(matches: Boolean) {
        textRunWidthMatchOverrideForTest = matches
    }

    internal fun isCursorFullyVisibleForTest(): Boolean {
        if (engine == null || width <= 0 || height <= 0) return false
        val cursor = currentFrame().cursor
        val cursorLeft = contentPadding + cursor.column * cellWidth
        val cursorRight = cursorLeft + cellWidth
        val cursorTop = contentPadding + cursor.row * cellHeight
        val cursorBottom = cursorTop + cellHeight
        return cursorLeft >= viewportX && cursorRight <= viewportX + width &&
            cursorTop >= viewportY && cursorBottom <= viewportY + height
    }

    internal fun cursorHorizontalBreathingRoomForTest(): Float = minOf(
        cellWidth * CURSOR_EDGE_MARGIN_CELLS,
        max(0f, (width - cellWidth) / 2f),
    )

    internal fun isCursorComfortablyVisibleForTest(): Boolean {
        if (engine == null || width <= 0 || height <= 0) return false
        val cursor = currentFrame().cursor
        val margin = cursorHorizontalBreathingRoomForTest()
        val cursorLeft = contentPadding + cursor.column * cellWidth
        val cursorRight = cursorLeft + cellWidth
        return cursorLeft >= viewportX + margin &&
            cursorRight <= viewportX + width - margin
    }

    private fun cellAt(x: Float, y: Float): CellPoint {
        val frame = currentFrame()
        val column = floor((x + viewportX - contentPadding) / cellWidth).toInt()
            .coerceIn(0, frame.size.columns - 1)
        val row = floor((y + viewportY - contentPadding - transcriptTopInset) / cellHeight).toInt()
            .coerceIn(0, frame.size.rows - 1)
        return CellPoint(column, row)
    }

    private fun selectableCellAt(x: Float, y: Float): CellPoint? {
        val frame = currentFrame()
        val gridX = x + viewportX - contentPadding
        val gridY = y + viewportY - contentPadding - transcriptTopInset
        if (gridX < 0f || gridY < 0f) return null
        var column = floor(gridX / cellWidth).toInt()
        val row = floor(gridY / cellHeight).toInt()
        if (column !in 0 until frame.size.columns || row !in 0 until frame.size.rows) return null

        var cell = frame.rows[row].cells[column]
        // The second half of a wide glyph is still a visible text hit. Resolve
        // it back to the lead cell instead of treating half the glyph as blank.
        if (cell.wideContinuation) {
            if (column == 0) return null
            column -= 1
            cell = frame.rows[row].cells[column]
        }
        val text = frame.graphemes.getOrNull(cell.graphemeId - 1)?.text ?: return null
        return CellPoint(column, row).takeIf { text.isNotBlank() }
    }

    private fun wordSelectionAt(cell: CellPoint): Pair<CellPoint, CellPoint> {
        val frame = currentFrame()
        fun hasText(column: Int): Boolean {
            val value = frame.rows[cell.row].cells[column]
            if (value.wideContinuation) return transcript != null
            return frame.graphemes.getOrNull(value.graphemeId - 1)?.text?.isNotBlank() == true
        }

        var start = cell.column
        var end = cell.column
        while (start > 0 && hasText(start - 1)) start--
        while (end < frame.size.columns - 1 && hasText(end + 1)) end++
        return CellPoint(start, cell.row) to CellPoint(end, cell.row)
    }

    private fun selectionHandlePosition(handle: SelectionHandle): Pair<Float, Float>? {
        val point = when (handle) {
            SelectionHandle.START -> selectionAnchor
            SelectionHandle.END -> selectionExtent
        } ?: return null
        val x = contentPadding + (if (handle == SelectionHandle.START) point.column else point.column + 1) * cellWidth - viewportX
        val y = contentPadding + (point.row + 1) * cellHeight - viewportY
        return x to y
    }

    private fun selectionHandleAt(x: Float, y: Float): SelectionHandle? {
        if (!hasSelectionState()) return null
        val radiusSquared = selectionHandleHitRadius * selectionHandleHitRadius
        return SelectionHandle.entries
            .mapNotNull { handle ->
                val position = selectionHandlePosition(handle) ?: return@mapNotNull null
                val dx = x - position.first
                val dy = y - position.second
                (dx * dx + dy * dy).takeIf { it <= radiusSquared }?.let { handle to it }
            }
            .minByOrNull { it.second }
            ?.first
    }

    private fun updateSelectionHandle(handle: SelectionHandle, x: Float, y: Float) {
        val edge = selectionHandleHitRadius * 1.5f
        val edgeDelta = when {
            y < edge -> -(edge - y) * 0.45f
            y > height - edge -> (y - (height - edge)) * 0.45f
            else -> 0f
        }
        if (edgeDelta != 0f) {
            val distance = if (transcript != null) cellHeight * edgeDelta.sign else edgeDelta
            viewportY = (viewportY + distance).coerceIn(0f, maxViewportY())
            saveTranscriptPresentation()
        }
        val point = cellAt(x, y)
        when (handle) {
            SelectionHandle.START -> selectionAnchor = point
            SelectionHandle.END -> selectionExtent = point
        }
        postInvalidateOnAnimation()
    }

    private fun drawSelectionHandles(
        canvas: Canvas,
        frame: TerminalRenderFrameV3,
        theme: TerminalThemeV1,
    ) {
        if (!hasSelectionState()) return
        selectionHandlePaint.color = theme.selectionBackground.value
        selectionHandlePaint.style = Paint.Style.FILL
        SelectionHandle.entries.forEach { handle ->
            val position = selectionHandlePosition(handle) ?: return@forEach
            val x = position.first
            val y = position.second
            if (x < -selectionHandleHitRadius || x > width + selectionHandleHitRadius ||
                y < -selectionHandleHitRadius || y > height + selectionHandleHitRadius
            ) return@forEach
            if (transcript != null) {
                // Material-style blue teardrops, drawn from circle/rectangle primitives.
                val radius = 12f * density
                val centerX = if (handle == SelectionHandle.START) x - radius else x + radius
                selectionHandlePaint.color = 0xff2196f3.toInt()
                canvas.drawCircle(centerX, y + radius, radius, selectionHandlePaint)
                canvas.drawRect(minOf(x, centerX), y, maxOf(x, centerX), y + radius, selectionHandlePaint)
                return@forEach
            }
            canvas.drawRect(
                x - selectionHandleRadius * 0.18f,
                y - selectionHandleRadius,
                x + selectionHandleRadius * 0.18f,
                y + selectionHandleRadius,
                selectionHandlePaint,
            )
            canvas.drawCircle(x, y + selectionHandleRadius, selectionHandleRadius, selectionHandlePaint)
        }
    }

    private fun mousePosition(x: Float, y: Float) = cellAt(x, y).let { cell ->
        terminalMousePosition(
            column = cell.column,
            row = cell.row,
            pixelX = (x + viewportX).roundToInt().coerceAtLeast(0),
            pixelY = (y + viewportY).roundToInt().coerceAtLeast(0),
        )
    }

    private fun eventModifiers(event: MotionEvent): TerminalInputModifiers = TerminalInputModifiers(
        shift = event.metaState and KeyEvent.META_SHIFT_ON != 0,
        alt = event.metaState and KeyEvent.META_ALT_ON != 0,
        control = event.metaState and KeyEvent.META_CTRL_ON != 0,
        meta = event.metaState and KeyEvent.META_META_ON != 0,
    )

    private fun isSelected(column: Int, row: Int, frame: TerminalRenderFrameV3): Boolean {
        val anchor = selectionAnchor ?: return false
        val extent = selectionExtent ?: return false
        val first = minOf(anchor.linear(frame), extent.linear(frame))
        val last = maxOf(anchor.linear(frame), extent.linear(frame))
        val value = row * frame.size.columns + column
        return value in first..last
    }

    private fun copySelection() {
        val frame = currentFrame()
        val anchor = selectionAnchor ?: return
        val extent = selectionExtent ?: return
        val first = minOf(anchor.linear(frame), extent.linear(frame))
        val last = maxOf(anchor.linear(frame), extent.linear(frame))
        val selected = buildString {
            for (rowIndex in frame.rows.indices) {
                val rowStart = rowIndex * frame.size.columns
                val start = max(0, first - rowStart)
                val end = minOf(frame.size.columns - 1, last - rowStart)
                if (start > end) continue
                val lineStart = length
                val firstColumn = if (transcript != null && frame.rows[rowIndex].cells[start].wideContinuation) start - 1 else start
                for (column in firstColumn..end) {
                    val cell = frame.rows[rowIndex].cells[column]
                    if (!cell.wideContinuation) {
                        append(frame.graphemes.getOrNull(cell.graphemeId - 1)?.text ?: " ")
                    }
                }
                if (transcript != null && !(frame.rows[rowIndex].softWrapped && end == frame.size.columns - 1)) {
                    while (length > lineStart && last() == ' ') setLength(length - 1)
                }
                if (rowStart + end < last && !frame.rows[rowIndex].softWrapped) append('\n')
            }
        }
        context.getSystemService(ClipboardManager::class.java)?.setPrimaryClip(
            ClipData.newPlainText("Terminal", selected),
        )
    }

    private fun dismissSelection(): Boolean {
        val hadSelection = hasSelectionState()
        val mode = selectionActionMode
        if (mode != null) {
            mode.finish()
            // Some platform implementations dispatch destruction later. The
            // terminal surface must still leave selection immediately.
            if (selectionActionMode === mode) clearSelectionState()
        } else if (hadSelection) {
            clearSelectionState()
        }
        return hadSelection
    }

    private fun hasSelectionState(): Boolean = selectionActionMode != null ||
        selectionAnchor != null || selectionExtent != null

    private fun clearSelectionState() {
        selectionActionMode = null
        selectionAnchor = null
        selectionExtent = null
        selectionDragActive = false
        selectionHandleDrag = null
        invalidate()
    }

    /** History preserves the pinned Termux renderer's metrics and synthetic styles. */
    private fun drawTranscript(canvas: Canvas, frame: TerminalRenderFrameV3, theme: TerminalThemeV1) {
        canvas.drawColor(theme.background.value)
        val first = floor(viewportY / cellHeight).toInt().coerceAtLeast(0)
        val last = minOf(frame.rows.size, first + transcriptVisibleRows())
        val measure = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            typeface = baseTypeface
            textSize = textSizePixels
        }
        fun color(value: TerminalRenderColorV1, default: Int, bright: Boolean): Int = when (value) {
            TerminalRenderColorV1.Default -> default
            is TerminalRenderColorV1.Rgb -> opaqueRgb(value.value)
            is TerminalRenderColorV1.Indexed -> {
                val index = if (bright && value.index < 8) value.index + 8 else value.index
                if (index < 16) theme.ansiColors[index].value
                else frame.palette.colorsRgb[index]?.let(::opaqueRgb) ?: xtermColor(index)
            }
        }
        for (rowIndex in first until last) {
            val row = frame.rows[rowIndex]
            var column = 0
            while (column < row.cells.size) {
                val firstCell = row.cells[column]
                if (firstCell.wideContinuation) { column++; continue }
                val rendition = frame.renditions[firstCell.renditionId]
                val selected = isSelected(column, rowIndex, frame)
                val start = column
                var measuredWidth = 0f
                val text = StringBuilder()
                while (column < row.cells.size) {
                    val cell = row.cells[column]
                    if (cell.renditionId != firstCell.renditionId || isSelected(column, rowIndex, frame) != selected) break
                    val glyph = frame.graphemes[cell.graphemeId - 1]
                    val leadLength = Character.charCount(glyph.text.codePointAt(0))
                    val glyphWidth = if (glyph.text.codePointAt(0) < 127) {
                        measure.measureText(glyph.text, 0, leadLength)
                    } else textPaint.measureText(glyph.text, 0, leadLength)
                    val mismatch = abs(glyphWidth / cellWidth - glyph.displayWidth) > 0.01f
                    if (mismatch && column > start) break
                    text.append(glyph.text)
                    measuredWidth += glyphWidth
                    column += glyph.displayWidth
                    if (mismatch) break
                }
                val bold = rendition.bold || rendition.blink
                var foreground = color(rendition.foreground, theme.foreground.value, bold)
                var background = color(rendition.background, theme.background.value, false)
                if (rendition.inverse xor selected) {
                    val swap = foreground
                    foreground = background
                    background = swap
                }
                val left = start * cellWidth - viewportX
                val allotted = (column - start) * cellWidth
                val top = transcriptTopInset + rowIndex * cellHeight - viewportY
                if (background != theme.background.value) {
                    shapePaint.color = background
                    canvas.drawRect(left, top, left + allotted, top + cellHeight, shapePaint)
                }
                if (!rendition.conceal && measuredWidth > 0f) {
                    if (rendition.faint) {
                        fun dim(shift: Int) = ((foreground ushr shift) and 255) * 2 / 3
                        foreground = opaqueRgb((dim(16) shl 16) or (dim(8) shl 8) or dim(0))
                    }
                    textPaint.typeface = baseTypeface
                    textPaint.color = foreground
                    textPaint.alpha = 255
                    textPaint.isFakeBoldText = bold
                    textPaint.textSkewX = if (rendition.italic) -0.35f else 0f
                    textPaint.isUnderlineText = rendition.underline
                    textPaint.isStrikeThruText = rendition.strikethrough
                    val save = canvas.save()
                    canvas.translate(left, 0f)
                    if (abs(measuredWidth / cellWidth - (column - start)) > 0.01f) {
                        canvas.scale(allotted / measuredWidth, 1f)
                    }
                    canvas.drawTextRun(text, 0, text.length, 0, text.length, 0f, top + baselineOffset, false, textPaint)
                    canvas.restoreToCount(save)
                }
            }
        }
        drawSelectionHandles(canvas, frame, theme)
    }

    private fun configureTextPaint(rendition: TerminalRenderRenditionV3, color: Int) {
        textPaint.color = color
        textPaint.alpha = if (rendition.faint) 166 else 255
        textPaint.typeface = when {
            rendition.bold && rendition.italic -> boldItalicTypeface
            rendition.bold -> boldTypeface
            rendition.italic -> italicTypeface
            else -> baseTypeface
        }
    }

    private fun measuredTextWidth(text: String, rendition: TerminalRenderRenditionV3): Float {
        val style = when {
            rendition.bold && rendition.italic -> Typeface.BOLD_ITALIC
            rendition.bold -> Typeface.BOLD
            rendition.italic -> Typeface.ITALIC
            else -> Typeface.NORMAL
        }
        val key = TextMeasureKey(text, style)
        return textWidthCache[key] ?: textPaint.measureText(text).also { textWidthCache[key] = it }
    }

    private fun drawTextRow(
        canvas: Canvas,
        frame: TerminalRenderFrameV3,
        rowIndex: Int,
        firstColumn: Int,
        lastColumn: Int,
        colorsByRendition: Array<EffectiveColors>,
    ) {
        val row = frame.rows[rowIndex]
        val top = contentPadding + rowIndex * cellHeight - viewportY
        var column = firstColumn
        while (column <= lastColumn) {
            val cell = row.cells[column]
            val grapheme = if (cell.wideContinuation) {
                null
            } else {
                frame.graphemes.getOrNull(cell.graphemeId - 1)
            }
            val rendition = frame.renditions[cell.renditionId]
            if (grapheme == null || rendition.conceal || grapheme.text.isEmpty()) {
                column++
                continue
            }
            if (!grapheme.isBatchableAscii()) {
                drawSingleGrapheme(
                    canvas,
                    column,
                    top,
                    grapheme.text,
                    grapheme.displayWidth,
                    rendition,
                    colorsByRendition[cell.renditionId],
                )
                column++
                continue
            }

            val runStart = column
            val renditionId = cell.renditionId
            val runText = StringBuilder()
            while (column <= lastColumn) {
                val runCell = row.cells[column]
                if (runCell.wideContinuation || runCell.renditionId != renditionId) break
                val runGrapheme = frame.graphemes.getOrNull(runCell.graphemeId - 1) ?: break
                if (!runGrapheme.isBatchableAscii()) break
                runText.append(runGrapheme.text)
                column++
            }

            val text = runText.toString()
            val colors = colorsByRendition[renditionId]
            configureTextPaint(rendition, colors.foreground)
            val textWidth = if (text.length >= MIN_BATCHED_TEXT_CELLS) matchingTextRunWidth(text) else null
            if (textWidth != null) {
                drawTextRun(canvas, runStart, top, text, textWidth, rendition, colors.foreground)
            } else {
                for (fallbackColumn in runStart until column) {
                    val fallbackCell = row.cells[fallbackColumn]
                    val fallbackGrapheme = frame.graphemes[fallbackCell.graphemeId - 1]
                    drawSingleGrapheme(
                        canvas,
                        fallbackColumn,
                        top,
                        fallbackGrapheme.text,
                        fallbackGrapheme.displayWidth,
                        rendition,
                        colors,
                    )
                }
            }
        }
    }

    private fun drawTextRun(
        canvas: Canvas,
        startColumn: Int,
        top: Float,
        text: String,
        textWidth: Float,
        rendition: TerminalRenderRenditionV3,
        color: Int,
    ) {
        val left = contentPadding + startColumn * cellWidth - viewportX
        val allottedWidth = cellWidth * text.length
        if (left + allottedWidth < 0f || left > width) return
        val x = left + ((allottedWidth - textWidth) / 2f).coerceAtLeast(0f)
        canvas.drawText(text, x, top + baselineOffset, textPaint)
        drawDecorations(canvas, left, top, allottedWidth, rendition, color)
    }

    private fun drawSingleGrapheme(
        canvas: Canvas,
        column: Int,
        top: Float,
        text: String,
        displayWidth: Int,
        rendition: TerminalRenderRenditionV3,
        colors: EffectiveColors,
    ) {
        val left = contentPadding + column * cellWidth - viewportX
        val allottedWidth = cellWidth * displayWidth
        if (left + allottedWidth < 0f || left > width) return
        configureTextPaint(rendition, colors.foreground)
        val textWidth = measuredTextWidth(text, rendition)
        val x = left + ((allottedWidth - textWidth) / 2f).coerceAtLeast(0f)
        canvas.drawText(text, x, top + baselineOffset, textPaint)
        drawDecorations(canvas, left, top, allottedWidth, rendition, colors.foreground)
    }

    private fun matchingTextRunWidth(text: String): Float? {
        textRunWidthMatchOverrideForTest?.let { matches ->
            return if (matches) cellWidth * text.length else null
        }
        val expectedWidth = cellWidth * text.length
        val measuredWidth = textPaint.measureText(text)
        val runTolerance = max(TEXT_RUN_WIDTH_TOLERANCE_PX, expectedWidth * TEXT_RUN_WIDTH_TOLERANCE_RATIO)
        return measuredWidth.takeIf { abs(it - expectedWidth) <= runTolerance }
    }

    private fun drawBackgroundRun(
        canvas: Canvas,
        startColumn: Int,
        endColumn: Int,
        top: Float,
        color: Int,
    ) {
        val left = contentPadding + startColumn * cellWidth - viewportX
        val right = contentPadding + endColumn * cellWidth - viewportX
        shapePaint.color = color
        canvas.drawRect(left, top, right, top + cellHeight, shapePaint)
    }

    private fun refreshTypeface(force: Boolean = false) {
        val hint = (engine?.theme() ?: transcript?.theme)?.fontFamilyHint
        if (!force && fontFamilyInitialized && hint == appliedFontFamilyHint) return
        baseTypeface = TerminalTypefaceResolver.resolve(context, hint)
        appliedFontFamilyHint = hint
        fontFamilyInitialized = true
        updateCellMetrics()
        if (isAttachedToWindow) requestLayout()
    }

    private fun drawDecorations(
        canvas: Canvas,
        left: Float,
        top: Float,
        width: Float,
        rendition: TerminalRenderRenditionV3,
        color: Int,
    ) {
        shapePaint.color = color
        shapePaint.strokeWidth = max(1f, density)
        when (rendition.underlineStyle) {
            TerminalRenderUnderlineStyleV3.NONE -> Unit
            TerminalRenderUnderlineStyleV3.SINGLE ->
                canvas.drawLine(left, top + cellHeight - density, left + width, top + cellHeight - density, shapePaint)
            TerminalRenderUnderlineStyleV3.DOUBLE -> {
                canvas.drawLine(left, top + cellHeight - density, left + width, top + cellHeight - density, shapePaint)
                canvas.drawLine(left, top + cellHeight - 3 * density, left + width, top + cellHeight - 3 * density, shapePaint)
            }
            TerminalRenderUnderlineStyleV3.CURLY -> {
                val y = top + cellHeight - 2 * density
                var x = left
                var up = true
                while (x < left + width) {
                    val next = minOf(x + 3 * density, left + width)
                    canvas.drawLine(x, y + if (up) -density else density, next, y + if (up) density else -density, shapePaint)
                    x = next
                    up = !up
                }
            }
            TerminalRenderUnderlineStyleV3.DOTTED -> {
                var x = left
                val y = top + cellHeight - density
                while (x < left + width) {
                    canvas.drawCircle(x, y, density / 2f, shapePaint)
                    x += 3 * density
                }
            }
            TerminalRenderUnderlineStyleV3.DASHED -> {
                var x = left
                val y = top + cellHeight - density
                while (x < left + width) {
                    canvas.drawLine(x, y, minOf(x + 4 * density, left + width), y, shapePaint)
                    x += 6 * density
                }
            }
        }
        if (rendition.strikethrough) {
            canvas.drawLine(left, top + cellHeight * 0.52f, left + width, top + cellHeight * 0.52f, shapePaint)
        }
        if (rendition.overline) canvas.drawLine(left, top + density, left + width, top + density, shapePaint)
    }

    private fun drawCursor(canvas: Canvas, frame: TerminalRenderFrameV3, theme: TerminalThemeV1) {
        if (!frame.cursor.visible) return
        val left = contentPadding + frame.cursor.column * cellWidth - viewportX
        val top = contentPadding + frame.cursor.row * cellHeight - viewportY
        if (left + cellWidth < 0 || left > width || top + cellHeight < 0 || top > height) return
        shapePaint.color = frame.palette.defaultCursorRgb?.let(::opaqueRgb) ?: theme.cursor.value
        when (frame.cursor.shape) {
            TerminalRenderCursorShapeV2.BLOCK -> {
                shapePaint.alpha = 112
                canvas.drawRect(left, top, left + cellWidth, top + cellHeight, shapePaint)
                shapePaint.alpha = 255
            }
            TerminalRenderCursorShapeV2.UNDERLINE ->
                canvas.drawRect(left, top + cellHeight - 2 * density, left + cellWidth, top + cellHeight, shapePaint)
            TerminalRenderCursorShapeV2.BAR ->
                canvas.drawRect(left, top, left + 2 * density, top + cellHeight, shapePaint)
        }
    }

    private data class CellPoint(val column: Int, val row: Int) {
        fun linear(frame: TerminalRenderFrameV3): Int = row * frame.size.columns + column
    }

    private data class TextMeasureKey(val text: String, val style: Int)

    private companion object {
        const val MAX_FLING_DISTANCE = 1_000_000
        const val TEXT_WIDTH_CACHE_SIZE = 512
        const val MIN_BATCHED_TEXT_CELLS = 2
        const val TEXT_RUN_WIDTH_TOLERANCE_PX = 0.5f
        const val TEXT_RUN_WIDTH_TOLERANCE_RATIO = 0.001f
        const val CURSOR_EDGE_MARGIN_CELLS = 2f
    }
}

private fun TerminalRenderGraphemeV1.isBatchableAscii(): Boolean =
    displayWidth == 1 && text.length == 1 && text[0] in ' '..'~'

private enum class ScrollAxis { UNDECIDED, HORIZONTAL, VERTICAL }

private data class EffectiveColors(val foreground: Int, val background: Int)

private fun effectiveColors(
    frame: TerminalRenderFrameV3,
    rendition: TerminalRenderRenditionV3,
    theme: TerminalThemeV1,
): EffectiveColors {
    var foreground = resolveColor(
        rendition.foreground,
        frame.palette.defaultForegroundRgb?.let(::opaqueRgb) ?: theme.foreground.value,
        frame,
        theme,
        rendition.bold,
    )
    var background = resolveColor(
        rendition.background,
        frame.palette.defaultBackgroundRgb?.let(::opaqueRgb) ?: theme.background.value,
        frame,
        theme,
        false,
    )
    if (rendition.inverse.xor(frame.reverseVideo)) {
        val swap = foreground
        foreground = background
        background = swap
    }
    return EffectiveColors(foreground, background)
}

private fun effectiveDefaultBackground(frame: TerminalRenderFrameV3, theme: TerminalThemeV1): Int =
    frame.palette.defaultBackgroundRgb?.let(::opaqueRgb)?.let { background ->
        if (frame.reverseVideo) {
            frame.palette.defaultForegroundRgb?.let(::opaqueRgb) ?: theme.foreground.value
        } else background
    } ?: if (frame.reverseVideo) theme.foreground.value else theme.background.value

private fun resolveColor(
    color: TerminalRenderColorV1,
    defaultColor: Int,
    frame: TerminalRenderFrameV3,
    theme: TerminalThemeV1,
    bold: Boolean,
): Int = when (color) {
    TerminalRenderColorV1.Default -> defaultColor
    is TerminalRenderColorV1.Rgb -> opaqueRgb(color.value)
    is TerminalRenderColorV1.Indexed -> {
        val requested = if (bold && theme.boldAsBright && color.index in 0..7) color.index + 8 else color.index
        frame.palette.colorsRgb[requested]?.let(::opaqueRgb)
            ?: if (requested < theme.ansiColors.size) theme.ansiColors[requested].value
            else xtermColor(requested)
    }
}

private fun opaqueRgb(rgb: Int): Int = 0xff000000.toInt() or rgb

private fun xtermColor(index: Int): Int = when {
    index in 16..231 -> {
        val value = index - 16
        val red = value / 36
        val green = value / 6 % 6
        val blue = value % 6
        opaqueRgb((cubeComponent(red) shl 16) or (cubeComponent(green) shl 8) or cubeComponent(blue))
    }
    index in 232..255 -> {
        val value = 8 + (index - 232) * 10
        opaqueRgb((value shl 16) or (value shl 8) or value)
    }
    else -> 0xff000000.toInt()
}

private fun cubeComponent(value: Int): Int = if (value == 0) 0 else 55 + value * 40
