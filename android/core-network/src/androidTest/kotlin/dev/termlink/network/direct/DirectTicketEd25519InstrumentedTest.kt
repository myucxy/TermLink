package dev.termlink.network.direct

import androidx.test.ext.junit.runners.AndroidJUnit4
import com.google.protobuf.ByteString
import dev.termlink.protocol.transport.v1.DirectPathTicketV1
import java.util.Base64
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class DirectTicketEd25519InstrumentedTest {
    @Test
    fun platformVerifierAcceptsCanonicalServerVector() {
        val publicKey = decode("A6EHv_POEL4dcN0Y50vAmWfk1jCbpQ1fHdyGZBJVMbg")
        val ticket = DirectPathTicketV1.parseFrom(
            decode(
                "CAESEAARIjNEVUZ3iJmqu8zd7v8aEBAhMkNUZUdoiZqrvM3e7_AiECAxQlNkdUhpipusvc7f8AEqEDBBUmN0hUlqi5ytvs_g8QIyEEBRYnOElUprjJ2uv9Dh8gM4B0CA8MyGpjdIwJnUhqY3UgEBWhAAAQIDBAUGBwgJCgsMDQ4PYhBkaXJlY3QtdjEtdmVjdG9yakCwJTg3vrjJ8h6M6z4BR3XkogSgH3TQI4jp2mBhQTUyJWcO36MFe3MVh3Zxk-uAFShR9t2MynoQJEk4LrR53KkI",
            ),
        )
        val signature = ticket.serverSignature.toByteArray()
        val claims = ticket.toBuilder().setServerSignature(ByteString.EMPTY).build().toByteArray()
        assertTrue(
            verifyEd25519(
                publicKey,
                "TERMLINK-DIRECT-TICKET-V1\n".encodeToByteArray() + claims,
                signature,
            ),
        )
    }

    private fun decode(value: String): ByteArray = Base64.getUrlDecoder().decode(value)
}
