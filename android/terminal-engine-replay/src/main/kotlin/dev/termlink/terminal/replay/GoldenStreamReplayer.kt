package dev.termlink.terminal.replay

import dev.termlink.terminal.api.TerminalEngine
import dev.termlink.terminal.api.TerminalSize
import kotlinx.serialization.json.Json
import java.nio.ByteBuffer
import java.nio.charset.StandardCharsets
import java.security.MessageDigest

class GoldenStreamReplayer {
    private val json: Json = Json {
        ignoreUnknownKeys = false
        isLenient = false
    }

    suspend fun replay(bundle: GoldenStreamBundle, engine: TerminalEngine): ReplayReport {
        val manifest = decodeManifest(bundle.manifestBytes)
        validateFilesAndHashes(manifest, bundle)
        val events = decodeEvents(bundle.eventsBytes)
        val expectations = decodeExpectations(bundle.expectationsBytes)

        validateManifestStatistics(manifest, events, bundle.outputBytes)
        engine.clear()

        var expectedSequence = 1L
        var lastElapsed = -1L
        var nextOutputOffset = 0L
        var outputEventCount = 0
        var startedCount = 0
        var endedCount = 0
        var exitCode: Int? = null
        var finalSize: TerminalSize? = null

        events.forEach { event ->
            formatCheck(event.seq == expectedSequence) {
                "Event sequence gap: expected $expectedSequence, got ${event.seq}"
            }
            formatCheck(event.elapsedUs >= lastElapsed) {
                "Event time moved backwards at seq ${event.seq}"
            }

            when (event.type) {
                TYPE_OUTPUT -> {
                    val offset = required(event.dataOffset, "data_offset", event.seq)
                    val length = required(event.dataLength, "data_length", event.seq)
                    formatCheck(offset == nextOutputOffset && length > 0) {
                        "Invalid output range at seq ${event.seq}"
                    }
                    val endOffset = offset + length
                    formatCheck(endOffset <= bundle.outputBytes.size.toLong()) {
                        "Output range exceeds output.bin at seq ${event.seq}"
                    }

                    val byteOffset = offset.toIntExact("data_offset")
                    val readOnlySlice = ByteBuffer
                        .wrap(bundle.outputBytes, byteOffset, length)
                        .slice()
                        .asReadOnlyBuffer()
                    engine.feed(event.seq, readOnlySlice)
                    nextOutputOffset = endOffset
                    outputEventCount++
                }

                TYPE_RESIZE -> {
                    val columns = required(event.columns, "columns", event.seq)
                    val rows = required(event.rows, "rows", event.seq)
                    formatCheck(columns > 0 && rows > 0) {
                        "Invalid terminal size at seq ${event.seq}"
                    }
                    formatCheck(!event.reason.isNullOrBlank()) {
                        "Resize reason is required at seq ${event.seq}"
                    }
                    engine.resize(columns, rows)
                    finalSize = TerminalSize(columns, rows)
                }

                TYPE_LIFECYCLE -> when (event.state) {
                    STATE_STARTED -> startedCount++
                    STATE_ENDED -> {
                        endedCount++
                        exitCode = required(event.exitCode, "exit_code", event.seq)
                    }
                    else -> throw GoldenStreamFormatException(
                        "Unsupported lifecycle state '${event.state}' at seq ${event.seq}",
                    )
                }

                else -> throw GoldenStreamFormatException(
                    "Unsupported event type '${event.type}' at seq ${event.seq}",
                )
            }

            expectedSequence++
            lastElapsed = event.elapsedUs
        }

        formatCheck(events.firstOrNull()?.isLifecycle(STATE_STARTED) == true && startedCount == 1) {
            "Golden Stream must contain exactly one starting lifecycle event"
        }
        formatCheck(events.lastOrNull()?.isLifecycle(STATE_ENDED) == true && endedCount == 1) {
            "Golden Stream must end with exactly one ended lifecycle event"
        }
        formatCheck(nextOutputOffset == bundle.outputBytes.size.toLong()) {
            "Output events do not cover output.bin exactly"
        }
        formatCheck(outputEventCount == manifest.statistics.outputEventCount) {
            "Output event count does not match manifest"
        }

        val observedSize = finalSize
            ?: throw GoldenStreamFormatException("Golden Stream contains no resize event")
        val observedExitCode = exitCode
            ?: throw GoldenStreamFormatException("Golden Stream contains no exit code")
        validateExpectations(expectations, observedSize, observedExitCode, bundle.outputBytes)

        return ReplayReport(
            captureId = manifest.captureId,
            caseName = manifest.caseName,
            eventCount = events.size,
            outputEventCount = outputEventCount,
            outputBytes = bundle.outputBytes.size,
            finalSequence = events.last().seq,
            finalSize = observedSize,
            exitCode = observedExitCode,
            durationMicroseconds = events.last().elapsedUs,
        )
    }

    private fun decodeManifest(bytes: ByteArray): GoldenStreamManifest = decodeJson(
        bytes = bytes,
        label = "manifest.json",
    )

    private fun decodeExpectations(bytes: ByteArray): GoldenStreamExpectations = decodeJson(
        bytes = bytes,
        label = "expectations.json",
    )

    private inline fun <reified T> decodeJson(bytes: ByteArray, label: String): T = try {
        json.decodeFromString<T>(bytes.toString(StandardCharsets.UTF_8))
    } catch (error: Exception) {
        throw GoldenStreamFormatException("Invalid $label: ${error.message}")
    }

    private fun decodeEvents(bytes: ByteArray): List<SerializedGoldenStreamEvent> {
        val text = bytes.toString(StandardCharsets.UTF_8)
        return text.lineSequence()
            .filter { it.isNotBlank() }
            .mapIndexed { index, line ->
                try {
                    json.decodeFromString<SerializedGoldenStreamEvent>(line)
                } catch (error: Exception) {
                    throw GoldenStreamFormatException(
                        "Invalid events.jsonl line ${index + 1}: ${error.message}",
                    )
                }
            }
            .toList()
    }

    private fun validateFilesAndHashes(
        manifest: GoldenStreamManifest,
        bundle: GoldenStreamBundle,
    ) {
        formatCheck(manifest.format == FORMAT && manifest.formatVersion == FORMAT_VERSION) {
            "Unsupported Golden Stream format: ${manifest.format} v${manifest.formatVersion}"
        }
        formatCheck(manifest.clock == CLOCK) { "Unsupported capture clock: ${manifest.clock}" }
        formatCheck(manifest.files.events == EVENTS_FILE) { "Invalid events file name" }
        formatCheck(manifest.files.output == OUTPUT_FILE) { "Invalid output file name" }
        formatCheck(manifest.files.expectations == EXPECTATIONS_FILE) {
            "Invalid expectations file name"
        }
        hashCheck(EVENTS_FILE, manifest.sha256.events, bundle.eventsBytes)
        hashCheck(OUTPUT_FILE, manifest.sha256.output, bundle.outputBytes)
        hashCheck(EXPECTATIONS_FILE, manifest.sha256.expectations, bundle.expectationsBytes)
    }

    private fun validateManifestStatistics(
        manifest: GoldenStreamManifest,
        events: List<SerializedGoldenStreamEvent>,
        outputBytes: ByteArray,
    ) {
        formatCheck(events.isNotEmpty()) { "Golden Stream contains no events" }
        formatCheck(manifest.statistics.eventCount == events.size) {
            "Event count does not match manifest"
        }
        formatCheck(manifest.statistics.outputBytes == outputBytes.size) {
            "Output byte count does not match manifest"
        }
        formatCheck(manifest.initialSize.columns > 0 && manifest.initialSize.rows > 0) {
            "Manifest initial size is invalid"
        }
    }

    private fun validateExpectations(
        expectations: GoldenStreamExpectations,
        finalSize: TerminalSize,
        exitCode: Int,
        output: ByteArray,
    ) {
        formatCheck(expectations.exitCode == exitCode) {
            "Exit code mismatch: expected ${expectations.exitCode}, got $exitCode"
        }
        formatCheck(
            expectations.finalSize.columns == finalSize.columns &&
                expectations.finalSize.rows == finalSize.rows,
        ) { "Final terminal size does not match expectations" }

        if (expectations.outputContainsUtf8.isNotEmpty()) {
            val decoded = output.toString(StandardCharsets.UTF_8)
            expectations.outputContainsUtf8.forEach { marker ->
                formatCheck(decoded.contains(marker)) {
                    "Expected UTF-8 output marker not found: $marker"
                }
            }
        }
    }

    private fun hashCheck(label: String, expected: String, bytes: ByteArray) {
        val actual = MessageDigest.getInstance("SHA-256")
            .digest(bytes)
            .joinToString(separator = "") { byte -> "%02x".format(byte) }
        formatCheck(expected == actual) { "SHA-256 mismatch for $label" }
    }

    private fun <T : Any> required(value: T?, field: String, sequence: Long): T = value
        ?: throw GoldenStreamFormatException("$field is required at seq $sequence")

    private fun Long.toIntExact(label: String): Int {
        formatCheck(this in 0..Int.MAX_VALUE.toLong()) { "$label exceeds supported range" }
        return toInt()
    }

    private fun SerializedGoldenStreamEvent.isLifecycle(expectedState: String): Boolean =
        type == TYPE_LIFECYCLE && state == expectedState

    private inline fun formatCheck(condition: Boolean, lazyMessage: () -> String) {
        if (!condition) throw GoldenStreamFormatException(lazyMessage())
    }

    private companion object {
        const val FORMAT = "termlink.golden-stream"
        const val FORMAT_VERSION = 1
        const val CLOCK = "monotonic-microseconds"
        const val EVENTS_FILE = "events.jsonl"
        const val OUTPUT_FILE = "output.bin"
        const val EXPECTATIONS_FILE = "expectations.json"
        const val TYPE_OUTPUT = "output"
        const val TYPE_RESIZE = "resize"
        const val TYPE_LIFECYCLE = "lifecycle"
        const val STATE_STARTED = "started"
        const val STATE_ENDED = "ended"
    }
}
