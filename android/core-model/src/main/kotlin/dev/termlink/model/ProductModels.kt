package dev.termlink.model

import java.time.Instant
import java.util.UUID

enum class DeviceType { WINDOWS_PC, ANDROID }
enum class DeviceTrustState { PENDING, TRUSTED, REVOKED }
enum class GrantPermission { VIEW, CONTROL, MANAGE }
enum class SessionState { RUNNING, INTERRUPTED, EXITED }

data class DeviceSummary(
    val id: UUID,
    val type: DeviceType,
    val name: String,
    val keyId: String,
    val exchangePublicKey: ByteArray,
    val trustState: DeviceTrustState,
    val createdAt: Instant,
) {
    override fun equals(other: Any?): Boolean =
        other is DeviceSummary && id == other.id && type == other.type && name == other.name &&
            keyId == other.keyId && exchangePublicKey.contentEquals(other.exchangePublicKey) &&
            trustState == other.trustState && createdAt == other.createdAt

    override fun hashCode(): Int = 31 * id.hashCode() + exchangePublicKey.contentHashCode()
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
    /** Agent-detected managed shells; clients must never infer this list. */
    val supportedShells: Set<String> = emptySet(),
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
) {
    override fun equals(other: Any?): Boolean =
        other is SessionSummary && id == other.id && pcId == other.pcId &&
            shellType == other.shellType && encryptedTitle.contentEquals(other.encryptedTitle) &&
            state == other.state && columns == other.columns && rows == other.rows &&
            lastOutputSequence == other.lastOutputSequence && createdAt == other.createdAt &&
            endedAt == other.endedAt && online == other.online

    override fun hashCode(): Int =
        31 * (31 * id.hashCode() + (encryptedTitle?.contentHashCode() ?: 0)) + online.hashCode()
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
