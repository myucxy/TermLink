package dev.termlink.network.production

/**
 * A control lease was granted by REST after a newer realtime revocation had
 * already fenced the request. This is an expected arbitration outcome, not a
 * server, network, or session-open failure.
 */
class ControlRequestSupersededException internal constructor() :
    IllegalStateException("control request was superseded by a newer revoke")
