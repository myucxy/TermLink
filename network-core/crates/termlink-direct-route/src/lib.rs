//! Pure routing state for lossless Relay-to-Direct cutover.
//!
//! This crate deliberately owns no socket, task, platform handle, protobuf
//! decoder, terminal session, renderer, or durable/application ACK. Callers
//! supply already-authenticated epochs and winning-pair classifications. An
//! [`OpaqueEnvelope`] is retained and forwarded byte-for-byte; this crate never
//! parses or reserializes it.

use std::{
    collections::{BTreeMap, HashMap, VecDeque},
    sync::Arc,
};

pub const MAX_BUFFERED_ENVELOPES: usize = 256;
pub const MAX_BUFFERED_BYTES: usize = 8 * 1024 * 1024;
pub const MAX_SESSION_BARRIERS: usize = 32;
/// A long-lived authenticated path can add sessions in multiple cutover
/// batches. Keep the resulting per-direction membership bounded even though a
/// single proposal has the smaller wire limit above.
pub const MAX_ACTIVE_SESSION_ROUTES: usize = 256;
pub const MAX_DUPLICATE_GRACE_MS: u32 = 5_000;
/// Per-direction bound for application cursor identities retained across path
/// epochs. Existing identities remain updateable at the limit; new identities
/// fail atomically so deduplication state is never silently evicted.
pub const MAX_APPLIED_CURSORS: usize = 256;
/// Frames 1 and 2 contain peer proof/result; frame 3 is initial liveness.
pub const FIRST_DIRECT_DATA_FRAME_SEQ: u64 = 4;

#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct SessionId([u8; 16]);

impl SessionId {
    pub const fn new(value: [u8; 16]) -> Self {
        Self(value)
    }

    pub const fn as_bytes(&self) -> &[u8; 16] {
        &self.0
    }
}

#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub struct BarrierId([u8; 16]);

impl BarrierId {
    pub const fn new(value: [u8; 16]) -> Self {
        Self(value)
    }

    pub const fn as_bytes(&self) -> &[u8; 16] {
        &self.0
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct DirectPathBinding([u8; 32]);

impl DirectPathBinding {
    pub const fn new(value: [u8; 32]) -> Self {
        Self(value)
    }

    pub const fn as_bytes(&self) -> &[u8; 32] {
        &self.0
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Direction {
    PcToAndroid,
    AndroidToPc,
}

impl Direction {
    pub const fn opposite(self) -> Self {
        match self {
            Self::PcToAndroid => Self::AndroidToPc,
            Self::AndroidToPc => Self::PcToAndroid,
        }
    }

    const fn index(self) -> usize {
        match self {
            Self::PcToAndroid => 0,
            Self::AndroidToPc => 1,
        }
    }
}

/// Classification of an already authenticated winning candidate pair.
///
/// The state machine never receives candidates and cannot infer this value.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AuthenticatedPathKind {
    LocalAreaNetwork,
    NatTraversal,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Carrier {
    Relay,
    Direct(AuthenticatedPathKind),
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct OpaqueEnvelope {
    pub session_id: SessionId,
    pub sequence: u64,
    pub bytes: Arc<[u8]>,
}

impl OpaqueEnvelope {
    pub fn new(session_id: SessionId, sequence: u64, bytes: impl Into<Arc<[u8]>>) -> Self {
        Self {
            session_id,
            sequence,
            bytes: bytes.into(),
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct EnvelopeTransmission {
    pub direction: Direction,
    pub carrier: Carrier,
    pub direct_frame_seq: Option<u64>,
    pub envelope: OpaqueEnvelope,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum FallbackReason {
    BufferEnvelopeLimit,
    BufferByteLimit,
    DirectFailure,
    EpochReplaced,
    NegotiationTimeout,
    PeerRejected,
    BarrierMismatch,
    InvalidEnvelope,
    ProtocolViolation,
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct RouteBatch {
    pub transmissions: Vec<EnvelopeTransmission>,
    pub controls: Vec<ControlMessage>,
    pub fallback_reason: Option<FallbackReason>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct SessionBarrier {
    pub session_id: SessionId,
    pub first_direct_seq: u64,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CutoverProposal {
    pub barrier_id: BarrierId,
    pub direct_path_binding: DirectPathBinding,
    /// SHA-256 of the exact canonical barrier bytes, computed by the caller.
    pub barrier_sha256: [u8; 32],
    pub session_barriers: Vec<SessionBarrier>,
    pub duplicate_grace_ms: u32,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ControlMessage {
    Proposal {
        direction: Direction,
        path_epoch: u64,
        proposal: CutoverProposal,
    },
    Accept {
        direction: Direction,
        path_epoch: u64,
        barrier_id: BarrierId,
        barrier_sha256: [u8; 32],
        accepted: bool,
        reason: Option<RejectReason>,
    },
    Commit {
        direction: Direction,
        path_epoch: u64,
        barrier_id: BarrierId,
        barrier_sha256: [u8; 32],
        is_echo: bool,
    },
    Fallback {
        direction: Direction,
        path_epoch: u64,
        barrier_id: Option<BarrierId>,
        reason: FallbackReason,
    },
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RejectReason {
    InvalidEpoch,
    FutureEpoch,
    InvalidProposal,
    InvalidTimeout,
    PathBindingMismatch,
    DirectionBusy,
    BarrierIdMismatch,
    BarrierHashMismatch,
    UnexpectedAccept,
    LateCommit,
    PrefixTimeout,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TransitionOutcome {
    Applied,
    DeferredForPrefix,
    Activated,
    Rejected(RejectReason),
    IgnoredStale {
        received_epoch: u64,
        current_epoch: u64,
    },
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Transition {
    pub outcome: TransitionOutcome,
    pub control: Option<ControlMessage>,
    pub routes: RouteBatch,
}

impl Transition {
    fn plain(outcome: TransitionOutcome) -> Self {
        Self {
            outcome,
            control: None,
            routes: RouteBatch::default(),
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DirectionState {
    RelayOnly,
    ProposerAwaitingAccept,
    ProposerAwaitingCommitEcho,
    ResponderAwaitingPrefix,
    ResponderAwaitingCommit,
    ResponderCommitEchoPending,
    DirectActive,
}

/// The authoritative carrier decision for one session. A physical Direct path
/// can be active for earlier sessions while a later session is still crossing
/// its own make-before-break barrier.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SessionRouteState {
    RelayOnly,
    Negotiating,
    DirectActive,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum NegotiationRole {
    Proposer,
    Responder,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum NegotiationStep {
    AwaitingAccept,
    AwaitingPrefix,
    AwaitingCommit,
    AwaitingCommitEcho,
    CommitEchoPending,
}

#[derive(Clone, Debug)]
struct Negotiation {
    role: NegotiationRole,
    step: NegotiationStep,
    proposal: CutoverProposal,
    barriers: BTreeMap<SessionId, u64>,
    deadline_ms: u64,
}

#[derive(Clone, Debug)]
struct ActiveRoute {
    sessions: BTreeMap<SessionId, ActiveSession>,
}

#[derive(Clone, Copy, Debug)]
struct ActiveSession {
    barrier_id: BarrierId,
    first_direct_seq: u64,
    grace_until_ms: u64,
}

#[derive(Clone, Debug)]
enum Phase {
    RelayOnly,
    Active(ActiveRoute),
    /// This direction was committed to Direct in the current path epoch and
    /// has since fallen back. Reliable-stream Envelope frames whose sequence
    /// was already allocated may still arrive, but new cutover attempts are
    /// rejected until a successor path epoch resets the direction.
    Draining {
        remaining_late_envelopes: usize,
    },
}

#[derive(Clone, Debug)]
struct CachedEnvelope {
    ordinal: u64,
    envelope: OpaqueEnvelope,
    relay_copy_sent: bool,
}

#[derive(Clone, Debug)]
struct DirectionRoute {
    phase: Phase,
    /// At most one incremental batch is negotiated at a time. Keeping this
    /// separate from `phase` lets already committed sessions continue over
    /// Direct while later sessions remain on Relay until their own commit echo.
    negotiation: Option<Negotiation>,
    pending: VecDeque<CachedEnvelope>,
    outstanding: BTreeMap<u64, CachedEnvelope>,
    cached_envelopes: usize,
    cached_bytes: usize,
    next_ordinal: u64,
    next_outbound_frame_seq: u64,
    highest_received_frame_seq: u64,
    applied_cursors: HashMap<SessionId, u64>,
    /// Sessions explicitly removed from this path epoch. Already queued Direct
    /// copies may still arrive after their authoritative Relay replay or
    /// unsubscribe, so ingress may discard those copies without failing an
    /// otherwise healthy shared path. Relay input is never suppressed by this
    /// transport-only tombstone.
    retired_sessions: VecDeque<SessionId>,
    /// When the last active session is removed, a bounded number of Direct
    /// frames may already have crossed the native writer ownership boundary.
    /// Consume those stream positions without making the physical path one-shot.
    remaining_retired_direction_frames: usize,
}

impl DirectionRoute {
    fn new() -> Self {
        Self {
            phase: Phase::RelayOnly,
            negotiation: None,
            pending: VecDeque::new(),
            outstanding: BTreeMap::new(),
            cached_envelopes: 0,
            cached_bytes: 0,
            next_ordinal: 0,
            next_outbound_frame_seq: FIRST_DIRECT_DATA_FRAME_SEQ,
            // Proof/result occupy 1/2 and initial liveness occupies 3.
            highest_received_frame_seq: FIRST_DIRECT_DATA_FRAME_SEQ - 1,
            applied_cursors: HashMap::new(),
            retired_sessions: VecDeque::new(),
            remaining_retired_direction_frames: 0,
        }
    }

    fn public_state(&self) -> DirectionState {
        if matches!(self.phase, Phase::Active(_)) {
            DirectionState::DirectActive
        } else {
            self.negotiation
                .as_ref()
                .map(Self::negotiation_state)
                .unwrap_or(DirectionState::RelayOnly)
        }
    }

    fn negotiation_state(value: &Negotiation) -> DirectionState {
        match (value.role, value.step) {
            (NegotiationRole::Proposer, NegotiationStep::AwaitingAccept) => {
                DirectionState::ProposerAwaitingAccept
            }
            (NegotiationRole::Proposer, NegotiationStep::AwaitingCommitEcho) => {
                DirectionState::ProposerAwaitingCommitEcho
            }
            (NegotiationRole::Responder, NegotiationStep::AwaitingPrefix) => {
                DirectionState::ResponderAwaitingPrefix
            }
            (NegotiationRole::Responder, NegotiationStep::AwaitingCommit) => {
                DirectionState::ResponderAwaitingCommit
            }
            (NegotiationRole::Responder, NegotiationStep::CommitEchoPending) => {
                DirectionState::ResponderCommitEchoPending
            }
            _ => DirectionState::RelayOnly,
        }
    }

    fn session_state(&self, session_id: SessionId) -> SessionRouteState {
        if matches!(
            &self.phase,
            Phase::Active(active) if active.sessions.contains_key(&session_id)
        ) {
            SessionRouteState::DirectActive
        } else if self
            .negotiation
            .as_ref()
            .is_some_and(|value| value.barriers.contains_key(&session_id))
        {
            SessionRouteState::Negotiating
        } else {
            SessionRouteState::RelayOnly
        }
    }

    fn reset_for_epoch(&mut self) {
        self.phase = Phase::RelayOnly;
        self.negotiation = None;
        self.pending.clear();
        self.outstanding.clear();
        self.cached_envelopes = 0;
        self.cached_bytes = 0;
        self.next_ordinal = 0;
        self.next_outbound_frame_seq = FIRST_DIRECT_DATA_FRAME_SEQ;
        self.highest_received_frame_seq = FIRST_DIRECT_DATA_FRAME_SEQ - 1;
        self.retired_sessions.clear();
        self.remaining_retired_direction_frames = 0;
        // Application cursors are independent of transport/path epochs. They
        // must survive a route replacement so a late Relay/Direct duplicate
        // cannot be delivered again before the platform repeats its cursor.
    }

    fn next_ordinal(&mut self) -> u64 {
        let value = self.next_ordinal;
        self.next_ordinal = self.next_ordinal.saturating_add(1);
        value
    }

    fn can_cache(&self, bytes: usize) -> Result<(), FallbackReason> {
        if self.cached_envelopes >= MAX_BUFFERED_ENVELOPES {
            return Err(FallbackReason::BufferEnvelopeLimit);
        }
        if bytes > MAX_BUFFERED_BYTES.saturating_sub(self.cached_bytes) {
            return Err(FallbackReason::BufferByteLimit);
        }
        Ok(())
    }

    fn add_cache_usage(&mut self, bytes: usize) {
        self.cached_envelopes += 1;
        self.cached_bytes += bytes;
    }

    fn remove_cache_usage(&mut self, bytes: usize) {
        self.cached_envelopes -= 1;
        self.cached_bytes -= bytes;
    }

    fn take_frame_seq(&mut self) -> Option<u64> {
        let value = self.next_outbound_frame_seq;
        self.next_outbound_frame_seq = value.checked_add(1)?;
        Some(value)
    }

    fn retire_session(&mut self, session_id: SessionId) {
        self.retired_sessions.retain(|value| *value != session_id);
        self.retired_sessions.push_back(session_id);
        while self.retired_sessions.len() > MAX_ACTIVE_SESSION_ROUTES {
            self.retired_sessions.pop_front();
        }
    }

    fn reactivate_sessions<'a>(&mut self, sessions: impl Iterator<Item = &'a SessionId>) {
        for session_id in sessions {
            self.retired_sessions.retain(|value| value != session_id);
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum EpochInstallOutcome {
    Installed,
    RejectedInvalid,
    IgnoredStale {
        received_epoch: u64,
        current_epoch: u64,
    },
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum OutboundFrameSequenceError {
    InvalidEpoch,
    PathNotReady,
    Exhausted,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct EpochInstallResult {
    pub outcome: EpochInstallOutcome,
    pub routes: RouteBatch,
}

/// One instance owns route decisions for one authenticated peer connection.
///
/// Calls and returned actions form an ordered actor protocol: the caller must
/// execute a returned [`RouteBatch`] in vector order before making its next
/// mutating call. Direct frame sequence numbers preserve that enqueue order.
/// During duplicate grace the common application receiver must continue to
/// use its existing per-session sequence deduplication, because identical
/// bytes may arrive first on either carrier.
#[derive(Clone, Debug)]
pub struct PeerRouteMachine {
    path_epoch: u64,
    path_kind: Option<AuthenticatedPathKind>,
    path_binding: Option<DirectPathBinding>,
    directions: [DirectionRoute; 2],
}

impl Default for PeerRouteMachine {
    fn default() -> Self {
        Self::new()
    }
}

impl PeerRouteMachine {
    pub fn new() -> Self {
        Self {
            path_epoch: 0,
            path_kind: None,
            path_binding: None,
            directions: [DirectionRoute::new(), DirectionRoute::new()],
        }
    }

    pub const fn path_epoch(&self) -> u64 {
        self.path_epoch
    }

    pub const fn authenticated_path_kind(&self) -> Option<AuthenticatedPathKind> {
        self.path_kind
    }

    pub const fn direct_path_binding(&self) -> Option<DirectPathBinding> {
        self.path_binding
    }

    pub fn state(&self, direction: Direction) -> DirectionState {
        self.direction(direction).public_state()
    }

    pub fn authoritative_carrier(&self, direction: Direction) -> Carrier {
        match &self.direction(direction).phase {
            Phase::Active(_) => Carrier::Direct(
                self.path_kind
                    .expect("an active direction always has an authenticated path"),
            ),
            _ => Carrier::Relay,
        }
    }

    pub fn session_state(&self, direction: Direction, session_id: SessionId) -> SessionRouteState {
        self.direction(direction).session_state(session_id)
    }

    pub fn authoritative_carrier_for_session(
        &self,
        direction: Direction,
        session_id: SessionId,
    ) -> Carrier {
        if self.session_state(direction, session_id) == SessionRouteState::DirectActive {
            Carrier::Direct(
                self.path_kind
                    .expect("an active session always has an authenticated path"),
            )
        } else {
            Carrier::Relay
        }
    }

    /// Returns only sessions currently participating in a cutover or committed
    /// to Direct. Relay-only sessions that never entered this state machine are
    /// deliberately absent.
    pub fn session_states(&self, direction: Direction) -> BTreeMap<SessionId, SessionRouteState> {
        let route = self.direction(direction);
        let mut states = BTreeMap::new();
        if let Phase::Active(active) = &route.phase {
            states.extend(
                active
                    .sessions
                    .keys()
                    .copied()
                    .map(|session_id| (session_id, SessionRouteState::DirectActive)),
            );
        }
        if let Some(negotiation) = &route.negotiation {
            states.extend(
                negotiation
                    .barriers
                    .keys()
                    .copied()
                    .map(|session_id| (session_id, SessionRouteState::Negotiating)),
            );
        }
        states
    }

    /// True only for a session explicitly removed in the current path epoch.
    /// Callers may drop a late Direct copy for this session without treating it
    /// as an attack on other sessions sharing the same authenticated path.
    pub fn is_retired_direct_session(&self, direction: Direction, session_id: SessionId) -> bool {
        self.direction(direction)
            .retired_sessions
            .contains(&session_id)
    }

    /// Returns the remaining duplicate grace for one committed session. This
    /// keeps ingress arbitration inside the native authority instead of
    /// trusting a platform-supplied timeout.
    pub fn session_duplicate_grace_remaining_ms(
        &self,
        direction: Direction,
        session_id: SessionId,
        now_ms: u64,
    ) -> u32 {
        let Phase::Active(active) = &self.direction(direction).phase else {
            return 0;
        };
        active
            .sessions
            .get(&session_id)
            .map(|session| {
                u32::try_from(session.grace_until_ms.saturating_sub(now_ms)).unwrap_or(u32::MAX)
            })
            .unwrap_or(0)
    }

    /// Authorizes one already parsed Direct Envelope against the exact
    /// committed session barrier and returns the remaining duplicate grace.
    /// `None` means this session/sequence is still Relay-authoritative.
    pub fn direct_ingress_grace_remaining_ms(
        &self,
        direction: Direction,
        session_id: SessionId,
        sequence: u64,
        now_ms: u64,
    ) -> Option<u32> {
        let Phase::Active(active) = &self.direction(direction).phase else {
            return None;
        };
        let session = active.sessions.get(&session_id)?;
        if sequence < session.first_direct_seq {
            return None;
        }
        Some(u32::try_from(session.grace_until_ms.saturating_sub(now_ms)).unwrap_or(u32::MAX))
    }

    pub fn applied_cursor(&self, direction: Direction, session_id: SessionId) -> u64 {
        self.direction(direction)
            .applied_cursors
            .get(&session_id)
            .copied()
            .unwrap_or(0)
    }

    /// Reserves the next sequence on one physical outbound Direct stream for
    /// a receipt or liveness frame. Envelope frames use the same allocator in
    /// `route_outbound`, so callers cannot create overlapping sequences.
    pub fn reserve_outbound_control_frame(
        &mut self,
        direction: Direction,
        path_epoch: u64,
    ) -> Result<u64, OutboundFrameSequenceError> {
        if path_epoch != self.path_epoch {
            return Err(OutboundFrameSequenceError::InvalidEpoch);
        }
        if self.path_kind.is_none() {
            return Err(OutboundFrameSequenceError::PathNotReady);
        }
        self.direction_mut(direction)
            .take_frame_seq()
            .ok_or(OutboundFrameSequenceError::Exhausted)
    }

    pub fn buffered_envelopes(&self, direction: Direction) -> usize {
        self.direction(direction).cached_envelopes
    }

    pub fn buffered_bytes(&self, direction: Direction) -> usize {
        self.direction(direction).cached_bytes
    }

    pub fn highest_received_direct_frame_seq(&self, direction: Direction) -> u64 {
        self.direction(direction).highest_received_frame_seq
    }

    /// Installs a path classification only after upper-layer authentication.
    /// Replacing an epoch returns every unreceipted exact Envelope that still
    /// needs Relay delivery before resetting the two direction machines.
    pub fn install_authenticated_path(
        &mut self,
        path_epoch: u64,
        kind: AuthenticatedPathKind,
        path_binding: DirectPathBinding,
    ) -> EpochInstallResult {
        if path_epoch == 0 || path_epoch > i64::MAX as u64 || is_all_zero(path_binding.as_bytes()) {
            return EpochInstallResult {
                outcome: EpochInstallOutcome::RejectedInvalid,
                routes: RouteBatch::default(),
            };
        }
        if path_epoch <= self.path_epoch {
            return EpochInstallResult {
                outcome: EpochInstallOutcome::IgnoredStale {
                    received_epoch: path_epoch,
                    current_epoch: self.path_epoch,
                },
                routes: RouteBatch::default(),
            };
        }

        let mut routes = self.fallback_all(FallbackReason::EpochReplaced, true);
        if routes.transmissions.is_empty() && routes.controls.is_empty() {
            routes.fallback_reason = None;
        }
        self.path_epoch = path_epoch;
        self.path_kind = Some(kind);
        self.path_binding = Some(path_binding);
        for route in &mut self.directions {
            route.reset_for_epoch();
        }
        EpochInstallResult {
            outcome: EpochInstallOutcome::Installed,
            routes,
        }
    }

    pub fn start_proposal(
        &mut self,
        direction: Direction,
        path_epoch: u64,
        proposal: CutoverProposal,
        now_ms: u64,
        timeout_ms: u64,
    ) -> Transition {
        if let Some(outcome) = self.reject_epoch(path_epoch) {
            return Transition::plain(outcome);
        }
        if timeout_ms == 0 {
            return Transition::plain(TransitionOutcome::Rejected(RejectReason::InvalidTimeout));
        }
        let barriers = match validate_proposal(&proposal, self.path_binding) {
            Ok(value) => value,
            Err(reason) => return Transition::plain(TransitionOutcome::Rejected(reason)),
        };
        let route = self.direction(direction);
        if route.negotiation.is_some()
            || matches!(route.phase, Phase::Draining { .. })
            || active_sessions_overlap_or_exceed(&route.phase, &barriers, proposal.barrier_id)
        {
            return Transition::plain(TransitionOutcome::Rejected(RejectReason::DirectionBusy));
        }

        let route = self.direction_mut(direction);
        route.reactivate_sessions(barriers.keys());
        route.negotiation = Some(Negotiation {
            role: NegotiationRole::Proposer,
            step: NegotiationStep::AwaitingAccept,
            proposal: proposal.clone(),
            barriers,
            deadline_ms: now_ms.saturating_add(timeout_ms),
        });
        Transition {
            outcome: TransitionOutcome::Applied,
            control: Some(ControlMessage::Proposal {
                direction,
                path_epoch,
                proposal,
            }),
            routes: RouteBatch::default(),
        }
    }

    pub fn receive_proposal(
        &mut self,
        direction: Direction,
        path_epoch: u64,
        proposal: CutoverProposal,
        now_ms: u64,
        timeout_ms: u64,
    ) -> Transition {
        if let Some(outcome) = self.reject_epoch(path_epoch) {
            return Transition::plain(outcome);
        }
        if timeout_ms == 0 {
            return Transition::plain(TransitionOutcome::Rejected(RejectReason::InvalidTimeout));
        }
        let barriers = match validate_proposal(&proposal, self.path_binding) {
            Ok(value) => value,
            Err(reason) => return Transition::plain(TransitionOutcome::Rejected(reason)),
        };
        let route = self.direction(direction);
        if route.negotiation.is_some()
            || matches!(route.phase, Phase::Draining { .. })
            || active_sessions_overlap_or_exceed(&route.phase, &barriers, proposal.barrier_id)
        {
            return Transition::plain(TransitionOutcome::Rejected(RejectReason::DirectionBusy));
        }
        let ready = self.prefix_ready(direction, &barriers);
        let step = if ready {
            NegotiationStep::AwaitingCommit
        } else {
            NegotiationStep::AwaitingPrefix
        };
        let route = self.direction_mut(direction);
        route.reactivate_sessions(barriers.keys());
        route.negotiation = Some(Negotiation {
            role: NegotiationRole::Responder,
            step,
            proposal: proposal.clone(),
            barriers,
            deadline_ms: now_ms.saturating_add(timeout_ms),
        });

        if ready {
            Transition {
                outcome: TransitionOutcome::Applied,
                control: Some(accept_message(direction, path_epoch, &proposal, true, None)),
                routes: RouteBatch::default(),
            }
        } else {
            Transition::plain(TransitionOutcome::DeferredForPrefix)
        }
    }

    /// Updates only the caller-verified highest continuous application cursor.
    /// It does not emit or advance any durable/application ACK.
    pub fn update_applied_cursor(
        &mut self,
        direction: Direction,
        session_id: SessionId,
        highest_contiguous: u64,
    ) -> Result<Option<Transition>, CursorUpdateError> {
        let route = self.direction_mut(direction);
        if !route.applied_cursors.contains_key(&session_id)
            && route.applied_cursors.len() >= MAX_APPLIED_CURSORS
        {
            return Err(CursorUpdateError::Capacity);
        }
        let cursor = route.applied_cursors.entry(session_id).or_default();
        *cursor = (*cursor).max(highest_contiguous);

        let ready = match &route.negotiation {
            Some(negotiation)
                if negotiation.role == NegotiationRole::Responder
                    && negotiation.step == NegotiationStep::AwaitingPrefix =>
            {
                negotiation.barriers.iter().all(|(session, first)| {
                    route.applied_cursors.get(session).copied().unwrap_or(0) >= first - 1
                })
            }
            _ => false,
        };
        if !ready {
            return Ok(None);
        }

        let proposal = match &mut route.negotiation {
            Some(negotiation) => {
                negotiation.step = NegotiationStep::AwaitingCommit;
                negotiation.proposal.clone()
            }
            _ => unreachable!("readiness was checked against a negotiation"),
        };
        Ok(Some(Transition {
            outcome: TransitionOutcome::Applied,
            control: Some(accept_message(
                direction,
                self.path_epoch,
                &proposal,
                true,
                None,
            )),
            routes: RouteBatch::default(),
        }))
    }

    /// Removes one session from this direction without disturbing other
    /// sessions on the authenticated path. Any unreceipted Direct-only bytes
    /// are returned for byte-identical Relay recovery. If the session belonged
    /// to an in-flight multi-session proposal, that uncommitted batch is
    /// cancelled atomically because its canonical barrier cannot be rewritten.
    pub fn deregister_session(
        &mut self,
        direction: Direction,
        session_id: SessionId,
    ) -> RouteBatch {
        if is_all_zero(session_id.as_bytes()) {
            return RouteBatch::default();
        }
        let was_negotiating = self
            .direction(direction)
            .negotiation
            .as_ref()
            .is_some_and(|value| value.barriers.contains_key(&session_id));
        let mut batch = if was_negotiating {
            self.abort_negotiation(direction, FallbackReason::PeerRejected, true)
        } else {
            RouteBatch::default()
        };

        let route = self.direction_mut(direction);
        route.applied_cursors.remove(&session_id);

        let mut removed_active = false;
        let mut last_active_removed = false;
        if let Phase::Active(active) = &mut route.phase {
            removed_active = active.sessions.remove(&session_id).is_some();
            last_active_removed = removed_active && active.sessions.is_empty();
        }
        if last_active_removed {
            route.phase = Phase::RelayOnly;
            route.remaining_retired_direction_frames = MAX_BUFFERED_ENVELOPES;
        }
        if removed_active || was_negotiating {
            route.retire_session(session_id);
        }

        let mut removed = Vec::new();
        let mut retained_pending = VecDeque::new();
        while let Some(entry) = route.pending.pop_front() {
            if entry.envelope.session_id == session_id {
                route.remove_cache_usage(entry.envelope.bytes.len());
                removed.push(entry);
            } else {
                retained_pending.push_back(entry);
            }
        }
        route.pending = retained_pending;

        let outstanding = std::mem::take(&mut route.outstanding);
        for (frame_seq, entry) in outstanding {
            if entry.envelope.session_id == session_id {
                route.remove_cache_usage(entry.envelope.bytes.len());
                removed.push(entry);
            } else {
                route.outstanding.insert(frame_seq, entry);
            }
        }
        removed.sort_by_key(|entry| entry.ordinal);
        batch.transmissions.extend(
            removed
                .into_iter()
                .filter(|entry| !entry.relay_copy_sent)
                .map(|entry| relay_transmission(direction, entry.envelope)),
        );
        batch
    }

    pub fn receive_accept(
        &mut self,
        direction: Direction,
        path_epoch: u64,
        barrier_id: BarrierId,
        barrier_sha256: [u8; 32],
        accepted: bool,
    ) -> Transition {
        if let Some(outcome) = self.reject_epoch(path_epoch) {
            return Transition::plain(outcome);
        }
        let expected = match &self.direction(direction).negotiation {
            Some(negotiation)
                if negotiation.role == NegotiationRole::Proposer
                    && negotiation.step == NegotiationStep::AwaitingAccept =>
            {
                negotiation.proposal.clone()
            }
            _ => {
                return Transition::plain(TransitionOutcome::Rejected(
                    RejectReason::UnexpectedAccept,
                ));
            }
        };
        if barrier_id != expected.barrier_id {
            return self.reject_barrier(direction, RejectReason::BarrierIdMismatch);
        }
        if barrier_sha256 != expected.barrier_sha256 {
            return self.reject_barrier(direction, RejectReason::BarrierHashMismatch);
        }
        if !accepted {
            let routes = self.abort_negotiation(direction, FallbackReason::PeerRejected, false);
            return Transition {
                outcome: TransitionOutcome::Applied,
                control: None,
                routes,
            };
        }

        if let Some(negotiation) = &mut self.direction_mut(direction).negotiation {
            negotiation.step = NegotiationStep::AwaitingCommitEcho;
        }
        Transition {
            outcome: TransitionOutcome::Applied,
            control: Some(ControlMessage::Commit {
                direction,
                path_epoch,
                barrier_id,
                barrier_sha256,
                is_echo: false,
            }),
            routes: RouteBatch::default(),
        }
    }

    /// Receives either the initial commit (responder) or its echo (proposer).
    pub fn receive_commit(
        &mut self,
        direction: Direction,
        path_epoch: u64,
        barrier_id: BarrierId,
        barrier_sha256: [u8; 32],
        now_ms: u64,
    ) -> Transition {
        if let Some(outcome) = self.reject_epoch(path_epoch) {
            return Transition::plain(outcome);
        }
        let (proposal, role, step) = match &self.direction(direction).negotiation {
            Some(negotiation) => (
                negotiation.proposal.clone(),
                negotiation.role,
                negotiation.step,
            ),
            _ => {
                return Transition::plain(TransitionOutcome::Rejected(RejectReason::LateCommit));
            }
        };
        if barrier_id != proposal.barrier_id {
            return self.reject_barrier(direction, RejectReason::BarrierIdMismatch);
        }
        if barrier_sha256 != proposal.barrier_sha256 {
            return self.reject_barrier(direction, RejectReason::BarrierHashMismatch);
        }

        match (role, step) {
            (NegotiationRole::Responder, NegotiationStep::AwaitingCommit) => {
                if let Some(negotiation) = &mut self.direction_mut(direction).negotiation {
                    negotiation.step = NegotiationStep::CommitEchoPending;
                }
                Transition {
                    outcome: TransitionOutcome::Applied,
                    control: Some(ControlMessage::Commit {
                        direction,
                        path_epoch,
                        barrier_id,
                        barrier_sha256,
                        is_echo: true,
                    }),
                    routes: RouteBatch::default(),
                }
            }
            (NegotiationRole::Proposer, NegotiationStep::AwaitingCommitEcho) => {
                self.activate(direction, now_ms)
            }
            _ => Transition::plain(TransitionOutcome::Rejected(RejectReason::LateCommit)),
        }
    }

    /// The responder calls this only after the commit echo has been accepted
    /// by its reliable control sender. Until then Relay remains authoritative.
    pub fn commit_echo_sent(
        &mut self,
        direction: Direction,
        path_epoch: u64,
        barrier_id: BarrierId,
        now_ms: u64,
    ) -> Transition {
        if let Some(outcome) = self.reject_epoch(path_epoch) {
            return Transition::plain(outcome);
        }
        let valid = matches!(
            &self.direction(direction).negotiation,
            Some(negotiation)
                if negotiation.role == NegotiationRole::Responder
                    && negotiation.step == NegotiationStep::CommitEchoPending
                    && negotiation.proposal.barrier_id == barrier_id
        );
        if !valid {
            return Transition::plain(TransitionOutcome::Rejected(RejectReason::LateCommit));
        }
        self.activate(direction, now_ms)
    }

    pub fn poll_timeout(&mut self, direction: Direction, now_ms: u64) -> Option<Transition> {
        let (expired, proposal, responder_waiting_prefix) =
            match &self.direction(direction).negotiation {
                Some(value) => (
                    now_ms >= value.deadline_ms,
                    value.proposal.clone(),
                    value.role == NegotiationRole::Responder
                        && value.step == NegotiationStep::AwaitingPrefix,
                ),
                _ => return None,
            };
        if !expired {
            return None;
        }
        let routes = self.abort_negotiation(direction, FallbackReason::NegotiationTimeout, false);
        let control = if responder_waiting_prefix {
            Some(accept_message(
                direction,
                self.path_epoch,
                &proposal,
                false,
                Some(RejectReason::PrefixTimeout),
            ))
        } else {
            Some(ControlMessage::Fallback {
                direction,
                path_epoch: self.path_epoch,
                barrier_id: Some(proposal.barrier_id),
                reason: FallbackReason::NegotiationTimeout,
            })
        };
        Some(Transition {
            outcome: TransitionOutcome::Rejected(RejectReason::PrefixTimeout),
            control,
            routes,
        })
    }

    /// Routes one exact serialized Envelope. Before commit echo, records at or
    /// beyond a declared barrier continue over Relay and retain an exact copy
    /// for the ordered Direct replay that follows activation.
    pub fn route_outbound(
        &mut self,
        direction: Direction,
        envelope: OpaqueEnvelope,
        now_ms: u64,
    ) -> RouteBatch {
        if !valid_direct_envelope(&envelope) {
            let mut batch =
                self.fallback_direction(direction, FallbackReason::InvalidEnvelope, true);
            batch
                .transmissions
                .push(relay_transmission(direction, envelope));
            return batch;
        }
        let (committed, first_direct) = {
            let route = self.direction(direction);
            let committed = match &route.phase {
                Phase::Active(active) => active
                    .sessions
                    .get(&envelope.session_id)
                    .map(|session| session.first_direct_seq),
                _ => None,
            };
            let negotiating = route
                .negotiation
                .as_ref()
                .and_then(|value| value.barriers.get(&envelope.session_id).copied());
            (committed.is_some(), committed.or(negotiating))
        };
        let Some(first_direct) = first_direct else {
            return relay_one(direction, envelope);
        };
        if envelope.sequence < first_direct {
            return relay_one(direction, envelope);
        }

        if let Err(reason) = self.direction(direction).can_cache(envelope.bytes.len()) {
            let mut batch = if committed {
                self.fallback_direction(direction, reason, true)
            } else {
                self.abort_negotiation(direction, reason, true)
            };
            batch
                .transmissions
                .push(relay_transmission(direction, envelope));
            return batch;
        }

        if committed {
            return self.send_direct(direction, envelope, now_ms, false);
        }

        let route = self.direction_mut(direction);
        let cached = CachedEnvelope {
            ordinal: route.next_ordinal(),
            envelope: envelope.clone(),
            relay_copy_sent: true,
        };
        route.add_cache_usage(cached.envelope.bytes.len());
        route.pending.push_back(cached);
        // make-before-break: the exact bytes continue immediately over
        // authoritative Relay while a retained copy awaits commit echo.
        relay_one(direction, envelope)
    }

    /// A physical Direct failure can invalidate both independently committed
    /// directions. Every unreceipted record without an existing Relay copy is
    /// returned in original enqueue order for immediate Relay retransmission.
    pub fn direct_failed(&mut self, path_epoch: u64) -> RouteBatch {
        if path_epoch != self.path_epoch {
            return RouteBatch::default();
        }
        let routes = self.fallback_all(FallbackReason::DirectFailure, true);
        self.path_kind = None;
        self.path_binding = None;
        routes
    }

    pub fn direction_failed(&mut self, direction: Direction, path_epoch: u64) -> RouteBatch {
        if path_epoch != self.path_epoch {
            return RouteBatch::default();
        }
        self.fallback_direction(direction, FallbackReason::DirectFailure, true)
    }

    pub fn receive_fallback(
        &mut self,
        direction: Direction,
        path_epoch: u64,
        barrier_id: Option<BarrierId>,
    ) -> Transition {
        if let Some(outcome) = self.reject_epoch(path_epoch) {
            return Transition::plain(outcome);
        }
        if barrier_id.is_none() {
            return Transition {
                outcome: TransitionOutcome::Applied,
                control: None,
                routes: self.fallback_direction(direction, FallbackReason::PeerRejected, false),
            };
        }
        let barrier_id = barrier_id.expect("checked above");
        if self
            .direction(direction)
            .negotiation
            .as_ref()
            .is_some_and(|value| value.proposal.barrier_id == barrier_id)
        {
            return Transition {
                outcome: TransitionOutcome::Applied,
                control: None,
                routes: self.abort_negotiation(direction, FallbackReason::PeerRejected, false),
            };
        }
        if !self.active_barrier_exists(direction, barrier_id) {
            return Transition::plain(TransitionOutcome::Rejected(RejectReason::BarrierIdMismatch));
        }
        Transition {
            outcome: TransitionOutcome::Applied,
            control: None,
            routes: self.fallback_direction(direction, FallbackReason::PeerRejected, false),
        }
    }

    /// Applies a transport-only receipt. It releases retained Direct copies
    /// and intentionally has no application/durable ACK return value.
    pub fn apply_transport_receipt(
        &mut self,
        acknowledged_direction: Direction,
        path_epoch: u64,
        receipt: TransportReceiptV1,
    ) -> ReceiptApplyOutcome {
        if path_epoch < self.path_epoch {
            return ReceiptApplyOutcome::IgnoredStale;
        }
        if path_epoch != self.path_epoch {
            return ReceiptApplyOutcome::Rejected;
        }
        let route = self.direction_mut(acknowledged_direction);
        if receipt.highest_contiguous_frame_seq >= route.next_outbound_frame_seq {
            return ReceiptApplyOutcome::Rejected;
        }
        let released: Vec<u64> = route
            .outstanding
            .range(..=receipt.highest_contiguous_frame_seq)
            .map(|(sequence, _)| *sequence)
            .collect();
        let mut released_bytes = 0_usize;
        for sequence in &released {
            if let Some(cached) = route.outstanding.remove(sequence) {
                released_bytes = released_bytes.saturating_add(cached.envelope.bytes.len());
                route.remove_cache_usage(cached.envelope.bytes.len());
            }
        }
        ReceiptApplyOutcome::Applied {
            released_envelopes: released.len(),
            released_bytes,
        }
    }

    /// Records an incoming Direct frame on its reliable V1 stream. Envelope
    /// frames get a cumulative receipt; receipt and control frames never do,
    /// structurally preventing receipt-of-receipt loops.
    pub fn observe_inbound_direct_frame(
        &mut self,
        frame_direction: Direction,
        path_epoch: u64,
        frame_seq: u64,
        kind: InboundDirectFrame,
    ) -> InboundFrameResult {
        if path_epoch < self.path_epoch {
            return InboundFrameResult::IgnoredStale;
        }
        if path_epoch != self.path_epoch {
            return InboundFrameResult::Rejected(DirectFrameReject::Epoch);
        }
        let expected = match self
            .direction(frame_direction)
            .highest_received_frame_seq
            .checked_add(1)
        {
            Some(value) => value,
            None => return InboundFrameResult::Rejected(DirectFrameReject::Sequence),
        };
        if frame_seq != expected {
            return InboundFrameResult::Rejected(DirectFrameReject::Sequence);
        }
        if matches!(kind, InboundDirectFrame::Envelope)
            && matches!(
                self.direction(frame_direction).phase,
                Phase::Draining { .. }
            )
        {
            let route = self.direction_mut(frame_direction);
            let Phase::Draining {
                remaining_late_envelopes,
            } = &mut route.phase
            else {
                unreachable!("the draining phase was checked above")
            };
            if *remaining_late_envelopes == 0 {
                return InboundFrameResult::Rejected(DirectFrameReject::DirectionNotCommitted);
            }
            *remaining_late_envelopes -= 1;
            // One frame can already be in the QUIC send future while a
            // direction-level Relay fallback is committed. Consume its stream
            // sequence but deliberately do not expose or receipt the payload;
            // the fallback batch already contains the exact Relay bytes. This
            // late in-flight copy must not retire the shared physical path or
            // the independently healthy opposite direction.
            route.highest_received_frame_seq = frame_seq;
            return InboundFrameResult::NoReceipt {
                applied_transport_receipt: None,
            };
        }
        if matches!(kind, InboundDirectFrame::Envelope)
            && matches!(self.direction(frame_direction).phase, Phase::RelayOnly)
            && self
                .direction(frame_direction)
                .remaining_retired_direction_frames
                > 0
        {
            let route = self.direction_mut(frame_direction);
            route.remaining_retired_direction_frames -= 1;
            route.highest_received_frame_seq = frame_seq;
            return InboundFrameResult::NoReceipt {
                applied_transport_receipt: None,
            };
        }
        if matches!(kind, InboundDirectFrame::Envelope)
            && !matches!(self.direction(frame_direction).phase, Phase::Active(_))
        {
            // A Direct data frame cannot become authoritative before the
            // direction's commit echo completes. Only Draining above may
            // consume a frame allocated by a formerly Active direction.
            return InboundFrameResult::Rejected(DirectFrameReject::DirectionNotCommitted);
        }
        self.direction_mut(frame_direction)
            .highest_received_frame_seq = frame_seq;

        match kind {
            InboundDirectFrame::Envelope => {
                let receipt_direction = frame_direction.opposite();
                let Some(receipt_frame_seq) =
                    self.direction_mut(receipt_direction).take_frame_seq()
                else {
                    return InboundFrameResult::Rejected(DirectFrameReject::Sequence);
                };
                InboundFrameResult::Receipt(ReceiptTransmission {
                    direction: receipt_direction,
                    path_epoch,
                    frame_seq: receipt_frame_seq,
                    receipt: TransportReceiptV1 {
                        highest_contiguous_frame_seq: frame_seq,
                    },
                })
            }
            InboundDirectFrame::TransportReceipt(receipt) => {
                let outcome =
                    self.apply_transport_receipt(frame_direction.opposite(), path_epoch, receipt);
                InboundFrameResult::NoReceipt {
                    applied_transport_receipt: Some(outcome),
                }
            }
            InboundDirectFrame::ControlNoReceipt => InboundFrameResult::NoReceipt {
                applied_transport_receipt: None,
            },
        }
    }

    fn activate(&mut self, direction: Direction, now_ms: u64) -> Transition {
        let negotiation = match self.direction_mut(direction).negotiation.take() {
            Some(value) => value,
            _ => return Transition::plain(TransitionOutcome::Rejected(RejectReason::LateCommit)),
        };
        let grace_until_ms =
            now_ms.saturating_add(u64::from(negotiation.proposal.duplicate_grace_ms));
        let route = self.direction_mut(direction);
        if !matches!(route.phase, Phase::Active(_)) {
            route.phase = Phase::Active(ActiveRoute {
                sessions: BTreeMap::new(),
            });
        }
        let Phase::Active(active) = &mut route.phase else {
            unreachable!("an activation installs or extends an active route")
        };
        for (session_id, first_direct_seq) in negotiation.barriers {
            active.sessions.insert(
                session_id,
                ActiveSession {
                    barrier_id: negotiation.proposal.barrier_id,
                    first_direct_seq,
                    grace_until_ms,
                },
            );
        }
        let routes = self.flush_pending(direction, now_ms);
        Transition {
            outcome: TransitionOutcome::Activated,
            control: None,
            routes,
        }
    }

    fn flush_pending(&mut self, direction: Direction, now_ms: u64) -> RouteBatch {
        let mut pending = std::mem::take(&mut self.direction_mut(direction).pending);
        let mut batch = RouteBatch::default();
        while let Some(cached) = pending.pop_front() {
            // Usage was already charged while the Envelope waited. Remove it
            // before using the regular active-send path, which charges again.
            self.direction_mut(direction)
                .remove_cache_usage(cached.envelope.bytes.len());
            let routed =
                self.send_direct(direction, cached.envelope, now_ms, cached.relay_copy_sent);
            batch.transmissions.extend(routed.transmissions);
            if routed.fallback_reason.is_some() {
                batch.fallback_reason = routed.fallback_reason;
                batch.transmissions.extend(pending.into_iter().map(|entry| {
                    self.direction_mut(direction)
                        .remove_cache_usage(entry.envelope.bytes.len());
                    relay_transmission(direction, entry.envelope)
                }));
                break;
            }
        }
        batch
    }

    fn send_direct(
        &mut self,
        direction: Direction,
        envelope: OpaqueEnvelope,
        now_ms: u64,
        relay_already_sent: bool,
    ) -> RouteBatch {
        let grace = match &self.direction(direction).phase {
            Phase::Active(active) => active
                .sessions
                .get(&envelope.session_id)
                .is_some_and(|session| now_ms < session.grace_until_ms),
            _ => return relay_one(direction, envelope),
        };
        let Some(frame_seq) = self.direction_mut(direction).take_frame_seq() else {
            let mut batch =
                self.fallback_direction(direction, FallbackReason::ProtocolViolation, true);
            batch
                .transmissions
                .push(relay_transmission(direction, envelope));
            return batch;
        };
        let path_kind = self
            .path_kind
            .expect("a direct send always has an authenticated path");
        let route = self.direction_mut(direction);
        let cached = CachedEnvelope {
            ordinal: route.next_ordinal(),
            envelope: envelope.clone(),
            relay_copy_sent: relay_already_sent || grace,
        };
        route.add_cache_usage(cached.envelope.bytes.len());
        route.outstanding.insert(frame_seq, cached);

        let send_relay = grace && !relay_already_sent;
        let mut transmissions = Vec::with_capacity(if send_relay { 2 } else { 1 });
        if send_relay {
            transmissions.push(relay_transmission(direction, envelope.clone()));
        }
        transmissions.push(EnvelopeTransmission {
            direction,
            carrier: Carrier::Direct(path_kind),
            direct_frame_seq: Some(frame_seq),
            envelope,
        });
        RouteBatch {
            transmissions,
            controls: Vec::new(),
            fallback_reason: None,
        }
    }

    fn reject_barrier(&mut self, direction: Direction, reason: RejectReason) -> Transition {
        let routes = self.fallback_direction(direction, FallbackReason::BarrierMismatch, true);
        Transition {
            outcome: TransitionOutcome::Rejected(reason),
            control: None,
            routes,
        }
    }

    fn reject_epoch(&self, received_epoch: u64) -> Option<TransitionOutcome> {
        if received_epoch < self.path_epoch {
            Some(TransitionOutcome::IgnoredStale {
                received_epoch,
                current_epoch: self.path_epoch,
            })
        } else if received_epoch == 0 {
            Some(TransitionOutcome::Rejected(RejectReason::InvalidEpoch))
        } else if received_epoch != self.path_epoch || self.path_kind.is_none() {
            Some(TransitionOutcome::Rejected(RejectReason::FutureEpoch))
        } else {
            None
        }
    }

    fn prefix_ready(&self, direction: Direction, barriers: &BTreeMap<SessionId, u64>) -> bool {
        barriers.iter().all(|(session, first)| {
            self.applied_cursor(direction, *session) >= first.saturating_sub(1)
        })
    }

    fn active_barrier_exists(&self, direction: Direction, barrier_id: BarrierId) -> bool {
        matches!(
            &self.direction(direction).phase,
            Phase::Active(active)
                if active.sessions.values().any(|session| session.barrier_id == barrier_id)
        )
    }

    /// Aborts only the uncommitted incremental batch. Existing Direct sessions
    /// and their outstanding exact bytes remain untouched.
    fn abort_negotiation(
        &mut self,
        direction: Direction,
        reason: FallbackReason,
        notify_peer: bool,
    ) -> RouteBatch {
        let path_epoch = self.path_epoch;
        let route = self.direction_mut(direction);
        let Some(negotiation) = route.negotiation.take() else {
            return RouteBatch::default();
        };
        let mut cached: Vec<CachedEnvelope> = route.pending.drain(..).collect();
        cached.sort_by_key(|entry| entry.ordinal);
        for entry in &cached {
            route.remove_cache_usage(entry.envelope.bytes.len());
        }
        let transmissions = cached
            .into_iter()
            .filter(|entry| !entry.relay_copy_sent)
            .map(|entry| relay_transmission(direction, entry.envelope))
            .collect::<Vec<_>>();
        RouteBatch {
            transmissions,
            controls: if notify_peer {
                vec![ControlMessage::Fallback {
                    direction,
                    path_epoch,
                    barrier_id: Some(negotiation.proposal.barrier_id),
                    reason,
                }]
            } else {
                Vec::new()
            },
            fallback_reason: Some(reason),
        }
    }

    fn fallback_all(&mut self, reason: FallbackReason, notify_peer: bool) -> RouteBatch {
        let mut transmissions = Vec::new();
        let mut controls = Vec::new();
        for direction in [Direction::PcToAndroid, Direction::AndroidToPc] {
            let batch = self.fallback_direction(direction, reason, notify_peer);
            transmissions.extend(batch.transmissions);
            controls.extend(batch.controls);
        }
        transmissions.sort_by_key(|transmission| {
            // Direction order is deterministic; per-direction enqueue order is
            // preserved by fallback_direction. There is no cross-direction
            // application ordering contract.
            direction_sort_key(transmission.direction)
        });
        RouteBatch {
            fallback_reason: (!transmissions.is_empty()).then_some(reason),
            transmissions,
            controls,
        }
    }

    fn fallback_direction(
        &mut self,
        direction: Direction,
        reason: FallbackReason,
        notify_peer: bool,
    ) -> RouteBatch {
        let path_epoch = self.path_epoch;
        let route = self.direction_mut(direction);
        // An active direction may contain sessions committed by several
        // incremental barriers. A physical/directional failure therefore
        // cannot name one representative barrier: the peer could interpret
        // it as a batch-local abort and leave the remaining sessions on the
        // failed path. `None` is the wire-level full-direction fallback.
        let was_active = matches!(route.phase, Phase::Active(_));
        let barrier_id = (!was_active)
            .then(|| {
                route
                    .negotiation
                    .as_ref()
                    .map(|value| value.proposal.barrier_id)
            })
            .flatten();
        let mut cached: Vec<CachedEnvelope> = route.pending.drain(..).collect();
        cached.extend(std::mem::take(&mut route.outstanding).into_values());
        cached.sort_by_key(|entry| entry.ordinal);
        let transmissions = cached
            .into_iter()
            .filter(|entry| !entry.relay_copy_sent)
            .map(|entry| relay_transmission(direction, entry.envelope))
            .collect::<Vec<_>>();
        route.phase = match &route.phase {
            Phase::Active(_) => Phase::Draining {
                remaining_late_envelopes: MAX_BUFFERED_ENVELOPES,
            },
            Phase::Draining {
                remaining_late_envelopes,
            } => Phase::Draining {
                remaining_late_envelopes: *remaining_late_envelopes,
            },
            Phase::RelayOnly => Phase::RelayOnly,
        };
        route.negotiation = None;
        route.cached_envelopes = 0;
        route.cached_bytes = 0;
        RouteBatch {
            fallback_reason: Some(reason),
            transmissions,
            controls: if notify_peer && (was_active || barrier_id.is_some()) {
                vec![ControlMessage::Fallback {
                    direction,
                    path_epoch,
                    barrier_id,
                    reason,
                }]
            } else {
                Vec::new()
            },
        }
    }

    fn direction(&self, direction: Direction) -> &DirectionRoute {
        &self.directions[direction.index()]
    }

    fn direction_mut(&mut self, direction: Direction) -> &mut DirectionRoute {
        &mut self.directions[direction.index()]
    }
}

fn validate_proposal(
    proposal: &CutoverProposal,
    expected_path_binding: Option<DirectPathBinding>,
) -> Result<BTreeMap<SessionId, u64>, RejectReason> {
    if is_all_zero(proposal.barrier_id.as_bytes())
        || is_all_zero(&proposal.barrier_sha256)
        || is_all_zero(proposal.direct_path_binding.as_bytes())
        || proposal.session_barriers.is_empty()
        || proposal.session_barriers.len() > MAX_SESSION_BARRIERS
        || proposal.duplicate_grace_ms > MAX_DUPLICATE_GRACE_MS
    {
        return Err(RejectReason::InvalidProposal);
    }
    if Some(proposal.direct_path_binding) != expected_path_binding {
        return Err(RejectReason::PathBindingMismatch);
    }
    let mut barriers = BTreeMap::new();
    let mut previous_session = None;
    for barrier in &proposal.session_barriers {
        if is_all_zero(barrier.session_id.as_bytes())
            || barrier.first_direct_seq == 0
            || barrier.first_direct_seq > i64::MAX as u64
            || previous_session.is_some_and(|previous| barrier.session_id <= previous)
            || barriers
                .insert(barrier.session_id, barrier.first_direct_seq)
                .is_some()
        {
            return Err(RejectReason::InvalidProposal);
        }
        previous_session = Some(barrier.session_id);
    }
    Ok(barriers)
}

fn active_sessions_overlap_or_exceed(
    phase: &Phase,
    proposed: &BTreeMap<SessionId, u64>,
    proposed_barrier_id: BarrierId,
) -> bool {
    let Phase::Active(active) = phase else {
        return false;
    };
    active
        .sessions
        .len()
        .checked_add(proposed.len())
        .is_none_or(|count| count > MAX_ACTIVE_SESSION_ROUTES)
        || active
            .sessions
            .values()
            .any(|session| session.barrier_id == proposed_barrier_id)
        || proposed
            .keys()
            .any(|session_id| active.sessions.contains_key(session_id))
}

fn valid_direct_envelope(envelope: &OpaqueEnvelope) -> bool {
    !is_all_zero(envelope.session_id.as_bytes())
        && envelope.sequence > 0
        && envelope.sequence <= i64::MAX as u64
        && !envelope.bytes.is_empty()
        && envelope.bytes.len() <= MAX_BUFFERED_BYTES
}

fn is_all_zero<const N: usize>(value: &[u8; N]) -> bool {
    value.iter().all(|byte| *byte == 0)
}

fn accept_message(
    direction: Direction,
    path_epoch: u64,
    proposal: &CutoverProposal,
    accepted: bool,
    reason: Option<RejectReason>,
) -> ControlMessage {
    ControlMessage::Accept {
        direction,
        path_epoch,
        barrier_id: proposal.barrier_id,
        barrier_sha256: proposal.barrier_sha256,
        accepted,
        reason,
    }
}

fn relay_one(direction: Direction, envelope: OpaqueEnvelope) -> RouteBatch {
    RouteBatch {
        transmissions: vec![relay_transmission(direction, envelope)],
        controls: Vec::new(),
        fallback_reason: None,
    }
}

fn relay_transmission(direction: Direction, envelope: OpaqueEnvelope) -> EnvelopeTransmission {
    EnvelopeTransmission {
        direction,
        carrier: Carrier::Relay,
        direct_frame_seq: None,
        envelope,
    }
}

const fn direction_sort_key(direction: Direction) -> u8 {
    match direction {
        Direction::PcToAndroid => 0,
        Direction::AndroidToPc => 1,
    }
}

/// V1 reliable-stream receipts cannot contain missing ranges. Keeping only the
/// cumulative value in this type makes that wire invariant hard to violate.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct TransportReceiptV1 {
    highest_contiguous_frame_seq: u64,
}

impl TransportReceiptV1 {
    pub fn new(highest_contiguous_frame_seq: u64) -> Result<Self, ReceiptValidationError> {
        if !(FIRST_DIRECT_DATA_FRAME_SEQ..=i64::MAX as u64).contains(&highest_contiguous_frame_seq)
        {
            return Err(ReceiptValidationError::InvalidSequence);
        }
        Ok(Self {
            highest_contiguous_frame_seq,
        })
    }

    pub fn from_wire(
        highest_contiguous_frame_seq: u64,
        missing_ranges: &[(u64, u64)],
    ) -> Result<Self, ReceiptValidationError> {
        if !missing_ranges.is_empty() {
            return Err(ReceiptValidationError::MissingRangesForbidden);
        }
        Self::new(highest_contiguous_frame_seq)
    }

    pub const fn highest_contiguous_frame_seq(&self) -> u64 {
        self.highest_contiguous_frame_seq
    }

    pub const fn missing_ranges(&self) -> &[(u64, u64)] {
        &[]
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ReceiptValidationError {
    InvalidSequence,
    MissingRangesForbidden,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ReceiptApplyOutcome {
    Applied {
        released_envelopes: usize,
        released_bytes: usize,
    },
    IgnoredStale,
    Rejected,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CursorUpdateError {
    Capacity,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum InboundDirectFrame {
    Envelope,
    TransportReceipt(TransportReceiptV1),
    ControlNoReceipt,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ReceiptTransmission {
    pub direction: Direction,
    pub path_epoch: u64,
    pub frame_seq: u64,
    pub receipt: TransportReceiptV1,
}

impl ReceiptTransmission {
    pub const fn requires_receipt(&self) -> bool {
        false
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DirectFrameReject {
    Epoch,
    Sequence,
    DirectionNotCommitted,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum InboundFrameResult {
    Receipt(ReceiptTransmission),
    NoReceipt {
        applied_transport_receipt: Option<ReceiptApplyOutcome>,
    },
    IgnoredStale,
    Rejected(DirectFrameReject),
}

#[cfg(test)]
mod tests {
    use super::*;

    const PC_TO_ANDROID: Direction = Direction::PcToAndroid;
    const ANDROID_TO_PC: Direction = Direction::AndroidToPc;

    fn session(value: u8) -> SessionId {
        SessionId::new([value; 16])
    }

    fn proposal(id: u8, barriers: &[(u8, u64)], grace_ms: u32) -> CutoverProposal {
        CutoverProposal {
            barrier_id: BarrierId::new([id; 16]),
            direct_path_binding: DirectPathBinding::new([0xa5; 32]),
            barrier_sha256: [id.wrapping_add(32); 32],
            session_barriers: barriers
                .iter()
                .map(|(session_id, first_direct_seq)| SessionBarrier {
                    session_id: session(*session_id),
                    first_direct_seq: *first_direct_seq,
                })
                .collect(),
            duplicate_grace_ms: grace_ms,
        }
    }

    fn envelope(session_id: u8, sequence: u64, bytes: Arc<[u8]>) -> OpaqueEnvelope {
        OpaqueEnvelope::new(session(session_id), sequence, bytes)
    }

    fn installed() -> PeerRouteMachine {
        let mut machine = PeerRouteMachine::new();
        assert_eq!(
            machine
                .install_authenticated_path(
                    7,
                    AuthenticatedPathKind::LocalAreaNetwork,
                    DirectPathBinding::new([0xa5; 32]),
                )
                .outcome,
            EpochInstallOutcome::Installed
        );
        machine
    }

    fn activate_proposer(
        machine: &mut PeerRouteMachine,
        direction: Direction,
        proposal: &CutoverProposal,
        now_ms: u64,
    ) -> Transition {
        machine.start_proposal(direction, 7, proposal.clone(), now_ms, 1_000);
        let accepted = machine.receive_accept(
            direction,
            7,
            proposal.barrier_id,
            proposal.barrier_sha256,
            true,
        );
        assert!(matches!(
            accepted.control,
            Some(ControlMessage::Commit { is_echo: false, .. })
        ));
        machine.receive_commit(
            direction,
            7,
            proposal.barrier_id,
            proposal.barrier_sha256,
            now_ms,
        )
    }

    #[test]
    fn control_and_envelope_frames_share_one_directional_allocator() {
        let mut machine = installed();
        assert_eq!(
            machine.reserve_outbound_control_frame(PC_TO_ANDROID, 7),
            Ok(FIRST_DIRECT_DATA_FRAME_SEQ)
        );
        let value = proposal(1, &[(1, 1)], 0);
        activate_proposer(&mut machine, PC_TO_ANDROID, &value, 0);
        let routed =
            machine.route_outbound(PC_TO_ANDROID, envelope(1, 1, Arc::from(&b"exact"[..])), 1);
        assert_eq!(
            routed.transmissions[0].direct_frame_seq,
            Some(FIRST_DIRECT_DATA_FRAME_SEQ + 1)
        );
        assert_eq!(
            machine.reserve_outbound_control_frame(PC_TO_ANDROID, 7),
            Ok(FIRST_DIRECT_DATA_FRAME_SEQ + 2)
        );
        assert_eq!(
            machine.reserve_outbound_control_frame(ANDROID_TO_PC, 7),
            Ok(FIRST_DIRECT_DATA_FRAME_SEQ)
        );
    }

    #[test]
    fn control_frame_reservation_is_epoch_and_ready_fenced() {
        let mut machine = PeerRouteMachine::new();
        assert_eq!(
            machine.reserve_outbound_control_frame(PC_TO_ANDROID, 1),
            Err(OutboundFrameSequenceError::InvalidEpoch)
        );
        assert_eq!(
            machine.reserve_outbound_control_frame(PC_TO_ANDROID, 0),
            Err(OutboundFrameSequenceError::PathNotReady)
        );
        let mut machine = installed();
        assert_eq!(
            machine.reserve_outbound_control_frame(PC_TO_ANDROID, 6),
            Err(OutboundFrameSequenceError::InvalidEpoch)
        );
        assert_eq!(
            machine.reserve_outbound_control_frame(PC_TO_ANDROID, 8),
            Err(OutboundFrameSequenceError::InvalidEpoch)
        );
    }

    #[test]
    fn application_cursor_survives_transport_epoch_replacement() {
        let mut machine = installed();
        machine
            .update_applied_cursor(PC_TO_ANDROID, session(1), 42)
            .unwrap();
        assert_eq!(machine.applied_cursor(PC_TO_ANDROID, session(1)), 42);
        assert_eq!(
            machine
                .install_authenticated_path(
                    8,
                    AuthenticatedPathKind::NatTraversal,
                    DirectPathBinding::new([0xb6; 32]),
                )
                .outcome,
            EpochInstallOutcome::Installed
        );
        assert_eq!(machine.applied_cursor(PC_TO_ANDROID, session(1)), 42);
        assert_eq!(machine.applied_cursor(ANDROID_TO_PC, session(1)), 0);
    }

    #[test]
    fn application_cursor_identities_are_bounded_without_evicting_existing_state() {
        let mut machine = installed();
        let mut first = SessionId::new([0; 16]);
        for index in 0..MAX_APPLIED_CURSORS {
            let mut id = [0_u8; 16];
            id[..8].copy_from_slice(&(index as u64 + 1).to_le_bytes());
            let id = SessionId::new(id);
            if index == 0 {
                first = id;
            }
            assert_eq!(
                machine.update_applied_cursor(PC_TO_ANDROID, id, index as u64 + 1),
                Ok(None)
            );
        }

        let overflow = SessionId::new([0xff; 16]);
        assert_eq!(
            machine.update_applied_cursor(PC_TO_ANDROID, overflow, 1),
            Err(CursorUpdateError::Capacity)
        );
        assert_eq!(machine.applied_cursor(PC_TO_ANDROID, overflow), 0);
        assert_eq!(
            machine.update_applied_cursor(PC_TO_ANDROID, first, 999),
            Ok(None)
        );
        assert_eq!(machine.applied_cursor(PC_TO_ANDROID, first), 999);

        assert_eq!(
            machine
                .install_authenticated_path(
                    8,
                    AuthenticatedPathKind::NatTraversal,
                    DirectPathBinding::new([0xb6; 32]),
                )
                .outcome,
            EpochInstallOutcome::Installed
        );
        assert_eq!(
            machine.update_applied_cursor(PC_TO_ANDROID, overflow, 1),
            Err(CursorUpdateError::Capacity)
        );
        assert_eq!(machine.applied_cursor(PC_TO_ANDROID, first), 999);
    }

    #[test]
    fn directions_have_independent_barriers_and_state() {
        let mut machine = installed();
        let down = proposal(1, &[(1, 11)], 0);
        let up = proposal(2, &[(1, 71)], 0);
        machine.start_proposal(PC_TO_ANDROID, 7, down.clone(), 0, 100);
        machine.start_proposal(ANDROID_TO_PC, 7, up, 0, 100);

        assert_eq!(
            machine.state(PC_TO_ANDROID),
            DirectionState::ProposerAwaitingAccept
        );
        assert_eq!(
            machine.state(ANDROID_TO_PC),
            DirectionState::ProposerAwaitingAccept
        );
        machine.receive_accept(PC_TO_ANDROID, 7, down.barrier_id, down.barrier_sha256, true);
        assert_eq!(
            machine.state(PC_TO_ANDROID),
            DirectionState::ProposerAwaitingCommitEcho
        );
        assert_eq!(
            machine.state(ANDROID_TO_PC),
            DirectionState::ProposerAwaitingAccept
        );
    }

    #[test]
    fn responder_waits_for_every_session_prefix_before_accepting() {
        let mut machine = installed();
        let value = proposal(3, &[(1, 10), (2, 20)], 0);
        machine
            .update_applied_cursor(PC_TO_ANDROID, session(1), 9)
            .unwrap();
        let received = machine.receive_proposal(PC_TO_ANDROID, 7, value, 0, 100);
        assert_eq!(received.outcome, TransitionOutcome::DeferredForPrefix);
        assert_eq!(received.control, None);
        assert_eq!(machine.authoritative_carrier(PC_TO_ANDROID), Carrier::Relay);

        assert!(
            machine
                .update_applied_cursor(PC_TO_ANDROID, session(2), 18)
                .unwrap()
                .is_none()
        );
        let accepted = machine
            .update_applied_cursor(PC_TO_ANDROID, session(2), 19)
            .expect("cursor capacity")
            .expect("the final continuous prefix releases accept");
        assert!(matches!(
            accepted.control,
            Some(ControlMessage::Accept { accepted: true, .. })
        ));
        assert_eq!(
            machine.state(PC_TO_ANDROID),
            DirectionState::ResponderAwaitingCommit
        );
    }

    #[test]
    fn proposal_accept_commit_and_echo_keep_relay_until_complete() {
        let mut proposer = installed();
        let value = proposal(4, &[(1, 5)], 0);
        proposer.start_proposal(PC_TO_ANDROID, 7, value.clone(), 0, 100);
        assert_eq!(
            proposer.authoritative_carrier(PC_TO_ANDROID),
            Carrier::Relay
        );
        let held = Arc::<[u8]>::from(&b"held-envelope"[..]);
        let before_commit = proposer.route_outbound(PC_TO_ANDROID, envelope(1, 5, held.clone()), 1);
        assert_eq!(before_commit.transmissions.len(), 1);
        assert_eq!(before_commit.transmissions[0].carrier, Carrier::Relay);
        proposer.receive_accept(
            PC_TO_ANDROID,
            7,
            value.barrier_id,
            value.barrier_sha256,
            true,
        );
        assert_eq!(
            proposer.authoritative_carrier(PC_TO_ANDROID),
            Carrier::Relay
        );
        let active =
            proposer.receive_commit(PC_TO_ANDROID, 7, value.barrier_id, value.barrier_sha256, 2);
        assert_eq!(active.outcome, TransitionOutcome::Activated);
        assert_eq!(
            proposer.authoritative_carrier(PC_TO_ANDROID),
            Carrier::Direct(AuthenticatedPathKind::LocalAreaNetwork)
        );
        assert_eq!(active.routes.transmissions.len(), 1);
        assert!(Arc::ptr_eq(
            &held,
            &active.routes.transmissions[0].envelope.bytes
        ));

        let mut responder = installed();
        responder
            .update_applied_cursor(PC_TO_ANDROID, session(1), 4)
            .unwrap();
        responder.receive_proposal(PC_TO_ANDROID, 7, value.clone(), 0, 100);
        let commit =
            responder.receive_commit(PC_TO_ANDROID, 7, value.barrier_id, value.barrier_sha256, 1);
        assert!(matches!(
            commit.control,
            Some(ControlMessage::Commit { is_echo: true, .. })
        ));
        assert_eq!(
            responder.authoritative_carrier(PC_TO_ANDROID),
            Carrier::Relay
        );
        responder.commit_echo_sent(PC_TO_ANDROID, 7, value.barrier_id, 2);
        assert!(matches!(
            responder.authoritative_carrier(PC_TO_ANDROID),
            Carrier::Direct(_)
        ));
    }

    #[test]
    fn envelope_count_limit_is_inclusive_then_fails_closed_to_relay() {
        let mut machine = installed();
        let value = proposal(5, &[(1, 1)], 0);
        machine.start_proposal(PC_TO_ANDROID, 7, value, 0, 100);
        for sequence in 1..=MAX_BUFFERED_ENVELOPES as u64 {
            let result = machine.route_outbound(
                PC_TO_ANDROID,
                envelope(1, sequence, Arc::from([sequence as u8])),
                0,
            );
            assert_eq!(result.transmissions.len(), 1);
            assert_eq!(result.transmissions[0].carrier, Carrier::Relay);
        }
        assert_eq!(
            machine.buffered_envelopes(PC_TO_ANDROID),
            MAX_BUFFERED_ENVELOPES
        );
        let overflow = machine.route_outbound(
            PC_TO_ANDROID,
            envelope(1, MAX_BUFFERED_ENVELOPES as u64 + 1, Arc::from([0xff])),
            0,
        );
        assert_eq!(
            overflow.fallback_reason,
            Some(FallbackReason::BufferEnvelopeLimit)
        );
        assert_eq!(overflow.transmissions.len(), 1);
        assert!(
            overflow
                .transmissions
                .iter()
                .all(|item| item.carrier == Carrier::Relay)
        );
        assert_eq!(machine.state(PC_TO_ANDROID), DirectionState::RelayOnly);
    }

    #[test]
    fn byte_limit_is_inclusive_then_fails_closed_without_dropping_bytes() {
        let mut machine = installed();
        machine.start_proposal(PC_TO_ANDROID, 7, proposal(6, &[(1, 1)], 0), 0, 100);
        let exact = Arc::<[u8]>::from(vec![0x5a; MAX_BUFFERED_BYTES]);
        let held = machine.route_outbound(PC_TO_ANDROID, envelope(1, 1, exact.clone()), 0);
        assert_eq!(held.transmissions.len(), 1);
        assert_eq!(held.transmissions[0].carrier, Carrier::Relay);
        assert_eq!(machine.buffered_bytes(PC_TO_ANDROID), MAX_BUFFERED_BYTES);
        let extra = Arc::<[u8]>::from([0xa5]);
        let overflow = machine.route_outbound(PC_TO_ANDROID, envelope(1, 2, extra.clone()), 0);
        assert_eq!(
            overflow.fallback_reason,
            Some(FallbackReason::BufferByteLimit)
        );
        assert_eq!(overflow.transmissions.len(), 1);
        assert!(Arc::ptr_eq(
            &extra,
            &overflow.transmissions[0].envelope.bytes
        ));
    }

    #[test]
    fn duplicate_grace_sends_the_same_arc_over_relay_and_direct() {
        let mut machine = installed();
        let value = proposal(7, &[(1, 1)], 500);
        activate_proposer(&mut machine, PC_TO_ANDROID, &value, 100);
        let bytes = Arc::<[u8]>::from(&b"same bytes, no serialization"[..]);
        let routed = machine.route_outbound(PC_TO_ANDROID, envelope(1, 1, bytes.clone()), 599);
        assert_eq!(routed.transmissions.len(), 2);
        assert_eq!(routed.transmissions[0].carrier, Carrier::Relay);
        assert!(matches!(
            routed.transmissions[1].carrier,
            Carrier::Direct(_)
        ));
        assert!(Arc::ptr_eq(
            &routed.transmissions[0].envelope.bytes,
            &routed.transmissions[1].envelope.bytes
        ));
        assert!(Arc::ptr_eq(&bytes, &routed.transmissions[1].envelope.bytes));
    }

    #[test]
    fn direct_failure_replays_unreceipted_exact_bytes_in_original_order() {
        let mut machine = installed();
        let value = proposal(8, &[(1, 1)], 0);
        activate_proposer(&mut machine, PC_TO_ANDROID, &value, 0);
        let one = Arc::<[u8]>::from(&b"\0raw-one\xff"[..]);
        let two = Arc::<[u8]>::from(&b"raw-two\0"[..]);
        machine.route_outbound(PC_TO_ANDROID, envelope(1, 1, one.clone()), 1);
        machine.route_outbound(PC_TO_ANDROID, envelope(1, 2, two.clone()), 1);

        let fallback = machine.direct_failed(7);
        assert_eq!(
            fallback.fallback_reason,
            Some(FallbackReason::DirectFailure)
        );
        assert_eq!(fallback.transmissions.len(), 2);
        assert!(Arc::ptr_eq(&one, &fallback.transmissions[0].envelope.bytes));
        assert!(Arc::ptr_eq(&two, &fallback.transmissions[1].envelope.bytes));
        assert_eq!(machine.state(PC_TO_ANDROID), DirectionState::RelayOnly);
    }

    #[test]
    fn direct_envelope_before_cutover_is_rejected_without_advancing_sequence() {
        let mut machine = installed();
        assert_eq!(
            machine.observe_inbound_direct_frame(
                PC_TO_ANDROID,
                7,
                FIRST_DIRECT_DATA_FRAME_SEQ,
                InboundDirectFrame::Envelope,
            ),
            InboundFrameResult::Rejected(DirectFrameReject::DirectionNotCommitted)
        );
        assert_eq!(
            machine.highest_received_direct_frame_seq(PC_TO_ANDROID),
            FIRST_DIRECT_DATA_FRAME_SEQ - 1
        );

        let value = proposal(12, &[(1, 1)], 0);
        machine.start_proposal(PC_TO_ANDROID, 7, value, 0, 1_000);
        assert_eq!(
            machine.observe_inbound_direct_frame(
                PC_TO_ANDROID,
                7,
                FIRST_DIRECT_DATA_FRAME_SEQ,
                InboundDirectFrame::Envelope,
            ),
            InboundFrameResult::Rejected(DirectFrameReject::DirectionNotCommitted)
        );
        assert_eq!(
            machine.highest_received_direct_frame_seq(PC_TO_ANDROID),
            FIRST_DIRECT_DATA_FRAME_SEQ - 1
        );
    }

    #[test]
    fn late_direct_envelope_after_direction_fallback_is_consumed_without_delivery() {
        let mut machine = installed();
        let value = proposal(12, &[(1, 1)], 0);
        activate_proposer(&mut machine, PC_TO_ANDROID, &value, 0);
        let routed =
            machine.route_outbound(PC_TO_ANDROID, envelope(1, 1, Arc::from(&b"late"[..])), 1);
        let frame_seq = routed.transmissions[0]
            .direct_frame_seq
            .expect("direct frame");
        let fallback = machine.receive_fallback(PC_TO_ANDROID, 7, Some(value.barrier_id));
        assert!(matches!(fallback.outcome, TransitionOutcome::Applied));
        assert_eq!(
            machine.observe_inbound_direct_frame(
                PC_TO_ANDROID,
                7,
                frame_seq,
                InboundDirectFrame::Envelope,
            ),
            InboundFrameResult::NoReceipt {
                applied_transport_receipt: None
            }
        );
        for offset in 1..MAX_BUFFERED_ENVELOPES as u64 {
            assert_eq!(
                machine.observe_inbound_direct_frame(
                    PC_TO_ANDROID,
                    7,
                    frame_seq + offset,
                    InboundDirectFrame::Envelope,
                ),
                InboundFrameResult::NoReceipt {
                    applied_transport_receipt: None
                }
            );
        }
        assert_eq!(
            machine.observe_inbound_direct_frame(
                PC_TO_ANDROID,
                7,
                frame_seq + MAX_BUFFERED_ENVELOPES as u64,
                InboundDirectFrame::Envelope,
            ),
            InboundFrameResult::Rejected(DirectFrameReject::DirectionNotCommitted)
        );
        assert_eq!(
            machine.highest_received_direct_frame_seq(PC_TO_ANDROID),
            frame_seq + MAX_BUFFERED_ENVELOPES as u64 - 1
        );
        assert_eq!(machine.authoritative_carrier(PC_TO_ANDROID), Carrier::Relay);
        assert_eq!(machine.authoritative_carrier(ANDROID_TO_PC), Carrier::Relay);
    }

    #[test]
    fn transport_receipt_releases_only_direct_copy_and_never_changes_app_cursor() {
        let mut machine = installed();
        let value = proposal(9, &[(1, 1)], 0);
        activate_proposer(&mut machine, PC_TO_ANDROID, &value, 0);
        let sent =
            machine.route_outbound(PC_TO_ANDROID, envelope(1, 1, Arc::from(&b"payload"[..])), 1);
        let frame_seq = sent.transmissions[0].direct_frame_seq.unwrap();
        assert_eq!(frame_seq, FIRST_DIRECT_DATA_FRAME_SEQ);
        assert_eq!(machine.applied_cursor(PC_TO_ANDROID, session(1)), 0);
        assert_eq!(
            machine.apply_transport_receipt(
                PC_TO_ANDROID,
                7,
                TransportReceiptV1 {
                    highest_contiguous_frame_seq: frame_seq
                },
            ),
            ReceiptApplyOutcome::Applied {
                released_envelopes: 1,
                released_bytes: b"payload".len(),
            }
        );
        assert_eq!(machine.applied_cursor(PC_TO_ANDROID, session(1)), 0);
        assert_eq!(machine.buffered_envelopes(PC_TO_ANDROID), 0);
        assert!(machine.direct_failed(7).transmissions.is_empty());
    }

    #[test]
    fn stale_epoch_bad_hash_and_late_commit_never_advance_epoch() {
        let mut machine = installed();
        let value = proposal(10, &[(1, 1)], 0);
        let stale = machine.receive_proposal(PC_TO_ANDROID, 6, value.clone(), 0, 100);
        assert!(matches!(
            stale.outcome,
            TransitionOutcome::IgnoredStale { .. }
        ));
        assert_eq!(machine.path_epoch(), 7);

        machine.start_proposal(PC_TO_ANDROID, 7, value.clone(), 0, 100);
        let wrong_hash =
            machine.receive_accept(PC_TO_ANDROID, 7, value.barrier_id, [0xee; 32], true);
        assert_eq!(
            wrong_hash.outcome,
            TransitionOutcome::Rejected(RejectReason::BarrierHashMismatch)
        );
        assert_eq!(machine.path_epoch(), 7);
        assert_eq!(machine.state(PC_TO_ANDROID), DirectionState::RelayOnly);

        let late =
            machine.receive_commit(PC_TO_ANDROID, 7, value.barrier_id, value.barrier_sha256, 5);
        assert_eq!(
            late.outcome,
            TransitionOutcome::Rejected(RejectReason::LateCommit)
        );
        assert_eq!(machine.path_epoch(), 7);
    }

    #[test]
    fn v1_receipts_have_no_missing_ranges_and_never_get_receipted() {
        assert_eq!(
            TransportReceiptV1::from_wire(0, &[]),
            Err(ReceiptValidationError::InvalidSequence)
        );
        assert_eq!(
            TransportReceiptV1::from_wire(i64::MAX as u64 + 1, &[]),
            Err(ReceiptValidationError::InvalidSequence)
        );
        assert_eq!(
            TransportReceiptV1::from_wire(9, &[(4, 5)]),
            Err(ReceiptValidationError::MissingRangesForbidden)
        );
        let receipt = TransportReceiptV1::from_wire(9, &[]).unwrap();
        assert!(receipt.missing_ranges().is_empty());

        let mut machine = installed();
        let result = machine.observe_inbound_direct_frame(
            PC_TO_ANDROID,
            7,
            FIRST_DIRECT_DATA_FRAME_SEQ,
            InboundDirectFrame::TransportReceipt(receipt),
        );
        assert!(matches!(
            result,
            InboundFrameResult::NoReceipt {
                applied_transport_receipt: Some(_)
            }
        ));
    }

    #[test]
    fn proposal_shape_path_binding_and_timeout_are_fail_closed() {
        fn rejected(machine: &mut PeerRouteMachine, value: CutoverProposal) -> RejectReason {
            match machine
                .start_proposal(PC_TO_ANDROID, 7, value, 0, 100)
                .outcome
            {
                TransitionOutcome::Rejected(reason) => reason,
                other => panic!("invalid proposal was not rejected: {other:?}"),
            }
        }

        let mut machine = installed();
        let mut value = proposal(12, &[(1, 1)], 0);
        value.barrier_id = BarrierId::new([0; 16]);
        assert_eq!(rejected(&mut machine, value), RejectReason::InvalidProposal);

        let mut value = proposal(12, &[(1, 1)], 0);
        value.barrier_sha256 = [0; 32];
        assert_eq!(rejected(&mut machine, value), RejectReason::InvalidProposal);

        let mut value = proposal(12, &[(1, 1)], 0);
        value.direct_path_binding = DirectPathBinding::new([0xb6; 32]);
        assert_eq!(
            rejected(&mut machine, value),
            RejectReason::PathBindingMismatch
        );

        let unsorted = proposal(12, &[(2, 1), (1, 1)], 0);
        assert_eq!(
            rejected(&mut machine, unsorted),
            RejectReason::InvalidProposal
        );

        let mut zero_session = proposal(12, &[(1, 1)], 0);
        zero_session.session_barriers[0].session_id = SessionId::new([0; 16]);
        assert_eq!(
            rejected(&mut machine, zero_session),
            RejectReason::InvalidProposal
        );

        let too_large_sequence = proposal(12, &[(1, i64::MAX as u64 + 1)], 0);
        assert_eq!(
            rejected(&mut machine, too_large_sequence),
            RejectReason::InvalidProposal
        );

        let too_many = proposal(
            12,
            &(1..=MAX_SESSION_BARRIERS as u8 + 1)
                .map(|id| (id, 1))
                .collect::<Vec<_>>(),
            0,
        );
        assert_eq!(
            rejected(&mut machine, too_many),
            RejectReason::InvalidProposal
        );

        let too_much_grace = proposal(12, &[(1, 1)], MAX_DUPLICATE_GRACE_MS + 1);
        assert_eq!(
            rejected(&mut machine, too_much_grace),
            RejectReason::InvalidProposal
        );

        let zero_timeout =
            machine.start_proposal(PC_TO_ANDROID, 7, proposal(12, &[(1, 1)], 0), 0, 0);
        assert_eq!(
            zero_timeout.outcome,
            TransitionOutcome::Rejected(RejectReason::InvalidTimeout)
        );
        assert_eq!(machine.state(PC_TO_ANDROID), DirectionState::RelayOnly);
    }

    #[test]
    fn prefix_timeout_rejects_and_keeps_relay_authoritative() {
        let mut machine = installed();
        let value = proposal(13, &[(1, 10)], 0);
        let pending = machine.receive_proposal(PC_TO_ANDROID, 7, value, 100, 50);
        assert_eq!(pending.outcome, TransitionOutcome::DeferredForPrefix);
        assert!(machine.poll_timeout(PC_TO_ANDROID, 149).is_none());
        let timeout = machine
            .poll_timeout(PC_TO_ANDROID, 150)
            .expect("deadline must abort the proposal");
        assert!(matches!(
            timeout.control,
            Some(ControlMessage::Accept {
                accepted: false,
                reason: Some(RejectReason::PrefixTimeout),
                ..
            })
        ));
        assert_eq!(machine.state(PC_TO_ANDROID), DirectionState::RelayOnly);
        assert_eq!(machine.authoritative_carrier(PC_TO_ANDROID), Carrier::Relay);
    }

    #[test]
    fn invalid_envelope_metadata_never_enters_direct_or_loses_exact_bytes() {
        let mut machine = installed();
        let value = proposal(14, &[(1, 1)], 0);
        activate_proposer(&mut machine, PC_TO_ANDROID, &value, 0);
        let bytes = Arc::<[u8]>::from(&b"invalid sequence retained"[..]);
        let routed = machine.route_outbound(PC_TO_ANDROID, envelope(1, 0, bytes.clone()), 1);
        assert_eq!(
            routed.fallback_reason,
            Some(FallbackReason::InvalidEnvelope)
        );
        assert_eq!(routed.transmissions.len(), 1);
        assert_eq!(routed.transmissions[0].carrier, Carrier::Relay);
        assert!(Arc::ptr_eq(&bytes, &routed.transmissions[0].envelope.bytes));
        assert_eq!(machine.state(PC_TO_ANDROID), DirectionState::RelayOnly);

        let empty = machine.route_outbound(
            PC_TO_ANDROID,
            OpaqueEnvelope::new(SessionId::new([0; 16]), 1, Arc::<[u8]>::from([])),
            2,
        );
        assert_eq!(empty.fallback_reason, Some(FallbackReason::InvalidEnvelope));
        assert_eq!(empty.transmissions.len(), 1);
        assert_eq!(empty.transmissions[0].carrier, Carrier::Relay);
    }

    #[test]
    fn session_specific_barriers_route_prefix_and_uncommitted_sessions_via_relay() {
        let mut machine = installed();
        let value = proposal(11, &[(1, 10), (2, 20)], 0);
        activate_proposer(&mut machine, PC_TO_ANDROID, &value, 0);

        let before =
            machine.route_outbound(PC_TO_ANDROID, envelope(1, 9, Arc::from(&b"before"[..])), 1);
        let at_first =
            machine.route_outbound(PC_TO_ANDROID, envelope(1, 10, Arc::from(&b"direct"[..])), 1);
        let other_before = machine.route_outbound(
            PC_TO_ANDROID,
            envelope(2, 19, Arc::from(&b"other-before"[..])),
            1,
        );
        let unlisted = machine.route_outbound(
            PC_TO_ANDROID,
            envelope(3, 99, Arc::from(&b"unlisted"[..])),
            1,
        );

        assert_eq!(before.transmissions[0].carrier, Carrier::Relay);
        assert!(matches!(
            at_first.transmissions[0].carrier,
            Carrier::Direct(_)
        ));
        assert_eq!(other_before.transmissions[0].carrier, Carrier::Relay);
        assert_eq!(unlisted.transmissions[0].carrier, Carrier::Relay);
    }

    #[test]
    fn incremental_cutover_keeps_existing_session_direct_until_new_session_commits() {
        let mut machine = installed();
        let first = proposal(20, &[(1, 1)], 0);
        activate_proposer(&mut machine, PC_TO_ANDROID, &first, 0);
        assert_eq!(
            machine.session_state(PC_TO_ANDROID, session(1)),
            SessionRouteState::DirectActive
        );

        let late = proposal(21, &[(2, 5)], 50);
        assert_eq!(
            machine
                .start_proposal(PC_TO_ANDROID, 7, late.clone(), 10, 100)
                .outcome,
            TransitionOutcome::Applied
        );
        assert_eq!(machine.state(PC_TO_ANDROID), DirectionState::DirectActive);
        assert_eq!(
            machine.session_state(PC_TO_ANDROID, session(2)),
            SessionRouteState::Negotiating
        );

        let existing = machine.route_outbound(
            PC_TO_ANDROID,
            envelope(1, 1, Arc::from(&b"existing"[..])),
            11,
        );
        assert!(matches!(
            existing.transmissions[0].carrier,
            Carrier::Direct(_)
        ));
        let pending = machine.route_outbound(
            PC_TO_ANDROID,
            envelope(2, 5, Arc::from(&b"pending"[..])),
            11,
        );
        assert_eq!(pending.transmissions[0].carrier, Carrier::Relay);

        machine.receive_accept(PC_TO_ANDROID, 7, late.barrier_id, late.barrier_sha256, true);
        let activated =
            machine.receive_commit(PC_TO_ANDROID, 7, late.barrier_id, late.barrier_sha256, 12);
        assert_eq!(activated.outcome, TransitionOutcome::Activated);
        assert_eq!(activated.routes.transmissions.len(), 1);
        assert!(matches!(
            activated.routes.transmissions[0].carrier,
            Carrier::Direct(_)
        ));
        assert_eq!(
            machine.session_state(PC_TO_ANDROID, session(1)),
            SessionRouteState::DirectActive
        );
        assert_eq!(
            machine.session_state(PC_TO_ANDROID, session(2)),
            SessionRouteState::DirectActive
        );
    }

    #[test]
    fn incremental_timeout_leaves_existing_direct_session_untouched() {
        let mut machine = installed();
        let first = proposal(22, &[(1, 1)], 0);
        activate_proposer(&mut machine, PC_TO_ANDROID, &first, 0);
        let existing = machine.route_outbound(
            PC_TO_ANDROID,
            envelope(1, 1, Arc::from(&b"existing"[..])),
            1,
        );
        let existing_frame = existing.transmissions[0].direct_frame_seq.unwrap();

        let late = proposal(23, &[(2, 1)], 0);
        machine.start_proposal(PC_TO_ANDROID, 7, late, 10, 5);
        machine.route_outbound(PC_TO_ANDROID, envelope(2, 1, Arc::from(&b"late"[..])), 11);
        let timeout = machine
            .poll_timeout(PC_TO_ANDROID, 15)
            .expect("incremental proposal must expire");
        assert_eq!(
            timeout.routes.fallback_reason,
            Some(FallbackReason::NegotiationTimeout)
        );
        assert!(timeout.routes.transmissions.is_empty());
        assert_eq!(machine.state(PC_TO_ANDROID), DirectionState::DirectActive);
        assert_eq!(
            machine.session_state(PC_TO_ANDROID, session(1)),
            SessionRouteState::DirectActive
        );
        assert_eq!(
            machine.session_state(PC_TO_ANDROID, session(2)),
            SessionRouteState::RelayOnly
        );
        assert_eq!(machine.buffered_envelopes(PC_TO_ANDROID), 1);
        assert_eq!(
            machine.apply_transport_receipt(
                PC_TO_ANDROID,
                7,
                TransportReceiptV1::new(existing_frame).unwrap(),
            ),
            ReceiptApplyOutcome::Applied {
                released_envelopes: 1,
                released_bytes: b"existing".len(),
            }
        );
    }

    #[test]
    fn fallback_for_any_committed_batch_retires_the_direction_but_not_the_path() {
        let mut machine = installed();
        let first = proposal(24, &[(1, 1)], 0);
        activate_proposer(&mut machine, PC_TO_ANDROID, &first, 0);
        let second = proposal(25, &[(2, 1)], 0);
        activate_proposer(&mut machine, PC_TO_ANDROID, &second, 1);
        machine.route_outbound(PC_TO_ANDROID, envelope(1, 1, Arc::from(&b"first"[..])), 2);
        machine.route_outbound(PC_TO_ANDROID, envelope(2, 1, Arc::from(&b"second"[..])), 2);

        let fallback = machine.receive_fallback(PC_TO_ANDROID, 7, Some(second.barrier_id));
        assert_eq!(fallback.routes.transmissions.len(), 2);
        assert_eq!(
            machine.session_state(PC_TO_ANDROID, session(1)),
            SessionRouteState::RelayOnly
        );
        assert_eq!(
            machine.session_state(PC_TO_ANDROID, session(2)),
            SessionRouteState::RelayOnly
        );
        assert_eq!(machine.state(PC_TO_ANDROID), DirectionState::RelayOnly);
    }

    #[test]
    fn physical_direction_failure_notifies_peer_with_full_direction_fallback() {
        let mut local = installed();
        let mut peer = installed();
        let first = proposal(27, &[(1, 1)], 0);
        let second = proposal(28, &[(2, 1)], 0);
        for machine in [&mut local, &mut peer] {
            activate_proposer(machine, PC_TO_ANDROID, &first, 0);
            activate_proposer(machine, PC_TO_ANDROID, &second, 1);
        }

        let failed = local.direction_failed(PC_TO_ANDROID, 7);
        assert_eq!(failed.controls.len(), 1);
        let ControlMessage::Fallback {
            direction,
            path_epoch,
            barrier_id,
            reason,
        } = failed.controls[0]
        else {
            panic!("direction failure must emit fallback control");
        };
        assert_eq!(direction, PC_TO_ANDROID);
        assert_eq!(path_epoch, 7);
        assert_eq!(barrier_id, None);
        assert_eq!(reason, FallbackReason::DirectFailure);

        let received = peer.receive_fallback(direction, path_epoch, barrier_id);
        assert_eq!(received.outcome, TransitionOutcome::Applied);
        assert_eq!(
            peer.session_state(PC_TO_ANDROID, session(1)),
            SessionRouteState::RelayOnly
        );
        assert_eq!(
            peer.session_state(PC_TO_ANDROID, session(2)),
            SessionRouteState::RelayOnly
        );
        assert_eq!(peer.state(PC_TO_ANDROID), DirectionState::RelayOnly);
    }

    #[test]
    fn committed_barrier_id_cannot_be_reused_for_an_incremental_batch() {
        let mut proposer = installed();
        let first = proposal(29, &[(1, 1)], 0);
        activate_proposer(&mut proposer, PC_TO_ANDROID, &first, 0);
        let reused = CutoverProposal {
            session_barriers: vec![SessionBarrier {
                session_id: session(2),
                first_direct_seq: 1,
            }],
            ..first.clone()
        };
        assert_eq!(
            proposer
                .start_proposal(PC_TO_ANDROID, 7, reused.clone(), 1, 100)
                .outcome,
            TransitionOutcome::Rejected(RejectReason::DirectionBusy)
        );

        let mut responder = installed();
        activate_proposer(&mut responder, PC_TO_ANDROID, &first, 0);
        assert_eq!(
            responder
                .receive_proposal(PC_TO_ANDROID, 7, reused, 1, 100)
                .outcome,
            TransitionOutcome::Rejected(RejectReason::DirectionBusy)
        );
    }

    #[test]
    fn deregister_replays_only_that_sessions_unreceipted_bytes_and_keeps_peer_path() {
        let mut machine = installed();
        let value = proposal(30, &[(1, 1), (2, 1)], 0);
        activate_proposer(&mut machine, PC_TO_ANDROID, &value, 0);
        machine
            .update_applied_cursor(PC_TO_ANDROID, session(1), 7)
            .unwrap();
        let removed_bytes = Arc::<[u8]>::from(&b"removed"[..]);
        let retained_bytes = Arc::<[u8]>::from(&b"retained"[..]);
        machine.route_outbound(PC_TO_ANDROID, envelope(1, 8, removed_bytes.clone()), 1);
        machine.route_outbound(PC_TO_ANDROID, envelope(2, 1, retained_bytes), 1);

        let deregistered = machine.deregister_session(PC_TO_ANDROID, session(1));
        assert_eq!(deregistered.transmissions.len(), 1);
        assert_eq!(deregistered.transmissions[0].carrier, Carrier::Relay);
        assert!(Arc::ptr_eq(
            &deregistered.transmissions[0].envelope.bytes,
            &removed_bytes
        ));
        assert_eq!(machine.applied_cursor(PC_TO_ANDROID, session(1)), 0);
        assert!(machine.is_retired_direct_session(PC_TO_ANDROID, session(1)));
        assert_eq!(
            machine.session_state(PC_TO_ANDROID, session(1)),
            SessionRouteState::RelayOnly
        );
        assert_eq!(
            machine.session_state(PC_TO_ANDROID, session(2)),
            SessionRouteState::DirectActive
        );
        assert_eq!(machine.buffered_envelopes(PC_TO_ANDROID), 1);
        assert_eq!(machine.state(PC_TO_ANDROID), DirectionState::DirectActive);
    }

    #[test]
    fn deregister_from_incremental_batch_aborts_only_that_uncommitted_batch() {
        let mut machine = installed();
        let existing = proposal(31, &[(1, 1)], 0);
        activate_proposer(&mut machine, PC_TO_ANDROID, &existing, 0);
        let late = proposal(32, &[(2, 1), (3, 1)], 0);
        assert_eq!(
            machine
                .start_proposal(PC_TO_ANDROID, 7, late.clone(), 1, 100)
                .outcome,
            TransitionOutcome::Applied
        );

        let deregistered = machine.deregister_session(PC_TO_ANDROID, session(2));
        assert!(matches!(
            deregistered.controls.as_slice(),
            [ControlMessage::Fallback {
                barrier_id: Some(value),
                ..
            }] if *value == late.barrier_id
        ));
        assert_eq!(
            machine.session_state(PC_TO_ANDROID, session(1)),
            SessionRouteState::DirectActive
        );
        assert_eq!(
            machine.session_state(PC_TO_ANDROID, session(2)),
            SessionRouteState::RelayOnly
        );
        assert_eq!(
            machine.session_state(PC_TO_ANDROID, session(3)),
            SessionRouteState::RelayOnly
        );
        assert_eq!(machine.state(PC_TO_ANDROID), DirectionState::DirectActive);
    }

    #[test]
    fn last_session_deregister_consumes_late_frame_and_path_accepts_a_new_batch() {
        let mut machine = installed();
        let first = proposal(33, &[(1, 1)], 0);
        activate_proposer(&mut machine, PC_TO_ANDROID, &first, 0);
        let routed =
            machine.route_outbound(PC_TO_ANDROID, envelope(1, 1, Arc::from(&b"late"[..])), 1);
        let frame_seq = routed.transmissions[0].direct_frame_seq.unwrap();
        machine.deregister_session(PC_TO_ANDROID, session(1));
        assert_eq!(machine.state(PC_TO_ANDROID), DirectionState::RelayOnly);
        assert_eq!(
            machine.observe_inbound_direct_frame(
                PC_TO_ANDROID,
                7,
                frame_seq,
                InboundDirectFrame::Envelope,
            ),
            InboundFrameResult::NoReceipt {
                applied_transport_receipt: None,
            }
        );

        let reopened = proposal(34, &[(1, 2)], 0);
        assert_eq!(
            machine
                .start_proposal(PC_TO_ANDROID, 7, reopened, 2, 100)
                .outcome,
            TransitionOutcome::Applied
        );
        assert!(!machine.is_retired_direct_session(PC_TO_ANDROID, session(1)));
    }

    #[test]
    fn direct_ingress_is_session_and_sequence_fenced_with_native_grace() {
        let mut machine = installed();
        let value = proposal(26, &[(1, 10)], 50);
        activate_proposer(&mut machine, PC_TO_ANDROID, &value, 100);
        assert_eq!(
            machine.direct_ingress_grace_remaining_ms(PC_TO_ANDROID, session(1), 9, 110),
            None
        );
        assert_eq!(
            machine.direct_ingress_grace_remaining_ms(PC_TO_ANDROID, session(2), 10, 110),
            None
        );
        assert_eq!(
            machine.direct_ingress_grace_remaining_ms(PC_TO_ANDROID, session(1), 10, 110),
            Some(40)
        );
        assert_eq!(
            machine.direct_ingress_grace_remaining_ms(PC_TO_ANDROID, session(1), 11, 151),
            Some(0)
        );
    }
}
