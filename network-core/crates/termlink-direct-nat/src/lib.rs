use std::{
    fmt, io,
    net::{IpAddr, Ipv4Addr, Ipv6Addr, SocketAddr, UdpSocket},
    time::{Duration, Instant},
};

use ring::rand::{SecureRandom, SystemRandom};

pub const STUN_HEADER_BYTES: usize = 20;
pub const STUN_MAGIC_COOKIE: u32 = 0x2112_A442;
pub const MAX_STUN_PACKET_BYTES: usize = 512;
pub const MAX_PROBE_PACKET_BYTES: usize = 512;
pub const MAX_SOCKET_PACKET_BYTES: usize = 512;
pub const MIN_CANDIDATE_PORT: u16 = 1024;
pub const MAX_PROBE_ATTEMPTS_PER_DIRECTION: usize = 4;
pub const MAX_PAIR_PROBE_BUDGET: Duration = Duration::from_millis(1_500);
pub const MAX_STUN_TIMEOUT: Duration = Duration::from_secs(5);
pub const MAX_STUN_SERVERS: usize = 2;
pub const LAN_FAST_PROBE_BUDGET: Duration = Duration::from_millis(700);
pub const PROBE_RETRY_INTERVAL: Duration = Duration::from_millis(150);
pub const SOCKET_PUMP_SLICE: Duration = Duration::from_millis(50);
pub const MAX_MULTIPAIR_CANDIDATE_PAIRS: usize = 32;
pub const MAX_ACTIVE_CANDIDATE_PAIRS: usize = 4;
pub const MAX_MULTIPAIR_CHECK_BUDGET: Duration = Duration::from_secs(10);

/// An opaque, caller-assigned identity for a local/remote candidate pair.
///
/// The NAT crate deliberately does not know candidate IDs or any TLD wire
/// layout. The wire/FFI layers may derive this token from their canonical pair
/// identity and must keep it stable for the lifetime of a check plan.
#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub struct PairToken([u8; 16]);

impl PairToken {
    pub const fn new(bytes: [u8; 16]) -> Self {
        Self(bytes)
    }

    pub const fn into_bytes(self) -> [u8; 16] {
        self.0
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MultipairCheckPair {
    pub token: PairToken,
    pub peer: SocketAddr,
    /// A complete, already-authenticated wire packet. The pump retransmits the
    /// exact bytes and never parses or rewrites them.
    pub probe_packet: Vec<u8>,
}

/// Authentication result returned by the wire layer for one received packet.
///
/// A peer probe proves that traffic from the peer reached this socket. An
/// acknowledgement proves that this socket's own probe reached the peer. The
/// pair is validated only after both independently authenticated observations.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum AuthenticatedMultipairPacket {
    Probe {
        token: PairToken,
        acknowledgement: Vec<u8>,
    },
    Acknowledgement {
        token: PairToken,
    },
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum MultipairPumpEvent {
    ServerReflexiveCandidate(SocketAddr),
    PairValidated {
        token: PairToken,
        peer: SocketAddr,
        round_trip_time: Duration,
        probe_attempts: usize,
    },
    ChecksComplete,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct HostCandidate {
    pub endpoint: SocketAddr,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DiscoveryProbeOutcome {
    pub server_reflexive_candidates: Vec<SocketAddr>,
    pub authenticated_peer_packet: Option<Vec<u8>>,
}

/// Result of authenticating one datagram in the shared STUN/probe pump.
/// `path_confirmed` must only become true after the caller has observed both
/// an authenticated peer probe and an authenticated acknowledgement for its
/// own probe. This prevents one peer from handing the socket to QUIC while the
/// other peer is still in the nomination phase.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct AuthenticatedProbePacket {
    pub reply: Vec<u8>,
    pub path_confirmed: bool,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct StunTransaction {
    id: [u8; 12],
}

impl StunTransaction {
    pub fn generate() -> Result<Self, NatError> {
        let mut id = [0_u8; 12];
        SystemRandom::new()
            .fill(&mut id)
            .map_err(|_| NatError::RandomUnavailable)?;
        Ok(Self { id })
    }

    pub fn from_id(id: [u8; 12]) -> Self {
        Self { id }
    }

    pub fn id(&self) -> [u8; 12] {
        self.id
    }

    pub fn binding_request(&self) -> [u8; STUN_HEADER_BYTES] {
        let mut request = [0_u8; STUN_HEADER_BYTES];
        request[..2].copy_from_slice(&0x0001_u16.to_be_bytes());
        request[2..4].copy_from_slice(&0_u16.to_be_bytes());
        request[4..8].copy_from_slice(&STUN_MAGIC_COOKIE.to_be_bytes());
        request[8..20].copy_from_slice(&self.id);
        request
    }

    pub fn parse_binding_response(
        &self,
        packet: &[u8],
        actual_source: SocketAddr,
        expected_source: SocketAddr,
    ) -> Result<SocketAddr, NatError> {
        if packet.len() < STUN_HEADER_BYTES || packet.len() > MAX_STUN_PACKET_BYTES {
            return Err(NatError::InvalidStun);
        }
        if actual_source != expected_source
            || u16::from_be_bytes(packet[0..2].try_into().unwrap()) != 0x0101
            || u32::from_be_bytes(packet[4..8].try_into().unwrap()) != STUN_MAGIC_COOKIE
            || packet[8..20] != self.id
        {
            return Err(NatError::StunContextMismatch);
        }
        let attributes_len = usize::from(u16::from_be_bytes(packet[2..4].try_into().unwrap()));
        if attributes_len % 4 != 0 || attributes_len + STUN_HEADER_BYTES != packet.len() {
            return Err(NatError::InvalidStun);
        }
        let mut offset = STUN_HEADER_BYTES;
        let mut mapped = None;
        while offset < packet.len() {
            let kind = u16::from_be_bytes(packet[offset..offset + 2].try_into().unwrap());
            let length = usize::from(u16::from_be_bytes(
                packet[offset + 2..offset + 4].try_into().unwrap(),
            ));
            offset += 4;
            let end = offset.checked_add(length).ok_or(NatError::InvalidStun)?;
            let value = packet.get(offset..end).ok_or(NatError::InvalidStun)?;
            if kind == 0x0020 {
                if mapped.is_some() {
                    return Err(NatError::InvalidStun);
                }
                mapped = Some(parse_xor_mapped(value, &self.id)?);
            }
            let padded = (length + 3) & !3;
            offset = offset.checked_add(padded).ok_or(NatError::InvalidStun)?;
            if offset > packet.len() {
                return Err(NatError::InvalidStun);
            }
        }
        let mapped = mapped.ok_or(NatError::InvalidStun)?;
        validate_candidate_endpoint(mapped)?;
        Ok(mapped)
    }
}

#[derive(Debug)]
pub struct DirectUdpSocket {
    socket: UdpSocket,
    bound_addr: SocketAddr,
}

impl DirectUdpSocket {
    pub fn bind(address: SocketAddr) -> Result<Self, NatError> {
        #[cfg(target_os = "android")]
        {
            let _ = address;
            Err(NatError::PlatformBinderRequired)
        }
        #[cfg(not(target_os = "android"))]
        {
            Self::bind_with_platform(address, &NoopPlatformSocketBinder)
        }
    }

    pub fn bind_with_platform(
        address: SocketAddr,
        binder: &dyn PlatformSocketBinder,
    ) -> Result<Self, NatError> {
        if address.ip().is_unspecified() {
            return Err(NatError::InvalidCandidate);
        }
        let socket = UdpSocket::bind(address)?;
        let bound_addr = socket.local_addr()?;
        if bound_addr.port() < MIN_CANDIDATE_PORT {
            return Err(NatError::InvalidCandidate);
        }
        // Must occur before STUN, probe, or any other packet leaves this socket.
        binder.bind_before_first_packet(&socket)?;
        Ok(Self { socket, bound_addr })
    }

    pub fn bound_addr(&self) -> SocketAddr {
        self.bound_addr
    }

    pub fn host_candidate(&self, advertised_ip: IpAddr) -> Result<HostCandidate, NatError> {
        let endpoint = SocketAddr::new(advertised_ip, self.bound_addr.port());
        validate_candidate_endpoint(endpoint)?;
        Ok(HostCandidate { endpoint })
    }

    pub fn send_stun_request(
        &self,
        server: SocketAddr,
        transaction: &StunTransaction,
    ) -> Result<usize, NatError> {
        validate_stun_server(server)?;
        Ok(self
            .socket
            .send_to(&transaction.binding_request(), server)?)
    }

    pub fn receive_stun_response(
        &self,
        server: SocketAddr,
        transaction: &StunTransaction,
        timeout: Duration,
    ) -> Result<SocketAddr, NatError> {
        validate_stun_server(server)?;
        if timeout.is_zero() || timeout > MAX_STUN_TIMEOUT {
            return Err(NatError::InvalidTimeout);
        }
        let deadline = Instant::now() + timeout;
        let mut buffer = [0_u8; MAX_STUN_PACKET_BYTES];
        loop {
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                return Err(NatError::ProbeTimeout);
            }
            self.socket.set_read_timeout(Some(remaining))?;
            match self.socket.recv_from(&mut buffer) {
                Ok((length, source)) if source == server => {
                    return transaction.parse_binding_response(&buffer[..length], source, server);
                }
                Ok(_) => continue,
                Err(error) if is_retryable_udp_receive_error(&error) => {
                    return Err(NatError::ProbeTimeout);
                }
                Err(error) => return Err(error.into()),
            }
        }
    }

    pub fn send_probe(&self, packet: &[u8], peer: SocketAddr) -> Result<usize, NatError> {
        if packet.is_empty() || packet.len() > MAX_PROBE_PACKET_BYTES {
            return Err(NatError::ProbeTooLarge);
        }
        validate_candidate_endpoint(peer)?;
        Ok(self.socket.send_to(packet, peer)?)
    }

    pub fn receive_probe(
        &self,
        buffer: &mut [u8; MAX_PROBE_PACKET_BYTES],
        timeout: Duration,
    ) -> Result<(usize, SocketAddr), NatError> {
        self.socket.set_read_timeout(Some(timeout))?;
        let received = self.socket.recv_from(buffer)?;
        validate_candidate_endpoint(received.1)?;
        Ok(received)
    }

    /// Sends and receives on this exact socket until an authenticated peer packet
    /// is accepted or the bounded pair budget expires. Both peers run this pump
    /// concurrently for UDP simultaneous-open; unrelated or malicious packets are
    /// ignored without affecting Relay.
    pub fn simultaneous_probe<F>(
        &self,
        peer: SocketAddr,
        outbound_packets: &[Vec<u8>],
        mut authenticate: F,
    ) -> Result<Vec<u8>, NatError>
    where
        F: FnMut(&[u8]) -> Option<Vec<u8>>,
    {
        validate_candidate_endpoint(peer)?;
        if outbound_packets.is_empty()
            || outbound_packets.len() > MAX_PROBE_ATTEMPTS_PER_DIRECTION
            || outbound_packets
                .iter()
                .any(|packet| packet.is_empty() || packet.len() > MAX_PROBE_PACKET_BYTES)
        {
            return Err(NatError::ProbeTooLarge);
        }
        let deadline = Instant::now() + MAX_PAIR_PROBE_BUDGET;
        let mut buffer = [0_u8; MAX_PROBE_PACKET_BYTES];
        for packet in outbound_packets {
            self.socket.send_to(packet, peer)?;
            loop {
                let remaining = deadline.saturating_duration_since(Instant::now());
                if remaining.is_zero() {
                    return Err(NatError::ProbeTimeout);
                }
                self.socket
                    .set_read_timeout(Some(remaining.min(Duration::from_millis(250))))?;
                match self.socket.recv_from(&mut buffer) {
                    Ok((length, source)) if source == peer => {
                        if let Some(reply) = authenticate(&buffer[..length]) {
                            if reply.len() > length || reply.len() > MAX_PROBE_PACKET_BYTES {
                                return Err(NatError::ProbeAmplificationRisk);
                            }
                            if !reply.is_empty() {
                                self.socket.send_to(&reply, peer)?;
                            }
                            return Ok(buffer[..length].to_vec());
                        }
                    }
                    Ok(_) => {}
                    Err(error) if is_retryable_udp_receive_error(&error) => {
                        break;
                    }
                    Err(error) => return Err(error.into()),
                }
            }
        }
        Err(NatError::ProbeTimeout)
    }

    /// Runs STUN discovery and the nominated host-candidate probe through one
    /// receive pump owned by this socket. This prevents a sequential STUN read
    /// from consuming an early peer probe. Host/LAN probes start immediately
    /// and retry only within the 700ms fast budget; STUN and peer traffic share
    /// the caller's single bounded deadline.
    pub fn discover_and_probe<F, C>(
        &self,
        peer: SocketAddr,
        stun_servers: &[SocketAddr],
        outbound_packets: &[Vec<u8>],
        total_budget: Duration,
        mut authenticate: F,
        mut cancelled: C,
    ) -> Result<DiscoveryProbeOutcome, NatError>
    where
        F: FnMut(&[u8]) -> Option<AuthenticatedProbePacket>,
        C: FnMut() -> bool,
    {
        validate_candidate_endpoint(peer)?;
        if stun_servers.len() > MAX_STUN_SERVERS
            || total_budget.is_zero()
            || total_budget > MAX_STUN_TIMEOUT
        {
            return Err(NatError::InvalidTimeout);
        }
        if outbound_packets.is_empty()
            || outbound_packets.len() > MAX_PROBE_ATTEMPTS_PER_DIRECTION
            || outbound_packets
                .iter()
                .any(|packet| packet.is_empty() || packet.len() > MAX_PROBE_PACKET_BYTES)
        {
            return Err(NatError::ProbeTooLarge);
        }
        for server in stun_servers {
            validate_stun_server(*server)?;
        }

        let transactions = stun_servers
            .iter()
            .map(|server| Ok((*server, StunTransaction::generate()?)))
            .collect::<Result<Vec<_>, NatError>>()?;
        for (server, transaction) in &transactions {
            self.socket
                .send_to(&transaction.binding_request(), server)?;
        }

        let started = Instant::now();
        let deadline = started + total_budget;
        let fast_deadline = started + LAN_FAST_PROBE_BUDGET.min(total_budget);
        let mut next_probe_at = started;
        let mut next_probe_index = 0_usize;
        let mut mapped = Vec::with_capacity(transactions.len());
        let mut buffer = [0_u8; MAX_SOCKET_PACKET_BYTES];

        loop {
            if cancelled() {
                return Err(NatError::Cancelled);
            }
            let now = Instant::now();
            if now >= deadline {
                return Ok(DiscoveryProbeOutcome {
                    server_reflexive_candidates: mapped,
                    authenticated_peer_packet: None,
                });
            }
            if now <= fast_deadline
                && now >= next_probe_at
                && next_probe_index < outbound_packets.len()
            {
                self.socket
                    .send_to(&outbound_packets[next_probe_index], peer)?;
                next_probe_index += 1;
                next_probe_at = now + PROBE_RETRY_INTERVAL;
            }

            let until_next_probe =
                if next_probe_index < outbound_packets.len() && next_probe_at < fast_deadline {
                    next_probe_at.saturating_duration_since(now)
                } else {
                    SOCKET_PUMP_SLICE
                };
            let wait = deadline
                .saturating_duration_since(now)
                .min(SOCKET_PUMP_SLICE)
                .min(until_next_probe.max(Duration::from_millis(1)));
            self.socket.set_read_timeout(Some(wait))?;
            match self.socket.recv_from(&mut buffer) {
                Ok((length, source)) if source == peer => {
                    if let Some(authenticated) = authenticate(&buffer[..length]) {
                        if authenticated.reply.len() > length
                            || authenticated.reply.len() > MAX_PROBE_PACKET_BYTES
                        {
                            return Err(NatError::ProbeAmplificationRisk);
                        }
                        if !authenticated.reply.is_empty() {
                            self.socket.send_to(&authenticated.reply, peer)?;
                        }
                        if !authenticated.path_confirmed {
                            continue;
                        }
                        return Ok(DiscoveryProbeOutcome {
                            server_reflexive_candidates: mapped,
                            authenticated_peer_packet: Some(buffer[..length].to_vec()),
                        });
                    }
                }
                Ok((length, source)) => {
                    if let Some((server, transaction)) =
                        transactions.iter().find(|(server, _)| *server == source)
                        && let Ok(candidate) =
                            transaction.parse_binding_response(&buffer[..length], source, *server)
                        && !mapped.contains(&candidate)
                    {
                        mapped.push(candidate);
                    }
                }
                Err(error) if is_retryable_udp_receive_error(&error) => {}
                Err(error) => return Err(error.into()),
            }
        }
    }

    /// Gathers bounded server-reflexive candidates while retaining exclusive
    /// ownership of this exact socket for a later nomination. No socket handle
    /// or duplicate is exposed to the platform.
    pub fn gather_server_reflexive<C>(
        &self,
        stun_servers: &[SocketAddr],
        timeout: Duration,
        mut cancelled: C,
    ) -> Result<Vec<SocketAddr>, NatError>
    where
        C: FnMut() -> bool,
    {
        if stun_servers.len() > MAX_STUN_SERVERS || timeout.is_zero() || timeout > MAX_STUN_TIMEOUT
        {
            return Err(NatError::InvalidTimeout);
        }
        for server in stun_servers {
            validate_stun_server(*server)?;
        }
        if stun_servers.is_empty() {
            return Ok(Vec::new());
        }
        let transactions = stun_servers
            .iter()
            .map(|server| Ok((*server, StunTransaction::generate()?)))
            .collect::<Result<Vec<_>, NatError>>()?;
        for (server, transaction) in &transactions {
            self.socket
                .send_to(&transaction.binding_request(), server)?;
        }
        let deadline = Instant::now() + timeout;
        let mut mapped = Vec::with_capacity(transactions.len());
        let mut buffer = [0_u8; MAX_STUN_PACKET_BYTES];
        while Instant::now() < deadline && mapped.len() < transactions.len() {
            if cancelled() {
                return Err(NatError::Cancelled);
            }
            self.socket.set_read_timeout(Some(
                deadline
                    .saturating_duration_since(Instant::now())
                    .min(SOCKET_PUMP_SLICE),
            ))?;
            match self.socket.recv_from(&mut buffer) {
                Ok((length, source)) => {
                    if let Some((server, transaction)) =
                        transactions.iter().find(|(server, _)| *server == source)
                        && let Ok(candidate) =
                            transaction.parse_binding_response(&buffer[..length], source, *server)
                        && !mapped.contains(&candidate)
                    {
                        mapped.push(candidate);
                    }
                }
                Err(error) if is_retryable_udp_receive_error(&error) => {}
                Err(error) => return Err(error.into()),
            }
        }
        Ok(mapped)
    }

    /// Moves this socket into the incremental multi-candidate receive pump.
    /// The caller may have already published `host_candidate`; no STUN request
    /// or connectivity packet is sent until the first `step` call.
    pub fn into_multipair_pump(
        self,
        stun_servers: &[SocketAddr],
        total_budget: Duration,
    ) -> Result<MultipairUdpPump, NatError> {
        MultipairUdpPump::new(self, stun_servers, total_budget)
    }

    /// Transfers exclusive ownership of the exact socket used for host/STUN/probe.
    /// There is intentionally no clone, raw-handle, or release-and-rebind API.
    pub fn into_quic_handoff(self) -> QuicSocketHandoff {
        QuicSocketHandoff {
            socket: self.socket,
            original_addr: self.bound_addr,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum PairStatus {
    Queued,
    Active,
    Validated,
    Failed,
}

#[derive(Debug)]
struct PairCheckState {
    pair: MultipairCheckPair,
    status: PairStatus,
    activated_at: Option<Instant>,
    next_probe_at: Option<Instant>,
    last_probe_sent_at: Option<Instant>,
    attempts_sent: usize,
    received_peer_probe: bool,
    received_own_probe_ack: bool,
    acknowledged_round_trip_time: Option<Duration>,
}

impl PairCheckState {
    fn new(pair: MultipairCheckPair) -> Self {
        Self {
            pair,
            status: PairStatus::Queued,
            activated_at: None,
            next_probe_at: None,
            last_probe_sent_at: None,
            attempts_sent: 0,
            received_peer_probe: false,
            received_own_probe_ack: false,
            acknowledged_round_trip_time: None,
        }
    }
}

/// Incremental, exclusive owner of one UDP socket during candidate gathering
/// and connectivity checks.
///
/// The pump has a single receive path. STUN and authenticated pair traffic are
/// demultiplexed in `step`, so neither gathering nor one candidate pair can
/// consume another pair's packet. Candidate pairs activate in append order,
/// with at most four active at once.
#[derive(Debug)]
pub struct MultipairUdpPump {
    socket: DirectUdpSocket,
    stun_transactions: Vec<(SocketAddr, StunTransaction)>,
    stun_requests_sent: bool,
    discovered_candidates: Vec<SocketAddr>,
    deadline: Instant,
    stun_deadline: Instant,
    pairs: Vec<PairCheckState>,
    plan_complete: bool,
    cancelled: bool,
    checks_complete: bool,
    checks_complete_emitted: bool,
}

impl MultipairUdpPump {
    pub fn new(
        socket: DirectUdpSocket,
        stun_servers: &[SocketAddr],
        total_budget: Duration,
    ) -> Result<Self, NatError> {
        if stun_servers.len() > MAX_STUN_SERVERS
            || total_budget.is_zero()
            || total_budget > MAX_MULTIPAIR_CHECK_BUDGET
        {
            return Err(NatError::InvalidTimeout);
        }
        for server in stun_servers {
            validate_stun_server(*server)?;
        }
        let stun_transactions = stun_servers
            .iter()
            .map(|server| Ok((*server, StunTransaction::generate()?)))
            .collect::<Result<Vec<_>, NatError>>()?;
        let started_at = Instant::now();
        Ok(Self {
            socket,
            stun_transactions,
            stun_requests_sent: false,
            discovered_candidates: Vec::with_capacity(stun_servers.len()),
            deadline: started_at + total_budget,
            stun_deadline: started_at + total_budget.min(MAX_STUN_TIMEOUT),
            pairs: Vec::new(),
            plan_complete: false,
            cancelled: false,
            checks_complete: false,
            checks_complete_emitted: false,
        })
    }

    pub fn bound_addr(&self) -> SocketAddr {
        self.socket.bound_addr()
    }

    /// Appends one batch atomically. If any item is invalid or the batch would
    /// exceed 32 total pairs, no item from the batch is retained. In
    /// particular, the 33rd pair is rejected rather than silently truncated.
    pub fn append_pairs(&mut self, pairs: &[MultipairCheckPair]) -> Result<(), NatError> {
        if self.plan_complete || self.cancelled || self.checks_complete {
            return Err(NatError::CheckPlanClosed);
        }
        if self.pairs.len().saturating_add(pairs.len()) > MAX_MULTIPAIR_CANDIDATE_PAIRS {
            return Err(NatError::TooManyCandidatePairs);
        }
        for pair in pairs {
            validate_candidate_endpoint(pair.peer)?;
            if pair.probe_packet.is_empty() || pair.probe_packet.len() > MAX_PROBE_PACKET_BYTES {
                return Err(NatError::ProbeTooLarge);
            }
            if self
                .pairs
                .iter()
                .any(|existing| existing.pair.token == pair.token)
                || pairs
                    .iter()
                    .filter(|candidate| candidate.token == pair.token)
                    .count()
                    > 1
            {
                return Err(NatError::DuplicatePairToken);
            }
        }
        self.pairs
            .extend(pairs.iter().cloned().map(PairCheckState::new));
        Ok(())
    }

    pub fn append_pair(&mut self, pair: MultipairCheckPair) -> Result<(), NatError> {
        self.append_pairs(&[pair])
    }

    /// Restarts the total connectivity-check budget immediately before the
    /// first pair batch is appended. Candidate gathering may legitimately
    /// precede signalling and ticket exchange, so its elapsed time must not
    /// consume the check budget.
    pub fn start_checks(&mut self, total_budget: Duration) -> Result<(), NatError> {
        if total_budget.is_zero()
            || total_budget > MAX_MULTIPAIR_CHECK_BUDGET
            || !self.pairs.is_empty()
            || self.plan_complete
            || self.cancelled
        {
            return Err(NatError::InvalidTimeout);
        }
        self.deadline = Instant::now() + total_budget;
        self.checks_complete = false;
        self.checks_complete_emitted = false;
        Ok(())
    }

    pub fn mark_plan_complete(&mut self) {
        self.plan_complete = true;
    }

    pub fn cancel(&mut self) {
        self.cancelled = true;
    }

    pub fn is_checks_complete(&self) -> bool {
        self.checks_complete
    }

    pub fn active_pair_count(&self) -> usize {
        self.pairs
            .iter()
            .filter(|pair| pair.status == PairStatus::Active)
            .count()
    }

    pub fn total_pair_count(&self) -> usize {
        self.pairs.len()
    }

    /// Drives sends and the sole receive pump for at most `max_wait`.
    ///
    /// `authenticate` owns all protocol parsing and cryptographic decisions.
    /// Results whose token/source do not identify an active or previously
    /// validated pair are ignored. The method returns as soon as it has an
    /// externally visible event, allowing the caller to trickle STUN and pair
    /// progress without dedicating another receive thread.
    pub fn step<F>(
        &mut self,
        max_wait: Duration,
        mut authenticate: F,
    ) -> Result<Vec<MultipairPumpEvent>, NatError>
    where
        F: FnMut(SocketAddr, &[u8]) -> Option<AuthenticatedMultipairPacket>,
    {
        if max_wait.is_zero() || max_wait > MAX_MULTIPAIR_CHECK_BUDGET {
            return Err(NatError::InvalidTimeout);
        }
        if self.cancelled {
            return Err(NatError::Cancelled);
        }
        if self.checks_complete && self.checks_complete_emitted {
            return Ok(Vec::new());
        }

        if !self.stun_requests_sent {
            for (server, transaction) in &self.stun_transactions {
                self.socket.send_stun_request(*server, transaction)?;
            }
            self.stun_requests_sent = true;
        }

        let step_deadline = Instant::now() + max_wait;
        let mut buffer = [0_u8; MAX_SOCKET_PACKET_BYTES];
        loop {
            if self.cancelled {
                return Err(NatError::Cancelled);
            }

            let now = Instant::now();
            self.expire_pairs(now);
            self.activate_available_pairs(now);
            self.send_due_probes(now)?;

            let mut events = Vec::new();
            self.finish_checks_if_ready(now);
            if self.checks_complete && !self.checks_complete_emitted {
                self.checks_complete_emitted = true;
                events.push(MultipairPumpEvent::ChecksComplete);
                return Ok(events);
            }
            if now >= step_deadline {
                return Ok(events);
            }

            let wait = self.next_receive_wait(now, step_deadline);
            if wait.is_zero() {
                continue;
            }
            self.socket.socket.set_read_timeout(Some(wait))?;
            match self.socket.socket.recv_from(&mut buffer) {
                Ok((length, source)) => {
                    let received_at = Instant::now();
                    if received_at >= self.deadline {
                        continue;
                    }
                    if received_at <= self.stun_deadline
                        && let Some((server, transaction)) = self
                            .stun_transactions
                            .iter()
                            .find(|(server, _)| *server == source)
                        && let Ok(candidate) =
                            transaction.parse_binding_response(&buffer[..length], source, *server)
                    {
                        if !self.discovered_candidates.contains(&candidate) {
                            self.discovered_candidates.push(candidate);
                            events.push(MultipairPumpEvent::ServerReflexiveCandidate(candidate));
                            return Ok(events);
                        }
                        continue;
                    }

                    let Some(authenticated) = authenticate(source, &buffer[..length]) else {
                        continue;
                    };
                    if let Some(event) =
                        self.apply_authenticated_packet(source, length, received_at, authenticated)?
                    {
                        events.push(event);
                        return Ok(events);
                    }
                }
                Err(error) if is_retryable_udp_receive_error(&error) => {}
                Err(error) => return Err(error.into()),
            }
        }
    }

    /// Commits exactly one already-validated winner and transfers the original
    /// socket to QUIC. Consuming `self` makes it impossible for losing checks to
    /// keep running or for the NAT layer to clone/rebind the selected socket.
    pub fn commit_winner(self, token: PairToken) -> Result<QuicSocketHandoff, NatError> {
        if self.cancelled
            || !self
                .pairs
                .iter()
                .any(|pair| pair.pair.token == token && pair.status == PairStatus::Validated)
        {
            return Err(NatError::WinnerNotValidated);
        }
        Ok(self.socket.into_quic_handoff())
    }

    fn activate_available_pairs(&mut self, now: Instant) {
        let mut remaining_slots =
            MAX_ACTIVE_CANDIDATE_PAIRS.saturating_sub(self.active_pair_count());
        if remaining_slots == 0 {
            return;
        }
        for state in &mut self.pairs {
            if remaining_slots == 0 {
                break;
            }
            if state.status == PairStatus::Queued {
                state.status = PairStatus::Active;
                state.activated_at = Some(now);
                state.next_probe_at = Some(now);
                remaining_slots -= 1;
            }
        }
    }

    fn expire_pairs(&mut self, now: Instant) {
        for state in &mut self.pairs {
            if state.status == PairStatus::Active
                && state
                    .activated_at
                    .is_some_and(|started| now.duration_since(started) >= MAX_PAIR_PROBE_BUDGET)
            {
                state.status = PairStatus::Failed;
                state.next_probe_at = None;
            }
        }
    }

    fn send_due_probes(&mut self, now: Instant) -> Result<(), NatError> {
        for state in &mut self.pairs {
            if state.status != PairStatus::Active
                || state.attempts_sent >= MAX_PROBE_ATTEMPTS_PER_DIRECTION
                || !state.next_probe_at.is_some_and(|due| now >= due)
            {
                continue;
            }
            self.socket
                .send_probe(&state.pair.probe_packet, state.pair.peer)?;
            state.last_probe_sent_at = Some(now);
            state.attempts_sent += 1;
            state.next_probe_at = (state.attempts_sent < MAX_PROBE_ATTEMPTS_PER_DIRECTION)
                .then_some(now + PROBE_RETRY_INTERVAL);
        }
        Ok(())
    }

    fn finish_checks_if_ready(&mut self, now: Instant) {
        if self.checks_complete {
            return;
        }
        if now >= self.deadline {
            for state in &mut self.pairs {
                if matches!(state.status, PairStatus::Queued | PairStatus::Active) {
                    state.status = PairStatus::Failed;
                    state.next_probe_at = None;
                }
            }
            self.checks_complete = true;
            return;
        }
        self.checks_complete = self.plan_complete
            && self
                .pairs
                .iter()
                .all(|pair| matches!(pair.status, PairStatus::Validated | PairStatus::Failed));
    }

    fn next_receive_wait(&self, now: Instant, step_deadline: Instant) -> Duration {
        let mut wake_at = step_deadline.min(self.deadline);
        for state in &self.pairs {
            if state.status != PairStatus::Active {
                continue;
            }
            if let Some(next_probe_at) = state.next_probe_at {
                wake_at = wake_at.min(next_probe_at);
            }
            if let Some(activated_at) = state.activated_at {
                wake_at = wake_at.min(activated_at + MAX_PAIR_PROBE_BUDGET);
            }
        }
        wake_at
            .saturating_duration_since(now)
            .min(SOCKET_PUMP_SLICE)
    }

    fn apply_authenticated_packet(
        &mut self,
        source: SocketAddr,
        received_length: usize,
        received_at: Instant,
        authenticated: AuthenticatedMultipairPacket,
    ) -> Result<Option<MultipairPumpEvent>, NatError> {
        let token = match &authenticated {
            AuthenticatedMultipairPacket::Probe { token, .. }
            | AuthenticatedMultipairPacket::Acknowledgement { token } => *token,
        };
        let Some(index) = self.pairs.iter().position(|state| {
            state.pair.token == token
                && state.pair.peer == source
                && matches!(state.status, PairStatus::Active | PairStatus::Validated)
        }) else {
            return Ok(None);
        };

        match authenticated {
            AuthenticatedMultipairPacket::Probe {
                acknowledgement, ..
            } => {
                if acknowledgement.len() > received_length
                    || acknowledgement.len() > MAX_PROBE_PACKET_BYTES
                {
                    return Err(NatError::ProbeAmplificationRisk);
                }
                self.pairs[index].received_peer_probe = true;
                if !acknowledgement.is_empty() {
                    self.socket.send_probe(&acknowledgement, source)?;
                }
            }
            AuthenticatedMultipairPacket::Acknowledgement { .. } => {
                if self.pairs[index].attempts_sent != 0 {
                    self.pairs[index].received_own_probe_ack = true;
                    self.pairs[index].acknowledged_round_trip_time = self.pairs[index]
                        .last_probe_sent_at
                        .map(|sent_at| received_at.saturating_duration_since(sent_at));
                }
            }
        }

        let state = &mut self.pairs[index];
        if state.status == PairStatus::Active
            && state.received_peer_probe
            && state.received_own_probe_ack
        {
            state.status = PairStatus::Validated;
            state.next_probe_at = None;
            return Ok(Some(MultipairPumpEvent::PairValidated {
                token: state.pair.token,
                peer: state.pair.peer,
                round_trip_time: state.acknowledged_round_trip_time.unwrap_or_default(),
                probe_attempts: state.attempts_sent,
            }));
        }
        Ok(None)
    }
}

/// Returns true for receive-side UDP errors that do not prove the nominated
/// path is dead. In particular, Windows reports an asynchronous ICMP Port
/// Unreachable as WSAECONNRESET (10054). That can be left over from the first
/// simultaneous-open probe sent just before the peer bound its socket, so the
/// bounded probe/QUIC receive pump must drain it and continue.
pub fn is_retryable_udp_receive_error(error: &io::Error) -> bool {
    matches!(
        error.kind(),
        io::ErrorKind::WouldBlock | io::ErrorKind::TimedOut | io::ErrorKind::Interrupted
    ) || cfg!(target_os = "windows") && error.raw_os_error() == Some(10_054)
}

pub trait PlatformSocketBinder: Send + Sync {
    fn bind_before_first_packet(&self, socket: &UdpSocket) -> Result<(), NatError>;
}

#[derive(Clone, Copy, Debug, Default)]
pub struct NoopPlatformSocketBinder;

impl PlatformSocketBinder for NoopPlatformSocketBinder {
    fn bind_before_first_packet(&self, _socket: &UdpSocket) -> Result<(), NatError> {
        Ok(())
    }
}

#[cfg(target_os = "android")]
#[derive(Clone, Copy, Debug)]
pub struct AndroidNetworkBinder {
    network_handle: u64,
}

#[cfg(target_os = "android")]
impl AndroidNetworkBinder {
    pub fn new(network_handle: u64) -> Result<Self, NatError> {
        if network_handle == 0 {
            return Err(NatError::PlatformBindFailed);
        }
        Ok(Self { network_handle })
    }
}

#[cfg(target_os = "android")]
impl PlatformSocketBinder for AndroidNetworkBinder {
    fn bind_before_first_packet(&self, socket: &UdpSocket) -> Result<(), NatError> {
        use std::os::fd::AsRawFd;

        #[link(name = "android")]
        unsafe extern "C" {
            fn android_setsocknetwork(network_handle: u64, socket_fd: i32) -> i32;
        }

        // SAFETY: The handle comes from Android Network#getNetworkHandle and the
        // descriptor is live for this call. This is invoked before the socket is shared.
        let result = unsafe { android_setsocknetwork(self.network_handle, socket.as_raw_fd()) };
        if result != 0 {
            return Err(NatError::PlatformBindFailed);
        }
        Ok(())
    }
}

#[derive(Debug)]
pub struct QuicSocketHandoff {
    socket: UdpSocket,
    original_addr: SocketAddr,
}

impl QuicSocketHandoff {
    pub fn original_addr(&self) -> SocketAddr {
        self.original_addr
    }

    pub fn into_std_socket(self) -> Result<UdpSocket, NatError> {
        if self.socket.local_addr()? != self.original_addr {
            return Err(NatError::SocketIdentityChanged);
        }
        self.socket.set_read_timeout(None)?;
        self.socket.set_write_timeout(None)?;
        self.socket.set_nonblocking(true)?;
        Ok(self.socket)
    }
}

#[derive(Debug)]
pub enum NatError {
    Io(io::Error),
    RandomUnavailable,
    InvalidCandidate,
    InvalidStunServer,
    InvalidStun,
    StunContextMismatch,
    ProbeTooLarge,
    ProbeTimeout,
    ProbeAmplificationRisk,
    SocketIdentityChanged,
    PlatformBindFailed,
    PlatformBinderRequired,
    InvalidTimeout,
    Cancelled,
    TooManyCandidatePairs,
    DuplicatePairToken,
    CheckPlanClosed,
    WinnerNotValidated,
}

impl fmt::Display for NatError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(match self {
            Self::Io(_) => "UDP I/O failed",
            Self::RandomUnavailable => "secure randomness is unavailable",
            Self::InvalidCandidate => "candidate endpoint is invalid",
            Self::InvalidStunServer => "STUN server endpoint is invalid",
            Self::InvalidStun => "STUN response is invalid",
            Self::StunContextMismatch => "STUN response source or transaction does not match",
            Self::ProbeTooLarge => "connectivity probe exceeds the wire limit",
            Self::ProbeTimeout => "connectivity probe timed out",
            Self::ProbeAmplificationRisk => "connectivity response would amplify traffic",
            Self::SocketIdentityChanged => "UDP socket identity changed before QUIC handoff",
            Self::PlatformBindFailed => {
                "UDP socket could not bind to the selected platform network"
            }
            Self::PlatformBinderRequired => {
                "Android UDP sockets require a Network handle before the first packet"
            }
            Self::InvalidTimeout => "direct UDP timeout is outside the allowed bounds",
            Self::Cancelled => "direct UDP attempt was cancelled",
            Self::TooManyCandidatePairs => "direct UDP check plan exceeds 32 candidate pairs",
            Self::DuplicatePairToken => "direct UDP candidate pair token is duplicated",
            Self::CheckPlanClosed => "direct UDP candidate pair plan is already closed",
            Self::WinnerNotValidated => "direct UDP winner has not completed bidirectional checks",
        })
    }
}

impl std::error::Error for NatError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            Self::Io(error) => Some(error),
            _ => None,
        }
    }
}

impl From<io::Error> for NatError {
    fn from(value: io::Error) -> Self {
        Self::Io(value)
    }
}

fn validate_stun_server(endpoint: SocketAddr) -> Result<(), NatError> {
    if endpoint.port() == 0 || !is_unicast(endpoint.ip()) {
        return Err(NatError::InvalidStunServer);
    }
    Ok(())
}

pub fn validate_candidate_endpoint(endpoint: SocketAddr) -> Result<(), NatError> {
    if endpoint.port() < MIN_CANDIDATE_PORT || !is_unicast(endpoint.ip()) {
        return Err(NatError::InvalidCandidate);
    }
    Ok(())
}

fn is_unicast(ip: IpAddr) -> bool {
    match ip {
        IpAddr::V4(ip) => {
            !ip.is_unspecified()
                && !ip.is_loopback()
                && !ip.is_multicast()
                && !ip.is_broadcast()
                && !ip.is_documentation()
                && !ip.is_link_local()
        }
        IpAddr::V6(ip) => {
            !ip.is_unspecified()
                && !ip.is_loopback()
                && !ip.is_multicast()
                && !ip.is_unicast_link_local()
        }
    }
}

fn parse_xor_mapped(value: &[u8], transaction_id: &[u8; 12]) -> Result<SocketAddr, NatError> {
    if value.len() < 4 || value[0] != 0 {
        return Err(NatError::InvalidStun);
    }
    let port = u16::from_be_bytes([value[2], value[3]]) ^ (STUN_MAGIC_COOKIE >> 16) as u16;
    let cookie = STUN_MAGIC_COOKIE.to_be_bytes();
    match value[1] {
        0x01 if value.len() == 8 => {
            let address = Ipv4Addr::new(
                value[4] ^ cookie[0],
                value[5] ^ cookie[1],
                value[6] ^ cookie[2],
                value[7] ^ cookie[3],
            );
            Ok(SocketAddr::new(IpAddr::V4(address), port))
        }
        0x02 if value.len() == 20 => {
            let mut mask = [0_u8; 16];
            mask[..4].copy_from_slice(&cookie);
            mask[4..].copy_from_slice(transaction_id);
            let mut address = [0_u8; 16];
            for index in 0..16 {
                address[index] = value[index + 4] ^ mask[index];
            }
            Ok(SocketAddr::new(IpAddr::V6(Ipv6Addr::from(address)), port))
        }
        _ => Err(NatError::InvalidStun),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::thread;

    #[test]
    fn socket_port_survives_exclusive_quic_handoff() {
        let owner = DirectUdpSocket::bind("127.0.0.1:0".parse().unwrap()).unwrap();
        let original = owner.bound_addr();
        let handoff = owner.into_quic_handoff();
        assert_eq!(handoff.original_addr(), original);
        let socket = handoff.into_std_socket().unwrap();
        assert_eq!(socket.local_addr().unwrap(), original);
    }

    #[test]
    fn parses_rfc8489_xor_mapped_address_and_rejects_spoofed_source() {
        let transaction = StunTransaction::from_id(*b"abcdefghijkl");
        let server: SocketAddr = "192.0.2.1:3478".parse().unwrap();
        let mapped: SocketAddr = "8.8.8.8:54321".parse().unwrap();
        let cookie = STUN_MAGIC_COOKIE.to_be_bytes();
        let mut value = vec![0, 1];
        value.extend_from_slice(&(mapped.port() ^ (STUN_MAGIC_COOKIE >> 16) as u16).to_be_bytes());
        let octets = match mapped.ip() {
            IpAddr::V4(ip) => ip.octets(),
            _ => unreachable!(),
        };
        for index in 0..4 {
            value.push(octets[index] ^ cookie[index]);
        }
        let mut packet = Vec::from([0x01, 0x01, 0, 12]);
        packet.extend_from_slice(&STUN_MAGIC_COOKIE.to_be_bytes());
        packet.extend_from_slice(&transaction.id());
        packet.extend_from_slice(&0x0020_u16.to_be_bytes());
        packet.extend_from_slice(&8_u16.to_be_bytes());
        packet.extend_from_slice(&value);
        assert_eq!(
            transaction
                .parse_binding_response(&packet, server, server)
                .unwrap(),
            mapped
        );
        assert!(matches!(
            transaction.parse_binding_response(&packet, "192.0.2.2:3478".parse().unwrap(), server),
            Err(NatError::StunContextMismatch)
        ));
    }

    #[test]
    fn one_socket_pump_demuxes_stun_and_an_early_lan_probe() {
        let ip = active_unicast_ip();
        let owner = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let owner_addr = owner.bound_addr();
        let stun = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let stun_addr = stun.local_addr().unwrap();
        let peer = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let peer_addr = peer.local_addr().unwrap();

        let stun_task = thread::spawn(move || {
            let mut request = [0_u8; STUN_HEADER_BYTES];
            let (_, source) = stun.recv_from(&mut request).unwrap();
            let transaction_id = request[8..20].try_into().unwrap();
            stun.send_to(&stun_response(source, transaction_id), source)
                .unwrap();
        });
        let peer_task = thread::spawn(move || {
            let mut request = [0_u8; MAX_PROBE_PACKET_BYTES];
            let (_, source) = peer.recv_from(&mut request).unwrap();
            thread::sleep(Duration::from_millis(40));
            peer.send_to(b"authenticated-peer", source).unwrap();
        });

        let started = Instant::now();
        let outcome = owner
            .discover_and_probe(
                peer_addr,
                &[stun_addr],
                &[vec![7; 32], vec![8; 32]],
                Duration::from_secs(1),
                |packet| {
                    (packet == b"authenticated-peer").then_some(AuthenticatedProbePacket {
                        reply: Vec::new(),
                        path_confirmed: true,
                    })
                },
                || false,
            )
            .unwrap();
        assert!(started.elapsed() < LAN_FAST_PROBE_BUDGET);
        assert_eq!(
            outcome.authenticated_peer_packet.as_deref(),
            Some(b"authenticated-peer".as_slice())
        );
        assert_eq!(outcome.server_reflexive_candidates, vec![owner_addr]);
        stun_task.join().unwrap();
        peer_task.join().unwrap();
    }

    #[test]
    fn one_socket_pump_honors_cancellation_without_waiting_for_io_timeout() {
        let ip = active_unicast_ip();
        let owner = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let peer = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let started = Instant::now();
        assert!(matches!(
            owner.discover_and_probe(
                peer.local_addr().unwrap(),
                &[],
                &[vec![1; 32]],
                Duration::from_secs(2),
                |_| None,
                || true,
            ),
            Err(NatError::Cancelled)
        ));
        assert!(started.elapsed() < SOCKET_PUMP_SLICE);
    }

    #[test]
    fn multipair_lan_validates_under_700ms_while_stun_is_a_blackhole() {
        let ip = active_unicast_ip();
        let owner = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let blackhole = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let peer = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let peer_addr = peer.local_addr().unwrap();
        let token = test_pair_token(1);
        let peer_task = thread::spawn(move || {
            let mut request = [0_u8; MAX_PROBE_PACKET_BYTES];
            let (_, source) = peer.recv_from(&mut request).unwrap();
            peer.send_to(b"peer-probe", source).unwrap();
            peer.send_to(b"probe-ack", source).unwrap();
        });

        let mut pump = owner
            .into_multipair_pump(&[blackhole.local_addr().unwrap()], Duration::from_secs(2))
            .unwrap();
        pump.append_pair(test_pair(token, peer_addr)).unwrap();
        pump.mark_plan_complete();
        let started = Instant::now();
        let validated = drive_until_validated(&mut pump, token, Duration::from_secs(1));
        assert!(validated);
        assert!((1..=MAX_PROBE_ATTEMPTS_PER_DIRECTION).contains(&pump.pairs[0].attempts_sent));
        assert!(pump.pairs[0].acknowledged_round_trip_time.is_some());
        assert!(started.elapsed() < LAN_FAST_PROBE_BUDGET);
        peer_task.join().unwrap();
    }

    #[test]
    fn check_budget_starts_when_checks_begin_not_when_gathering_started() {
        let ip = active_unicast_ip();
        let owner = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let peer = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let mut pump = owner
            .into_multipair_pump(&[], Duration::from_millis(100))
            .unwrap();

        thread::sleep(Duration::from_millis(125));
        assert!(
            pump.step(Duration::from_millis(1), |_, _| None)
                .unwrap()
                .iter()
                .any(|event| matches!(event, MultipairPumpEvent::ChecksComplete))
        );

        pump.start_checks(Duration::from_millis(500)).unwrap();
        pump.append_pair(test_pair(test_pair_token(1), peer.local_addr().unwrap()))
            .unwrap();
        assert!(
            pump.step(Duration::from_millis(20), |_, _| None)
                .unwrap()
                .is_empty()
        );
        assert!(!pump.is_checks_complete());
        assert_eq!(pump.active_pair_count(), 1);
    }

    #[test]
    fn multipair_activates_at_most_four_pairs_in_append_order() {
        let ip = active_unicast_ip();
        let owner = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let peer = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let mut pump = owner
            .into_multipair_pump(&[], Duration::from_secs(3))
            .unwrap();
        let pairs = (0..5)
            .map(|index| test_pair(test_pair_token(index), peer.local_addr().unwrap()))
            .collect::<Vec<_>>();
        pump.append_pairs(&pairs).unwrap();

        assert!(
            pump.step(Duration::from_millis(30), |_, _| None)
                .unwrap()
                .is_empty()
        );
        assert_eq!(pump.active_pair_count(), MAX_ACTIVE_CANDIDATE_PAIRS);
        assert_eq!(pump.pairs[0].status, PairStatus::Active);
        assert_eq!(pump.pairs[3].status, PairStatus::Active);
        assert_eq!(pump.pairs[4].status, PairStatus::Queued);
    }

    #[test]
    fn multipair_first_four_can_fail_before_fifth_succeeds() {
        let ip = active_unicast_ip();
        let owner = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let silent_peers = (0..4)
            .map(|_| UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap())
            .collect::<Vec<_>>();
        let fifth_peer = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let fifth_addr = fifth_peer.local_addr().unwrap();
        let fifth_token = test_pair_token(5);
        let peer_task = thread::spawn(move || {
            let mut request = [0_u8; MAX_PROBE_PACKET_BYTES];
            let (_, source) = fifth_peer.recv_from(&mut request).unwrap();
            fifth_peer.send_to(b"peer-probe", source).unwrap();
            fifth_peer.send_to(b"probe-ack", source).unwrap();
        });

        let mut pump = owner
            .into_multipair_pump(&[], Duration::from_secs(4))
            .unwrap();
        for (index, peer) in silent_peers.iter().enumerate() {
            pump.append_pair(test_pair(
                test_pair_token(index),
                peer.local_addr().unwrap(),
            ))
            .unwrap();
        }
        pump.append_pair(test_pair(fifth_token, fifth_addr))
            .unwrap();
        pump.mark_plan_complete();

        assert!(drive_until_validated(
            &mut pump,
            fifth_token,
            Duration::from_secs(3)
        ));
        for failed in &pump.pairs[..4] {
            assert_eq!(failed.status, PairStatus::Failed);
            assert_eq!(failed.attempts_sent, MAX_PROBE_ATTEMPTS_PER_DIRECTION);
        }
        assert_eq!(pump.pairs[4].status, PairStatus::Validated);
        peer_task.join().unwrap();
    }

    #[test]
    fn multipair_rejects_the_33rd_pair_without_truncating_or_partially_appending() {
        let ip = active_unicast_ip();
        let owner = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let peer = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let peer_addr = peer.local_addr().unwrap();
        let mut pump = owner
            .into_multipair_pump(&[], Duration::from_secs(1))
            .unwrap();
        let first_32 = (0..MAX_MULTIPAIR_CANDIDATE_PAIRS)
            .map(|index| test_pair(test_pair_token(index), peer_addr))
            .collect::<Vec<_>>();
        pump.append_pairs(&first_32).unwrap();

        assert!(matches!(
            pump.append_pair(test_pair(test_pair_token(32), peer_addr)),
            Err(NatError::TooManyCandidatePairs)
        ));
        assert_eq!(pump.total_pair_count(), MAX_MULTIPAIR_CANDIDATE_PAIRS);
    }

    #[test]
    fn multipair_requires_both_peer_probe_and_own_probe_ack() {
        let ip = active_unicast_ip();
        let owner = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let peer = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let peer_addr = peer.local_addr().unwrap();
        let token = test_pair_token(7);
        let peer_task = thread::spawn(move || {
            let mut request = [0_u8; MAX_PROBE_PACKET_BYTES];
            let (_, source) = peer.recv_from(&mut request).unwrap();
            peer.send_to(b"probe-ack", source).unwrap();
            thread::sleep(Duration::from_millis(120));
            peer.send_to(b"peer-probe", source).unwrap();
        });
        let mut pump = owner
            .into_multipair_pump(&[], Duration::from_secs(2))
            .unwrap();
        pump.append_pair(test_pair(token, peer_addr)).unwrap();

        let early = pump
            .step(Duration::from_millis(80), |source, packet| {
                authenticate_test_packet(token, peer_addr, source, packet)
            })
            .unwrap();
        assert!(early.is_empty());
        assert_eq!(pump.pairs[0].status, PairStatus::Active);
        assert!(!pump.pairs[0].received_peer_probe);
        assert!(pump.pairs[0].received_own_probe_ack);

        assert!(drive_until_validated(
            &mut pump,
            token,
            Duration::from_secs(1)
        ));
        peer_task.join().unwrap();
    }

    #[test]
    fn multipair_cancel_is_immediate_and_closes_the_plan() {
        let ip = active_unicast_ip();
        let owner = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let peer = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let mut pump = owner
            .into_multipair_pump(&[], Duration::from_secs(3))
            .unwrap();
        pump.append_pair(test_pair(test_pair_token(1), peer.local_addr().unwrap()))
            .unwrap();
        pump.cancel();

        let started = Instant::now();
        assert!(matches!(
            pump.step(Duration::from_secs(1), |_, _| None),
            Err(NatError::Cancelled)
        ));
        assert!(started.elapsed() < SOCKET_PUMP_SLICE);
        assert!(matches!(
            pump.append_pair(test_pair(test_pair_token(2), peer.local_addr().unwrap())),
            Err(NatError::CheckPlanClosed)
        ));
    }

    #[test]
    fn multipair_complete_plan_emits_checks_complete_once() {
        let ip = active_unicast_ip();
        let owner = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let mut pump = owner
            .into_multipair_pump(&[], Duration::from_secs(1))
            .unwrap();
        pump.mark_plan_complete();

        assert_eq!(
            pump.step(Duration::from_millis(20), |_, _| None).unwrap(),
            vec![MultipairPumpEvent::ChecksComplete]
        );
        assert!(pump.is_checks_complete());
        assert!(
            pump.step(Duration::from_millis(20), |_, _| None)
                .unwrap()
                .is_empty()
        );
    }

    #[test]
    fn multipair_validated_winner_moves_the_original_socket_into_quic_handoff() {
        let ip = active_unicast_ip();
        let owner = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let original_addr = owner.bound_addr();
        let peer = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let peer_addr = peer.local_addr().unwrap();
        let token = test_pair_token(9);
        let peer_task = thread::spawn(move || {
            let mut request = [0_u8; MAX_PROBE_PACKET_BYTES];
            let (_, source) = peer.recv_from(&mut request).unwrap();
            peer.send_to(b"peer-probe", source).unwrap();
            peer.send_to(b"probe-ack", source).unwrap();
        });
        let mut pump = owner
            .into_multipair_pump(&[], Duration::from_secs(2))
            .unwrap();
        pump.append_pair(test_pair(token, peer_addr)).unwrap();
        assert!(drive_until_validated(
            &mut pump,
            token,
            Duration::from_secs(1)
        ));

        let handoff = pump.commit_winner(token).unwrap();
        assert_eq!(handoff.original_addr(), original_addr);
        let socket = handoff.into_std_socket().unwrap();
        assert_eq!(socket.local_addr().unwrap(), original_addr);
        peer_task.join().unwrap();
    }

    fn drive_until_validated(
        pump: &mut MultipairUdpPump,
        token: PairToken,
        timeout: Duration,
    ) -> bool {
        let peer = pump
            .pairs
            .iter()
            .find(|pair| pair.pair.token == token)
            .unwrap()
            .pair
            .peer;
        let deadline = Instant::now() + timeout;
        while Instant::now() < deadline {
            let events = pump
                .step(Duration::from_millis(100), |source, packet| {
                    authenticate_test_packet(token, peer, source, packet)
                })
                .unwrap();
            if events.iter().any(|event| {
                matches!(
                    event,
                    MultipairPumpEvent::PairValidated {
                        token: validated,
                        ..
                    } if *validated == token
                )
            }) {
                return true;
            }
        }
        false
    }

    fn authenticate_test_packet(
        token: PairToken,
        expected_peer: SocketAddr,
        source: SocketAddr,
        packet: &[u8],
    ) -> Option<AuthenticatedMultipairPacket> {
        if source != expected_peer {
            return None;
        }
        match packet {
            b"peer-probe" => Some(AuthenticatedMultipairPacket::Probe {
                token,
                acknowledgement: b"ack".to_vec(),
            }),
            b"probe-ack" => Some(AuthenticatedMultipairPacket::Acknowledgement { token }),
            _ => None,
        }
    }

    fn test_pair(token: PairToken, peer: SocketAddr) -> MultipairCheckPair {
        MultipairCheckPair {
            token,
            peer,
            probe_packet: b"local-probe".to_vec(),
        }
    }

    fn test_pair_token(index: usize) -> PairToken {
        let mut bytes = [0_u8; 16];
        bytes[..8].copy_from_slice(&(index as u64).to_be_bytes());
        PairToken::new(bytes)
    }

    fn active_unicast_ip() -> IpAddr {
        let socket = UdpSocket::bind("0.0.0.0:0").unwrap();
        socket.connect("192.0.2.1:9").unwrap();
        let ip = socket.local_addr().unwrap().ip();
        validate_candidate_endpoint(SocketAddr::new(ip, 49_152)).unwrap();
        ip
    }

    fn stun_response(mapped: SocketAddr, transaction_id: [u8; 12]) -> Vec<u8> {
        let IpAddr::V4(ip) = mapped.ip() else {
            panic!("test currently requires IPv4")
        };
        let cookie = STUN_MAGIC_COOKIE.to_be_bytes();
        let mut response = Vec::from([0x01, 0x01, 0, 12]);
        response.extend_from_slice(&cookie);
        response.extend_from_slice(&transaction_id);
        response.extend_from_slice(&0x0020_u16.to_be_bytes());
        response.extend_from_slice(&8_u16.to_be_bytes());
        response.extend_from_slice(&[0, 1]);
        response
            .extend_from_slice(&(mapped.port() ^ (STUN_MAGIC_COOKIE >> 16) as u16).to_be_bytes());
        for (octet, mask) in ip.octets().into_iter().zip(cookie) {
            response.push(octet ^ mask);
        }
        response
    }
}
