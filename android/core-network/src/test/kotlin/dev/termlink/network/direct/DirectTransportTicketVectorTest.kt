package dev.termlink.network.direct

import dev.termlink.protocol.transport.v1.DirectCarrierV1
import dev.termlink.protocol.transport.v1.DirectPathTicketV1
import java.io.File
import java.security.MessageDigest
import java.util.Base64
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.bouncycastle.crypto.params.Ed25519PublicKeyParameters
import org.bouncycastle.crypto.signers.Ed25519Signer
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class DirectTransportTicketVectorTest {
    @Test
    fun canonicalServerTicketRoundTripsAndVerifiesWithGeneratedJavaWire() {
        val vectorFile = findRepositoryFile(
            "protocol/test-vectors/direct-transport-ticket-v1.json",
        )
        val vector = Json.parseToJsonElement(vectorFile.readText()).jsonObject
        val publicKey = decodeBase64Url(
            vector.getValue("server_public_key_base64url").jsonPrimitive.content,
        )
        val wire = decodeBase64Url(
            vector.getValue("ticket_protobuf_base64url").jsonPrimitive.content,
        )
        assertEquals(
            vector.getValue("ticket_sha256").jsonPrimitive.content,
            MessageDigest.getInstance("SHA-256").digest(wire).toHex(),
        )
        val ticket = DirectPathTicketV1.parseFrom(wire)
        assertArrayEquals(wire, ticket.toByteArray())
        assertEquals(listOf(DirectCarrierV1.DIRECT_CARRIER_V1_UDP_QUIC), ticket.allowedCarriersList)
        assertEquals(
            vector.getValue("server_signing_key_id").jsonPrimitive.content,
            ticket.serverSigningKeyId.toStringUtf8(),
        )
        val signature = ticket.serverSignature.toByteArray()
        val claims = ticket.toBuilder().clearServerSignature().build().toByteArray()
        val signed = "TERMLINK-DIRECT-TICKET-V1\n".encodeToByteArray() + claims
        val verifier = Ed25519Signer().apply {
            init(false, Ed25519PublicKeyParameters(publicKey, 0))
            update(signed, 0, signed.size)
        }
        assertTrue("canonical server ticket signature was rejected", verifier.verifySignature(signature))
    }

    private fun findRepositoryFile(relativePath: String): File {
        val starts = listOfNotNull(
            System.getProperty("user.dir")?.let(::File),
            javaClass.protectionDomain?.codeSource?.location?.toURI()?.let(::File),
        )
        starts.forEach { start ->
            var current: File? = start.canonicalFile
            while (current != null) {
                val candidate = current.resolve(relativePath)
                if (candidate.isFile) return candidate
                current = current.parentFile
            }
        }
        error("canonical direct-transport ticket vector was not found")
    }

    private fun decodeBase64Url(value: String): ByteArray {
        require(value.isNotBlank() && '=' !in value)
        val decoded = Base64.getUrlDecoder().decode(value)
        assertEquals(value, Base64.getUrlEncoder().withoutPadding().encodeToString(decoded))
        return decoded
    }

    private fun ByteArray.toHex(): String = joinToString("") { byte -> "%02x".format(byte) }
}
