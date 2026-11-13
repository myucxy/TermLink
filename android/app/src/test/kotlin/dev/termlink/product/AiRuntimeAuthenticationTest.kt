package dev.termlink.product

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import java.io.IOException
import java.time.Instant
import java.util.UUID
import kotlinx.coroutines.*
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class AiRuntimeAuthenticationTest {
    private val now = Instant.parse("2026-11-11T12:00:00Z")
    private val officialURL = "https://auth.openai.com/codex/device"
    private fun challenge() = JSONObject().put("agentId", "codex").put("status", "pending").put("method", "device_code")
        .put("verificationUrl", officialURL).put("userCode", "ABCD-1234").put("expiresAt", now.plusSeconds(600).toString())
    private fun metadata() = JSONObject().put("agentId", "codex").put("status", "isolated_required").put("isolated", true)
        .put("method", "device_code").put("canCheck", true).put("canStart", true)
    private fun catalog(host: String = "host") = JSONObject().put("hostId", host).put("sessions", JSONArray()).put("agents", JSONArray().put(
        JSONObject().put("id", "codex").put("name", "Codex").put("status", "available").put("strategy", "native")
            .put("runtimeAuthentication", metadata())))

    @Test fun summariesDescribeStatusWithoutEchoingUntrustedMetadata() {
        val expected = mapOf("authenticated" to "已登录", "not_required" to "不要求", "required" to "单独授权",
            "isolated_required" to "单独授权", "cancelled" to "已取消", "failed" to "未完成")
        for ((status, text) in expected) {
            val value = JSONObject().put("status", status).put("secret", "DO-NOT-DISPLAY").put("account", "personal@example.invalid")
            val summary = aiRuntimeAuthenticationSummary(value, now)
            assertTrue(status, summary.contains(text))
            assertFalse(summary.contains("DO-NOT-DISPLAY")); assertFalse(summary.contains("personal@"))
        }
        assertTrue(aiRuntimeAuthenticationSummary(challenge(), now).contains("设备码"))
        assertTrue(aiRuntimeAuthenticationSummary(challenge(), now.plusSeconds(601)).contains("过期"))
        for (status in listOf(null, "", "future-secret-status", "AUTHENTICATED", "authenticated\n", 1, true)) {
            val value = JSONObject().put("status", status ?: JSONObject.NULL)
            assertTrue(aiRuntimeAuthenticationSummary(value, now).startsWith("尚未确认"))
        }
    }

    @Test fun deviceChallengeAcceptsOnlyTheExactOfficialURL() {
        assertEquals(officialURL to "ABCD-1234", aiRuntimeAuthenticationChallenge(challenge(), now))
        for (url in listOf("http://auth.openai.com/codex/device", "https://auth.openai.com/codex/device/", "$officialURL?next=evil",
            "$officialURL#fragment", "https://auth.openai.com:443/codex/device", "https://user@auth.openai.com/codex/device",
            "https://auth.openai.com.evil.invalid/codex/device", "https://AUTH.OPENAI.COM/codex/device", " $officialURL", "$officialURL\n",
            "https://auth.openai.com/codex/%64evice", "https://auth.openai.com/codex/../device", "https://аuth.openai.com/codex/device")) {
            assertNull(url, aiRuntimeAuthenticationURL(url))
            assertNull(url, aiRuntimeAuthenticationChallenge(challenge().put("verificationUrl", url), now))
        }
    }

    @Test fun challengeRejectsExpiredMalformedOrUnknownMetadata() {
        for (value in listOf(challenge().put("expiresAt", now.toString()), challenge().put("expiresAt", now.minusSeconds(1).toString()),
            challenge().put("expiresAt", "not-a-time"), challenge().put("expiresAt", JSONObject.NULL),
            challenge().put("status", "authenticated"), challenge().put("status", "unknown"),
            challenge().put("method", "host_setup"), challenge().put("verificationUrl", JSONObject.NULL))) {
            assertNull(value.toString(), aiRuntimeAuthenticationChallenge(value, now))
        }
        for (code in listOf("", "ABC", "A".repeat(33), "AB CD", "ABCD\n", "ＡBCD", "ABCD/1234", "<script>", "ABCD\u0000")) {
            assertNull(code, aiRuntimeAuthenticationChallenge(challenge().put("userCode", code), now))
        }
        assertNull(aiRuntimeAuthenticationChallenge(challenge().put("userCode", 1234), now))
        assertEquals("ABCD", aiRuntimeAuthenticationChallenge(challenge().put("userCode", "ABCD"), now)?.second)
        assertEquals("A".repeat(32), aiRuntimeAuthenticationChallenge(challenge().put("userCode", "A".repeat(32)), now)?.second)
        assertNull(aiRuntimeAuthenticationChallenge(JSONObject(), now))
    }

    @Test fun catalogAuthenticationIsAdditiveAndChallengesAreVolatile(): Unit = runBlocking {
        val pc = UUID.randomUUID()
        val prefs = preferences()
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String = when (JSONObject(body).getString("action")) {
                "catalog" -> catalog().toString()
                else -> JSONObject().put("hostId", "host").put("authentication", challenge()).toString()
            }
        }
        val workspace = AiWorkspace(repository, this, prefs)
        try {
            workspace.refresh(pc)
            val agent = workspace.catalogs.getValue(pc).agents.single()
            assertEquals("device_code", agent.runtimeAuthentication.getString("method"))
            workspace.runtimeAuthentication(pc, "codex")
            assertEquals("ABCD-1234", workspace.runtimeAuthentications.getValue(pc to "codex").getString("userCode"))
            assertFalse(prefs.all.values.any { it.toString().contains("ABCD-1234") || it.toString().contains(officialURL) })
            workspace.retire()
            assertTrue(workspace.runtimeAuthentications.isEmpty())
        } finally { workspace.retire(); prefs.edit().clear().commit() }
    }

    @Test fun delayedReadCannotOverwriteAnExplicitCancelReceipt(): Unit = runBlocking {
        val pc = UUID.randomUUID(); val prefs = preferences(); val queried = CompletableDeferred<Unit>(); val delayed = CompletableDeferred<String>()
        var reads = 0
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String {
                return when (JSONObject(body).getString("action")) {
                    "catalog" -> catalog().toString()
                    "authentication" -> { reads++; if (reads == 1) { queried.complete(Unit); delayed.await() }
                        else JSONObject().put("hostId", "host").put("authentication", JSONObject().put("agentId", "codex").put("status", "cancelled")).toString() }
                    else -> JSONObject().put("hostId", "host").put("authentication", JSONObject().put("agentId", "codex").put("status", "cancelled")).toString()
                }
            }
        }
        val workspace = AiWorkspace(repository, this, prefs)
        try {
            workspace.refresh(pc)
            val reading = async { runCatching { workspace.runtimeAuthentication(pc, "codex") } }
            queried.await()
            assertTrue(workspace.mutate(pc, "authentication.cancel", extra = JSONObject().put("agentId", "codex"))!!.await())
            delayed.complete(JSONObject().put("hostId", "host").put("authentication", challenge()).toString())
            assertTrue(reading.await().isFailure)
            assertEquals("cancelled", workspace.runtimeAuthentications.getValue(pc to "codex").getString("status"))
            assertFalse(workspace.isBlocked(pc))
            workspace.runtimeAuthentication(pc, "codex")
            assertEquals(2, reads)
        } finally { delayed.cancel(); workspace.retire(); prefs.edit().clear().commit() }
    }

    @Test fun wrongHostAgentOrIncompleteMutationReceiptKeepsTheOriginalRequest(): Unit = runBlocking {
        for (kind in listOf("wrong-host", "wrong-agent", "missing-authentication")) {
            val pc = UUID.randomUUID(); val prefs = preferences(); val requests = mutableListOf<String>()
            val repository = object : ProductRepository by UnavailableProductRepository() {
                override suspend fun aiRequest(pcId: UUID, body: String): String {
                    if (JSONObject(body).getString("action") == "catalog") return catalog().toString()
                    requests += body
                    val reply = JSONObject().put("hostId", if (kind == "wrong-host") "retired-host" else "host")
                    if (kind != "missing-authentication") reply.put("authentication", JSONObject().put("agentId", if (kind == "wrong-agent") "zcode" else "codex").put("status", "required"))
                    return reply.toString()
                }
            }
            val workspace = AiWorkspace(repository, this, prefs)
            try {
                workspace.refresh(pc)
                assertFalse(workspace.mutate(pc, "authentication.start", extra = JSONObject().put("agentId", "codex"))!!.await())
                assertTrue(workspace.isUncertain(pc)); assertTrue(workspace.runtimeAuthentications.isEmpty())
                val original = requests.single()
                workspace.retryUncertain(pc)
                while (workspace.isBusy(pc)) yield()
                assertEquals(listOf(original, original), requests)
                assertTrue(prefs.contains("pending")); assertTrue(workspace.isUncertain(pc))
            } finally { workspace.retire(); prefs.edit().clear().commit() }
        }
    }

    @Test fun hostChangesDiscardChallengesAndRejectTheOldRead(): Unit = runBlocking {
        val pc = UUID.randomUUID(); val prefs = preferences(); var host = "host"
        val queried = CompletableDeferred<Unit>(); val delayed = CompletableDeferred<String>(); var delayRead = false
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String {
                if (JSONObject(body).getString("action") == "catalog") return catalog(host).toString()
                if (delayRead) { queried.complete(Unit); return delayed.await() }
                return JSONObject().put("hostId", host).put("authentication", challenge()).toString()
            }
        }
        val workspace = AiWorkspace(repository, this, prefs)
        try {
            workspace.refresh(pc); workspace.runtimeAuthentication(pc, "codex")
            assertFalse(workspace.runtimeAuthentications.isEmpty())
            delayRead = true
            val reading = async { runCatching { workspace.runtimeAuthentication(pc, "codex") } }
            queried.await(); host = "replacement"; workspace.refresh(pc)
            assertTrue(workspace.runtimeAuthentications.isEmpty())
            delayed.complete(JSONObject().put("hostId", "host").put("authentication", challenge()).toString())
            assertTrue(reading.await().isFailure); assertTrue(workspace.runtimeAuthentications.isEmpty())
        } finally { delayed.cancel(); workspace.retire(); prefs.edit().clear().commit() }
    }

    @Test fun unknownOrUnsupportedAuthMetadataNeverStartsARequest(): Unit = runBlocking {
        for (auth in listOf(JSONObject(), metadata().put("canCheck", false), metadata().put("canCheck", "true"))) {
            val pc = UUID.randomUUID(); val prefs = preferences(); var requests = 0
            val repository = object : ProductRepository by UnavailableProductRepository() { override suspend fun aiRequest(pcId: UUID, body: String): String { requests++; throw IOException() } }
            val workspace = AiWorkspace(repository, this, prefs)
            try {
                workspace.catalogs[pc] = AiCatalog("host", listOf(AiAgentOption("codex", "Codex", "1", "available", runtimeAuthentication = auth)), emptyList())
                assertTrue(runCatching { workspace.runtimeAuthentication(pc, "codex") }.isFailure)
                assertEquals(0, requests)
            } finally { workspace.retire(); prefs.edit().clear().commit() }
        }
    }

    @Test fun aLateReadCannotReplaceANewerReadOnTheSameHost(): Unit = runBlocking {
        val pc = UUID.randomUUID(); val prefs = preferences(); var reads = 0
        val queried = CompletableDeferred<Unit>(); val delayed = CompletableDeferred<String>()
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String {
                if (JSONObject(body).getString("action") == "catalog") return catalog().toString()
                if (++reads == 1) { queried.complete(Unit); return delayed.await() }
                return JSONObject().put("hostId", "host").put("authentication", JSONObject().put("agentId", "codex").put("status", "authenticated")).toString()
            }
        }
        val workspace = AiWorkspace(repository, this, prefs)
        try {
            workspace.refresh(pc)
            val old = async { runCatching { workspace.runtimeAuthentication(pc, "codex") } }
            queried.await(); workspace.runtimeAuthentication(pc, "codex")
            delayed.complete(JSONObject().put("hostId", "host").put("authentication", challenge()).toString())
            assertTrue(old.await().isFailure)
            assertEquals("authenticated", workspace.runtimeAuthentications.getValue(pc to "codex").getString("status"))
        } finally { delayed.cancel(); workspace.retire(); prefs.edit().clear().commit() }
    }

    private fun preferences() = ApplicationProvider.getApplicationContext<Context>().getSharedPreferences("runtime-auth-${UUID.randomUUID()}", Context.MODE_PRIVATE)
}
