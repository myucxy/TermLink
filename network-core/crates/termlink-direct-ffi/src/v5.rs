//! Versioned production-data boundary for the optional direct path.
//!
//! V5 owns a promoted, authenticated QUIC path and bounded copies of opaque
//! realtime Envelopes. It never owns Relay/session/renderer/durable-ACK state.

// The canonical safety contract for this C ABI is kept next to all records and
// functions in include/termlink_direct_ffi_v5.h; repeating it on every exported
// Rust shim would create a second contract that could drift.
#![allow(clippy::missing_safety_doc)]

use std::{
    collections::VecDeque,
    ffi::c_void,
    panic::{AssertUnwindSafe, catch_unwind},
    ptr, slice,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
        mpsc::{self, Receiver, SyncSender, TrySendError},
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

use termlink_direct_quic::{
    DirectConnectionControl, DirectDataReader, DirectDataWriter, DirectEndpoint, DirectFrameBody,
    DirectLivenessTracker, DirectPathBindingInputs, DirectPingV1, DirectPongV1,
    DirectTransportReceiptV1, Ed25519PeerProofVerifier, ExpectedPeer, PeerBindingContext,
    PeerProofRequest, PeerRole, direct_path_binding_sha256,
    exchange_and_authenticate_direct_connection, tls_exporter_sha256,
};
use termlink_direct_route::{
    AuthenticatedPathKind, BarrierId, Carrier, ControlMessage, CutoverProposal, DirectPathBinding,
    Direction, EnvelopeTransmission, OpaqueEnvelope, PeerRouteMachine, RouteBatch, SessionBarrier,
    SessionId, SessionRouteState, TransitionOutcome,
};
use termlink_direct_wire::{
    DirectTicketExpectation, DirectTicketRole, MAX_DIRECT_TICKET_BYTES, TicketError, sha256,
    verify_direct_path_ticket,
};

use super::v4::{
    self, ProductionHandoffV4, TermLinkDirectCandidatePairV4, TermLinkDirectCommitNominationV4,
    TermLinkDirectSignatureV4,
};

pub const ABI_VERSION_V5: u32 = 5;
pub const MAX_FRAMES_V5: usize = 256;
pub const MAX_BYTES_V5: usize = 8 * 1024 * 1024;
pub const FAILURE_ACTION_TAIL_V5: usize = 4;
pub const MAX_RETAINED_FRAMES_V5: usize = MAX_FRAMES_V5 + FAILURE_ACTION_TAIL_V5;
pub const MAX_APPLIED_CURSORS_PER_DIRECTION_V5: usize = termlink_direct_route::MAX_APPLIED_CURSORS;
pub const MAX_ENVELOPE_BYTES_V5: usize = (1024 * 1024) - 1;
pub const FIRST_DATA_FRAME_SEQ_V5: u64 = 4;
pub const MAX_FRAME_SEQ_V5: u64 = i64::MAX as u64;
const CUTOVER_DIGEST_DOMAIN_V1: &[u8] = b"TERMLINK-DIRECT-CUTOVER-V1\0";

pub const V5_OK: i32 = 0;
pub const V5_NO_DATA: i32 = 1;
pub const V5_ERR_INVALID_HANDLE: i32 = -1;
pub const V5_ERR_INVALID_ARGUMENT: i32 = -2;
pub const V5_ERR_QUEUE_FULL: i32 = -3;
pub const V5_ERR_STALE_PATH: i32 = -4;
pub const V5_ERR_BUFFER_TOO_SMALL: i32 = -5;
pub const V5_ERR_NOT_READY: i32 = -6;
pub const V5_ERR_CANCELLED: i32 = -7;
pub const V5_ERR_LIMIT: i32 = -8;
pub const V5_ERR_PROTOCOL: i32 = -9;
pub const V5_ERR_DISABLED: i32 = -10;
pub const V5_ERR_TIMEOUT: i32 = -11;
pub const V5_ERR_TICKET_EXPIRED: i32 = -12;

pub const EVENT_PATH_USED_V5: u32 = 1;
pub const EVENT_ENVELOPE_QUEUED_V5: u32 = 2;
pub const EVENT_ENVELOPE_AVAILABLE_V5: u32 = 3;
pub const EVENT_TRANSPORT_RECEIPT_V5: u32 = 4;
pub const EVENT_CANCELLED_V5: u32 = 5;
pub const EVENT_NETWORK_CHANGED_V5: u32 = 6;
pub const EVENT_QUEUE_LIMIT_V5: u32 = 7;
pub const EVENT_PATH_READY_V5: u32 = 8;
pub const EVENT_DIRECT_FAILED_V5: u32 = 9;
pub const EVENT_PING_V5: u32 = 10;
pub const EVENT_PONG_V5: u32 = 11;
pub const EVENT_RENEWAL_DUE_V5: u32 = 13;
pub const EVENT_SIGN_REQUEST_V5: u32 = 12;

pub const EVENT_FLAG_OUTBOUND_V5: u32 = 1;
pub const EVENT_FLAG_INBOUND_V5: u32 = 2;
pub const EVENT_FLAG_TRANSPORT_COPY_V5: u32 = 4;
pub const EVENT_FLAG_LAN_V5: u32 = 8;
pub const EVENT_FLAG_NAT_V5: u32 = 16;

pub const PATH_KIND_LAN_V5: u32 = 1;
pub const PATH_KIND_NAT_V5: u32 = 2;

pub const ROLE_INITIATOR_V5: u32 = 1;
pub const ROLE_RESPONDER_V5: u32 = 2;
pub const CANDIDATE_HOST_V5: u32 = 1;
pub const CANDIDATE_SERVER_REFLEXIVE_V5: u32 = 2;
pub const CANDIDATE_CARRIER_UDP_QUIC_V5: u32 = 1;

pub const DIRECTION_PC_TO_ANDROID_V5: u32 = 1;
pub const DIRECTION_ANDROID_TO_PC_V5: u32 = 2;

pub const ROUTE_ACTION_DIRECT_ENVELOPE_V5: u32 = 1;
pub const ROUTE_ACTION_RELAY_ENVELOPE_V5: u32 = 2;
pub const ROUTE_ACTION_ACCEPT_V5: u32 = 4;
pub const ROUTE_ACTION_COMMIT_V5: u32 = 5;
pub const ROUTE_ACTION_FALLBACK_V5: u32 = 6;
pub const ROUTE_ACTION_CARRIER_V5: u32 = 7;
pub const ROUTE_ACTION_INGRESS_DELIVER_V5: u32 = 8;
pub const ROUTE_ACTION_INGRESS_HOLD_V5: u32 = 9;
pub const ROUTE_ACTION_INGRESS_DROP_V5: u32 = 10;
pub const ROUTE_ACTION_SESSION_CARRIER_V5: u32 = 11;

pub const INGRESS_CARRIER_RELAY_V5: u32 = 1;
pub const INGRESS_CARRIER_DIRECT_V5: u32 = 2;
pub const ROUTE_CARRIER_RELAY_V5: u32 = 1;
pub const ROUTE_CARRIER_LAN_DIRECT_V5: u32 = 2;
pub const ROUTE_CARRIER_NAT_DIRECT_V5: u32 = 3;

#[repr(C, align(8))]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct TermLinkSocketAddrV5 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub ip: [u8; 16],
    pub ip_length: u32,
    pub port: u32,
    pub reserved: [u64; 2],
}

#[repr(C, align(8))]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct TermLinkDirectCandidateV5 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub candidate_id: [u8; 16],
    pub related_candidate_id: [u8; 16],
    pub endpoint: TermLinkSocketAddrV5,
    pub kind: u32,
    pub carrier: u32,
    pub priority: u32,
    pub network_cost: u32,
    pub expires_at_ms: i64,
    pub reserved: [u64; 2],
}

#[repr(C, align(8))]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct TermLinkDirectCandidatePairV5 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub rank: u32,
    pub reserved0: u32,
    pub initiator_candidate: TermLinkDirectCandidateV5,
    pub responder_candidate: TermLinkDirectCandidateV5,
    pub reserved: [u64; 2],
}

#[repr(C, align(8))]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct TermLinkDirectPathV5 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub path_epoch: u64,
    pub connection_id: [u8; 16],
    pub attempt_id: [u8; 16],
    pub reserved: [u64; 2],
}

#[repr(C, align(8))]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct TermLinkDirectCreateV5 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub enabled: u32,
    pub reserved0: u32,
    pub queue_capacity_frames: u32,
    pub reserved1: u32,
    pub queue_capacity_bytes: u64,
    pub path: TermLinkDirectPathV5,
    pub reserved: [u64; 2],
}

#[repr(C, align(8))]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct TermLinkDirectEventV5 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub kind: u32,
    pub status: i32,
    pub flags: u32,
    pub reserved0: u32,
    pub path_epoch: u64,
    pub attempt_id: [u8; 16],
    pub frame_seq: u64,
    pub payload_bytes: u32,
    pub released_frames: u32,
    pub reserved: [u64; 2],
}

/// Promotes one already validated, not-yet-committed V4 multipair attempt.
/// This is an independent V5 record; the V4 handle is only an opaque argument
/// to the promotion call and is never stored by V5.
#[repr(C, align(8))]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct TermLinkDirectPromoteV5 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub path: TermLinkDirectPathV5,
    pub check_round_id: [u8; 16],
    pub nomination_id: [u8; 16],
    pub winning_pair: TermLinkDirectCandidatePairV5,
    pub role: u32,
    pub timeout_ms: u32,
    pub selection_confirmed: u32,
    pub local_proof_nonce_length: u32,
    pub peer_certificate_sha256: [u8; 32],
    pub peer_device_signing_public_key: [u8; 32],
    pub local_proof_nonce: [u8; 32],
    pub now_ms: i64,
    /// Semantic direction sent by this local endpoint. Windows is
    /// PC_TO_ANDROID; Android is ANDROID_TO_PC. This is never inferred from
    /// the QUIC initiator/responder role.
    pub local_outbound_direction: u32,
    pub reserved0: u32,
    pub reserved: [u64; 3],
}

#[repr(C, align(8))]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct TermLinkDirectPathInfoV5 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub path: TermLinkDirectPathV5,
    pub path_kind: u32,
    pub ready: u32,
    pub direct_path_binding: [u8; 32],
    pub reserved: [u64; 2],
}

#[repr(C, align(8))]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct TermLinkDirectSignatureV5 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub path: TermLinkDirectPathV5,
    pub request_id: u64,
    pub signature_length: u32,
    pub reserved0: u32,
    pub signature: [u8; 128],
    pub reserved: [u64; 2],
}

#[repr(C, align(8))]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct TermLinkDirectSessionBarrierV5 {
    pub session_id: [u8; 16],
    pub first_direct_seq: u64,
}

#[repr(C, align(8))]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct TermLinkDirectProposalV5 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub path: TermLinkDirectPathV5,
    pub direction: u32,
    pub session_count: u32,
    pub barrier_id: [u8; 16],
    pub barrier_sha256: [u8; 32],
    pub direct_path_binding: [u8; 32],
    pub duplicate_grace_ms: u32,
    pub reserved0: u32,
    pub now_ms: u64,
    pub timeout_ms: u64,
    pub sessions: [TermLinkDirectSessionBarrierV5; 32],
    pub reserved: [u64; 2],
}

#[repr(C, align(8))]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct TermLinkDirectTransitionV5 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub path: TermLinkDirectPathV5,
    pub direction: u32,
    pub reserved0: u32,
    pub barrier_id: [u8; 16],
    pub barrier_sha256: [u8; 32],
    pub accepted: u32,
    pub is_echo: u32,
    pub reserved1: u32,
    pub reserved2: u32,
    pub now_ms: u64,
    pub timeout_ms: u64,
    pub reserved: [u64; 2],
}

#[repr(C, align(8))]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct TermLinkDirectCursorV5 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub path: TermLinkDirectPathV5,
    pub direction: u32,
    pub reserved0: u32,
    pub session_id: [u8; 16],
    pub highest_contiguous: u64,
    pub reserved: [u64; 2],
}

#[repr(C, align(8))]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct TermLinkDirectRouteEnvelopeV5 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub path: TermLinkDirectPathV5,
    pub direction: u32,
    /// Zero means unknown/control and is always Relay-only. One means an
    /// eligible session data Envelope.
    pub route_eligible: u32,
    pub session_id: [u8; 16],
    pub envelope_sequence: u64,
    pub now_ms: u64,
    pub reserved: [u64; 2],
}

#[repr(C, align(8))]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct TermLinkDirectIngressV5 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub path: TermLinkDirectPathV5,
    pub direction: u32,
    /// One for Relay, two for Direct. Any other value fails closed.
    pub source: u32,
    pub session_id: [u8; 16],
    pub envelope_sequence: u64,
    pub now_ms: u64,
    pub duplicate_grace_ms: u32,
    pub reserved0: u32,
    pub reserved: [u64; 2],
}

#[repr(C, align(8))]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct TermLinkDirectRouteActionV5 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub path: TermLinkDirectPathV5,
    pub kind: u32,
    pub direction: u32,
    pub session_id: [u8; 16],
    pub envelope_sequence: u64,
    pub direct_frame_seq: u64,
    pub barrier_id: [u8; 16],
    pub barrier_sha256: [u8; 32],
    pub accepted: u32,
    pub is_echo: u32,
    pub carrier: u32,
    pub flags: u32,
    pub reason: u32,
    pub payload_bytes: u32,
    pub reserved: [u64; 2],
}

impl Default for TermLinkDirectSignatureV5 {
    fn default() -> Self {
        // All fields use zero as their reserved/empty representation.
        unsafe { std::mem::zeroed() }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct Fence {
    epoch: u64,
    connection_id: [u8; 16],
    attempt_id: [u8; 16],
}

#[derive(Debug)]
struct OwnedFrame {
    frame_seq: u64,
    bytes: Vec<u8>,
}

#[derive(Clone, Debug)]
enum OutboundWork {
    Envelope {
        frame_seq: u64,
        bytes: Arc<[u8]>,
    },
    Receipt {
        frame_seq: u64,
        highest_contiguous_frame_seq: u64,
    },
    Ping {
        frame_seq: u64,
        nonce: [u8; 16],
        timestamp_ms: i64,
    },
    Pong {
        frame_seq: u64,
        nonce: [u8; 16],
        timestamp_ms: i64,
    },
}

impl OutboundWork {
    fn frame_seq(&self) -> u64 {
        match self {
            Self::Envelope { frame_seq, .. }
            | Self::Receipt { frame_seq, .. }
            | Self::Ping { frame_seq, .. }
            | Self::Pong { frame_seq, .. } => *frame_seq,
        }
    }
}

#[derive(Debug)]
struct OwnedRouteAction {
    record: TermLinkDirectRouteActionV5,
    bytes: Arc<[u8]>,
    charged_bytes: usize,
}

#[derive(Debug)]
struct IngressEntry {
    direction: Direction,
    session_id: SessionId,
    envelope_sequence: u64,
    bytes: Arc<[u8]>,
    source_direct: bool,
    deadline_ms: u64,
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct DeliveredIngress {
    direction: Direction,
    session_id: SessionId,
    envelope_sequence: u64,
    digest: [u8; 32],
    bytes: Arc<[u8]>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum Lifecycle {
    Active,
    Ready,
    Cancelled,
    NetworkChanged,
    Failed,
}

#[derive(Debug)]
struct State {
    enabled: bool,
    max_frames: usize,
    max_bytes: usize,
    path: Option<Fence>,
    lifecycle: Lifecycle,
    outgoing: VecDeque<OutboundWork>,
    incoming: VecDeque<OwnedFrame>,
    queued_bytes: usize,
    local_outbound_direction: Option<Direction>,
    last_wire_sent_seq: u64,
    last_received_seq: u64,
    last_receipt_seq: u64,
    events: VecDeque<TermLinkDirectEventV5>,
    event_overflow: bool,
    binding: [u8; 32],
    path_kind: u32,
    signatures: VecDeque<TermLinkDirectSignatureV4>,
    signing_payloads: VecDeque<OwnedFrame>,
    route: PeerRouteMachine,
    route_actions: VecDeque<OwnedRouteAction>,
    ingress: VecDeque<IngressEntry>,
    delivered_ingress: VecDeque<DeliveredIngress>,
    // Ticket-renewal state (audit #2): the applied expiry moves forward when
    // the host applies a server-issued renewal ticket; the due flag suppresses
    // duplicate EVENT_RENEWAL_DUE emissions for one window.
    ticket_expiry_ms: i64,
    renewal_due_emitted: bool,
    ticket_renewal_binding: Option<DirectTicketExpectation>,
    server_signing_key: Option<[u8; 32]>,
}

impl State {
    fn new(enabled: bool, max_frames: usize, max_bytes: usize, path: Option<Fence>) -> Self {
        Self {
            enabled,
            max_frames,
            max_bytes,
            path,
            lifecycle: Lifecycle::Active,
            outgoing: VecDeque::new(),
            incoming: VecDeque::new(),
            queued_bytes: 0,
            local_outbound_direction: None,
            last_wire_sent_seq: FIRST_DATA_FRAME_SEQ_V5 - 1,
            last_received_seq: FIRST_DATA_FRAME_SEQ_V5 - 1,
            last_receipt_seq: FIRST_DATA_FRAME_SEQ_V5 - 1,
            events: VecDeque::new(),
            event_overflow: false,
            binding: [0; 32],
            path_kind: 0,
            signatures: VecDeque::new(),
            signing_payloads: VecDeque::new(),
            route: PeerRouteMachine::new(),
            route_actions: VecDeque::new(),
            ingress: VecDeque::new(),
            delivered_ingress: VecDeque::new(),
            ticket_expiry_ms: 0,
            renewal_due_emitted: false,
            ticket_renewal_binding: None,
            server_signing_key: None,
        }
    }

    fn path_metadata(&self) -> (u64, [u8; 16]) {
        self.path
            .map_or((0, [0; 16]), |path| (path.epoch, path.attempt_id))
    }

    fn clear_for_new_path(&mut self, path: Fence) {
        if let Some(old_path) = self.path {
            fallback_direct_in_state(self, old_path);
        }
        // Event metadata belongs to one transport fence. A successor starts
        // with a clean event queue so predecessor pressure cannot immediately
        // fail the new path or surface events under the wrong epoch.
        self.events.clear();
        self.event_overflow = false;
        self.outgoing.clear();
        while let Some(frame) = self.incoming.pop_front() {
            self.queued_bytes = self.queued_bytes.saturating_sub(frame.bytes.len());
        }
        while let Some(entry) = self.ingress.pop_front() {
            self.queued_bytes = self.queued_bytes.saturating_sub(entry.bytes.len());
        }
        self.signatures.clear();
        self.signing_payloads.clear();
        self.path = Some(path);
        self.lifecycle = Lifecycle::Active;
        self.local_outbound_direction = None;
        self.last_wire_sent_seq = FIRST_DATA_FRAME_SEQ_V5 - 1;
        self.last_received_seq = FIRST_DATA_FRAME_SEQ_V5 - 1;
        self.last_receipt_seq = FIRST_DATA_FRAME_SEQ_V5 - 1;
        self.binding = [0; 32];
        self.path_kind = 0;
    }

    fn total_frames(&self) -> usize {
        self.outgoing.len()
            + self.incoming.len()
            + self.route_actions.len()
            + self.ingress.len()
            + self.delivered_ingress.len()
            + self.route.buffered_envelopes(Direction::PcToAndroid)
            + self.route.buffered_envelopes(Direction::AndroidToPc)
    }

    fn route_buffered_bytes(&self) -> usize {
        self.route
            .buffered_bytes(Direction::PcToAndroid)
            .saturating_add(self.route.buffered_bytes(Direction::AndroidToPc))
    }

    fn check_path(&self, path: Fence) -> Result<(), i32> {
        self.check_fence(path)?;
        if !matches!(self.lifecycle, Lifecycle::Active | Lifecycle::Ready) {
            return Err(V5_ERR_CANCELLED);
        }
        Ok(())
    }

    fn check_fence(&self, path: Fence) -> Result<(), i32> {
        let Some(current) = self.path else {
            return Err(V5_ERR_STALE_PATH);
        };
        if current != path {
            return Err(V5_ERR_STALE_PATH);
        }
        Ok(())
    }

    fn push_event(
        &mut self,
        kind: u32,
        status: i32,
        flags: u32,
        frame_seq: u64,
        payload_bytes: u32,
        released_frames: u32,
    ) -> bool {
        if self.events.len() >= self.max_frames {
            self.event_overflow = true;
            if matches!(self.lifecycle, Lifecycle::Active | Lifecycle::Ready) {
                if let Some(path) = self.path {
                    fallback_direct_in_state(self, path);
                }
                self.lifecycle = Lifecycle::Failed;
            }
            return false;
        }
        let (path_epoch, attempt_id) = self.path_metadata();
        self.events.push_back(TermLinkDirectEventV5 {
            abi_version: ABI_VERSION_V5,
            struct_size: std::mem::size_of::<TermLinkDirectEventV5>() as u32,
            kind,
            status,
            flags,
            reserved0: 0,
            path_epoch,
            attempt_id,
            frame_seq,
            payload_bytes,
            released_frames,
            reserved: [0; 2],
        });
        true
    }

    fn queue_available(&self, bytes: usize) -> bool {
        self.total_frames() < self.max_frames
            && self
                .queued_bytes
                .checked_add(self.route_buffered_bytes())
                .and_then(|total| total.checked_add(bytes))
                .is_some_and(|total| total <= self.max_bytes)
    }

    fn can_commit_actions_and_work(
        &self,
        actions: &[OwnedRouteAction],
        work: &[OutboundWork],
    ) -> bool {
        self.can_commit_with_route(&self.route, actions, work)
    }

    fn can_commit_with_route(
        &self,
        route: &PeerRouteMachine,
        actions: &[OwnedRouteAction],
        work: &[OutboundWork],
    ) -> bool {
        let added_frames = actions.len().saturating_add(work.len());
        let added_bytes = actions
            .iter()
            .map(|entry| entry.charged_bytes)
            .sum::<usize>();
        let route_frames = route
            .buffered_envelopes(Direction::PcToAndroid)
            .saturating_add(route.buffered_envelopes(Direction::AndroidToPc));
        let non_route_frames = self
            .outgoing
            .len()
            .saturating_add(self.incoming.len())
            .saturating_add(self.route_actions.len())
            .saturating_add(self.ingress.len())
            .saturating_add(self.delivered_ingress.len());
        let route_bytes = route
            .buffered_bytes(Direction::PcToAndroid)
            .saturating_add(route.buffered_bytes(Direction::AndroidToPc));
        non_route_frames
            .saturating_add(route_frames)
            .saturating_add(added_frames)
            <= self.max_frames
            && self
                .queued_bytes
                .checked_add(route_bytes)
                .and_then(|total| total.checked_add(added_bytes))
                .is_some_and(|total| total <= self.max_bytes)
    }

    fn commit_actions(&mut self, actions: Vec<OwnedRouteAction>) {
        self.queued_bytes += actions
            .iter()
            .map(|entry| entry.charged_bytes)
            .sum::<usize>();
        self.route_actions.extend(actions);
    }

    fn commit_failure_actions(&mut self, actions: Vec<OwnedRouteAction>) {
        // Failure actions replace already-owned route retention with Relay
        // work. They are permitted to use the emergency tail of the queue so
        // ordinary traffic cannot prevent exact fallback from being drained.
        self.commit_actions(actions);
        debug_assert!(
            self.total_frames() <= self.max_frames.saturating_add(FAILURE_ACTION_TAIL_V5)
        );
        debug_assert!(
            self.queued_bytes
                .checked_add(self.route_buffered_bytes())
                .is_some_and(|total| total <= self.max_bytes)
        );
    }
}

enum WorkerCommand {
    Activate {
        path: Fence,
        handoff: Box<ProductionHandoffV4>,
    },
    Cancel {
        path: Fence,
    },
    NetworkChanged {
        path: Fence,
    },
}

pub struct NativeDirectV5 {
    state: Arc<Mutex<State>>,
    commands: Option<SyncSender<WorkerCommand>>,
    worker: Option<JoinHandle<()>>,
    #[cfg_attr(not(test), allow(dead_code))]
    worker_exited: Arc<AtomicBool>,
    control: Arc<Mutex<Option<(Fence, DirectConnectionControl)>>>,
    stop: Arc<AtomicBool>,
}

impl NativeDirectV5 {
    fn new(
        enabled: bool,
        max_frames: usize,
        max_bytes: usize,
        path: Option<Fence>,
    ) -> Result<Self, i32> {
        let state = Arc::new(Mutex::new(State::new(enabled, max_frames, max_bytes, path)));
        let (commands, command_rx) = mpsc::sync_channel(1);
        let worker_exited = Arc::new(AtomicBool::new(false));
        let control = Arc::new(Mutex::new(None));
        let stop = Arc::new(AtomicBool::new(false));
        let worker_state = Arc::clone(&state);
        let worker_exited_flag = Arc::clone(&worker_exited);
        let worker_control = Arc::clone(&control);
        let worker_stop = Arc::clone(&stop);
        let worker = thread::Builder::new()
            .name("termlink-direct-v5".to_owned())
            .spawn(move || {
                worker_loop(command_rx, worker_state, worker_control, worker_stop);
                worker_exited_flag.store(true, Ordering::Release);
            })
            .map_err(|_| V5_ERR_INVALID_HANDLE)?;
        Ok(Self {
            state,
            commands: Some(commands),
            worker: Some(worker),
            worker_exited,
            control,
            stop,
        })
    }

    fn lock_state(&self) -> std::sync::MutexGuard<'_, State> {
        self.state
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    fn path_usage(&self, path: Fence) -> i32 {
        let mut state = self.lock_state();
        match state.path {
            None => {
                state.path = Some(path);
                state.lifecycle = Lifecycle::Active;
                let _ = state.push_event(EVENT_PATH_USED_V5, V5_OK, 0, 0, 0, 0);
                V5_OK
            }
            Some(current)
                if current == path
                    && matches!(state.lifecycle, Lifecycle::Active | Lifecycle::Ready) =>
            {
                V5_OK
            }
            Some(current) if path.epoch > current.epoch => {
                state.clear_for_new_path(path);
                let _ = state.push_event(EVENT_PATH_USED_V5, V5_OK, 0, 0, 0, 0);
                V5_OK
            }
            Some(_) => V5_ERR_STALE_PATH,
        }
    }

    fn attach(&self, path: Fence) -> i32 {
        let state = self.lock_state();
        if let Err(status) = state.check_path(path) {
            return status;
        }
        if !state.enabled {
            return V5_ERR_DISABLED;
        }
        // Production attachment requires the one-shot nominated socket owned
        // by a live V4 attempt. Use `termlink_direct_promote_v5`.
        V5_ERR_NOT_READY
    }

    fn promote(&self, v4_bridge: &v4::NativeBridgeV4, promote: &TermLinkDirectPromoteV5) -> i32 {
        let path = match checked_path(&promote.path) {
            Ok(path) => path,
            Err(status) => return status,
        };
        let local_outbound_direction = match decode_direction(promote.local_outbound_direction) {
            Ok(value) => value,
            Err(status) => return status,
        };
        let commit = match promote_to_v4(promote) {
            Ok(value) => value,
            Err(status) => return status,
        };
        {
            let mut state = self.lock_state();
            if !state.enabled {
                return V5_ERR_DISABLED;
            }
            match state.path {
                None => state.path = Some(path),
                Some(current)
                    if current == path
                        && state.lifecycle == Lifecycle::Active
                        && state.local_outbound_direction.is_none() => {}
                Some(current) if path.epoch > current.epoch => state.clear_for_new_path(path),
                Some(current) if current == path => return V5_ERR_CANCELLED,
                Some(_) => return V5_ERR_STALE_PATH,
            }
            state.local_outbound_direction = Some(local_outbound_direction);
        }
        let completion = match v4_bridge.accept_production_commit_for_v5(commit) {
            Ok(value) => value,
            Err(status) => {
                let status = map_v4_status(status);
                fail_state(&self.state, path, status);
                return status;
            }
        };
        let handoff =
            match completion.recv_timeout(Duration::from_millis(u64::from(promote.timeout_ms))) {
                Ok(Ok(value)) => value,
                Ok(Err(reason)) => {
                    let status = map_v4_reason(reason);
                    fail_state(&self.state, path, status);
                    return status;
                }
                Err(_) => {
                    fail_state(&self.state, path, V5_ERR_TIMEOUT);
                    return V5_ERR_TIMEOUT;
                }
            };
        let result = {
            // Keep the state lock from the terminal-state check through the
            // Activate enqueue.  Cancel/network-change therefore linearizes
            // either before promotion (and rejects it) or after the socket is
            // owned by the worker command; it cannot retire the path in the
            // gap and still let promotion report success.
            let state = self.lock_state();
            if state.path != Some(path) || state.lifecycle != Lifecycle::Active {
                return V5_ERR_CANCELLED;
            }
            match self.commands.as_ref().expect("live V5 worker").try_send(
                WorkerCommand::Activate {
                    path,
                    handoff: Box::new(handoff),
                },
            ) {
                Ok(()) => V5_OK,
                Err(TrySendError::Full(_)) => V5_ERR_QUEUE_FULL,
                Err(TrySendError::Disconnected(_)) => V5_ERR_CANCELLED,
            }
        };
        if result != V5_OK {
            fail_state(&self.state, path, result);
        }
        result
    }

    fn receive_envelope(
        &self,
        path: Fence,
        frame_seq_out: &mut u64,
        output: *mut u8,
        output_capacity: usize,
        required_bytes: &mut u32,
    ) -> i32 {
        let mut state = self.lock_state();
        if !state.enabled {
            return V5_ERR_DISABLED;
        }
        if let Err(status) = state.check_fence(path) {
            return status;
        }
        let Some(front) = state.incoming.front() else {
            *required_bytes = 0;
            return V5_NO_DATA;
        };
        let length = front.bytes.len();
        *required_bytes = length as u32;
        if output_capacity < length || output.is_null() {
            // Do not pop or mutate the front item on a short-buffer result.
            return V5_ERR_BUFFER_TOO_SMALL;
        }
        *frame_seq_out = front.frame_seq;
        // SAFETY: The caller supplied a non-null buffer of at least `length`
        // bytes, as checked above. The source is an owned queue entry.
        unsafe { ptr::copy_nonoverlapping(front.bytes.as_ptr(), output, length) };
        let frame = state.incoming.pop_front().expect("front checked above");
        state.queued_bytes -= frame.bytes.len();
        V5_OK
    }

    fn submit_signature_v5(&self, value: TermLinkDirectSignatureV5) -> i32 {
        let path = match checked_path(&value.path) {
            Ok(path) => path,
            Err(status) => return status,
        };
        if value.abi_version != ABI_VERSION_V5
            || value.struct_size as usize != size_of::<TermLinkDirectSignatureV5>()
            || value.request_id == 0
            || value.signature_length == 0
            || value.signature_length as usize > value.signature.len()
            || value.reserved0 != 0
            || value.reserved != [0; 2]
            || value.signature[value.signature_length as usize..]
                .iter()
                .any(|byte| *byte != 0)
        {
            return V5_ERR_INVALID_ARGUMENT;
        }
        let mut state = self.lock_state();
        if let Err(status) = state.check_path(path) {
            return status;
        }
        if state.signatures.len() >= state.max_frames {
            return V5_ERR_QUEUE_FULL;
        }
        let mut signature = TermLinkDirectSignatureV4 {
            abi_version: v4::ABI_VERSION_V4,
            struct_size: size_of::<TermLinkDirectSignatureV4>() as u32,
            epoch: path.epoch,
            attempt_id: path.attempt_id,
            request_id: value.request_id,
            signature_length: value.signature_length,
            reserved0: 0,
            signature: [0; 128],
            reserved: [0; 2],
        };
        signature.signature.copy_from_slice(&value.signature);
        state.signatures.push_back(signature);
        V5_OK
    }

    fn path_info(&self, path: Fence) -> Result<TermLinkDirectPathInfoV5, i32> {
        let state = self.lock_state();
        state.check_fence(path)?;
        Ok(TermLinkDirectPathInfoV5 {
            abi_version: ABI_VERSION_V5,
            struct_size: size_of::<TermLinkDirectPathInfoV5>() as u32,
            path: path_to_ffi(path),
            path_kind: state.path_kind,
            ready: u32::from(state.lifecycle == Lifecycle::Ready),
            direct_path_binding: state.binding,
            reserved: [0; 2],
        })
    }

    fn receive_signing_payload(
        &self,
        path: Fence,
        request_id_out: &mut u64,
        output: *mut u8,
        output_capacity: usize,
        required_bytes: &mut u32,
    ) -> i32 {
        let mut state = self.lock_state();
        if let Err(status) = state.check_path(path) {
            return status;
        }
        let Some(front) = state.signing_payloads.front() else {
            *required_bytes = 0;
            return V5_NO_DATA;
        };
        *required_bytes = front.bytes.len() as u32;
        if output.is_null() || output_capacity < front.bytes.len() {
            return V5_ERR_BUFFER_TOO_SMALL;
        }
        *request_id_out = front.frame_seq;
        // SAFETY: non-null capacity was checked above.
        unsafe { ptr::copy_nonoverlapping(front.bytes.as_ptr(), output, front.bytes.len()) };
        state.signing_payloads.pop_front();
        V5_OK
    }

    fn start_proposal(&self, value: &TermLinkDirectProposalV5, received: bool) -> i32 {
        let (path, direction, proposal) = match checked_proposal(value) {
            Ok(value) => value,
            Err(status) => return status,
        };
        let mut state = self.lock_state();
        if state.lifecycle != Lifecycle::Ready {
            return V5_ERR_NOT_READY;
        }
        if let Err(status) = state.check_path(path) {
            return status;
        }
        let mut staged_route = state.route.clone();
        let transition = if received {
            staged_route.receive_proposal(
                direction,
                path.epoch,
                proposal,
                value.now_ms,
                value.timeout_ms,
            )
        } else {
            staged_route.start_proposal(
                direction,
                path.epoch,
                proposal,
                value.now_ms,
                value.timeout_ms,
            )
        };
        commit_transition(&mut state, path, direction, staged_route, transition)
    }

    fn apply_transition(&self, value: &TermLinkDirectTransitionV5, operation: u32) -> i32 {
        let path = match checked_transition(value) {
            Ok(value) => value,
            Err(status) => return status,
        };
        let direction = match decode_direction(value.direction) {
            Ok(value) => value,
            Err(status) => return status,
        };
        let mut state = self.lock_state();
        if let Err(status) = state.check_path(path) {
            return status;
        }
        let barrier = BarrierId::new(value.barrier_id);
        let mut staged_route = state.route.clone();
        let transition = match operation {
            1 => staged_route.receive_accept(
                direction,
                path.epoch,
                barrier,
                value.barrier_sha256,
                value.accepted == 1,
            ),
            2 => staged_route.receive_commit(
                direction,
                path.epoch,
                barrier,
                value.barrier_sha256,
                value.now_ms,
            ),
            3 => staged_route.commit_echo_sent(direction, path.epoch, barrier, value.now_ms),
            3_000 => staged_route.receive_fallback(
                direction,
                path.epoch,
                (value.barrier_id != [0; 16]).then_some(barrier),
            ),
            _ => return V5_ERR_INVALID_ARGUMENT,
        };
        commit_transition(&mut state, path, direction, staged_route, transition)
    }

    fn update_cursor(&self, value: &TermLinkDirectCursorV5) -> i32 {
        let path = match checked_cursor(value) {
            Ok(value) => value,
            Err(status) => return status,
        };
        let direction = match decode_direction(value.direction) {
            Ok(value) => value,
            Err(status) => return status,
        };
        let mut state = self.lock_state();
        if let Err(status) = state.check_fence(path) {
            return status;
        }
        let session_id = SessionId::new(value.session_id);
        let mut staged_route = state.route.clone();
        match staged_route.update_applied_cursor(direction, session_id, value.highest_contiguous) {
            Ok(Some(transition)) => {
                let status =
                    commit_transition(&mut state, path, direction, staged_route, transition);
                if status == V5_OK {
                    prune_delivered_ingress(
                        &mut state,
                        direction,
                        session_id,
                        value.highest_contiguous,
                    );
                }
                status
            }
            Ok(None) => {
                state.route = staged_route;
                prune_delivered_ingress(
                    &mut state,
                    direction,
                    session_id,
                    value.highest_contiguous,
                );
                V5_OK
            }
            Err(termlink_direct_route::CursorUpdateError::Capacity) => V5_ERR_LIMIT,
        }
    }

    fn deregister_session(&self, value: &TermLinkDirectCursorV5) -> i32 {
        let path = match checked_cursor(value) {
            Ok(value) => value,
            Err(status) => return status,
        };
        if value.highest_contiguous != 0 {
            return V5_ERR_INVALID_ARGUMENT;
        }
        let direction = match decode_direction(value.direction) {
            Ok(value) => value,
            Err(status) => return status,
        };
        let session_id = SessionId::new(value.session_id);
        let mut state = self.lock_state();
        if let Err(status) = state.check_fence(path) {
            return status;
        }

        let old_carrier = state.route.authoritative_carrier(direction);
        let old_session_state = state.route.session_state(direction, session_id);
        let mut staged_route = state.route.clone();
        let batch = staged_route.deregister_session(direction, session_id);
        let mut actions = stage_batch_actions(path, &batch, &staged_route);
        let new_carrier = staged_route.authoritative_carrier(direction);
        if new_carrier != old_carrier {
            actions.push(carrier_action(path, direction, new_carrier));
        } else if old_session_state != SessionRouteState::RelayOnly
            && staged_route.session_state(direction, session_id) == SessionRouteState::RelayOnly
        {
            actions.push(session_carrier_action(
                path,
                direction,
                session_id,
                Carrier::Relay,
            ));
        }
        state.route = staged_route;
        // Do not remove an Envelope that already owns a Direct frame sequence.
        // Skipping it would create a reliable-stream sequence hole and retire
        // the shared path when the next frame arrives. Relay recovery above is
        // authoritative; this already-owned Direct duplicate drains in order
        // and the peer's retired-session fence discards it.
        remove_session_ingress(&mut state, direction, session_id);
        if state.can_commit_actions_and_work(&actions, &[]) {
            state.commit_actions(actions);
        } else {
            state.commit_failure_actions(actions);
        }
        V5_OK
    }

    fn tick(&self, value: &TermLinkDirectTransitionV5) -> i32 {
        let path = match checked_transition(value) {
            Ok(value) => value,
            Err(status) => return status,
        };
        let direction = match decode_direction(value.direction) {
            Ok(value) => value,
            Err(status) => return status,
        };
        let mut state = self.lock_state();
        if let Err(status) = state.check_path(path) {
            return status;
        }
        let mut staged_route = state.route.clone();
        if let Some(transition) = staged_route.poll_timeout(direction, value.now_ms) {
            commit_transition(&mut state, path, direction, staged_route, transition)
        } else {
            V5_OK
        }
    }

    fn route_envelope(&self, value: &TermLinkDirectRouteEnvelopeV5, bytes: &[u8]) -> i32 {
        let path = match checked_route_envelope(value) {
            Ok(value) => value,
            Err(status) => return status,
        };
        let direction = match decode_direction(value.direction) {
            Ok(value) => value,
            Err(status) => return status,
        };
        if bytes.is_empty() || bytes.len() > MAX_ENVELOPE_BYTES_V5 {
            return V5_ERR_LIMIT;
        }
        let mut state = self.lock_state();
        if let Err(status) = state.check_path(path) {
            return status;
        }
        if value.route_eligible == 1 && state.local_outbound_direction != Some(direction) {
            return V5_ERR_INVALID_ARGUMENT;
        }
        let envelope = OpaqueEnvelope::new(
            SessionId::new(value.session_id),
            value.envelope_sequence,
            Arc::<[u8]>::from(bytes),
        );
        let batch = if value.route_eligible == 1 {
            let mut staged_route = state.route.clone();
            let batch = staged_route.route_outbound(direction, envelope, value.now_ms);
            let staged_actions = stage_batch_actions(path, &batch, &staged_route);
            let direct_work = match direct_work_from_batch(&batch) {
                Ok(value) => value,
                Err(status) => return status,
            };
            if !state.can_commit_with_route(&staged_route, &staged_actions, &direct_work) {
                // The current call has not crossed the ownership boundary.
                // Fail the previously committed route state and preserve only
                // bytes accepted by earlier successful calls. The caller gets
                // QUEUE_FULL and may retry the current Envelope over Relay.
                let mut failed_route = state.route.clone();
                let old_carriers = [
                    failed_route.authoritative_carrier(Direction::PcToAndroid),
                    failed_route.authoritative_carrier(Direction::AndroidToPc),
                ];
                let fallback = failed_route.direct_failed(path.epoch);
                let mut failure_actions = stage_batch_actions(path, &fallback, &failed_route);
                for (failed_direction, old_carrier) in
                    [Direction::PcToAndroid, Direction::AndroidToPc]
                        .into_iter()
                        .zip(old_carriers)
                {
                    if !matches!(old_carrier, Carrier::Relay) {
                        let mut record = base_route_action(
                            path,
                            ROUTE_ACTION_CARRIER_V5,
                            encode_direction(failed_direction),
                        );
                        record.carrier = ROUTE_CARRIER_RELAY_V5;
                        failure_actions.push(OwnedRouteAction {
                            record,
                            bytes: Arc::from([]),
                            charged_bytes: 0,
                        });
                    }
                }
                state.route = failed_route;
                state.outgoing.clear();
                state.lifecycle = Lifecycle::Failed;
                state.commit_failure_actions(failure_actions);
                let _ = state.push_event(EVENT_DIRECT_FAILED_V5, V5_ERR_QUEUE_FULL, 0, 0, 0, 0);
                if let Some((active, control)) = self
                    .control
                    .lock()
                    .unwrap_or_else(|entry| entry.into_inner())
                    .as_ref()
                    && *active == path
                {
                    control.cancel();
                }
                return V5_ERR_QUEUE_FULL;
            }
            state.route = staged_route;
            state.commit_actions(staged_actions);
            state.outgoing.extend(direct_work);
            return V5_OK;
        } else {
            RouteBatch {
                transmissions: vec![EnvelopeTransmission {
                    direction,
                    carrier: Carrier::Relay,
                    direct_frame_seq: None,
                    envelope,
                }],
                controls: Vec::new(),
                fallback_reason: None,
            }
        };
        enqueue_batch(&mut state, path, batch)
    }

    fn poll_route_action(
        &self,
        action: &mut TermLinkDirectRouteActionV5,
        output: *mut u8,
        output_capacity: usize,
        required_bytes: &mut u32,
    ) -> i32 {
        let mut state = self.lock_state();
        let Some(front) = state.route_actions.front() else {
            *required_bytes = 0;
            return V5_NO_DATA;
        };
        *required_bytes = front.bytes.len() as u32;
        if !front.bytes.is_empty() && (output.is_null() || output_capacity < front.bytes.len()) {
            return V5_ERR_BUFFER_TOO_SMALL;
        }
        *action = front.record;
        if !front.bytes.is_empty() {
            // SAFETY: non-null capacity was checked above.
            unsafe { ptr::copy_nonoverlapping(front.bytes.as_ptr(), output, front.bytes.len()) };
        }
        let charged_bytes = front.charged_bytes;
        state.route_actions.pop_front();
        state.queued_bytes -= charged_bytes;
        V5_OK
    }

    fn arbitrate_ingress(&self, value: &TermLinkDirectIngressV5, bytes: &[u8]) -> i32 {
        let path = match checked_ingress(value) {
            Ok(value) => value,
            Err(status) => return status,
        };
        let direction = match decode_direction(value.direction) {
            Ok(value) => value,
            Err(status) => return status,
        };
        if bytes.is_empty() || bytes.len() > MAX_ENVELOPE_BYTES_V5 {
            return V5_ERR_LIMIT;
        }
        let mut state = self.lock_state();
        if let Err(status) = state.check_fence(path) {
            return status;
        }
        let session_id = SessionId::new(value.session_id);
        let direct_grace_remaining = if value.source == INGRESS_CARRIER_DIRECT_V5 {
            state.route.direct_ingress_grace_remaining_ms(
                direction,
                session_id,
                value.envelope_sequence,
                value.now_ms,
            )
        } else {
            None
        };
        if matches!(
            state.lifecycle,
            Lifecycle::Cancelled | Lifecycle::NetworkChanged | Lifecycle::Failed
        ) {
            return V5_ERR_CANCELLED;
        }
        if value.source == INGRESS_CARRIER_DIRECT_V5
            && state.route.is_retired_direct_session(direction, session_id)
        {
            let record = ingress_action(
                value,
                path,
                ROUTE_ACTION_INGRESS_DROP_V5,
                bytes.len(),
                state.path_kind,
            );
            return if enqueue_action(&mut state, record, Vec::new()).is_ok() {
                V5_OK
            } else {
                V5_ERR_QUEUE_FULL
            };
        }
        if value.source == INGRESS_CARRIER_DIRECT_V5
            && (state.lifecycle != Lifecycle::Ready
                || !matches!(state.path_kind, PATH_KIND_LAN_V5 | PATH_KIND_NAT_V5)
                || direct_grace_remaining.is_none())
        {
            // A direction-level Direct state is insufficient: every incoming
            // Envelope must belong to a committed session barrier and be at or
            // beyond its first Direct sequence. Violation retires only this
            // optional path; Relay remains authoritative and alive.
            fail_direct_in_state(&mut state, path, V5_ERR_PROTOCOL);
            drop(state);
            self.cancel_path_control(path);
            return V5_ERR_PROTOCOL;
        }
        if let Err(status) = flush_ingress_deadlines(&mut state, path, value.now_ms) {
            return status;
        }
        let digest = sha256(bytes);
        if value.envelope_sequence <= state.route.applied_cursor(direction, session_id) {
            let record = ingress_action(
                value,
                path,
                ROUTE_ACTION_INGRESS_DROP_V5,
                bytes.len(),
                state.path_kind,
            );
            return if enqueue_action(&mut state, record, Vec::new()).is_ok() {
                V5_OK
            } else {
                V5_ERR_QUEUE_FULL
            };
        }
        if let Some(delivered) = state.delivered_ingress.iter().find(|entry| {
            entry.direction == direction
                && entry.session_id == session_id
                && entry.envelope_sequence == value.envelope_sequence
        }) {
            if delivered.digest != digest || delivered.bytes.as_ref() != bytes {
                fail_direct_in_state(&mut state, path, V5_ERR_PROTOCOL);
                self.cancel_path_control(path);
                return V5_ERR_PROTOCOL;
            }
            let record = ingress_action(
                value,
                path,
                ROUTE_ACTION_INGRESS_DROP_V5,
                bytes.len(),
                state.path_kind,
            );
            return if enqueue_action(&mut state, record, Vec::new()).is_ok() {
                V5_OK
            } else {
                V5_ERR_QUEUE_FULL
            };
        }
        if let Some(index) = state.ingress.iter().position(|entry| {
            entry.direction == direction
                && entry.session_id == session_id
                && entry.envelope_sequence == value.envelope_sequence
        }) {
            let existing_bytes = Arc::clone(&state.ingress[index].bytes);
            let existing_source_direct = state.ingress[index].source_direct;
            if existing_bytes.as_ref() != bytes {
                if existing_source_direct && value.source == INGRESS_CARRIER_RELAY_V5 {
                    // Relay remains authoritative during the duplicate grace
                    // window. Retire Direct, discard the conflicting held
                    // copy, and deliver the exact Relay bytes once.
                    let delivery_bytes = Arc::<[u8]>::from(bytes);
                    if let Err(status) = commit_ingress_delivery(
                        &mut state,
                        path,
                        value,
                        direction,
                        session_id,
                        digest,
                        delivery_bytes,
                        Some(index),
                    ) {
                        return status;
                    }
                    fail_direct_in_state(&mut state, path, V5_ERR_PROTOCOL);
                    self.cancel_path_control(path);
                    return V5_OK;
                }
                let held = state.ingress.remove(index).expect("index found");
                state.queued_bytes = state.queued_bytes.saturating_sub(held.bytes.len());
                fail_direct_in_state(&mut state, path, V5_ERR_PROTOCOL);
                self.cancel_path_control(path);
                return V5_ERR_PROTOCOL;
            }
            if existing_source_direct && value.source == INGRESS_CARRIER_RELAY_V5 {
                return match commit_ingress_delivery(
                    &mut state,
                    path,
                    value,
                    direction,
                    session_id,
                    digest,
                    existing_bytes,
                    Some(index),
                ) {
                    Ok(()) => V5_OK,
                    Err(status) => status,
                };
            }
            let record = ingress_action(
                value,
                path,
                ROUTE_ACTION_INGRESS_DROP_V5,
                bytes.len(),
                state.path_kind,
            );
            return if enqueue_action(&mut state, record, Vec::new()).is_ok() {
                V5_OK
            } else {
                V5_ERR_QUEUE_FULL
            };
        }
        let native_grace_remaining = direct_grace_remaining.unwrap_or(0);
        if value.source == INGRESS_CARRIER_DIRECT_V5 && native_grace_remaining > 0 {
            let record = ingress_action(
                value,
                path,
                ROUTE_ACTION_INGRESS_HOLD_V5,
                bytes.len(),
                state.path_kind,
            );
            let action = OwnedRouteAction {
                record,
                bytes: Arc::from([]),
                charged_bytes: 0,
            };
            if state.total_frames().saturating_add(2) > state.max_frames
                || !state.queue_available(bytes.len())
                || !state.can_commit_actions_and_work(std::slice::from_ref(&action), &[])
            {
                return V5_ERR_QUEUE_FULL;
            }
            state.commit_actions(vec![action]);
            state.queued_bytes += bytes.len();
            state.ingress.push_back(IngressEntry {
                direction,
                session_id,
                envelope_sequence: value.envelope_sequence,
                bytes: Arc::from(bytes),
                source_direct: true,
                deadline_ms: value
                    .now_ms
                    .saturating_add(u64::from(native_grace_remaining)),
            });
            return V5_OK;
        }
        let delivery_bytes = Arc::<[u8]>::from(bytes);
        match commit_ingress_delivery(
            &mut state,
            path,
            value,
            direction,
            session_id,
            digest,
            delivery_bytes,
            None,
        ) {
            Ok(()) => V5_OK,
            Err(status) => status,
        }
    }

    fn cancel_path_control(&self, path: Fence) {
        if let Some((active, control)) = self
            .control
            .lock()
            .unwrap_or_else(|entry| entry.into_inner())
            .as_ref()
            && *active == path
        {
            control.cancel();
        }
    }

    fn tick_ingress(&self, path: Fence, now_ms: u64) -> i32 {
        let mut state = self.lock_state();
        if let Err(status) = state.check_fence(path) {
            return status;
        }
        if matches!(
            state.lifecycle,
            Lifecycle::Cancelled | Lifecycle::NetworkChanged
        ) {
            return V5_ERR_CANCELLED;
        }
        match flush_ingress_deadlines(&mut state, path, now_ms) {
            Ok(()) => V5_OK,
            Err(status) => status,
        }
    }

    fn poll_event(&self) -> Option<TermLinkDirectEventV5> {
        let mut state = self.lock_state();
        if state.event_overflow {
            state.event_overflow = false;
            let (path_epoch, attempt_id) = state.path_metadata();
            state.events.clear();
            return Some(TermLinkDirectEventV5 {
                abi_version: ABI_VERSION_V5,
                struct_size: std::mem::size_of::<TermLinkDirectEventV5>() as u32,
                kind: EVENT_QUEUE_LIMIT_V5,
                status: V5_ERR_QUEUE_FULL,
                flags: 0,
                reserved0: 0,
                path_epoch,
                attempt_id,
                frame_seq: 0,
                payload_bytes: 0,
                released_frames: 0,
                reserved: [0; 2],
            });
        }
        state.events.pop_front()
    }

    fn control(&self, command: WorkerCommand) -> i32 {
        match command {
            WorkerCommand::Cancel { path } => retire_state(&self.state, &self.control, path, false),
            WorkerCommand::NetworkChanged { path } => {
                retire_state(&self.state, &self.control, path, true)
            }
            WorkerCommand::Activate { .. } => V5_ERR_INVALID_ARGUMENT,
        }
    }

    #[cfg(test)]
    fn inject_received(&self, path: Fence, frame_seq: u64, bytes: Vec<u8>) -> i32 {
        if bytes.is_empty() || bytes.len() > MAX_ENVELOPE_BYTES_V5 {
            return V5_ERR_LIMIT;
        }
        if !(FIRST_DATA_FRAME_SEQ_V5..=MAX_FRAME_SEQ_V5).contains(&frame_seq) {
            return V5_ERR_PROTOCOL;
        }
        let payload_bytes = bytes.len() as u32;
        let mut state = self.lock_state();
        if let Err(status) = state.check_path(path) {
            return status;
        }
        if frame_seq <= state.last_received_seq {
            return V5_ERR_PROTOCOL;
        }
        if !state.queue_available(bytes.len()) {
            return V5_ERR_QUEUE_FULL;
        }
        state.queued_bytes += bytes.len();
        state.incoming.push_back(OwnedFrame { frame_seq, bytes });
        state.last_received_seq = frame_seq;
        if !state.push_event(
            EVENT_ENVELOPE_AVAILABLE_V5,
            V5_OK,
            EVENT_FLAG_INBOUND_V5,
            frame_seq,
            payload_bytes,
            0,
        ) {
            return V5_ERR_QUEUE_FULL;
        }
        V5_OK
    }
}

impl Drop for NativeDirectV5 {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        if let Some((_, control)) = self
            .control
            .lock()
            .unwrap_or_else(|entry| entry.into_inner())
            .take()
        {
            control.cancel();
        }
        // Dropping the command sender wakes the worker. The QUIC control above
        // wakes blocked reader/writer I/O; the join is therefore bounded by no
        // network timeout and no native code survives the handle.
        self.commands.take();
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

fn worker_loop(
    commands: Receiver<WorkerCommand>,
    state: Arc<Mutex<State>>,
    control_slot: Arc<Mutex<Option<(Fence, DirectConnectionControl)>>>,
    stop: Arc<AtomicBool>,
) {
    while !stop.load(Ordering::Acquire) {
        let command = match commands.recv_timeout(Duration::from_millis(20)) {
            Ok(value) => value,
            Err(mpsc::RecvTimeoutError::Timeout) => continue,
            Err(mpsc::RecvTimeoutError::Disconnected) => break,
        };
        match command {
            WorkerCommand::Activate { path, handoff } => {
                if is_retired(&state, path) {
                    continue;
                }
                let runtime = match tokio::runtime::Builder::new_current_thread()
                    .enable_all()
                    .build()
                {
                    Ok(value) => value,
                    Err(_) => {
                        fail_state(&state, path, V5_ERR_PROTOCOL);
                        continue;
                    }
                };
                if let Err(status) = runtime.block_on(run_production_path(
                    *handoff,
                    path,
                    &state,
                    &control_slot,
                    &stop,
                )) && !is_retired(&state, path)
                {
                    fail_state(&state, path, status);
                }
                control_slot
                    .lock()
                    .unwrap_or_else(|entry| entry.into_inner())
                    .take();
            }
            WorkerCommand::Cancel { path } => {
                let _ = retire_state(&state, &control_slot, path, false);
            }
            WorkerCommand::NetworkChanged { path } => {
                let _ = retire_state(&state, &control_slot, path, true);
            }
        }
    }
}

async fn run_production_path(
    handoff: ProductionHandoffV4,
    path: Fence,
    state: &Arc<Mutex<State>>,
    control_slot: &Arc<Mutex<Option<(Fence, DirectConnectionControl)>>>,
    stop: &AtomicBool,
) -> Result<(), i32> {
    let begin = handoff.begin;
    let commit = handoff.commit;
    let role = if commit.role == v4::ROLE_INITIATOR_V4 {
        PeerRole::Initiator
    } else {
        PeerRole::Responder
    };
    let remote = v4::decode_socket(
        v4::remote_candidate(commit.winning_pair, commit.role).endpoint,
        false,
    )
    .map_err(|_| V5_ERR_PROTOCOL)?;
    let ticket = verify_direct_path_ticket(
        &begin.ticket[..begin.ticket_length as usize],
        &begin.server_signing_public_key,
        &DirectTicketExpectation {
            connection_id: begin.connection_id,
            path_epoch: begin.epoch,
            local_device_id: begin.local_device_id,
            peer_device_id: begin.peer_device_id,
            local_role: if role == PeerRole::Initiator {
                DirectTicketRole::Initiator
            } else {
                DirectTicketRole::Responder
            },
            now_ms: commit.now_ms,
        },
    )
    .map_err(map_ticket_error)?;
    let ticket_expires_at_ms = ticket.expires_at_ms;
    if ticket_expires_at_ms <= now_ms() {
        return Err(V5_ERR_TICKET_EXPIRED);
    }
    {
        let mut locked = state.lock().unwrap_or_else(|entry| entry.into_inner());
        locked.ticket_expiry_ms = ticket_expires_at_ms;
        locked.renewal_due_emitted = false;
        locked.ticket_renewal_binding = Some(DirectTicketExpectation {
            connection_id: begin.connection_id,
            path_epoch: begin.epoch,
            local_device_id: begin.local_device_id,
            peer_device_id: begin.peer_device_id,
            local_role: if role == PeerRole::Initiator {
                DirectTicketRole::Initiator
            } else {
                DirectTicketRole::Responder
            },
            now_ms: commit.now_ms,
        });
        locked.server_signing_key = Some(begin.server_signing_public_key);
    }
    let endpoint = if role == PeerRole::Initiator {
        DirectEndpoint::client(
            handoff.handoff,
            remote,
            handoff.identity,
            commit.peer_certificate_sha256,
        )
    } else {
        DirectEndpoint::server(
            handoff.handoff,
            remote,
            handoff.identity,
            commit.peer_certificate_sha256,
        )
    }
    .map_err(|_| V5_ERR_PROTOCOL)?;
    let connection = tokio::select! {
        () = wait_cancelled(state, path, stop) => {
            endpoint.close();
            return Err(V5_ERR_CANCELLED);
        }
        result = async {
            if role == PeerRole::Initiator {
                endpoint.connect_pinned(remote, commit.peer_certificate_sha256).await
            } else {
                endpoint.accept_pinned(remote, commit.peer_certificate_sha256).await
            }
        } => result.map_err(|_| V5_ERR_PROTOCOL)?,
    };
    let context = PeerBindingContext {
        ticket_sha256: ticket.ticket_sha256,
        connection_id: begin.connection_id,
        path_epoch: begin.epoch,
    };
    let exporter = tls_exporter_sha256(&connection, &context).map_err(|_| V5_ERR_PROTOCOL)?;
    let proof = PeerProofRequest {
        ticket_sha256: context.ticket_sha256,
        role,
        device_id: begin.local_device_id,
        peer_device_id: begin.peer_device_id,
        connection_id: begin.connection_id,
        path_epoch: begin.epoch,
        proof_nonce: commit.local_proof_nonce[..commit.local_proof_nonce_length as usize].to_vec(),
        tls_exporter_sha256: exporter,
    };
    let transcript = proof.transcript().map_err(|_| V5_ERR_PROTOCOL)?;
    let request_id = publish_sign_request(state, path, &transcript).map_err(|_| V5_ERR_PROTOCOL)?;
    let signature = wait_signature(
        state,
        path,
        request_id,
        Duration::from_millis(u64::from(commit.timeout_ms)),
        stop,
    )
    .await
    .map_err(|_| V5_ERR_TIMEOUT)?;
    let verifier = Ed25519PeerProofVerifier::new(commit.peer_device_signing_public_key);
    let authenticated = tokio::select! {
        () = wait_cancelled(state, path, stop) => {
            endpoint.close();
            return Err(V5_ERR_CANCELLED);
        }
        result = exchange_and_authenticate_direct_connection(
            &endpoint,
            connection,
            &context,
            ExpectedPeer {
                role: if role == PeerRole::Initiator { PeerRole::Responder } else { PeerRole::Initiator },
                device_id: begin.peer_device_id,
                local_device_id: begin.local_device_id,
            },
            &begin.ticket[..begin.ticket_length as usize],
            &proof,
            &signature,
            &verifier,
        ) => result.map_err(|_| V5_ERR_PROTOCOL)?,
    };
    if ticket_expires_at_ms <= now_ms() {
        authenticated.control().cancel();
        return Err(V5_ERR_TICKET_EXPIRED);
    }
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
    let binding = direct_path_binding_sha256(DirectPathBindingInputs {
        ticket_sha256: context.ticket_sha256,
        initiator_candidate_id: commit.winning_pair.initiator_candidate.candidate_id,
        responder_candidate_id: commit.winning_pair.responder_candidate.candidate_id,
        initiator_certificate_sha256,
        responder_certificate_sha256,
        initiator_proof_sha256,
        responder_proof_sha256,
        tls_exporter_sha256: exporter,
    });
    let kind = if commit.winning_pair.initiator_candidate.kind == v4::CANDIDATE_HOST_V4
        && commit.winning_pair.responder_candidate.kind == v4::CANDIDATE_HOST_V4
    {
        AuthenticatedPathKind::LocalAreaNetwork
    } else {
        AuthenticatedPathKind::NatTraversal
    };
    let control = authenticated.control();
    *control_slot
        .lock()
        .unwrap_or_else(|entry| entry.into_inner()) = Some((path, control.clone()));
    {
        let mut locked = state.lock().unwrap_or_else(|entry| entry.into_inner());
        locked.check_path(path)?;
        if ticket_expires_at_ms <= now_ms() {
            control.cancel();
            return Err(V5_ERR_TICKET_EXPIRED);
        }
        let mut staged_route = locked.route.clone();
        let installed = staged_route.install_authenticated_path(
            path.epoch,
            kind,
            DirectPathBinding::new(binding),
        );
        if !matches!(
            installed.outcome,
            termlink_direct_route::EpochInstallOutcome::Installed
        ) {
            control.cancel();
            return Err(V5_ERR_PROTOCOL);
        }
        let replacement_actions = stage_batch_actions(path, &installed.routes, &staged_route);
        locked.route = staged_route;
        locked.commit_failure_actions(replacement_actions);
        locked.lifecycle = Lifecycle::Ready;
        locked.binding = binding;
        locked.path_kind = if kind == AuthenticatedPathKind::LocalAreaNetwork {
            PATH_KIND_LAN_V5
        } else {
            PATH_KIND_NAT_V5
        };
        let flags = if locked.path_kind == PATH_KIND_LAN_V5 {
            EVENT_FLAG_LAN_V5
        } else {
            EVENT_FLAG_NAT_V5
        };
        if !locked.push_event(EVENT_PATH_READY_V5, V5_OK, flags, 0, 0, 0) {
            control.cancel();
            return Err(V5_ERR_QUEUE_FULL);
        }
    }
    let (mut writer, mut reader, _) = authenticated.into_parts();
    data_loop(
        state,
        path,
        &mut writer,
        &mut reader,
        &control,
        stop,
    )
    .await
    .map_err(|_| V5_ERR_PROTOCOL)
}

async fn data_loop(
    state: &Arc<Mutex<State>>,
    path: Fence,
    writer: &mut DirectDataWriter,
    reader: &mut DirectDataReader,
    control: &DirectConnectionControl,
    stop: &AtomicBool,
) -> Result<(), ()> {
    let direction = {
        let locked = state.lock().unwrap_or_else(|entry| entry.into_inner());
        locked.local_outbound_direction.ok_or(())?
    };
    let tracker = Arc::new(Mutex::new(
        DirectLivenessTracker::new(Duration::from_secs(10)).map_err(|_| ())?,
    ));
    let writer_task = writer_loop(state, path, writer, &tracker, stop);
    let reader_task = reader_loop(state, path, direction.opposite(), reader, &tracker, stop);
    let liveness_task = liveness_loop(state, path, direction, &tracker, stop);
    // Ticket renewal (audit #2): the data loop polls the applied expiry once
    // a second. When the remaining lifetime falls inside the renewal window
    // the host receives EVENT_RENEWAL_DUE once and is expected to fetch a
    // server-renewed ticket and apply it via termlink_direct_renew_apply_v5;
    // the applied expiry then slides forward and the loop continues. Only a
    // grace period past the applied expiry without a renewal tears the path
    // down — the pre-renewal hard stop that forced a rebuild every ticket
    // generation.
    const RENEWAL_LEAD_MS: i64 = 60_000;
    const RENEWAL_GRACE_MS: i64 = 30_000;
    const RENEWAL_POLL_MS: u64 = 1_000;
    let mut renewal_poll = tokio::time::interval(Duration::from_millis(RENEWAL_POLL_MS));
    renewal_poll.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
    let mut renewal_due_emitted = false;
    tokio::pin!(writer_task, reader_task, liveness_task);
    let result = loop {
        let hard_deadline_ms = {
            let locked = state.lock().unwrap_or_else(|entry| entry.into_inner());
            locked.ticket_expiry_ms.saturating_add(RENEWAL_GRACE_MS)
        };
        tokio::select! {
            biased;
            () = control.cancelled() => {
                break data_loop_cancelled_result(state, path, stop);
            },
            result = &mut writer_task => break result,
            result = &mut reader_task => break result,
            result = &mut liveness_task => break result,
            _ = renewal_poll.tick() => {
                let now = now_ms();
                if now >= hard_deadline_ms {
                    fail_state(state, path, V5_ERR_TICKET_EXPIRED);
                    break Err(());
                }
                if now >= hard_deadline_ms - RENEWAL_LEAD_MS && !renewal_due_emitted {
                    renewal_due_emitted = true;
                    let mut locked = state.lock().unwrap_or_else(|entry| entry.into_inner());
                    locked.renewal_due_emitted = true;
                    let _ = locked.push_event(EVENT_RENEWAL_DUE_V5, V5_OK, 0, 0, 0, 0);
                }
            }
        }
    };
    control.cancel();
    result
}

fn data_loop_cancelled_result(
    state: &Arc<Mutex<State>>,
    path: Fence,
    stop: &AtomicBool,
) -> Result<(), ()> {
    // `DirectDataReader`/`DirectDataWriter` retire their shared QUIC control
    // before returning an I/O/protocol error. That wakes this select branch at
    // the same time as their Err result. Only a path already retired by the V5
    // owner (or bridge destruction) is an expected cancellation; otherwise the
    // control closure is a Direct failure and must reach worker-level fallback.
    if stop.load(Ordering::Acquire) || is_retired(state, path) {
        Ok(())
    } else {
        Err(())
    }
}

/// Applies a server-renewed direct-transport ticket to a live V5 path
/// (audit #2). The ticket is verified against the same
/// connection/epoch/device binding and server signing key as the original;
/// the applied expiry must be strictly newer than the currently applied one
/// (monotonic, rollback-proof). On success the data loop slides its
/// hard-stop forward; the host is expected to have requested the renewal
/// from the server after EVENT_RENEWAL_DUE.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn termlink_direct_renew_apply_v5(
    handle: *mut c_void,
    ticket: *const u8,
    ticket_length: usize,
) -> i32 {
    if handle.is_null() || ticket.is_null() || ticket_length == 0
        || ticket_length > MAX_DIRECT_TICKET_BYTES
    {
        return V5_ERR_INVALID_ARGUMENT;
    }
    let packet = unsafe { std::slice::from_raw_parts(ticket, ticket_length) };
    catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: handle must be live and externally synchronized.
        let Some(bridge) = (unsafe { bridge_ref(handle) }) else {
            return V5_ERR_INVALID_HANDLE;
        };
        let mut locked = bridge.lock_state();
        let Some(binding) = locked.ticket_renewal_binding else {
            return V5_ERR_PROTOCOL;
        };
        let Some(server_key) = locked.server_signing_key else {
            return V5_ERR_PROTOCOL;
        };
        let expectation = DirectTicketExpectation {
            now_ms: now_ms(),
            ..binding
        };
        let verified = match verify_direct_path_ticket(packet, &server_key, &expectation) {
            Ok(verified) => verified,
            Err(_) => return V5_ERR_TICKET_EXPIRED,
        };
        if verified.expires_at_ms <= locked.ticket_expiry_ms {
            return V5_ERR_TICKET_EXPIRED;
        }
        locked.ticket_expiry_ms = verified.expires_at_ms;
        locked.renewal_due_emitted = false;
        V5_OK
    }))
    .unwrap_or(V5_ERR_PROTOCOL)
}

fn duration_until_ms(deadline_ms: i64) -> Duration {
    let remaining = deadline_ms.saturating_sub(now_ms());
    Duration::from_millis(u64::try_from(remaining).unwrap_or(0))
}

fn map_ticket_error(error: TicketError) -> i32 {
    match error {
        TicketError::Expired => V5_ERR_TICKET_EXPIRED,
        _ => V5_ERR_PROTOCOL,
    }
}

async fn writer_loop(
    state: &Arc<Mutex<State>>,
    path: Fence,
    writer: &mut DirectDataWriter,
    tracker: &Arc<Mutex<DirectLivenessTracker>>,
    stop: &AtomicBool,
) -> Result<(), ()> {
    loop {
        if stop.load(Ordering::Acquire) || is_retired(state, path) {
            return Ok(());
        }
        if let Some((expected_seq, body, kind, flags, payload_bytes)) = next_outbound(state, path) {
            let actual_seq = writer.send(body).await.map_err(|_| ())?;
            if actual_seq != expected_seq {
                return Err(());
            }
            mark_sent(state, path, actual_seq, kind, flags, payload_bytes, tracker)?;
            continue;
        }
        tokio::time::sleep(Duration::from_millis(2)).await;
    }
}

async fn reader_loop(
    state: &Arc<Mutex<State>>,
    path: Fence,
    inbound_direction: Direction,
    reader: &mut DirectDataReader,
    tracker: &Arc<Mutex<DirectLivenessTracker>>,
    stop: &AtomicBool,
) -> Result<(), ()> {
    loop {
        if stop.load(Ordering::Acquire) || is_retired(state, path) {
            return Ok(());
        }
        let frame = reader.receive().await.map_err(|_| ())?;
        receive_frame(state, path, inbound_direction, frame, tracker)?;
    }
}

async fn liveness_loop(
    state: &Arc<Mutex<State>>,
    path: Fence,
    outbound_direction: Direction,
    tracker: &Arc<Mutex<DirectLivenessTracker>>,
    stop: &AtomicBool,
) -> Result<(), ()> {
    let mut counter = 0_u64;
    loop {
        tokio::time::sleep(Duration::from_secs(3)).await;
        if stop.load(Ordering::Acquire) || is_retired(state, path) {
            return Ok(());
        }
        {
            let tracker = tracker.lock().unwrap_or_else(|entry| entry.into_inner());
            if tracker.check_timeout(Instant::now()).is_err() {
                drop(tracker);
                fail_state(state, path, V5_ERR_TIMEOUT);
                return Err(());
            }
            if tracker.has_pending_ping() {
                continue;
            }
        }
        let mut locked = state.lock().unwrap_or_else(|entry| entry.into_inner());
        locked.check_path(path).map_err(|_| ())?;
        if locked
            .outgoing
            .iter()
            .any(|entry| matches!(entry, OutboundWork::Ping { .. }))
        {
            continue;
        }
        if locked.outgoing.len() >= locked.max_frames {
            return Err(());
        }
        let frame_seq = locked
            .route
            .reserve_outbound_control_frame(outbound_direction, path.epoch)
            .map_err(|_| ())?;
        counter = counter.checked_add(1).ok_or(())?;
        let timestamp_ms = now_ms();
        let mut input = Vec::with_capacity(48);
        input.extend_from_slice(&path.connection_id);
        input.extend_from_slice(&path.attempt_id);
        input.extend_from_slice(&counter.to_le_bytes());
        input.extend_from_slice(&timestamp_ms.to_le_bytes());
        let digest = sha256(&input);
        let mut nonce = [0_u8; 16];
        nonce.copy_from_slice(&digest[..16]);
        locked.outgoing.push_back(OutboundWork::Ping {
            frame_seq,
            nonce,
            timestamp_ms,
        });
    }
}

fn next_outbound(
    state: &Arc<Mutex<State>>,
    path: Fence,
) -> Option<(u64, DirectFrameBody, u32, u32, u32)> {
    let locked = state.lock().unwrap_or_else(|entry| entry.into_inner());
    if locked.lifecycle != Lifecycle::Ready || locked.check_path(path).is_err() {
        return None;
    }
    match locked.outgoing.front()?.clone() {
        OutboundWork::Envelope { frame_seq, bytes } => Some((
            frame_seq,
            DirectFrameBody::RealtimeEnvelope(bytes.to_vec()),
            EVENT_ENVELOPE_QUEUED_V5,
            EVENT_FLAG_OUTBOUND_V5 | EVENT_FLAG_TRANSPORT_COPY_V5,
            bytes.len() as u32,
        )),
        OutboundWork::Receipt {
            frame_seq,
            highest_contiguous_frame_seq,
        } => Some((
            frame_seq,
            DirectFrameBody::TransportReceipt(DirectTransportReceiptV1 {
                highest_contiguous_frame_seq,
            }),
            EVENT_TRANSPORT_RECEIPT_V5,
            EVENT_FLAG_OUTBOUND_V5,
            0,
        )),
        OutboundWork::Ping {
            frame_seq,
            nonce,
            timestamp_ms,
        } => Some((
            frame_seq,
            DirectFrameBody::Ping(DirectPingV1 {
                nonce,
                sent_at_ms: timestamp_ms,
            }),
            EVENT_PING_V5,
            EVENT_FLAG_OUTBOUND_V5,
            0,
        )),
        OutboundWork::Pong {
            frame_seq,
            nonce,
            timestamp_ms,
        } => Some((
            frame_seq,
            DirectFrameBody::Pong(DirectPongV1 {
                nonce,
                received_at_ms: timestamp_ms,
            }),
            EVENT_PONG_V5,
            EVENT_FLAG_OUTBOUND_V5,
            0,
        )),
    }
}

fn mark_sent(
    state: &Arc<Mutex<State>>,
    path: Fence,
    frame_seq: u64,
    kind: u32,
    flags: u32,
    payload_bytes: u32,
    tracker: &Arc<Mutex<DirectLivenessTracker>>,
) -> Result<(), ()> {
    let mut locked = state.lock().unwrap_or_else(|entry| entry.into_inner());
    locked.check_path(path).map_err(|_| ())?;
    let sent = locked.outgoing.pop_front().ok_or(())?;
    if sent.frame_seq() != frame_seq {
        return Err(());
    }
    locked.last_wire_sent_seq = frame_seq;
    if let OutboundWork::Ping {
        nonce,
        timestamp_ms,
        ..
    } = sent
    {
        tracker
            .lock()
            .unwrap_or_else(|entry| entry.into_inner())
            .begin_ping(nonce, timestamp_ms, Instant::now())
            .map_err(|_| ())?;
    }
    if locked.push_event(kind, V5_OK, flags, frame_seq, payload_bytes, 0) {
        Ok(())
    } else {
        Err(())
    }
}

fn receive_frame(
    state: &Arc<Mutex<State>>,
    path: Fence,
    inbound_direction: Direction,
    frame: termlink_direct_quic::ReceivedDirectFrame,
    tracker: &Arc<Mutex<DirectLivenessTracker>>,
) -> Result<(), ()> {
    let mut locked = state.lock().unwrap_or_else(|entry| entry.into_inner());
    locked.check_path(path).map_err(|_| ())?;
    match frame.body {
        DirectFrameBody::RealtimeEnvelope(bytes) => {
            let observed = locked.route.observe_inbound_direct_frame(
                inbound_direction,
                path.epoch,
                frame.frame_seq,
                termlink_direct_route::InboundDirectFrame::Envelope,
            );
            let receipt = match observed {
                termlink_direct_route::InboundFrameResult::Receipt(receipt) => receipt,
                termlink_direct_route::InboundFrameResult::NoReceipt {
                    applied_transport_receipt: None,
                } => {
                    // Direction-level fallback already arranged exact Relay
                    // recovery. Consume and ignore the one or more Direct
                    // frames that were allocated before fallback; do not
                    // surface them to application ingress and do not retire
                    // the physical connection used by the other direction.
                    return Ok(());
                }
                _ => return Err(()),
            };
            if locked.total_frames().saturating_add(2) > locked.max_frames
                || !locked.queue_available(bytes.len())
            {
                return Err(());
            }
            let payload_bytes = bytes.len() as u32;
            locked.queued_bytes += bytes.len();
            locked.incoming.push_back(OwnedFrame {
                frame_seq: frame.frame_seq,
                bytes,
            });
            if !locked.push_event(
                EVENT_ENVELOPE_AVAILABLE_V5,
                V5_OK,
                EVENT_FLAG_INBOUND_V5,
                frame.frame_seq,
                payload_bytes,
                0,
            ) {
                return Err(());
            }
            if receipt.direction != inbound_direction.opposite() {
                return Err(());
            }
            locked.outgoing.push_back(OutboundWork::Receipt {
                frame_seq: receipt.frame_seq,
                highest_contiguous_frame_seq: receipt.receipt.highest_contiguous_frame_seq(),
            });
        }
        DirectFrameBody::TransportReceipt(receipt) => {
            let highest = receipt.highest_contiguous_frame_seq;
            if highest > locked.last_wire_sent_seq {
                return Err(());
            }
            let route_receipt =
                termlink_direct_route::TransportReceiptV1::new(highest).map_err(|_| ())?;
            let outcome = locked.route.observe_inbound_direct_frame(
                inbound_direction,
                path.epoch,
                frame.frame_seq,
                termlink_direct_route::InboundDirectFrame::TransportReceipt(route_receipt),
            );
            let released = match outcome {
                termlink_direct_route::InboundFrameResult::NoReceipt {
                    applied_transport_receipt:
                        Some(termlink_direct_route::ReceiptApplyOutcome::Applied {
                            released_envelopes,
                            released_bytes: _,
                        }),
                } => u32::try_from(released_envelopes).map_err(|_| ())?,
                termlink_direct_route::InboundFrameResult::NoReceipt {
                    applied_transport_receipt:
                        Some(termlink_direct_route::ReceiptApplyOutcome::IgnoredStale),
                } => 0,
                _ => return Err(()),
            };
            locked.last_receipt_seq = locked.last_receipt_seq.max(highest);
            if !locked.push_event(
                EVENT_TRANSPORT_RECEIPT_V5,
                V5_OK,
                EVENT_FLAG_INBOUND_V5 | EVENT_FLAG_TRANSPORT_COPY_V5,
                highest,
                0,
                released,
            ) {
                return Err(());
            }
        }
        DirectFrameBody::Ping(ping) => {
            if locked.total_frames().saturating_add(1) > locked.max_frames {
                return Err(());
            }
            if matches!(
                locked.route.observe_inbound_direct_frame(
                    inbound_direction,
                    path.epoch,
                    frame.frame_seq,
                    termlink_direct_route::InboundDirectFrame::ControlNoReceipt,
                ),
                termlink_direct_route::InboundFrameResult::Rejected(_)
            ) {
                return Err(());
            }
            if !locked.push_event(
                EVENT_PING_V5,
                V5_OK,
                EVENT_FLAG_INBOUND_V5,
                frame.frame_seq,
                0,
                0,
            ) {
                return Err(());
            }
            let outbound_direction = locked.local_outbound_direction.ok_or(())?;
            if outbound_direction != inbound_direction.opposite() {
                return Err(());
            }
            let frame_seq = locked
                .route
                .reserve_outbound_control_frame(outbound_direction, path.epoch)
                .map_err(|_| ())?;
            locked.outgoing.push_back(OutboundWork::Pong {
                frame_seq,
                nonce: ping.nonce,
                timestamp_ms: now_ms(),
            });
        }
        DirectFrameBody::Pong(pong) => {
            if matches!(
                locked.route.observe_inbound_direct_frame(
                    inbound_direction,
                    path.epoch,
                    frame.frame_seq,
                    termlink_direct_route::InboundDirectFrame::ControlNoReceipt,
                ),
                termlink_direct_route::InboundFrameResult::Rejected(_)
            ) {
                return Err(());
            }
            tracker
                .lock()
                .unwrap_or_else(|entry| entry.into_inner())
                .observe_pong(&pong, Instant::now())
                .map_err(|_| ())?;
            if !locked.push_event(
                EVENT_PONG_V5,
                V5_OK,
                EVENT_FLAG_INBOUND_V5,
                frame.frame_seq,
                0,
                0,
            ) {
                return Err(());
            }
        }
    }
    Ok(())
}

fn now_ms() -> i64 {
    use std::time::{SystemTime, UNIX_EPOCH};
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .ok()
        .and_then(|value| i64::try_from(value.as_millis()).ok())
        .unwrap_or(1)
}

fn is_retired(state: &Arc<Mutex<State>>, path: Fence) -> bool {
    let locked = state.lock().unwrap_or_else(|entry| entry.into_inner());
    locked.path != Some(path)
        || matches!(
            locked.lifecycle,
            Lifecycle::Cancelled | Lifecycle::NetworkChanged | Lifecycle::Failed
        )
}

async fn wait_cancelled(state: &Arc<Mutex<State>>, path: Fence, stop: &AtomicBool) {
    while !stop.load(Ordering::Acquire) && !is_retired(state, path) {
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
}

fn fail_state(state: &Arc<Mutex<State>>, path: Fence, status: i32) {
    let mut locked = state.lock().unwrap_or_else(|entry| entry.into_inner());
    if locked.path != Some(path)
        || !matches!(locked.lifecycle, Lifecycle::Active | Lifecycle::Ready)
    {
        return;
    }
    fail_direct_in_state(&mut locked, path, status);
}

fn retire_state(
    state: &Arc<Mutex<State>>,
    control_slot: &Arc<Mutex<Option<(Fence, DirectConnectionControl)>>>,
    path: Fence,
    network_changed: bool,
) -> i32 {
    {
        let mut locked = state.lock().unwrap_or_else(|entry| entry.into_inner());
        if locked.path != Some(path) {
            return V5_ERR_STALE_PATH;
        }
        if matches!(locked.lifecycle, Lifecycle::Active | Lifecycle::Ready) {
            fallback_direct_in_state(&mut locked, path);
            locked.lifecycle = if network_changed {
                Lifecycle::NetworkChanged
            } else {
                Lifecycle::Cancelled
            };
            let kind = if network_changed {
                EVENT_NETWORK_CHANGED_V5
            } else {
                EVENT_CANCELLED_V5
            };
            let _ = locked.push_event(kind, V5_OK, 0, 0, 0, 0);
        }
    }
    if let Some((active, control)) = control_slot
        .lock()
        .unwrap_or_else(|entry| entry.into_inner())
        .take()
        && active == path
    {
        if network_changed {
            control.network_changed();
        } else {
            control.cancel();
        }
    }
    V5_OK
}

fn publish_sign_request(
    state: &Arc<Mutex<State>>,
    path: Fence,
    transcript: &[u8],
) -> Result<u64, ()> {
    if transcript.is_empty() || transcript.len() > v4::MAX_EVENT_BYTES_V4 {
        return Err(());
    }
    let mut locked = state.lock().unwrap_or_else(|entry| entry.into_inner());
    locked.check_path(path).map_err(|_| ())?;
    if locked.signing_payloads.len() >= locked.max_frames {
        return Err(());
    }
    let request_id = locked
        .events
        .back()
        .map_or(1, |event| event.frame_seq.saturating_add(1))
        .max(1);
    locked.signing_payloads.push_back(OwnedFrame {
        frame_seq: request_id,
        bytes: transcript.to_vec(),
    });
    if locked.push_event(
        EVENT_SIGN_REQUEST_V5,
        V5_OK,
        0,
        request_id,
        transcript.len() as u32,
        0,
    ) {
        Ok(request_id)
    } else {
        Err(())
    }
}

async fn wait_signature(
    state: &Arc<Mutex<State>>,
    path: Fence,
    request_id: u64,
    timeout: Duration,
    stop: &AtomicBool,
) -> Result<Vec<u8>, ()> {
    let deadline = Instant::now() + timeout;
    loop {
        if stop.load(Ordering::Acquire) || is_retired(state, path) || Instant::now() >= deadline {
            return Err(());
        }
        {
            let mut locked = state.lock().unwrap_or_else(|entry| entry.into_inner());
            if let Some(index) = locked.signatures.iter().position(|value| {
                value.request_id == request_id
                    && value.epoch == path.epoch
                    && value.attempt_id == path.attempt_id
            }) {
                let value = locked.signatures.remove(index).ok_or(())?;
                return Ok(value.signature[..value.signature_length as usize].to_vec());
            }
        }
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
}

fn path_to_ffi(path: Fence) -> TermLinkDirectPathV5 {
    TermLinkDirectPathV5 {
        abi_version: ABI_VERSION_V5,
        struct_size: size_of::<TermLinkDirectPathV5>() as u32,
        path_epoch: path.epoch,
        connection_id: path.connection_id,
        attempt_id: path.attempt_id,
        reserved: [0; 2],
    }
}

fn promote_to_v4(value: &TermLinkDirectPromoteV5) -> Result<TermLinkDirectCommitNominationV4, i32> {
    if value.abi_version != ABI_VERSION_V5
        || value.struct_size as usize != size_of::<TermLinkDirectPromoteV5>()
        || value.reserved0 != 0
        || value.reserved != [0; 3]
        || decode_direction(value.local_outbound_direction).is_err()
        || !matches!(value.role, ROLE_INITIATOR_V5 | ROLE_RESPONDER_V5)
        || !(500..=10_000).contains(&value.timeout_ms)
        || value.selection_confirmed != 1
        || !(16..=32).contains(&value.local_proof_nonce_length)
        || value.local_proof_nonce[value.local_proof_nonce_length as usize..]
            .iter()
            .any(|byte| *byte != 0)
        || value.check_round_id == [0; 16]
        || value.nomination_id == [0; 16]
        || value.peer_certificate_sha256 == [0; 32]
        || value.peer_device_signing_public_key == [0; 32]
        || value.now_ms < 0
        || !valid_candidate_pair_v5(&value.winning_pair)
    {
        return Err(V5_ERR_INVALID_ARGUMENT);
    }
    Ok(TermLinkDirectCommitNominationV4 {
        abi_version: v4::ABI_VERSION_V4,
        struct_size: size_of::<TermLinkDirectCommitNominationV4>() as u32,
        epoch: value.path.path_epoch,
        attempt_id: value.path.attempt_id,
        check_round_id: value.check_round_id,
        nomination_id: value.nomination_id,
        winning_pair: pair_to_v4(value.winning_pair),
        role: value.role,
        timeout_ms: value.timeout_ms,
        selection_confirmed: value.selection_confirmed,
        local_proof_nonce_length: value.local_proof_nonce_length,
        peer_certificate_sha256: value.peer_certificate_sha256,
        peer_device_signing_public_key: value.peer_device_signing_public_key,
        local_proof_nonce: value.local_proof_nonce,
        now_ms: value.now_ms,
        reserved: [0; 4],
    })
}

fn valid_candidate_pair_v5(value: &TermLinkDirectCandidatePairV5) -> bool {
    value.abi_version == ABI_VERSION_V5
        && value.struct_size as usize == size_of::<TermLinkDirectCandidatePairV5>()
        && value.reserved0 == 0
        && value.reserved == [0; 2]
        && value.rank != 0
        && valid_candidate_v5(&value.initiator_candidate)
        && valid_candidate_v5(&value.responder_candidate)
        && value.initiator_candidate.candidate_id != value.responder_candidate.candidate_id
}

fn valid_candidate_v5(value: &TermLinkDirectCandidateV5) -> bool {
    value.abi_version == ABI_VERSION_V5
        && value.struct_size as usize == size_of::<TermLinkDirectCandidateV5>()
        && value.reserved == [0; 2]
        && matches!(
            value.kind,
            CANDIDATE_HOST_V5 | CANDIDATE_SERVER_REFLEXIVE_V5
        )
        && value.carrier == CANDIDATE_CARRIER_UDP_QUIC_V5
        && value.candidate_id != [0; 16]
        && value.priority != 0
        && value.priority <= i32::MAX as u32
        && value.network_cost <= 999
        && value.expires_at_ms > 0
        && ((value.kind == CANDIDATE_HOST_V5 && value.related_candidate_id == [0; 16])
            || (value.kind == CANDIDATE_SERVER_REFLEXIVE_V5
                && value.related_candidate_id != [0; 16]))
        && valid_socket_addr_v5(&value.endpoint)
        && v4::decode_socket(candidate_to_v4(*value).endpoint, false).is_ok()
}

fn valid_socket_addr_v5(value: &TermLinkSocketAddrV5) -> bool {
    value.abi_version == ABI_VERSION_V5
        && value.struct_size as usize == size_of::<TermLinkSocketAddrV5>()
        && matches!(value.ip_length, 4 | 16)
        && value.reserved == [0; 2]
}

fn pair_to_v4(value: TermLinkDirectCandidatePairV5) -> TermLinkDirectCandidatePairV4 {
    TermLinkDirectCandidatePairV4 {
        abi_version: v4::ABI_VERSION_V4,
        struct_size: size_of::<TermLinkDirectCandidatePairV4>() as u32,
        rank: value.rank,
        reserved0: value.reserved0,
        initiator_candidate: candidate_to_v4(value.initiator_candidate),
        responder_candidate: candidate_to_v4(value.responder_candidate),
        reserved: value.reserved,
    }
}

fn candidate_to_v4(value: TermLinkDirectCandidateV5) -> v4::TermLinkDirectCandidateV4 {
    v4::TermLinkDirectCandidateV4 {
        abi_version: v4::ABI_VERSION_V4,
        struct_size: size_of::<v4::TermLinkDirectCandidateV4>() as u32,
        candidate_id: value.candidate_id,
        related_candidate_id: value.related_candidate_id,
        endpoint: v4::TermLinkSocketAddrV4 {
            abi_version: v4::ABI_VERSION_V4,
            struct_size: size_of::<v4::TermLinkSocketAddrV4>() as u32,
            ip: value.endpoint.ip,
            ip_length: value.endpoint.ip_length,
            port: value.endpoint.port,
            reserved: value.endpoint.reserved,
        },
        kind: value.kind,
        carrier: value.carrier,
        priority: value.priority,
        network_cost: value.network_cost,
        expires_at_ms: value.expires_at_ms,
        reserved: value.reserved,
    }
}

fn map_v4_status(status: i32) -> i32 {
    match status {
        0 => V5_OK,
        -1 => V5_ERR_INVALID_HANDLE,
        -2 => V5_ERR_INVALID_ARGUMENT,
        -3 => V5_ERR_STALE_PATH,
        -4 => V5_ERR_QUEUE_FULL,
        _ => V5_ERR_PROTOCOL,
    }
}

fn map_v4_reason(reason: u32) -> i32 {
    match reason {
        v4::REASON_DISABLED_V4 => V5_ERR_DISABLED,
        v4::REASON_INVALID_TICKET_V4 => V5_ERR_TICKET_EXPIRED,
        v4::REASON_CANCELLED_V4 | v4::REASON_NETWORK_CHANGED_V4 => V5_ERR_CANCELLED,
        v4::REASON_QUEUE_FULL_V4 => V5_ERR_QUEUE_FULL,
        _ => V5_ERR_PROTOCOL,
    }
}

fn decode_direction(value: u32) -> Result<Direction, i32> {
    match value {
        DIRECTION_PC_TO_ANDROID_V5 => Ok(Direction::PcToAndroid),
        DIRECTION_ANDROID_TO_PC_V5 => Ok(Direction::AndroidToPc),
        _ => Err(V5_ERR_INVALID_ARGUMENT),
    }
}

fn encode_direction(value: Direction) -> u32 {
    match value {
        Direction::PcToAndroid => DIRECTION_PC_TO_ANDROID_V5,
        Direction::AndroidToPc => DIRECTION_ANDROID_TO_PC_V5,
    }
}

fn checked_proposal(
    value: &TermLinkDirectProposalV5,
) -> Result<(Fence, Direction, CutoverProposal), i32> {
    if value.abi_version != ABI_VERSION_V5
        || value.struct_size as usize != size_of::<TermLinkDirectProposalV5>()
        || value.session_count == 0
        || value.session_count as usize > value.sessions.len()
        || value.reserved0 != 0
        || value.reserved != [0; 2]
        || value.timeout_ms == 0
        || value.sessions[value.session_count as usize..]
            .iter()
            .any(|session| *session != TermLinkDirectSessionBarrierV5::default())
    {
        return Err(V5_ERR_INVALID_ARGUMENT);
    }
    let path = checked_path(&value.path)?;
    let direction = decode_direction(value.direction)?;
    let sessions = value.sessions[..value.session_count as usize]
        .iter()
        .map(|session| SessionBarrier {
            session_id: SessionId::new(session.session_id),
            first_direct_seq: session.first_direct_seq,
        })
        .collect::<Vec<_>>();
    let proposal = CutoverProposal {
        barrier_id: BarrierId::new(value.barrier_id),
        direct_path_binding: DirectPathBinding::new(value.direct_path_binding),
        barrier_sha256: value.barrier_sha256,
        session_barriers: sessions,
        duplicate_grace_ms: value.duplicate_grace_ms,
    };
    if canonical_cutover_barrier_sha256(path, direction, &proposal) != value.barrier_sha256 {
        return Err(V5_ERR_INVALID_ARGUMENT);
    }
    Ok((path, direction, proposal))
}

fn canonical_cutover_barrier_sha256(
    path: Fence,
    direction: Direction,
    proposal: &CutoverProposal,
) -> [u8; 32] {
    let wire = canonical_cutover_proposal_wire(direction, proposal);
    let mut input = Vec::with_capacity(
        CUTOVER_DIGEST_DOMAIN_V1.len() + path.connection_id.len() + size_of::<u64>() + wire.len(),
    );
    input.extend_from_slice(CUTOVER_DIGEST_DOMAIN_V1);
    input.extend_from_slice(&path.connection_id);
    input.extend_from_slice(&path.epoch.to_be_bytes());
    input.extend_from_slice(&wire);
    sha256(&input)
}

fn canonical_cutover_proposal_wire(direction: Direction, proposal: &CutoverProposal) -> Vec<u8> {
    let mut wire = Vec::with_capacity(128 + proposal.session_barriers.len() * 32);
    append_proto_bytes(&mut wire, 1, proposal.barrier_id.as_bytes());
    append_proto_bytes(&mut wire, 2, proposal.direct_path_binding.as_bytes());
    for barrier in &proposal.session_barriers {
        let mut session = Vec::with_capacity(32);
        append_proto_bytes(&mut session, 1, barrier.session_id.as_bytes());
        append_proto_varint_field(
            &mut session,
            match direction {
                Direction::PcToAndroid => 2,
                Direction::AndroidToPc => 3,
            },
            barrier.first_direct_seq,
        );
        append_proto_bytes(&mut wire, 3, &session);
    }
    if proposal.duplicate_grace_ms != 0 {
        append_proto_varint_field(&mut wire, 4, u64::from(proposal.duplicate_grace_ms));
    }
    wire
}

fn append_proto_bytes(output: &mut Vec<u8>, field_number: u8, bytes: &[u8]) {
    output.push(field_number << 3 | 2);
    append_proto_varint(output, bytes.len() as u64);
    output.extend_from_slice(bytes);
}

fn append_proto_varint_field(output: &mut Vec<u8>, field_number: u8, value: u64) {
    output.push(field_number << 3);
    append_proto_varint(output, value);
}

fn append_proto_varint(output: &mut Vec<u8>, mut value: u64) {
    while value >= 0x80 {
        output.push((value as u8 & 0x7f) | 0x80);
        value >>= 7;
    }
    output.push(value as u8);
}

fn checked_transition(value: &TermLinkDirectTransitionV5) -> Result<Fence, i32> {
    if value.abi_version != ABI_VERSION_V5
        || value.struct_size as usize != size_of::<TermLinkDirectTransitionV5>()
        || value.accepted > 1
        || value.is_echo > 1
        || value.reserved0 != 0
        || value.reserved1 != 0
        || value.reserved2 != 0
        || value.reserved != [0; 2]
    {
        return Err(V5_ERR_INVALID_ARGUMENT);
    }
    checked_path(&value.path)
}

fn checked_cursor(value: &TermLinkDirectCursorV5) -> Result<Fence, i32> {
    if value.abi_version != ABI_VERSION_V5
        || value.struct_size as usize != size_of::<TermLinkDirectCursorV5>()
        || value.reserved0 != 0
        || value.reserved != [0; 2]
        || value.session_id == [0; 16]
    {
        return Err(V5_ERR_INVALID_ARGUMENT);
    }
    checked_path(&value.path)
}

fn checked_route_envelope(value: &TermLinkDirectRouteEnvelopeV5) -> Result<Fence, i32> {
    if value.abi_version != ABI_VERSION_V5
        || value.struct_size as usize != size_of::<TermLinkDirectRouteEnvelopeV5>()
        || value.route_eligible > 1
        || value.session_id == [0; 16]
        || value.envelope_sequence == 0
        || value.reserved != [0; 2]
    {
        return Err(V5_ERR_INVALID_ARGUMENT);
    }
    checked_path(&value.path)
}

fn checked_ingress(value: &TermLinkDirectIngressV5) -> Result<Fence, i32> {
    if value.abi_version != ABI_VERSION_V5
        || value.struct_size as usize != size_of::<TermLinkDirectIngressV5>()
        || !matches!(value.source, 1 | 2)
        || value.session_id == [0; 16]
        || value.envelope_sequence == 0
        || value.reserved0 != 0
        || value.reserved != [0; 2]
        || value.duplicate_grace_ms > 5_000
    {
        return Err(V5_ERR_INVALID_ARGUMENT);
    }
    checked_path(&value.path)
}

fn ingress_action(
    value: &TermLinkDirectIngressV5,
    path: Fence,
    kind: u32,
    payload_bytes: usize,
    path_kind: u32,
) -> TermLinkDirectRouteActionV5 {
    let mut record = base_route_action(path, kind, value.direction);
    record.carrier = if value.source == INGRESS_CARRIER_RELAY_V5 {
        ROUTE_CARRIER_RELAY_V5
    } else if path_kind == PATH_KIND_LAN_V5 {
        ROUTE_CARRIER_LAN_DIRECT_V5
    } else if path_kind == PATH_KIND_NAT_V5 {
        ROUTE_CARRIER_NAT_DIRECT_V5
    } else {
        0
    };
    record.session_id = value.session_id;
    record.envelope_sequence = value.envelope_sequence;
    record.payload_bytes = payload_bytes as u32;
    record
}

#[allow(clippy::too_many_arguments)]
fn commit_ingress_delivery(
    state: &mut State,
    path: Fence,
    value: &TermLinkDirectIngressV5,
    direction: Direction,
    session_id: SessionId,
    digest: [u8; 32],
    bytes: Arc<[u8]>,
    held_index: Option<usize>,
) -> Result<(), i32> {
    let removed_bytes = held_index
        .and_then(|index| state.ingress.get(index))
        .map_or(0, |entry| entry.bytes.len());
    let final_frames = state
        .total_frames()
        .saturating_sub(usize::from(held_index.is_some()))
        .saturating_add(2);
    let final_bytes = state
        .queued_bytes
        .saturating_sub(removed_bytes)
        .checked_add(bytes.len())
        .and_then(|queued| queued.checked_add(state.route_buffered_bytes()));
    if state.delivered_ingress.len() >= state.max_frames
        || final_frames > state.max_frames
        || !final_bytes.is_some_and(|total| total <= state.max_bytes)
    {
        return Err(V5_ERR_QUEUE_FULL);
    }

    if let Some(index) = held_index {
        let held = state.ingress.remove(index).ok_or(V5_ERR_PROTOCOL)?;
        state.queued_bytes = state.queued_bytes.saturating_sub(held.bytes.len());
    }
    let record = ingress_action(
        value,
        path,
        ROUTE_ACTION_INGRESS_DELIVER_V5,
        bytes.len(),
        state.path_kind,
    );
    state.commit_actions(vec![OwnedRouteAction {
        record,
        bytes: Arc::clone(&bytes),
        charged_bytes: 0,
    }]);
    state.queued_bytes += bytes.len();
    state.delivered_ingress.push_back(DeliveredIngress {
        direction,
        session_id,
        envelope_sequence: value.envelope_sequence,
        digest,
        bytes,
    });
    Ok(())
}

fn flush_ingress_deadlines(state: &mut State, path: Fence, now_ms: u64) -> Result<(), i32> {
    let mut index = 0;
    while index < state.ingress.len() {
        if state.ingress[index].deadline_ms > now_ms {
            index += 1;
            continue;
        }
        let entry = &state.ingress[index];
        let value = TermLinkDirectIngressV5 {
            abi_version: ABI_VERSION_V5,
            struct_size: size_of::<TermLinkDirectIngressV5>() as u32,
            path: path_to_ffi(path),
            direction: encode_direction(entry.direction),
            source: if entry.source_direct { 2 } else { 1 },
            session_id: *entry.session_id.as_bytes(),
            envelope_sequence: entry.envelope_sequence,
            now_ms,
            duplicate_grace_ms: 0,
            reserved0: 0,
            reserved: [0; 2],
        };
        let delivery_bytes = Arc::clone(&entry.bytes);
        let digest = sha256(&delivery_bytes);
        commit_ingress_delivery(
            state,
            path,
            &value,
            entry.direction,
            entry.session_id,
            digest,
            delivery_bytes,
            Some(index),
        )?;
    }
    Ok(())
}

fn prune_delivered_ingress(
    state: &mut State,
    direction: Direction,
    session_id: SessionId,
    highest_contiguous: u64,
) {
    let mut index = 0;
    while index < state.delivered_ingress.len() {
        let entry = &state.delivered_ingress[index];
        if entry.direction == direction
            && entry.session_id == session_id
            && entry.envelope_sequence <= highest_contiguous
        {
            let entry = state
                .delivered_ingress
                .remove(index)
                .expect("index checked");
            state.queued_bytes = state.queued_bytes.saturating_sub(entry.bytes.len());
        } else {
            index += 1;
        }
    }
}

fn remove_session_ingress(state: &mut State, direction: Direction, session_id: SessionId) {
    let mut index = 0;
    while index < state.ingress.len() {
        let entry = &state.ingress[index];
        if entry.direction == direction && entry.session_id == session_id {
            let entry = state.ingress.remove(index).expect("index checked");
            state.queued_bytes = state.queued_bytes.saturating_sub(entry.bytes.len());
        } else {
            index += 1;
        }
    }
    index = 0;
    while index < state.delivered_ingress.len() {
        let entry = &state.delivered_ingress[index];
        if entry.direction == direction && entry.session_id == session_id {
            let entry = state
                .delivered_ingress
                .remove(index)
                .expect("index checked");
            state.queued_bytes = state.queued_bytes.saturating_sub(entry.bytes.len());
        } else {
            index += 1;
        }
    }
}

fn fail_direct_in_state(state: &mut State, path: Fence, status: i32) {
    if state.path != Some(path) || !matches!(state.lifecycle, Lifecycle::Active | Lifecycle::Ready)
    {
        return;
    }
    fallback_direct_in_state(state, path);
    state.lifecycle = Lifecycle::Failed;
    let _ = state.push_event(EVENT_DIRECT_FAILED_V5, status, 0, 0, 0, 0);
}

fn fallback_direct_in_state(state: &mut State, path: Fence) {
    let old_carriers = [
        state.route.authoritative_carrier(Direction::PcToAndroid),
        state.route.authoritative_carrier(Direction::AndroidToPc),
    ];
    let mut staged_route = state.route.clone();
    let fallback = staged_route.direct_failed(path.epoch);
    let mut actions = stage_batch_actions(path, &fallback, &staged_route);
    for (direction, old) in [Direction::PcToAndroid, Direction::AndroidToPc]
        .into_iter()
        .zip(old_carriers)
    {
        if !matches!(old, Carrier::Relay) {
            let mut record =
                base_route_action(path, ROUTE_ACTION_CARRIER_V5, encode_direction(direction));
            record.carrier = ROUTE_CARRIER_RELAY_V5;
            actions.push(OwnedRouteAction {
                record,
                bytes: Arc::from([]),
                charged_bytes: 0,
            });
        }
    }
    state.route = staged_route;
    state.outgoing.clear();
    state.commit_failure_actions(actions);
}

fn commit_transition(
    state: &mut State,
    path: Fence,
    direction: Direction,
    staged_route: PeerRouteMachine,
    transition: termlink_direct_route::Transition,
) -> i32 {
    let status = match &transition.outcome {
        TransitionOutcome::Rejected(_) => V5_ERR_PROTOCOL,
        TransitionOutcome::IgnoredStale { .. } => V5_ERR_STALE_PATH,
        TransitionOutcome::Applied
        | TransitionOutcome::DeferredForPrefix
        | TransitionOutcome::Activated => V5_OK,
    };
    let old_carrier = state.route.authoritative_carrier(direction);
    let old_session_states = state.route.session_states(direction);
    let mut actions = Vec::new();
    if let Some(control) = transition.control.clone()
        && let Some(action) = stage_control(path, control, &staged_route)
    {
        actions.push(action);
    }
    actions.extend(stage_batch_actions(path, &transition.routes, &staged_route));
    let work = match direct_work_from_batch(&transition.routes) {
        Ok(value) => value,
        Err(status) => return status,
    };
    let new_carrier = staged_route.authoritative_carrier(direction);
    if new_carrier != old_carrier {
        let mut record =
            base_route_action(path, ROUTE_ACTION_CARRIER_V5, encode_direction(direction));
        record.carrier = encode_carrier(new_carrier);
        actions.push(OwnedRouteAction {
            record,
            bytes: Arc::from([]),
            charged_bytes: 0,
        });
    }
    if matches!(new_carrier, Carrier::Direct(_)) {
        for (session_id, session_state) in staged_route.session_states(direction) {
            if session_state == SessionRouteState::DirectActive
                && old_session_states.get(&session_id) != Some(&SessionRouteState::DirectActive)
            {
                actions.push(session_carrier_action(
                    path,
                    direction,
                    session_id,
                    new_carrier,
                ));
            }
        }
    }
    if !state.can_commit_with_route(&staged_route, &actions, &work) {
        if transition.routes.fallback_reason.is_some() {
            // A fallback releases native-retained exact bytes. Once observed,
            // it must take effect even when ordinary action capacity is full;
            // the bounded retention it replaces becomes the emergency tail.
            state.route = staged_route;
            state.commit_failure_actions(actions);
            return status;
        }
        return V5_ERR_QUEUE_FULL;
    }
    state.route = staged_route;
    state.commit_actions(actions);
    state.outgoing.extend(work);
    status
}

fn enqueue_batch(state: &mut State, path: Fence, batch: RouteBatch) -> i32 {
    let actions = stage_batch_actions(path, &batch, &state.route);
    if !state.can_commit_actions_and_work(&actions, &[]) {
        return V5_ERR_QUEUE_FULL;
    }
    state.commit_actions(actions);
    V5_OK
}

fn stage_batch_actions(
    path: Fence,
    batch: &RouteBatch,
    route: &PeerRouteMachine,
) -> Vec<OwnedRouteAction> {
    let mut actions = Vec::with_capacity(batch.transmissions.len() + batch.controls.len());
    for transmission in &batch.transmissions {
        let kind = match transmission.carrier {
            Carrier::Relay => ROUTE_ACTION_RELAY_ENVELOPE_V5,
            Carrier::Direct(_) => ROUTE_ACTION_DIRECT_ENVELOPE_V5,
        };
        let mut record = base_route_action(path, kind, encode_direction(transmission.direction));
        record.carrier = encode_carrier(transmission.carrier);
        record.session_id = *transmission.envelope.session_id.as_bytes();
        record.envelope_sequence = transmission.envelope.sequence;
        record.direct_frame_seq = transmission.direct_frame_seq.unwrap_or(0);
        record.payload_bytes = transmission.envelope.bytes.len() as u32;
        record.reason = batch.fallback_reason.map_or(0, encode_fallback_reason);
        actions.push(OwnedRouteAction {
            record,
            bytes: if matches!(transmission.carrier, Carrier::Direct(_)) {
                Arc::from([])
            } else {
                Arc::clone(&transmission.envelope.bytes)
            },
            charged_bytes: if matches!(transmission.carrier, Carrier::Direct(_)) {
                0
            } else {
                transmission.envelope.bytes.len()
            },
        });
    }
    for control in &batch.controls {
        if let Some(action) = stage_control(path, control.clone(), route) {
            actions.push(action);
        }
    }
    actions
}

fn direct_work_from_batch(batch: &RouteBatch) -> Result<Vec<OutboundWork>, i32> {
    batch
        .transmissions
        .iter()
        .filter_map(|transmission| {
            let Carrier::Direct(_) = transmission.carrier else {
                return None;
            };
            Some(
                transmission
                    .direct_frame_seq
                    .map(|frame_seq| OutboundWork::Envelope {
                        frame_seq,
                        bytes: Arc::clone(&transmission.envelope.bytes),
                    })
                    .ok_or(V5_ERR_PROTOCOL),
            )
        })
        .collect()
}

fn stage_control(
    path: Fence,
    control: ControlMessage,
    route: &PeerRouteMachine,
) -> Option<OwnedRouteAction> {
    let mut reason_code = 0;
    let (kind, direction, barrier, hash, accepted, echo) = match control {
        // The caller already owns the complete proposal passed to
        // start_proposal_v5. A successful return authorizes sending that exact
        // record; emitting a partial action would require a hidden side map.
        ControlMessage::Proposal { .. } => return None,
        ControlMessage::Accept {
            direction,
            barrier_id,
            barrier_sha256,
            accepted,
            reason,
            ..
        } => (
            ROUTE_ACTION_ACCEPT_V5,
            direction,
            barrier_id,
            barrier_sha256,
            {
                reason_code = reason.map_or(0, encode_reject_reason);
                u32::from(accepted)
            },
            0,
        ),
        ControlMessage::Commit {
            direction,
            barrier_id,
            barrier_sha256,
            is_echo,
            ..
        } => (
            ROUTE_ACTION_COMMIT_V5,
            direction,
            barrier_id,
            barrier_sha256,
            0,
            u32::from(is_echo),
        ),
        ControlMessage::Fallback {
            direction,
            barrier_id,
            reason,
            ..
        } => (
            ROUTE_ACTION_FALLBACK_V5,
            direction,
            barrier_id.unwrap_or_else(|| BarrierId::new([0; 16])),
            [0; 32],
            {
                reason_code = encode_fallback_reason(reason);
                0
            },
            0,
        ),
    };
    let mut record = base_route_action(path, kind, encode_direction(direction));
    record.barrier_id = *barrier.as_bytes();
    record.barrier_sha256 = hash;
    record.accepted = accepted;
    record.is_echo = echo;
    record.reason = reason_code;
    if kind == ROUTE_ACTION_FALLBACK_V5 {
        record.direct_frame_seq = route.highest_received_direct_frame_seq(direction);
    }
    Some(OwnedRouteAction {
        record,
        bytes: Arc::from([]),
        charged_bytes: 0,
    })
}

fn base_route_action(path: Fence, kind: u32, direction: u32) -> TermLinkDirectRouteActionV5 {
    TermLinkDirectRouteActionV5 {
        abi_version: ABI_VERSION_V5,
        struct_size: size_of::<TermLinkDirectRouteActionV5>() as u32,
        path: path_to_ffi(path),
        kind,
        direction,
        ..TermLinkDirectRouteActionV5::default()
    }
}

fn carrier_action(path: Fence, direction: Direction, carrier: Carrier) -> OwnedRouteAction {
    let mut record = base_route_action(path, ROUTE_ACTION_CARRIER_V5, encode_direction(direction));
    record.carrier = encode_carrier(carrier);
    OwnedRouteAction {
        record,
        bytes: Arc::from([]),
        charged_bytes: 0,
    }
}

fn session_carrier_action(
    path: Fence,
    direction: Direction,
    session_id: SessionId,
    carrier: Carrier,
) -> OwnedRouteAction {
    let mut record = base_route_action(
        path,
        ROUTE_ACTION_SESSION_CARRIER_V5,
        encode_direction(direction),
    );
    record.session_id = *session_id.as_bytes();
    record.carrier = encode_carrier(carrier);
    OwnedRouteAction {
        record,
        bytes: Arc::from([]),
        charged_bytes: 0,
    }
}

fn enqueue_action(
    state: &mut State,
    record: TermLinkDirectRouteActionV5,
    bytes: impl Into<Arc<[u8]>>,
) -> Result<(), ()> {
    let bytes = bytes.into();
    let charged_bytes = bytes.len();
    enqueue_action_owned(
        state,
        OwnedRouteAction {
            record,
            bytes,
            charged_bytes,
        },
    )
}

fn enqueue_action_owned(state: &mut State, action: OwnedRouteAction) -> Result<(), ()> {
    if !state.can_commit_actions_and_work(std::slice::from_ref(&action), &[]) {
        return Err(());
    }
    state.commit_actions(vec![action]);
    Ok(())
}

fn encode_carrier(value: Carrier) -> u32 {
    match value {
        Carrier::Relay => ROUTE_CARRIER_RELAY_V5,
        Carrier::Direct(AuthenticatedPathKind::LocalAreaNetwork) => ROUTE_CARRIER_LAN_DIRECT_V5,
        Carrier::Direct(AuthenticatedPathKind::NatTraversal) => ROUTE_CARRIER_NAT_DIRECT_V5,
    }
}

fn encode_fallback_reason(value: termlink_direct_route::FallbackReason) -> u32 {
    use termlink_direct_route::FallbackReason::*;
    match value {
        BufferEnvelopeLimit => 1,
        BufferByteLimit => 2,
        DirectFailure => 3,
        EpochReplaced => 4,
        NegotiationTimeout => 5,
        PeerRejected => 6,
        BarrierMismatch => 7,
        InvalidEnvelope => 8,
        ProtocolViolation => 9,
    }
}

fn encode_reject_reason(value: termlink_direct_route::RejectReason) -> u32 {
    use termlink_direct_route::RejectReason::*;
    match value {
        InvalidEpoch => 101,
        FutureEpoch => 102,
        InvalidProposal => 103,
        InvalidTimeout => 104,
        PathBindingMismatch => 105,
        DirectionBusy => 106,
        BarrierIdMismatch => 107,
        BarrierHashMismatch => 108,
        UnexpectedAccept => 109,
        LateCommit => 110,
        PrefixTimeout => 111,
    }
}

fn checked_path(path: &TermLinkDirectPathV5) -> Result<Fence, i32> {
    if path.abi_version != ABI_VERSION_V5
        || path.struct_size as usize != std::mem::size_of::<TermLinkDirectPathV5>()
        || path.path_epoch == 0
        || path.path_epoch > MAX_FRAME_SEQ_V5
        || path.connection_id == [0; 16]
        || path.attempt_id == [0; 16]
        || path.reserved != [0; 2]
    {
        return Err(V5_ERR_INVALID_ARGUMENT);
    }
    Ok(Fence {
        epoch: path.path_epoch,
        connection_id: path.connection_id,
        attempt_id: path.attempt_id,
    })
}

fn optional_path(path: &TermLinkDirectPathV5) -> Result<Option<Fence>, i32> {
    if path.abi_version == 0
        && path.struct_size == 0
        && path.path_epoch == 0
        && path.connection_id == [0; 16]
        && path.attempt_id == [0; 16]
        && path.reserved == [0; 2]
    {
        Ok(None)
    } else {
        checked_path(path).map(Some)
    }
}

fn checked_create(
    config: &TermLinkDirectCreateV5,
) -> Result<(bool, usize, usize, Option<Fence>), i32> {
    if config.abi_version != ABI_VERSION_V5
        || config.struct_size as usize != std::mem::size_of::<TermLinkDirectCreateV5>()
        || config.enabled > 1
        || config.reserved0 != 0
        || config.reserved1 != 0
        || config.reserved != [0; 2]
    {
        return Err(V5_ERR_INVALID_ARGUMENT);
    }
    let max_frames = if config.queue_capacity_frames == 0 {
        MAX_FRAMES_V5
    } else {
        config.queue_capacity_frames as usize
    };
    let max_bytes = if config.queue_capacity_bytes == 0 {
        MAX_BYTES_V5
    } else {
        usize::try_from(config.queue_capacity_bytes).map_err(|_| V5_ERR_LIMIT)?
    };
    if max_frames == 0 || max_frames > MAX_FRAMES_V5 || max_bytes == 0 || max_bytes > MAX_BYTES_V5 {
        return Err(V5_ERR_LIMIT);
    }
    Ok((
        config.enabled == 1,
        max_frames,
        max_bytes,
        optional_path(&config.path)?,
    ))
}

unsafe fn bridge_ref<'a>(handle: *mut c_void) -> Option<&'a NativeDirectV5> {
    if handle.is_null() {
        None
    } else {
        // SAFETY: The C contract requires a live handle returned by create;
        // callers must synchronize all operations with destroy.
        Some(unsafe { &*handle.cast::<NativeDirectV5>() })
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn termlink_direct_abi_version_v5() -> u32 {
    ABI_VERSION_V5
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn termlink_direct_create_v5(
    config: *const TermLinkDirectCreateV5,
) -> *mut c_void {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(config) = (!config.is_null()).then(|| unsafe { &*config }) else {
            return ptr::null_mut();
        };
        let Ok((enabled, max_frames, max_bytes, path)) = checked_create(config) else {
            return ptr::null_mut();
        };
        NativeDirectV5::new(enabled, max_frames, max_bytes, path)
            .map(|bridge| Box::into_raw(Box::new(bridge)).cast())
            .unwrap_or(ptr::null_mut())
    }))
    .unwrap_or(ptr::null_mut())
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn termlink_direct_path_usage_v5(
    handle: *mut c_void,
    path: *const TermLinkDirectPathV5,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(path) = (unsafe { path.as_ref() }) else {
            return V5_ERR_INVALID_ARGUMENT;
        };
        let Ok(path) = checked_path(path) else {
            return V5_ERR_INVALID_ARGUMENT;
        };
        // SAFETY: The C contract requires a live create handle.
        unsafe { bridge_ref(handle) }
            .map_or(V5_ERR_INVALID_HANDLE, |bridge| bridge.path_usage(path))
    }))
    .unwrap_or(V5_ERR_INVALID_ARGUMENT)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn termlink_direct_attach_v5(
    handle: *mut c_void,
    path: *const TermLinkDirectPathV5,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(path) = (unsafe { path.as_ref() }) else {
            return V5_ERR_INVALID_ARGUMENT;
        };
        let Ok(path) = checked_path(path) else {
            return V5_ERR_INVALID_ARGUMENT;
        };
        // SAFETY: The C contract requires a live create handle.
        unsafe { bridge_ref(handle) }.map_or(V5_ERR_INVALID_HANDLE, |bridge| bridge.attach(path))
    }))
    .unwrap_or(V5_ERR_INVALID_ARGUMENT)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn termlink_direct_promote_v5(
    handle: *mut c_void,
    v4_handle: *mut c_void,
    promote: *const TermLinkDirectPromoteV5,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: both handles must be live and externally synchronized.
        let Some(bridge) = (unsafe { bridge_ref(handle) }) else {
            return V5_ERR_INVALID_HANDLE;
        };
        // SAFETY: the caller supplies a live V4 handle for this call.
        let Some(v4_bridge) = (unsafe { v4::bridge_ref_v4(v4_handle) }) else {
            return V5_ERR_INVALID_HANDLE;
        };
        if promote.is_null() {
            return V5_ERR_INVALID_ARGUMENT;
        }
        // SAFETY: promote is readable for one fixed record and copied now.
        let promote = unsafe { promote.read_unaligned() };
        bridge.promote(v4_bridge, &promote)
    }))
    .unwrap_or(V5_ERR_PROTOCOL)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn termlink_direct_submit_signature_v5(
    handle: *mut c_void,
    signature: *const TermLinkDirectSignatureV5,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: handle must be live and externally synchronized.
        let Some(bridge) = (unsafe { bridge_ref(handle) }) else {
            return V5_ERR_INVALID_HANDLE;
        };
        if signature.is_null() {
            return V5_ERR_INVALID_ARGUMENT;
        }
        // SAFETY: signature is readable for one fixed record and copied now.
        bridge.submit_signature_v5(unsafe { signature.read_unaligned() })
    }))
    .unwrap_or(V5_ERR_PROTOCOL)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn termlink_direct_receive_envelope_v5(
    handle: *mut c_void,
    path: *const TermLinkDirectPathV5,
    frame_seq: *mut u64,
    output: *mut u8,
    output_capacity: u32,
    required_bytes: *mut u32,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(path) = (unsafe { path.as_ref() }) else {
            return V5_ERR_INVALID_ARGUMENT;
        };
        let Ok(path) = checked_path(path) else {
            return V5_ERR_INVALID_ARGUMENT;
        };
        let (Some(frame_seq), Some(required_bytes)) = (unsafe { frame_seq.as_mut() }, unsafe {
            required_bytes.as_mut()
        }) else {
            return V5_ERR_INVALID_ARGUMENT;
        };
        // SAFETY: The C contract requires a live create handle.
        unsafe { bridge_ref(handle) }.map_or(V5_ERR_INVALID_HANDLE, |bridge| {
            bridge.receive_envelope(
                path,
                frame_seq,
                output,
                output_capacity as usize,
                required_bytes,
            )
        })
    }))
    .unwrap_or(V5_ERR_INVALID_ARGUMENT)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn termlink_direct_receive_signing_payload_v5(
    handle: *mut c_void,
    path: *const TermLinkDirectPathV5,
    request_id: *mut u64,
    output: *mut u8,
    output_capacity: u32,
    required_bytes: *mut u32,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: handle must be live and externally synchronized.
        let Some(bridge) = (unsafe { bridge_ref(handle) }) else {
            return V5_ERR_INVALID_HANDLE;
        };
        if path.is_null() || request_id.is_null() || required_bytes.is_null() {
            return V5_ERR_INVALID_ARGUMENT;
        }
        // SAFETY: fixed input/output records are valid for this call.
        let raw_path = unsafe { path.read_unaligned() };
        let Ok(path) = checked_path(&raw_path) else {
            return V5_ERR_INVALID_ARGUMENT;
        };
        // SAFETY: null checks above and caller guarantees writable outputs.
        bridge.receive_signing_payload(
            path,
            unsafe { &mut *request_id },
            output,
            output_capacity as usize,
            unsafe { &mut *required_bytes },
        )
    }))
    .unwrap_or(V5_ERR_PROTOCOL)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn termlink_direct_path_info_v5(
    handle: *mut c_void,
    path: *const TermLinkDirectPathV5,
    output: *mut TermLinkDirectPathInfoV5,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: handle must be live and externally synchronized.
        let Some(bridge) = (unsafe { bridge_ref(handle) }) else {
            return V5_ERR_INVALID_HANDLE;
        };
        if path.is_null() || output.is_null() {
            return V5_ERR_INVALID_ARGUMENT;
        }
        // SAFETY: path is readable and output is writable for fixed records.
        let raw_path = unsafe { path.read_unaligned() };
        let Ok(path) = checked_path(&raw_path) else {
            return V5_ERR_INVALID_ARGUMENT;
        };
        match bridge.path_info(path) {
            Ok(info) => {
                // SAFETY: output is non-null and writable by contract.
                unsafe { output.write_unaligned(info) };
                V5_OK
            }
            Err(status) => status,
        }
    }))
    .unwrap_or(V5_ERR_PROTOCOL)
}

unsafe fn proposal_ffi(
    handle: *mut c_void,
    proposal: *const TermLinkDirectProposalV5,
    received: bool,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: handle must be live and externally synchronized.
        let Some(bridge) = (unsafe { bridge_ref(handle) }) else {
            return V5_ERR_INVALID_HANDLE;
        };
        if proposal.is_null() {
            return V5_ERR_INVALID_ARGUMENT;
        }
        // SAFETY: proposal is readable for one fixed record.
        bridge.start_proposal(unsafe { &*proposal }, received)
    }))
    .unwrap_or(V5_ERR_PROTOCOL)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn termlink_direct_start_proposal_v5(
    handle: *mut c_void,
    proposal: *const TermLinkDirectProposalV5,
) -> i32 {
    // SAFETY: forwards the same caller contract.
    unsafe { proposal_ffi(handle, proposal, false) }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn termlink_direct_receive_proposal_v5(
    handle: *mut c_void,
    proposal: *const TermLinkDirectProposalV5,
) -> i32 {
    // SAFETY: forwards the same caller contract.
    unsafe { proposal_ffi(handle, proposal, true) }
}

unsafe fn transition_ffi(
    handle: *mut c_void,
    value: *const TermLinkDirectTransitionV5,
    operation: u32,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: handle must be live and externally synchronized.
        let Some(bridge) = (unsafe { bridge_ref(handle) }) else {
            return V5_ERR_INVALID_HANDLE;
        };
        if value.is_null() {
            return V5_ERR_INVALID_ARGUMENT;
        }
        // SAFETY: value is readable for one fixed record.
        bridge.apply_transition(unsafe { &*value }, operation)
    }))
    .unwrap_or(V5_ERR_PROTOCOL)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn termlink_direct_receive_accept_v5(
    handle: *mut c_void,
    value: *const TermLinkDirectTransitionV5,
) -> i32 {
    // SAFETY: forwards the same caller contract.
    unsafe { transition_ffi(handle, value, 1) }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn termlink_direct_receive_commit_v5(
    handle: *mut c_void,
    value: *const TermLinkDirectTransitionV5,
) -> i32 {
    // SAFETY: forwards the same caller contract.
    unsafe { transition_ffi(handle, value, 2) }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn termlink_direct_commit_echo_sent_v5(
    handle: *mut c_void,
    value: *const TermLinkDirectTransitionV5,
) -> i32 {
    // SAFETY: forwards the same caller contract.
    unsafe { transition_ffi(handle, value, 3) }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn termlink_direct_receive_fallback_v5(
    handle: *mut c_void,
    value: *const TermLinkDirectTransitionV5,
) -> i32 {
    // SAFETY: forwards the same caller contract.
    unsafe { transition_ffi(handle, value, 3_000) }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn termlink_direct_tick_v5(
    handle: *mut c_void,
    value: *const TermLinkDirectTransitionV5,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: handle must be live and externally synchronized.
        let Some(bridge) = (unsafe { bridge_ref(handle) }) else {
            return V5_ERR_INVALID_HANDLE;
        };
        if value.is_null() {
            return V5_ERR_INVALID_ARGUMENT;
        }
        // SAFETY: value is readable for one fixed record.
        bridge.tick(unsafe { &*value })
    }))
    .unwrap_or(V5_ERR_PROTOCOL)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn termlink_direct_update_applied_cursor_v5(
    handle: *mut c_void,
    value: *const TermLinkDirectCursorV5,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: handle must be live and externally synchronized.
        let Some(bridge) = (unsafe { bridge_ref(handle) }) else {
            return V5_ERR_INVALID_HANDLE;
        };
        if value.is_null() {
            return V5_ERR_INVALID_ARGUMENT;
        }
        // SAFETY: value is readable for one fixed record.
        bridge.update_cursor(unsafe { &*value })
    }))
    .unwrap_or(V5_ERR_PROTOCOL)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn termlink_direct_deregister_session_v5(
    handle: *mut c_void,
    value: *const TermLinkDirectCursorV5,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: handle must be live and externally synchronized.
        let Some(bridge) = (unsafe { bridge_ref(handle) }) else {
            return V5_ERR_INVALID_HANDLE;
        };
        if value.is_null() {
            return V5_ERR_INVALID_ARGUMENT;
        }
        // SAFETY: value is readable for one fixed record.
        bridge.deregister_session(unsafe { &*value })
    }))
    .unwrap_or(V5_ERR_PROTOCOL)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn termlink_direct_route_envelope_v5(
    handle: *mut c_void,
    value: *const TermLinkDirectRouteEnvelopeV5,
    envelope: *const u8,
    envelope_bytes: u32,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: handle must be live and externally synchronized.
        let Some(bridge) = (unsafe { bridge_ref(handle) }) else {
            return V5_ERR_INVALID_HANDLE;
        };
        if value.is_null() || envelope.is_null() || envelope_bytes == 0 {
            return V5_ERR_INVALID_ARGUMENT;
        }
        if envelope_bytes as usize > MAX_ENVELOPE_BYTES_V5 {
            return V5_ERR_LIMIT;
        }
        // SAFETY: fixed record and byte span are readable for this call.
        bridge.route_envelope(unsafe { &*value }, unsafe {
            slice::from_raw_parts(envelope, envelope_bytes as usize)
        })
    }))
    .unwrap_or(V5_ERR_PROTOCOL)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn termlink_direct_poll_route_action_v5(
    handle: *mut c_void,
    action: *mut TermLinkDirectRouteActionV5,
    output: *mut u8,
    output_capacity: u32,
    required_bytes: *mut u32,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: handle must be live and externally synchronized.
        let Some(bridge) = (unsafe { bridge_ref(handle) }) else {
            return V5_ERR_INVALID_HANDLE;
        };
        if action.is_null() || required_bytes.is_null() {
            return V5_ERR_INVALID_ARGUMENT;
        }
        // SAFETY: null checks above and caller guarantees writable outputs.
        bridge.poll_route_action(
            unsafe { &mut *action },
            output,
            output_capacity as usize,
            unsafe { &mut *required_bytes },
        )
    }))
    .unwrap_or(V5_ERR_PROTOCOL)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn termlink_direct_arbitrate_ingress_v5(
    handle: *mut c_void,
    value: *const TermLinkDirectIngressV5,
    envelope: *const u8,
    envelope_bytes: u32,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: handle must be live and externally synchronized.
        let Some(bridge) = (unsafe { bridge_ref(handle) }) else {
            return V5_ERR_INVALID_HANDLE;
        };
        if value.is_null() || envelope.is_null() || envelope_bytes == 0 {
            return V5_ERR_INVALID_ARGUMENT;
        }
        if envelope_bytes as usize > MAX_ENVELOPE_BYTES_V5 {
            return V5_ERR_LIMIT;
        }
        // SAFETY: fixed record and byte span are readable for this call.
        bridge.arbitrate_ingress(unsafe { &*value }, unsafe {
            slice::from_raw_parts(envelope, envelope_bytes as usize)
        })
    }))
    .unwrap_or(V5_ERR_PROTOCOL)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn termlink_direct_tick_ingress_v5(
    handle: *mut c_void,
    path: *const TermLinkDirectPathV5,
    now_ms: u64,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: handle must be live and externally synchronized.
        let Some(bridge) = (unsafe { bridge_ref(handle) }) else {
            return V5_ERR_INVALID_HANDLE;
        };
        if path.is_null() {
            return V5_ERR_INVALID_ARGUMENT;
        }
        // SAFETY: path is readable for one fixed record.
        let path = unsafe { path.read_unaligned() };
        let Ok(path) = checked_path(&path) else {
            return V5_ERR_INVALID_ARGUMENT;
        };
        bridge.tick_ingress(path, now_ms)
    }))
    .unwrap_or(V5_ERR_PROTOCOL)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn termlink_direct_poll_event_v5(
    handle: *mut c_void,
    output: *mut TermLinkDirectEventV5,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(output) = (unsafe { output.as_mut() }) else {
            return V5_ERR_INVALID_ARGUMENT;
        };
        // SAFETY: The C contract requires a live create handle.
        let Some(bridge) = (unsafe { bridge_ref(handle) }) else {
            return V5_ERR_INVALID_HANDLE;
        };
        let Some(event) = bridge.poll_event() else {
            return 0;
        };
        *output = event;
        1
    }))
    .unwrap_or(V5_ERR_INVALID_ARGUMENT)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn termlink_direct_cancel_v5(
    handle: *mut c_void,
    path: *const TermLinkDirectPathV5,
) -> i32 {
    unsafe { control_v5(handle, path, false) }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn termlink_direct_network_changed_v5(
    handle: *mut c_void,
    path: *const TermLinkDirectPathV5,
) -> i32 {
    unsafe { control_v5(handle, path, true) }
}

unsafe fn control_v5(
    handle: *mut c_void,
    path: *const TermLinkDirectPathV5,
    network_changed: bool,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(path) = (unsafe { path.as_ref() }) else {
            return V5_ERR_INVALID_ARGUMENT;
        };
        let Ok(path) = checked_path(path) else {
            return V5_ERR_INVALID_ARGUMENT;
        };
        // SAFETY: The C contract requires a live create handle.
        let Some(bridge) = (unsafe { bridge_ref(handle) }) else {
            return V5_ERR_INVALID_HANDLE;
        };
        if network_changed {
            bridge.control(WorkerCommand::NetworkChanged { path })
        } else {
            bridge.control(WorkerCommand::Cancel { path })
        }
    }))
    .unwrap_or(V5_ERR_INVALID_ARGUMENT)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn termlink_direct_destroy_v5(handle: *mut c_void) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if !handle.is_null() {
            // SAFETY: Required by the C contract and checked for null above.
            drop(unsafe { Box::from_raw(handle.cast::<NativeDirectV5>()) });
        }
    }));
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{
        mem::{align_of, offset_of, size_of},
        time::Duration,
    };

    fn path(epoch: u64, attempt: u8) -> (TermLinkDirectPathV5, Fence) {
        let mut connection_id = [0_u8; 16];
        connection_id[0] = 0xC1;
        connection_id[1] = epoch as u8;
        let mut attempt_id = [0_u8; 16];
        attempt_id[0] = attempt;
        let path = TermLinkDirectPathV5 {
            abi_version: ABI_VERSION_V5,
            struct_size: size_of::<TermLinkDirectPathV5>() as u32,
            path_epoch: epoch,
            connection_id,
            attempt_id,
            reserved: [0; 2],
        };
        (
            path,
            Fence {
                epoch,
                connection_id,
                attempt_id,
            },
        )
    }

    fn bridge(path: Option<Fence>, max_frames: usize, max_bytes: usize) -> NativeDirectV5 {
        NativeDirectV5::new(true, max_frames, max_bytes, path).unwrap()
    }

    fn ready_bridge(
        fence: Fence,
        max_frames: usize,
        max_bytes: usize,
        outbound: Direction,
    ) -> NativeDirectV5 {
        let bridge = bridge(Some(fence), max_frames, max_bytes);
        {
            let mut state = bridge.lock_state();
            let installed = state.route.install_authenticated_path(
                fence.epoch,
                AuthenticatedPathKind::LocalAreaNetwork,
                DirectPathBinding::new([0xa5; 32]),
            );
            assert!(matches!(
                installed.outcome,
                termlink_direct_route::EpochInstallOutcome::Installed
            ));
            state.lifecycle = Lifecycle::Ready;
            state.local_outbound_direction = Some(outbound);
            state.binding = [0xa5; 32];
            state.path_kind = PATH_KIND_LAN_V5;
        }
        bridge
    }

    fn activate_direction(
        bridge: &NativeDirectV5,
        direction: Direction,
        session_id: [u8; 16],
        grace_ms: u32,
    ) {
        let mut state = bridge.lock_state();
        let proposal = CutoverProposal {
            barrier_id: BarrierId::new([0xb1; 16]),
            direct_path_binding: DirectPathBinding::new([0xa5; 32]),
            barrier_sha256: [0xc1; 32],
            session_barriers: vec![SessionBarrier {
                session_id: SessionId::new(session_id),
                first_direct_seq: 1,
            }],
            duplicate_grace_ms: grace_ms,
        };
        let epoch = state.path.expect("test path").epoch;
        let started = state
            .route
            .start_proposal(direction, epoch, proposal.clone(), 1, 1_000);
        assert!(matches!(started.outcome, TransitionOutcome::Applied));
        let accepted = state.route.receive_accept(
            direction,
            epoch,
            proposal.barrier_id,
            proposal.barrier_sha256,
            true,
        );
        assert!(matches!(accepted.outcome, TransitionOutcome::Applied));
        let activated = state.route.receive_commit(
            direction,
            epoch,
            proposal.barrier_id,
            proposal.barrier_sha256,
            100,
        );
        assert!(matches!(activated.outcome, TransitionOutcome::Activated));
    }

    fn proposal_record(
        raw_path: TermLinkDirectPathV5,
        fence: Fence,
        direction: Direction,
        barrier_byte: u8,
        sessions: &[([u8; 16], u64)],
    ) -> TermLinkDirectProposalV5 {
        let mut session_records = [TermLinkDirectSessionBarrierV5::default(); 32];
        let route_sessions = sessions
            .iter()
            .enumerate()
            .map(|(index, (session_id, first_direct_seq))| {
                session_records[index] = TermLinkDirectSessionBarrierV5 {
                    session_id: *session_id,
                    first_direct_seq: *first_direct_seq,
                };
                SessionBarrier {
                    session_id: SessionId::new(*session_id),
                    first_direct_seq: *first_direct_seq,
                }
            })
            .collect();
        let proposal = CutoverProposal {
            barrier_id: BarrierId::new([barrier_byte; 16]),
            direct_path_binding: DirectPathBinding::new([0xa5; 32]),
            barrier_sha256: [0; 32],
            session_barriers: route_sessions,
            duplicate_grace_ms: 0,
        };
        let digest = canonical_cutover_barrier_sha256(fence, direction, &proposal);
        TermLinkDirectProposalV5 {
            abi_version: ABI_VERSION_V5,
            struct_size: size_of::<TermLinkDirectProposalV5>() as u32,
            path: raw_path,
            direction: encode_direction(direction),
            session_count: sessions.len() as u32,
            barrier_id: [barrier_byte; 16],
            barrier_sha256: digest,
            direct_path_binding: [0xa5; 32],
            duplicate_grace_ms: 0,
            now_ms: 1,
            timeout_ms: 1_000,
            sessions: session_records,
            ..TermLinkDirectProposalV5::default()
        }
    }

    fn transition_record(
        proposal: &TermLinkDirectProposalV5,
        accepted: bool,
    ) -> TermLinkDirectTransitionV5 {
        TermLinkDirectTransitionV5 {
            abi_version: ABI_VERSION_V5,
            struct_size: size_of::<TermLinkDirectTransitionV5>() as u32,
            path: proposal.path,
            direction: proposal.direction,
            barrier_id: proposal.barrier_id,
            barrier_sha256: proposal.barrier_sha256,
            accepted: u32::from(accepted),
            now_ms: proposal.now_ms,
            timeout_ms: proposal.timeout_ms,
            ..TermLinkDirectTransitionV5::default()
        }
    }

    fn ingress(
        raw_path: TermLinkDirectPathV5,
        direction: Direction,
        source: u32,
        session_id: [u8; 16],
        sequence: u64,
        now_ms: u64,
        grace_ms: u32,
    ) -> TermLinkDirectIngressV5 {
        TermLinkDirectIngressV5 {
            abi_version: ABI_VERSION_V5,
            struct_size: size_of::<TermLinkDirectIngressV5>() as u32,
            path: raw_path,
            direction: encode_direction(direction),
            source,
            session_id,
            envelope_sequence: sequence,
            now_ms,
            duplicate_grace_ms: grace_ms,
            reserved0: 0,
            reserved: [0; 2],
        }
    }

    fn route_envelope(
        raw_path: TermLinkDirectPathV5,
        direction: Direction,
        session_id: [u8; 16],
        sequence: u64,
        eligible: bool,
    ) -> TermLinkDirectRouteEnvelopeV5 {
        TermLinkDirectRouteEnvelopeV5 {
            abi_version: ABI_VERSION_V5,
            struct_size: size_of::<TermLinkDirectRouteEnvelopeV5>() as u32,
            path: raw_path,
            direction: encode_direction(direction),
            route_eligible: u32::from(eligible),
            session_id,
            envelope_sequence: sequence,
            now_ms: 10_000_u64.saturating_add(sequence),
            reserved: [0; 2],
        }
    }

    fn structurally_valid_promote(path: TermLinkDirectPathV5) -> TermLinkDirectPromoteV5 {
        let mut endpoint = TermLinkSocketAddrV5 {
            abi_version: ABI_VERSION_V5,
            struct_size: size_of::<TermLinkSocketAddrV5>() as u32,
            ip_length: 4,
            port: 40_000,
            ..TermLinkSocketAddrV5::default()
        };
        endpoint.ip[..4].copy_from_slice(&[192, 0, 2, 1]);
        let initiator_candidate = TermLinkDirectCandidateV5 {
            abi_version: ABI_VERSION_V5,
            struct_size: size_of::<TermLinkDirectCandidateV5>() as u32,
            candidate_id: [1; 16],
            endpoint,
            kind: CANDIDATE_HOST_V5,
            carrier: CANDIDATE_CARRIER_UDP_QUIC_V5,
            priority: 1,
            expires_at_ms: 1,
            ..TermLinkDirectCandidateV5::default()
        };
        let mut responder_candidate = initiator_candidate;
        responder_candidate.candidate_id = [2; 16];
        let mut promote = TermLinkDirectPromoteV5 {
            abi_version: ABI_VERSION_V5,
            struct_size: size_of::<TermLinkDirectPromoteV5>() as u32,
            path,
            check_round_id: [6; 16],
            nomination_id: [7; 16],
            winning_pair: TermLinkDirectCandidatePairV5 {
                abi_version: ABI_VERSION_V5,
                struct_size: size_of::<TermLinkDirectCandidatePairV5>() as u32,
                rank: 1,
                initiator_candidate,
                responder_candidate,
                ..TermLinkDirectCandidatePairV5::default()
            },
            role: ROLE_INITIATOR_V5,
            timeout_ms: 500,
            selection_confirmed: 1,
            local_proof_nonce_length: 16,
            peer_certificate_sha256: [3; 32],
            peer_device_signing_public_key: [4; 32],
            local_outbound_direction: DIRECTION_PC_TO_ANDROID_V5,
            ..TermLinkDirectPromoteV5::default()
        };
        promote.local_proof_nonce[..16].fill(5);
        promote
    }

    fn poll_action(
        bridge: &NativeDirectV5,
        output_capacity: usize,
    ) -> (i32, TermLinkDirectRouteActionV5, Vec<u8>, u32) {
        let mut action = TermLinkDirectRouteActionV5::default();
        let mut output = vec![0_u8; output_capacity];
        let output_ptr = if output_capacity == 0 {
            ptr::null_mut()
        } else {
            output.as_mut_ptr()
        };
        let mut required = 0;
        let status =
            bridge.poll_route_action(&mut action, output_ptr, output_capacity, &mut required);
        if status == V5_OK {
            output.truncate(required as usize);
        }
        (status, action, output, required)
    }

    #[test]
    fn v5_layout_is_fixed_and_does_not_touch_v4_types() {
        macro_rules! assert_layout {
            ($type:ty, $size:expr, { $($field:ident: $offset:expr),+ $(,)? }) => {{
                assert_eq!(align_of::<$type>(), 8, "{} alignment", stringify!($type));
                assert_eq!(size_of::<$type>(), $size, "{} size", stringify!($type));
                $(assert_eq!(
                    offset_of!($type, $field),
                    $offset,
                    "{}.{} offset",
                    stringify!($type),
                    stringify!($field),
                );)+
            }};
        }

        assert_layout!(TermLinkDirectPathV5, 64, {
            abi_version: 0, struct_size: 4, path_epoch: 8, connection_id: 16,
            attempt_id: 32, reserved: 48,
        });
        assert_layout!(TermLinkDirectCreateV5, 112, {
            abi_version: 0, struct_size: 4, enabled: 8, reserved0: 12,
            queue_capacity_frames: 16, reserved1: 20, queue_capacity_bytes: 24,
            path: 32, reserved: 96,
        });
        assert_layout!(TermLinkDirectEventV5, 80, {
            abi_version: 0, struct_size: 4, kind: 8, status: 12, flags: 16,
            reserved0: 20, path_epoch: 24, attempt_id: 32, frame_seq: 48,
            payload_bytes: 56, released_frames: 60, reserved: 64,
        });
        assert_layout!(TermLinkSocketAddrV5, 48, {
            abi_version: 0, struct_size: 4, ip: 8, ip_length: 24, port: 28,
            reserved: 32,
        });
        assert_layout!(TermLinkDirectCandidateV5, 128, {
            abi_version: 0, struct_size: 4, candidate_id: 8,
            related_candidate_id: 24, endpoint: 40, kind: 88, carrier: 92,
            priority: 96, network_cost: 100, expires_at_ms: 104, reserved: 112,
        });
        assert_layout!(TermLinkDirectCandidatePairV5, 288, {
            abi_version: 0, struct_size: 4, rank: 8, reserved0: 12,
            initiator_candidate: 16, responder_candidate: 144, reserved: 272,
        });
        assert_layout!(TermLinkDirectPromoteV5, 544, {
            abi_version: 0, struct_size: 4, path: 8, check_round_id: 72,
            nomination_id: 88, winning_pair: 104, role: 392, timeout_ms: 396,
            selection_confirmed: 400, local_proof_nonce_length: 404,
            peer_certificate_sha256: 408, peer_device_signing_public_key: 440,
            local_proof_nonce: 472, now_ms: 504, local_outbound_direction: 512,
            reserved0: 516, reserved: 520,
        });
        assert_layout!(TermLinkDirectPathInfoV5, 128, {
            abi_version: 0, struct_size: 4, path: 8, path_kind: 72, ready: 76,
            direct_path_binding: 80, reserved: 112,
        });
        assert_layout!(TermLinkDirectSignatureV5, 232, {
            abi_version: 0, struct_size: 4, path: 8, request_id: 72,
            signature_length: 80, reserved0: 84, signature: 88, reserved: 216,
        });
        assert_layout!(TermLinkDirectSessionBarrierV5, 24, {
            session_id: 0, first_direct_seq: 16,
        });
        assert_layout!(TermLinkDirectProposalV5, 968, {
            abi_version: 0, struct_size: 4, path: 8, direction: 72,
            session_count: 76, barrier_id: 80, barrier_sha256: 96,
            direct_path_binding: 128, duplicate_grace_ms: 160, reserved0: 164,
            now_ms: 168, timeout_ms: 176, sessions: 184, reserved: 952,
        });
        assert_layout!(TermLinkDirectTransitionV5, 176, {
            abi_version: 0, struct_size: 4, path: 8, direction: 72, reserved0: 76,
            barrier_id: 80, barrier_sha256: 96, accepted: 128, is_echo: 132,
            reserved1: 136, reserved2: 140, now_ms: 144, timeout_ms: 152,
            reserved: 160,
        });
        assert_layout!(TermLinkDirectCursorV5, 120, {
            abi_version: 0, struct_size: 4, path: 8, direction: 72,
            reserved0: 76, session_id: 80, highest_contiguous: 96, reserved: 104,
        });
        assert_layout!(TermLinkDirectRouteEnvelopeV5, 128, {
            abi_version: 0, struct_size: 4, path: 8, direction: 72,
            route_eligible: 76, session_id: 80, envelope_sequence: 96,
            now_ms: 104, reserved: 112,
        });
        assert_layout!(TermLinkDirectIngressV5, 136, {
            abi_version: 0, struct_size: 4, path: 8, direction: 72, source: 76,
            session_id: 80, envelope_sequence: 96, now_ms: 104,
            duplicate_grace_ms: 112, reserved0: 116, reserved: 120,
        });
        assert_layout!(TermLinkDirectRouteActionV5, 200, {
            abi_version: 0, struct_size: 4, path: 8, kind: 72, direction: 76,
            session_id: 80, envelope_sequence: 96, direct_frame_seq: 104,
            barrier_id: 112, barrier_sha256: 128, accepted: 160, is_echo: 164,
            carrier: 168, flags: 172, reason: 176, payload_bytes: 180,
            reserved: 184,
        });
    }

    #[test]
    fn proposal_digest_matches_android_windows_vector_and_is_native_verified() {
        let mut binding = [0_u8; 32];
        for (index, byte) in binding.iter_mut().enumerate() {
            *byte = index as u8 + 1;
        }
        let expected_digest = [
            0x83, 0x22, 0xfe, 0x77, 0xac, 0xa3, 0x1e, 0x9d, 0xf6, 0x86, 0x20, 0xa0, 0x41, 0xe9,
            0xb5, 0x96, 0xa8, 0x4f, 0x00, 0x35, 0x7a, 0x29, 0x0c, 0xc8, 0x8a, 0x14, 0x93, 0xa5,
            0xe6, 0x33, 0x03, 0xec,
        ];
        let mut sessions = [TermLinkDirectSessionBarrierV5::default(); 32];
        sessions[0] = TermLinkDirectSessionBarrierV5 {
            session_id: [
                0x00, 0x00, 0x00, 0x00, 0xff, 0xff, 0xee, 0xee, 0xdd, 0xdd, 0xcc, 0xcc, 0xbb, 0xbb,
                0x00, 0x02,
            ],
            first_direct_seq: 1,
        };
        sessions[1] = TermLinkDirectSessionBarrierV5 {
            session_id: [
                0xff, 0xff, 0xff, 0xff, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x01,
            ],
            first_direct_seq: 300,
        };
        let path = TermLinkDirectPathV5 {
            abi_version: ABI_VERSION_V5,
            struct_size: size_of::<TermLinkDirectPathV5>() as u32,
            path_epoch: 0x0102_0304_0506_0708,
            connection_id: [
                0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd,
                0xee, 0xff,
            ],
            attempt_id: [0xaa; 16],
            reserved: [0; 2],
        };
        let mut value = TermLinkDirectProposalV5 {
            abi_version: ABI_VERSION_V5,
            struct_size: size_of::<TermLinkDirectProposalV5>() as u32,
            path,
            direction: DIRECTION_ANDROID_TO_PC_V5,
            session_count: 2,
            barrier_id: [
                0xfe, 0xdc, 0xba, 0x98, 0x76, 0x54, 0x32, 0x10, 0xff, 0xff, 0xee, 0xee, 0x00, 0x00,
                0x11, 0x11,
            ],
            barrier_sha256: expected_digest,
            direct_path_binding: binding,
            duplicate_grace_ms: 1_250,
            now_ms: 1,
            timeout_ms: 1_000,
            sessions,
            ..TermLinkDirectProposalV5::default()
        };
        let (fence, direction, proposal) = checked_proposal(&value).expect("shared vector");
        assert_eq!(direction, Direction::AndroidToPc);
        assert_eq!(
            canonical_cutover_barrier_sha256(fence, direction, &proposal),
            expected_digest
        );

        value.barrier_sha256[0] ^= 1;
        assert_eq!(checked_proposal(&value), Err(V5_ERR_INVALID_ARGUMENT));
        value.barrier_sha256 = expected_digest;
        value.sessions[2].first_direct_seq = 1;
        assert_eq!(checked_proposal(&value), Err(V5_ERR_INVALID_ARGUMENT));
    }

    #[test]
    fn commit_emits_session_carrier_without_changing_the_action_abi() {
        let (raw, fence) = path(1, 1);
        let session = [0x51; 16];
        let bridge = ready_bridge(fence, 16, 4_096, Direction::PcToAndroid);
        let proposal = proposal_record(raw, fence, Direction::PcToAndroid, 0x61, &[(session, 1)]);
        assert_eq!(bridge.start_proposal(&proposal, false), V5_OK);
        let transition = transition_record(&proposal, true);
        assert_eq!(bridge.apply_transition(&transition, 1), V5_OK);
        assert_eq!(poll_action(&bridge, 0).1.kind, ROUTE_ACTION_COMMIT_V5);
        assert_eq!(bridge.apply_transition(&transition, 2), V5_OK);

        let (_, direction_carrier, _, _) = poll_action(&bridge, 0);
        assert_eq!(direction_carrier.kind, ROUTE_ACTION_CARRIER_V5);
        assert_eq!(direction_carrier.carrier, ROUTE_CARRIER_LAN_DIRECT_V5);
        let (_, session_carrier, _, required) = poll_action(&bridge, 0);
        assert_eq!(session_carrier.kind, ROUTE_ACTION_SESSION_CARRIER_V5);
        assert_eq!(session_carrier.direction, DIRECTION_PC_TO_ANDROID_V5);
        assert_eq!(session_carrier.session_id, session);
        assert_eq!(session_carrier.carrier, ROUTE_CARRIER_LAN_DIRECT_V5);
        assert_eq!(required, 0);
        assert_eq!(poll_action(&bridge, 0).0, V5_NO_DATA);
    }

    #[test]
    fn deregister_is_session_scoped_replays_exact_bytes_and_drops_late_direct() {
        let (raw, fence) = path(1, 1);
        let removed = [0x52; 16];
        let retained = [0x53; 16];
        let bridge = ready_bridge(fence, 32, 4_096, Direction::PcToAndroid);
        {
            let mut state = bridge.lock_state();
            let proposal = CutoverProposal {
                barrier_id: BarrierId::new([0x62; 16]),
                direct_path_binding: DirectPathBinding::new([0xa5; 32]),
                barrier_sha256: [0x72; 32],
                session_barriers: vec![
                    SessionBarrier {
                        session_id: SessionId::new(removed),
                        first_direct_seq: 1,
                    },
                    SessionBarrier {
                        session_id: SessionId::new(retained),
                        first_direct_seq: 1,
                    },
                ],
                duplicate_grace_ms: 50,
            };
            let started = state.route.start_proposal(
                Direction::PcToAndroid,
                fence.epoch,
                proposal.clone(),
                1,
                1_000,
            );
            assert_eq!(started.outcome, TransitionOutcome::Applied);
            state.route.receive_accept(
                Direction::PcToAndroid,
                fence.epoch,
                proposal.barrier_id,
                proposal.barrier_sha256,
                true,
            );
            assert_eq!(
                state
                    .route
                    .receive_commit(
                        Direction::PcToAndroid,
                        fence.epoch,
                        proposal.barrier_id,
                        proposal.barrier_sha256,
                        2,
                    )
                    .outcome,
                TransitionOutcome::Activated
            );
        }

        let routed = route_envelope(raw, Direction::PcToAndroid, removed, 1, true);
        assert_eq!(bridge.route_envelope(&routed, b"exact-removed"), V5_OK);
        assert_eq!(
            poll_action(&bridge, 0).1.kind,
            ROUTE_ACTION_DIRECT_ENVELOPE_V5
        );
        let held = ingress(
            raw,
            Direction::PcToAndroid,
            INGRESS_CARRIER_DIRECT_V5,
            removed,
            2,
            3,
            0,
        );
        assert_eq!(bridge.arbitrate_ingress(&held, b"held"), V5_OK);
        assert_eq!(poll_action(&bridge, 0).1.kind, ROUTE_ACTION_INGRESS_HOLD_V5);
        assert_eq!(bridge.lock_state().outgoing.len(), 1);
        let deregister = TermLinkDirectCursorV5 {
            abi_version: ABI_VERSION_V5,
            struct_size: size_of::<TermLinkDirectCursorV5>() as u32,
            path: raw,
            direction: DIRECTION_PC_TO_ANDROID_V5,
            session_id: removed,
            ..TermLinkDirectCursorV5::default()
        };
        assert_eq!(bridge.deregister_session(&deregister), V5_OK);
        {
            let state = bridge.lock_state();
            assert_eq!(state.outgoing.len(), 1);
            assert!(state.ingress.is_empty());
            assert!(state.delivered_ingress.is_empty());
            assert_eq!(state.queued_bytes, b"exact-removed".len());
            assert_eq!(
                state
                    .route
                    .session_state(Direction::PcToAndroid, SessionId::new(retained)),
                SessionRouteState::DirectActive
            );
            assert_eq!(state.lifecycle, Lifecycle::Ready);
        }
        let (status, relay, bytes, required) = poll_action(&bridge, b"exact-removed".len());
        assert_eq!(status, V5_OK);
        assert_eq!(relay.kind, ROUTE_ACTION_RELAY_ENVELOPE_V5);
        assert_eq!(relay.session_id, removed);
        assert_eq!(bytes, b"exact-removed");
        assert_eq!(required, b"exact-removed".len() as u32);
        assert_eq!(bridge.lock_state().queued_bytes, 0);
        let (_, carrier, _, _) = poll_action(&bridge, 0);
        assert_eq!(carrier.kind, ROUTE_ACTION_SESSION_CARRIER_V5);
        assert_eq!(carrier.session_id, removed);
        assert_eq!(carrier.carrier, ROUTE_CARRIER_RELAY_V5);

        let late = ingress(
            raw,
            Direction::PcToAndroid,
            INGRESS_CARRIER_DIRECT_V5,
            removed,
            1,
            3,
            0,
        );
        assert_eq!(bridge.arbitrate_ingress(&late, b"late"), V5_OK);
        let (_, drop_action, _, _) = poll_action(&bridge, 0);
        assert_eq!(drop_action.kind, ROUTE_ACTION_INGRESS_DROP_V5);
        assert_eq!(bridge.lock_state().lifecycle, Lifecycle::Ready);
    }

    #[test]
    fn deregister_rejects_cursor_values_and_stale_paths_without_mutation() {
        let (raw, fence) = path(2, 2);
        let bridge = ready_bridge(fence, 8, 1_024, Direction::PcToAndroid);
        let mut value = TermLinkDirectCursorV5 {
            abi_version: ABI_VERSION_V5,
            struct_size: size_of::<TermLinkDirectCursorV5>() as u32,
            path: raw,
            direction: DIRECTION_PC_TO_ANDROID_V5,
            session_id: [0x54; 16],
            highest_contiguous: 1,
            ..TermLinkDirectCursorV5::default()
        };
        assert_eq!(bridge.deregister_session(&value), V5_ERR_INVALID_ARGUMENT);
        value.highest_contiguous = 0;
        value.path.path_epoch = 1;
        assert_eq!(bridge.deregister_session(&value), V5_ERR_STALE_PATH);
        assert_eq!(poll_action(&bridge, 0).0, V5_NO_DATA);
    }

    #[test]
    fn promotion_rejects_malformed_nested_v5_records_before_conversion() {
        let (path, _) = path(1, 1);
        let valid = structurally_valid_promote(path);
        assert!(promote_to_v4(&valid).is_ok());

        let mut malformed = valid;
        malformed.winning_pair.abi_version = 4;
        assert_eq!(promote_to_v4(&malformed), Err(V5_ERR_INVALID_ARGUMENT));

        malformed = valid;
        malformed.winning_pair.reserved0 = 1;
        assert_eq!(promote_to_v4(&malformed), Err(V5_ERR_INVALID_ARGUMENT));

        malformed = valid;
        malformed.winning_pair.initiator_candidate.struct_size = 0;
        assert_eq!(promote_to_v4(&malformed), Err(V5_ERR_INVALID_ARGUMENT));

        malformed = valid;
        malformed.winning_pair.responder_candidate.kind = 99;
        assert_eq!(promote_to_v4(&malformed), Err(V5_ERR_INVALID_ARGUMENT));

        malformed = valid;
        malformed.winning_pair.responder_candidate.carrier = 99;
        assert_eq!(promote_to_v4(&malformed), Err(V5_ERR_INVALID_ARGUMENT));

        malformed = valid;
        malformed
            .winning_pair
            .responder_candidate
            .endpoint
            .abi_version = 4;
        assert_eq!(promote_to_v4(&malformed), Err(V5_ERR_INVALID_ARGUMENT));

        malformed = valid;
        malformed.winning_pair.responder_candidate.endpoint.reserved[0] = 1;
        assert_eq!(promote_to_v4(&malformed), Err(V5_ERR_INVALID_ARGUMENT));

        malformed = valid;
        malformed.role = 99;
        assert_eq!(promote_to_v4(&malformed), Err(V5_ERR_INVALID_ARGUMENT));

        malformed = valid;
        malformed.timeout_ms = u32::MAX;
        assert_eq!(promote_to_v4(&malformed), Err(V5_ERR_INVALID_ARGUMENT));

        malformed = valid;
        malformed.selection_confirmed = 0;
        assert_eq!(promote_to_v4(&malformed), Err(V5_ERR_INVALID_ARGUMENT));

        malformed = valid;
        malformed.local_proof_nonce_length = 15;
        assert_eq!(promote_to_v4(&malformed), Err(V5_ERR_INVALID_ARGUMENT));

        malformed = valid;
        malformed.local_proof_nonce[31] = 1;
        assert_eq!(promote_to_v4(&malformed), Err(V5_ERR_INVALID_ARGUMENT));
    }

    #[test]
    fn short_receive_buffer_does_not_dequeue() {
        let (raw, fence) = path(1, 1);
        let bridge = bridge(Some(fence), 4, 64);
        assert_eq!(bridge.inject_received(fence, 4, vec![7, 8, 9]), V5_OK);
        let mut frame_seq = 0;
        let mut required = 0;
        let mut small = [0_u8; 2];
        assert_eq!(
            bridge.receive_envelope(fence, &mut frame_seq, small.as_mut_ptr(), 2, &mut required),
            V5_ERR_BUFFER_TOO_SMALL
        );
        assert_eq!(required, 3);
        let mut output = [0_u8; 3];
        assert_eq!(
            bridge.receive_envelope(fence, &mut frame_seq, output.as_mut_ptr(), 3, &mut required),
            V5_OK
        );
        assert_eq!(frame_seq, 4);
        assert_eq!(output, [7, 8, 9]);
        assert_eq!(
            bridge.receive_envelope(fence, &mut frame_seq, output.as_mut_ptr(), 3, &mut required),
            V5_NO_DATA
        );
        assert_eq!(raw.path_epoch, 1);
    }

    #[test]
    fn ffi_rejects_oversized_input_before_borrowing_the_byte_span() {
        let (raw, fence) = path(1, 1);
        let bridge_handle = Box::into_raw(Box::new(bridge(Some(fence), 4, 64))).cast::<c_void>();
        let route = TermLinkDirectRouteEnvelopeV5 {
            abi_version: ABI_VERSION_V5,
            struct_size: size_of::<TermLinkDirectRouteEnvelopeV5>() as u32,
            path: raw,
            direction: DIRECTION_PC_TO_ANDROID_V5,
            route_eligible: 0,
            session_id: [1; 16],
            envelope_sequence: 1,
            now_ms: 1,
            reserved: [0; 2],
        };
        let byte = [0_u8; 1];
        // SAFETY: `bridge_handle`, `route`, and `byte` are live for this call. The
        // deliberately oversized length must be rejected before a slice is
        // formed from `byte`.
        assert_eq!(
            unsafe {
                termlink_direct_route_envelope_v5(
                    bridge_handle,
                    &route,
                    byte.as_ptr(),
                    (MAX_ENVELOPE_BYTES_V5 + 1) as u32,
                )
            },
            V5_ERR_LIMIT
        );
        // SAFETY: The bridge was allocated above and is consumed exactly once.
        unsafe { termlink_direct_destroy_v5(bridge_handle) };

        let bridge_handle = Box::into_raw(Box::new(bridge(Some(fence), 4, 64))).cast::<c_void>();
        let ingress = TermLinkDirectIngressV5 {
            abi_version: ABI_VERSION_V5,
            struct_size: size_of::<TermLinkDirectIngressV5>() as u32,
            path: raw,
            direction: DIRECTION_PC_TO_ANDROID_V5,
            source: INGRESS_CARRIER_RELAY_V5,
            session_id: [1; 16],
            envelope_sequence: 1,
            now_ms: 1,
            duplicate_grace_ms: 0,
            reserved0: 0,
            reserved: [0; 2],
        };
        // SAFETY: Same ownership and lifetime conditions as above.
        assert_eq!(
            unsafe {
                termlink_direct_arbitrate_ingress_v5(
                    bridge_handle,
                    &ingress,
                    byte.as_ptr(),
                    (MAX_ENVELOPE_BYTES_V5 + 1) as u32,
                )
            },
            V5_ERR_LIMIT
        );
        // SAFETY: The bridge was allocated above and is consumed exactly once.
        unsafe { termlink_direct_destroy_v5(bridge_handle) };
    }

    #[test]
    fn stale_epoch_and_attempt_fail_closed() {
        let (raw_one, fence_one) = path(1, 1);
        let bridge = bridge(Some(fence_one), 4, 64);
        let (raw_two, fence_two) = path(2, 2);
        assert_eq!(bridge.path_usage(fence_two), V5_OK);
        assert_eq!(
            bridge.lock_state().check_path(fence_one),
            Err(V5_ERR_STALE_PATH)
        );
        assert_eq!(bridge.lock_state().check_path(fence_two), Ok(()));
        let mut same_epoch_different_attempt = raw_two;
        same_epoch_different_attempt.attempt_id[0] = 3;
        assert_eq!(
            checked_path(&same_epoch_different_attempt),
            Ok(Fence {
                epoch: 2,
                connection_id: raw_two.connection_id,
                attempt_id: [3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]
            })
        );
        assert_eq!(
            bridge.lock_state().check_path(Fence {
                epoch: 2,
                connection_id: raw_two.connection_id,
                attempt_id: [3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]
            }),
            Err(V5_ERR_STALE_PATH)
        );
        assert_eq!(raw_one.path_epoch, 1);
    }

    #[test]
    fn attach_without_promoted_socket_is_not_ready() {
        let (_, fence) = path(1, 1);
        let bridge = bridge(Some(fence), 4, 64);
        assert_eq!(bridge.attach(fence), V5_ERR_NOT_READY);
        assert!(bridge.poll_event().is_none());
    }

    #[test]
    fn cancel_and_network_change_are_synchronous_fences() {
        let (_, fence) = path(1, 1);
        let bridge = bridge(Some(fence), 4, 64);
        assert_eq!(bridge.control(WorkerCommand::Cancel { path: fence }), V5_OK);
        assert_eq!(bridge.lock_state().check_path(fence), Err(V5_ERR_CANCELLED));
        let event = bridge.poll_event().expect("cancel event");
        assert_eq!(event.kind, EVENT_CANCELLED_V5);

        let (_, next) = path(2, 2);
        assert_eq!(bridge.path_usage(next), V5_OK);
        assert_eq!(
            bridge.control(WorkerCommand::NetworkChanged { path: next }),
            V5_OK
        );
        assert_eq!(bridge.lock_state().check_path(next), Err(V5_ERR_CANCELLED));
        assert_eq!(bridge.poll_event().unwrap().kind, EVENT_PATH_USED_V5);
        assert_eq!(bridge.poll_event().unwrap().kind, EVENT_NETWORK_CHANGED_V5);
    }

    #[test]
    fn direct_ingress_requires_the_same_direction_to_be_committed() {
        let (raw, fence) = path(1, 1);
        let session = [0x10; 16];
        let bridge = ready_bridge(fence, 16, 1_024, Direction::AndroidToPc);
        let direct = ingress(
            raw,
            Direction::PcToAndroid,
            INGRESS_CARRIER_DIRECT_V5,
            session,
            1,
            10,
            50,
        );
        assert_eq!(
            bridge.arbitrate_ingress(&direct, b"uncommitted"),
            V5_ERR_PROTOCOL
        );
        {
            let state = bridge.lock_state();
            assert_eq!(state.lifecycle, Lifecycle::Failed);
            assert!(state.ingress.is_empty());
            assert!(state.delivered_ingress.is_empty());
            assert_eq!(state.queued_bytes, 0);
        }

        let (negotiating_raw, negotiating_fence) = path(2, 2);
        let negotiating = ready_bridge(negotiating_fence, 16, 1_024, Direction::AndroidToPc);
        let negotiating_direct = ingress(
            negotiating_raw,
            Direction::PcToAndroid,
            INGRESS_CARRIER_DIRECT_V5,
            session,
            1,
            10,
            50,
        );
        {
            let mut state = negotiating.lock_state();
            let proposal = CutoverProposal {
                barrier_id: BarrierId::new([0xb0; 16]),
                direct_path_binding: DirectPathBinding::new([0xa5; 32]),
                barrier_sha256: [0xc0; 32],
                session_barriers: vec![SessionBarrier {
                    session_id: SessionId::new(session),
                    first_direct_seq: 1,
                }],
                duplicate_grace_ms: 50,
            };
            assert!(matches!(
                state
                    .route
                    .start_proposal(
                        Direction::PcToAndroid,
                        negotiating_fence.epoch,
                        proposal,
                        10,
                        1_000,
                    )
                    .outcome,
                TransitionOutcome::Applied
            ));
        }
        assert_eq!(
            negotiating.arbitrate_ingress(&negotiating_direct, b"negotiating"),
            V5_ERR_PROTOCOL
        );

        let (active_raw, active_fence) = path(3, 3);
        let active = ready_bridge(active_fence, 16, 1_024, Direction::AndroidToPc);
        activate_direction(&active, Direction::PcToAndroid, session, 50);
        {
            let mut state = active.lock_state();
            let fallback = state
                .route
                .direction_failed(Direction::PcToAndroid, active_fence.epoch);
            assert!(matches!(
                fallback.fallback_reason,
                Some(termlink_direct_route::FallbackReason::DirectFailure)
            ));
        }
        let after_fallback = ingress(
            active_raw,
            Direction::PcToAndroid,
            INGRESS_CARRIER_DIRECT_V5,
            session,
            1,
            10,
            50,
        );
        assert_eq!(
            active.arbitrate_ingress(&after_fallback, b"draining"),
            V5_ERR_PROTOCOL
        );
        let state = active.lock_state();
        assert_eq!(state.lifecycle, Lifecycle::Failed);
        assert!(state.route_actions.iter().all(|action| {
            matches!(
                action.record.kind,
                ROUTE_ACTION_FALLBACK_V5 | ROUTE_ACTION_CARRIER_V5
            )
        }));
        assert!(state.ingress.is_empty());
        assert!(state.delivered_ingress.is_empty());
        assert_eq!(state.queued_bytes, 0);
    }

    #[test]
    fn transport_control_close_fails_ready_path_but_accepts_owner_retirement() {
        let (_, fence) = path(1, 1);
        let bridge = ready_bridge(fence, 8, 1_024, Direction::PcToAndroid);
        activate_direction(&bridge, Direction::PcToAndroid, [0x0f; 16], 0);
        let stop = AtomicBool::new(false);

        // A reader/writer protocol or I/O failure retires its QUIC control
        // first. While V5 still considers the path Ready, the simultaneous
        // cancellation wakeup must propagate failure to worker fallback.
        assert_eq!(
            data_loop_cancelled_result(&bridge.state, fence, &stop),
            Err(())
        );
        fail_state(&bridge.state, fence, V5_ERR_PROTOCOL);
        assert_eq!(
            data_loop_cancelled_result(&bridge.state, fence, &stop),
            Ok(())
        );
        let state = bridge.lock_state();
        assert_eq!(state.lifecycle, Lifecycle::Failed);
        assert_eq!(
            state.route.authoritative_carrier(Direction::PcToAndroid),
            Carrier::Relay
        );
        assert!(state.route_actions.iter().any(|action| {
            action.record.kind == ROUTE_ACTION_CARRIER_V5
                && action.record.direction == DIRECTION_PC_TO_ANDROID_V5
                && action.record.carrier == ROUTE_CARRIER_RELAY_V5
        }));
        assert_eq!(
            state
                .events
                .iter()
                .filter(|event| event.kind == EVENT_DIRECT_FAILED_V5)
                .count(),
            1
        );

        let (_, destroy_fence) = path(2, 2);
        let destroying = ready_bridge(destroy_fence, 8, 1_024, Direction::PcToAndroid);
        let stopping = AtomicBool::new(true);
        assert_eq!(
            data_loop_cancelled_result(&destroying.state, destroy_fence, &stopping),
            Ok(())
        );
    }

    #[test]
    fn relay_first_then_matching_direct_is_delivered_exactly_once() {
        let (raw, fence) = path(1, 1);
        let session = [0x11; 16];
        let bridge = ready_bridge(fence, 16, 1_024, Direction::PcToAndroid);
        activate_direction(&bridge, Direction::PcToAndroid, session, 50);
        let relay = ingress(
            raw,
            Direction::PcToAndroid,
            INGRESS_CARRIER_RELAY_V5,
            session,
            1,
            10,
            50,
        );
        assert_eq!(bridge.arbitrate_ingress(&relay, b"one"), V5_OK);
        let (status, action, bytes, required) = poll_action(&bridge, 3);
        assert_eq!(status, V5_OK);
        assert_eq!(action.kind, ROUTE_ACTION_INGRESS_DELIVER_V5);
        assert_eq!(action.carrier, ROUTE_CARRIER_RELAY_V5);
        assert_eq!(required, 3);
        assert_eq!(bytes, b"one");

        let direct = ingress(
            raw,
            Direction::PcToAndroid,
            INGRESS_CARRIER_DIRECT_V5,
            session,
            1,
            11,
            50,
        );
        assert_eq!(bridge.arbitrate_ingress(&direct, b"one"), V5_OK);
        let (status, action, bytes, required) = poll_action(&bridge, 0);
        assert_eq!(status, V5_OK);
        assert_eq!(action.kind, ROUTE_ACTION_INGRESS_DROP_V5);
        assert_eq!(action.carrier, ROUTE_CARRIER_LAN_DIRECT_V5);
        assert_eq!(action.payload_bytes, 3);
        assert_eq!(required, 0);
        assert!(bytes.is_empty());
        assert_eq!(poll_action(&bridge, 0).0, V5_NO_DATA);
        assert_eq!(bridge.lock_state().delivered_ingress.len(), 1);
    }

    #[test]
    fn direct_first_holds_then_matching_relay_delivers_once() {
        let (raw, fence) = path(1, 1);
        let session = [0x12; 16];
        let bridge = ready_bridge(fence, 16, 1_024, Direction::PcToAndroid);
        activate_direction(&bridge, Direction::PcToAndroid, session, 50);
        let direct = ingress(
            raw,
            Direction::PcToAndroid,
            INGRESS_CARRIER_DIRECT_V5,
            session,
            1,
            100,
            50,
        );
        assert_eq!(bridge.arbitrate_ingress(&direct, b"same"), V5_OK);
        let (status, action, _, required) = poll_action(&bridge, 0);
        assert_eq!(status, V5_OK);
        assert_eq!(action.kind, ROUTE_ACTION_INGRESS_HOLD_V5);
        assert_eq!(action.carrier, ROUTE_CARRIER_LAN_DIRECT_V5);
        assert_eq!(action.payload_bytes, 4);
        assert_eq!(required, 0);

        let relay = ingress(
            raw,
            Direction::PcToAndroid,
            INGRESS_CARRIER_RELAY_V5,
            session,
            1,
            101,
            50,
        );
        assert_eq!(bridge.arbitrate_ingress(&relay, b"same"), V5_OK);
        let (status, action, bytes, _) = poll_action(&bridge, 4);
        assert_eq!(status, V5_OK);
        assert_eq!(action.kind, ROUTE_ACTION_INGRESS_DELIVER_V5);
        assert_eq!(action.carrier, ROUTE_CARRIER_RELAY_V5);
        assert_eq!(bytes, b"same");
        assert_eq!(poll_action(&bridge, 0).0, V5_NO_DATA);
        let state = bridge.lock_state();
        assert!(state.ingress.is_empty());
        assert_eq!(state.delivered_ingress.len(), 1);
        assert_eq!(state.queued_bytes, 4);
    }

    #[test]
    fn conflicting_relay_replaces_held_direct_and_retires_direct() {
        let (raw, fence) = path(1, 1);
        let session = [0x13; 16];
        let bridge = ready_bridge(fence, 16, 1_024, Direction::PcToAndroid);
        activate_direction(&bridge, Direction::PcToAndroid, session, 50);
        let direct = ingress(
            raw,
            Direction::PcToAndroid,
            INGRESS_CARRIER_DIRECT_V5,
            session,
            1,
            100,
            50,
        );
        assert_eq!(bridge.arbitrate_ingress(&direct, b"direct"), V5_OK);
        assert_eq!(poll_action(&bridge, 0).1.kind, ROUTE_ACTION_INGRESS_HOLD_V5);

        let relay = ingress(
            raw,
            Direction::PcToAndroid,
            INGRESS_CARRIER_RELAY_V5,
            session,
            1,
            101,
            50,
        );
        assert_eq!(bridge.arbitrate_ingress(&relay, b"relay"), V5_OK);
        let (status, action, bytes, _) = poll_action(&bridge, 5);
        assert_eq!(status, V5_OK);
        assert_eq!(action.kind, ROUTE_ACTION_INGRESS_DELIVER_V5);
        assert_eq!(action.carrier, ROUTE_CARRIER_RELAY_V5);
        assert_eq!(bytes, b"relay");
        {
            let state = bridge.lock_state();
            assert_eq!(state.lifecycle, Lifecycle::Failed);
            assert!(state.ingress.is_empty());
            assert_eq!(state.delivered_ingress[0].bytes.as_ref(), b"relay");
        }
        assert_eq!(bridge.tick_ingress(fence, 1_000), V5_OK);
        let (status, fallback, _, required) = poll_action(&bridge, 0);
        assert_eq!(status, V5_OK);
        assert_eq!(fallback.kind, ROUTE_ACTION_FALLBACK_V5);
        assert_eq!(fallback.direction, DIRECTION_PC_TO_ANDROID_V5);
        assert_eq!(required, 0);
        let (status, carrier, _, required) = poll_action(&bridge, 0);
        assert_eq!(status, V5_OK);
        assert_eq!(carrier.kind, ROUTE_ACTION_CARRIER_V5);
        assert_eq!(carrier.direction, DIRECTION_PC_TO_ANDROID_V5);
        assert_eq!(carrier.carrier, ROUTE_CARRIER_RELAY_V5);
        assert_eq!(required, 0);
        assert_eq!(poll_action(&bridge, 0).0, V5_NO_DATA);
        assert_eq!(
            bridge.arbitrate_ingress(&direct, b"direct"),
            V5_ERR_CANCELLED
        );
    }

    #[test]
    fn conflicting_relay_after_direct_delivery_is_returned_for_durable_recovery() {
        let (raw, fence) = path(1, 1);
        let session = [0x17; 16];
        let bridge = ready_bridge(fence, 16, 1_024, Direction::PcToAndroid);
        activate_direction(&bridge, Direction::PcToAndroid, session, 0);
        let direct = ingress(
            raw,
            Direction::PcToAndroid,
            INGRESS_CARRIER_DIRECT_V5,
            session,
            1,
            100,
            0,
        );
        assert_eq!(bridge.arbitrate_ingress(&direct, b"direct"), V5_OK);
        let (status, action, bytes, _) = poll_action(&bridge, 6);
        assert_eq!(status, V5_OK);
        assert_eq!(action.kind, ROUTE_ACTION_INGRESS_DELIVER_V5);
        assert_eq!(bytes, b"direct");

        let relay = ingress(
            raw,
            Direction::PcToAndroid,
            INGRESS_CARRIER_RELAY_V5,
            session,
            1,
            101,
            0,
        );
        assert_eq!(bridge.arbitrate_ingress(&relay, b"relay"), V5_ERR_PROTOCOL);
        let state = bridge.lock_state();
        assert_eq!(state.lifecycle, Lifecycle::Failed);
        assert_eq!(state.delivered_ingress[0].bytes.as_ref(), b"direct");
    }

    #[test]
    fn ingress_deadline_transfers_charge_and_applied_cursor_prevents_redelivery() {
        let (raw, fence) = path(1, 1);
        let session = [0x14; 16];
        let bridge = ready_bridge(fence, 16, 1_024, Direction::PcToAndroid);
        activate_direction(&bridge, Direction::PcToAndroid, session, 10);
        let direct = ingress(
            raw,
            Direction::PcToAndroid,
            INGRESS_CARRIER_DIRECT_V5,
            session,
            7,
            100,
            10,
        );
        assert_eq!(bridge.arbitrate_ingress(&direct, b"held"), V5_OK);
        assert_eq!(poll_action(&bridge, 0).1.kind, ROUTE_ACTION_INGRESS_HOLD_V5);
        assert_eq!(bridge.lock_state().queued_bytes, 4);
        assert_eq!(bridge.tick_ingress(fence, 110), V5_OK);
        let (status, action, bytes, _) = poll_action(&bridge, 4);
        assert_eq!(status, V5_OK);
        assert_eq!(action.kind, ROUTE_ACTION_INGRESS_DELIVER_V5);
        assert_eq!(bytes, b"held");
        {
            let state = bridge.lock_state();
            assert!(state.ingress.is_empty());
            assert_eq!(state.delivered_ingress.len(), 1);
            assert_eq!(state.queued_bytes, 4);
        }

        let cursor = TermLinkDirectCursorV5 {
            abi_version: ABI_VERSION_V5,
            struct_size: size_of::<TermLinkDirectCursorV5>() as u32,
            path: raw,
            direction: DIRECTION_PC_TO_ANDROID_V5,
            reserved0: 0,
            session_id: session,
            highest_contiguous: 7,
            reserved: [0; 2],
        };
        assert_eq!(bridge.update_cursor(&cursor), V5_OK);
        {
            let state = bridge.lock_state();
            assert!(state.delivered_ingress.is_empty());
            assert_eq!(state.queued_bytes, 0);
        }
        let relay = ingress(
            raw,
            Direction::PcToAndroid,
            INGRESS_CARRIER_RELAY_V5,
            session,
            7,
            120,
            0,
        );
        assert_eq!(bridge.arbitrate_ingress(&relay, b"held"), V5_OK);
        let (status, action, _, required) = poll_action(&bridge, 0);
        assert_eq!(status, V5_OK);
        assert_eq!(action.kind, ROUTE_ACTION_INGRESS_DROP_V5);
        assert_eq!(required, 0);
    }

    #[test]
    fn cursor_transition_is_atomic_when_action_queue_is_full() {
        let (raw, fence) = path(1, 1);
        let session = [0x15; 16];
        let bridge = ready_bridge(fence, 2, 1_024, Direction::PcToAndroid);
        {
            let mut state = bridge.lock_state();
            let proposal = CutoverProposal {
                barrier_id: BarrierId::new([0xb2; 16]),
                direct_path_binding: DirectPathBinding::new([0xa5; 32]),
                barrier_sha256: [0xc2; 32],
                session_barriers: vec![SessionBarrier {
                    session_id: SessionId::new(session),
                    first_direct_seq: 2,
                }],
                duplicate_grace_ms: 0,
            };
            let transition = state.route.receive_proposal(
                Direction::PcToAndroid,
                fence.epoch,
                proposal,
                1,
                1_000,
            );
            assert!(matches!(
                transition.outcome,
                TransitionOutcome::DeferredForPrefix
            ));
            state.queued_bytes = 1;
            state.delivered_ingress.push_back(DeliveredIngress {
                direction: Direction::PcToAndroid,
                session_id: SessionId::new(session),
                envelope_sequence: 1,
                digest: sha256(b"x"),
                bytes: Arc::from(&b"x"[..]),
            });
            state.route_actions.push_back(OwnedRouteAction {
                record: base_route_action(fence, ROUTE_ACTION_CARRIER_V5, 1),
                bytes: Arc::from([]),
                charged_bytes: 0,
            });
        }
        let cursor = TermLinkDirectCursorV5 {
            abi_version: ABI_VERSION_V5,
            struct_size: size_of::<TermLinkDirectCursorV5>() as u32,
            path: raw,
            direction: DIRECTION_PC_TO_ANDROID_V5,
            reserved0: 0,
            session_id: session,
            highest_contiguous: 1,
            reserved: [0; 2],
        };
        assert_eq!(bridge.update_cursor(&cursor), V5_ERR_QUEUE_FULL);
        {
            let state = bridge.lock_state();
            assert_eq!(
                state
                    .route
                    .applied_cursor(Direction::PcToAndroid, SessionId::new(session)),
                0
            );
            assert_eq!(state.delivered_ingress.len(), 1);
        }
        bridge.lock_state().route_actions.clear();
        assert_eq!(bridge.update_cursor(&cursor), V5_OK);
        let state = bridge.lock_state();
        assert_eq!(
            state
                .route
                .applied_cursor(Direction::PcToAndroid, SessionId::new(session)),
            1
        );
        assert!(state.delivered_ingress.is_empty());
    }

    #[test]
    fn cursor_identity_capacity_maps_to_limit_without_mutating_existing_state() {
        let (raw, fence) = path(1, 1);
        let bridge = ready_bridge(fence, 8, 1_024, Direction::PcToAndroid);
        let mut first_session = [0_u8; 16];
        for index in 0..MAX_APPLIED_CURSORS_PER_DIRECTION_V5 {
            let mut session_id = [0_u8; 16];
            session_id[..8].copy_from_slice(&(index as u64 + 1).to_le_bytes());
            if index == 0 {
                first_session = session_id;
            }
            let cursor = TermLinkDirectCursorV5 {
                abi_version: ABI_VERSION_V5,
                struct_size: size_of::<TermLinkDirectCursorV5>() as u32,
                path: raw,
                direction: DIRECTION_PC_TO_ANDROID_V5,
                session_id,
                highest_contiguous: index as u64 + 1,
                ..TermLinkDirectCursorV5::default()
            };
            assert_eq!(bridge.update_cursor(&cursor), V5_OK);
        }

        let overflow_session = [0xff; 16];
        let overflow = TermLinkDirectCursorV5 {
            abi_version: ABI_VERSION_V5,
            struct_size: size_of::<TermLinkDirectCursorV5>() as u32,
            path: raw,
            direction: DIRECTION_PC_TO_ANDROID_V5,
            session_id: overflow_session,
            highest_contiguous: 1,
            ..TermLinkDirectCursorV5::default()
        };
        assert_eq!(bridge.update_cursor(&overflow), V5_ERR_LIMIT);
        let state = bridge.lock_state();
        assert_eq!(
            state
                .route
                .applied_cursor(Direction::PcToAndroid, SessionId::new(overflow_session)),
            0
        );
        assert_eq!(
            state
                .route
                .applied_cursor(Direction::PcToAndroid, SessionId::new(first_session)),
            1
        );
    }

    #[test]
    fn successor_epoch_discards_predecessor_incoming_and_held_ingress() {
        let (raw_one, fence_one) = path(1, 1);
        let bridge = ready_bridge(fence_one, 16, 1_024, Direction::PcToAndroid);
        activate_direction(&bridge, Direction::PcToAndroid, [0x16; 16], 50);
        assert_eq!(bridge.inject_received(fence_one, 4, b"old".to_vec()), V5_OK);
        let held = ingress(
            raw_one,
            Direction::PcToAndroid,
            INGRESS_CARRIER_DIRECT_V5,
            [0x16; 16],
            1,
            100,
            50,
        );
        assert_eq!(bridge.arbitrate_ingress(&held, b"held"), V5_OK);
        {
            let mut state = bridge.lock_state();
            while state.events.len() < state.max_frames {
                assert!(state.push_event(EVENT_PING_V5, V5_OK, 0, 0, 0, 0));
            }
            assert!(!state.push_event(EVENT_PING_V5, V5_OK, 0, 0, 0, 0));
            assert!(state.event_overflow);
            assert_eq!(state.lifecycle, Lifecycle::Failed);
        }
        let (_, fence_two) = path(2, 2);
        assert_eq!(bridge.path_usage(fence_two), V5_OK);
        {
            let state = bridge.lock_state();
            assert!(state.incoming.is_empty());
            assert!(state.ingress.is_empty());
            assert!(!state.event_overflow);
            assert_eq!(state.events.len(), 1);
            assert_eq!(state.events[0].path_epoch, fence_two.epoch);
            assert_eq!(state.events[0].kind, EVENT_PATH_USED_V5);
            assert_eq!(state.lifecycle, Lifecycle::Active);
            assert_eq!(state.queued_bytes, 0);
        }
        let mut frame_seq = 99;
        let mut required = 99;
        assert_eq!(
            bridge.receive_envelope(fence_two, &mut frame_seq, ptr::null_mut(), 0, &mut required,),
            V5_NO_DATA
        );
        assert_eq!(required, 0);
        assert_eq!(bridge.tick_ingress(fence_two, 1_000), V5_OK);
        assert!(bridge.lock_state().ingress.is_empty());
    }

    #[test]
    fn route_queue_failure_falls_back_prior_exact_bytes_without_accepting_current() {
        let (raw, fence) = path(1, 1);
        let session = [0x17; 16];
        let bridge = ready_bridge(fence, 4, 1_024, Direction::PcToAndroid);
        activate_direction(&bridge, Direction::PcToAndroid, session, 0);
        let first = route_envelope(raw, Direction::PcToAndroid, session, 1, true);
        assert_eq!(bridge.route_envelope(&first, b"old"), V5_OK);
        let second = route_envelope(raw, Direction::PcToAndroid, session, 2, true);
        assert_eq!(
            bridge.route_envelope(&second, b"current"),
            V5_ERR_QUEUE_FULL
        );
        {
            let state = bridge.lock_state();
            assert_eq!(state.lifecycle, Lifecycle::Failed);
            assert_eq!(state.route.buffered_envelopes(Direction::PcToAndroid), 0);
            assert!(state.outgoing.is_empty());
        }
        let mut payloads = Vec::new();
        loop {
            let (status, action, bytes, _) = poll_action(&bridge, 16);
            if status == V5_NO_DATA {
                break;
            }
            assert_eq!(status, V5_OK);
            if action.kind == ROUTE_ACTION_RELAY_ENVELOPE_V5 {
                payloads.push(bytes);
            }
        }
        assert_eq!(payloads, vec![b"old".to_vec()]);
        let before = {
            let state = bridge.lock_state();
            (state.total_frames(), state.queued_bytes)
        };
        for _ in 0..8 {
            assert_eq!(bridge.route_envelope(&second, b"current"), V5_ERR_CANCELLED);
        }
        let state = bridge.lock_state();
        assert_eq!((state.total_frames(), state.queued_bytes), before);
    }

    #[test]
    fn dual_direction_failure_uses_only_the_fixed_emergency_action_tail() {
        let (_, fence) = path(1, 1);
        let bridge = ready_bridge(fence, 8, 1_024, Direction::PcToAndroid);
        activate_direction(&bridge, Direction::PcToAndroid, [0x31; 16], 0);
        activate_direction(&bridge, Direction::AndroidToPc, [0x32; 16], 0);
        {
            let mut state = bridge.lock_state();
            assert_eq!(
                state
                    .route
                    .session_state(Direction::PcToAndroid, SessionId::new([0x31; 16]),),
                termlink_direct_route::SessionRouteState::DirectActive,
            );
            assert_eq!(
                state
                    .route
                    .session_state(Direction::AndroidToPc, SessionId::new([0x32; 16]),),
                termlink_direct_route::SessionRouteState::DirectActive,
            );
            let down = state.route.route_outbound(
                Direction::PcToAndroid,
                OpaqueEnvelope::new(SessionId::new([0x31; 16]), 1, Arc::<[u8]>::from(&b"d"[..])),
                101,
            );
            let up = state.route.route_outbound(
                Direction::AndroidToPc,
                OpaqueEnvelope::new(SessionId::new([0x32; 16]), 1, Arc::<[u8]>::from(&b"u"[..])),
                101,
            );
            assert!(matches!(down.transmissions[0].carrier, Carrier::Direct(_)));
            assert!(matches!(up.transmissions[0].carrier, Carrier::Direct(_)));
            while state.total_frames() < state.max_frames {
                state.route_actions.push_back(OwnedRouteAction {
                    record: base_route_action(fence, ROUTE_ACTION_INGRESS_DROP_V5, 0),
                    bytes: Arc::from([]),
                    charged_bytes: 0,
                });
            }
            assert_eq!(state.total_frames(), state.max_frames);

            fallback_direct_in_state(&mut state, fence);
            assert_eq!(
                state.total_frames(),
                state.max_frames + FAILURE_ACTION_TAIL_V5
            );
            assert!(state.total_frames() <= MAX_RETAINED_FRAMES_V5);
            assert_eq!(state.queued_bytes, 2);
            assert_eq!(state.route_buffered_bytes(), 0);
        }
    }

    #[test]
    fn route_action_short_buffer_is_non_destructive_and_metadata_only_lengths_are_distinct() {
        let (raw, fence) = path(1, 1);
        let bridge = ready_bridge(fence, 8, 1_024, Direction::PcToAndroid);
        activate_direction(&bridge, Direction::PcToAndroid, [0x19; 16], 50);
        let value = route_envelope(raw, Direction::PcToAndroid, [0x18; 16], 1, false);
        assert_eq!(bridge.route_envelope(&value, b"abc"), V5_OK);
        let mut action = TermLinkDirectRouteActionV5 {
            kind: 0xfeed,
            payload_bytes: 0xbeef,
            ..TermLinkDirectRouteActionV5::default()
        };
        let mut short = [0_u8; 2];
        let mut required = 0;
        assert_eq!(
            bridge.poll_route_action(&mut action, short.as_mut_ptr(), 2, &mut required),
            V5_ERR_BUFFER_TOO_SMALL
        );
        assert_eq!(required, 3);
        assert_eq!(action.kind, 0xfeed);
        assert_eq!(action.payload_bytes, 0xbeef);
        let (status, action, bytes, required) = poll_action(&bridge, 3);
        assert_eq!(status, V5_OK);
        assert_eq!(action.kind, ROUTE_ACTION_RELAY_ENVELOPE_V5);
        assert_eq!(action.payload_bytes, 3);
        assert_eq!(required, 3);
        assert_eq!(bytes, b"abc");

        let direct = ingress(
            raw,
            Direction::PcToAndroid,
            INGRESS_CARRIER_DIRECT_V5,
            [0x19; 16],
            1,
            10,
            50,
        );
        assert_eq!(bridge.arbitrate_ingress(&direct, b"meta"), V5_OK);
        let (status, action, _, required) = poll_action(&bridge, 0);
        assert_eq!(status, V5_OK);
        assert_eq!(action.kind, ROUTE_ACTION_INGRESS_HOLD_V5);
        assert_eq!(action.payload_bytes, 4);
        assert_eq!(required, 0);
    }

    #[test]
    fn signing_payload_short_buffer_does_not_dequeue_or_write_request_id() {
        let (_, fence) = path(1, 1);
        let bridge = bridge(Some(fence), 4, 64);
        bridge.lock_state().signing_payloads.push_back(OwnedFrame {
            frame_seq: 7,
            bytes: b"sig".to_vec(),
        });
        let mut request_id = 99;
        let mut required = 0;
        let mut short = [0_u8; 2];
        assert_eq!(
            bridge.receive_signing_payload(
                fence,
                &mut request_id,
                short.as_mut_ptr(),
                2,
                &mut required,
            ),
            V5_ERR_BUFFER_TOO_SMALL
        );
        assert_eq!(request_id, 99);
        assert_eq!(required, 3);
        assert_eq!(bridge.lock_state().signing_payloads.len(), 1);
        let mut output = [0_u8; 3];
        assert_eq!(
            bridge.receive_signing_payload(
                fence,
                &mut request_id,
                output.as_mut_ptr(),
                3,
                &mut required,
            ),
            V5_OK
        );
        assert_eq!(request_id, 7);
        assert_eq!(output, *b"sig");
    }

    #[test]
    fn inbound_direction_uses_opposite_semantics_and_shares_local_sequence_allocator() {
        let (raw, fence) = path(1, 1);
        let session = [0x20; 16];
        let bridge = ready_bridge(fence, 16, 1_024, Direction::AndroidToPc);
        activate_direction(&bridge, Direction::PcToAndroid, session, 0);
        let tracker = Arc::new(Mutex::new(
            DirectLivenessTracker::new(Duration::from_secs(10)).unwrap(),
        ));
        assert_eq!(
            receive_frame(
                &bridge.state,
                fence,
                Direction::PcToAndroid,
                termlink_direct_quic::ReceivedDirectFrame {
                    frame_seq: FIRST_DATA_FRAME_SEQ_V5,
                    body: DirectFrameBody::RealtimeEnvelope(b"in".to_vec()),
                },
                &tracker,
            ),
            Ok(())
        );
        {
            let state = bridge.lock_state();
            assert!(matches!(
                state.outgoing.back(),
                Some(OutboundWork::Receipt {
                    frame_seq: FIRST_DATA_FRAME_SEQ_V5,
                    highest_contiguous_frame_seq: FIRST_DATA_FRAME_SEQ_V5,
                })
            ));
        }
        activate_direction(&bridge, Direction::AndroidToPc, session, 0);
        let outbound = route_envelope(raw, Direction::AndroidToPc, session, 1, true);
        assert_eq!(bridge.route_envelope(&outbound, b"out"), V5_OK);
        let direct_seq = bridge
            .lock_state()
            .route_actions
            .iter()
            .find(|entry| entry.record.kind == ROUTE_ACTION_DIRECT_ENVELOPE_V5)
            .map(|entry| entry.record.direct_frame_seq)
            .expect("direct metadata");
        assert_eq!(direct_seq, FIRST_DATA_FRAME_SEQ_V5 + 1);
    }

    #[test]
    fn transport_receipt_releases_native_copy_not_application_cursor() {
        let (raw, fence) = path(1, 1);
        let session = [0x21; 16];
        let bridge = ready_bridge(fence, 16, 1_024, Direction::PcToAndroid);
        activate_direction(&bridge, Direction::PcToAndroid, session, 0);
        let outbound = route_envelope(raw, Direction::PcToAndroid, session, 1, true);
        assert_eq!(bridge.route_envelope(&outbound, b"copy"), V5_OK);
        bridge.lock_state().last_wire_sent_seq = FIRST_DATA_FRAME_SEQ_V5;
        let tracker = Arc::new(Mutex::new(
            DirectLivenessTracker::new(Duration::from_secs(10)).unwrap(),
        ));
        assert_eq!(
            receive_frame(
                &bridge.state,
                fence,
                Direction::AndroidToPc,
                termlink_direct_quic::ReceivedDirectFrame {
                    frame_seq: FIRST_DATA_FRAME_SEQ_V5,
                    body: DirectFrameBody::TransportReceipt(DirectTransportReceiptV1 {
                        highest_contiguous_frame_seq: FIRST_DATA_FRAME_SEQ_V5,
                    }),
                },
                &tracker,
            ),
            Ok(())
        );
        let state = bridge.lock_state();
        assert_eq!(state.route.buffered_envelopes(Direction::PcToAndroid), 0);
        assert_eq!(
            state
                .route
                .applied_cursor(Direction::PcToAndroid, SessionId::new(session)),
            0
        );
        assert_eq!(state.events.back().unwrap().released_frames, 1);
    }

    #[test]
    fn terminal_events_are_linearized_and_failed_paths_can_only_drain() {
        let (_, fence) = path(1, 1);
        let bridge = ready_bridge(fence, 8, 1_024, Direction::PcToAndroid);
        assert_eq!(bridge.control(WorkerCommand::Cancel { path: fence }), V5_OK);
        fail_state(&bridge.state, fence, V5_ERR_TIMEOUT);
        {
            let state = bridge.lock_state();
            assert_eq!(state.lifecycle, Lifecycle::Cancelled);
            assert_eq!(state.events.len(), 1);
            assert_eq!(state.events[0].kind, EVENT_CANCELLED_V5);
        }
        assert_eq!(bridge.path_usage(fence), V5_ERR_STALE_PATH);

        let (second_raw, second_fence) = path(2, 2);
        let second = ready_bridge(second_fence, 8, 1_024, Direction::PcToAndroid);
        assert_eq!(
            second.inject_received(second_fence, 4, b"drain".to_vec()),
            V5_OK
        );
        fail_state(&second.state, second_fence, V5_ERR_TIMEOUT);
        assert_eq!(
            second.control(WorkerCommand::NetworkChanged { path: second_fence }),
            V5_OK
        );
        {
            let state = second.lock_state();
            assert_eq!(state.lifecycle, Lifecycle::Failed);
            assert_eq!(
                state
                    .events
                    .iter()
                    .filter(|event| matches!(
                        event.kind,
                        EVENT_DIRECT_FAILED_V5 | EVENT_NETWORK_CHANGED_V5
                    ))
                    .count(),
                1
            );
        }
        let rejected_relay = ingress(
            second_raw,
            Direction::PcToAndroid,
            INGRESS_CARRIER_RELAY_V5,
            [0x55; 16],
            1,
            1_000,
            0,
        );
        let retained_before = {
            let state = second.lock_state();
            (
                state.route_actions.len(),
                state.ingress.len(),
                state.delivered_ingress.len(),
                state.queued_bytes,
            )
        };
        assert_eq!(
            second.arbitrate_ingress(&rejected_relay, b"new-after-failure"),
            V5_ERR_CANCELLED
        );
        {
            let state = second.lock_state();
            assert_eq!(
                (
                    state.route_actions.len(),
                    state.ingress.len(),
                    state.delivered_ingress.len(),
                    state.queued_bytes,
                ),
                retained_before
            );
        }
        let mut seq = 0;
        let mut required = 0;
        let mut output = [0_u8; 5];
        assert_eq!(
            second.receive_envelope(
                second_fence,
                &mut seq,
                output.as_mut_ptr(),
                output.len(),
                &mut required,
            ),
            V5_OK
        );
        assert_eq!(output, *b"drain");
    }

    #[test]
    fn ticket_expiry_and_liveness_failure_publish_specific_direct_statuses() {
        assert_eq!(
            map_ticket_error(TicketError::Expired),
            V5_ERR_TICKET_EXPIRED
        );
        assert_eq!(map_ticket_error(TicketError::Signature), V5_ERR_PROTOCOL);
        assert_eq!(
            map_v4_reason(v4::REASON_INVALID_TICKET_V4),
            V5_ERR_TICKET_EXPIRED
        );
        let started = Instant::now();
        let mut tracker = DirectLivenessTracker::new(Duration::from_millis(1)).unwrap();
        tracker.begin_ping([0x44; 16], 1, started).unwrap();
        assert!(
            tracker
                .check_timeout(started + Duration::from_millis(1))
                .is_err()
        );
        let (_, ticket_fence) = path(1, 1);
        let ticket = ready_bridge(ticket_fence, 8, 1_024, Direction::PcToAndroid);
        fail_state(&ticket.state, ticket_fence, V5_ERR_TICKET_EXPIRED);
        let event = ticket
            .lock_state()
            .events
            .iter()
            .find(|event| event.kind == EVENT_DIRECT_FAILED_V5)
            .copied()
            .expect("ticket failure");
        assert_eq!(event.status, V5_ERR_TICKET_EXPIRED);

        let (_, live_fence) = path(2, 2);
        let live = ready_bridge(live_fence, 8, 1_024, Direction::PcToAndroid);
        fail_state(&live.state, live_fence, V5_ERR_TIMEOUT);
        let state = live.lock_state();
        assert_eq!(state.lifecycle, Lifecycle::Failed);
        assert_eq!(state.path, Some(live_fence));
        assert_eq!(
            state
                .events
                .iter()
                .find(|event| event.kind == EVENT_DIRECT_FAILED_V5)
                .unwrap()
                .status,
            V5_ERR_TIMEOUT
        );
    }

    #[test]
    fn delivered_ingress_is_bounded_by_the_frame_cap() {
        let (raw, fence) = path(1, 1);
        let bridge = ready_bridge(fence, 4, 1_024, Direction::PcToAndroid);
        for sequence in 1..=3 {
            let value = ingress(
                raw,
                Direction::PcToAndroid,
                INGRESS_CARRIER_RELAY_V5,
                [0x22; 16],
                sequence,
                sequence,
                0,
            );
            assert_eq!(bridge.arbitrate_ingress(&value, b"x"), V5_OK);
            assert_eq!(poll_action(&bridge, 1).0, V5_OK);
        }
        let fourth = ingress(
            raw,
            Direction::PcToAndroid,
            INGRESS_CARRIER_RELAY_V5,
            [0x22; 16],
            4,
            4,
            0,
        );
        assert_eq!(bridge.arbitrate_ingress(&fourth, b"x"), V5_ERR_QUEUE_FULL);
        let state = bridge.lock_state();
        assert_eq!(state.delivered_ingress.len(), 3);
        assert!(state.total_frames() <= state.max_frames);
    }

    #[test]
    fn destroy_joins_worker_before_returning() {
        let (_, fence) = path(1, 1);
        let bridge = bridge(Some(fence), 4, 64);
        let exited = Arc::clone(&bridge.worker_exited);
        drop(bridge);
        assert!(exited.load(Ordering::Acquire));
        thread::sleep(Duration::from_millis(1));
    }
}
