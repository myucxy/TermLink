package dev.termlink.product

import org.json.JSONArray
import org.json.JSONObject

/** Presentation only. Restored answers never issue commands; current host requests remain authoritative. */
internal fun sanitizeAiWebState(value: JSONObject, session: AiConversation): JSONObject {
    require(value.toString().length <= 48000)
    val result = JSONObject().put("tail", value.optBoolean("tail", true))
    value.optJSONObject("anchor")?.let {
        val id = it.optString("id")
        if (id.length in 1..256) result.put("anchor", JSONObject().put("id", id).put("offset", it.optInt("offset").coerceIn(-100000, 0)))
    }
    val expanded = value.optJSONArray("expanded") ?: JSONArray()
    result.put("expanded", JSONArray((0 until minOf(expanded.length(), 128)).map { expanded.optString(it).take(256) }))
    val drafts = value.optJSONObject("questions") ?: JSONObject()
    result.put("questions", JSONObject().apply {
        session.pending.take(16).forEach { pending ->
            val draft = drafts.optJSONObject(pending.id) ?: return@forEach
            // A reused request ID with different questions must not inherit the previous answer.
            if (draft.optString("schema") != aiQuestionSchema(pending)) return@forEach
            val choices = draft.optJSONObject("answers") ?: JSONObject()
            val free = draft.optJSONObject("extra") ?: JSONObject()
            val answers = JSONObject(); val extra = JSONObject()
            pending.questions.forEach { q ->
                val selected = choices.optJSONArray(q.id) ?: JSONArray()
                answers.put(q.id, JSONArray((0 until minOf(selected.length(), 32)).map { selected.optString(it) }
                    .filter { it in q.options }.distinct().take(if (q.multiSelect) 32 else 1)))
                extra.put(q.id, free.optString(q.id).take(4000))
            }
            put(pending.id, JSONObject().put("schema", aiQuestionSchema(pending)).put("answers", answers).put("extra", extra))
        }
    })
    return result
}

internal fun aiQuestionSchema(p: AiPending): String = JSONArray().apply {
    p.questions.forEach { q -> put(JSONArray().put(q.id).put(q.text).put(JSONArray(q.options)).put(q.multiSelect)) }
}.toString()
