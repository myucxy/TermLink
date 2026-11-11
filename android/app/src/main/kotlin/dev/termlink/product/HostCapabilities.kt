package dev.termlink.product

import dev.termlink.model.PcCapabilityIds
import dev.termlink.model.PcSummary

/** AI-only hosts have no terminal catalog to query or upgrade. */
internal fun requiresHostSessionCatalog(pc: PcSummary): Boolean =
    PcCapabilityIds.HOST_SESSION_CATALOG_V1 in pc.capabilities ||
        PcCapabilityIds.REALTIME_TERMINAL_V1 in pc.capabilities ||
        PcCapabilityIds.TERMINAL_COMMANDS_V1 in pc.capabilities

/** CLI creation and file browsing are separate host capabilities. */
internal fun supportsRemoteTerminalCreation(pc: PcSummary): Boolean =
    supportedRemoteTerminalCommands(pc).isNotEmpty() &&
        PcCapabilityIds.TERMINAL_COMMANDS_V1 in pc.capabilities &&
        (PcCapabilityIds.FILE_OPERATIONS_V1 in pc.capabilities ||
            pc.platform == "macos" && PcCapabilityIds.REALTIME_TERMINAL_V1 in pc.capabilities)

/** Mac CLI directory selection does not depend on an AI Agent catalog. */
internal fun usesManualMacCliDirectory(pc: PcSummary): Boolean = pc.platform == "macos"

/** Preserve the exact path: quotes, spaces and metacharacters are data, never shell syntax. */
internal fun validMacCliDirectory(path: String): Boolean =
    path.startsWith('/') && path.encodeToByteArray().size <= 4096 && path.none(Char::isISOControl)
