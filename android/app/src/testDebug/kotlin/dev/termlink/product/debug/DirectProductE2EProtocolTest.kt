package dev.termlink.product.debug

import dev.termlink.product.DirectProductE2EOperationResult
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class DirectProductE2EProtocolTest {
    @Test
    fun eachInspectResponseCarriesOnlyItsOwnRequestIdentity() {
        val first = response("0".repeat(32))
        val second = response("1".repeat(32))

        assertNotEquals(first, second)
        assertEquals("0".repeat(32), first.getValue("request_id").jsonPrimitive.content)
        assertEquals("1".repeat(32), second.getValue("request_id").jsonPrimitive.content)
        assertEquals("inspect_transport", first.getValue("operation").jsonPrimitive.content)
        assertEquals("inspect_transport", second.getValue("operation").jsonPrimitive.content)
    }

    @Test
    fun failedResponseIsBilingualBoundedAndDoesNotExposeExceptionText() {
        val response = response("a".repeat(32))
        val encoded = response.toString()

        assertFalse(response.getValue("ok").jsonPrimitive.content.toBoolean())
        assertTrue(response.getValue("message_zh").jsonPrimitive.content.isNotBlank())
        assertTrue(response.getValue("message_en").jsonPrimitive.content.isNotBlank())
        assertFalse(encoded.contains("exception", ignoreCase = true))
        assertFalse(encoded.contains("stack", ignoreCase = true))
        assertTrue(encoded.encodeToByteArray().size < 4096)
    }

    private fun response(requestId: String) = DirectProductE2EProtocol.operationResponse(
        requestId = requestId,
        runIdSha256 = "b".repeat(64),
        operation = "inspect_transport",
        ready = true,
        result = DirectProductE2EOperationResult.blocked("PRODUCT_OPERATION_FAILED"),
    )
}
