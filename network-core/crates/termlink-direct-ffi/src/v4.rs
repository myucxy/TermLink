use ring::rand::{SecureRandom, SystemRandom};
use std::{
    collections::{HashMap, HashSet, VecDeque},
    ffi::c_void,
    mem::size_of,
    net::{IpAddr, Ipv4Addr, Ipv6Addr, SocketAddr},
    panic::{AssertUnwindSafe, catch_unwind},
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, AtomicU64, Ordering},
        mpsc::{self, Receiver, RecvTimeoutError, SyncSender, TrySendError},
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

#[cfg(target_os = "android")]
use termlink_direct_nat::AndroidNetworkBinder;
use termlink_direct_nat::{
    AuthenticatedMultipairPacket, DirectUdpSocket, MultipairCheckPair, MultipairPumpEvent,
    MultipairUdpPump, PairToken, QuicSocketHandoff, validate_candidate_endpoint,
};
use termlink_direct_quic::{
    DirectPathBindingInputs, Ed25519PeerProofVerifier, ExpectedPeer, PeerBindingContext,
    PeerProofRequest, PeerRole, ShadowEndpoint, TemporaryIdentity, direct_path_binding_sha256,
    exchange_and_authenticate_shadow_connection, shadow_echo_once, shadow_ping,
    tls_exporter_sha256,
};
use termlink_direct_wire::{
    CandidatePair, ConnectivityDatagram, DatagramKind, DirectTicketExpectation, DirectTicketRole,
    ExpectedDatagram, ProbeFields, derive_pair_probe_nonce_v2, sha256, verify_direct_path_ticket,
};

pub const ABI_VERSION_V4: u32 = 4;
pub const MAX_STUN_SERVERS_V4: usize = 2;
pub const MAX_CANDIDATES_PER_BATCH_V4: usize = 8;
pub const MAX_CANDIDATES_TOTAL_V4: usize = 32;
pub const MAX_PAIRS_PER_BATCH_V4: usize = 8;
pub const MAX_PAIRS_TOTAL_V4: usize = 32;
pub const MAX_TICKET_BYTES_V4: usize = 2_048;
pub const MAX_EVENT_BYTES_V4: usize = 2_048;
pub const MAX_SIGNATURE_BYTES_V4: usize = 128;
pub const MAX_PROOF_NONCE_BYTES_V4: usize = 32;
const MIN_PROOF_NONCE_BYTES_V4: usize = 16;
const MAX_QUEUE_CAPACITY_V4: usize = 64;
const MAX_CANDIDATE_LIFETIME_MS_V4: i64 = 120_000;

pub const PLATFORM_WINDOWS_V4: u32 = 1;
pub const PLATFORM_ANDROID_V4: u32 = 2;
pub const ROLE_INITIATOR_V4: u32 = 1;
pub const ROLE_RESPONDER_V4: u32 = 2;
pub const CANDIDATE_HOST_V4: u32 = 1;
pub const CANDIDATE_SERVER_REFLEXIVE_V4: u32 = 2;
pub const CARRIER_UDP_QUIC_V4: u32 = 1;

pub const EVENT_IDENTITY_READY_V4: u32 = 1;
pub const EVENT_STATE_CHANGED_V4: u32 = 2;
pub const EVENT_DIRECT_FAILED_V4: u32 = 3;
pub const EVENT_SIGN_REQUEST_V4: u32 = 4;
pub const EVENT_CANDIDATE_BATCH_V4: u32 = 5;
pub const EVENT_PAIR_VALIDATED_V4: u32 = 6;
pub const EVENT_CHECKS_COMPLETE_V4: u32 = 7;
pub const EVENT_NOMINATION_READY_V4: u32 = 8;
pub const EVENT_SHADOW_READY_V4: u32 = 9;
pub const EVENT_IGNORED_STALE_V4: u32 = 10;

pub const STATE_RELAY_ONLY_V4: u32 = 1;
pub const STATE_GATHERING_V4: u32 = 2;
pub const STATE_CHECKING_V4: u32 = 3;
pub const STATE_NOMINATION_PENDING_V4: u32 = 4;
pub const STATE_AUTHENTICATING_V4: u32 = 5;
pub const STATE_SHADOW_DIRECT_V4: u32 = 6;

pub const REASON_DISABLED_V4: u32 = 1;
pub const REASON_INVALID_ABI_V4: u32 = 2;
pub const REASON_INVALID_EPOCH_V4: u32 = 3;
pub const REASON_INVALID_PLATFORM_V4: u32 = 4;
pub const REASON_INVALID_CANDIDATE_V4: u32 = 5;
pub const REASON_INVALID_TICKET_V4: u32 = 6;
pub const REASON_SOCKET_V4: u32 = 7;
pub const REASON_CHECKS_EXHAUSTED_V4: u32 = 8;
pub const REASON_QUIC_V4: u32 = 9;
pub const REASON_PEER_PROOF_V4: u32 = 10;
pub const REASON_SIGNATURE_TIMEOUT_V4: u32 = 11;
pub const REASON_CANCELLED_V4: u32 = 12;
pub const REASON_NETWORK_CHANGED_V4: u32 = 13;
pub const REASON_QUEUE_FULL_V4: u32 = 14;
pub const REASON_INTERNAL_V4: u32 = 15;
pub const REASON_INVALID_PLAN_V4: u32 = 16;
pub const REASON_NOMINATION_REJECTED_V4: u32 = 17;

macro_rules! ffi_record {
    ($name:ident { $($field:ident : $ty:ty),* $(,)? }) => {
        #[repr(C, align(8))]
        #[derive(Clone, Copy, Debug, Eq, PartialEq)]
        pub struct $name { $(pub $field: $ty),* }
    };
}

ffi_record!(TermLinkDirectCreateV4 {
    abi_version: u32,
    struct_size: u32,
    enabled: u32,
    queue_capacity: u32,
    platform: u32,
    reserved: [u32; 3],
});
ffi_record!(TermLinkSocketAddrV4 {
    abi_version: u32,
    struct_size: u32,
    ip: [u8; 16],
    ip_length: u32,
    port: u32,
    reserved: [u64; 2],
});
ffi_record!(TermLinkDirectGatherV4 {
    abi_version: u32,
    struct_size: u32,
    epoch: u64,
    attempt_id: [u8; 16],
    network_handle: u64,
    local_bind: TermLinkSocketAddrV4,
    advertised_local_ip: [u8; 16],
    advertised_local_ip_length: u32,
    network_cost: u32,
    candidate_expires_at_ms: i64,
    stun_count: u32,
    gather_timeout_ms: u32,
    stun_servers: [TermLinkSocketAddrV4; MAX_STUN_SERVERS_V4],
    reserved: [u64; 4],
});
ffi_record!(TermLinkDirectCandidateV4 {
    abi_version: u32,
    struct_size: u32,
    candidate_id: [u8; 16],
    related_candidate_id: [u8; 16],
    endpoint: TermLinkSocketAddrV4,
    kind: u32,
    carrier: u32,
    priority: u32,
    network_cost: u32,
    expires_at_ms: i64,
    reserved: [u64; 2],
});
ffi_record!(TermLinkDirectCandidateBatchV4 {
    abi_version: u32,
    struct_size: u32,
    epoch: u64,
    attempt_id: [u8; 16],
    batch_seq: u32,
    candidate_count: u32,
    total_candidate_count: u32,
    gathering_complete: u32,
    candidates: [TermLinkDirectCandidateV4; MAX_CANDIDATES_PER_BATCH_V4],
    reserved: [u64; 2],
});
ffi_record!(TermLinkDirectCandidatePairV4 {
    abi_version: u32,
    struct_size: u32,
    rank: u32,
    reserved0: u32,
    initiator_candidate: TermLinkDirectCandidateV4,
    responder_candidate: TermLinkDirectCandidateV4,
    reserved: [u64; 2],
});
ffi_record!(TermLinkDirectBeginChecksV4 {
    abi_version: u32,
    struct_size: u32,
    epoch: u64,
    attempt_id: [u8; 16],
    connection_id: [u8; 16],
    local_device_id: [u8; 16],
    peer_device_id: [u8; 16],
    network_handle: u64,
    now_ms: i64,
    timeout_ms: u32,
    role: u32,
    ticket_length: u32,
    pair_batch_seq: u32,
    pair_count: u32,
    total_pair_count: u32,
    plan_complete: u32,
    reserved0: u32,
    ticket: [u8; MAX_TICKET_BYTES_V4],
    server_signing_public_key: [u8; 32],
    ticket_id: [u8; 16],
    check_round_id: [u8; 16],
    connectivity_nonce: [u8; 16],
    local_send_probe_hmac_key: [u8; 32],
    peer_receive_probe_hmac_key: [u8; 32],
    pairs: [TermLinkDirectCandidatePairV4; MAX_PAIRS_PER_BATCH_V4],
    reserved: [u64; 4],
});
ffi_record!(TermLinkDirectAppendCheckPairsV4 {
    abi_version: u32,
    struct_size: u32,
    epoch: u64,
    attempt_id: [u8; 16],
    check_round_id: [u8; 16],
    pair_batch_seq: u32,
    pair_count: u32,
    total_pair_count: u32,
    plan_complete: u32,
    pairs: [TermLinkDirectCandidatePairV4; MAX_PAIRS_PER_BATCH_V4],
    reserved: [u64; 4],
});
ffi_record!(TermLinkDirectCommitNominationV4 {
    abi_version: u32,
    struct_size: u32,
    epoch: u64,
    attempt_id: [u8; 16],
    check_round_id: [u8; 16],
    nomination_id: [u8; 16],
    winning_pair: TermLinkDirectCandidatePairV4,
    role: u32,
    timeout_ms: u32,
    selection_confirmed: u32,
    local_proof_nonce_length: u32,
    peer_certificate_sha256: [u8; 32],
    peer_device_signing_public_key: [u8; 32],
    local_proof_nonce: [u8; MAX_PROOF_NONCE_BYTES_V4],
    now_ms: i64,
    reserved: [u64; 4],
});
ffi_record!(TermLinkDirectControlV4 {
    abi_version: u32,
    struct_size: u32,
    epoch: u64,
    attempt_id: [u8; 16],
    reserved: [u64; 2],
});
ffi_record!(TermLinkDirectSignatureV4 {
    abi_version: u32,
    struct_size: u32,
    epoch: u64,
    attempt_id: [u8; 16],
    request_id: u64,
    signature_length: u32,
    reserved0: u32,
    signature: [u8; MAX_SIGNATURE_BYTES_V4],
    reserved: [u64; 2],
});
ffi_record!(TermLinkDirectOpaqueBytesV4 {
    abi_version: u32,
    struct_size: u32,
    data_length: u32,
    reserved0: u32,
    data: [u8; MAX_EVENT_BYTES_V4],
    reserved: [u64; 2],
});
ffi_record!(TermLinkDirectPairValidatedV4 {
    abi_version: u32,
    struct_size: u32,
    epoch: u64,
    attempt_id: [u8; 16],
    check_round_id: [u8; 16],
    pair: TermLinkDirectCandidatePairV4,
    round_trip_time_ms: u32,
    probe_attempts: u32,
    reserved: [u64; 2],
});
ffi_record!(TermLinkDirectChecksCompleteV4 {
    abi_version: u32,
    struct_size: u32,
    epoch: u64,
    attempt_id: [u8; 16],
    check_round_id: [u8; 16],
    total_pair_count: u32,
    validated_pair_count: u32,
    exhausted_pair_count: u32,
    result: u32,
    reserved: [u64; 2],
});
ffi_record!(TermLinkDirectNominationReadyV4 {
    abi_version: u32,
    struct_size: u32,
    epoch: u64,
    attempt_id: [u8; 16],
    check_round_id: [u8; 16],
    nomination_id: [u8; 16],
    winning_pair: TermLinkDirectCandidatePairV4,
    role: u32,
    reserved0: u32,
    reserved: [u64; 2],
});
ffi_record!(TermLinkDirectShadowReadyV4 {
    abi_version: u32,
    struct_size: u32,
    epoch: u64,
    attempt_id: [u8; 16],
    check_round_id: [u8; 16],
    nomination_id: [u8; 16],
    winning_pair: TermLinkDirectCandidatePairV4,
    direct_path_binding: [u8; 32],
    reserved: [u64; 2],
});

#[repr(C, align(8))]
#[derive(Clone, Copy)]
pub union TermLinkDirectEventPayloadV4 {
    pub opaque: TermLinkDirectOpaqueBytesV4,
    pub candidate_batch: TermLinkDirectCandidateBatchV4,
    pub pair_validated: TermLinkDirectPairValidatedV4,
    pub checks_complete: TermLinkDirectChecksCompleteV4,
    pub nomination_ready: TermLinkDirectNominationReadyV4,
    pub shadow_ready: TermLinkDirectShadowReadyV4,
    pub raw: [u8; 2_080],
}

#[repr(C, align(8))]
#[derive(Clone, Copy, Debug)]
pub struct TermLinkDirectEventV4 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub kind: u32,
    pub state: u32,
    pub reason: u32,
    pub payload_size: u32,
    pub epoch: u64,
    pub generation: u64,
    pub request_id: u64,
    pub attempt_id: [u8; 16],
    pub endpoint: TermLinkSocketAddrV4,
    pub payload: TermLinkDirectEventPayloadV4,
    pub reserved: [u64; 4],
}

impl std::fmt::Debug for TermLinkDirectEventPayloadV4 {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str("TermLinkDirectEventPayloadV4(..)")
    }
}
macro_rules! impl_zero_default {
    ($($name:ty),+ $(,)?) => {$(
        impl Default for $name {
            fn default() -> Self {
                // All ABI records define zero as their unused/reserved representation.
                unsafe { std::mem::zeroed() }
            }
        }
    )+};
}
impl_zero_default!(
    TermLinkDirectCreateV4,
    TermLinkSocketAddrV4,
    TermLinkDirectGatherV4,
    TermLinkDirectCandidateV4,
    TermLinkDirectCandidateBatchV4,
    TermLinkDirectCandidatePairV4,
    TermLinkDirectBeginChecksV4,
    TermLinkDirectAppendCheckPairsV4,
    TermLinkDirectCommitNominationV4,
    TermLinkDirectControlV4,
    TermLinkDirectSignatureV4,
    TermLinkDirectOpaqueBytesV4,
    TermLinkDirectPairValidatedV4,
    TermLinkDirectChecksCompleteV4,
    TermLinkDirectNominationReadyV4,
    TermLinkDirectShadowReadyV4,
    TermLinkDirectEventPayloadV4,
    TermLinkDirectEventV4,
);

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
struct OwnerV4 {
    epoch: u64,
    attempt_id: [u8; 16],
}
impl OwnerV4 {
    fn matches(self, epoch: u64, attempt_id: [u8; 16]) -> bool {
        self.epoch == epoch && self.attempt_id == attempt_id
    }
}

struct EventQueueV4 {
    capacity: usize,
    generation: AtomicU64,
    request_id: AtomicU64,
    queue: Mutex<VecDeque<TermLinkDirectEventV4>>,
}
impl EventQueueV4 {
    fn new(capacity: usize) -> Self {
        Self {
            capacity,
            generation: AtomicU64::new(0),
            request_id: AtomicU64::new(0),
            queue: Mutex::new(VecDeque::new()),
        }
    }
    fn next_generation(&self) -> u64 {
        self.generation.fetch_add(1, Ordering::AcqRel) + 1
    }
    fn next_request_id(&self) -> u64 {
        loop {
            let value = self.request_id.fetch_add(1, Ordering::AcqRel) + 1;
            if value != 0 {
                return value;
            }
        }
    }
    fn push(&self, mut event: TermLinkDirectEventV4) -> bool {
        let mut queue = self.queue.lock().unwrap_or_else(|e| e.into_inner());
        if queue.len() >= self.capacity {
            return false;
        }
        event.generation = self.next_generation();
        queue.push_back(event);
        true
    }
    fn force_failure(&self, epoch: u64, attempt_id: [u8; 16], reason: u32) {
        let mut queue = self.queue.lock().unwrap_or_else(|e| e.into_inner());
        if queue.iter().any(|event| {
            event.epoch == epoch
                && event.attempt_id == attempt_id
                && event.kind == EVENT_DIRECT_FAILED_V4
        }) {
            return;
        }
        let mut event = base_event(
            EVENT_DIRECT_FAILED_V4,
            STATE_RELAY_ONLY_V4,
            epoch,
            attempt_id,
        );
        event.reason = reason;
        if queue.len() < self.capacity {
            event.generation = self.next_generation();
            queue.push_back(event);
        } else if let Some((index, generation)) = queue
            .iter()
            .enumerate()
            .rev()
            .find(|(_, queued)| queued.epoch == epoch && queued.attempt_id == attempt_id)
            .map(|(index, queued)| (index, queued.generation))
        {
            event.generation = generation;
            queue[index] = event;
        } else if let Some(queued) = queue.back_mut() {
            event.generation = queued.generation;
            *queued = event;
        }
    }

    fn retire_predecessor_events(&self, current: OwnerV4) {
        let mut queue = self.queue.lock().unwrap_or_else(|e| e.into_inner());
        for queued in queue.iter_mut() {
            if queued.kind == EVENT_IGNORED_STALE_V4
                || current.matches(queued.epoch, queued.attempt_id)
            {
                continue;
            }
            let generation = queued.generation;
            let received = OwnerV4 {
                epoch: queued.epoch,
                attempt_id: queued.attempt_id,
            };
            let mut stale = base_event(
                EVENT_IGNORED_STALE_V4,
                STATE_RELAY_ONLY_V4,
                received.epoch,
                received.attempt_id,
            );
            stale.generation = generation;
            stale.request_id = current.epoch;
            *queued = stale;
        }
    }

    fn push_stale(&self, event: TermLinkDirectEventV4) -> bool {
        let mut queue = self.queue.lock().unwrap_or_else(|e| e.into_inner());
        if queue.len().saturating_add(2) >= self.capacity
            || queue
                .iter()
                .any(|queued| queued.kind == EVENT_IGNORED_STALE_V4)
        {
            return false;
        }
        let mut event = event;
        event.generation = self.next_generation();
        queue.push_back(event);
        true
    }
    fn poll(&self) -> Option<TermLinkDirectEventV4> {
        self.queue
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .pop_front()
    }
}

enum CommandV4 {
    Gather(Box<TermLinkDirectGatherV4>),
    Begin(Box<TermLinkDirectBeginChecksV4>),
    Append(Box<TermLinkDirectAppendCheckPairsV4>),
    Commit(Box<TermLinkDirectCommitNominationV4>),
    Cancel(TermLinkDirectControlV4),
    NetworkChanged(TermLinkDirectControlV4),
    #[cfg(test)]
    AuthenticateForTest {
        begin: Box<TermLinkDirectBeginChecksV4>,
        identity: TemporaryIdentity,
        commit: Box<TermLinkDirectCommitNominationV4>,
        handoff: QuicSocketHandoff,
        completion: SyncSender<u32>,
    },
    #[cfg(test)]
    WaitForSignatureForTest {
        expected: OwnerV4,
        request_id: u64,
        entered: SyncSender<()>,
        completion: SyncSender<u32>,
    },
}

struct RetainedV4 {
    owner: OwnerV4,
    network_handle: u64,
    identity: TemporaryIdentity,
    pump: MultipairUdpPump,
    candidates: Vec<TermLinkDirectCandidateV4>,
    next_candidate_batch_seq: u32,
    gather_deadline: Instant,
    gathering_complete: bool,
    network_cost: u32,
    candidate_expires_at_ms: i64,
}

struct CheckV4 {
    owner: OwnerV4,
    begin: TermLinkDirectBeginChecksV4,
    identity: TemporaryIdentity,
    local_candidates: Vec<TermLinkDirectCandidateV4>,
    next_candidate_batch_seq: u32,
    gather_deadline: Instant,
    gathering_complete: bool,
    network_cost: u32,
    candidate_expires_at_ms: i64,
    ticket_expires_at_ms: i64,
    pump: MultipairUdpPump,
    pairs: HashMap<PairToken, TermLinkDirectCandidatePairV4>,
    pair_keys: HashSet<([u8; 16], [u8; 16])>,
    validated: HashSet<PairToken>,
    next_batch_seq: u32,
    total_pair_count: u32,
    plan_complete: bool,
    nomination_committed: bool,
}

pub struct NativeBridgeV4 {
    config: TermLinkDirectCreateV4,
    commands: Option<SyncSender<CommandV4>>,
    signatures: Option<SyncSender<Box<TermLinkDirectSignatureV4>>>,
    events: Arc<EventQueueV4>,
    owner: Arc<Mutex<OwnerV4>>,
    cancel_epoch: Arc<AtomicU64>,
    network_changed_epoch: Arc<AtomicU64>,
    relay_only: Arc<AtomicBool>,
    worker: Option<JoinHandle<()>>,
    #[cfg_attr(not(test), allow(dead_code))]
    worker_exited: Arc<AtomicBool>,
}

struct WorkerExitV4(Arc<AtomicBool>);
impl Drop for WorkerExitV4 {
    fn drop(&mut self) {
        self.0.store(true, Ordering::Release);
    }
}

impl NativeBridgeV4 {
    fn create(config: TermLinkDirectCreateV4) -> Result<Self, ()> {
        if config.abi_version != ABI_VERSION_V4
            || config.struct_size as usize != size_of::<TermLinkDirectCreateV4>()
            || config.enabled > 1
            || !matches!(config.platform, PLATFORM_WINDOWS_V4 | PLATFORM_ANDROID_V4)
            || config.queue_capacity == 0
            || config.queue_capacity as usize > MAX_QUEUE_CAPACITY_V4
            || config.reserved != [0; 3]
        {
            return Err(());
        }
        let capacity = config.queue_capacity as usize;
        let (commands, command_rx) = mpsc::sync_channel(capacity);
        let (signatures, signature_rx) = mpsc::sync_channel(capacity);
        let events = Arc::new(EventQueueV4::new(capacity));
        let owner = Arc::new(Mutex::new(OwnerV4::default()));
        let cancel_epoch = Arc::new(AtomicU64::new(0));
        let network_changed_epoch = Arc::new(AtomicU64::new(0));
        let relay_only = Arc::new(AtomicBool::new(true));
        let worker_exited = Arc::new(AtomicBool::new(false));
        let worker = {
            let events = Arc::clone(&events);
            let owner = Arc::clone(&owner);
            let cancel_epoch = Arc::clone(&cancel_epoch);
            let network_changed_epoch = Arc::clone(&network_changed_epoch);
            let relay_only = Arc::clone(&relay_only);
            let worker_exited = Arc::clone(&worker_exited);
            thread::Builder::new()
                .name("termlink-direct-v4-shadow".into())
                .spawn(move || {
                    let _exit = WorkerExitV4(worker_exited);
                    worker_loop_v4(
                        config,
                        command_rx,
                        signature_rx,
                        events,
                        owner,
                        cancel_epoch,
                        network_changed_epoch,
                        relay_only,
                    )
                })
                .map_err(|_| ())?
        };
        Ok(Self {
            config,
            commands: Some(commands),
            signatures: Some(signatures),
            events,
            owner,
            cancel_epoch,
            network_changed_epoch,
            relay_only,
            worker: Some(worker),
            worker_exited,
        })
    }

    fn owner(&self) -> OwnerV4 {
        *self.owner.lock().unwrap_or_else(|e| e.into_inner())
    }
    fn stale(&self, epoch: u64, attempt_id: [u8; 16]) {
        let current = self.owner().epoch;
        let mut event = base_event(
            EVENT_IGNORED_STALE_V4,
            STATE_RELAY_ONLY_V4,
            epoch,
            attempt_id,
        );
        event.request_id = current;
        let _ = self.events.push_stale(event);
    }
    fn submit(&self, command: CommandV4, epoch: u64, attempt_id: [u8; 16]) -> i32 {
        match self
            .commands
            .as_ref()
            .expect("live bridge")
            .try_send(command)
        {
            Ok(()) => 0,
            Err(TrySendError::Full(_)) => {
                let expected = OwnerV4 { epoch, attempt_id };
                let owner = self.owner.lock().unwrap_or_else(|e| e.into_inner());
                if *owner == expected {
                    self.cancel_epoch.fetch_max(epoch, Ordering::AcqRel);
                    self.events
                        .force_failure(epoch, attempt_id, REASON_QUEUE_FULL_V4);
                }
                -4
            }
            Err(TrySendError::Disconnected(_)) => {
                let expected = OwnerV4 { epoch, attempt_id };
                let owner = self.owner.lock().unwrap_or_else(|e| e.into_inner());
                if *owner == expected {
                    self.events
                        .force_failure(epoch, attempt_id, REASON_INTERNAL_V4);
                }
                -1
            }
        }
    }
    fn accept_gather(&self, value: TermLinkDirectGatherV4) -> i32 {
        if validate_gather(self.config, &value).is_err() {
            return -2;
        }
        let mut owner = self.owner.lock().unwrap_or_else(|e| e.into_inner());
        if value.epoch <= owner.epoch {
            let current = owner.epoch;
            drop(owner);
            self.stale(value.epoch, value.attempt_id);
            let _ = current;
            return 0;
        }
        if owner.epoch != 0 {
            self.cancel_epoch.fetch_max(owner.epoch, Ordering::AcqRel);
        }
        *owner = OwnerV4 {
            epoch: value.epoch,
            attempt_id: value.attempt_id,
        };
        self.events.retire_predecessor_events(*owner);
        drop(owner);
        self.relay_only.store(true, Ordering::Release);
        self.submit(
            CommandV4::Gather(Box::new(value)),
            value.epoch,
            value.attempt_id,
        )
    }
    fn accept_current(&self, epoch: u64, attempt_id: [u8; 16]) -> bool {
        if self.owner().matches(epoch, attempt_id) {
            true
        } else {
            self.stale(epoch, attempt_id);
            false
        }
    }
    fn accept_control(&self, value: TermLinkDirectControlV4, network: bool) -> i32 {
        if !valid_header(&value, value.abi_version, value.struct_size)
            || value.reserved != [0; 2]
            || value.epoch == 0
            || value.attempt_id == [0; 16]
        {
            return -2;
        }
        let owner = self.owner.lock().unwrap_or_else(|e| e.into_inner());
        if !owner.matches(value.epoch, value.attempt_id) {
            let current = owner.epoch;
            drop(owner);
            let mut event = base_event(
                EVENT_IGNORED_STALE_V4,
                STATE_RELAY_ONLY_V4,
                value.epoch,
                value.attempt_id,
            );
            event.request_id = current;
            let _ = self.events.push_stale(event);
            return 0;
        }
        self.cancel_epoch.fetch_max(value.epoch, Ordering::AcqRel);
        if network {
            self.network_changed_epoch
                .fetch_max(value.epoch, Ordering::AcqRel);
        }
        self.relay_only.store(true, Ordering::Release);
        self.events.force_failure(
            value.epoch,
            value.attempt_id,
            if network {
                REASON_NETWORK_CHANGED_V4
            } else {
                REASON_CANCELLED_V4
            },
        );
        drop(owner);
        self.submit(
            if network {
                CommandV4::NetworkChanged(value)
            } else {
                CommandV4::Cancel(value)
            },
            value.epoch,
            value.attempt_id,
        )
    }
}

impl Drop for NativeBridgeV4 {
    fn drop(&mut self) {
        let owner = self.owner();
        if owner.epoch != 0 {
            self.cancel_epoch.fetch_max(owner.epoch, Ordering::AcqRel);
        }
        self.relay_only.store(true, Ordering::Release);
        self.commands.take();
        self.signatures.take();
        if let Some(worker) = self.worker.take() {
            debug_assert_ne!(worker.thread().id(), thread::current().id());
            let _ = worker.join();
        }
    }
}

fn valid_header<T>(_: &T, version: u32, struct_size: u32) -> bool {
    version == ABI_VERSION_V4 && struct_size as usize == size_of::<T>()
}
fn current_owner(owner: &Mutex<OwnerV4>) -> OwnerV4 {
    *owner.lock().unwrap_or_else(|e| e.into_inner())
}
fn is_current(owner: &Mutex<OwnerV4>, expected: OwnerV4) -> bool {
    current_owner(owner) == expected
}
fn force_failure_if_current(
    events: &EventQueueV4,
    owner: &Mutex<OwnerV4>,
    expected: OwnerV4,
    reason: u32,
) {
    let current = owner.lock().unwrap_or_else(|e| e.into_inner());
    if *current == expected {
        events.force_failure(expected.epoch, expected.attempt_id, reason);
    }
}
fn push_if_active(
    events: &EventQueueV4,
    owner: &Mutex<OwnerV4>,
    cancel: &AtomicU64,
    expected: OwnerV4,
    event: TermLinkDirectEventV4,
) -> bool {
    let current = owner.lock().unwrap_or_else(|e| e.into_inner());
    if *current != expected || cancel.load(Ordering::Acquire) >= expected.epoch {
        return false;
    }
    events.push(event)
}
fn cancelled(
    cancel: &AtomicU64,
    network: &AtomicU64,
    owner: &Mutex<OwnerV4>,
    expected: OwnerV4,
) -> bool {
    !is_current(owner, expected)
        || cancel.load(Ordering::Acquire) >= expected.epoch
        || network.load(Ordering::Acquire) >= expected.epoch
}

#[allow(clippy::too_many_arguments)]
fn worker_loop_v4(
    config: TermLinkDirectCreateV4,
    receiver: Receiver<CommandV4>,
    signatures: Receiver<Box<TermLinkDirectSignatureV4>>,
    events: Arc<EventQueueV4>,
    owner: Arc<Mutex<OwnerV4>>,
    cancel: Arc<AtomicU64>,
    network: Arc<AtomicU64>,
    relay_only: Arc<AtomicBool>,
) {
    // This worker is the sole owner of the optional UDP socket across gather, checks and QUIC.
    let mut retained: Option<RetainedV4> = None;
    let mut checks: Option<CheckV4> = None;
    loop {
        let command = if checks.is_some() || retained.is_some() {
            match receiver.recv_timeout(Duration::from_millis(5)) {
                Ok(value) => Some(value),
                Err(RecvTimeoutError::Timeout) => None,
                Err(RecvTimeoutError::Disconnected) => break,
            }
        } else {
            match receiver.recv() {
                Ok(value) => Some(value),
                Err(_) => break,
            }
        };
        if let Some(command) = command {
            match command {
                CommandV4::Gather(value) => {
                    let command_owner = OwnerV4 {
                        epoch: value.epoch,
                        attempt_id: value.attempt_id,
                    };
                    if !is_current(&owner, command_owner) {
                        publish_stale_worker(&events, command_owner, current_owner(&owner).epoch);
                        continue;
                    }
                    if cancelled(&cancel, &network, &owner, command_owner) {
                        continue;
                    }
                    checks.take();
                    retained.take();
                    let gather_owner = command_owner;
                    match run_gather_v4(config, *value, &events, &owner, &cancel) {
                        Ok(value) => {
                            if !cancelled(&cancel, &network, &owner, value.owner) {
                                retained = Some(value);
                            } else if !is_current(&owner, value.owner) {
                                publish_stale_worker(
                                    &events,
                                    value.owner,
                                    current_owner(&owner).epoch,
                                );
                            }
                        }
                        Err(reason) => {
                            force_failure_if_current(&events, &owner, gather_owner, reason)
                        }
                    }
                }
                CommandV4::Begin(value) => {
                    let expected = OwnerV4 {
                        epoch: value.epoch,
                        attempt_id: value.attempt_id,
                    };
                    if !is_current(&owner, expected) {
                        publish_stale_worker(&events, expected, current_owner(&owner).epoch);
                        continue;
                    }
                    if cancelled(&cancel, &network, &owner, expected) {
                        continue;
                    }
                    let Some(current_ref) = retained.as_ref() else {
                        force_failure_if_current(
                            &events,
                            &owner,
                            expected,
                            REASON_INVALID_EPOCH_V4,
                        );
                        continue;
                    };
                    if current_ref.owner != expected {
                        force_failure_if_current(
                            &events,
                            &owner,
                            expected,
                            REASON_INVALID_EPOCH_V4,
                        );
                        continue;
                    }
                    let current = retained.take().expect("retained owner checked above");
                    match begin_checks_v4(config, *value, current) {
                        Ok(value) => {
                            if !push_if_active(
                                &events,
                                &owner,
                                &cancel,
                                expected,
                                base_event(
                                    EVENT_STATE_CHANGED_V4,
                                    STATE_CHECKING_V4,
                                    expected.epoch,
                                    expected.attempt_id,
                                ),
                            ) {
                                force_failure_if_current(
                                    &events,
                                    &owner,
                                    expected,
                                    REASON_QUEUE_FULL_V4,
                                );
                            } else {
                                checks = Some(value);
                            }
                        }
                        Err((reason, _current)) => {
                            // A published DIRECT_FAILED event is terminal for
                            // this owner; do not allow a later command in the
                            // same epoch to resurrect the retained socket.
                            force_failure_if_current(&events, &owner, expected, reason);
                        }
                    }
                }
                CommandV4::Append(value) => {
                    let expected = OwnerV4 {
                        epoch: value.epoch,
                        attempt_id: value.attempt_id,
                    };
                    if !is_current(&owner, expected) {
                        publish_stale_worker(&events, expected, current_owner(&owner).epoch);
                    } else if cancelled(&cancel, &network, &owner, expected) {
                        continue;
                    } else if let Some(check) = checks.as_mut() {
                        if let Err(reason) = append_pairs_v4(check, &value) {
                            force_failure_if_current(&events, &owner, expected, reason);
                            checks.take();
                        }
                    } else {
                        force_failure_if_current(&events, &owner, expected, REASON_INVALID_PLAN_V4);
                    }
                }
                CommandV4::Commit(value) => {
                    let command_owner = OwnerV4 {
                        epoch: value.epoch,
                        attempt_id: value.attempt_id,
                    };
                    if !is_current(&owner, command_owner) {
                        publish_stale_worker(&events, command_owner, current_owner(&owner).epoch);
                    } else if cancelled(&cancel, &network, &owner, command_owner) {
                        continue;
                    } else if let Some(check) = checks.take() {
                        let expected = check.owner;
                        match commit_v4(
                            config,
                            *value,
                            check,
                            &signatures,
                            &events,
                            &cancel,
                            &network,
                            &owner,
                            &relay_only,
                        ) {
                            Ok(()) => {}
                            Err((reason, _returned)) => {
                                force_failure_if_current(&events, &owner, expected, reason);
                            }
                        }
                    } else {
                        force_failure_if_current(
                            &events,
                            &owner,
                            command_owner,
                            REASON_NOMINATION_REJECTED_V4,
                        );
                    }
                }
                #[cfg(test)]
                CommandV4::AuthenticateForTest {
                    begin,
                    identity,
                    commit,
                    handoff,
                    completion,
                } => {
                    let expected = OwnerV4 {
                        epoch: begin.epoch,
                        attempt_id: begin.attempt_id,
                    };
                    if !is_current(&owner, expected) {
                        publish_stale_worker(&events, expected, current_owner(&owner).epoch);
                    } else if !cancelled(&cancel, &network, &owner, expected) {
                        checks.take();
                        retained.take();
                        let reason = authenticate_quic_v4(
                            config,
                            *begin,
                            identity,
                            &commit,
                            handoff,
                            &signatures,
                            &events,
                            &cancel,
                            &network,
                            &owner,
                            &relay_only,
                        )
                        .err()
                        .unwrap_or(0);
                        if reason != 0 {
                            force_failure_if_current(&events, &owner, expected, reason);
                        }
                        let _ = completion.send(reason);
                    }
                }
                #[cfg(test)]
                CommandV4::WaitForSignatureForTest {
                    expected,
                    request_id,
                    entered,
                    completion,
                } => {
                    let _ = entered.send(());
                    let reason = wait_signature_v4(
                        &signatures,
                        request_id,
                        expected,
                        Duration::from_secs(5),
                        &cancel,
                        &network,
                        &owner,
                    )
                    .err()
                    .unwrap_or(0);
                    let _ = completion.send(reason);
                }
                CommandV4::Cancel(value) | CommandV4::NetworkChanged(value) => {
                    if retained
                        .as_ref()
                        .is_some_and(|v| v.owner.matches(value.epoch, value.attempt_id))
                    {
                        retained.take();
                    }
                    if checks
                        .as_ref()
                        .is_some_and(|v| v.owner.matches(value.epoch, value.attempt_id))
                    {
                        checks.take();
                    }
                }
            }
        }
        if let Some(gathering) = retained.as_mut() {
            if cancelled(&cancel, &network, &owner, gathering.owner) {
                retained.take();
                continue;
            }
            match gathering.pump.step(Duration::from_millis(10), |_, _| None) {
                Ok(pump_events) => {
                    for pump_event in pump_events {
                        if !gathering.gathering_complete
                            && let MultipairPumpEvent::ServerReflexiveCandidate(endpoint) =
                                pump_event
                            && !gathering
                                .candidates
                                .iter()
                                .any(|candidate| candidate.endpoint == encode_socket(endpoint))
                        {
                            let related = gathering.candidates[0].candidate_id;
                            match candidate(
                                endpoint,
                                CANDIDATE_SERVER_REFLEXIVE_V4,
                                related,
                                gathering.network_cost,
                                gathering.candidate_expires_at_ms,
                            ) {
                                Ok(candidate) => {
                                    gathering.candidates.push(candidate);
                                    let sequence = gathering.next_candidate_batch_seq;
                                    gathering.next_candidate_batch_seq += 1;
                                    if publish_candidate_batch(
                                        &events,
                                        &owner,
                                        &cancel,
                                        gathering.owner,
                                        sequence,
                                        &[candidate],
                                        gathering.candidates.len() as u32,
                                        false,
                                    )
                                    .is_err()
                                    {
                                        let expected = gathering.owner;
                                        retained.take();
                                        force_failure_if_current(
                                            &events,
                                            &owner,
                                            expected,
                                            REASON_QUEUE_FULL_V4,
                                        );
                                        break;
                                    }
                                }
                                Err(reason) => {
                                    let expected = gathering.owner;
                                    retained.take();
                                    force_failure_if_current(&events, &owner, expected, reason);
                                    break;
                                }
                            }
                        }
                    }
                }
                Err(_) => {
                    let expected = gathering.owner;
                    retained.take();
                    force_failure_if_current(&events, &owner, expected, REASON_SOCKET_V4);
                }
            }
            if let Some(gathering) = retained.as_mut()
                && !gathering.gathering_complete
                && Instant::now() >= gathering.gather_deadline
            {
                let sequence = gathering.next_candidate_batch_seq;
                gathering.next_candidate_batch_seq += 1;
                gathering.gathering_complete = true;
                if publish_candidate_batch(
                    &events,
                    &owner,
                    &cancel,
                    gathering.owner,
                    sequence,
                    &[],
                    gathering.candidates.len() as u32,
                    true,
                )
                .is_err()
                {
                    let expected = gathering.owner;
                    retained.take();
                    force_failure_if_current(&events, &owner, expected, REASON_QUEUE_FULL_V4);
                }
            }
        }
        if let Some(check) = checks.as_mut() {
            if cancelled(&cancel, &network, &owner, check.owner) {
                checks.take();
                continue;
            }
            let now_ms = check.begin.now_ms;
            let begin = check.begin;
            let pairs = check.pairs.clone();
            let events_from_pump = check
                .pump
                .step(Duration::from_millis(10), |source, packet| {
                    authenticate_packet(&begin, &pairs, source, packet, now_ms)
                });
            match events_from_pump {
                Ok(pump_events) => {
                    for event in pump_events {
                        match event {
                            MultipairPumpEvent::ServerReflexiveCandidate(endpoint) => {
                                if !check.gathering_complete
                                    && check.local_candidates.len() < MAX_CANDIDATES_TOTAL_V4
                                    && !check.local_candidates.iter().any(|candidate| {
                                        candidate.endpoint == encode_socket(endpoint)
                                    })
                                {
                                    let related = check.local_candidates[0].candidate_id;
                                    match candidate(
                                        endpoint,
                                        CANDIDATE_SERVER_REFLEXIVE_V4,
                                        related,
                                        check.network_cost,
                                        check
                                            .candidate_expires_at_ms
                                            .min(check.ticket_expires_at_ms),
                                    ) {
                                        Ok(candidate) => {
                                            check.local_candidates.push(candidate);
                                            let sequence = check.next_candidate_batch_seq;
                                            check.next_candidate_batch_seq += 1;
                                            if publish_candidate_batch(
                                                &events,
                                                &owner,
                                                &cancel,
                                                check.owner,
                                                sequence,
                                                &[candidate],
                                                check.local_candidates.len() as u32,
                                                false,
                                            )
                                            .is_err()
                                            {
                                                let expected = check.owner;
                                                checks.take();
                                                force_failure_if_current(
                                                    &events,
                                                    &owner,
                                                    expected,
                                                    REASON_QUEUE_FULL_V4,
                                                );
                                                break;
                                            }
                                        }
                                        Err(reason) => {
                                            let expected = check.owner;
                                            checks.take();
                                            force_failure_if_current(
                                                &events, &owner, expected, reason,
                                            );
                                            break;
                                        }
                                    }
                                }
                            }
                            MultipairPumpEvent::PairValidated {
                                token,
                                round_trip_time,
                                probe_attempts,
                                ..
                            } => {
                                if let Some(pair) = check.pairs.get(&token).copied() {
                                    check.validated.insert(token);
                                    let payload = TermLinkDirectPairValidatedV4 {
                                        abi_version: ABI_VERSION_V4,
                                        struct_size: size_of::<TermLinkDirectPairValidatedV4>()
                                            as u32,
                                        epoch: check.owner.epoch,
                                        attempt_id: check.owner.attempt_id,
                                        check_round_id: check.begin.check_round_id,
                                        pair,
                                        round_trip_time_ms: round_trip_time
                                            .as_millis()
                                            .min(u128::from(u32::MAX))
                                            as u32,
                                        probe_attempts: probe_attempts.clamp(1, 4) as u32,
                                        reserved: [0; 2],
                                    };
                                    let mut out = base_event(
                                        EVENT_PAIR_VALIDATED_V4,
                                        STATE_CHECKING_V4,
                                        check.owner.epoch,
                                        check.owner.attempt_id,
                                    );
                                    out.payload_size =
                                        size_of::<TermLinkDirectPairValidatedV4>() as u32;
                                    out.payload = TermLinkDirectEventPayloadV4 { raw: [0; 2_080] };
                                    out.payload.pair_validated = payload;
                                    if !push_if_active(&events, &owner, &cancel, check.owner, out) {
                                        let expected = check.owner;
                                        checks.take();
                                        force_failure_if_current(
                                            &events,
                                            &owner,
                                            expected,
                                            REASON_QUEUE_FULL_V4,
                                        );
                                        break;
                                    }
                                }
                            }
                            MultipairPumpEvent::ChecksComplete => {
                                let validated = check.validated.len() as u32;
                                let payload = TermLinkDirectChecksCompleteV4 {
                                    abi_version: ABI_VERSION_V4,
                                    struct_size: size_of::<TermLinkDirectChecksCompleteV4>() as u32,
                                    epoch: check.owner.epoch,
                                    attempt_id: check.owner.attempt_id,
                                    check_round_id: check.begin.check_round_id,
                                    total_pair_count: check.total_pair_count,
                                    validated_pair_count: validated,
                                    exhausted_pair_count: check
                                        .total_pair_count
                                        .saturating_sub(validated),
                                    result: u32::from(validated != 0),
                                    reserved: [0; 2],
                                };
                                let mut out = base_event(
                                    EVENT_CHECKS_COMPLETE_V4,
                                    STATE_NOMINATION_PENDING_V4,
                                    check.owner.epoch,
                                    check.owner.attempt_id,
                                );
                                out.payload_size =
                                    size_of::<TermLinkDirectChecksCompleteV4>() as u32;
                                out.payload = TermLinkDirectEventPayloadV4 { raw: [0; 2_080] };
                                out.payload.checks_complete = payload;
                                if !push_if_active(&events, &owner, &cancel, check.owner, out) {
                                    let expected = check.owner;
                                    checks.take();
                                    force_failure_if_current(
                                        &events,
                                        &owner,
                                        expected,
                                        REASON_QUEUE_FULL_V4,
                                    );
                                    break;
                                }
                            }
                        }
                    }
                }
                Err(_) => {
                    let expected = check.owner;
                    checks.take();
                    force_failure_if_current(&events, &owner, expected, REASON_SOCKET_V4);
                }
            }
            if let Some(check) = checks.as_mut()
                && !check.gathering_complete
                && Instant::now() >= check.gather_deadline
            {
                let sequence = check.next_candidate_batch_seq;
                check.next_candidate_batch_seq += 1;
                check.gathering_complete = true;
                if publish_candidate_batch(
                    &events,
                    &owner,
                    &cancel,
                    check.owner,
                    sequence,
                    &[],
                    check.local_candidates.len() as u32,
                    true,
                )
                .is_err()
                {
                    let expected = check.owner;
                    checks.take();
                    force_failure_if_current(&events, &owner, expected, REASON_QUEUE_FULL_V4);
                }
            }
        }
    }
}

fn publish_stale_worker(events: &EventQueueV4, received: OwnerV4, current_epoch: u64) {
    let mut event = base_event(
        EVENT_IGNORED_STALE_V4,
        STATE_RELAY_ONLY_V4,
        received.epoch,
        received.attempt_id,
    );
    event.request_id = current_epoch;
    let _ = events.push_stale(event);
}

fn validate_gather(
    config: TermLinkDirectCreateV4,
    value: &TermLinkDirectGatherV4,
) -> Result<(), u32> {
    if !valid_header(value, value.abi_version, value.struct_size)
        || value.epoch == 0
        || value.epoch > i64::MAX as u64
        || value.attempt_id == [0; 16]
        || value.reserved != [0; 4]
        || value.stun_count as usize > MAX_STUN_SERVERS_V4
        || !(100..=5_000).contains(&value.gather_timeout_ms)
        || value.network_cost > 999
        || value.candidate_expires_at_ms <= 0
    {
        return Err(REASON_INVALID_ABI_V4);
    }
    if (config.platform == PLATFORM_ANDROID_V4 && value.network_handle == 0)
        || (config.platform == PLATFORM_WINDOWS_V4 && value.network_handle != 0)
    {
        return Err(REASON_INVALID_PLATFORM_V4);
    }
    let bind = decode_socket(value.local_bind, true)?;
    let advertised = decode_ip(value.advertised_local_ip, value.advertised_local_ip_length)?;
    if bind.is_ipv4() != advertised.is_ipv4() {
        return Err(REASON_INVALID_CANDIDATE_V4);
    }
    for server in &value.stun_servers[..value.stun_count as usize] {
        decode_socket(*server, false)?;
    }
    if value.stun_servers[value.stun_count as usize..]
        .iter()
        .any(|v| *v != TermLinkSocketAddrV4::default())
    {
        return Err(REASON_INVALID_ABI_V4);
    }
    Ok(())
}

fn run_gather_v4(
    config: TermLinkDirectCreateV4,
    gather: TermLinkDirectGatherV4,
    events: &EventQueueV4,
    owner: &Mutex<OwnerV4>,
    cancel: &AtomicU64,
) -> Result<RetainedV4, u32> {
    if config.enabled == 0 {
        return Err(REASON_DISABLED_V4);
    }
    let expected = OwnerV4 {
        epoch: gather.epoch,
        attempt_id: gather.attempt_id,
    };
    let identity = TemporaryIdentity::generate().map_err(|_| REASON_INTERNAL_V4)?;
    let certificate = identity.certificate_der();
    if certificate.len() > MAX_EVENT_BYTES_V4 {
        return Err(REASON_INTERNAL_V4);
    }
    let mut identity_payload = TermLinkDirectOpaqueBytesV4 {
        abi_version: ABI_VERSION_V4,
        struct_size: size_of::<TermLinkDirectOpaqueBytesV4>() as u32,
        data_length: certificate.len() as u32,
        ..TermLinkDirectOpaqueBytesV4::default()
    };
    identity_payload.data[..certificate.len()].copy_from_slice(certificate.as_ref());
    let mut identity_event = base_event(
        EVENT_IDENTITY_READY_V4,
        STATE_GATHERING_V4,
        gather.epoch,
        gather.attempt_id,
    );
    identity_event.payload_size = size_of::<TermLinkDirectOpaqueBytesV4>() as u32;
    identity_event.payload = TermLinkDirectEventPayloadV4 { raw: [0; 2_080] };
    identity_event.payload.opaque = identity_payload;
    if !push_if_active(events, owner, cancel, expected, identity_event) {
        return Err(REASON_QUEUE_FULL_V4);
    }
    let bind = decode_socket(gather.local_bind, true)?;
    let socket = bind_socket(config.platform, bind, gather.network_handle)?;
    let advertised = decode_ip(
        gather.advertised_local_ip,
        gather.advertised_local_ip_length,
    )?;
    let host_endpoint = socket
        .host_candidate(advertised)
        .map_err(|_| REASON_INVALID_CANDIDATE_V4)?
        .endpoint;
    let host = candidate(
        host_endpoint,
        CANDIDATE_HOST_V4,
        [0; 16],
        gather.network_cost,
        gather.candidate_expires_at_ms,
    )?;
    publish_candidate_batch(events, owner, cancel, expected, 1, &[host], 1, false)?;
    let stun_servers = gather.stun_servers[..gather.stun_count as usize]
        .iter()
        .map(|v| decode_socket(*v, false))
        .collect::<Result<Vec<_>, _>>()?;
    let pump = socket
        .into_multipair_pump(&stun_servers, Duration::from_secs(10))
        .map_err(|_| REASON_SOCKET_V4)?;
    Ok(RetainedV4 {
        owner: expected,
        network_handle: gather.network_handle,
        identity,
        pump,
        candidates: vec![host],
        next_candidate_batch_seq: 2,
        gather_deadline: Instant::now()
            + Duration::from_millis(u64::from(gather.gather_timeout_ms)),
        gathering_complete: false,
        network_cost: gather.network_cost,
        candidate_expires_at_ms: gather.candidate_expires_at_ms,
    })
}

fn candidate(
    endpoint: SocketAddr,
    kind: u32,
    related: [u8; 16],
    network_cost: u32,
    expires: i64,
) -> Result<TermLinkDirectCandidateV4, u32> {
    let mut id = [0; 16];
    SystemRandom::new()
        .fill(&mut id)
        .map_err(|_| REASON_INTERNAL_V4)?;
    if id == [0; 16] {
        id[0] = 1;
    }
    Ok(TermLinkDirectCandidateV4 {
        abi_version: ABI_VERSION_V4,
        struct_size: size_of::<TermLinkDirectCandidateV4>() as u32,
        candidate_id: id,
        related_candidate_id: related,
        endpoint: encode_socket(endpoint),
        kind,
        carrier: CARRIER_UDP_QUIC_V4,
        priority: if kind == CANDIDATE_HOST_V4 {
            2_130_706_431
        } else {
            1_694_498_815
        },
        network_cost,
        expires_at_ms: expires,
        reserved: [0; 2],
    })
}

#[allow(clippy::too_many_arguments)]
fn publish_candidate_batch(
    events: &EventQueueV4,
    owner_state: &Mutex<OwnerV4>,
    cancel: &AtomicU64,
    event_owner: OwnerV4,
    sequence: u32,
    values: &[TermLinkDirectCandidateV4],
    total: u32,
    complete: bool,
) -> Result<(), u32> {
    if values.len() > MAX_CANDIDATES_PER_BATCH_V4 {
        return Err(REASON_INTERNAL_V4);
    }
    let mut payload = TermLinkDirectCandidateBatchV4 {
        abi_version: ABI_VERSION_V4,
        struct_size: size_of::<TermLinkDirectCandidateBatchV4>() as u32,
        epoch: event_owner.epoch,
        attempt_id: event_owner.attempt_id,
        batch_seq: sequence,
        candidate_count: values.len() as u32,
        total_candidate_count: total,
        gathering_complete: u32::from(complete),
        ..TermLinkDirectCandidateBatchV4::default()
    };
    payload.candidates[..values.len()].copy_from_slice(values);
    let mut event = base_event(
        EVENT_CANDIDATE_BATCH_V4,
        STATE_GATHERING_V4,
        event_owner.epoch,
        event_owner.attempt_id,
    );
    event.payload_size = size_of::<TermLinkDirectCandidateBatchV4>() as u32;
    event.payload = TermLinkDirectEventPayloadV4 { raw: [0; 2_080] };
    event.payload.candidate_batch = payload;
    push_if_active(events, owner_state, cancel, event_owner, event)
        .then_some(())
        .ok_or(REASON_QUEUE_FULL_V4)
}

fn begin_checks_v4(
    config: TermLinkDirectCreateV4,
    begin: TermLinkDirectBeginChecksV4,
    retained: RetainedV4,
) -> Result<CheckV4, (u32, Option<Box<RetainedV4>>)> {
    let ticket_expires_at_ms = match validate_begin(config, &begin, &retained) {
        Ok(value) => value,
        Err(reason) => return Err((reason, Some(Box::new(retained)))),
    };
    let mut pump = retained.pump;
    if pump
        .start_checks(Duration::from_millis(u64::from(begin.timeout_ms)))
        .is_err()
    {
        return Err((REASON_INVALID_PLAN_V4, None));
    }
    let mut check = CheckV4 {
        owner: retained.owner,
        begin,
        identity: retained.identity,
        local_candidates: retained.candidates,
        next_candidate_batch_seq: retained.next_candidate_batch_seq,
        gather_deadline: retained.gather_deadline,
        gathering_complete: retained.gathering_complete,
        network_cost: retained.network_cost,
        candidate_expires_at_ms: retained.candidate_expires_at_ms,
        ticket_expires_at_ms,
        pump,
        pairs: HashMap::new(),
        pair_keys: HashSet::new(),
        validated: HashSet::new(),
        next_batch_seq: 1,
        total_pair_count: 0,
        plan_complete: false,
        nomination_committed: false,
    };
    if let Err(reason) = append_initial_pairs_v4(&mut check) {
        return Err((reason, None));
    }
    Ok(check)
}

fn validate_begin(
    config: TermLinkDirectCreateV4,
    value: &TermLinkDirectBeginChecksV4,
    retained: &RetainedV4,
) -> Result<i64, u32> {
    if !valid_header(value, value.abi_version, value.struct_size)
        || value.reserved0 != 0
        || value.reserved != [0; 4]
        || !retained.owner.matches(value.epoch, value.attempt_id)
        || value.connection_id == [0; 16]
        || value.local_device_id == [0; 16]
        || value.peer_device_id == [0; 16]
        || value.local_device_id == value.peer_device_id
        || value.check_round_id == [0; 16]
        || value.connectivity_nonce == [0; 16]
        || value.local_send_probe_hmac_key == [0; 32]
        || value.peer_receive_probe_hmac_key == [0; 32]
        || value.local_send_probe_hmac_key == value.peer_receive_probe_hmac_key
        || !matches!(value.role, ROLE_INITIATOR_V4 | ROLE_RESPONDER_V4)
        || !(500..=10_000).contains(&value.timeout_ms)
        || value.now_ms < 0
        || value.ticket_length == 0
        || value.ticket_length as usize > MAX_TICKET_BYTES_V4
        || value.pair_batch_seq != 1
        || value.pair_count as usize > MAX_PAIRS_PER_BATCH_V4
        || value.total_pair_count != value.pair_count
        || value.total_pair_count as usize > MAX_PAIRS_TOTAL_V4
        || value.plan_complete > 1
        || (value.pair_count == 0 && value.plan_complete == 0)
        || value.network_handle != retained.network_handle
    {
        return Err(REASON_INVALID_PLAN_V4);
    }
    if (config.platform == PLATFORM_ANDROID_V4 && value.network_handle == 0)
        || (config.platform == PLATFORM_WINDOWS_V4 && value.network_handle != 0)
    {
        return Err(REASON_INVALID_PLATFORM_V4);
    }
    if value.ticket[value.ticket_length as usize..]
        .iter()
        .any(|v| *v != 0)
    {
        return Err(REASON_INVALID_TICKET_V4);
    }
    let role = if value.role == ROLE_INITIATOR_V4 {
        DirectTicketRole::Initiator
    } else {
        DirectTicketRole::Responder
    };
    let ticket = verify_direct_path_ticket(
        &value.ticket[..value.ticket_length as usize],
        &value.server_signing_public_key,
        &DirectTicketExpectation {
            connection_id: value.connection_id,
            path_epoch: value.epoch,
            local_device_id: value.local_device_id,
            peer_device_id: value.peer_device_id,
            local_role: role,
            now_ms: value.now_ms,
        },
    )
    .map_err(|_| REASON_INVALID_TICKET_V4)?;
    if ticket.ticket_id != value.ticket_id {
        return Err(REASON_INVALID_TICKET_V4);
    }
    Ok(ticket.expires_at_ms)
}

fn append_initial_pairs_v4(check: &mut CheckV4) -> Result<(), u32> {
    let begin = check.begin;
    append_pair_slice(
        check,
        begin.pair_batch_seq,
        begin.pair_count,
        begin.total_pair_count,
        begin.plan_complete,
        &begin.pairs,
    )
}
fn append_pairs_v4(
    check: &mut CheckV4,
    value: &TermLinkDirectAppendCheckPairsV4,
) -> Result<(), u32> {
    if !valid_header(value, value.abi_version, value.struct_size)
        || value.reserved != [0; 4]
        || !check.owner.matches(value.epoch, value.attempt_id)
        || value.check_round_id != check.begin.check_round_id
        || value.pair_count as usize > MAX_PAIRS_PER_BATCH_V4
        || value.plan_complete > 1
        || (value.pair_count == 0 && value.plan_complete == 0)
    {
        return Err(REASON_INVALID_PLAN_V4);
    }
    append_pair_slice(
        check,
        value.pair_batch_seq,
        value.pair_count,
        value.total_pair_count,
        value.plan_complete,
        &value.pairs,
    )
}
fn append_pair_slice(
    check: &mut CheckV4,
    sequence: u32,
    count: u32,
    total: u32,
    complete: u32,
    records: &[TermLinkDirectCandidatePairV4; MAX_PAIRS_PER_BATCH_V4],
) -> Result<(), u32> {
    if check.plan_complete
        || sequence != check.next_batch_seq
        || total != check.total_pair_count + count
        || total as usize > MAX_PAIRS_TOTAL_V4
        || records[count as usize..]
            .iter()
            .any(|v| *v != TermLinkDirectCandidatePairV4::default())
    {
        return Err(REASON_INVALID_PLAN_V4);
    }
    let mut new_keys = HashSet::new();
    let mut native = Vec::with_capacity(count as usize);
    let mut decoded = Vec::with_capacity(count as usize);
    for (offset, pair) in records[..count as usize].iter().enumerate() {
        validate_pair(
            pair,
            check.begin.role,
            check.total_pair_count + offset as u32 + 1,
            check.begin.now_ms,
            check.ticket_expires_at_ms,
        )?;
        if !check
            .local_candidates
            .contains(&local_candidate(*pair, check.begin.role))
        {
            return Err(REASON_INVALID_CANDIDATE_V4);
        }
        let key = (
            pair.initiator_candidate.candidate_id,
            pair.responder_candidate.candidate_id,
        );
        if check.pair_keys.contains(&key) || !new_keys.insert(key) {
            return Err(REASON_INVALID_PLAN_V4);
        }
        let wire_pair = CandidatePair {
            initiator_candidate_id: key.0,
            responder_candidate_id: key.1,
        };
        let token = pair_token(&wire_pair);
        if let Some(existing) = check.pairs.get(&token) {
            let existing_key = (
                existing.initiator_candidate.candidate_id,
                existing.responder_candidate.candidate_id,
            );
            if existing_key != key {
                return Err(REASON_INVALID_PLAN_V4);
            }
        }
        if decoded.iter().any(|(existing_token, _, existing_key)| {
            *existing_token == token && *existing_key != key
        }) {
            return Err(REASON_INVALID_PLAN_V4);
        }
        let peer = remote_candidate(*pair, check.begin.role).endpoint;
        let peer = decode_socket(peer, false)?;
        let nonce = derive_pair_probe_nonce_v2(
            &check.begin.local_send_probe_hmac_key,
            &check.begin.connectivity_nonce,
            &wire_pair,
        );
        let probe = ConnectivityDatagram::new_probe(ProbeFields {
            connection_id: check.begin.connection_id,
            path_epoch: check.begin.epoch,
            ticket_id: check.begin.ticket_id,
            nomination_id: check.begin.check_round_id,
            pair: wire_pair,
            sender_device_id: check.begin.local_device_id,
            probe_nonce: nonce,
            sent_at_ms: check.begin.now_ms,
            anti_amplification_padding: check.begin.attempt_id,
        })
        .and_then(|v| v.encode_authenticated_v2(&check.begin.local_send_probe_hmac_key))
        .map_err(|_| REASON_INVALID_PLAN_V4)?;
        native.push(MultipairCheckPair {
            token,
            peer,
            probe_packet: probe,
        });
        decoded.push((token, *pair, key));
    }
    check
        .pump
        .append_pairs(&native)
        .map_err(|_| REASON_INVALID_PLAN_V4)?;
    for (token, pair, key) in decoded {
        check.pairs.insert(token, pair);
        check.pair_keys.insert(key);
    }
    check.total_pair_count = total;
    check.next_batch_seq += 1;
    check.plan_complete = complete == 1;
    if check.plan_complete {
        check.pump.mark_plan_complete();
    }
    Ok(())
}

fn validate_pair(
    pair: &TermLinkDirectCandidatePairV4,
    role: u32,
    expected_rank: u32,
    now_ms: i64,
    ticket_expires_at_ms: i64,
) -> Result<(), u32> {
    if !valid_header(pair, pair.abi_version, pair.struct_size)
        || pair.rank != expected_rank
        || pair.reserved0 != 0
        || pair.reserved != [0; 2]
    {
        return Err(REASON_INVALID_PLAN_V4);
    }
    validate_candidate(&pair.initiator_candidate)?;
    validate_candidate(&pair.responder_candidate)?;
    let local = local_candidate(*pair, role);
    let remote = remote_candidate(*pair, role);
    let local_endpoint = decode_socket(local.endpoint, false)?;
    let remote_endpoint = decode_socket(remote.endpoint, false)?;
    if local.candidate_id == remote.candidate_id
        || local_endpoint.is_ipv4() != remote_endpoint.is_ipv4()
        || local.expires_at_ms <= now_ms
        || remote.expires_at_ms <= now_ms
        || local.expires_at_ms > now_ms.saturating_add(MAX_CANDIDATE_LIFETIME_MS_V4)
        || remote.expires_at_ms > now_ms.saturating_add(MAX_CANDIDATE_LIFETIME_MS_V4)
        || local.expires_at_ms > ticket_expires_at_ms
        || remote.expires_at_ms > ticket_expires_at_ms
    {
        return Err(REASON_INVALID_PLAN_V4);
    }
    validate_candidate_endpoint(remote_endpoint).map_err(|_| REASON_INVALID_CANDIDATE_V4)?;
    Ok(())
}
fn validate_candidate(value: &TermLinkDirectCandidateV4) -> Result<(), u32> {
    if !valid_header(value, value.abi_version, value.struct_size)
        || value.candidate_id == [0; 16]
        || value.reserved != [0; 2]
        || value.carrier != CARRIER_UDP_QUIC_V4
        || !matches!(
            value.kind,
            CANDIDATE_HOST_V4 | CANDIDATE_SERVER_REFLEXIVE_V4
        )
        || value.priority == 0
        || value.priority > i32::MAX as u32
        || value.network_cost > 999
        || value.expires_at_ms <= 0
        || (value.kind == CANDIDATE_HOST_V4 && value.related_candidate_id != [0; 16])
        || (value.kind == CANDIDATE_SERVER_REFLEXIVE_V4 && value.related_candidate_id == [0; 16])
    {
        return Err(REASON_INVALID_CANDIDATE_V4);
    }
    decode_socket(value.endpoint, false)?;
    Ok(())
}
fn local_candidate(pair: TermLinkDirectCandidatePairV4, role: u32) -> TermLinkDirectCandidateV4 {
    if role == ROLE_INITIATOR_V4 {
        pair.initiator_candidate
    } else {
        pair.responder_candidate
    }
}
fn remote_candidate(pair: TermLinkDirectCandidatePairV4, role: u32) -> TermLinkDirectCandidateV4 {
    if role == ROLE_INITIATOR_V4 {
        pair.responder_candidate
    } else {
        pair.initiator_candidate
    }
}
fn pair_token(pair: &CandidatePair) -> PairToken {
    let mut input = [0; 32];
    input[..16].copy_from_slice(&pair.initiator_candidate_id);
    input[16..].copy_from_slice(&pair.responder_candidate_id);
    PairToken::new(sha256(&input)[..16].try_into().unwrap())
}

fn authenticate_packet(
    begin: &TermLinkDirectBeginChecksV4,
    pairs: &HashMap<PairToken, TermLinkDirectCandidatePairV4>,
    source: SocketAddr,
    packet: &[u8],
    now_ms: i64,
) -> Option<AuthenticatedMultipairPacket> {
    for (token, record) in pairs {
        if decode_socket(remote_candidate(*record, begin.role).endpoint, false).ok()? != source {
            continue;
        }
        let pair = CandidatePair {
            initiator_candidate_id: record.initiator_candidate.candidate_id,
            responder_candidate_id: record.responder_candidate.candidate_id,
        };
        let peer_nonce = derive_pair_probe_nonce_v2(
            &begin.peer_receive_probe_hmac_key,
            &begin.connectivity_nonce,
            &pair,
        );
        let expected_probe = ExpectedDatagram {
            kind: DatagramKind::Probe,
            connection_id: begin.connection_id,
            path_epoch: begin.epoch,
            ticket_id: begin.ticket_id,
            nomination_id: begin.check_round_id,
            pair: pair.clone(),
            sender_device_id: begin.peer_device_id,
            probe_nonce: Some(peer_nonce),
            now_ms,
            max_clock_skew_ms: 30_000,
        };
        if let Ok(probe) = ConnectivityDatagram::decode_authenticated_v2(
            packet,
            &begin.peer_receive_probe_hmac_key,
            &expected_probe,
        ) {
            let mut response = [0; 16];
            if SystemRandom::new().fill(&mut response).is_err() || response == [0; 16] {
                return None;
            }
            let ack = ConnectivityDatagram::acknowledgement(
                &probe,
                begin.local_device_id,
                response,
                now_ms,
            )
            .ok()?
            .encode_ack_without_amplification_v2(packet.len(), &begin.local_send_probe_hmac_key)
            .ok()?;
            return Some(AuthenticatedMultipairPacket::Probe {
                token: *token,
                acknowledgement: ack,
            });
        }
        let local_nonce = derive_pair_probe_nonce_v2(
            &begin.local_send_probe_hmac_key,
            &begin.connectivity_nonce,
            &pair,
        );
        let expected_ack = ExpectedDatagram {
            kind: DatagramKind::ProbeAck,
            connection_id: begin.connection_id,
            path_epoch: begin.epoch,
            ticket_id: begin.ticket_id,
            nomination_id: begin.check_round_id,
            pair,
            sender_device_id: begin.peer_device_id,
            probe_nonce: Some(local_nonce),
            now_ms,
            max_clock_skew_ms: 30_000,
        };
        if ConnectivityDatagram::decode_authenticated_v2(
            packet,
            &begin.peer_receive_probe_hmac_key,
            &expected_ack,
        )
        .is_ok()
        {
            return Some(AuthenticatedMultipairPacket::Acknowledgement { token: *token });
        }
    }
    None
}

#[allow(clippy::too_many_arguments)]
fn commit_v4(
    config: TermLinkDirectCreateV4,
    commit: TermLinkDirectCommitNominationV4,
    mut check: CheckV4,
    signatures: &Receiver<Box<TermLinkDirectSignatureV4>>,
    events: &EventQueueV4,
    cancel: &AtomicU64,
    network: &AtomicU64,
    owner: &Mutex<OwnerV4>,
    relay_only: &AtomicBool,
) -> Result<(), (u32, Option<Box<CheckV4>>)> {
    if !check.plan_complete
        || check.nomination_committed
        || !valid_header(&commit, commit.abi_version, commit.struct_size)
        || !check.owner.matches(commit.epoch, commit.attempt_id)
        || commit.check_round_id != check.begin.check_round_id
        || commit.nomination_id == [0; 16]
        || commit.role != check.begin.role
        || commit.selection_confirmed != 1
        || !(500..=10_000).contains(&commit.timeout_ms)
        || commit.local_proof_nonce_length as usize > MAX_PROOF_NONCE_BYTES_V4
        || (commit.local_proof_nonce_length as usize) < MIN_PROOF_NONCE_BYTES_V4
        || commit.peer_certificate_sha256 == [0; 32]
        || commit.peer_device_signing_public_key == [0; 32]
        || commit.now_ms < check.begin.now_ms
        || commit.now_ms > check.ticket_expires_at_ms
        || commit.reserved != [0; 4]
        || commit.local_proof_nonce[commit.local_proof_nonce_length as usize..]
            .iter()
            .any(|v| *v != 0)
        || validate_pair(
            &commit.winning_pair,
            commit.role,
            commit.winning_pair.rank,
            commit.now_ms,
            check.ticket_expires_at_ms,
        )
        .is_err()
    {
        return Err((REASON_NOMINATION_REJECTED_V4, Some(Box::new(check))));
    }
    let wire_pair = CandidatePair {
        initiator_candidate_id: commit.winning_pair.initiator_candidate.candidate_id,
        responder_candidate_id: commit.winning_pair.responder_candidate.candidate_id,
    };
    let token = pair_token(&wire_pair);
    if !check.validated.contains(&token) || check.pairs.get(&token) != Some(&commit.winning_pair) {
        return Err((REASON_NOMINATION_REJECTED_V4, Some(Box::new(check))));
    }
    check.nomination_committed = true;
    let CheckV4 {
        begin,
        identity,
        pump,
        ..
    } = check;
    let handoff = match pump.commit_winner(token) {
        Ok(v) => v,
        Err(_) => return Err((REASON_NOMINATION_REJECTED_V4, None)),
    };
    let result = authenticate_quic_v4(
        config, begin, identity, &commit, handoff, signatures, events, cancel, network, owner,
        relay_only,
    );
    result.map_err(|reason| (reason, None))
}

#[allow(clippy::too_many_arguments)]
fn authenticate_quic_v4(
    _config: TermLinkDirectCreateV4,
    begin: TermLinkDirectBeginChecksV4,
    identity: TemporaryIdentity,
    commit: &TermLinkDirectCommitNominationV4,
    handoff: QuicSocketHandoff,
    signatures: &Receiver<Box<TermLinkDirectSignatureV4>>,
    events: &EventQueueV4,
    cancel: &AtomicU64,
    network: &AtomicU64,
    owner: &Mutex<OwnerV4>,
    relay_only: &AtomicBool,
) -> Result<(), u32> {
    let expected = OwnerV4 {
        epoch: begin.epoch,
        attempt_id: begin.attempt_id,
    };
    let remote = decode_socket(
        remote_candidate(commit.winning_pair, commit.role).endpoint,
        false,
    )?;
    let ticket = verify_direct_path_ticket(
        &begin.ticket[..begin.ticket_length as usize],
        &begin.server_signing_public_key,
        &DirectTicketExpectation {
            connection_id: begin.connection_id,
            path_epoch: begin.epoch,
            local_device_id: begin.local_device_id,
            peer_device_id: begin.peer_device_id,
            local_role: if begin.role == ROLE_INITIATOR_V4 {
                DirectTicketRole::Initiator
            } else {
                DirectTicketRole::Responder
            },
            now_ms: commit.now_ms,
        },
    )
    .map_err(|_| REASON_INVALID_TICKET_V4)?;
    let role = if commit.role == ROLE_INITIATOR_V4 {
        PeerRole::Initiator
    } else {
        PeerRole::Responder
    };
    let mut state = base_event(
        EVENT_STATE_CHANGED_V4,
        STATE_AUTHENTICATING_V4,
        expected.epoch,
        expected.attempt_id,
    );
    state.endpoint = encode_socket(remote);
    if !push_if_active(events, owner, cancel, expected, state) {
        return Err(REASON_QUEUE_FULL_V4);
    }
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .map_err(|_| REASON_INTERNAL_V4)?;
    let binding_value = runtime.block_on(async {
        let endpoint = if role == PeerRole::Initiator {
            ShadowEndpoint::client(handoff, remote, identity, commit.peer_certificate_sha256)
        } else {
            ShadowEndpoint::server(handoff, remote, identity, commit.peer_certificate_sha256)
        }
        .map_err(|_| REASON_QUIC_V4)?;
        if cancelled(cancel, network, owner, expected) {
            endpoint.close();
            return Err(if network.load(Ordering::Acquire) >= expected.epoch {
                REASON_NETWORK_CHANGED_V4
            } else {
                REASON_CANCELLED_V4
            });
        }
        let payload = TermLinkDirectNominationReadyV4 {
            abi_version: ABI_VERSION_V4,
            struct_size: size_of::<TermLinkDirectNominationReadyV4>() as u32,
            epoch: expected.epoch,
            attempt_id: expected.attempt_id,
            check_round_id: begin.check_round_id,
            nomination_id: commit.nomination_id,
            winning_pair: commit.winning_pair,
            role: commit.role,
            reserved0: 0,
            reserved: [0; 2],
        };
        let mut nomination = base_event(
            EVENT_NOMINATION_READY_V4,
            STATE_NOMINATION_PENDING_V4,
            expected.epoch,
            expected.attempt_id,
        );
        nomination.endpoint = encode_socket(remote);
        nomination.payload_size = size_of::<TermLinkDirectNominationReadyV4>() as u32;
        nomination.payload = TermLinkDirectEventPayloadV4 { raw: [0; 2_080] };
        nomination.payload.nomination_ready = payload;
        if !push_if_active(events, owner, cancel, expected, nomination) {
            endpoint.close();
            return Err(REASON_QUEUE_FULL_V4);
        }
        let connection = tokio::select! {
            reason = wait_for_cancellation_v4(cancel, network, owner, expected) => {
                endpoint.close();
                return Err(reason);
            }
            result = async {
                if role == PeerRole::Initiator {
                    endpoint.connect_pinned(remote, commit.peer_certificate_sha256).await
                } else {
                    endpoint.accept_pinned(remote, commit.peer_certificate_sha256).await
                }
            } => result.map_err(|_| REASON_QUIC_V4)?,
        };
        let context = PeerBindingContext {
            ticket_sha256: ticket.ticket_sha256,
            connection_id: begin.connection_id,
            path_epoch: begin.epoch,
        };
        let exporter =
            tls_exporter_sha256(&connection, &context).map_err(|_| REASON_PEER_PROOF_V4)?;
        let proof = PeerProofRequest {
            ticket_sha256: context.ticket_sha256,
            role,
            device_id: begin.local_device_id,
            peer_device_id: begin.peer_device_id,
            connection_id: begin.connection_id,
            path_epoch: begin.epoch,
            proof_nonce: commit.local_proof_nonce[..commit.local_proof_nonce_length as usize]
                .to_vec(),
            tls_exporter_sha256: exporter,
        };
        let transcript = proof.transcript().map_err(|_| REASON_PEER_PROOF_V4)?;
        let request_id = events.next_request_id();
        let mut payload = TermLinkDirectOpaqueBytesV4 {
            abi_version: ABI_VERSION_V4,
            struct_size: size_of::<TermLinkDirectOpaqueBytesV4>() as u32,
            data_length: transcript.len() as u32,
            ..TermLinkDirectOpaqueBytesV4::default()
        };
        payload.data[..transcript.len()].copy_from_slice(&transcript);
        let mut sign = base_event(
            EVENT_SIGN_REQUEST_V4,
            STATE_AUTHENTICATING_V4,
            expected.epoch,
            expected.attempt_id,
        );
        sign.request_id = request_id;
        sign.payload_size = size_of::<TermLinkDirectOpaqueBytesV4>() as u32;
        sign.payload = TermLinkDirectEventPayloadV4 { raw: [0; 2_080] };
        sign.payload.opaque = payload;
        if !push_if_active(events, owner, cancel, expected, sign) {
            return Err(REASON_QUEUE_FULL_V4);
        }
        let signature = wait_signature_v4(
            signatures,
            request_id,
            expected,
            Duration::from_millis(u64::from(commit.timeout_ms)),
            cancel,
            network,
            owner,
        )?;
        let peer_verifier = Ed25519PeerProofVerifier::new(commit.peer_device_signing_public_key);
        let authenticated = tokio::select! {
            reason = wait_for_cancellation_v4(cancel, network, owner, expected) => {
                endpoint.close();
                return Err(reason);
            }
            result = exchange_and_authenticate_shadow_connection(
                connection,
                &context,
                ExpectedPeer {
                    role: if role == PeerRole::Initiator {
                        PeerRole::Responder
                    } else {
                        PeerRole::Initiator
                    },
                    device_id: begin.peer_device_id,
                    local_device_id: begin.local_device_id,
                },
                &begin.ticket[..begin.ticket_length as usize],
                &proof,
                &signature,
                &peer_verifier,
            ) => result.map_err(|_| REASON_PEER_PROOF_V4)?,
        };
        let local_certificate = endpoint.certificate_sha256();
        let local_proof = sha256(&transcript);
        let peer_proof = authenticated.peer_proof_sha256();
        let (initiator_certificate_sha256, responder_certificate_sha256) =
            if role == PeerRole::Initiator {
                (local_certificate, commit.peer_certificate_sha256)
            } else {
                (commit.peer_certificate_sha256, local_certificate)
            };
        let (initiator_proof_sha256, responder_proof_sha256) = if role == PeerRole::Initiator {
            (local_proof, peer_proof)
        } else {
            (peer_proof, local_proof)
        };
        let path = direct_path_binding_sha256(DirectPathBindingInputs {
            ticket_sha256: context.ticket_sha256,
            initiator_candidate_id: commit.winning_pair.initiator_candidate.candidate_id,
            responder_candidate_id: commit.winning_pair.responder_candidate.candidate_id,
            initiator_certificate_sha256,
            responder_certificate_sha256,
            initiator_proof_sha256,
            responder_proof_sha256,
            tls_exporter_sha256: exporter,
        });
        tokio::select! {
            reason = wait_for_cancellation_v4(cancel, network, owner, expected) => {
                endpoint.close();
                return Err(reason);
            }
            result = async {
                if role == PeerRole::Initiator {
                    shadow_ping(&authenticated, expected.attempt_id).await
                } else {
                    shadow_echo_once(&authenticated).await
                }
            } => result.map_err(|_| REASON_QUIC_V4)?,
        }
        endpoint.close();
        Ok(path)
    })?;
    if cancelled(cancel, network, owner, expected) {
        return Err(if network.load(Ordering::Acquire) >= expected.epoch {
            REASON_NETWORK_CHANGED_V4
        } else {
            REASON_CANCELLED_V4
        });
    }
    let ready = shadow_ready_event(&begin, commit, remote, binding_value);
    if !push_if_active(events, owner, cancel, expected, ready) {
        return Err(REASON_QUEUE_FULL_V4);
    }
    // Shadow readiness is observational only; Relay remains the authoritative route.
    relay_only.store(true, Ordering::Release);
    Ok(())
}

async fn wait_for_cancellation_v4(
    cancel: &AtomicU64,
    network: &AtomicU64,
    owner: &Mutex<OwnerV4>,
    expected: OwnerV4,
) -> u32 {
    loop {
        if cancelled(cancel, network, owner, expected) {
            return if network.load(Ordering::Acquire) >= expected.epoch {
                REASON_NETWORK_CHANGED_V4
            } else {
                REASON_CANCELLED_V4
            };
        }
        tokio::time::sleep(Duration::from_millis(20)).await;
    }
}

fn shadow_ready_event(
    begin: &TermLinkDirectBeginChecksV4,
    commit: &TermLinkDirectCommitNominationV4,
    remote: SocketAddr,
    direct_path_binding: [u8; 32],
) -> TermLinkDirectEventV4 {
    let payload = TermLinkDirectShadowReadyV4 {
        abi_version: ABI_VERSION_V4,
        struct_size: size_of::<TermLinkDirectShadowReadyV4>() as u32,
        epoch: begin.epoch,
        attempt_id: begin.attempt_id,
        check_round_id: begin.check_round_id,
        nomination_id: commit.nomination_id,
        winning_pair: commit.winning_pair,
        direct_path_binding,
        reserved: [0; 2],
    };
    let mut ready = base_event(
        EVENT_SHADOW_READY_V4,
        STATE_SHADOW_DIRECT_V4,
        begin.epoch,
        begin.attempt_id,
    );
    ready.endpoint = encode_socket(remote);
    ready.payload_size = size_of::<TermLinkDirectShadowReadyV4>() as u32;
    ready.payload = TermLinkDirectEventPayloadV4 { raw: [0; 2_080] };
    ready.payload.shadow_ready = payload;
    ready
}

fn wait_signature_v4(
    receiver: &Receiver<Box<TermLinkDirectSignatureV4>>,
    request_id: u64,
    expected: OwnerV4,
    timeout: Duration,
    cancel: &AtomicU64,
    network: &AtomicU64,
    owner: &Mutex<OwnerV4>,
) -> Result<Vec<u8>, u32> {
    let deadline = Instant::now() + timeout;
    loop {
        if cancelled(cancel, network, owner, expected) {
            return Err(if network.load(Ordering::Acquire) >= expected.epoch {
                REASON_NETWORK_CHANGED_V4
            } else {
                REASON_CANCELLED_V4
            });
        }
        let remaining = deadline.saturating_duration_since(Instant::now());
        if remaining.is_zero() {
            return Err(REASON_SIGNATURE_TIMEOUT_V4);
        }
        match receiver.recv_timeout(remaining.min(Duration::from_millis(50))) {
            Ok(value)
                if value.epoch == expected.epoch
                    && value.attempt_id == expected.attempt_id
                    && value.request_id == request_id
                    && value.signature_length > 0
                    && value.signature_length as usize <= MAX_SIGNATURE_BYTES_V4 =>
            {
                return Ok(value.signature[..value.signature_length as usize].to_vec());
            }
            Ok(_) | Err(RecvTimeoutError::Timeout) => {}
            Err(RecvTimeoutError::Disconnected) => {
                return Err(if network.load(Ordering::Acquire) >= expected.epoch {
                    REASON_NETWORK_CHANGED_V4
                } else {
                    REASON_CANCELLED_V4
                });
            }
        }
    }
}

fn bind_socket(
    platform: u32,
    address: SocketAddr,
    network_handle: u64,
) -> Result<DirectUdpSocket, u32> {
    match platform {
        PLATFORM_WINDOWS_V4 => DirectUdpSocket::bind(address).map_err(|_| REASON_SOCKET_V4),
        PLATFORM_ANDROID_V4 => {
            #[cfg(target_os = "android")]
            {
                let binder = AndroidNetworkBinder::new(network_handle)
                    .map_err(|_| REASON_INVALID_PLATFORM_V4)?;
                DirectUdpSocket::bind_with_platform(address, &binder).map_err(|_| REASON_SOCKET_V4)
            }
            #[cfg(not(target_os = "android"))]
            {
                let _ = (address, network_handle);
                Err(REASON_INVALID_PLATFORM_V4)
            }
        }
        _ => Err(REASON_INVALID_PLATFORM_V4),
    }
}
fn decode_ip(bytes: [u8; 16], length: u32) -> Result<IpAddr, u32> {
    let ip = match length {
        4 if bytes[4..].iter().all(|v| *v == 0) => {
            IpAddr::V4(Ipv4Addr::new(bytes[0], bytes[1], bytes[2], bytes[3]))
        }
        16 => IpAddr::V6(Ipv6Addr::from(bytes)),
        _ => return Err(REASON_INVALID_CANDIDATE_V4),
    };
    if ip.is_unspecified() || ip.is_loopback() {
        return Err(REASON_INVALID_CANDIDATE_V4);
    }
    Ok(ip)
}
fn decode_socket(value: TermLinkSocketAddrV4, allow_zero_port: bool) -> Result<SocketAddr, u32> {
    if !valid_header(&value, value.abi_version, value.struct_size) || value.reserved != [0; 2] {
        return Err(REASON_INVALID_CANDIDATE_V4);
    }
    let ip = decode_ip(value.ip, value.ip_length)?;
    let port = u16::try_from(value.port).map_err(|_| REASON_INVALID_CANDIDATE_V4)?;
    if (!allow_zero_port && port < 1024) || (allow_zero_port && port != 0 && port < 1024) {
        return Err(REASON_INVALID_CANDIDATE_V4);
    }
    Ok(SocketAddr::new(ip, port))
}
fn encode_socket(value: SocketAddr) -> TermLinkSocketAddrV4 {
    let mut out = TermLinkSocketAddrV4 {
        abi_version: ABI_VERSION_V4,
        struct_size: size_of::<TermLinkSocketAddrV4>() as u32,
        port: u32::from(value.port()),
        ..TermLinkSocketAddrV4::default()
    };
    match value.ip() {
        IpAddr::V4(ip) => {
            out.ip[..4].copy_from_slice(&ip.octets());
            out.ip_length = 4;
        }
        IpAddr::V6(ip) => {
            out.ip = ip.octets();
            out.ip_length = 16;
        }
    }
    out
}
fn base_event(kind: u32, state: u32, epoch: u64, attempt_id: [u8; 16]) -> TermLinkDirectEventV4 {
    TermLinkDirectEventV4 {
        abi_version: ABI_VERSION_V4,
        struct_size: size_of::<TermLinkDirectEventV4>() as u32,
        kind,
        state,
        epoch,
        attempt_id,
        endpoint: TermLinkSocketAddrV4::default(),
        ..TermLinkDirectEventV4::default()
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn termlink_direct_abi_version_v4() -> u32 {
    ABI_VERSION_V4
}
#[unsafe(no_mangle)]
/// Creates an independent V4 shadow-direct owner.
///
/// # Safety
/// `config` must be null or readable for one `TermLinkDirectCreateV4` during this call.
pub unsafe extern "C" fn termlink_direct_create_v4(
    config: *const TermLinkDirectCreateV4,
) -> *mut c_void {
    catch_unwind(AssertUnwindSafe(|| {
        if config.is_null() {
            return std::ptr::null_mut();
        }
        NativeBridgeV4::create(unsafe { config.read_unaligned() })
            .map(|v| Box::into_raw(Box::new(v)).cast())
            .unwrap_or(std::ptr::null_mut())
    }))
    .unwrap_or(std::ptr::null_mut())
}
macro_rules! submit_ffi {
    ($name:ident, $ty:ty, $body:expr) => {
        #[unsafe(no_mangle)]
        #[doc = "# Safety\n`handle` must be a live V4 handle and `input` must be readable for the declared input record during this call."]
        pub unsafe extern "C" fn $name(handle: *mut c_void, input: *const $ty) -> i32 {
            catch_unwind(AssertUnwindSafe(|| {
                let Some(bridge) = (unsafe { bridge_ref_v4(handle) }) else {
                    return -1;
                };
                if input.is_null() {
                    return -2;
                }
                let value = unsafe { input.read_unaligned() };
                ($body)(bridge, value)
            }))
            .unwrap_or(-3)
        }
    };
}
submit_ffi!(
    termlink_direct_gather_v4,
    TermLinkDirectGatherV4,
    |bridge: &NativeBridgeV4, value| bridge.accept_gather(value)
);
submit_ffi!(
    termlink_direct_begin_checks_v4,
    TermLinkDirectBeginChecksV4,
    |bridge: &NativeBridgeV4, value: TermLinkDirectBeginChecksV4| {
        if !valid_header(&value, value.abi_version, value.struct_size) {
            return -2;
        }
        if !bridge.accept_current(value.epoch, value.attempt_id) {
            return 0;
        }
        bridge.submit(
            CommandV4::Begin(Box::new(value)),
            value.epoch,
            value.attempt_id,
        )
    }
);
submit_ffi!(
    termlink_direct_append_check_pairs_v4,
    TermLinkDirectAppendCheckPairsV4,
    |bridge: &NativeBridgeV4, value: TermLinkDirectAppendCheckPairsV4| {
        if !valid_header(&value, value.abi_version, value.struct_size) {
            return -2;
        }
        if !bridge.accept_current(value.epoch, value.attempt_id) {
            return 0;
        }
        bridge.submit(
            CommandV4::Append(Box::new(value)),
            value.epoch,
            value.attempt_id,
        )
    }
);
submit_ffi!(
    termlink_direct_commit_nomination_v4,
    TermLinkDirectCommitNominationV4,
    |bridge: &NativeBridgeV4, value: TermLinkDirectCommitNominationV4| {
        if !valid_header(&value, value.abi_version, value.struct_size) {
            return -2;
        }
        if !bridge.accept_current(value.epoch, value.attempt_id) {
            return 0;
        }
        bridge.submit(
            CommandV4::Commit(Box::new(value)),
            value.epoch,
            value.attempt_id,
        )
    }
);
submit_ffi!(
    termlink_direct_cancel_v4,
    TermLinkDirectControlV4,
    |bridge: &NativeBridgeV4, value| bridge.accept_control(value, false)
);
submit_ffi!(
    termlink_direct_network_changed_v4,
    TermLinkDirectControlV4,
    |bridge: &NativeBridgeV4, value| bridge.accept_control(value, true)
);
submit_ffi!(
    termlink_direct_submit_signature_v4,
    TermLinkDirectSignatureV4,
    |bridge: &NativeBridgeV4, value: TermLinkDirectSignatureV4| {
        if !valid_header(&value, value.abi_version, value.struct_size)
            || value.reserved0 != 0
            || value.reserved != [0; 2]
            || value.request_id == 0
            || value.signature_length == 0
            || value.signature_length as usize > MAX_SIGNATURE_BYTES_V4
            || value.signature[value.signature_length as usize..]
                .iter()
                .any(|v| *v != 0)
        {
            return -2;
        }
        if !bridge.accept_current(value.epoch, value.attempt_id) {
            return 0;
        }
        match bridge
            .signatures
            .as_ref()
            .expect("live bridge")
            .try_send(Box::new(value))
        {
            Ok(()) => 0,
            Err(TrySendError::Full(_)) => -4,
            Err(TrySendError::Disconnected(_)) => -1,
        }
    }
);
#[unsafe(no_mangle)]
/// Polls one V4 event without blocking.
///
/// # Safety
/// `handle` must be live and `output` must be writable for one `TermLinkDirectEventV4`.
pub unsafe extern "C" fn termlink_direct_poll_event_v4(
    handle: *mut c_void,
    output: *mut TermLinkDirectEventV4,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(bridge) = (unsafe { bridge_ref_v4(handle) }) else {
            return -1;
        };
        if output.is_null() {
            return -2;
        }
        let Some(event) = bridge.events.poll() else {
            return 0;
        };
        unsafe { output.write_unaligned(event) };
        1
    }))
    .unwrap_or(-3)
}
#[unsafe(no_mangle)]
/// Cancels optional network work and retires a V4 handle after its worker has exited.
///
/// # Safety
/// `handle` must be null or a live handle returned by `termlink_direct_create_v4`; a non-null
/// handle must not be used again after this call.
pub unsafe extern "C" fn termlink_direct_destroy_v4(handle: *mut c_void) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if !handle.is_null() {
            drop(unsafe { Box::from_raw(handle.cast::<NativeBridgeV4>()) });
        }
    }));
}
unsafe fn bridge_ref_v4<'a>(handle: *mut c_void) -> Option<&'a NativeBridgeV4> {
    if handle.is_null() {
        None
    } else {
        Some(unsafe { &*handle.cast::<NativeBridgeV4>() })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::net::UdpSocket;
    use std::sync::Barrier;

    fn active_ip() -> IpAddr {
        let socket = UdpSocket::bind("0.0.0.0:0").unwrap();
        socket.connect("192.0.2.1:9").unwrap();
        socket.local_addr().unwrap().ip()
    }

    fn stun_response(mapped: SocketAddr, transaction_id: [u8; 12]) -> Vec<u8> {
        let IpAddr::V4(ip) = mapped.ip() else {
            panic!("test requires IPv4")
        };
        let cookie = termlink_direct_nat::STUN_MAGIC_COOKIE.to_be_bytes();
        let mut response = Vec::from([0x01, 0x01, 0, 12]);
        response.extend_from_slice(&cookie);
        response.extend_from_slice(&transaction_id);
        response.extend_from_slice(&0x0020_u16.to_be_bytes());
        response.extend_from_slice(&8_u16.to_be_bytes());
        response.extend_from_slice(&[0, 1]);
        response.extend_from_slice(
            &(mapped.port() ^ (termlink_direct_nat::STUN_MAGIC_COOKIE >> 16) as u16).to_be_bytes(),
        );
        for (octet, mask) in ip.octets().into_iter().zip(cookie) {
            response.push(octet ^ mask);
        }
        response
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

    fn hex<const N: usize>(value: &str) -> [u8; N] {
        assert_eq!(value.len(), N * 2);
        let mut result = [0; N];
        for (index, byte) in result.iter_mut().enumerate() {
            *byte = u8::from_str_radix(&value[index * 2..index * 2 + 2], 16).unwrap();
        }
        result
    }

    fn test_candidate(endpoint: SocketAddr, id: u8) -> TermLinkDirectCandidateV4 {
        TermLinkDirectCandidateV4 {
            abi_version: ABI_VERSION_V4,
            struct_size: size_of::<TermLinkDirectCandidateV4>() as u32,
            candidate_id: [id; 16],
            endpoint: encode_socket(endpoint),
            kind: CANDIDATE_HOST_V4,
            carrier: CARRIER_UDP_QUIC_V4,
            priority: 100,
            expires_at_ms: 10_000,
            ..TermLinkDirectCandidateV4::default()
        }
    }

    fn test_pair(
        initiator: TermLinkDirectCandidateV4,
        responder: TermLinkDirectCandidateV4,
        rank: u32,
    ) -> TermLinkDirectCandidatePairV4 {
        TermLinkDirectCandidatePairV4 {
            abi_version: ABI_VERSION_V4,
            struct_size: size_of::<TermLinkDirectCandidatePairV4>() as u32,
            rank,
            initiator_candidate: initiator,
            responder_candidate: responder,
            ..TermLinkDirectCandidatePairV4::default()
        }
    }

    fn test_begin(
        role: u32,
        local_device_id: [u8; 16],
        peer_device_id: [u8; 16],
        send_key: [u8; 32],
        receive_key: [u8; 32],
        pair: TermLinkDirectCandidatePairV4,
    ) -> TermLinkDirectBeginChecksV4 {
        let mut pairs = [TermLinkDirectCandidatePairV4::default(); MAX_PAIRS_PER_BATCH_V4];
        pairs[0] = pair;
        TermLinkDirectBeginChecksV4 {
            abi_version: ABI_VERSION_V4,
            struct_size: size_of::<TermLinkDirectBeginChecksV4>() as u32,
            epoch: 1,
            attempt_id: [9; 16],
            connection_id: [10; 16],
            local_device_id,
            peer_device_id,
            now_ms: 1_000,
            timeout_ms: 5_000,
            role,
            pair_batch_seq: 1,
            pair_count: 1,
            total_pair_count: 1,
            plan_complete: 1,
            ticket_id: [11; 16],
            check_round_id: [12; 16],
            connectivity_nonce: [13; 16],
            local_send_probe_hmac_key: send_key,
            peer_receive_probe_hmac_key: receive_key,
            pairs,
            ..TermLinkDirectBeginChecksV4::default()
        }
    }

    fn test_check(
        socket: DirectUdpSocket,
        begin: TermLinkDirectBeginChecksV4,
        local: TermLinkDirectCandidateV4,
    ) -> CheckV4 {
        let mut check = CheckV4 {
            owner: OwnerV4 {
                epoch: begin.epoch,
                attempt_id: begin.attempt_id,
            },
            begin,
            identity: TemporaryIdentity::generate().unwrap(),
            local_candidates: vec![local],
            next_candidate_batch_seq: 2,
            gather_deadline: Instant::now() + Duration::from_secs(1),
            gathering_complete: false,
            network_cost: 0,
            candidate_expires_at_ms: 10_000,
            ticket_expires_at_ms: 10_000,
            pump: socket
                .into_multipair_pump(&[], Duration::from_secs(5))
                .unwrap(),
            pairs: HashMap::new(),
            pair_keys: HashSet::new(),
            validated: HashSet::new(),
            next_batch_seq: 1,
            total_pair_count: 0,
            plan_complete: false,
            nomination_committed: false,
        };
        append_initial_pairs_v4(&mut check).unwrap();
        check
    }

    fn ticket_vector_begin(
        role: u32,
        pair: TermLinkDirectCandidatePairV4,
    ) -> TermLinkDirectBeginChecksV4 {
        let initiator = hex("20314253647548698a9bacbdcedff001");
        let responder = hex("304152637485496a8b9cadbecfe0f102");
        let (local, peer, send_key, receive_key) = if role == ROLE_INITIATOR_V4 {
            (initiator, responder, [3; 32], [4; 32])
        } else {
            (responder, initiator, [4; 32], [3; 32])
        };
        let ticket = base64url(
            "CAESEAARIjNEVUZ3iJmqu8zd7v8aEBAhMkNUZUdoiZqrvM3e7_AiECAxQlNkdUhpipusvc7f8AEqEDBBUmN0hUlqi5ytvs_g8QIyEEBRYnOElUprjJ2uv9Dh8gM4B0CA8MyGpjdIwJnUhqY3UgEBWhAAAQIDBAUGBwgJCgsMDQ4PYhBkaXJlY3QtdjEtdmVjdG9yakCwJTg3vrjJ8h6M6z4BR3XkogSgH3TQI4jp2mBhQTUyJWcO36MFe3MVh3Zxk-uAFShR9t2MynoQJEk4LrR53KkI",
        );
        let mut begin = test_begin(role, local, peer, send_key, receive_key, pair);
        begin.epoch = 7;
        begin.connection_id = hex("4051627384954a6b8c9daebfd0e1f203");
        begin.now_ms = 1_900_000_060_000;
        begin.timeout_ms = 5_000;
        begin.ticket_length = ticket.len() as u32;
        begin.ticket[..ticket.len()].copy_from_slice(&ticket);
        begin.server_signing_public_key = base64url("A6EHv_POEL4dcN0Y50vAmWfk1jCbpQ1fHdyGZBJVMbg")
            .try_into()
            .unwrap();
        begin.ticket_id = hex("00112233445546778899aabbccddeeff");
        begin
    }

    fn ticket_vector_commit(
        begin: TermLinkDirectBeginChecksV4,
        pair: TermLinkDirectCandidatePairV4,
        peer_certificate_sha256: [u8; 32],
    ) -> TermLinkDirectCommitNominationV4 {
        let mut local_proof_nonce = [0; MAX_PROOF_NONCE_BYTES_V4];
        local_proof_nonce[..MIN_PROOF_NONCE_BYTES_V4].fill(0xe0);
        TermLinkDirectCommitNominationV4 {
            abi_version: ABI_VERSION_V4,
            struct_size: size_of::<TermLinkDirectCommitNominationV4>() as u32,
            epoch: begin.epoch,
            attempt_id: begin.attempt_id,
            check_round_id: begin.check_round_id,
            nomination_id: [14; 16],
            winning_pair: pair,
            role: begin.role,
            timeout_ms: 5_000,
            selection_confirmed: 1,
            local_proof_nonce_length: MIN_PROOF_NONCE_BYTES_V4 as u32,
            peer_certificate_sha256,
            peer_device_signing_public_key: [0x55; 32],
            local_proof_nonce,
            now_ms: begin.now_ms,
            reserved: [0; 4],
        }
    }
    #[test]
    fn v4_layouts_match_header() {
        assert_eq!(size_of::<TermLinkDirectCreateV4>(), 32);
        assert_eq!(size_of::<TermLinkSocketAddrV4>(), 48);
        assert_eq!(size_of::<TermLinkDirectGatherV4>(), 256);
        assert_eq!(size_of::<TermLinkDirectCandidateV4>(), 128);
        assert_eq!(size_of::<TermLinkDirectCandidateBatchV4>(), 1088);
        assert_eq!(size_of::<TermLinkDirectCandidatePairV4>(), 288);
        assert_eq!(size_of::<TermLinkDirectBeginChecksV4>(), 4656);
        assert_eq!(size_of::<TermLinkDirectAppendCheckPairsV4>(), 2400);
        assert_eq!(size_of::<TermLinkDirectCommitNominationV4>(), 504);
        assert_eq!(size_of::<TermLinkDirectControlV4>(), 48);
        assert_eq!(size_of::<TermLinkDirectSignatureV4>(), 192);
        assert_eq!(size_of::<TermLinkDirectOpaqueBytesV4>(), 2080);
        assert_eq!(size_of::<TermLinkDirectPairValidatedV4>(), 360);
        assert_eq!(size_of::<TermLinkDirectChecksCompleteV4>(), 80);
        assert_eq!(size_of::<TermLinkDirectNominationReadyV4>(), 376);
        assert_eq!(size_of::<TermLinkDirectShadowReadyV4>(), 400);
        assert_eq!(size_of::<TermLinkDirectEventPayloadV4>(), 2080);
        assert_eq!(size_of::<TermLinkDirectEventV4>(), 2224);
        assert_eq!(
            std::mem::offset_of!(TermLinkDirectBeginChecksV4, pairs),
            2320
        );
        assert_eq!(std::mem::offset_of!(TermLinkDirectEventV4, payload), 112);
    }

    #[test]
    fn request_ids_do_not_consume_visible_event_generations() {
        let events = EventQueueV4::new(8);
        assert!(events.push(base_event(
            EVENT_STATE_CHANGED_V4,
            STATE_GATHERING_V4,
            1,
            [1; 16]
        )));
        assert_eq!(events.next_request_id(), 1);
        assert!(events.push(base_event(
            EVENT_SIGN_REQUEST_V4,
            STATE_AUTHENTICATING_V4,
            1,
            [1; 16]
        )));
        assert_eq!(events.poll().unwrap().generation, 1);
        assert_eq!(events.poll().unwrap().generation, 2);
    }

    #[test]
    fn full_queue_failure_prefers_the_same_owner_without_a_generation_gap() {
        let events = EventQueueV4::new(2);
        assert!(events.push(base_event(
            EVENT_STATE_CHANGED_V4,
            STATE_GATHERING_V4,
            1,
            [1; 16]
        )));
        assert!(events.push(base_event(
            EVENT_STATE_CHANGED_V4,
            STATE_GATHERING_V4,
            2,
            [2; 16]
        )));
        events.force_failure(1, [1; 16], REASON_QUEUE_FULL_V4);
        let failure = events.poll().unwrap();
        let successor = events.poll().unwrap();
        assert_eq!(failure.kind, EVENT_DIRECT_FAILED_V4);
        assert_eq!(failure.epoch, 1);
        assert_eq!(failure.generation, 1);
        assert_eq!(successor.epoch, 2);
        assert_eq!(successor.generation, 2);
    }

    #[test]
    fn successor_reclassifies_queued_predecessor_events_without_generation_gaps() {
        let events = EventQueueV4::new(4);
        assert!(events.push(base_event(
            EVENT_IDENTITY_READY_V4,
            STATE_GATHERING_V4,
            1,
            [1; 16]
        )));
        assert!(events.push(base_event(
            EVENT_CANDIDATE_BATCH_V4,
            STATE_GATHERING_V4,
            1,
            [1; 16]
        )));
        let successor = OwnerV4 {
            epoch: 2,
            attempt_id: [2; 16],
        };
        events.retire_predecessor_events(successor);
        assert!(events.push(base_event(
            EVENT_IDENTITY_READY_V4,
            STATE_GATHERING_V4,
            successor.epoch,
            successor.attempt_id,
        )));

        for generation in 1..=2 {
            let stale = events.poll().unwrap();
            assert_eq!(stale.kind, EVENT_IGNORED_STALE_V4);
            assert_eq!(stale.epoch, 1);
            assert_eq!(stale.attempt_id, [1; 16]);
            assert_eq!(stale.request_id, successor.epoch);
            assert_eq!(stale.generation, generation);
        }
        let current = events.poll().unwrap();
        assert_eq!(current.kind, EVENT_IDENTITY_READY_V4);
        assert_eq!(current.epoch, successor.epoch);
        assert_eq!(current.generation, 3);
    }

    #[test]
    fn full_predecessor_queue_still_publishes_successor_failure_contiguously() {
        let events = EventQueueV4::new(2);
        for state in [STATE_GATHERING_V4, STATE_CHECKING_V4] {
            assert!(events.push(base_event(EVENT_STATE_CHANGED_V4, state, 1, [1; 16])));
        }
        events.force_failure(2, [2; 16], REASON_QUEUE_FULL_V4);

        let predecessor = events.poll().unwrap();
        assert_eq!(predecessor.epoch, 1);
        assert_eq!(predecessor.generation, 1);
        let failure = events.poll().unwrap();
        assert_eq!(failure.kind, EVENT_DIRECT_FAILED_V4);
        assert_eq!(failure.epoch, 2);
        assert_eq!(failure.attempt_id, [2; 16]);
        assert_eq!(failure.reason, REASON_QUEUE_FULL_V4);
        assert_eq!(failure.generation, 2);
    }

    #[test]
    fn terminal_cancel_fence_rejects_every_later_progress_event() {
        let expected = OwnerV4 {
            epoch: 7,
            attempt_id: [7; 16],
        };
        let owner = Mutex::new(expected);
        let cancel = AtomicU64::new(0);
        let events = EventQueueV4::new(4);
        assert!(push_if_active(
            &events,
            &owner,
            &cancel,
            expected,
            base_event(
                EVENT_STATE_CHANGED_V4,
                STATE_GATHERING_V4,
                expected.epoch,
                expected.attempt_id,
            ),
        ));
        cancel.store(expected.epoch, Ordering::Release);
        force_failure_if_current(&events, &owner, expected, REASON_CANCELLED_V4);
        assert!(!push_if_active(
            &events,
            &owner,
            &cancel,
            expected,
            base_event(
                EVENT_STATE_CHANGED_V4,
                STATE_CHECKING_V4,
                expected.epoch,
                expected.attempt_id,
            ),
        ));

        assert_eq!(events.poll().unwrap().generation, 1);
        let failure = events.poll().unwrap();
        assert_eq!(failure.kind, EVENT_DIRECT_FAILED_V4);
        assert_eq!(failure.reason, REASON_CANCELLED_V4);
        assert_eq!(failure.generation, 2);
        assert!(events.poll().is_none());
    }

    #[test]
    fn concurrent_cancel_linearizes_before_or_after_progress_but_never_between_failure_and_progress()
     {
        for iteration in 1..=64_u64 {
            let expected = OwnerV4 {
                epoch: iteration,
                attempt_id: [iteration as u8; 16],
            };
            let owner = Arc::new(Mutex::new(expected));
            let cancel = Arc::new(AtomicU64::new(0));
            let events = Arc::new(EventQueueV4::new(4));
            let barrier = Arc::new(Barrier::new(3));

            let progress = {
                let owner = Arc::clone(&owner);
                let cancel = Arc::clone(&cancel);
                let events = Arc::clone(&events);
                let barrier = Arc::clone(&barrier);
                thread::spawn(move || {
                    barrier.wait();
                    push_if_active(
                        &events,
                        &owner,
                        &cancel,
                        expected,
                        base_event(
                            EVENT_STATE_CHANGED_V4,
                            STATE_CHECKING_V4,
                            expected.epoch,
                            expected.attempt_id,
                        ),
                    )
                })
            };
            let terminal = {
                let owner = Arc::clone(&owner);
                let cancel = Arc::clone(&cancel);
                let events = Arc::clone(&events);
                let barrier = Arc::clone(&barrier);
                thread::spawn(move || {
                    barrier.wait();
                    let current = owner.lock().unwrap_or_else(|e| e.into_inner());
                    assert_eq!(*current, expected);
                    cancel.store(expected.epoch, Ordering::Release);
                    events.force_failure(expected.epoch, expected.attempt_id, REASON_CANCELLED_V4);
                })
            };
            barrier.wait();
            let progress_won = progress.join().unwrap();
            terminal.join().unwrap();

            let mut observed = Vec::new();
            while let Some(event) = events.poll() {
                observed.push(event);
            }
            assert_eq!(
                observed.last().map(|event| event.kind),
                Some(EVENT_DIRECT_FAILED_V4)
            );
            assert_eq!(observed.len(), if progress_won { 2 } else { 1 });
            for (index, event) in observed.iter().enumerate() {
                assert_eq!(event.generation, index as u64 + 1);
            }
        }
    }

    #[test]
    fn concurrent_successor_install_never_leaks_a_predecessor_progress_event() {
        for iteration in 1..=64_u64 {
            let predecessor = OwnerV4 {
                epoch: iteration,
                attempt_id: [1; 16],
            };
            let successor = OwnerV4 {
                epoch: iteration + 1,
                attempt_id: [2; 16],
            };
            let owner = Arc::new(Mutex::new(predecessor));
            let cancel = Arc::new(AtomicU64::new(0));
            let events = Arc::new(EventQueueV4::new(4));
            let barrier = Arc::new(Barrier::new(3));

            let publisher = {
                let owner = Arc::clone(&owner);
                let cancel = Arc::clone(&cancel);
                let events = Arc::clone(&events);
                let barrier = Arc::clone(&barrier);
                thread::spawn(move || {
                    barrier.wait();
                    push_if_active(
                        &events,
                        &owner,
                        &cancel,
                        predecessor,
                        base_event(
                            EVENT_STATE_CHANGED_V4,
                            STATE_GATHERING_V4,
                            predecessor.epoch,
                            predecessor.attempt_id,
                        ),
                    );
                })
            };
            let installer = {
                let owner = Arc::clone(&owner);
                let events = Arc::clone(&events);
                let barrier = Arc::clone(&barrier);
                thread::spawn(move || {
                    barrier.wait();
                    let mut current = owner.lock().unwrap_or_else(|e| e.into_inner());
                    *current = successor;
                    events.retire_predecessor_events(successor);
                })
            };
            barrier.wait();
            publisher.join().unwrap();
            installer.join().unwrap();
            assert!(push_if_active(
                &events,
                &owner,
                &cancel,
                successor,
                base_event(
                    EVENT_IDENTITY_READY_V4,
                    STATE_GATHERING_V4,
                    successor.epoch,
                    successor.attempt_id,
                ),
            ));

            let mut generation = 0;
            while let Some(event) = events.poll() {
                generation += 1;
                assert_eq!(event.generation, generation);
                if event.epoch == predecessor.epoch {
                    assert_eq!(event.kind, EVENT_IGNORED_STALE_V4);
                } else {
                    assert_eq!(event.epoch, successor.epoch);
                }
            }
            assert!(generation >= 1);
        }
    }

    #[test]
    fn network_change_during_signature_wait_is_the_only_terminal_event() {
        let ip = active_ip();
        let local_socket = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let peer_socket = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let local_address = local_socket.bound_addr();
        let peer_address = peer_socket.bound_addr();
        let mut local_candidate = test_candidate(local_address, 1);
        let mut peer_candidate = test_candidate(peer_address, 2);
        local_candidate.expires_at_ms = 1_900_000_110_000;
        peer_candidate.expires_at_ms = 1_900_000_110_000;
        let pair = test_pair(local_candidate, peer_candidate, 1);
        let begin = ticket_vector_begin(ROLE_INITIATOR_V4, pair);
        let expected = OwnerV4 {
            epoch: begin.epoch,
            attempt_id: begin.attempt_id,
        };
        let local_identity = TemporaryIdentity::generate().unwrap();
        let local_fingerprint = local_identity.certificate_sha256();
        let peer_identity = TemporaryIdentity::generate().unwrap();
        let peer_fingerprint = peer_identity.certificate_sha256();
        let commit = ticket_vector_commit(begin, pair, peer_fingerprint);

        let (peer_ready_tx, peer_ready_rx) = mpsc::sync_channel(1);
        let (peer_stop_tx, peer_stop_rx) = mpsc::sync_channel(1);
        let peer = thread::spawn(move || {
            let runtime = tokio::runtime::Builder::new_current_thread()
                .enable_all()
                .build()
                .unwrap();
            runtime.block_on(async move {
                let endpoint = ShadowEndpoint::server(
                    peer_socket.into_quic_handoff(),
                    local_address,
                    peer_identity,
                    local_fingerprint,
                )
                .unwrap();
                peer_ready_tx.send(()).unwrap();
                let connection = endpoint
                    .accept_pinned(local_address, local_fingerprint)
                    .await
                    .unwrap();
                let _ = peer_stop_rx.recv_timeout(Duration::from_secs(1));
                drop(connection);
                endpoint.close();
            });
        });
        peer_ready_rx.recv_timeout(Duration::from_secs(1)).unwrap();

        let events = Arc::new(EventQueueV4::new(16));
        let owner = Arc::new(Mutex::new(expected));
        let cancel = Arc::new(AtomicU64::new(0));
        let network = Arc::new(AtomicU64::new(0));
        let relay_only = Arc::new(AtomicBool::new(true));
        let (_signature_tx, signature_rx) = mpsc::sync_channel(1);
        let (result_tx, result_rx) = mpsc::sync_channel(1);
        let worker = {
            let events = Arc::clone(&events);
            let owner = Arc::clone(&owner);
            let cancel = Arc::clone(&cancel);
            let network = Arc::clone(&network);
            let relay_only = Arc::clone(&relay_only);
            thread::spawn(move || {
                let result = authenticate_quic_v4(
                    TermLinkDirectCreateV4 {
                        abi_version: ABI_VERSION_V4,
                        struct_size: size_of::<TermLinkDirectCreateV4>() as u32,
                        enabled: 1,
                        queue_capacity: 16,
                        platform: PLATFORM_WINDOWS_V4,
                        reserved: [0; 3],
                    },
                    begin,
                    local_identity,
                    &commit,
                    local_socket.into_quic_handoff(),
                    &signature_rx,
                    &events,
                    &cancel,
                    &network,
                    &owner,
                    &relay_only,
                );
                if let Err(reason) = result {
                    // Mirrors the worker's Commit error path. The already-published
                    // network terminal must not be rewritten as Cancelled.
                    force_failure_if_current(&events, &owner, expected, reason);
                }
                result_tx.send(result).unwrap();
            })
        };

        let deadline = Instant::now() + Duration::from_secs(2);
        let mut observed = Vec::new();
        loop {
            if let Some(event) = events.poll() {
                let is_sign_request = event.kind == EVENT_SIGN_REQUEST_V4;
                observed.push(event);
                if is_sign_request {
                    break;
                }
            }
            assert!(Instant::now() < deadline, "SIGN_REQUEST was not reached");
            thread::yield_now();
        }

        let cancel_started = Instant::now();
        {
            let current = owner.lock().unwrap_or_else(|error| error.into_inner());
            assert_eq!(*current, expected);
            cancel.fetch_max(expected.epoch, Ordering::AcqRel);
            network.fetch_max(expected.epoch, Ordering::AcqRel);
            relay_only.store(true, Ordering::Release);
            events.force_failure(
                expected.epoch,
                expected.attempt_id,
                REASON_NETWORK_CHANGED_V4,
            );
        }
        assert_eq!(
            result_rx.recv_timeout(Duration::from_millis(500)).unwrap(),
            Err(REASON_NETWORK_CHANGED_V4)
        );
        assert!(cancel_started.elapsed() < Duration::from_millis(500));
        worker.join().unwrap();
        let _ = peer_stop_tx.send(());
        peer.join().unwrap();
        while let Some(event) = events.poll() {
            observed.push(event);
        }

        let terminals = observed
            .iter()
            .filter(|event| event.kind == EVENT_DIRECT_FAILED_V4)
            .collect::<Vec<_>>();
        assert_eq!(terminals.len(), 1);
        assert_eq!(terminals[0].reason, REASON_NETWORK_CHANGED_V4);
        let terminal_index = observed
            .iter()
            .position(|event| event.kind == EVENT_DIRECT_FAILED_V4)
            .unwrap();
        assert_eq!(terminal_index + 1, observed.len());
        assert!(relay_only.load(Ordering::Acquire));
        for (index, event) in observed.iter().enumerate() {
            assert_eq!(event.generation, index as u64 + 1);
            assert_eq!(event.epoch, expected.epoch);
            assert_eq!(event.attempt_id, expected.attempt_id);
        }
    }

    #[test]
    fn quic_blackhole_is_preempted_and_successor_host_is_bounded() {
        let ip = active_ip();
        let bridge = NativeBridgeV4::create(TermLinkDirectCreateV4 {
            abi_version: ABI_VERSION_V4,
            struct_size: size_of::<TermLinkDirectCreateV4>() as u32,
            enabled: 1,
            queue_capacity: 16,
            platform: PLATFORM_WINDOWS_V4,
            reserved: [0; 3],
        })
        .unwrap();
        let auth_socket = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let blackhole = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let local_address = auth_socket.bound_addr();
        let blackhole_address = blackhole.local_addr().unwrap();
        let mut local_candidate = test_candidate(local_address, 1);
        let mut peer_candidate = test_candidate(blackhole_address, 2);
        local_candidate.expires_at_ms = 1_900_000_110_000;
        peer_candidate.expires_at_ms = 1_900_000_110_000;
        let pair = test_pair(local_candidate, peer_candidate, 1);
        let begin = ticket_vector_begin(ROLE_INITIATOR_V4, pair);
        let predecessor = OwnerV4 {
            epoch: begin.epoch,
            attempt_id: begin.attempt_id,
        };
        let identity = TemporaryIdentity::generate().unwrap();
        let commit = ticket_vector_commit(begin, pair, [0x44; 32]);
        let encoded = encode_socket(SocketAddr::new(ip, 0));
        let predecessor_gather = TermLinkDirectGatherV4 {
            abi_version: ABI_VERSION_V4,
            struct_size: size_of::<TermLinkDirectGatherV4>() as u32,
            epoch: predecessor.epoch,
            attempt_id: predecessor.attempt_id,
            local_bind: encoded,
            advertised_local_ip: encoded.ip,
            advertised_local_ip_length: if ip.is_ipv4() { 4 } else { 16 },
            gather_timeout_ms: 100,
            candidate_expires_at_ms: 1_900_000_110_000,
            ..TermLinkDirectGatherV4::default()
        };
        assert_eq!(bridge.accept_gather(predecessor_gather), 0);
        let (authentication_finished_tx, authentication_finished_rx) = mpsc::sync_channel(1);
        assert_eq!(
            bridge.submit(
                CommandV4::AuthenticateForTest {
                    begin: Box::new(begin),
                    identity,
                    commit: Box::new(commit),
                    handoff: auth_socket.into_quic_handoff(),
                    completion: authentication_finished_tx,
                },
                predecessor.epoch,
                predecessor.attempt_id,
            ),
            0
        );

        let deadline = Instant::now() + Duration::from_secs(2);
        let mut observed = Vec::new();
        loop {
            if let Some(event) = bridge.events.poll() {
                let nomination_ready = event.kind == EVENT_NOMINATION_READY_V4;
                observed.push(event);
                if nomination_ready {
                    break;
                }
            }
            assert!(
                Instant::now() < deadline,
                "blackholed QUIC flow did not reach NominationReady"
            );
            thread::yield_now();
        }

        let successor = OwnerV4 {
            epoch: predecessor.epoch + 1,
            attempt_id: [0x22; 16],
        };
        let successor_started = Instant::now();
        let successor_gather = TermLinkDirectGatherV4 {
            abi_version: ABI_VERSION_V4,
            struct_size: size_of::<TermLinkDirectGatherV4>() as u32,
            epoch: successor.epoch,
            attempt_id: successor.attempt_id,
            local_bind: encoded,
            advertised_local_ip: encoded.ip,
            advertised_local_ip_length: if ip.is_ipv4() { 4 } else { 16 },
            gather_timeout_ms: 100,
            candidate_expires_at_ms: 1_900_000_110_000,
            ..TermLinkDirectGatherV4::default()
        };
        // This is the production successor path: it atomically installs the
        // owner, cancels the predecessor, then queues Gather behind the
        // blackholed AuthenticateForTest command on NativeBridgeV4's one worker.
        assert_eq!(bridge.accept_gather(successor_gather), 0);
        assert_eq!(
            authentication_finished_rx
                .recv_timeout(Duration::from_millis(500))
                .unwrap(),
            REASON_CANCELLED_V4
        );
        assert!(successor_started.elapsed() < Duration::from_millis(500));

        let host_deadline = successor_started + Duration::from_millis(700);
        let mut successor_host = false;
        while Instant::now() < host_deadline && !successor_host {
            if let Some(event) = bridge.events.poll() {
                if event.epoch == predecessor.epoch {
                    assert_eq!(event.kind, EVENT_IGNORED_STALE_V4);
                }
                assert!(
                    event.epoch != predecessor.epoch
                        || !matches!(event.kind, EVENT_SIGN_REQUEST_V4 | EVENT_SHADOW_READY_V4),
                    "predecessor progressed after successor installation"
                );
                if event.epoch == successor.epoch && event.kind == EVENT_CANDIDATE_BATCH_V4 {
                    // SAFETY: Candidate-batch events initialize this union member.
                    let batch = unsafe { event.payload.candidate_batch };
                    successor_host = batch.candidates[..batch.candidate_count as usize]
                        .iter()
                        .any(|candidate| candidate.kind == CANDIDATE_HOST_V4);
                }
                observed.push(event);
            } else {
                thread::yield_now();
            }
        }
        assert!(
            successor_host,
            "successor HOST was not published within 700ms"
        );
        assert!(successor_started.elapsed() < Duration::from_millis(700));
        assert!(bridge.relay_only.load(Ordering::Acquire));
        assert_eq!(bridge.owner(), successor);
        let mut previous_generation = 0;
        for event in &observed {
            assert_eq!(event.generation, previous_generation + 1);
            previous_generation = event.generation;
        }
        drop(blackhole);
    }

    #[test]
    fn ffi_destroy_preempts_gather_blackhole_and_joins_worker() {
        let ip = active_ip();
        let blackhole = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let config = TermLinkDirectCreateV4 {
            abi_version: ABI_VERSION_V4,
            struct_size: size_of::<TermLinkDirectCreateV4>() as u32,
            enabled: 1,
            queue_capacity: 8,
            platform: PLATFORM_WINDOWS_V4,
            reserved: [0; 3],
        };
        // SAFETY: `config` remains readable for the duration of this call.
        let handle = unsafe { termlink_direct_create_v4(&raw const config) };
        assert!(!handle.is_null());
        // SAFETY: `handle` is live until the destroy call below.
        let worker_exited = Arc::clone(&unsafe { bridge_ref_v4(handle) }.unwrap().worker_exited);
        let encoded = encode_socket(SocketAddr::new(ip, 0));
        let mut gather = TermLinkDirectGatherV4 {
            abi_version: ABI_VERSION_V4,
            struct_size: size_of::<TermLinkDirectGatherV4>() as u32,
            epoch: 1,
            attempt_id: [1; 16],
            local_bind: encoded,
            advertised_local_ip: encoded.ip,
            advertised_local_ip_length: if ip.is_ipv4() { 4 } else { 16 },
            stun_count: 1,
            gather_timeout_ms: 5_000,
            candidate_expires_at_ms: 10_000,
            ..TermLinkDirectGatherV4::default()
        };
        gather.stun_servers[0] = encode_socket(blackhole.local_addr().unwrap());
        // SAFETY: the handle is live and `gather` is readable during this call.
        assert_eq!(
            unsafe { termlink_direct_gather_v4(handle, &raw const gather) },
            0
        );

        let deadline = Instant::now() + Duration::from_secs(2);
        loop {
            let mut event = TermLinkDirectEventV4::default();
            // SAFETY: the handle is live and `event` is writable during this call.
            let status = unsafe { termlink_direct_poll_event_v4(handle, &raw mut event) };
            if status == 1 && event.kind == EVENT_IDENTITY_READY_V4 {
                break;
            }
            assert!(
                Instant::now() < deadline,
                "Gather did not enter native work"
            );
            thread::yield_now();
        }

        let started = Instant::now();
        // SAFETY: this consumes the live handle exactly once.
        unsafe { termlink_direct_destroy_v4(handle) };
        assert!(started.elapsed() < Duration::from_millis(500));
        assert!(worker_exited.load(Ordering::Acquire));
    }

    #[test]
    fn destroy_preempts_quic_blackhole_and_joins_worker() {
        let ip = active_ip();
        let bridge = NativeBridgeV4::create(TermLinkDirectCreateV4 {
            abi_version: ABI_VERSION_V4,
            struct_size: size_of::<TermLinkDirectCreateV4>() as u32,
            enabled: 1,
            queue_capacity: 16,
            platform: PLATFORM_WINDOWS_V4,
            reserved: [0; 3],
        })
        .unwrap();
        let worker_exited = Arc::clone(&bridge.worker_exited);
        let auth_socket = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let blackhole = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let mut local = test_candidate(auth_socket.bound_addr(), 1);
        let mut remote = test_candidate(blackhole.local_addr().unwrap(), 2);
        local.expires_at_ms = 1_900_000_110_000;
        remote.expires_at_ms = 1_900_000_110_000;
        let pair = test_pair(local, remote, 1);
        let begin = ticket_vector_begin(ROLE_INITIATOR_V4, pair);
        let expected = OwnerV4 {
            epoch: begin.epoch,
            attempt_id: begin.attempt_id,
        };
        let commit = ticket_vector_commit(begin, pair, [0x44; 32]);
        let encoded = encode_socket(SocketAddr::new(ip, 0));
        assert_eq!(
            bridge.accept_gather(TermLinkDirectGatherV4 {
                abi_version: ABI_VERSION_V4,
                struct_size: size_of::<TermLinkDirectGatherV4>() as u32,
                epoch: expected.epoch,
                attempt_id: expected.attempt_id,
                local_bind: encoded,
                advertised_local_ip: encoded.ip,
                advertised_local_ip_length: if ip.is_ipv4() { 4 } else { 16 },
                gather_timeout_ms: 100,
                candidate_expires_at_ms: 1_900_000_110_000,
                ..TermLinkDirectGatherV4::default()
            }),
            0
        );
        let (completion_tx, completion_rx) = mpsc::sync_channel(1);
        assert_eq!(
            bridge.submit(
                CommandV4::AuthenticateForTest {
                    begin: Box::new(begin),
                    identity: TemporaryIdentity::generate().unwrap(),
                    commit: Box::new(commit),
                    handoff: auth_socket.into_quic_handoff(),
                    completion: completion_tx,
                },
                expected.epoch,
                expected.attempt_id,
            ),
            0
        );
        let deadline = Instant::now() + Duration::from_secs(2);
        loop {
            if bridge
                .events
                .poll()
                .is_some_and(|event| event.kind == EVENT_NOMINATION_READY_V4)
            {
                break;
            }
            assert!(Instant::now() < deadline, "QUIC blackhole was not reached");
            thread::yield_now();
        }

        let started = Instant::now();
        drop(bridge);
        assert!(started.elapsed() < Duration::from_millis(500));
        assert_eq!(completion_rx.try_recv().unwrap(), REASON_CANCELLED_V4);
        assert!(worker_exited.load(Ordering::Acquire));
    }

    #[test]
    fn destroy_preempts_signature_wait_and_joins_worker() {
        let ip = active_ip();
        let bridge = NativeBridgeV4::create(TermLinkDirectCreateV4 {
            abi_version: ABI_VERSION_V4,
            struct_size: size_of::<TermLinkDirectCreateV4>() as u32,
            enabled: 1,
            queue_capacity: 8,
            platform: PLATFORM_WINDOWS_V4,
            reserved: [0; 3],
        })
        .unwrap();
        let worker_exited = Arc::clone(&bridge.worker_exited);
        let expected = OwnerV4 {
            epoch: 1,
            attempt_id: [1; 16],
        };
        let encoded = encode_socket(SocketAddr::new(ip, 0));
        assert_eq!(
            bridge.accept_gather(TermLinkDirectGatherV4 {
                abi_version: ABI_VERSION_V4,
                struct_size: size_of::<TermLinkDirectGatherV4>() as u32,
                epoch: expected.epoch,
                attempt_id: expected.attempt_id,
                local_bind: encoded,
                advertised_local_ip: encoded.ip,
                advertised_local_ip_length: if ip.is_ipv4() { 4 } else { 16 },
                gather_timeout_ms: 100,
                candidate_expires_at_ms: 10_000,
                ..TermLinkDirectGatherV4::default()
            }),
            0
        );
        let (entered_tx, entered_rx) = mpsc::sync_channel(1);
        let (completion_tx, completion_rx) = mpsc::sync_channel(1);
        assert_eq!(
            bridge.submit(
                CommandV4::WaitForSignatureForTest {
                    expected,
                    request_id: 1,
                    entered: entered_tx,
                    completion: completion_tx,
                },
                expected.epoch,
                expected.attempt_id,
            ),
            0
        );
        entered_rx.recv_timeout(Duration::from_secs(2)).unwrap();

        let started = Instant::now();
        drop(bridge);
        assert!(started.elapsed() < Duration::from_millis(500));
        assert_eq!(completion_rx.try_recv().unwrap(), REASON_CANCELLED_V4);
        assert!(worker_exited.load(Ordering::Acquire));
    }

    #[test]
    fn disabled_handle_performs_no_socket_io() {
        let bridge = NativeBridgeV4::create(TermLinkDirectCreateV4 {
            abi_version: 4,
            struct_size: 32,
            enabled: 0,
            queue_capacity: 8,
            platform: PLATFORM_WINDOWS_V4,
            reserved: [0; 3],
        })
        .unwrap();
        let gather = TermLinkDirectGatherV4 {
            abi_version: 4,
            struct_size: 256,
            epoch: 1,
            attempt_id: [1; 16],
            local_bind: encode_socket("192.0.2.1:0".parse().unwrap()),
            advertised_local_ip: {
                let mut v = [0; 16];
                v[..4].copy_from_slice(&[192, 0, 2, 1]);
                v
            },
            advertised_local_ip_length: 4,
            gather_timeout_ms: 100,
            candidate_expires_at_ms: 10_000,
            ..TermLinkDirectGatherV4::default()
        };
        assert_eq!(bridge.accept_gather(gather), 0);
        let deadline = Instant::now() + Duration::from_secs(1);
        loop {
            if let Some(event) = bridge.events.poll()
                && event.kind == EVENT_DIRECT_FAILED_V4
            {
                assert_eq!(event.reason, REASON_DISABLED_V4);
                break;
            }
            assert!(Instant::now() < deadline);
            thread::yield_now();
        }
    }
    #[test]
    fn candidate_pair_token_is_orientation_sensitive() {
        let a = CandidatePair {
            initiator_candidate_id: [1; 16],
            responder_candidate_id: [2; 16],
        };
        let b = CandidatePair {
            initiator_candidate_id: [2; 16],
            responder_candidate_id: [1; 16],
        };
        assert_ne!(pair_token(&a), pair_token(&b));
    }

    #[test]
    fn host_candidate_is_published_before_stun_blackhole_waits() {
        let ip = active_ip();
        let blackhole = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let bridge = NativeBridgeV4::create(TermLinkDirectCreateV4 {
            abi_version: 4,
            struct_size: 32,
            enabled: 1,
            queue_capacity: 8,
            platform: PLATFORM_WINDOWS_V4,
            reserved: [0; 3],
        })
        .unwrap();
        let mut gather = TermLinkDirectGatherV4 {
            abi_version: 4,
            struct_size: 256,
            epoch: 1,
            attempt_id: [1; 16],
            local_bind: encode_socket(SocketAddr::new(ip, 0)),
            advertised_local_ip: encode_socket(SocketAddr::new(ip, 0)).ip,
            advertised_local_ip_length: if ip.is_ipv4() { 4 } else { 16 },
            stun_count: 1,
            gather_timeout_ms: 1_000,
            candidate_expires_at_ms: 10_000,
            ..TermLinkDirectGatherV4::default()
        };
        gather.stun_servers[0] = encode_socket(blackhole.local_addr().unwrap());
        let started = Instant::now();
        assert_eq!(bridge.accept_gather(gather), 0);
        loop {
            let event = bridge.events.poll();
            if let Some(event) = event
                && event.kind == EVENT_CANDIDATE_BATCH_V4
            {
                // SAFETY: This event kind initializes candidate_batch.
                let batch = unsafe { event.payload.candidate_batch };
                assert_eq!(batch.candidate_count, 1);
                assert_eq!(batch.gathering_complete, 0);
                assert_eq!(batch.candidates[0].kind, CANDIDATE_HOST_V4);
                assert!(started.elapsed() < Duration::from_millis(700));
                break;
            }
            assert!(started.elapsed() < Duration::from_secs(1));
            thread::yield_now();
        }
    }

    #[test]
    fn delayed_stun_after_final_gather_batch_is_not_published() {
        let ip = active_ip();
        assert!(ip.is_ipv4());
        let stun = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let stun_address = stun.local_addr().unwrap();
        let responder = thread::spawn(move || {
            let mut request = [0_u8; 64];
            let (_, source) = stun.recv_from(&mut request).unwrap();
            let transaction_id = request[8..20].try_into().unwrap();
            thread::sleep(Duration::from_millis(250));
            stun.send_to(&stun_response(source, transaction_id), source)
                .unwrap();
        });
        let bridge = NativeBridgeV4::create(TermLinkDirectCreateV4 {
            abi_version: ABI_VERSION_V4,
            struct_size: size_of::<TermLinkDirectCreateV4>() as u32,
            enabled: 1,
            queue_capacity: 16,
            platform: PLATFORM_WINDOWS_V4,
            reserved: [0; 3],
        })
        .unwrap();
        let encoded = encode_socket(SocketAddr::new(ip, 0));
        let mut gather = TermLinkDirectGatherV4 {
            abi_version: ABI_VERSION_V4,
            struct_size: size_of::<TermLinkDirectGatherV4>() as u32,
            epoch: 1,
            attempt_id: [1; 16],
            local_bind: encoded,
            advertised_local_ip: encoded.ip,
            advertised_local_ip_length: 4,
            stun_count: 1,
            gather_timeout_ms: 100,
            candidate_expires_at_ms: 10_000,
            ..TermLinkDirectGatherV4::default()
        };
        gather.stun_servers[0] = encode_socket(stun_address);
        assert_eq!(bridge.accept_gather(gather), 0);

        let deadline = Instant::now() + Duration::from_secs(1);
        loop {
            if let Some(event) = bridge.events.poll()
                && event.kind == EVENT_CANDIDATE_BATCH_V4
            {
                // SAFETY: Candidate-batch events initialize this union member.
                let batch = unsafe { event.payload.candidate_batch };
                if batch.gathering_complete == 1 {
                    break;
                }
            }
            assert!(
                Instant::now() < deadline,
                "final gather batch was not published"
            );
            thread::yield_now();
        }

        responder.join().unwrap();
        thread::sleep(Duration::from_millis(100));
        while let Some(event) = bridge.events.poll() {
            assert_ne!(
                event.kind, EVENT_CANDIDATE_BATCH_V4,
                "late STUN response reopened a completed candidate stream"
            );
        }
    }

    #[test]
    fn incremental_plan_rejects_the_33rd_pair_atomically() {
        let ip = active_ip();
        let local_socket = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let remote_socket = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let local = test_candidate(local_socket.bound_addr(), 1);
        let remote = test_candidate(remote_socket.local_addr().unwrap(), 2);
        let mut begin = test_begin(
            ROLE_INITIATOR_V4,
            [1; 16],
            [2; 16],
            [3; 32],
            [4; 32],
            test_pair(local, remote, 1),
        );
        begin.plan_complete = 0;
        let mut check = test_check(local_socket, begin, local);
        for batch in 0..3_u32 {
            let mut records = [TermLinkDirectCandidatePairV4::default(); MAX_PAIRS_PER_BATCH_V4];
            for (offset, record) in records.iter_mut().enumerate() {
                let rank = 2 + batch * 8 + offset as u32;
                *record = test_pair(
                    local,
                    test_candidate(remote_socket.local_addr().unwrap(), rank as u8 + 1),
                    rank,
                );
            }
            append_pair_slice(&mut check, batch + 2, 8, 9 + batch * 8, 0, &records).unwrap();
        }
        let mut records = [TermLinkDirectCandidatePairV4::default(); MAX_PAIRS_PER_BATCH_V4];
        for (offset, record) in records[..7].iter_mut().enumerate() {
            let rank = 26 + offset as u32;
            *record = test_pair(
                local,
                test_candidate(remote_socket.local_addr().unwrap(), rank as u8 + 1),
                rank,
            );
        }
        append_pair_slice(&mut check, 5, 7, 32, 0, &records).unwrap();
        let before = check.total_pair_count;
        records = [TermLinkDirectCandidatePairV4::default(); MAX_PAIRS_PER_BATCH_V4];
        records[0] = test_pair(
            local,
            test_candidate(remote_socket.local_addr().unwrap(), 34),
            33,
        );
        assert_eq!(
            append_pair_slice(&mut check, 6, 1, 33, 1, &records),
            Err(REASON_INVALID_PLAN_V4)
        );
        assert_eq!(check.total_pair_count, before);
        assert_eq!(check.pump.total_pair_count(), 32);
    }

    #[test]
    fn both_authenticated_directions_validate_and_handoff_the_same_socket() {
        let ip = active_ip();
        let initiator_socket = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let responder_socket = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let initiator_addr = initiator_socket.bound_addr();
        let responder_addr = responder_socket.bound_addr();
        let initiator = test_candidate(initiator_addr, 1);
        let responder = test_candidate(responder_addr, 2);
        let pair = test_pair(initiator, responder, 1);
        let mut a = test_check(
            initiator_socket,
            test_begin(ROLE_INITIATOR_V4, [1; 16], [2; 16], [3; 32], [4; 32], pair),
            initiator,
        );
        let mut b = test_check(
            responder_socket,
            test_begin(ROLE_RESPONDER_V4, [2; 16], [1; 16], [4; 32], [3; 32], pair),
            responder,
        );
        let token = pair_token(&CandidatePair {
            initiator_candidate_id: initiator.candidate_id,
            responder_candidate_id: responder.candidate_id,
        });
        let deadline = Instant::now() + Duration::from_secs(2);
        let mut a_valid = false;
        let mut b_valid = false;
        while !(a_valid && b_valid) {
            let begin = a.begin;
            let pairs = a.pairs.clone();
            a_valid |= a
                .pump
                .step(Duration::from_millis(20), |source, packet| {
                    authenticate_packet(&begin, &pairs, source, packet, begin.now_ms)
                })
                .unwrap()
                .iter()
                .any(|event| matches!(event, MultipairPumpEvent::PairValidated { .. }));
            let begin = b.begin;
            let pairs = b.pairs.clone();
            b_valid |= b
                .pump
                .step(Duration::from_millis(20), |source, packet| {
                    authenticate_packet(&begin, &pairs, source, packet, begin.now_ms)
                })
                .unwrap()
                .iter()
                .any(|event| matches!(event, MultipairPumpEvent::PairValidated { .. }));
            assert!(Instant::now() < deadline);
        }
        assert_eq!(
            a.pump.commit_winner(token).unwrap().original_addr(),
            initiator_addr
        );
        assert_eq!(
            b.pump.commit_winner(token).unwrap().original_addr(),
            responder_addr
        );
    }

    #[test]
    fn stale_append_is_rejected_without_mutating_the_live_plan() {
        let ip = active_ip();
        let local_socket = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let remote_socket = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let local = test_candidate(local_socket.bound_addr(), 1);
        let remote = test_candidate(remote_socket.local_addr().unwrap(), 2);
        let begin = test_begin(
            ROLE_INITIATOR_V4,
            [1; 16],
            [2; 16],
            [3; 32],
            [4; 32],
            test_pair(local, remote, 1),
        );
        let mut check = test_check(local_socket, begin, local);
        let stale = TermLinkDirectAppendCheckPairsV4 {
            abi_version: ABI_VERSION_V4,
            struct_size: size_of::<TermLinkDirectAppendCheckPairsV4>() as u32,
            epoch: 2,
            attempt_id: [9; 16],
            check_round_id: begin.check_round_id,
            pair_batch_seq: 2,
            plan_complete: 1,
            ..TermLinkDirectAppendCheckPairsV4::default()
        };
        assert_eq!(
            append_pairs_v4(&mut check, &stale),
            Err(REASON_INVALID_PLAN_V4)
        );
        assert_eq!(check.total_pair_count, 1);
        assert_eq!(check.pump.total_pair_count(), 1);
    }

    #[test]
    fn truncated_pair_token_collision_is_rejected_before_map_overwrite() {
        let ip = active_ip();
        let local_socket = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let remote_socket = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let local = test_candidate(local_socket.bound_addr(), 1);
        let remote = test_candidate(remote_socket.local_addr().unwrap(), 2);
        let mut begin = test_begin(
            ROLE_INITIATOR_V4,
            [1; 16],
            [2; 16],
            [3; 32],
            [4; 32],
            test_pair(local, remote, 1),
        );
        begin.plan_complete = 0;
        let mut check = test_check(local_socket, begin, local);
        let colliding_pair = test_pair(
            local,
            test_candidate(remote_socket.local_addr().unwrap(), 3),
            2,
        );
        let colliding_token = pair_token(&CandidatePair {
            initiator_candidate_id: colliding_pair.initiator_candidate.candidate_id,
            responder_candidate_id: colliding_pair.responder_candidate.candidate_id,
        });
        // Simulate the astronomically unlikely truncated-hash collision directly.
        check.pairs.insert(colliding_token, begin.pairs[0]);
        let mut records = [TermLinkDirectCandidatePairV4::default(); MAX_PAIRS_PER_BATCH_V4];
        records[0] = colliding_pair;
        assert_eq!(
            append_pair_slice(&mut check, 2, 1, 2, 1, &records),
            Err(REASON_INVALID_PLAN_V4)
        );
        assert_eq!(check.total_pair_count, 1);
    }

    #[test]
    fn unvalidated_nomination_cannot_consume_the_socket() {
        let ip = active_ip();
        let local_socket = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let remote_socket = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let local = test_candidate(local_socket.bound_addr(), 1);
        let remote = test_candidate(remote_socket.local_addr().unwrap(), 2);
        let pair = test_pair(local, remote, 1);
        let begin = test_begin(ROLE_INITIATOR_V4, [1; 16], [2; 16], [3; 32], [4; 32], pair);
        let check = test_check(local_socket, begin, local);
        let mut proof_nonce = [0; 32];
        proof_nonce[..16].fill(17);
        let commit = TermLinkDirectCommitNominationV4 {
            abi_version: ABI_VERSION_V4,
            struct_size: size_of::<TermLinkDirectCommitNominationV4>() as u32,
            epoch: begin.epoch,
            attempt_id: begin.attempt_id,
            check_round_id: begin.check_round_id,
            nomination_id: [14; 16],
            winning_pair: pair,
            role: begin.role,
            timeout_ms: 1_000,
            selection_confirmed: 1,
            local_proof_nonce_length: 16,
            peer_certificate_sha256: [15; 32],
            peer_device_signing_public_key: [16; 32],
            local_proof_nonce: proof_nonce,
            now_ms: begin.now_ms,
            ..TermLinkDirectCommitNominationV4::default()
        };
        let (_signature_tx, signature_rx) = mpsc::sync_channel(1);
        let events = EventQueueV4::new(8);
        let cancel = AtomicU64::new(0);
        let network = AtomicU64::new(0);
        let owner = Mutex::new(check.owner);
        let relay_only = AtomicBool::new(true);
        let error = commit_v4(
            TermLinkDirectCreateV4 {
                abi_version: 4,
                struct_size: 32,
                enabled: 1,
                queue_capacity: 8,
                platform: PLATFORM_WINDOWS_V4,
                reserved: [0; 3],
            },
            commit,
            check,
            &signature_rx,
            &events,
            &cancel,
            &network,
            &owner,
            &relay_only,
        )
        .unwrap_err();
        assert_eq!(error.0, REASON_NOMINATION_REJECTED_V4);
        assert!(
            error.1.is_some(),
            "rejected nomination retained its socket owner"
        );
        assert!(relay_only.load(Ordering::Acquire));
        assert!(events.poll().is_none());
    }

    #[test]
    fn nomination_is_rejected_until_the_incremental_pair_plan_is_complete() {
        let ip = active_ip();
        let local_socket = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let remote_socket = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let local = test_candidate(local_socket.bound_addr(), 1);
        let remote = test_candidate(remote_socket.local_addr().unwrap(), 2);
        let pair = test_pair(local, remote, 1);
        let mut begin = test_begin(ROLE_INITIATOR_V4, [1; 16], [2; 16], [3; 32], [4; 32], pair);
        begin.plan_complete = 0;
        let mut check = test_check(local_socket, begin, local);
        let token = pair_token(&CandidatePair {
            initiator_candidate_id: pair.initiator_candidate.candidate_id,
            responder_candidate_id: pair.responder_candidate.candidate_id,
        });
        check.validated.insert(token);
        assert!(!check.plan_complete);
        assert_eq!(check.total_pair_count, 1);

        let commit = ticket_vector_commit(begin, pair, [15; 32]);
        let (_signature_tx, signature_rx) = mpsc::sync_channel(1);
        let events = EventQueueV4::new(8);
        let cancel = AtomicU64::new(0);
        let network = AtomicU64::new(0);
        let owner = Mutex::new(check.owner);
        let relay_only = AtomicBool::new(true);
        let error = commit_v4(
            TermLinkDirectCreateV4 {
                abi_version: 4,
                struct_size: 32,
                enabled: 1,
                queue_capacity: 8,
                platform: PLATFORM_WINDOWS_V4,
                reserved: [0; 3],
            },
            commit,
            check,
            &signature_rx,
            &events,
            &cancel,
            &network,
            &owner,
            &relay_only,
        )
        .unwrap_err();

        assert_eq!(error.0, REASON_NOMINATION_REJECTED_V4);
        let retained = error
            .1
            .expect("an incomplete plan must retain its sole socket owner");
        assert!(!retained.plan_complete);
        assert!(retained.validated.contains(&token));
        assert_eq!(retained.pump.total_pair_count(), 1);
        assert!(relay_only.load(Ordering::Acquire));
        assert!(events.poll().is_none());
    }

    #[test]
    fn pair_validation_rejects_family_mismatch_and_ticket_expiry_violations() {
        let local = test_candidate("192.0.2.10:49152".parse().unwrap(), 1);
        let remote_v6 = test_candidate("[2001:db8::10]:49153".parse().unwrap(), 2);
        assert_eq!(
            validate_pair(
                &test_pair(local, remote_v6, 1),
                ROLE_INITIATOR_V4,
                1,
                1_000,
                10_000,
            ),
            Err(REASON_INVALID_PLAN_V4)
        );
        let mut expired = test_candidate("192.0.2.11:49153".parse().unwrap(), 2);
        expired.expires_at_ms = 1_000;
        assert_eq!(
            validate_pair(
                &test_pair(local, expired, 1),
                ROLE_INITIATOR_V4,
                1,
                1_000,
                10_000,
            ),
            Err(REASON_INVALID_PLAN_V4)
        );
        let mut beyond_ticket = test_candidate("192.0.2.11:49153".parse().unwrap(), 2);
        beyond_ticket.expires_at_ms = 10_001;
        assert_eq!(
            validate_pair(
                &test_pair(local, beyond_ticket, 1),
                ROLE_INITIATOR_V4,
                1,
                1_000,
                10_000,
            ),
            Err(REASON_INVALID_PLAN_V4)
        );

        let mut bounded = test_candidate("8.8.8.8:49153".parse().unwrap(), 2);
        bounded.network_cost = 999;
        bounded.priority = i32::MAX as u32;
        bounded.expires_at_ms = 2_000;
        assert!(validate_candidate(&bounded).is_ok());
        assert!(
            validate_pair(
                &test_pair(local, bounded, 1),
                ROLE_INITIATOR_V4,
                1,
                1_000,
                10_000
            )
            .is_ok()
        );

        let mut invalid_cost = bounded;
        invalid_cost.network_cost = 1_000;
        assert_eq!(
            validate_candidate(&invalid_cost),
            Err(REASON_INVALID_CANDIDATE_V4)
        );
        let mut invalid_priority = bounded;
        invalid_priority.priority = i32::MAX as u32 + 1;
        assert_eq!(
            validate_candidate(&invalid_priority),
            Err(REASON_INVALID_CANDIDATE_V4)
        );
        let mut zero_expiry = bounded;
        zero_expiry.expires_at_ms = 0;
        assert_eq!(
            validate_candidate(&zero_expiry),
            Err(REASON_INVALID_CANDIDATE_V4)
        );

        let mut at_now = bounded;
        at_now.expires_at_ms = 1_000;
        assert_eq!(
            validate_pair(
                &test_pair(local, at_now, 1),
                ROLE_INITIATOR_V4,
                1,
                1_000,
                10_000
            ),
            Err(REASON_INVALID_PLAN_V4)
        );
        let mut beyond_horizon = bounded;
        beyond_horizon.expires_at_ms = 121_001;
        assert_eq!(
            validate_pair(
                &test_pair(local, beyond_horizon, 1),
                ROLE_INITIATOR_V4,
                1,
                1_000,
                200_000,
            ),
            Err(REASON_INVALID_PLAN_V4)
        );
    }

    #[test]
    fn host_checks_can_start_before_delayed_stun_trickles_a_candidate() {
        let ip = active_ip();
        assert!(ip.is_ipv4());
        let owner = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let peer = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let stun = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let stun_addr = stun.local_addr().unwrap();
        let responder = thread::spawn(move || {
            let mut request = [0_u8; 64];
            let (_, source) = stun.recv_from(&mut request).unwrap();
            let transaction_id = request[8..20].try_into().unwrap();
            thread::sleep(Duration::from_millis(50));
            stun.send_to(&stun_response(source, transaction_id), source)
                .unwrap();
        });
        let mut pump = owner
            .into_multipair_pump(&[stun_addr], Duration::from_secs(2))
            .unwrap();
        pump.append_pair(MultipairCheckPair {
            token: PairToken::new([1; 16]),
            peer: peer.local_addr().unwrap(),
            probe_packet: vec![1; 64],
        })
        .unwrap();
        let deadline = Instant::now() + Duration::from_secs(1);
        loop {
            let values = pump.step(Duration::from_millis(20), |_, _| None).unwrap();
            if values
                .iter()
                .any(|event| matches!(event, MultipairPumpEvent::ServerReflexiveCandidate(_)))
            {
                assert_eq!(pump.total_pair_count(), 1, "HOST check remained active");
                break;
            }
            assert!(Instant::now() < deadline);
        }
        responder.join().unwrap();
    }

    #[test]
    fn append_and_commit_without_checks_emit_direct_only_failures() {
        fn bridge_with_gather(ip: IpAddr) -> NativeBridgeV4 {
            let bridge = NativeBridgeV4::create(TermLinkDirectCreateV4 {
                abi_version: 4,
                struct_size: 32,
                enabled: 1,
                queue_capacity: 8,
                platform: PLATFORM_WINDOWS_V4,
                reserved: [0; 3],
            })
            .unwrap();
            let encoded = encode_socket(SocketAddr::new(ip, 0));
            let gather = TermLinkDirectGatherV4 {
                abi_version: 4,
                struct_size: 256,
                epoch: 1,
                attempt_id: [1; 16],
                local_bind: encoded,
                advertised_local_ip: encoded.ip,
                advertised_local_ip_length: encoded.ip_length,
                gather_timeout_ms: 100,
                candidate_expires_at_ms: 10_000,
                ..TermLinkDirectGatherV4::default()
            };
            assert_eq!(bridge.accept_gather(gather), 0);
            bridge
        }
        fn await_failure(bridge: &NativeBridgeV4) -> TermLinkDirectEventV4 {
            let deadline = Instant::now() + Duration::from_secs(1);
            loop {
                if let Some(event) = bridge.events.poll()
                    && event.kind == EVENT_DIRECT_FAILED_V4
                {
                    return event;
                }
                assert!(Instant::now() < deadline);
                thread::yield_now();
            }
        }
        let ip = active_ip();
        let append_bridge = bridge_with_gather(ip);
        let append = TermLinkDirectAppendCheckPairsV4 {
            abi_version: 4,
            struct_size: size_of::<TermLinkDirectAppendCheckPairsV4>() as u32,
            epoch: 1,
            attempt_id: [1; 16],
            check_round_id: [2; 16],
            pair_batch_seq: 1,
            plan_complete: 1,
            ..TermLinkDirectAppendCheckPairsV4::default()
        };
        assert_eq!(
            append_bridge.submit(CommandV4::Append(Box::new(append)), 1, [1; 16]),
            0
        );
        assert_eq!(await_failure(&append_bridge).reason, REASON_INVALID_PLAN_V4);
        assert!(append_bridge.relay_only.load(Ordering::Acquire));

        let commit_bridge = bridge_with_gather(ip);
        let commit = TermLinkDirectCommitNominationV4 {
            abi_version: 4,
            struct_size: size_of::<TermLinkDirectCommitNominationV4>() as u32,
            epoch: 1,
            attempt_id: [1; 16],
            ..TermLinkDirectCommitNominationV4::default()
        };
        assert_eq!(
            commit_bridge.submit(CommandV4::Commit(Box::new(commit)), 1, [1; 16]),
            0
        );
        assert_eq!(
            await_failure(&commit_bridge).reason,
            REASON_NOMINATION_REJECTED_V4
        );
        assert!(commit_bridge.relay_only.load(Ordering::Acquire));
    }

    #[test]
    fn shadow_ready_payload_binds_round_nomination_pair_and_path_without_routing() {
        let local = test_candidate("192.0.2.10:49152".parse().unwrap(), 1);
        let remote = test_candidate("192.0.2.11:49153".parse().unwrap(), 2);
        let pair = test_pair(local, remote, 1);
        let begin = test_begin(ROLE_INITIATOR_V4, [1; 16], [2; 16], [3; 32], [4; 32], pair);
        let commit = TermLinkDirectCommitNominationV4 {
            epoch: begin.epoch,
            attempt_id: begin.attempt_id,
            check_round_id: begin.check_round_id,
            nomination_id: [14; 16],
            winning_pair: pair,
            role: begin.role,
            ..TermLinkDirectCommitNominationV4::default()
        };
        let event = shadow_ready_event(
            &begin,
            &commit,
            decode_socket(remote.endpoint, false).unwrap(),
            [15; 32],
        );
        assert_eq!(event.kind, EVENT_SHADOW_READY_V4);
        assert_eq!(event.state, STATE_SHADOW_DIRECT_V4);
        assert_eq!(event.reason, 0);
        // SAFETY: shadow_ready_event initializes this exact union member.
        let payload = unsafe { event.payload.shadow_ready };
        assert_eq!(payload.check_round_id, begin.check_round_id);
        assert_eq!(payload.nomination_id, commit.nomination_id);
        assert_eq!(payload.winning_pair, pair);
        assert_eq!(payload.direct_path_binding, [15; 32]);
        assert_eq!(payload.reserved, [0; 2]);
    }

    #[test]
    fn stale_gather_cannot_destroy_a_successor_socket_before_begin() {
        let ip = active_ip();
        let bridge = NativeBridgeV4::create(TermLinkDirectCreateV4 {
            abi_version: 4,
            struct_size: 32,
            enabled: 1,
            queue_capacity: 16,
            platform: PLATFORM_WINDOWS_V4,
            reserved: [0; 3],
        })
        .unwrap();
        let encoded = encode_socket(SocketAddr::new(ip, 0));
        let successor = TermLinkDirectGatherV4 {
            abi_version: 4,
            struct_size: 256,
            epoch: 7,
            attempt_id: [7; 16],
            local_bind: encoded,
            advertised_local_ip: encoded.ip,
            advertised_local_ip_length: encoded.ip_length,
            candidate_expires_at_ms: 1_900_000_110_000,
            gather_timeout_ms: 100,
            ..TermLinkDirectGatherV4::default()
        };
        assert_eq!(bridge.accept_gather(successor), 0);
        let deadline = Instant::now() + Duration::from_secs(1);
        let mut local_candidate = None;
        loop {
            if let Some(event) = bridge.events.poll()
                && event.kind == EVENT_CANDIDATE_BATCH_V4
            {
                // SAFETY: Candidate-batch events initialize this member.
                let batch = unsafe { event.payload.candidate_batch };
                if batch.candidate_count == 1 {
                    local_candidate = Some(batch.candidates[0]);
                }
                if batch.gathering_complete == 1 {
                    break;
                }
            }
            assert!(Instant::now() < deadline);
            thread::yield_now();
        }
        let local_candidate = local_candidate.unwrap();

        let stale = TermLinkDirectGatherV4 {
            epoch: 6,
            attempt_id: [6; 16],
            ..TermLinkDirectGatherV4::default()
        };
        assert_eq!(
            bridge.submit(CommandV4::Gather(Box::new(stale)), 6, [6; 16]),
            0
        );

        let peer = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let mut remote_candidate = test_candidate(peer.local_addr().unwrap(), 42);
        remote_candidate.expires_at_ms = 1_900_000_110_000;
        let pair = test_pair(local_candidate, remote_candidate, 1);
        let ticket = base64url(
            "CAESEAARIjNEVUZ3iJmqu8zd7v8aEBAhMkNUZUdoiZqrvM3e7_AiECAxQlNkdUhpipusvc7f8AEqEDBBUmN0hUlqi5ytvs_g8QIyEEBRYnOElUprjJ2uv9Dh8gM4B0CA8MyGpjdIwJnUhqY3UgEBWhAAAQIDBAUGBwgJCgsMDQ4PYhBkaXJlY3QtdjEtdmVjdG9yakCwJTg3vrjJ8h6M6z4BR3XkogSgH3TQI4jp2mBhQTUyJWcO36MFe3MVh3Zxk-uAFShR9t2MynoQJEk4LrR53KkI",
        );
        let mut begin = TermLinkDirectBeginChecksV4 {
            abi_version: 4,
            struct_size: size_of::<TermLinkDirectBeginChecksV4>() as u32,
            epoch: 7,
            attempt_id: [7; 16],
            connection_id: hex("4051627384954a6b8c9daebfd0e1f203"),
            local_device_id: hex("20314253647548698a9bacbdcedff001"),
            peer_device_id: hex("304152637485496a8b9cadbecfe0f102"),
            now_ms: 1_900_000_060_000,
            timeout_ms: 5_000,
            role: ROLE_INITIATOR_V4,
            ticket_length: ticket.len() as u32,
            pair_batch_seq: 1,
            pair_count: 1,
            total_pair_count: 1,
            plan_complete: 0,
            server_signing_public_key: base64url("A6EHv_POEL4dcN0Y50vAmWfk1jCbpQ1fHdyGZBJVMbg")
                .try_into()
                .unwrap(),
            ticket_id: hex("00112233445546778899aabbccddeeff"),
            check_round_id: [12; 16],
            connectivity_nonce: [13; 16],
            local_send_probe_hmac_key: [3; 32],
            peer_receive_probe_hmac_key: [4; 32],
            ..TermLinkDirectBeginChecksV4::default()
        };
        begin.ticket[..ticket.len()].copy_from_slice(&ticket);
        begin.pairs[0] = pair;
        assert_eq!(
            bridge.submit(CommandV4::Begin(Box::new(begin)), 7, [7; 16]),
            0
        );
        let deadline = Instant::now() + Duration::from_secs(1);
        loop {
            if let Some(event) = bridge.events.poll() {
                assert_ne!(
                    event.kind, EVENT_DIRECT_FAILED_V4,
                    "stale gather consumed the successor socket (reason {})",
                    event.reason
                );
                if event.kind == EVENT_STATE_CHANGED_V4 && event.state == STATE_CHECKING_V4 {
                    break;
                }
            }
            assert!(Instant::now() < deadline);
            thread::yield_now();
        }
    }
}
