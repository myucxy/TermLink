package dev.termlink.product

import android.content.ClipData
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.ClipEntry
import androidx.compose.ui.platform.LocalClipboard
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.LinkAnnotation
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextLinkStyles
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.withLink
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.launch
import org.commonmark.parser.Parser
import org.commonmark.ext.gfm.tables.*
import org.commonmark.node.*
import org.commonmark.node.Text as MarkdownText
import java.net.URI

private val aiMarkdownParser = Parser.builder().extensions(listOf(TablesExtension.create())).build()
internal fun aiMarkdown(text: String): Node = aiMarkdownParser.parse(text)
private fun Node.children(): List<Node> = generateSequence(firstChild) { it.next }.toList()
internal fun aiWebLink(destination: String): Boolean = runCatching {
    val uri = URI(destination)
    uri.scheme?.lowercase() in setOf("https", "http") && !uri.host.isNullOrBlank() && uri.userInfo == null
}.getOrDefault(false)

// Only explicit http(s) links are actionable. No HTML execution or remote image fetching.
internal fun aiInline(node: Node, primary: Color, codeBackground: Color): AnnotatedString = buildAnnotatedString {
    fun visit(value: Node, depth: Int) {
        if (depth > 64) return
        when (value) {
            is MarkdownText -> append(value.literal)
            is Code -> withStyle(SpanStyle(fontFamily = FontFamily.Monospace, background = codeBackground)) { append(value.literal) }
            is SoftLineBreak -> append("\n")
            is HardLineBreak -> append("\n")
            is HtmlInline -> append(value.literal)
            is Emphasis -> withStyle(SpanStyle(fontStyle = FontStyle.Italic)) { value.children().forEach { visit(it, depth + 1) } }
            is StrongEmphasis -> withStyle(SpanStyle(fontWeight = FontWeight.SemiBold)) { value.children().forEach { visit(it, depth + 1) } }
            is Link -> if (aiWebLink(value.destination)) {
                withLink(LinkAnnotation.Url(value.destination, TextLinkStyles(SpanStyle(color = primary, textDecoration = TextDecoration.Underline)))) {
                    value.children().forEach { visit(it, depth + 1) }
                }
            } else {
                value.children().forEach { visit(it, depth + 1) }
                append(" (${value.destination})")
            }
            is Image -> { append("[图片] "); value.children().forEach { visit(it, depth + 1) } }
            else -> value.children().forEach { visit(it, depth + 1) }
        }
    }
    visit(node, 0)
}

@Composable
internal fun AiCopyAction(text: String, label: String = "复制") {
    val clipboard = LocalClipboard.current
    val scope = rememberCoroutineScope()
    TextButton(onClick = { scope.launch { clipboard.setClipEntry(ClipEntry(ClipData.newPlainText("AI", text))) } },
        contentPadding = PaddingValues(horizontal = 8.dp, vertical = 0.dp)) { Text(label, style = MaterialTheme.typography.labelSmall) }
}

@Composable
internal fun AiReplyContent(text: String) {
    val document = remember(text) { aiMarkdown(text) }
    SelectionContainer { AiMarkdownBlocks(document.children()) }
}

@Composable
private fun AiMarkdownBlocks(nodes: List<Node>, depth: Int = 0) {
    val primary = MaterialTheme.colorScheme.primary
    val codeBackground = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = .5f)
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        nodes.forEach { node ->
            when {
                node is FencedCodeBlock -> AiCodeBlock(node.literal.removeSuffix("\n"), node.info.substringBefore(' ').take(40))
                node is IndentedCodeBlock -> AiCodeBlock(node.literal.removeSuffix("\n"), "")
                node is Heading -> Text(aiInline(node, primary, codeBackground), style = when (node.level) {
                    1 -> MaterialTheme.typography.titleLarge
                    2 -> MaterialTheme.typography.titleMedium
                    else -> MaterialTheme.typography.titleSmall
                }, fontWeight = FontWeight.SemiBold)
                node is ThematicBreak -> HorizontalDivider(Modifier.padding(vertical = 4.dp))
                node is HtmlBlock -> Text(node.literal, style = MaterialTheme.typography.bodyMedium)
                node is TableBlock -> AiMarkdownTable(node)
                (node is BulletList || node is OrderedList) && depth < 12 -> {
                    val start = (node as? OrderedList)?.markerStartNumber ?: 1
                    node.children().forEachIndexed { index, item ->
                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            Text(if (node is OrderedList) "${start + index}." else "•", style = MaterialTheme.typography.bodyMedium)
                            Box(Modifier.weight(1f)) { AiMarkdownBlocks(item.children(), depth + 1) }
                        }
                    }
                }
                node is BlockQuote && depth < 12 -> Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text("▎", color = MaterialTheme.colorScheme.outline)
                    Box(Modifier.weight(1f)) { AiMarkdownBlocks(node.children(), depth + 1) }
                }
                else -> Text(aiInline(node, primary, codeBackground), style = MaterialTheme.typography.bodyMedium)
            }
        }
    }
}

@Composable
private fun AiCodeBlock(text: String, language: String) {
    Surface(Modifier.fillMaxWidth(), shape = RoundedCornerShape(8.dp), color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = .5f)) {
        Column {
            Row(Modifier.fillMaxWidth().padding(start = 10.dp, end = 2.dp), verticalAlignment = Alignment.CenterVertically) {
                Text(language.ifBlank { "代码" }, Modifier.weight(1f), style = MaterialTheme.typography.labelSmall)
                AiCopyAction(text, "复制代码")
            }
            Text(text, Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 10.dp, vertical = 8.dp),
                fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.bodySmall, softWrap = false)
        }
    }
}

@Composable
private fun AiMarkdownTable(table: TableBlock) {
    val rows = table.children().flatMap { it.children() }
    Column(Modifier.horizontalScroll(rememberScrollState())) {
        rows.forEach { row ->
            val cells = row.children().filterIsInstance<TableCell>()
            Row {
                cells.forEach { cell ->
                    Surface(color = if (cell.isHeader) MaterialTheme.colorScheme.surfaceVariant else Color.Transparent) {
                        Text(aiInline(cell, MaterialTheme.colorScheme.primary, MaterialTheme.colorScheme.surfaceVariant),
                            Modifier.width(156.dp).padding(8.dp), style = MaterialTheme.typography.bodySmall,
                            fontWeight = if (cell.isHeader) FontWeight.SemiBold else FontWeight.Normal)
                    }
                }
            }
            HorizontalDivider(Modifier.width((cells.size * 156).dp))
        }
    }
}
