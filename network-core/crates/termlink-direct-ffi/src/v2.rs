use ring::rand::{SecureRandom, SystemRandom};
use std::{
    collections::VecDeque,
    ffi::c_void,
    mem::size_of,
    net::{IpAddr, Ipv4Addr, Ipv6Addr, SocketAddr},
    panic::{AssertUnwindSafe, catch_unwind},
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, AtomicU64, Ordering},
        mpsc::{self, Receiver, RecvTimeoutError, SyncSender, TrySendError},
    },
    thread,
    time::{Duration, Instant},
};

#[cfg(target_os = "android")]
use termlink_direct_nat::AndroidNetworkBinder;
use termlink_direct_nat::{
    AuthenticatedProbePacket, DirectUdpSocket, MAX_STUN_SERVERS, validate_candidate_endpoint,
};
use termlink_direct_quic::{
    DirectPathBindingInputs, Ed25519PeerProofVerifier, ExpectedPeer, PeerBindingContext,
    PeerProofRequest, PeerRole, ShadowEndpoint, TemporaryIdentity, direct_path_binding_sha256,
    exchange_and_authenticate_shadow_connection, shadow_echo_once, shadow_ping,
    tls_exporter_sha256,
};
use termlink_direct_wire::{
    CandidatePair, ConnectivityDatagram, DatagramKind, DirectTicketExpectation, DirectTicketRole,
    ExpectedDatagram, ProbeFields, WireError, verify_direct_path_ticket,
};

pub const ABI_VERSION_V2: u32 = 2;
/// ABI v2 is retained only so loaders can identify and reject the legacy
/// single-probe-key contract. Production and test callers must use v3.
const LEGACY_ABI_V2_RUNTIME_ENABLED: bool = false;
pub const MAX_V2_QUEUE_CAPACITY: usize = 64;
pub const MAX_TICKET_BYTES_V2: usize = 2_048;
pub const MAX_IDENTITY_CERTIFICATE_BYTES_V2: usize = 2_048;
pub const MAX_EVENT_PAYLOAD_BYTES_V2: usize = 2_048;
pub const MAX_SIGNATURE_BYTES_V2: usize = 128;
pub const MAX_PROOF_NONCE_BYTES_V2: usize = 32;
pub const MIN_PROOF_NONCE_BYTES_V2: usize = 16;
pub const MIN_ATTEMPT_TIMEOUT_MS_V2: u32 = 500;
pub const MAX_ATTEMPT_TIMEOUT_MS_V2: u32 = 10_000;

pub const PLATFORM_WINDOWS_V2: u32 = 1;
pub const PLATFORM_ANDROID_V2: u32 = 2;

pub const ROLE_INITIATOR_V2: u32 = 1;
pub const ROLE_RESPONDER_V2: u32 = 2;

pub const EVENT_IDENTITY_READY_V2: u32 = 1;
pub const EVENT_STATE_CHANGED_V2: u32 = 2;
pub const EVENT_DIRECT_FAILED_V2: u32 = 3;
pub const EVENT_SIGN_REQUEST_V2: u32 = 4;
pub const EVENT_SHADOW_READY_V2: u32 = 5;
pub const EVENT_IGNORED_STALE_V2: u32 = 6;

pub const STATE_RELAY_ONLY_V2: u32 = 1;
pub const STATE_GATHERING_V2: u32 = 2;
pub const STATE_PROBING_V2: u32 = 3;
pub const STATE_AUTHENTICATING_V2: u32 = 4;
pub const STATE_SHADOW_DIRECT_V2: u32 = 5;

pub const REASON_DISABLED_V2: u32 = 1;
pub const REASON_INVALID_ABI_V2: u32 = 2;
pub const REASON_INVALID_EPOCH_V2: u32 = 3;
pub const REASON_INVALID_PLATFORM_V2: u32 = 4;
pub const REASON_INVALID_CANDIDATE_V2: u32 = 5;
pub const REASON_INVALID_TICKET_V2: u32 = 6;
pub const REASON_SOCKET_V2: u32 = 7;
pub const REASON_PROBE_V2: u32 = 8;
pub const REASON_QUIC_V2: u32 = 9;
pub const REASON_PEER_PROOF_V2: u32 = 10;
pub const REASON_SIGNATURE_TIMEOUT_V2: u32 = 11;
pub const REASON_CANCELLED_V2: u32 = 12;
pub const REASON_NETWORK_CHANGED_V2: u32 = 13;
pub const REASON_QUEUE_FULL_V2: u32 = 14;
pub const REASON_INTERNAL_V2: u32 = 15;

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct TermLinkDirectCreateV2 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub enabled: u32,
    pub queue_capacity: u32,
    pub platform: u32,
    pub reserved: [u32; 3],
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct TermLinkSocketAddrV2 {
    pub ip: [u8; 16],
    pub ip_length: u32,
    pub port: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct TermLinkDirectStartV2 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub epoch: u64,
    pub attempt_id: [u8; 16],
    pub connection_id: [u8; 16],
    pub local_device_id: [u8; 16],
    pub peer_device_id: [u8; 16],
    pub role: u32,
    pub timeout_ms: u32,
    pub network_handle: u64,
    pub local_bind: TermLinkSocketAddrV2,
    pub remote_candidate: TermLinkSocketAddrV2,
    pub stun_count: u32,
    pub reserved0: u32,
    pub stun_servers: [TermLinkSocketAddrV2; MAX_STUN_SERVERS],
    pub ticket_length: u32,
    pub reserved1: u32,
    pub ticket: [u8; MAX_TICKET_BYTES_V2],
    pub server_signing_public_key: [u8; 32],
    pub peer_certificate_sha256: [u8; 32],
    pub peer_device_signing_public_key: [u8; 32],
    pub probe_hmac_key: [u8; 32],
    pub ticket_id: [u8; 16],
    pub nomination_id: [u8; 16],
    pub initiator_candidate_id: [u8; 16],
    pub responder_candidate_id: [u8; 16],
    pub local_probe_nonce: [u8; 16],
    pub peer_probe_nonce: [u8; 16],
    pub local_proof_nonce_length: u32,
    pub peer_proof_nonce_length: u32,
    pub local_proof_nonce: [u8; MAX_PROOF_NONCE_BYTES_V2],
    pub peer_proof_nonce: [u8; MAX_PROOF_NONCE_BYTES_V2],
    pub now_ms: i64,
    pub reserved: [u64; 4],
}

impl Default for TermLinkDirectStartV2 {
    fn default() -> Self {
        Self {
            abi_version: 0,
            struct_size: 0,
            epoch: 0,
            attempt_id: [0; 16],
            connection_id: [0; 16],
            local_device_id: [0; 16],
            peer_device_id: [0; 16],
            role: 0,
            timeout_ms: 0,
            network_handle: 0,
            local_bind: TermLinkSocketAddrV2::default(),
            remote_candidate: TermLinkSocketAddrV2::default(),
            stun_count: 0,
            reserved0: 0,
            stun_servers: [TermLinkSocketAddrV2::default(); MAX_STUN_SERVERS],
            ticket_length: 0,
            reserved1: 0,
            ticket: [0; MAX_TICKET_BYTES_V2],
            server_signing_public_key: [0; 32],
            peer_certificate_sha256: [0; 32],
            peer_device_signing_public_key: [0; 32],
            probe_hmac_key: [0; 32],
            ticket_id: [0; 16],
            nomination_id: [0; 16],
            initiator_candidate_id: [0; 16],
            responder_candidate_id: [0; 16],
            local_probe_nonce: [0; 16],
            peer_probe_nonce: [0; 16],
            local_proof_nonce_length: 0,
            peer_proof_nonce_length: 0,
            local_proof_nonce: [0; MAX_PROOF_NONCE_BYTES_V2],
            peer_proof_nonce: [0; MAX_PROOF_NONCE_BYTES_V2],
            now_ms: 0,
            reserved: [0; 4],
        }
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct TermLinkDirectControlV2 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub epoch: u64,
    pub attempt_id: [u8; 16],
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct TermLinkDirectSignatureV2 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub epoch: u64,
    pub attempt_id: [u8; 16],
    pub request_id: u64,
    pub signature_length: u32,
    pub reserved: u32,
    pub signature: [u8; MAX_SIGNATURE_BYTES_V2],
}

impl Default for TermLinkDirectSignatureV2 {
    fn default() -> Self {
        Self {
            abi_version: 0,
            struct_size: 0,
            epoch: 0,
            attempt_id: [0; 16],
            request_id: 0,
            signature_length: 0,
            reserved: 0,
            signature: [0; MAX_SIGNATURE_BYTES_V2],
        }
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct TermLinkDirectEventV2 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub kind: u32,
    pub state: u32,
    pub reason: u32,
    pub payload_length: u32,
    pub epoch: u64,
    pub generation: u64,
    pub request_id: u64,
    pub attempt_id: [u8; 16],
    pub endpoint: TermLinkSocketAddrV2,
    pub payload: [u8; MAX_EVENT_PAYLOAD_BYTES_V2],
}

impl Default for TermLinkDirectEventV2 {
    fn default() -> Self {
        Self {
            abi_version: ABI_VERSION_V2,
            struct_size: size_of::<Self>() as u32,
            kind: 0,
            state: STATE_RELAY_ONLY_V2,
            reason: 0,
            payload_length: 0,
            epoch: 0,
            generation: 0,
            request_id: 0,
            attempt_id: [0; 16],
            endpoint: TermLinkSocketAddrV2::default(),
            payload: [0; MAX_EVENT_PAYLOAD_BYTES_V2],
        }
    }
}

pub(crate) enum CommandV2 {
    Start(Box<TermLinkDirectStartV2>),
    Cancel(TermLinkDirectControlV2),
    NetworkChanged(TermLinkDirectControlV2),
    Signature(Box<TermLinkDirectSignatureV2>),
}

pub(crate) struct EventQueueV2 {
    abi_version: u32,
    capacity: usize,
    queue: Mutex<VecDeque<TermLinkDirectEventV2>>,
    generation: AtomicU64,
}

impl EventQueueV2 {
    pub(crate) fn new(capacity: usize, abi_version: u32) -> Self {
        Self {
            abi_version,
            capacity,
            queue: Mutex::new(VecDeque::with_capacity(capacity)),
            generation: AtomicU64::new(0),
        }
    }

    fn next_generation(&self) -> u64 {
        self.generation.fetch_add(1, Ordering::AcqRel) + 1
    }

    pub(crate) fn push(&self, mut event: TermLinkDirectEventV2) -> bool {
        event.abi_version = self.abi_version;
        event.generation = self.next_generation();
        let mut queue = self.queue.lock().unwrap_or_else(|error| error.into_inner());
        if queue.len() == self.capacity {
            queue.clear();
            return false;
        }
        queue.push_back(event);
        true
    }

    /// Advisory events must never clear an authoritative failure/state event
    /// when stale or untrusted input arrives while the bounded queue is full.
    pub(crate) fn push_best_effort(&self, mut event: TermLinkDirectEventV2) -> bool {
        event.abi_version = self.abi_version;
        let mut queue = self.queue.lock().unwrap_or_else(|error| error.into_inner());
        if queue.len() == self.capacity {
            return false;
        }
        event.generation = self.next_generation();
        queue.push_back(event);
        true
    }

    pub(crate) fn force_failure(&self, epoch: u64, attempt_id: [u8; 16], reason: u32) {
        let mut queue = self.queue.lock().unwrap_or_else(|error| error.into_inner());
        queue.clear();
        let mut event = failure_event(epoch, attempt_id, reason);
        event.abi_version = self.abi_version;
        event.generation = self.next_generation();
        queue.push_back(event);
    }

    pub(crate) fn poll(&self) -> Option<TermLinkDirectEventV2> {
        self.queue
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .pop_front()
    }
}

pub struct NativeBridgeV2 {
    commands: SyncSender<CommandV2>,
    events: Arc<EventQueueV2>,
    cancel_epoch: Arc<AtomicU64>,
    network_changed_epoch: Arc<AtomicU64>,
    relay_only: Arc<AtomicBool>,
    authoritative_epoch: Arc<AtomicU64>,
    current_attempt_high: Arc<AtomicU64>,
    current_attempt_low: Arc<AtomicU64>,
}

impl NativeBridgeV2 {
    fn create(config: TermLinkDirectCreateV2) -> Result<Self, ()> {
        if config.abi_version != ABI_VERSION_V2
            || config.struct_size as usize != size_of::<TermLinkDirectCreateV2>()
            || config.enabled > 1
            || !matches!(config.platform, PLATFORM_WINDOWS_V2 | PLATFORM_ANDROID_V2)
            || config.queue_capacity == 0
            || config.queue_capacity as usize > MAX_V2_QUEUE_CAPACITY
        {
            return Err(());
        }
        let identity = TemporaryIdentity::generate().map_err(|_| ())?;
        let certificate = identity.certificate_der();
        if certificate.len() > MAX_IDENTITY_CERTIFICATE_BYTES_V2 {
            return Err(());
        }
        let capacity = config.queue_capacity as usize;
        let (commands, receiver) = mpsc::sync_channel(capacity);
        let events = Arc::new(EventQueueV2::new(capacity, ABI_VERSION_V2));
        let cancel_epoch = Arc::new(AtomicU64::new(0));
        let network_changed_epoch = Arc::new(AtomicU64::new(0));
        let relay_only = Arc::new(AtomicBool::new(true));
        let authoritative_epoch = Arc::new(AtomicU64::new(0));
        let current_attempt_high = Arc::new(AtomicU64::new(0));
        let current_attempt_low = Arc::new(AtomicU64::new(0));

        let mut identity_event = TermLinkDirectEventV2 {
            kind: EVENT_IDENTITY_READY_V2,
            payload_length: certificate.len() as u32,
            ..TermLinkDirectEventV2::default()
        };
        identity_event.payload[..certificate.len()].copy_from_slice(certificate.as_ref());
        let _ = events.push(identity_event);

        let worker_events = Arc::clone(&events);
        let worker_cancel = Arc::clone(&cancel_epoch);
        let worker_network_changed = Arc::clone(&network_changed_epoch);
        let worker_relay_only = Arc::clone(&relay_only);
        let worker_epoch = Arc::clone(&authoritative_epoch);
        let worker_attempt_high = Arc::clone(&current_attempt_high);
        let worker_attempt_low = Arc::clone(&current_attempt_low);
        thread::Builder::new()
            .name("termlink-direct-v2-shadow".into())
            .spawn(move || {
                worker_v2(
                    config,
                    identity,
                    receiver,
                    worker_events,
                    worker_cancel,
                    worker_network_changed,
                    worker_relay_only,
                    worker_epoch,
                    worker_attempt_high,
                    worker_attempt_low,
                )
            })
            .map_err(|_| ())?;
        Ok(Self {
            commands,
            events,
            cancel_epoch,
            network_changed_epoch,
            relay_only,
            authoritative_epoch,
            current_attempt_high,
            current_attempt_low,
        })
    }

    fn submit(&self, command: CommandV2, epoch: u64, attempt_id: [u8; 16]) -> i32 {
        match self.commands.try_send(command) {
            Ok(()) => 0,
            Err(TrySendError::Full(_)) => {
                self.cancel_epoch.store(epoch, Ordering::Release);
                self.relay_only.store(true, Ordering::Release);
                self.events
                    .force_failure(epoch, attempt_id, REASON_QUEUE_FULL_V2);
                -4
            }
            Err(TrySendError::Disconnected(_)) => {
                self.relay_only.store(true, Ordering::Release);
                self.events
                    .force_failure(epoch, attempt_id, REASON_INTERNAL_V2);
                -1
            }
        }
    }

    fn current_attempt(&self) -> [u8; 16] {
        join_attempt(
            self.current_attempt_high.load(Ordering::Acquire),
            self.current_attempt_low.load(Ordering::Acquire),
        )
    }
}

#[allow(clippy::too_many_arguments)]
fn worker_v2(
    config: TermLinkDirectCreateV2,
    identity: TemporaryIdentity,
    commands: Receiver<CommandV2>,
    events: Arc<EventQueueV2>,
    cancel_epoch: Arc<AtomicU64>,
    network_changed_epoch: Arc<AtomicU64>,
    relay_only: Arc<AtomicBool>,
    authoritative_epoch: Arc<AtomicU64>,
    current_attempt_high: Arc<AtomicU64>,
    current_attempt_low: Arc<AtomicU64>,
) {
    let mut identity = Some(identity);
    let mut processed_epoch = 0_u64;
    while let Ok(command) = commands.recv() {
        match command {
            CommandV2::Start(start) => {
                if start.epoch <= processed_epoch {
                    let _ =
                        events.push(stale_event(start.epoch, start.attempt_id, processed_epoch));
                    continue;
                }
                processed_epoch = start.epoch;
                let active_epoch = start.epoch;
                let active_attempt = start.attempt_id;
                let (high, low) = split_attempt(start.attempt_id);
                current_attempt_high.store(high, Ordering::Release);
                current_attempt_low.store(low, Ordering::Release);
                relay_only.store(true, Ordering::Release);

                let result = if config.enabled == 0 {
                    Err(REASON_DISABLED_V2)
                } else if let Some(attempt_identity) = identity.take() {
                    run_attempt_v2(
                        config.platform,
                        *start,
                        attempt_identity,
                        &commands,
                        &events,
                        &cancel_epoch,
                        &network_changed_epoch,
                        &relay_only,
                    )
                } else {
                    Err(REASON_INTERNAL_V2)
                };
                if let Err(reason) = result {
                    relay_only.store(true, Ordering::Release);
                    events.force_failure(active_epoch, active_attempt, reason);
                }
            }
            CommandV2::Cancel(control) => {
                handle_idle_control(
                    control,
                    REASON_CANCELLED_V2,
                    &events,
                    &relay_only,
                    &authoritative_epoch,
                    &current_attempt_high,
                    &current_attempt_low,
                );
            }
            CommandV2::NetworkChanged(control) => {
                handle_idle_control(
                    control,
                    REASON_NETWORK_CHANGED_V2,
                    &events,
                    &relay_only,
                    &authoritative_epoch,
                    &current_attempt_high,
                    &current_attempt_low,
                );
            }
            CommandV2::Signature(signature) => {
                let current = authoritative_epoch.load(Ordering::Acquire);
                let _ = events.push(stale_event(signature.epoch, signature.attempt_id, current));
            }
        }
    }
}

#[allow(clippy::too_many_arguments)]
fn run_attempt_v2(
    platform: u32,
    start: TermLinkDirectStartV2,
    identity: TemporaryIdentity,
    commands: &Receiver<CommandV2>,
    events: &EventQueueV2,
    cancel_epoch: &AtomicU64,
    network_changed_epoch: &AtomicU64,
    relay_only: &AtomicBool,
) -> Result<(), u32> {
    let validated = validate_start(platform, &start)?;
    push_or_overflow(
        events,
        state_event(start.epoch, start.attempt_id, STATE_GATHERING_V2),
        cancel_epoch,
        start.epoch,
    )?;

    let socket = bind_platform_socket(platform, validated.local_bind, start.network_handle)?;
    let peer_probe_hmac_key = start.probe_hmac_key;
    run_bound_attempt(
        start,
        peer_probe_hmac_key,
        identity,
        socket,
        validated,
        commands,
        events,
        cancel_epoch,
        network_changed_epoch,
        relay_only,
    )
}

pub(crate) trait SignatureInbox {
    #[allow(clippy::too_many_arguments)]
    fn wait_signature(
        &self,
        events: &EventQueueV2,
        cancel_epoch: &AtomicU64,
        network_changed_epoch: &AtomicU64,
        epoch: u64,
        attempt_id: [u8; 16],
        request_id: u64,
        timeout: Duration,
    ) -> Result<Vec<u8>, u32>;
}

impl SignatureInbox for Receiver<CommandV2> {
    fn wait_signature(
        &self,
        events: &EventQueueV2,
        cancel_epoch: &AtomicU64,
        network_changed_epoch: &AtomicU64,
        epoch: u64,
        attempt_id: [u8; 16],
        request_id: u64,
        timeout: Duration,
    ) -> Result<Vec<u8>, u32> {
        wait_for_signature(
            self,
            events,
            cancel_epoch,
            network_changed_epoch,
            epoch,
            attempt_id,
            request_id,
            timeout,
        )
    }
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn run_bound_attempt<I: SignatureInbox>(
    start: TermLinkDirectStartV2,
    peer_probe_hmac_key: [u8; 32],
    identity: TemporaryIdentity,
    socket: DirectUdpSocket,
    validated: ValidatedStart,
    commands: &I,
    events: &EventQueueV2,
    cancel_epoch: &AtomicU64,
    network_changed_epoch: &AtomicU64,
    relay_only: &AtomicBool,
) -> Result<(), u32> {
    run_bound_attempt_inner(
        start,
        Some(peer_probe_hmac_key),
        identity,
        socket,
        validated,
        commands,
        events,
        cancel_epoch,
        network_changed_epoch,
        relay_only,
    )
}

/// Continues with QUIC, peer proof and shadow validation after a newer native
/// connectivity-check state machine has already authenticated both directions
/// for the committed pair. This never skips ticket, certificate, device-proof,
/// TLS exporter, ping/echo or path-binding validation.
#[allow(clippy::too_many_arguments)]
#[expect(
    dead_code,
    reason = "retained as the reviewed prevalidated QUIC/proof adapter for independent newer ABIs"
)]
pub(crate) fn run_prevalidated_bound_attempt<I: SignatureInbox>(
    start: TermLinkDirectStartV2,
    identity: TemporaryIdentity,
    socket: DirectUdpSocket,
    validated: ValidatedStart,
    commands: &I,
    events: &EventQueueV2,
    cancel_epoch: &AtomicU64,
    network_changed_epoch: &AtomicU64,
    relay_only: &AtomicBool,
) -> Result<(), u32> {
    run_bound_attempt_inner(
        start,
        None,
        identity,
        socket,
        validated,
        commands,
        events,
        cancel_epoch,
        network_changed_epoch,
        relay_only,
    )
}

#[allow(clippy::too_many_arguments)]
fn run_bound_attempt_inner<I: SignatureInbox>(
    start: TermLinkDirectStartV2,
    peer_probe_hmac_key: Option<[u8; 32]>,
    identity: TemporaryIdentity,
    socket: DirectUdpSocket,
    validated: ValidatedStart,
    commands: &I,
    events: &EventQueueV2,
    cancel_epoch: &AtomicU64,
    network_changed_epoch: &AtomicU64,
    relay_only: &AtomicBool,
) -> Result<(), u32> {
    if let Some(peer_probe_hmac_key) = peer_probe_hmac_key {
        push_or_overflow(
            events,
            state_event(start.epoch, start.attempt_id, STATE_PROBING_V2),
            cancel_epoch,
            start.epoch,
        )?;

        let pair = CandidatePair {
            initiator_candidate_id: start.initiator_candidate_id,
            responder_candidate_id: start.responder_candidate_id,
        };
        let local_probe = ConnectivityDatagram::new_probe(ProbeFields {
            connection_id: start.connection_id,
            path_epoch: start.epoch,
            ticket_id: start.ticket_id,
            nomination_id: start.nomination_id,
            pair: pair.clone(),
            sender_device_id: start.local_device_id,
            probe_nonce: start.local_probe_nonce,
            sent_at_ms: start.now_ms,
            anti_amplification_padding: start.attempt_id,
        })
        .map_err(|_| REASON_PROBE_V2)?;
        let local_wire = local_probe
            .encode_authenticated(&start.probe_hmac_key)
            .map_err(|_| REASON_PROBE_V2)?;
        let expected_peer_probe = ExpectedDatagram {
            kind: DatagramKind::Probe,
            connection_id: start.connection_id,
            path_epoch: start.epoch,
            ticket_id: start.ticket_id,
            nomination_id: start.nomination_id,
            pair: pair.clone(),
            sender_device_id: start.peer_device_id,
            probe_nonce: Some(start.peer_probe_nonce),
            now_ms: start.now_ms,
            max_clock_skew_ms: 30_000,
        };
        let expected_peer_ack = ExpectedDatagram {
            kind: DatagramKind::ProbeAck,
            connection_id: start.connection_id,
            path_epoch: start.epoch,
            ticket_id: start.ticket_id,
            nomination_id: start.nomination_id,
            pair,
            sender_device_id: start.peer_device_id,
            probe_nonce: Some(start.local_probe_nonce),
            now_ms: start.now_ms,
            max_clock_skew_ms: 30_000,
        };
        let mut response_nonce = [0_u8; 16];
        SystemRandom::new()
            .fill(&mut response_nonce)
            .map_err(|_| REASON_PROBE_V2)?;
        let mut peer_probe_seen = false;
        let mut local_probe_ack_seen = false;
        let outcome = socket
            .discover_and_probe(
                validated.remote,
                &validated.stun_servers,
                &[
                    local_wire.clone(),
                    local_wire.clone(),
                    local_wire.clone(),
                    local_wire,
                ],
                validated.timeout,
                |packet| {
                    match ConnectivityDatagram::decode_authenticated(
                        packet,
                        &peer_probe_hmac_key,
                        &expected_peer_probe,
                    ) {
                        Ok(probe) => {
                            peer_probe_seen = true;
                            let reply = ConnectivityDatagram::acknowledgement(
                                &probe,
                                start.local_device_id,
                                response_nonce,
                                start.now_ms,
                            )
                            .and_then(|ack| {
                                ack.encode_ack_without_amplification(
                                    packet.len(),
                                    &start.probe_hmac_key,
                                )
                            })
                            .ok()?;
                            return Some(AuthenticatedProbePacket {
                                reply,
                                path_confirmed: local_probe_ack_seen,
                            });
                        }
                        Err(WireError::StaleEpoch) => return None,
                        Err(_) => {}
                    }

                    let acknowledgement = ConnectivityDatagram::decode_authenticated(
                        packet,
                        &peer_probe_hmac_key,
                        &expected_peer_ack,
                    )
                    .ok()?;
                    if acknowledgement
                        .response_nonce
                        .iter()
                        .all(|value| *value == 0)
                    {
                        return None;
                    }
                    local_probe_ack_seen = true;
                    Some(AuthenticatedProbePacket {
                        reply: Vec::new(),
                        path_confirmed: peer_probe_seen,
                    })
                },
                || {
                    is_cancelled(cancel_epoch, start.epoch)
                        || is_cancelled(network_changed_epoch, start.epoch)
                },
            )
            .map_err(|_error| {
                #[cfg(test)]
                eprintln!("direct v2 probe failure: {_error:?}");
                cancellation_reason(
                    cancel_epoch,
                    network_changed_epoch,
                    start.epoch,
                    REASON_PROBE_V2,
                )
            })?;
        if outcome.authenticated_peer_packet.is_none() {
            return Err(cancellation_reason(
                cancel_epoch,
                network_changed_epoch,
                start.epoch,
                REASON_PROBE_V2,
            ));
        }
        if is_cancelled(cancel_epoch, start.epoch)
            || is_cancelled(network_changed_epoch, start.epoch)
        {
            return Err(cancellation_reason(
                cancel_epoch,
                network_changed_epoch,
                start.epoch,
                REASON_CANCELLED_V2,
            ));
        }
    }

    push_or_overflow(
        events,
        state_event(start.epoch, start.attempt_id, STATE_AUTHENTICATING_V2),
        cancel_epoch,
        start.epoch,
    )?;
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .map_err(|_| REASON_INTERNAL_V2)?;
    runtime.block_on(async {
        let local_role = validated.local_role;
        let endpoint = if local_role == PeerRole::Initiator {
            ShadowEndpoint::client(
                socket.into_quic_handoff(),
                validated.remote,
                identity,
                start.peer_certificate_sha256,
            )
            .map_err(|_error| {
                #[cfg(test)]
                eprintln!("direct v2 client endpoint failure: {_error:?}");
                REASON_QUIC_V2
            })?
        } else {
            ShadowEndpoint::server(
                socket.into_quic_handoff(),
                validated.remote,
                identity,
                start.peer_certificate_sha256,
            )
            .map_err(|_error| {
                #[cfg(test)]
                eprintln!("direct v2 server endpoint failure: {_error:?}");
                REASON_QUIC_V2
            })?
        };
        let connection = if local_role == PeerRole::Initiator {
            endpoint
                .connect_pinned(validated.remote, start.peer_certificate_sha256)
                .await
                .map_err(|_error| {
                    #[cfg(test)]
                    eprintln!("direct v2 client QUIC failure: {_error:?}");
                    REASON_QUIC_V2
                })?
        } else {
            endpoint
                .accept_pinned(validated.remote, start.peer_certificate_sha256)
                .await
                .map_err(|_error| {
                    #[cfg(test)]
                    eprintln!("direct v2 server QUIC failure: {_error:?}");
                    REASON_QUIC_V2
                })?
        };
        let local_certificate_sha256 = endpoint.certificate_sha256();
        let binding = PeerBindingContext {
            ticket_sha256: validated.ticket_sha256,
            connection_id: start.connection_id,
            path_epoch: start.epoch,
        };
        let exporter =
            tls_exporter_sha256(&connection, &binding).map_err(|_| REASON_PEER_PROOF_V2)?;
        let local_proof = PeerProofRequest {
            ticket_sha256: binding.ticket_sha256,
            role: local_role,
            device_id: start.local_device_id,
            peer_device_id: start.peer_device_id,
            connection_id: start.connection_id,
            path_epoch: start.epoch,
            proof_nonce: validated.local_proof_nonce,
            tls_exporter_sha256: exporter,
        };
        let transcript = local_proof.transcript().map_err(|_| REASON_PEER_PROOF_V2)?;
        let request_id = events.next_generation();
        let mut sign_event = TermLinkDirectEventV2 {
            kind: EVENT_SIGN_REQUEST_V2,
            state: STATE_AUTHENTICATING_V2,
            epoch: start.epoch,
            attempt_id: start.attempt_id,
            request_id,
            payload_length: transcript.len() as u32,
            ..TermLinkDirectEventV2::default()
        };
        sign_event.payload[..transcript.len()].copy_from_slice(&transcript);
        push_or_overflow(events, sign_event, cancel_epoch, start.epoch)?;
        let signature = commands.wait_signature(
            events,
            cancel_epoch,
            network_changed_epoch,
            start.epoch,
            start.attempt_id,
            request_id,
            validated.timeout,
        )?;
        let verifier = Ed25519PeerProofVerifier::new(start.peer_device_signing_public_key);
        let authenticated = exchange_and_authenticate_shadow_connection(
            connection,
            &binding,
            ExpectedPeer {
                role: opposite_role(local_role),
                device_id: start.peer_device_id,
                local_device_id: start.local_device_id,
            },
            &validated.ticket,
            &local_proof,
            &signature,
            &verifier,
        )
        .await
        .map_err(|_| REASON_PEER_PROOF_V2)?;
        let local_proof_sha256 = termlink_direct_wire::sha256(&transcript);
        let peer_proof_sha256 = authenticated.peer_proof_sha256();
        let (initiator_certificate_sha256, responder_certificate_sha256) =
            if local_role == PeerRole::Initiator {
                (local_certificate_sha256, start.peer_certificate_sha256)
            } else {
                (start.peer_certificate_sha256, local_certificate_sha256)
            };
        let (initiator_proof_sha256, responder_proof_sha256) = if local_role == PeerRole::Initiator
        {
            (local_proof_sha256, peer_proof_sha256)
        } else {
            (peer_proof_sha256, local_proof_sha256)
        };
        let path_binding = direct_path_binding_sha256(DirectPathBindingInputs {
            ticket_sha256: binding.ticket_sha256,
            initiator_candidate_id: start.initiator_candidate_id,
            responder_candidate_id: start.responder_candidate_id,
            initiator_certificate_sha256,
            responder_certificate_sha256,
            initiator_proof_sha256,
            responder_proof_sha256,
            tls_exporter_sha256: exporter,
        });
        if local_role == PeerRole::Initiator {
            shadow_ping(&authenticated, start.attempt_id)
                .await
                .map_err(|_error| {
                    #[cfg(test)]
                    eprintln!("direct v2 ping failure: {_error:?}");
                    REASON_QUIC_V2
                })?;
        } else {
            shadow_echo_once(&authenticated).await.map_err(|_error| {
                #[cfg(test)]
                eprintln!("direct v2 echo failure: {_error:?}");
                REASON_QUIC_V2
            })?;
        }
        if is_cancelled(cancel_epoch, start.epoch)
            || is_cancelled(network_changed_epoch, start.epoch)
        {
            return Err(cancellation_reason(
                cancel_epoch,
                network_changed_epoch,
                start.epoch,
                REASON_CANCELLED_V2,
            ));
        }
        relay_only.store(false, Ordering::Release);
        let mut ready = TermLinkDirectEventV2 {
            kind: EVENT_SHADOW_READY_V2,
            state: STATE_SHADOW_DIRECT_V2,
            epoch: start.epoch,
            attempt_id: start.attempt_id,
            endpoint: encode_socket(validated.remote),
            ..TermLinkDirectEventV2::default()
        };
        ready.payload[..32].copy_from_slice(&path_binding);
        ready.payload_length = 32;
        push_or_overflow(events, ready, cancel_epoch, start.epoch)?;
        endpoint.close();
        Ok(())
    })
}

pub(crate) struct ValidatedStart {
    pub(crate) local_bind: SocketAddr,
    pub(crate) remote: SocketAddr,
    pub(crate) stun_servers: Vec<SocketAddr>,
    pub(crate) timeout: Duration,
    pub(crate) local_role: PeerRole,
    pub(crate) local_proof_nonce: Vec<u8>,
    pub(crate) ticket: Vec<u8>,
    pub(crate) ticket_sha256: [u8; 32],
}

pub(crate) fn validate_start(
    platform: u32,
    start: &TermLinkDirectStartV2,
) -> Result<ValidatedStart, u32> {
    if start.abi_version != ABI_VERSION_V2
        || start.struct_size as usize != size_of::<TermLinkDirectStartV2>()
    {
        return Err(REASON_INVALID_ABI_V2);
    }
    if start.epoch == 0
        || start.epoch > i64::MAX as u64
        || start.attempt_id == [0; 16]
        || start.connection_id == [0; 16]
        || start.local_device_id == start.peer_device_id
    {
        return Err(REASON_INVALID_EPOCH_V2);
    }
    if (platform == PLATFORM_ANDROID_V2 && start.network_handle == 0)
        || (platform == PLATFORM_WINDOWS_V2 && start.network_handle != 0)
    {
        return Err(REASON_INVALID_PLATFORM_V2);
    }
    let local_bind = decode_socket(start.local_bind, true)?;
    let remote = decode_socket(start.remote_candidate, false)?;
    validate_candidate_endpoint(remote).map_err(|_| REASON_INVALID_CANDIDATE_V2)?;
    let stun_count = start.stun_count as usize;
    if stun_count > MAX_STUN_SERVERS {
        return Err(REASON_INVALID_CANDIDATE_V2);
    }
    let stun_servers = start.stun_servers[..stun_count]
        .iter()
        .map(|value| decode_socket(*value, false))
        .collect::<Result<Vec<_>, _>>()?;
    let timeout =
        if (MIN_ATTEMPT_TIMEOUT_MS_V2..=MAX_ATTEMPT_TIMEOUT_MS_V2).contains(&start.timeout_ms) {
            Duration::from_millis(u64::from(start.timeout_ms))
        } else {
            return Err(REASON_INVALID_ABI_V2);
        };
    let local_role = match start.role {
        ROLE_INITIATOR_V2 => PeerRole::Initiator,
        ROLE_RESPONDER_V2 => PeerRole::Responder,
        _ => return Err(REASON_INVALID_ABI_V2),
    };
    let ticket_length = start.ticket_length as usize;
    let local_nonce_length = start.local_proof_nonce_length as usize;
    if ticket_length == 0
        || ticket_length > MAX_TICKET_BYTES_V2
        || start.peer_certificate_sha256 == [0; 32]
        || !(MIN_PROOF_NONCE_BYTES_V2..=MAX_PROOF_NONCE_BYTES_V2).contains(&local_nonce_length)
    {
        return Err(REASON_INVALID_ABI_V2);
    }
    let ticket = verify_direct_path_ticket(
        &start.ticket[..ticket_length],
        &start.server_signing_public_key,
        &DirectTicketExpectation {
            connection_id: start.connection_id,
            path_epoch: start.epoch,
            local_device_id: start.local_device_id,
            peer_device_id: start.peer_device_id,
            local_role: if local_role == PeerRole::Initiator {
                DirectTicketRole::Initiator
            } else {
                DirectTicketRole::Responder
            },
            now_ms: start.now_ms,
        },
    )
    .map_err(|_| REASON_INVALID_TICKET_V2)?;
    if ticket.ticket_id != start.ticket_id {
        return Err(REASON_INVALID_TICKET_V2);
    }
    Ok(ValidatedStart {
        local_bind,
        remote,
        stun_servers,
        timeout,
        local_role,
        local_proof_nonce: start.local_proof_nonce[..local_nonce_length].to_vec(),
        ticket: start.ticket[..ticket_length].to_vec(),
        ticket_sha256: ticket.ticket_sha256,
    })
}

fn bind_platform_socket(
    platform: u32,
    address: SocketAddr,
    network_handle: u64,
) -> Result<DirectUdpSocket, u32> {
    match platform {
        PLATFORM_WINDOWS_V2 => DirectUdpSocket::bind(address).map_err(|_| REASON_SOCKET_V2),
        PLATFORM_ANDROID_V2 => {
            #[cfg(target_os = "android")]
            {
                let binder = AndroidNetworkBinder::new(network_handle)
                    .map_err(|_| REASON_INVALID_PLATFORM_V2)?;
                DirectUdpSocket::bind_with_platform(address, &binder).map_err(|_| REASON_SOCKET_V2)
            }
            #[cfg(not(target_os = "android"))]
            {
                let _ = (address, network_handle);
                Err(REASON_INVALID_PLATFORM_V2)
            }
        }
        _ => Err(REASON_INVALID_PLATFORM_V2),
    }
}

#[allow(clippy::too_many_arguments)]
fn wait_for_signature(
    commands: &Receiver<CommandV2>,
    events: &EventQueueV2,
    cancel_epoch: &AtomicU64,
    network_changed_epoch: &AtomicU64,
    epoch: u64,
    attempt_id: [u8; 16],
    request_id: u64,
    timeout: Duration,
) -> Result<Vec<u8>, u32> {
    let deadline = Instant::now() + timeout;
    loop {
        if is_cancelled(cancel_epoch, epoch) || is_cancelled(network_changed_epoch, epoch) {
            return Err(cancellation_reason(
                cancel_epoch,
                network_changed_epoch,
                epoch,
                REASON_CANCELLED_V2,
            ));
        }
        let remaining = deadline.saturating_duration_since(Instant::now());
        if remaining.is_zero() {
            return Err(REASON_SIGNATURE_TIMEOUT_V2);
        }
        match commands.recv_timeout(remaining.min(Duration::from_millis(50))) {
            Ok(CommandV2::Signature(signature))
                if signature.abi_version == ABI_VERSION_V2
                    && signature.struct_size as usize == size_of::<TermLinkDirectSignatureV2>()
                    && signature.epoch == epoch
                    && signature.attempt_id == attempt_id
                    && signature.request_id == request_id
                    && signature.signature_length > 0
                    && signature.signature_length as usize <= MAX_SIGNATURE_BYTES_V2 =>
            {
                return Ok(signature.signature[..signature.signature_length as usize].to_vec());
            }
            Ok(CommandV2::Signature(signature)) => {
                let _ = events.push(stale_event(signature.epoch, signature.attempt_id, epoch));
            }
            Ok(CommandV2::Cancel(control))
                if control.epoch == epoch && control.attempt_id == attempt_id =>
            {
                return Err(REASON_CANCELLED_V2);
            }
            Ok(CommandV2::NetworkChanged(control))
                if control.epoch == epoch && control.attempt_id == attempt_id =>
            {
                return Err(REASON_NETWORK_CHANGED_V2);
            }
            Ok(CommandV2::Cancel(control) | CommandV2::NetworkChanged(control)) => {
                let _ = events.push(stale_event(control.epoch, control.attempt_id, epoch));
            }
            Ok(CommandV2::Start(start)) => {
                let _ = events.push(stale_event(start.epoch, start.attempt_id, epoch));
            }
            Err(RecvTimeoutError::Timeout) => {}
            Err(RecvTimeoutError::Disconnected) => return Err(REASON_CANCELLED_V2),
        }
    }
}

fn handle_idle_control(
    control: TermLinkDirectControlV2,
    reason: u32,
    events: &EventQueueV2,
    relay_only: &AtomicBool,
    epoch: &AtomicU64,
    attempt_high: &AtomicU64,
    attempt_low: &AtomicU64,
) {
    let current_epoch = epoch.load(Ordering::Acquire);
    let current_attempt = join_attempt(
        attempt_high.load(Ordering::Acquire),
        attempt_low.load(Ordering::Acquire),
    );
    if control.epoch != current_epoch || control.attempt_id != current_attempt {
        let _ = events.push(stale_event(
            control.epoch,
            control.attempt_id,
            current_epoch,
        ));
    } else {
        relay_only.store(true, Ordering::Release);
        events.force_failure(current_epoch, current_attempt, reason);
    }
}

fn decode_socket(value: TermLinkSocketAddrV2, allow_zero_port: bool) -> Result<SocketAddr, u32> {
    let ip = match value.ip_length {
        4 => IpAddr::V4(Ipv4Addr::new(
            value.ip[0],
            value.ip[1],
            value.ip[2],
            value.ip[3],
        )),
        16 => IpAddr::V6(Ipv6Addr::from(value.ip)),
        _ => return Err(REASON_INVALID_CANDIDATE_V2),
    };
    let port = u16::try_from(value.port).map_err(|_| REASON_INVALID_CANDIDATE_V2)?;
    if (!allow_zero_port && port < 1024) || ip.is_unspecified() || ip.is_loopback() {
        return Err(REASON_INVALID_CANDIDATE_V2);
    }
    Ok(SocketAddr::new(ip, port))
}

fn encode_socket(value: SocketAddr) -> TermLinkSocketAddrV2 {
    let mut encoded = TermLinkSocketAddrV2 {
        port: u32::from(value.port()),
        ..TermLinkSocketAddrV2::default()
    };
    match value.ip() {
        IpAddr::V4(ip) => {
            encoded.ip[..4].copy_from_slice(&ip.octets());
            encoded.ip_length = 4;
        }
        IpAddr::V6(ip) => {
            encoded.ip.copy_from_slice(&ip.octets());
            encoded.ip_length = 16;
        }
    }
    encoded
}

fn opposite_role(role: PeerRole) -> PeerRole {
    match role {
        PeerRole::Initiator => PeerRole::Responder,
        PeerRole::Responder => PeerRole::Initiator,
    }
}

fn state_event(epoch: u64, attempt_id: [u8; 16], state: u32) -> TermLinkDirectEventV2 {
    TermLinkDirectEventV2 {
        kind: EVENT_STATE_CHANGED_V2,
        state,
        epoch,
        attempt_id,
        ..TermLinkDirectEventV2::default()
    }
}

fn failure_event(epoch: u64, attempt_id: [u8; 16], reason: u32) -> TermLinkDirectEventV2 {
    TermLinkDirectEventV2 {
        kind: EVENT_DIRECT_FAILED_V2,
        state: STATE_RELAY_ONLY_V2,
        reason,
        epoch,
        attempt_id,
        ..TermLinkDirectEventV2::default()
    }
}

fn stale_event(
    received_epoch: u64,
    attempt_id: [u8; 16],
    current_epoch: u64,
) -> TermLinkDirectEventV2 {
    TermLinkDirectEventV2 {
        kind: EVENT_IGNORED_STALE_V2,
        epoch: current_epoch,
        request_id: received_epoch,
        attempt_id,
        ..TermLinkDirectEventV2::default()
    }
}

fn push_or_overflow(
    events: &EventQueueV2,
    event: TermLinkDirectEventV2,
    cancel_epoch: &AtomicU64,
    active_epoch: u64,
) -> Result<(), u32> {
    let epoch = event.epoch;
    let attempt = event.attempt_id;
    if events.push(event) {
        Ok(())
    } else {
        cancel_epoch.store(active_epoch, Ordering::Release);
        events.force_failure(epoch, attempt, REASON_QUEUE_FULL_V2);
        Err(REASON_QUEUE_FULL_V2)
    }
}

fn is_cancelled(target_epoch: &AtomicU64, active_epoch: u64) -> bool {
    let cancelled_through = target_epoch.load(Ordering::Acquire);
    cancelled_through != 0 && cancelled_through >= active_epoch
}

fn cancellation_reason(
    cancel_epoch: &AtomicU64,
    network_changed_epoch: &AtomicU64,
    active_epoch: u64,
    fallback: u32,
) -> u32 {
    if is_cancelled(network_changed_epoch, active_epoch) {
        REASON_NETWORK_CHANGED_V2
    } else if is_cancelled(cancel_epoch, active_epoch) {
        REASON_CANCELLED_V2
    } else {
        fallback
    }
}

fn split_attempt(attempt: [u8; 16]) -> (u64, u64) {
    (
        u64::from_ne_bytes(attempt[..8].try_into().unwrap()),
        u64::from_ne_bytes(attempt[8..].try_into().unwrap()),
    )
}

fn join_attempt(high: u64, low: u64) -> [u8; 16] {
    let mut attempt = [0_u8; 16];
    attempt[..8].copy_from_slice(&high.to_ne_bytes());
    attempt[8..].copy_from_slice(&low.to_ne_bytes());
    attempt
}

#[unsafe(no_mangle)]
pub extern "C" fn termlink_direct_abi_version_v2() -> u32 {
    ABI_VERSION_V2
}

#[unsafe(no_mangle)]
/// # Safety
/// `config` must be readable for one create record for this call only.
pub unsafe extern "C" fn termlink_direct_create_v2(
    config: *const TermLinkDirectCreateV2,
) -> *mut c_void {
    catch_unwind(AssertUnwindSafe(|| {
        if !LEGACY_ABI_V2_RUNTIME_ENABLED {
            return std::ptr::null_mut();
        }
        if config.is_null() {
            return std::ptr::null_mut();
        }
        // SAFETY: The caller supplies a readable record; it is copied immediately.
        let config = unsafe { config.read_unaligned() };
        NativeBridgeV2::create(config)
            .map(|bridge| Box::into_raw(Box::new(bridge)).cast())
            .unwrap_or(std::ptr::null_mut())
    }))
    .unwrap_or(std::ptr::null_mut())
}

#[unsafe(no_mangle)]
/// # Safety
/// `handle` must be a live v2 handle and `start` a readable record for this call.
pub unsafe extern "C" fn termlink_direct_start_v2(
    handle: *mut c_void,
    start: *const TermLinkDirectStartV2,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        if start.is_null() {
            return -2;
        }
        // SAFETY: The caller supplies a readable record; it is copied immediately.
        let start = unsafe { start.read_unaligned() };
        // SAFETY: Required by this function's contract.
        let Some(bridge) = (unsafe { bridge_v2_ref(handle) }) else {
            return -1;
        };
        if start.abi_version != ABI_VERSION_V2
            || start.struct_size as usize != size_of::<TermLinkDirectStartV2>()
            || start.epoch == 0
            || start.epoch > i64::MAX as u64
            || start.attempt_id == [0; 16]
        {
            bridge.events.force_failure(
                bridge.authoritative_epoch.load(Ordering::Acquire),
                bridge.current_attempt(),
                REASON_INVALID_ABI_V2,
            );
            return -2;
        }
        let mut current = bridge.authoritative_epoch.load(Ordering::Acquire);
        loop {
            if start.epoch <= current {
                let _ = bridge
                    .events
                    .push(stale_event(start.epoch, start.attempt_id, current));
                return 0;
            }
            match bridge.authoritative_epoch.compare_exchange_weak(
                current,
                start.epoch,
                Ordering::AcqRel,
                Ordering::Acquire,
            ) {
                Ok(_) => break,
                Err(actual) => current = actual,
            }
        }
        if current != 0 {
            bridge.cancel_epoch.store(current, Ordering::Release);
        }
        let (high, low) = split_attempt(start.attempt_id);
        bridge.current_attempt_high.store(high, Ordering::Release);
        bridge.current_attempt_low.store(low, Ordering::Release);
        bridge.submit(
            CommandV2::Start(Box::new(start)),
            start.epoch,
            start.attempt_id,
        )
    }))
    .unwrap_or(-3)
}

#[unsafe(no_mangle)]
/// # Safety
/// Pointers must be live/readable for this call and externally synchronized with destroy.
pub unsafe extern "C" fn termlink_direct_cancel_v2(
    handle: *mut c_void,
    control: *const TermLinkDirectControlV2,
) -> i32 {
    // SAFETY: Forwarded under the same caller contract.
    unsafe { submit_control(handle, control, false) }
}

#[unsafe(no_mangle)]
/// # Safety
/// Pointers must be live/readable for this call and externally synchronized with destroy.
pub unsafe extern "C" fn termlink_direct_network_changed_v2(
    handle: *mut c_void,
    control: *const TermLinkDirectControlV2,
) -> i32 {
    // SAFETY: Forwarded under the same caller contract.
    unsafe { submit_control(handle, control, true) }
}

unsafe fn submit_control(
    handle: *mut c_void,
    control: *const TermLinkDirectControlV2,
    network_change: bool,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        if control.is_null() {
            return -2;
        }
        // SAFETY: The caller supplies a readable record; it is copied immediately.
        let control = unsafe { control.read_unaligned() };
        // SAFETY: Required by this function's contract.
        let Some(bridge) = (unsafe { bridge_v2_ref(handle) }) else {
            return -1;
        };
        if control.abi_version != ABI_VERSION_V2
            || control.struct_size as usize != size_of::<TermLinkDirectControlV2>()
        {
            bridge.events.force_failure(
                bridge.authoritative_epoch.load(Ordering::Acquire),
                bridge.current_attempt(),
                REASON_INVALID_ABI_V2,
            );
            return -2;
        }
        let is_current = control.epoch == bridge.authoritative_epoch.load(Ordering::Acquire)
            && control.attempt_id == bridge.current_attempt();
        if is_current {
            bridge.cancel_epoch.store(control.epoch, Ordering::Release);
            if network_change {
                bridge
                    .network_changed_epoch
                    .store(control.epoch, Ordering::Release);
            }
            bridge.relay_only.store(true, Ordering::Release);
        }
        let command = if network_change {
            CommandV2::NetworkChanged(control)
        } else {
            CommandV2::Cancel(control)
        };
        bridge.submit(command, control.epoch, control.attempt_id)
    }))
    .unwrap_or(-3)
}

#[unsafe(no_mangle)]
/// # Safety
/// Pointers must be live/readable for this call and externally synchronized with destroy.
pub unsafe extern "C" fn termlink_direct_submit_signature_v2(
    handle: *mut c_void,
    signature: *const TermLinkDirectSignatureV2,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        if signature.is_null() {
            return -2;
        }
        // SAFETY: The caller supplies a readable record; it is copied immediately.
        let signature = unsafe { signature.read_unaligned() };
        // SAFETY: Required by this function's contract.
        let Some(bridge) = (unsafe { bridge_v2_ref(handle) }) else {
            return -1;
        };
        bridge.submit(
            CommandV2::Signature(Box::new(signature)),
            signature.epoch,
            signature.attempt_id,
        )
    }))
    .unwrap_or(-3)
}

#[unsafe(no_mangle)]
/// # Safety
/// `output` must be writable for one event and handle must be live for this call.
pub unsafe extern "C" fn termlink_direct_poll_event_v2(
    handle: *mut c_void,
    output: *mut TermLinkDirectEventV2,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        if output.is_null() {
            return -2;
        }
        // SAFETY: Required by this function's contract.
        let Some(event) =
            (unsafe { bridge_v2_ref(handle) }).and_then(|bridge| bridge.events.poll())
        else {
            return 0;
        };
        // SAFETY: The caller supplies a writable event record.
        unsafe { output.write_unaligned(event) };
        1
    }))
    .unwrap_or(-3)
}

#[unsafe(no_mangle)]
/// # Safety
/// `handle` must be a live v2 handle and must not be used after this call.
pub unsafe extern "C" fn termlink_direct_destroy_v2(handle: *mut c_void) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if !handle.is_null() {
            // SAFETY: Required by this function's contract.
            let bridge = unsafe { Box::from_raw(handle.cast::<NativeBridgeV2>()) };
            bridge.cancel_epoch.store(
                bridge.authoritative_epoch.load(Ordering::Acquire),
                Ordering::Release,
            );
            bridge.relay_only.store(true, Ordering::Release);
            // Dropping the sender wakes command/signature waits. The detached
            // worker owns all remaining data and every network operation has a
            // fixed timeout, so destroy never joins or blocks on network I/O.
            drop(bridge);
        }
    }));
}

unsafe fn bridge_v2_ref<'a>(handle: *mut c_void) -> Option<&'a NativeBridgeV2> {
    if handle.is_null() {
        None
    } else {
        // SAFETY: FFI callers must pass a live handle created by this ABI.
        Some(unsafe { &*handle.cast::<NativeBridgeV2>() })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ring::{
        rand::SystemRandom,
        signature::{Ed25519KeyPair, KeyPair},
    };

    fn create(platform: u32, capacity: u32) -> NativeBridgeV2 {
        NativeBridgeV2::create(TermLinkDirectCreateV2 {
            abi_version: ABI_VERSION_V2,
            struct_size: size_of::<TermLinkDirectCreateV2>() as u32,
            enabled: 1,
            queue_capacity: capacity,
            platform,
            reserved: [0; 3],
        })
        .unwrap()
    }

    fn control(epoch: u64, attempt_id: [u8; 16]) -> TermLinkDirectControlV2 {
        TermLinkDirectControlV2 {
            abi_version: ABI_VERSION_V2,
            struct_size: size_of::<TermLinkDirectControlV2>() as u32,
            epoch,
            attempt_id,
        }
    }

    #[test]
    fn abi_records_are_fixed_sized_and_self_describing() {
        const { assert!(!LEGACY_ABI_V2_RUNTIME_ENABLED) };
        assert_eq!(size_of::<TermLinkDirectCreateV2>(), 32);
        assert_eq!(size_of::<TermLinkSocketAddrV2>(), 24);
        assert_eq!(size_of::<TermLinkDirectControlV2>(), 32);
        assert_eq!(size_of::<TermLinkDirectSignatureV2>(), 176);
        assert_eq!(size_of::<TermLinkDirectStartV2>(), 2_592);
        assert_eq!(size_of::<TermLinkDirectEventV2>(), 2_136);
        assert_eq!(std::mem::offset_of!(TermLinkDirectStartV2, ticket), 208);
        assert_eq!(
            std::mem::offset_of!(TermLinkDirectStartV2, server_signing_public_key),
            2_256
        );
        assert_eq!(
            std::mem::offset_of!(TermLinkDirectStartV2, peer_certificate_sha256),
            2_288
        );
        assert_eq!(std::mem::offset_of!(TermLinkDirectStartV2, now_ms), 2_552);
        assert_eq!(std::mem::offset_of!(TermLinkDirectEventV2, generation), 32);
        assert_eq!(std::mem::offset_of!(TermLinkDirectEventV2, payload), 88);
    }

    #[test]
    fn invalid_abi_and_android_missing_network_handle_never_start_network() {
        let mut start = TermLinkDirectStartV2 {
            abi_version: ABI_VERSION_V2,
            struct_size: size_of::<TermLinkDirectStartV2>() as u32,
            epoch: 1,
            attempt_id: [1; 16],
            connection_id: [2; 16],
            local_device_id: [3; 16],
            peer_device_id: [4; 16],
            role: ROLE_INITIATOR_V2,
            timeout_ms: 1_000,
            ..TermLinkDirectStartV2::default()
        };
        assert!(matches!(
            validate_start(PLATFORM_ANDROID_V2, &start),
            Err(REASON_INVALID_PLATFORM_V2)
        ));
        start.abi_version = 1;
        assert!(matches!(
            validate_start(PLATFORM_WINDOWS_V2, &start),
            Err(REASON_INVALID_ABI_V2)
        ));
    }

    #[test]
    fn stale_cancel_does_not_retire_the_new_epoch() {
        let bridge = create(PLATFORM_WINDOWS_V2, 8);
        bridge.authoritative_epoch.store(8, Ordering::Release);
        let current = [8; 16];
        let (high, low) = split_attempt(current);
        bridge.current_attempt_high.store(high, Ordering::Release);
        bridge.current_attempt_low.store(low, Ordering::Release);
        bridge.relay_only.store(false, Ordering::Release);
        let stale = control(7, [7; 16]);
        assert_eq!(
            bridge.submit(CommandV2::Cancel(stale), stale.epoch, stale.attempt_id),
            0
        );
        thread::sleep(Duration::from_millis(20));
        assert!(!bridge.relay_only.load(Ordering::Acquire));
    }

    #[test]
    fn queue_overflow_publishes_authoritative_relay_only_failure() {
        let events = EventQueueV2::new(1, ABI_VERSION_V2);
        assert!(events.push(state_event(9, [9; 16], STATE_GATHERING_V2)));
        assert!(!events.push(state_event(9, [9; 16], STATE_PROBING_V2)));
        events.force_failure(9, [9; 16], REASON_QUEUE_FULL_V2);
        let event = events.poll().unwrap();
        assert_eq!(event.kind, EVENT_DIRECT_FAILED_V2);
        assert_eq!(event.state, STATE_RELAY_ONLY_V2);
        assert_eq!(event.reason, REASON_QUEUE_FULL_V2);
        assert!(event.generation >= 3);
        assert!(events.poll().is_none());
    }

    #[test]
    fn signature_epoch_request_and_attempt_are_strictly_fenced() {
        let (sender, receiver) = mpsc::sync_channel(4);
        let events = EventQueueV2::new(4, ABI_VERSION_V2);
        let cancel_epoch = AtomicU64::new(0);
        let network_epoch = AtomicU64::new(0);
        sender
            .send(CommandV2::Signature(Box::new(TermLinkDirectSignatureV2 {
                abi_version: ABI_VERSION_V2,
                struct_size: size_of::<TermLinkDirectSignatureV2>() as u32,
                epoch: 6,
                attempt_id: [6; 16],
                request_id: 99,
                signature_length: 64,
                signature: [7; MAX_SIGNATURE_BYTES_V2],
                ..TermLinkDirectSignatureV2::default()
            })))
            .unwrap();
        drop(sender);
        assert_eq!(
            wait_for_signature(
                &receiver,
                &events,
                &cancel_epoch,
                &network_epoch,
                7,
                [7; 16],
                100,
                Duration::from_millis(100),
            ),
            Err(REASON_CANCELLED_V2)
        );
        assert_eq!(events.poll().unwrap().kind, EVENT_IGNORED_STALE_V2);
    }

    #[test]
    fn network_change_cancels_immediately_and_destroy_never_joins_worker() {
        let bridge = create(PLATFORM_WINDOWS_V2, 8);
        bridge.authoritative_epoch.store(2, Ordering::Release);
        let attempt = [2; 16];
        let (high, low) = split_attempt(attempt);
        bridge.current_attempt_high.store(high, Ordering::Release);
        bridge.current_attempt_low.store(low, Ordering::Release);
        let changed = control(2, attempt);
        bridge.network_changed_epoch.store(2, Ordering::Release);
        bridge.cancel_epoch.store(2, Ordering::Release);
        assert_eq!(
            bridge.submit(
                CommandV2::NetworkChanged(changed),
                changed.epoch,
                changed.attempt_id,
            ),
            0
        );
        assert_eq!(bridge.cancel_epoch.load(Ordering::Acquire), 2);
        assert_eq!(bridge.network_changed_epoch.load(Ordering::Acquire), 2);
        let started = Instant::now();
        drop(bridge);
        assert!(started.elapsed() < Duration::from_millis(50));
    }

    #[test]
    fn ffi_v2_completes_mutual_shadow_workflow_without_relay_actions() {
        let ip = active_unicast_ip();
        let initiator_port = reserve_port(ip);
        let responder_port = reserve_port(ip);
        let initiator = create(PLATFORM_WINDOWS_V2, 32);
        let responder = create(PLATFORM_WINDOWS_V2, 32);
        let initiator_certificate = await_identity(&initiator);
        let responder_certificate = await_identity(&responder);

        let random = SystemRandom::new();
        let initiator_key =
            Ed25519KeyPair::from_pkcs8(Ed25519KeyPair::generate_pkcs8(&random).unwrap().as_ref())
                .unwrap();
        let responder_key =
            Ed25519KeyPair::from_pkcs8(Ed25519KeyPair::generate_pkcs8(&random).unwrap().as_ref())
                .unwrap();
        let initiator_public: [u8; 32] = initiator_key.public_key().as_ref().try_into().unwrap();
        let responder_public: [u8; 32] = responder_key.public_key().as_ref().try_into().unwrap();
        let ticket = base64url(
            "CAESEAARIjNEVUZ3iJmqu8zd7v8aEBAhMkNUZUdoiZqrvM3e7_AiECAxQlNkdUhpipusvc7f8AEqEDBBUmN0hUlqi5ytvs_g8QIyEEBRYnOElUprjJ2uv9Dh8gM4B0CA8MyGpjdIwJnUhqY3UgEBWhAAAQIDBAUGBwgJCgsMDQ4PYhBkaXJlY3QtdjEtdmVjdG9yakCwJTg3vrjJ8h6M6z4BR3XkogSgH3TQI4jp2mBhQTUyJWcO36MFe3MVh3Zxk-uAFShR9t2MynoQJEk4LrR53KkI",
        );
        let server_key: [u8; 32] = base64url("A6EHv_POEL4dcN0Y50vAmWfk1jCbpQ1fHdyGZBJVMbg")
            .try_into()
            .unwrap();
        let initiator_device = hex("20314253647548698a9bacbdcedff001");
        let responder_device = hex("304152637485496a8b9cadbecfe0f102");
        let connection_id = hex("4051627384954a6b8c9daebfd0e1f203");
        let attempt_id = [0x44; 16];
        let mut initiator_start = start_record(
            ip,
            initiator_port,
            responder_port,
            ROLE_INITIATOR_V2,
            attempt_id,
            connection_id,
            initiator_device,
            responder_device,
            &ticket,
            server_key,
        );
        let mut responder_start = start_record(
            ip,
            responder_port,
            initiator_port,
            ROLE_RESPONDER_V2,
            attempt_id,
            connection_id,
            responder_device,
            initiator_device,
            &ticket,
            server_key,
        );
        initiator_start.peer_certificate_sha256 =
            termlink_direct_wire::sha256(&responder_certificate);
        responder_start.peer_certificate_sha256 =
            termlink_direct_wire::sha256(&initiator_certificate);
        initiator_start.peer_device_signing_public_key = responder_public;
        responder_start.peer_device_signing_public_key = initiator_public;
        initiator_start.local_probe_nonce = [0xa1; 16];
        initiator_start.peer_probe_nonce = [0xb2; 16];
        responder_start.local_probe_nonce = initiator_start.peer_probe_nonce;
        responder_start.peer_probe_nonce = initiator_start.local_probe_nonce;
        initiator_start.local_proof_nonce = [0xc3; 32];
        initiator_start.peer_proof_nonce = [0xd4; 32];
        responder_start.local_proof_nonce = initiator_start.peer_proof_nonce;
        responder_start.peer_proof_nonce = initiator_start.local_proof_nonce;

        prime_attempt(&initiator, &initiator_start);
        prime_attempt(&responder, &responder_start);
        assert_eq!(
            initiator.submit(CommandV2::Start(Box::new(initiator_start)), 7, attempt_id,),
            0
        );
        assert_eq!(
            responder.submit(CommandV2::Start(Box::new(responder_start)), 7, attempt_id,),
            0
        );

        let deadline = Instant::now() + Duration::from_secs(8);
        let mut initiator_binding = None;
        let mut responder_binding = None;
        while (initiator_binding.is_none() || responder_binding.is_none())
            && Instant::now() < deadline
        {
            service_events(
                &initiator,
                &initiator_key,
                &mut initiator_binding,
                "initiator",
            );
            service_events(
                &responder,
                &responder_key,
                &mut responder_binding,
                "responder",
            );
            thread::yield_now();
        }
        let initiator_binding = initiator_binding.expect("initiator shadow did not become ready");
        let responder_binding = responder_binding.expect("responder shadow did not become ready");
        assert_eq!(initiator_binding, responder_binding);
        assert!(!initiator.relay_only.load(Ordering::Acquire));
        assert!(!responder.relay_only.load(Ordering::Acquire));
    }

    fn await_identity(bridge: &NativeBridgeV2) -> Vec<u8> {
        let deadline = Instant::now() + Duration::from_secs(1);
        loop {
            if let Some(event) = bridge.events.poll()
                && event.kind == EVENT_IDENTITY_READY_V2
            {
                return event.payload[..event.payload_length as usize].to_vec();
            }
            assert!(Instant::now() < deadline, "identity event timed out");
            thread::yield_now();
        }
    }

    fn prime_attempt(bridge: &NativeBridgeV2, start: &TermLinkDirectStartV2) {
        bridge
            .authoritative_epoch
            .store(start.epoch, Ordering::Release);
        let (high, low) = split_attempt(start.attempt_id);
        bridge.current_attempt_high.store(high, Ordering::Release);
        bridge.current_attempt_low.store(low, Ordering::Release);
    }

    fn service_events(
        bridge: &NativeBridgeV2,
        key: &Ed25519KeyPair,
        ready: &mut Option<[u8; 32]>,
        side: &str,
    ) {
        while let Some(event) = bridge.events.poll() {
            match event.kind {
                EVENT_SIGN_REQUEST_V2 => {
                    let signature = key.sign(&event.payload[..event.payload_length as usize]);
                    let mut response = TermLinkDirectSignatureV2 {
                        abi_version: ABI_VERSION_V2,
                        struct_size: size_of::<TermLinkDirectSignatureV2>() as u32,
                        epoch: event.epoch,
                        attempt_id: event.attempt_id,
                        request_id: event.request_id,
                        signature_length: signature.as_ref().len() as u32,
                        ..TermLinkDirectSignatureV2::default()
                    };
                    response.signature[..signature.as_ref().len()]
                        .copy_from_slice(signature.as_ref());
                    assert_eq!(
                        bridge.submit(
                            CommandV2::Signature(Box::new(response)),
                            response.epoch,
                            response.attempt_id,
                        ),
                        0
                    );
                }
                EVENT_SHADOW_READY_V2 => {
                    assert_eq!(event.payload_length, 32);
                    *ready = Some(event.payload[..32].try_into().unwrap());
                }
                EVENT_DIRECT_FAILED_V2 => {
                    panic!("{side} shadow failed with reason {}", event.reason)
                }
                _ => {}
            }
        }
    }

    #[allow(clippy::too_many_arguments)]
    fn start_record(
        ip: IpAddr,
        local_port: u16,
        remote_port: u16,
        role: u32,
        attempt_id: [u8; 16],
        connection_id: [u8; 16],
        local_device_id: [u8; 16],
        peer_device_id: [u8; 16],
        ticket: &[u8],
        server_key: [u8; 32],
    ) -> TermLinkDirectStartV2 {
        let mut start = TermLinkDirectStartV2 {
            abi_version: ABI_VERSION_V2,
            struct_size: size_of::<TermLinkDirectStartV2>() as u32,
            epoch: 7,
            attempt_id,
            connection_id,
            local_device_id,
            peer_device_id,
            role,
            timeout_ms: 5_000,
            local_bind: encode_socket(SocketAddr::new(ip, local_port)),
            remote_candidate: encode_socket(SocketAddr::new(ip, remote_port)),
            ticket_length: ticket.len() as u32,
            server_signing_public_key: server_key,
            probe_hmac_key: [0x42; 32],
            ticket_id: hex("00112233445546778899aabbccddeeff"),
            nomination_id: [0x50; 16],
            initiator_candidate_id: [0x60; 16],
            responder_candidate_id: [0x70; 16],
            local_proof_nonce_length: 16,
            peer_proof_nonce_length: 16,
            now_ms: 1_900_000_060_000,
            ..TermLinkDirectStartV2::default()
        };
        start.ticket[..ticket.len()].copy_from_slice(ticket);
        start
    }

    fn active_unicast_ip() -> IpAddr {
        let socket = std::net::UdpSocket::bind("0.0.0.0:0").unwrap();
        socket.connect("192.0.2.1:9").unwrap();
        socket.local_addr().unwrap().ip()
    }

    fn reserve_port(ip: IpAddr) -> u16 {
        std::net::UdpSocket::bind(SocketAddr::new(ip, 0))
            .unwrap()
            .local_addr()
            .unwrap()
            .port()
    }

    fn hex<const N: usize>(value: &str) -> [u8; N] {
        assert_eq!(value.len(), N * 2);
        let mut result = [0_u8; N];
        for (index, slot) in result.iter_mut().enumerate() {
            *slot = u8::from_str_radix(&value[index * 2..index * 2 + 2], 16).unwrap();
        }
        result
    }

    fn base64url(value: &str) -> Vec<u8> {
        fn digit(byte: u8) -> u8 {
            match byte {
                b'A'..=b'Z' => byte - b'A',
                b'a'..=b'z' => byte - b'a' + 26,
                b'0'..=b'9' => byte - b'0' + 52,
                b'-' => 62,
                b'_' => 63,
                _ => panic!("invalid base64url test vector"),
            }
        }
        let mut output = Vec::new();
        let mut accumulator = 0_u32;
        let mut bits = 0_u32;
        for byte in value.bytes() {
            accumulator = (accumulator << 6) | u32::from(digit(byte));
            bits += 6;
            if bits >= 8 {
                bits -= 8;
                output.push((accumulator >> bits) as u8);
                accumulator &= (1_u32 << bits).saturating_sub(1);
            }
        }
        output
    }
}
