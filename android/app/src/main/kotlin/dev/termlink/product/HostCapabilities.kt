package dev.termlink.product

import dev.termlink.model.PcCapabilityIds
import dev.termlink.model.PcSummary

/** AI-only hosts have no terminal catalog to query or upgrade. */
internal fun requiresHostSessionCatalog(pc: PcSummary): Boolean =
    PcCapabilityIds.HOST_SESSION_CATALOG_V1 in pc.capabilities ||
        PcCapabilityIds.REALTIME_TERMINAL_V1 in pc.capabilities ||
        PcCapabilityIds.TERMINAL_COMMANDS_V1 in pc.capabilities
