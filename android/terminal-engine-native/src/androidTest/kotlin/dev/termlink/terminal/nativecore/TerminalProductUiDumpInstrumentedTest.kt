package dev.termlink.terminal.nativecore

import android.accessibilityservice.AccessibilityServiceInfo
import android.graphics.Rect
import android.os.SystemClock
import android.view.accessibility.AccessibilityNodeInfo
import androidx.annotation.Keep
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.OutputStream
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.xmlpull.v1.XmlSerializer
import android.util.Xml

/**
 * Captures the product's currently visible accessibility windows without
 * starting or restarting an activity. This is deliberately a test-apk-local
 * evidence file: the companion PowerShell helper reads it with run-as.
 */
@Keep
@RunWith(AndroidJUnit4::class)
class TerminalProductUiDumpInstrumentedTest {
    @Test
    fun dumpTargetUiHierarchy() {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val targetPackage = InstrumentationRegistry.getArguments()
            .getString(ARG_TARGET_PACKAGE)
            ?.trim()
            ?: error("target_package instrumentation argument is required")
        require(PACKAGE_PATTERN.matches(targetPackage)) {
            "target_package is not a valid Android package name"
        }

        val output = File(instrumentation.context.filesDir, OUTPUT_NAME)
        output.delete()
        val capture = captureWindows(instrumentation.uiAutomation, targetPackage)
        assertTrue("no accessible nodes for target package", capture.nodeCount > 0)
        val bytes = ByteArrayOutputStream()
        writeHierarchy(bytes, capture, targetPackage)
        assertTrue("accessibility hierarchy exceeds $MAX_OUTPUT_BYTES bytes", bytes.size() <= MAX_OUTPUT_BYTES)
        output.writeBytes(bytes.toByteArray())
        println("TERMINAL_UI_HIERARCHY windows=${capture.windows.size} nodes=${capture.nodeCount}")
    }

    private fun captureWindows(
        automation: android.app.UiAutomation,
        targetPackage: String,
    ): Capture {
        val current = automation.serviceInfo ?: AccessibilityServiceInfo()
        current.flags = current.flags or
            AccessibilityServiceInfo.FLAG_RETRIEVE_INTERACTIVE_WINDOWS or
            AccessibilityServiceInfo.FLAG_REPORT_VIEW_IDS
        automation.serviceInfo = current

        val deadline = SystemClock.uptimeMillis() + WINDOW_WAIT_MILLIS
        var lastWindowCount = 0
        while (true) {
            val windows = automation.windows ?: emptyList()
            lastWindowCount = windows.size
            val budget = NodeBudget(0)
            val captured = ArrayList<CapturedWindow>()
            var nodeCount = 0
            for (window in windows) {
                val root = try { window.root } catch (_: RuntimeException) { null } ?: continue
                val packageName = root.packageName?.toString()
                if (packageName != null && packageName != targetPackage) continue
                val node = captureNode(root, targetPackage, budget) ?: continue
                nodeCount = budget.used
                captured += CapturedWindow(
                    packageName = targetPackage,
                    bounds = boundsOf(window),
                    root = node.value,
                )
            }
            if (captured.isNotEmpty() && nodeCount > 0) {
                // Compose popups can appear one frame after the host window is
                // visible. Compare one delayed sample and keep the richer tree;
                // this remains bounded and does not wait for a popup in normal
                // terminal captures.
                SystemClock.sleep(POPUP_STABILIZE_MILLIS)
                val retryBudget = NodeBudget(0)
                val retryCaptured = ArrayList<CapturedWindow>()
                var retryNodeCount = 0
                for (window in (automation.windows ?: emptyList())) {
                    val root = try { window.root } catch (_: RuntimeException) { null } ?: continue
                    val packageName = root.packageName?.toString()
                    if (packageName != null && packageName != targetPackage) continue
                    val node = captureNode(root, targetPackage, retryBudget) ?: continue
                    retryNodeCount = retryBudget.used
                    retryCaptured += CapturedWindow(
                        packageName = targetPackage,
                        bounds = boundsOf(window),
                        root = node.value,
                    )
                }
                if (retryCaptured.size > captured.size ||
                    (retryCaptured.size == captured.size && retryNodeCount > nodeCount)
                ) {
                    return Capture(retryCaptured, retryNodeCount)
                }
                return Capture(captured, nodeCount)
            }
            if (SystemClock.uptimeMillis() >= deadline) {
                error("target package has no accessible window (windows=$lastWindowCount)")
            }
            SystemClock.sleep(POLL_MILLIS)
        }
    }

    private fun captureNode(
        node: AccessibilityNodeInfo,
        targetPackage: String,
        budget: NodeBudget,
        depth: Int = 0,
    ): NodeCapture? {
        if (depth > MAX_DEPTH) error("accessibility hierarchy exceeds depth $MAX_DEPTH")
        val packageName = node.packageName?.toString()
        if (packageName != null && packageName != targetPackage) return null
        budget.used++
        if (budget.used > MAX_NODES) error("accessibility hierarchy exceeds $MAX_NODES nodes")
        val children = ArrayList<CapturedNode>()
        for (index in 0 until node.childCount) {
            val child = try { node.getChild(index) } catch (_: RuntimeException) { null } ?: continue
            val captured = captureNode(child, targetPackage, budget, depth + 1)
            if (captured != null) children += captured.value
        }
        return NodeCapture(
            value = CapturedNode(
                packageName = targetPackage,
                // Password text must never leave the device in test evidence.
                text = if (node.isPassword) "" else node.text?.toString().orEmpty(),
                contentDescription = node.contentDescription?.toString().orEmpty(),
                className = node.className?.toString().orEmpty(),
                bounds = boundsOf(node),
                clickable = node.isClickable,
                enabled = node.isEnabled,
                checkable = node.isCheckable,
                checked = node.isChecked,
                focused = node.isFocused,
                password = node.isPassword,
                selected = node.isSelected,
                children = children,
            ),
        )
    }

    private fun writeHierarchy(output: OutputStream, capture: Capture, targetPackage: String) {
        val serializer = Xml.newSerializer()
        serializer.setOutput(output, Charsets.UTF_8.name())
        serializer.startDocument(Charsets.UTF_8.name(), true)
        serializer.startTag(null, "hierarchy")
        serializer.attribute(null, "rotation", "0")
        for (window in capture.windows) {
            serializer.startTag(null, "window")
            serializer.attribute(null, "package", targetPackage)
            serializer.attribute(null, "bounds", window.bounds)
            writeNode(serializer, window.root)
            serializer.endTag(null, "window")
        }
        serializer.endTag(null, "hierarchy")
        serializer.endDocument()
        serializer.flush()
    }

    private fun writeNode(serializer: XmlSerializer, node: CapturedNode) {
        serializer.startTag(null, "node")
        serializer.attribute(null, "package", node.packageName)
        serializer.attribute(null, "class", node.className)
        serializer.attribute(null, "text", node.text)
        serializer.attribute(null, "content-desc", node.contentDescription)
        serializer.attribute(null, "bounds", node.bounds)
        serializer.attribute(null, "clickable", node.clickable.toString())
        serializer.attribute(null, "enabled", node.enabled.toString())
        serializer.attribute(null, "checkable", node.checkable.toString())
        serializer.attribute(null, "checked", node.checked.toString())
        serializer.attribute(null, "focused", node.focused.toString())
        serializer.attribute(null, "password", node.password.toString())
        serializer.attribute(null, "selected", node.selected.toString())
        node.children.forEach { writeNode(serializer, it) }
        serializer.endTag(null, "node")
    }

    private fun boundsOf(node: AccessibilityNodeInfo): String {
        val bounds = Rect()
        node.getBoundsInScreen(bounds)
        return formatBounds(bounds)
    }

    private fun boundsOf(window: android.view.accessibility.AccessibilityWindowInfo): String {
        val bounds = Rect()
        window.getBoundsInScreen(bounds)
        return formatBounds(bounds)
    }

    private fun formatBounds(bounds: Rect): String =
        "[${bounds.left},${bounds.top}][${bounds.right},${bounds.bottom}]"

    private data class NodeBudget(var used: Int)
    private data class NodeCapture(val value: CapturedNode)
    private data class CapturedNode(
        val packageName: String,
        val className: String,
        val text: String,
        val contentDescription: String,
        val bounds: String,
        val clickable: Boolean,
        val enabled: Boolean,
        val checkable: Boolean,
        val checked: Boolean,
        val focused: Boolean,
        val password: Boolean,
        val selected: Boolean,
        val children: List<CapturedNode>,
    )
    private data class CapturedWindow(
        val packageName: String,
        val bounds: String,
        val root: CapturedNode,
    )
    private data class Capture(val windows: List<CapturedWindow>, val nodeCount: Int)

    private companion object {
        const val ARG_TARGET_PACKAGE = "target_package"
        const val OUTPUT_NAME = "terminal-ui-hierarchy.xml"
        const val MAX_NODES = 5000
        const val MAX_DEPTH = 256
        const val MAX_OUTPUT_BYTES = 1024 * 1024
        const val WINDOW_WAIT_MILLIS = 5000L
        const val POLL_MILLIS = 100L
        const val POPUP_STABILIZE_MILLIS = 350L
        val PACKAGE_PATTERN = Regex("[A-Za-z][A-Za-z0-9_]*(\\.[A-Za-z][A-Za-z0-9_]*)+")
    }
}
