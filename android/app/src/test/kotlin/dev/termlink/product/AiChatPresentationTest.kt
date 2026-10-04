package dev.termlink.product

import androidx.compose.ui.graphics.Color
import org.commonmark.ext.gfm.tables.TableBlock
import org.commonmark.node.*
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.util.UUID

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class AiChatPresentationTest {
    @Test fun groupingPreservesOrderDetailsAndStableKeysAsToolsArrive() {
        val user = AiMessage("u", "user", "task")
        val tool = AiMessage("t", "tool", "full details", title = "运行命令", summary = "npm test", status = "running")
        val reply = AiMessage("a", "assistant", "answer")
        val before = aiTimelineEntries(listOf(user, tool))
        val after = aiTimelineEntries(listOf(user, tool, tool.copy(id = "t2"), reply, tool.copy(id = "t3")))
        assertEquals(before[1].key, after[1].key)
        assertEquals(listOf("u", "t", "t2", "a", "t3"), after.flatMap { when(it) {
            is AiTimelineEntry.Message -> listOf(it.value.id)
            is AiTimelineEntry.Tools -> it.values.map(AiMessage::id)
        } })
        assertEquals("full details", (after[1] as AiTimelineEntry.Tools).values.first().text)
        assertFalse(aiToolGroupLabel(listOf(tool, tool.copy(status = "unknown"))).contains("已完成"))
        assertTrue(aiToolGroupLabel(listOf(tool, tool.copy(status = "failed"))).contains("失败"))
        assertTrue(aiToolGroupLabel(listOf(tool.copy(status = "completed"))).contains("已完成"))
    }

    @Test fun oldSnapshotsRemainReadableAndNewPresentationSurvivesParsing() {
        val value = JSONObject("""{"kind":"ai","id":"${UUID.randomUUID()}","agentName":"Codex","directory":"D:\\work",
            "state":"idle","controlVersion":1,"canControl":true,"messages":[
            {"id":"old","role":"tool","text":"legacy details"},
            {"id":"new","role":"tool","text":"complete details","kind":"tool","title":"运行命令","summary":"npm test","status":"failed"}],
            "pending":[{"id":"p","kind":"approval","detail":"original request","questions":[],"canApprove":true,"title":"运行命令","summary":"echo review"}]}""")
        val session = parseConversation(UUID.randomUUID(), value)
        assertEquals("unknown", session.messages[0].status)
        assertEquals("legacy details", session.messages[0].text)
        assertEquals("failed", session.messages[1].status)
        assertEquals("echo review", session.pending.single().summary)
        assertEquals("original request", session.pending.single().detail)
    }

    @Test fun markdownRendersStructuresAndRejectsExecutableLinks() {
        val doc = aiMarkdown("# 标题\n\n1. 一\n2. 二\n\n| a | b |\n|---|---|\n|1|2|\n\n**粗体**和`代码` [网站](https://example.com) [危险](javascript:alert)\n")
        val nodes = generateSequence(doc.firstChild) { it.next }.toList()
        assertTrue(nodes[0] is Heading); assertTrue(nodes[1] is OrderedList); assertTrue(nodes[2] is TableBlock)
        val inline = aiInline(nodes.last(), Color.Blue, Color.Gray)
        assertTrue(inline.text.contains("粗体和代码"))
        assertTrue(inline.text.contains("javascript:alert"))
        assertEquals(1, inline.getLinkAnnotations(0, inline.length).size)
        assertTrue(inline.spanStyles.isNotEmpty())
        assertFalse(aiWebLink("file:///C:/secret")); assertFalse(aiWebLink("intent://launch"))
        assertFalse(aiWebLink("https://user:password@example.com")); assertTrue(aiWebLink("https://example.com/test"))
        assertTrue(aiMarkdown("<script>alert(1)</script>").firstChild is HtmlBlock)
    }
}
