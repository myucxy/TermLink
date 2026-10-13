package dev.termlink.network.production

/**
 * A control lease was granted by REST after a newer realtime revocation had
 * already fenced the request. This is an expected arbitration outcome, not a
 * session-open failure. A physical connection change is a retryable IO failure.
 */
class ControlRequestSupersededException internal constructor() :
    IllegalStateException("control request was superseded by a newer revoke")
