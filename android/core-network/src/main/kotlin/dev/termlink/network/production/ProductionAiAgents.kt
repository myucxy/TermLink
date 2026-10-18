package dev.termlink.network.production

import dev.termlink.crypto.DeviceIdentity
import dev.termlink.protocol.realtime.v1.AiAgentRequestPayload
import dev.termlink.protocol.realtime.v1.AiAgentResponsePayload
import dev.termlink.protocol.realtime.v1.MessageType
import java.util.UUID
import org.json.JSONObject

internal fun aiAgentRequestTimeoutMillis(action: String): Long =
    if (action == "import-native" || action == "resume") 120_000L else 40_000L

suspend fun requestAiAgent(
    multiplexer: ProductionRealtimeMultiplexer,
    pcId: UUID, pcDeviceId: UUID, androidDeviceId: UUID,
    identity: DeviceIdentity, pcExchangePublicKey: ByteArray, keyEpoch: Long, sequence: Long,
    body: String,
): String {
    require(body.encodeToByteArray().size <= 48 * 1024)
    return requestHostDevice(
        multiplexer, pcId, pcDeviceId, androidDeviceId, identity, pcExchangePublicKey, keyEpoch, sequence,
        MessageType.AI_AGENT_REQUEST, MessageType.AI_AGENT_RESPONSE, "ai_agent_v1",
        aiAgentRequestTimeoutMillis(JSONObject(body).optString("action")),
        request = { AiAgentRequestPayload.newBuilder().setRequestId(it).setJson(body).build().toByteArray() },
        response = { bytes, id ->
            require(bytes.size <= 900 * 1024)
            AiAgentResponsePayload.parseFrom(bytes).also { require(it.requestId == id) }.json
        },
    )
}
