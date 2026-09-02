use std::{
    collections::{HashSet, VecDeque},
    fmt,
    net::SocketAddr,
};

use termlink_direct_wire::{ConnectivityDatagram, DatagramKind, ExpectedDatagram, WireError};

pub const MAX_REPLAY_NONCES: usize = 64;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PathState {
    RelayOnly,
    Gathering,
    Probing,
    Authenticating,
    ShadowDirect,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DirectFailureReason {
    Disabled,
    InvalidEpoch,
    Replay,
    Authentication,
    Timeout,
    Socket,
    Quic,
    PeerProof,
    QueueFull,
    NetworkChanged,
    Internal,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum DirectEvent {
    StateChanged {
        state: PathState,
        epoch: u64,
    },
    DirectFailed {
        epoch: u64,
        reason: DirectFailureReason,
    },
    ShadowReady {
        epoch: u64,
        endpoint: SocketAddr,
    },
    IgnoredStale {
        received_epoch: u64,
        current_epoch: u64,
    },
}

/// Optional path state only. It has no Relay socket, durable ACK, session,
/// renderer, or Envelope APIs, so a direct failure cannot mutate those layers.
pub struct ShadowPathMachine {
    enabled: bool,
    state: PathState,
    epoch: u64,
    replay_nonces: HashSet<[u8; 16]>,
    replay_order: VecDeque<[u8; 16]>,
}

impl ShadowPathMachine {
    pub fn new(enabled: bool) -> Self {
        Self {
            enabled,
            state: PathState::RelayOnly,
            epoch: 0,
            replay_nonces: HashSet::with_capacity(MAX_REPLAY_NONCES),
            replay_order: VecDeque::with_capacity(MAX_REPLAY_NONCES),
        }
    }

    pub fn state(&self) -> PathState {
        self.state
    }

    pub fn epoch(&self) -> u64 {
        self.epoch
    }

    pub fn begin(&mut self, epoch: u64) -> DirectEvent {
        if !self.enabled {
            return self.fail(DirectFailureReason::Disabled);
        }
        if epoch <= self.epoch {
            return DirectEvent::IgnoredStale {
                received_epoch: epoch,
                current_epoch: self.epoch,
            };
        }
        if epoch == 0 || epoch > i64::MAX as u64 {
            return self.fail(DirectFailureReason::InvalidEpoch);
        }
        self.epoch = epoch;
        self.replay_nonces.clear();
        self.replay_order.clear();
        self.state = PathState::Gathering;
        DirectEvent::StateChanged {
            state: self.state,
            epoch,
        }
    }

    pub fn probing(&mut self, epoch: u64) -> DirectEvent {
        self.transition(epoch, PathState::Gathering, PathState::Probing)
    }

    pub fn accept_authenticated_datagram(
        &mut self,
        packet: &[u8],
        key: &[u8; 32],
        expected: &ExpectedDatagram,
    ) -> Result<ConnectivityDatagram, DirectEvent> {
        if expected.path_epoch < self.epoch {
            return Err(DirectEvent::IgnoredStale {
                received_epoch: expected.path_epoch,
                current_epoch: self.epoch,
            });
        }
        if self.state != PathState::Probing || expected.path_epoch != self.epoch {
            return Err(self.fail(DirectFailureReason::InvalidEpoch));
        }
        let datagram =
            ConnectivityDatagram::decode_authenticated(packet, key, expected).map_err(|error| {
                let reason = match error {
                    WireError::StaleEpoch => {
                        return DirectEvent::IgnoredStale {
                            received_epoch: expected.path_epoch,
                            current_epoch: self.epoch,
                        };
                    }
                    WireError::ContextMismatch | WireError::InvalidEpoch => {
                        DirectFailureReason::InvalidEpoch
                    }
                    _ => DirectFailureReason::Authentication,
                };
                self.fail(reason)
            })?;
        if datagram.kind == DatagramKind::ProbeAck
            && !self.remember_nonce(datagram.response_nonce.as_slice())
        {
            return Err(self.fail(DirectFailureReason::Replay));
        }
        Ok(datagram)
    }

    pub fn authenticating(&mut self, epoch: u64) -> DirectEvent {
        self.transition(epoch, PathState::Probing, PathState::Authenticating)
    }

    pub fn shadow_ready(&mut self, epoch: u64, endpoint: SocketAddr) -> DirectEvent {
        if epoch < self.epoch {
            return DirectEvent::IgnoredStale {
                received_epoch: epoch,
                current_epoch: self.epoch,
            };
        }
        if self.state != PathState::Authenticating || epoch != self.epoch {
            return self.fail(DirectFailureReason::InvalidEpoch);
        }
        self.state = PathState::ShadowDirect;
        DirectEvent::ShadowReady { epoch, endpoint }
    }

    pub fn fail(&mut self, reason: DirectFailureReason) -> DirectEvent {
        let failed_epoch = self.epoch;
        self.state = PathState::RelayOnly;
        self.replay_nonces.clear();
        self.replay_order.clear();
        DirectEvent::DirectFailed {
            epoch: failed_epoch,
            reason,
        }
    }

    pub fn network_changed(&mut self) -> DirectEvent {
        self.fail(DirectFailureReason::NetworkChanged)
    }

    fn transition(&mut self, epoch: u64, required: PathState, next: PathState) -> DirectEvent {
        if epoch < self.epoch {
            return DirectEvent::IgnoredStale {
                received_epoch: epoch,
                current_epoch: self.epoch,
            };
        }
        if self.state != required || epoch != self.epoch {
            return self.fail(DirectFailureReason::InvalidEpoch);
        }
        self.state = next;
        DirectEvent::StateChanged { state: next, epoch }
    }

    fn remember_nonce(&mut self, nonce: &[u8]) -> bool {
        let Ok(nonce): Result<[u8; 16], _> = nonce.try_into() else {
            return false;
        };
        if !self.replay_nonces.insert(nonce) {
            return false;
        }
        self.replay_order.push_back(nonce);
        if self.replay_order.len() > MAX_REPLAY_NONCES
            && let Some(oldest) = self.replay_order.pop_front()
        {
            self.replay_nonces.remove(&oldest);
        }
        true
    }
}

impl fmt::Debug for ShadowPathMachine {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("ShadowPathMachine")
            .field("enabled", &self.enabled)
            .field("state", &self.state)
            .field("epoch", &self.epoch)
            .field("replay_nonce_count", &self.replay_nonces.len())
            .finish()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn old_or_duplicate_epoch_fails_only_the_direct_path() {
        let mut machine = ShadowPathMachine::new(true);
        assert!(matches!(
            machine.begin(7),
            DirectEvent::StateChanged {
                state: PathState::Gathering,
                epoch: 7
            }
        ));
        let event = machine.begin(7);
        assert_eq!(machine.state(), PathState::Gathering);
        assert_eq!(
            event,
            DirectEvent::IgnoredStale {
                received_epoch: 7,
                current_epoch: 7
            }
        );
    }

    #[test]
    fn every_failure_synchronously_returns_to_relay_only() {
        for reason in [
            DirectFailureReason::Authentication,
            DirectFailureReason::Timeout,
            DirectFailureReason::Socket,
            DirectFailureReason::Quic,
            DirectFailureReason::PeerProof,
            DirectFailureReason::QueueFull,
            DirectFailureReason::Internal,
        ] {
            let mut machine = ShadowPathMachine::new(true);
            machine.begin(1);
            let event = machine.fail(reason);
            assert_eq!(machine.state(), PathState::RelayOnly);
            assert_eq!(event, DirectEvent::DirectFailed { epoch: 1, reason });
        }
    }

    #[test]
    fn event_surface_has_no_disconnect_or_durable_ack_action() {
        fn exhaust(event: DirectEvent) -> &'static str {
            match event {
                DirectEvent::StateChanged { .. } => "state",
                DirectEvent::DirectFailed { .. } => "direct_failed",
                DirectEvent::ShadowReady { .. } => "shadow_ready",
                DirectEvent::IgnoredStale { .. } => "ignored_stale",
            }
        }
        let mut machine = ShadowPathMachine::new(true);
        assert_eq!(
            exhaust(machine.fail(DirectFailureReason::Quic)),
            "direct_failed"
        );
    }
}
