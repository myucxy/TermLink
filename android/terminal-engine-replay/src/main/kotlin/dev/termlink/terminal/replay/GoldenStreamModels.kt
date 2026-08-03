package dev.termlink.terminal.replay

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonElement

data class GoldenStreamBundle(
    val manifestBytes: ByteArray,
    val eventsBytes: ByteArray,
    val outputBytes: ByteArray,
    val expectationsBytes: ByteArray,
)

@Serializable
internal data class GoldenStreamManifest(
    val format: String,
    @SerialName("format_version") val formatVersion: Int,
    @SerialName("capture_id") val captureId: String,
    @SerialName("case_name") val caseName: String,
    @SerialName("created_at") val createdAt: String,
    val clock: String,
    val source: GoldenStreamSource,
    @SerialName("initial_size") val initialSize: SerializedTerminalSize,
    val files: GoldenStreamFiles,
    val statistics: GoldenStreamStatistics,
    val sha256: GoldenStreamHashes,
)

@Serializable
internal data class GoldenStreamSource(
    val platform: String,
    val shell: String,
    @SerialName("command_line") val commandLine: String,
    @SerialName("working_directory") val workingDirectory: String? = null,
)

@Serializable
internal data class SerializedTerminalSize(
    val columns: Int,
    val rows: Int,
)

@Serializable
internal data class GoldenStreamFiles(
    val events: String,
    val output: String,
    val expectations: String,
)

@Serializable
internal data class GoldenStreamStatistics(
    @SerialName("event_count") val eventCount: Int,
    @SerialName("output_event_count") val outputEventCount: Int,
    @SerialName("output_bytes") val outputBytes: Int,
)

@Serializable
internal data class GoldenStreamHashes(
    val events: String,
    val output: String,
    val expectations: String,
)

@Serializable
internal data class SerializedGoldenStreamEvent(
    val seq: Long,
    @SerialName("elapsed_us") val elapsedUs: Long,
    val type: String,
    @SerialName("data_offset") val dataOffset: Long? = null,
    @SerialName("data_length") val dataLength: Int? = null,
    val columns: Int? = null,
    val rows: Int? = null,
    val reason: String? = null,
    val state: String? = null,
    @SerialName("exit_code") val exitCode: Int? = null,
)

@Serializable
internal data class GoldenStreamExpectations(
    @SerialName("exit_code") val exitCode: Int,
    @SerialName("final_size") val finalSize: SerializedTerminalSize,
    @SerialName("output_contains_utf8") val outputContainsUtf8: List<String>,
    @SerialName("normalized_grid") val normalizedGrid: JsonElement? = null,
    @SerialName("reference_screenshot") val referenceScreenshot: String? = null,
)

data class ReplayReport(
    val captureId: String,
    val caseName: String,
    val eventCount: Int,
    val outputEventCount: Int,
    val outputBytes: Int,
    val finalSequence: Long,
    val finalSize: dev.termlink.terminal.api.TerminalSize,
    val exitCode: Int,
    val durationMicroseconds: Long,
)

class GoldenStreamFormatException(message: String) : IllegalArgumentException(message)
