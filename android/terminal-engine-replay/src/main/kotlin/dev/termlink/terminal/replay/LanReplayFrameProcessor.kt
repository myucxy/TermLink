package dev.termlink.terminal.replay

import dev.termlink.terminal.api.TerminalEngine
import dev.termlink.terminal.api.TerminalSize
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.StandardCharsets
import java.security.MessageDigest

data class LanReplayReport(
    val caseName: String,
    val eventCount: Int,
    val outputEventCount: Int,
    val outputBytes: Int,
    val finalSize: TerminalSize,
    val exitCode: Int,
    val durationMicroseconds: Long,
)

sealed interface LanReplayProgress {
    data class Started(val caseName: String, val eventCount: Int) : LanReplayProgress

    data class Receiving(
        val sequence: Long,
        val eventCount: Int,
        val outputBytes: Int,
    ) : LanReplayProgress

    data class Complete(val report: LanReplayReport) : LanReplayProgress
}

class LanReplayFormatException(message: String) : IllegalArgumentException(message)

class LanReplayFrameProcessor(
    private val engine: TerminalEngine,
) {
    private val json = Json { ignoreUnknownKeys = false }
    private var expectedSequence = 1L
    private var lastElapsed = -1L
    private var metadata: StartedMetadata? = null
    private var outputEventCount = 0
    private var outputBytes = 0
    private var finalSize: TerminalSize? = null
    private var outputDigest = MessageDigest.getInstance("SHA-256")
    private var complete = false

    suspend fun accept(frame: ByteArray): LanReplayProgress {
        formatCheck(!complete) { "Replay already completed" }
        formatCheck(frame.size >= HEADER_SIZE) { "LAN replay frame is shorter than its header" }

        val buffer = ByteBuffer.wrap(frame).order(ByteOrder.BIG_ENDIAN)
        val version = buffer.get().toInt() and 0xff
        val type = buffer.get().toInt() and 0xff
        val sequence = buffer.long
        val elapsed = buffer.long
        formatCheck(version == PROTOCOL_VERSION) { "Unsupported LAN replay version: $version" }
        formatCheck(sequence > 0 && sequence == expectedSequence) {
            "LAN replay sequence gap: expected $expectedSequence, got $sequence"
        }
        formatCheck(elapsed >= 0 && elapsed >= lastElapsed) {
            "LAN replay time moved backwards at seq $sequence"
        }
        val payload = ByteArray(buffer.remaining())
        buffer.get(payload)

        val progress = when (type) {
            TYPE_STARTED -> processStarted(sequence, payload)
            TYPE_RESIZE -> processResize(sequence, payload)
            TYPE_OUTPUT -> processOutput(sequence, payload)
            TYPE_ENDED -> processEnded(sequence, elapsed, payload)
            else -> throw LanReplayFormatException("Unknown LAN replay frame type: $type")
        }

        expectedSequence++
        lastElapsed = elapsed
        return progress
    }

    private fun processStarted(sequence: Long, payload: ByteArray): LanReplayProgress {
        formatCheck(sequence == 1L && metadata == null) { "Started must be the first frame" }
        val started = try {
            json.decodeFromString<StartedMetadata>(payload.toString(StandardCharsets.UTF_8))
        } catch (error: Exception) {
            throw LanReplayFormatException("Invalid Started metadata: ${error.message}")
        }
        formatCheck(
            started.caseName.matches(CASE_NAME_PATTERN) &&
                started.eventCount >= MIN_EVENT_COUNT &&
                started.outputEventCount > 0 &&
                started.outputBytes > 0 &&
                started.outputSHA256.matches(SHA256_PATTERN),
        ) { "Started metadata is invalid" }

        engine.clear()
        metadata = started
        outputEventCount = 0
        outputBytes = 0
        finalSize = null
        outputDigest = MessageDigest.getInstance("SHA-256")
        return LanReplayProgress.Started(started.caseName, started.eventCount)
    }

    private fun processResize(sequence: Long, payload: ByteArray): LanReplayProgress {
        val started = requireMetadata(sequence)
        formatCheck(payload.size == RESIZE_PAYLOAD_SIZE) { "Resize payload must contain 4 bytes" }
        val sizeBuffer = ByteBuffer.wrap(payload).order(ByteOrder.BIG_ENDIAN)
        val columns = sizeBuffer.short.toInt() and 0xffff
        val rows = sizeBuffer.short.toInt() and 0xffff
        formatCheck(columns > 0 && rows > 0) { "Resize dimensions must be positive" }
        engine.resize(columns, rows)
        finalSize = TerminalSize(columns, rows)
        return receiving(sequence, started)
    }

    private suspend fun processOutput(sequence: Long, payload: ByteArray): LanReplayProgress {
        val started = requireMetadata(sequence)
        formatCheck(payload.isNotEmpty()) { "Output payload must not be empty" }
        outputDigest.update(payload)
        outputEventCount++
        outputBytes += payload.size
        formatCheck(
            outputEventCount <= started.outputEventCount && outputBytes <= started.outputBytes,
        ) { "Output exceeds Started metadata" }
        engine.feed(sequence, ByteBuffer.wrap(payload).asReadOnlyBuffer())
        return receiving(sequence, started)
    }

    private fun processEnded(
        sequence: Long,
        elapsed: Long,
        payload: ByteArray,
    ): LanReplayProgress {
        val started = requireMetadata(sequence)
        formatCheck(payload.size == ENDED_PAYLOAD_SIZE) { "Ended payload must contain 4 bytes" }
        formatCheck(sequence == started.eventCount.toLong()) {
            "Ended sequence does not match Started event count"
        }
        formatCheck(outputEventCount == started.outputEventCount) {
            "Output event count does not match Started metadata"
        }
        formatCheck(outputBytes == started.outputBytes) {
            "Output byte count does not match Started metadata"
        }
        val actualSHA256 = outputDigest.digest().toHex()
        formatCheck(actualSHA256 == started.outputSHA256) {
            "Output SHA-256 does not match Started metadata"
        }
        val size = finalSize ?: throw LanReplayFormatException("Replay contains no Resize frame")
        val exitCode = ByteBuffer.wrap(payload).order(ByteOrder.BIG_ENDIAN).int
        complete = true
        return LanReplayProgress.Complete(
            LanReplayReport(
                caseName = started.caseName,
                eventCount = started.eventCount,
                outputEventCount = outputEventCount,
                outputBytes = outputBytes,
                finalSize = size,
                exitCode = exitCode,
                durationMicroseconds = elapsed,
            ),
        )
    }

    private fun receiving(sequence: Long, started: StartedMetadata): LanReplayProgress =
        LanReplayProgress.Receiving(
            sequence = sequence,
            eventCount = started.eventCount,
            outputBytes = outputBytes,
        )

    private fun requireMetadata(sequence: Long): StartedMetadata = metadata
        ?: throw LanReplayFormatException("Frame $sequence arrived before Started")

    private fun ByteArray.toHex(): String = joinToString(separator = "") { byte ->
        "%02x".format(byte)
    }

    private inline fun formatCheck(condition: Boolean, lazyMessage: () -> String) {
        if (!condition) throw LanReplayFormatException(lazyMessage())
    }

    @Serializable
    private data class StartedMetadata(
        @SerialName("case_name") val caseName: String,
        @SerialName("event_count") val eventCount: Int,
        @SerialName("output_event_count") val outputEventCount: Int,
        @SerialName("output_bytes") val outputBytes: Int,
        @SerialName("output_sha256") val outputSHA256: String,
    )

    private companion object {
        const val HEADER_SIZE = 18
        const val RESIZE_PAYLOAD_SIZE = 4
        const val ENDED_PAYLOAD_SIZE = 4
        const val MIN_EVENT_COUNT = 3
        const val PROTOCOL_VERSION = 1
        const val TYPE_STARTED = 1
        const val TYPE_RESIZE = 2
        const val TYPE_OUTPUT = 3
        const val TYPE_ENDED = 4
        val CASE_NAME_PATTERN = Regex("^[a-z0-9][a-z0-9._-]*$")
        val SHA256_PATTERN = Regex("^[a-f0-9]{64}$")
    }
}
