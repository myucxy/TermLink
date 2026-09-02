package dev.termlink.model

import java.time.Instant
import java.util.UUID

enum class DeviceType { WINDOWS_PC, PC_AGENT, ANDROID }
fun DeviceType.isPcAgent(): Boolean = this == DeviceType.WINDOWS_PC || this == DeviceType.PC_AGENT
enum class DeviceTrustState { PENDING, TRUSTED, REVOKED }
enum class GrantPermission { VIEW, CONTROL, MANAGE }
enum class SessionState { RUNNING, INTERRUPTED, EXITED }

data class DeviceSummary(
    val id: UUID,
    val type: DeviceType,
    val name: String,
    val keyId: String,
    val exchangePublicKey: ByteArray,
    /** Server-approved Ed25519 identity used only to verify direct peer proof. */
    val signingPublicKey: ByteArray = ByteArray(0),
    val trustState: DeviceTrustState,
    val createdAt: Instant,
) {
    override fun equals(other: Any?): Boolean =
        other is DeviceSummary && id == other.id && type == other.type && name == other.name &&
            keyId == other.keyId && exchangePublicKey.contentEquals(other.exchangePublicKey) &&
            signingPublicKey.contentEquals(other.signingPublicKey) &&
            trustState == other.trustState && createdAt == other.createdAt

    override fun hashCode(): Int =
        31 * (31 * id.hashCode() + exchangePublicKey.contentHashCode()) + signingPublicKey.contentHashCode()
}

data class PcSummary(
    val id: UUID,
    val deviceId: UUID,
    val name: String,
    val agentVersion: String,
    val keyEpoch: Long = 1,
    val remoteRoutingEnabled: Boolean,
    val online: Boolean,
    val lastSeenAt: Instant?,
    /** Server-authoritative runtime platform; legacy Windows records default to windows. */
    val platform: String = "windows",
    val architecture: String = "unknown",
    val platformVersion: String = "legacy",
    /** Bounded Agent feature identifiers used to gate remote actions. */
    val capabilities: Set<String> = emptySet(),
    /** Agent-detected managed shells; clients must never infer this list. */
    val supportedShells: Set<String> = emptySet(),
    /** Agent-authoritative terminal commands, including Windows-configured manual entries. */
    val terminalCommands: List<TerminalCommandCapability> = emptyList(),
    /** Server-authoritative support for Android-triggered transactional Agent updates. */
    val remoteAgentUpdateCapable: Boolean = false,
)

object PcCapabilityIds {
    const val REALTIME_TERMINAL_V1 = "realtime_terminal_v1"
    const val TERMINAL_SNAPSHOT_V1 = "terminal_snapshot_v1"
    const val TERMINAL_SNAPSHOT_V2 = "terminal_snapshot_v2"
    const val FILE_OPERATIONS_V1 = "file_operations_v1"
    const val TERMINAL_COMMANDS_V1 = "terminal_commands_v1"
    const val REMOTE_AGENT_UPDATE_V1 = "remote_agent_update_v1"
}

data class TerminalCommandCapability(
    val id: String,
    val displayName: String,
    val shellType: String,
    val source: String,
)

data class SessionSummary(
    val id: UUID,
    val pcId: UUID,
    val shellType: String,
    val encryptedTitle: ByteArray?,
    val state: SessionState,
    val columns: Int,
    val rows: Int,
    val lastOutputSequence: ULong,
    val createdAt: Instant,
    val endedAt: Instant?,
    /** Server-authoritative reachability of this exact session UUID. */
    val online: Boolean = false,
    /** Safe producer diagnostic metadata; never contains terminal text. */
    val snapshotStatus: String? = null,
    val snapshotReasonCode: String? = null,
) {
    override fun equals(other: Any?): Boolean =
        other is SessionSummary && id == other.id && pcId == other.pcId &&
            shellType == other.shellType && encryptedTitle.contentEquals(other.encryptedTitle) &&
            state == other.state && columns == other.columns && rows == other.rows &&
            lastOutputSequence == other.lastOutputSequence && createdAt == other.createdAt &&
            endedAt == other.endedAt && online == other.online &&
            snapshotStatus == other.snapshotStatus && snapshotReasonCode == other.snapshotReasonCode

    override fun hashCode(): Int =
        31 * (31 * (31 * (31 * id.hashCode() +
            (encryptedTitle?.contentHashCode() ?: 0)) + online.hashCode()) +
            (snapshotStatus?.hashCode() ?: 0)) + (snapshotReasonCode?.hashCode() ?: 0)
}

data class HistoryObject(
    val id: UUID,
    val kind: Kind,
    val objectKey: String,
    val keyId: String,
    val ciphertextSha256: ByteArray,
    val sizeBytes: Long,
    val startSequence: ULong?,
    val endSequence: ULong?,
    val snapshotSequence: ULong?,
    val formatName: String?,
    val formatVersion: Int?,
) {
    enum class Kind { CHUNK, SNAPSHOT }
}

// Plans are data from the published server Catalog. The client deliberately
// has no FREE/PRO product enum and never derives authorization from this type.
data class CatalogPlan(
    val key: String,
    val displayName: String,
    val description: String,
    val billingPeriod: String,
    /** Server returns -1 for unlimited PC/session quantities. */
    val maxRemotePcs: Int,
    val maxLiveSessions: Int,
    val featureFlags: Map<String, Boolean>,
    val channelProducts: Map<String, String>,
)

data class Entitlement(
    val planKey: String,
    val catalogVersion: Long,
    /** Server returns -1 for unlimited PC/session quantities. */
    val maxRemotePcs: Int,
    val maxLiveSessions: Int,
    val featureFlags: Map<String, Boolean>,
    val version: Long,
    val effectiveAt: Instant,
    val expiresAt: Instant?,
)

data class TermLinkError(
    val code: String,
    val messageKey: String,
    val traceId: String,
)
