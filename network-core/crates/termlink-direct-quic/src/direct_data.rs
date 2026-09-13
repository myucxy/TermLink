use std::{
    net::SocketAddr,
    sync::{
        Arc,
        atomic::{AtomicBool, AtomicU64, Ordering},
    },
    time::{Duration, Instant},
};

use quinn::{ClientConfig, Connection, Endpoint, ServerConfig, TransportConfig, VarInt};
use tokio::sync::watch;

use super::{
    ExpectedPeer, MAX_PEER_SIGNATURE_BYTES, PeerBindingContext, PeerProofRequest, PeerRole,
    PlatformPeerProofVerifier, PortableSingleDatagramSocket, QUIC_MTU, QuicError,
    SHADOW_OPERATION_TIMEOUT, TemporaryIdentity, authenticate_shadow_connection,
    client_config_with_transport, decode_peer_proof_frame, decode_peer_proof_result_frame,
    direct_now_ms, encode_peer_proof_frame, encode_peer_proof_result_frame, endpoint_config,
    put_bytes_field, put_varint_field, require_same_socket, server_config_with_transport, sha256,
    verify_peer_certificate,
};
use termlink_direct_nat::{QuicSocketHandoff, validate_candidate_endpoint};

/// Maximum serialized `Envelope` carried without parsing or reserializing it.
pub const MAX_REALTIME_ENVELOPE_BYTES: usize = 1024 * 1024 - 1;
/// Maximum length-prefixed production `DirectPathFrameV1` wire record.
pub const MAX_DIRECT_DATA_FRAME_WIRE_BYTES: usize = 1_048_640;
/// The initial production liveness exchange consumes sequence 3 independently
/// in each direction, immediately after proof/result sequences 1 and 2.
pub const INITIAL_DIRECT_LIVENESS_FRAME_SEQ: u64 = 3;
/// The first frame sequence exposed to the post-authentication data API.
pub const FIRST_DIRECT_DATA_FRAME_SEQ: u64 = 4;
/// The largest V1 protobuf integer accepted by platform runtimes.
pub const MAX_DIRECT_FRAME_SEQ: u64 = i64::MAX as u64;
/// Production QUIC flow-control budget. Upper layers remain responsible for
/// bounding their own queues by both frame count and byte count.
pub const DIRECT_FLOW_WINDOW_BYTES: u32 = 8 * 1024 * 1024;

const DIRECT_CLOSE_CODE: u32 = 16;
const DIRECT_PROTOCOL_VERSION: u64 = 1;
const MAX_LIVENESS_BODY_BYTES: usize = 40;
const MAX_RECEIPT_BODY_BYTES: usize = 16;
const INITIAL_LIVENESS_NONCE_DOMAIN: &[u8] = b"TERMLINK-DIRECT-INITIAL-LIVENESS-V1\0";

/// A production endpoint is deliberately separate from `ShadowEndpoint`, so
/// increasing the data-plane flow window cannot change ABI-v4 shadow probes.
pub struct DirectEndpoint {
    endpoint: Endpoint,
    local_addr: SocketAddr,
    expected_remote: SocketAddr,
    certificate_sha256: [u8; 32],
    certificate_der: Option<rustls::pki_types::CertificateDer<'static>>,
}

impl DirectEndpoint {
    pub fn server(
        handoff: QuicSocketHandoff,
        expected_remote: SocketAddr,
        identity: TemporaryIdentity,
        pinned_client_certificate_sha256: [u8; 32],
    ) -> Result<Self, QuicError> {
        validate_candidate_endpoint(expected_remote).map_err(|_| QuicError::InvalidCandidate)?;
        let original_addr = handoff.original_addr();
        let certificate = identity.certificate_der();
        let certificate_sha256 = identity.certificate_sha256();
        let server_config = direct_server_config(identity, pinned_client_certificate_sha256)?;
        let socket = handoff.into_std_socket()?;
        let runtime: Arc<dyn quinn::Runtime> = Arc::new(quinn::TokioRuntime);
        let socket = PortableSingleDatagramSocket::wrap(socket, expected_remote)?;
        let endpoint = Endpoint::new_with_abstract_socket(
            endpoint_config()?,
            Some(server_config),
            socket,
            runtime,
        )?;
        require_same_socket(original_addr, endpoint.local_addr()?)?;
        Ok(Self {
            endpoint,
            local_addr: original_addr,
            expected_remote,
            certificate_sha256,
            certificate_der: Some(certificate),
        })
    }

    pub fn client(
        handoff: QuicSocketHandoff,
        expected_remote: SocketAddr,
        identity: TemporaryIdentity,
        pinned_server_certificate_sha256: [u8; 32],
    ) -> Result<Self, QuicError> {
        validate_candidate_endpoint(expected_remote).map_err(|_| QuicError::InvalidCandidate)?;
        let original_addr = handoff.original_addr();
        let certificate_sha256 = identity.certificate_sha256();
        let socket = handoff.into_std_socket()?;
        let runtime: Arc<dyn quinn::Runtime> = Arc::new(quinn::TokioRuntime);
        let socket = PortableSingleDatagramSocket::wrap(socket, expected_remote)?;
        let endpoint =
            Endpoint::new_with_abstract_socket(endpoint_config()?, None, socket, runtime)?;
        require_same_socket(original_addr, endpoint.local_addr()?)?;
        let mut direct = Self {
            endpoint,
            local_addr: original_addr,
            expected_remote,
            certificate_sha256,
            certificate_der: None,
        };
        direct
            .endpoint
            .set_default_client_config(direct_client_config(
                identity,
                pinned_server_certificate_sha256,
            )?);
        Ok(direct)
    }

    pub fn local_addr(&self) -> SocketAddr {
        self.local_addr
    }

    pub fn certificate_sha256(&self) -> [u8; 32] {
        self.certificate_sha256
    }

    pub fn certificate_der(&self) -> Option<rustls::pki_types::CertificateDer<'static>> {
        self.certificate_der.clone()
    }

    pub async fn accept_pinned(
        &self,
        expected_remote: SocketAddr,
        expected_certificate_sha256: [u8; 32],
    ) -> Result<Connection, QuicError> {
        if expected_remote != self.expected_remote {
            return Err(QuicError::InvalidCandidate);
        }
        let incoming = tokio::time::timeout(SHADOW_OPERATION_TIMEOUT, self.endpoint.accept())
            .await
            .map_err(|_| QuicError::Timeout)?
            .ok_or(QuicError::EndpointClosed)?;
        let connection = tokio::time::timeout(SHADOW_OPERATION_TIMEOUT, incoming)
            .await
            .map_err(|_| QuicError::Timeout)?
            .map_err(QuicError::Connection)?;
        if connection.remote_address() != expected_remote {
            connection.close(VarInt::from_u32(3), b"unexpected-remote-endpoint");
            return Err(QuicError::UnexpectedRemote);
        }
        verify_peer_certificate(&connection, expected_certificate_sha256)?;
        Ok(connection)
    }

    pub async fn connect_pinned(
        &self,
        remote: SocketAddr,
        expected_certificate_sha256: [u8; 32],
    ) -> Result<Connection, QuicError> {
        validate_candidate_endpoint(remote).map_err(|_| QuicError::InvalidCandidate)?;
        if remote != self.expected_remote {
            return Err(QuicError::InvalidCandidate);
        }
        let connecting = self.endpoint.connect(remote, "termlink-direct.invalid")?;
        let connection = tokio::time::timeout(SHADOW_OPERATION_TIMEOUT, connecting)
            .await
            .map_err(|_| QuicError::Timeout)?
            .map_err(QuicError::Connection)?;
        verify_peer_certificate(&connection, expected_certificate_sha256)?;
        Ok(connection)
    }

    pub fn close(&self) {
        self.endpoint.close(
            VarInt::from_u32(DIRECT_CLOSE_CODE),
            b"direct-endpoint-retired",
        );
    }
}

fn direct_transport_config() -> Arc<TransportConfig> {
    let mut config = TransportConfig::default();
    config
        .initial_mtu(QUIC_MTU)
        .min_mtu(QUIC_MTU)
        .mtu_discovery_config(None)
        // Peer proof, result, and all production data share one reliable
        // bidirectional stream. No additional application stream is accepted.
        .max_concurrent_bidi_streams(1_u8.into())
        .max_concurrent_uni_streams(0_u8.into())
        .stream_receive_window(VarInt::from_u32(DIRECT_FLOW_WINDOW_BYTES))
        .receive_window(VarInt::from_u32(DIRECT_FLOW_WINDOW_BYTES))
        .send_window(u64::from(DIRECT_FLOW_WINDOW_BYTES))
        .max_idle_timeout(Some(VarInt::from_u32(60_000).into()))
        .keep_alive_interval(None);
    Arc::new(config)
}

fn direct_server_config(
    identity: TemporaryIdentity,
    pinned_client_certificate_sha256: [u8; 32],
) -> Result<ServerConfig, QuicError> {
    server_config_with_transport(
        identity,
        pinned_client_certificate_sha256,
        direct_transport_config(),
    )
}

fn direct_client_config(
    identity: TemporaryIdentity,
    pinned_server_certificate_sha256: [u8; 32],
) -> Result<ClientConfig, QuicError> {
    client_config_with_transport(
        identity,
        pinned_server_certificate_sha256,
        direct_transport_config(),
    )
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DirectTransportReceiptV1 {
    pub highest_contiguous_frame_seq: u64,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DirectPingV1 {
    pub nonce: [u8; 16],
    pub sent_at_ms: i64,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DirectPongV1 {
    pub nonce: [u8; 16],
    pub received_at_ms: i64,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum DirectFrameBody {
    /// Exact serialized `termlink.realtime.v1.Envelope` bytes. This crate does
    /// not inspect, normalize, or reserialize the value.
    RealtimeEnvelope(Vec<u8>),
    TransportReceipt(DirectTransportReceiptV1),
    Ping(DirectPingV1),
    Pong(DirectPongV1),
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DirectPathFrame {
    pub frame_seq: u64,
    pub body: DirectFrameBody,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ReceivedDirectFrame {
    pub frame_seq: u64,
    pub body: DirectFrameBody,
}

impl ReceivedDirectFrame {
    /// Only opaque data consumes retransmission-buffer credit. Receipts never
    /// solicit another receipt, preventing transport receipt loops.
    pub fn needs_transport_receipt(&self) -> bool {
        matches!(self.body, DirectFrameBody::RealtimeEnvelope(_))
    }
}

pub fn encode_direct_path_frame(
    binding: &PeerBindingContext,
    frame_seq: u64,
    body: &DirectFrameBody,
) -> Result<Vec<u8>, QuicError> {
    validate_binding_and_sequence(binding, frame_seq)?;
    let (field, encoded_body) = match body {
        DirectFrameBody::RealtimeEnvelope(envelope) => {
            if envelope.is_empty() || envelope.len() > MAX_REALTIME_ENVELOPE_BYTES {
                return Err(QuicError::InvalidDirectFrame);
            }
            (12, envelope.clone())
        }
        DirectFrameBody::TransportReceipt(receipt) => {
            validate_receipt(receipt)?;
            let mut encoded = Vec::with_capacity(MAX_RECEIPT_BODY_BYTES);
            put_varint_field(1, receipt.highest_contiguous_frame_seq, &mut encoded);
            (13, encoded)
        }
        DirectFrameBody::Ping(ping) => {
            validate_timestamp(ping.sent_at_ms)?;
            (14, encode_liveness_body(ping.nonce, ping.sent_at_ms))
        }
        DirectFrameBody::Pong(pong) => {
            validate_timestamp(pong.received_at_ms)?;
            (15, encode_liveness_body(pong.nonce, pong.received_at_ms))
        }
    };

    let mut encoded = Vec::with_capacity(encoded_body.len() + 64);
    put_varint_field(1, DIRECT_PROTOCOL_VERSION, &mut encoded);
    put_bytes_field(2, &binding.connection_id, &mut encoded);
    put_varint_field(3, binding.path_epoch, &mut encoded);
    put_varint_field(4, frame_seq, &mut encoded);
    put_bytes_field(field, &encoded_body, &mut encoded);
    if encoded.len() + 4 > MAX_DIRECT_DATA_FRAME_WIRE_BYTES {
        return Err(QuicError::InvalidDirectFrame);
    }
    let mut framed = Vec::with_capacity(encoded.len() + 4);
    framed.extend_from_slice(&(encoded.len() as u32).to_be_bytes());
    framed.extend_from_slice(&encoded);
    Ok(framed)
}

pub fn decode_direct_path_frame(
    wire: &[u8],
    binding: &PeerBindingContext,
) -> Result<DirectPathFrame, QuicError> {
    if wire.len() < 5 || wire.len() > MAX_DIRECT_DATA_FRAME_WIRE_BYTES {
        return Err(QuicError::InvalidDirectFrame);
    }
    let declared = u32::from_be_bytes(
        wire[..4]
            .try_into()
            .map_err(|_| QuicError::InvalidDirectFrame)?,
    ) as usize;
    if declared != wire.len() - 4 {
        return Err(QuicError::InvalidDirectFrame);
    }

    let mut cursor = DirectCursor::new(&wire[4..]);
    let protocol_version = cursor.required_varint(1)?;
    let connection_id: [u8; 16] = cursor
        .required_bytes(2, 16)?
        .try_into()
        .map_err(|_| QuicError::InvalidDirectFrame)?;
    let path_epoch = cursor.required_varint(3)?;
    let frame_seq = cursor.required_varint(4)?;
    let (body_field, body_bytes) = cursor.required_body()?;
    cursor.finish()?;

    if protocol_version != DIRECT_PROTOCOL_VERSION
        || connection_id != binding.connection_id
        || path_epoch != binding.path_epoch
    {
        return Err(QuicError::InvalidDirectFrame);
    }
    validate_binding_and_sequence(binding, frame_seq)?;
    let body = match body_field {
        12 => {
            if body_bytes.is_empty() || body_bytes.len() > MAX_REALTIME_ENVELOPE_BYTES {
                return Err(QuicError::InvalidDirectFrame);
            }
            DirectFrameBody::RealtimeEnvelope(body_bytes.to_vec())
        }
        13 => DirectFrameBody::TransportReceipt(decode_receipt(body_bytes)?),
        14 => {
            let (nonce, timestamp_ms) = decode_liveness_body(body_bytes)?;
            DirectFrameBody::Ping(DirectPingV1 {
                nonce,
                sent_at_ms: timestamp_ms,
            })
        }
        15 => {
            let (nonce, timestamp_ms) = decode_liveness_body(body_bytes)?;
            DirectFrameBody::Pong(DirectPongV1 {
                nonce,
                received_at_ms: timestamp_ms,
            })
        }
        _ => return Err(QuicError::InvalidDirectFrame),
    };
    Ok(DirectPathFrame { frame_seq, body })
}

fn validate_binding_and_sequence(
    binding: &PeerBindingContext,
    frame_seq: u64,
) -> Result<(), QuicError> {
    if binding.path_epoch == 0
        || binding.path_epoch > MAX_DIRECT_FRAME_SEQ
        || !(INITIAL_DIRECT_LIVENESS_FRAME_SEQ..=MAX_DIRECT_FRAME_SEQ).contains(&frame_seq)
    {
        return Err(QuicError::InvalidDirectSequence);
    }
    Ok(())
}

fn validate_receipt(receipt: &DirectTransportReceiptV1) -> Result<(), QuicError> {
    if !(1..=MAX_DIRECT_FRAME_SEQ).contains(&receipt.highest_contiguous_frame_seq) {
        return Err(QuicError::InvalidDirectReceipt);
    }
    Ok(())
}

fn validate_timestamp(timestamp_ms: i64) -> Result<(), QuicError> {
    if timestamp_ms <= 0 {
        return Err(QuicError::InvalidDirectFrame);
    }
    Ok(())
}

fn encode_liveness_body(nonce: [u8; 16], timestamp_ms: i64) -> Vec<u8> {
    let mut encoded = Vec::with_capacity(32);
    put_bytes_field(1, &nonce, &mut encoded);
    put_varint_field(2, timestamp_ms as u64, &mut encoded);
    encoded
}

fn decode_liveness_body(bytes: &[u8]) -> Result<([u8; 16], i64), QuicError> {
    if bytes.len() > MAX_LIVENESS_BODY_BYTES {
        return Err(QuicError::InvalidDirectFrame);
    }
    let mut cursor = DirectCursor::new(bytes);
    let nonce = cursor
        .required_bytes(1, 16)?
        .try_into()
        .map_err(|_| QuicError::InvalidDirectFrame)?;
    let timestamp = cursor.required_varint(2)?;
    cursor.finish()?;
    if timestamp == 0 || timestamp > i64::MAX as u64 {
        return Err(QuicError::InvalidDirectFrame);
    }
    Ok((nonce, timestamp as i64))
}

fn decode_receipt(bytes: &[u8]) -> Result<DirectTransportReceiptV1, QuicError> {
    if bytes.len() > MAX_RECEIPT_BODY_BYTES {
        return Err(QuicError::InvalidDirectReceipt);
    }
    let mut cursor = DirectCursor::new(bytes);
    let receipt = DirectTransportReceiptV1 {
        highest_contiguous_frame_seq: cursor.required_varint(1)?,
    };
    // Any field 2 range, including an empty or zero-valued range message, is
    // rejected for V1. Because fields are strictly ordered, finish also rejects
    // duplicate field 1 and every unknown field.
    cursor
        .finish()
        .map_err(|_| QuicError::InvalidDirectReceipt)?;
    validate_receipt(&receipt)?;
    Ok(receipt)
}

struct DirectCursor<'a> {
    bytes: &'a [u8],
    offset: usize,
    last_field: u64,
}

impl<'a> DirectCursor<'a> {
    fn new(bytes: &'a [u8]) -> Self {
        Self {
            bytes,
            offset: 0,
            last_field: 0,
        }
    }

    fn required_varint(&mut self, field: u64) -> Result<u64, QuicError> {
        self.require_key(field, 0)?;
        self.varint()
    }

    fn required_bytes(&mut self, field: u64, maximum: usize) -> Result<&'a [u8], QuicError> {
        self.require_key(field, 2)?;
        self.bytes(maximum)
    }

    fn required_body(&mut self) -> Result<(u64, &'a [u8]), QuicError> {
        let key = self.varint()?;
        let field = key >> 3;
        if !(12..=15).contains(&field) || key & 7 != 2 || field <= self.last_field {
            return Err(QuicError::InvalidDirectFrame);
        }
        self.last_field = field;
        let body = self.bytes(MAX_REALTIME_ENVELOPE_BYTES)?;
        Ok((field, body))
    }

    fn bytes(&mut self, maximum: usize) -> Result<&'a [u8], QuicError> {
        let length = usize::try_from(self.varint()?).map_err(|_| QuicError::InvalidDirectFrame)?;
        if length > maximum {
            return Err(QuicError::InvalidDirectFrame);
        }
        let end = self
            .offset
            .checked_add(length)
            .ok_or(QuicError::InvalidDirectFrame)?;
        let value = self
            .bytes
            .get(self.offset..end)
            .ok_or(QuicError::InvalidDirectFrame)?;
        self.offset = end;
        Ok(value)
    }

    fn require_key(&mut self, field: u64, wire_type: u64) -> Result<(), QuicError> {
        let key = self.varint()?;
        if key >> 3 != field || key & 7 != wire_type || field <= self.last_field {
            return Err(QuicError::InvalidDirectFrame);
        }
        self.last_field = field;
        Ok(())
    }

    fn varint(&mut self) -> Result<u64, QuicError> {
        let start = self.offset;
        let mut value = 0_u64;
        for shift in (0..70).step_by(7) {
            let byte = *self
                .bytes
                .get(self.offset)
                .ok_or(QuicError::InvalidDirectFrame)?;
            self.offset += 1;
            if shift == 63 && byte > 1 {
                return Err(QuicError::InvalidDirectFrame);
            }
            value |= u64::from(byte & 0x7f) << shift;
            if byte & 0x80 == 0 {
                if self.offset - start != direct_varint_len(value) {
                    return Err(QuicError::InvalidDirectFrame);
                }
                return Ok(value);
            }
        }
        Err(QuicError::InvalidDirectFrame)
    }

    fn finish(&self) -> Result<(), QuicError> {
        if self.offset == self.bytes.len() {
            Ok(())
        } else {
            Err(QuicError::InvalidDirectFrame)
        }
    }
}

fn direct_varint_len(mut value: u64) -> usize {
    let mut length = 1;
    while value >= 0x80 {
        value >>= 7;
        length += 1;
    }
    length
}

struct DirectShared {
    endpoint: Endpoint,
    connection: Connection,
    binding: PeerBindingContext,
    cancelled: AtomicBool,
    cancellation: watch::Sender<bool>,
    highest_sent: AtomicU64,
    highest_received: AtomicU64,
}

impl DirectShared {
    fn retire(&self, reason: &'static [u8]) {
        if !self.cancelled.swap(true, Ordering::AcqRel) {
            // Wake cancellable reads/writes before QUIC reports its own close,
            // so an explicit local cancellation has deterministic semantics.
            self.cancellation.send_replace(true);
            self.connection
                .close(VarInt::from_u32(DIRECT_CLOSE_CODE), reason);
            self.endpoint
                .close(VarInt::from_u32(DIRECT_CLOSE_CODE), reason);
        }
    }
}

#[derive(Clone)]
pub struct DirectConnectionControl {
    shared: Arc<DirectShared>,
}

impl DirectConnectionControl {
    pub fn binding(&self) -> PeerBindingContext {
        self.shared.binding.clone()
    }

    pub fn is_cancelled(&self) -> bool {
        self.shared.cancelled.load(Ordering::Acquire)
    }

    pub fn cancel(&self) {
        self.shared.retire(b"direct-cancelled");
    }

    /// A nominated UDP path cannot survive an interface-generation change.
    /// This retires only Direct; Relay ownership remains outside this crate.
    pub fn network_changed(&self) {
        self.shared.retire(b"direct-network-changed");
    }

    pub async fn cancelled(&self) {
        let mut receiver = self.shared.cancellation.subscribe();
        if *receiver.borrow_and_update() {
            return;
        }
        while receiver.changed().await.is_ok() {
            if *receiver.borrow_and_update() {
                return;
            }
        }
    }

    pub fn highest_sent_frame_seq(&self) -> u64 {
        self.shared.highest_sent.load(Ordering::Acquire)
    }

    pub fn highest_received_frame_seq(&self) -> u64 {
        self.shared.highest_received.load(Ordering::Acquire)
    }

    /// Applies a liveness deadline to this exact Direct generation. Expiry
    /// closes the Direct endpoint/connection and wakes blocked stream I/O.
    pub fn enforce_liveness(
        &self,
        tracker: &DirectLivenessTracker,
        now: Instant,
    ) -> Result<(), QuicError> {
        if let Err(error) = tracker.check_timeout(now) {
            self.shared.retire(b"direct-liveness-timeout");
            Err(error)
        } else {
            Ok(())
        }
    }
}

pub struct AuthenticatedDirectConnection {
    send: quinn::SendStream,
    receive: quinn::RecvStream,
    control: DirectConnectionControl,
    tls_exporter_sha256: [u8; 32],
    peer_proof_sha256: [u8; 32],
}

impl AuthenticatedDirectConnection {
    pub fn tls_exporter_sha256(&self) -> [u8; 32] {
        self.tls_exporter_sha256
    }

    pub fn peer_proof_sha256(&self) -> [u8; 32] {
        self.peer_proof_sha256
    }

    pub fn control(&self) -> DirectConnectionControl {
        self.control.clone()
    }

    /// Splits the sole authenticated stream. Both directions begin at sequence
    /// 4 because proof/result consume sequences 1/2 and the mutual initial
    /// ping/pong liveness exchange consumes sequence 3 in each direction.
    pub fn into_parts(self) -> (DirectDataWriter, DirectDataReader, DirectConnectionControl) {
        let control = self.control;
        (
            DirectDataWriter {
                send: self.send,
                control: control.clone(),
                next_sequence: FIRST_DIRECT_DATA_FRAME_SEQ,
            },
            DirectDataReader {
                receive: self.receive,
                control: control.clone(),
                expected_sequence: FIRST_DIRECT_DATA_FRAME_SEQ,
            },
            control,
        )
    }
}

pub struct DirectDataWriter {
    send: quinn::SendStream,
    control: DirectConnectionControl,
    next_sequence: u64,
}

impl DirectDataWriter {
    pub fn next_frame_seq(&self) -> u64 {
        self.next_sequence
    }

    pub async fn send(&mut self, body: DirectFrameBody) -> Result<u64, QuicError> {
        if self.control.is_cancelled() {
            return Err(QuicError::Cancelled);
        }
        if let DirectFrameBody::TransportReceipt(receipt) = &body
            && receipt.highest_contiguous_frame_seq
                > self.control.shared.highest_received.load(Ordering::Acquire)
        {
            self.control.shared.retire(b"invalid-direct-receipt");
            return Err(QuicError::InvalidDirectReceipt);
        }
        let sequence = self.next_sequence;
        let wire = encode_direct_path_frame(&self.control.shared.binding, sequence, &body)?;
        let write_result = tokio::select! {
            biased;
            () = self.control.cancelled() => Err(QuicError::Cancelled),
            result = self.send.write_all(&wire) => result.map_err(QuicError::Write),
        };
        if let Err(error) = write_result {
            self.control.shared.retire(b"direct-write-failed");
            return Err(error);
        }
        self.control
            .shared
            .highest_sent
            .store(sequence, Ordering::Release);
        self.next_sequence = sequence.checked_add(1).ok_or_else(|| {
            self.control.shared.retire(b"direct-sequence-exhausted");
            QuicError::InvalidDirectSequence
        })?;
        if self.next_sequence > MAX_DIRECT_FRAME_SEQ {
            self.control.shared.retire(b"direct-sequence-exhausted");
        }
        Ok(sequence)
    }
}

pub struct DirectDataReader {
    receive: quinn::RecvStream,
    control: DirectConnectionControl,
    expected_sequence: u64,
}

impl DirectDataReader {
    pub fn expected_frame_seq(&self) -> u64 {
        self.expected_sequence
    }

    pub async fn receive(&mut self) -> Result<ReceivedDirectFrame, QuicError> {
        if self.control.is_cancelled() {
            return Err(QuicError::Cancelled);
        }
        let wire = match read_production_frame(&mut self.receive, &self.control).await {
            Ok(wire) => wire,
            Err(error) => {
                self.control.shared.retire(b"direct-read-failed");
                return Err(error);
            }
        };
        let frame = match decode_direct_path_frame(&wire, &self.control.shared.binding) {
            Ok(frame) => frame,
            Err(error) => {
                self.control.shared.retire(b"invalid-direct-frame");
                return Err(error);
            }
        };
        if let Err(error) = validate_incoming_sequence(self.expected_sequence, frame.frame_seq) {
            self.control.shared.retire(b"invalid-direct-sequence");
            return Err(error);
        }
        if let DirectFrameBody::TransportReceipt(receipt) = &frame.body
            && let Err(error) = validate_receipt_upper_bound(
                receipt,
                self.control.shared.highest_sent.load(Ordering::Acquire),
            )
        {
            self.control.shared.retire(b"invalid-direct-receipt");
            return Err(error);
        }
        self.control
            .shared
            .highest_received
            .store(frame.frame_seq, Ordering::Release);
        self.expected_sequence = frame.frame_seq.checked_add(1).ok_or_else(|| {
            self.control.shared.retire(b"direct-sequence-exhausted");
            QuicError::InvalidDirectSequence
        })?;
        if self.expected_sequence > MAX_DIRECT_FRAME_SEQ {
            self.control.shared.retire(b"direct-sequence-exhausted");
        }
        Ok(ReceivedDirectFrame {
            frame_seq: frame.frame_seq,
            body: frame.body,
        })
    }
}

fn validate_incoming_sequence(expected: u64, actual: u64) -> Result<(), QuicError> {
    if actual != expected {
        Err(QuicError::InvalidDirectSequence)
    } else {
        Ok(())
    }
}

fn validate_receipt_upper_bound(
    receipt: &DirectTransportReceiptV1,
    highest_sent: u64,
) -> Result<(), QuicError> {
    if receipt.highest_contiguous_frame_seq > highest_sent {
        Err(QuicError::InvalidDirectReceipt)
    } else {
        Ok(())
    }
}

async fn read_production_frame(
    receive: &mut quinn::RecvStream,
    control: &DirectConnectionControl,
) -> Result<Vec<u8>, QuicError> {
    let mut length_bytes = [0_u8; 4];
    tokio::select! {
        biased;
        () = control.cancelled() => return Err(QuicError::Cancelled),
        result = receive.read_exact(&mut length_bytes) => result?,
    }
    let length = u32::from_be_bytes(length_bytes) as usize;
    if length == 0 || length + 4 > MAX_DIRECT_DATA_FRAME_WIRE_BYTES {
        return Err(QuicError::InvalidDirectFrame);
    }
    let mut wire = vec![0_u8; length + 4];
    wire[..4].copy_from_slice(&length_bytes);
    tokio::select! {
        biased;
        () = control.cancelled() => return Err(QuicError::Cancelled),
        result = receive.read_exact(&mut wire[4..]) => result?,
    }
    Ok(wire)
}

/// Exchanges the canonical proof/result records on the same stream that will
/// carry production data. Unlike ABI-v4 shadow authentication, neither stream
/// direction is finished and the endpoint is retained until explicit cancel,
/// peer close, malformed data, or liveness retirement.
#[allow(clippy::too_many_arguments)]
pub async fn exchange_and_authenticate_direct_connection(
    endpoint: &DirectEndpoint,
    connection: Connection,
    binding: &PeerBindingContext,
    expected_peer: ExpectedPeer,
    ticket: &[u8],
    local_proof: &PeerProofRequest,
    local_signature: &[u8],
    verifier: &dyn PlatformPeerProofVerifier,
) -> Result<AuthenticatedDirectConnection, QuicError> {
    if local_signature.is_empty()
        || local_signature.len() > MAX_PEER_SIGNATURE_BYTES
        || ticket.is_empty()
        || ticket.len() > super::MAX_DIRECT_TICKET_WIRE_BYTES
        || sha256(ticket) != binding.ticket_sha256
        || local_proof.ticket_sha256 != binding.ticket_sha256
        || local_proof.connection_id != binding.connection_id
        || local_proof.path_epoch != binding.path_epoch
        || local_proof.role == expected_peer.role
        || local_proof.device_id != expected_peer.local_device_id
        || local_proof.peer_device_id != expected_peer.device_id
    {
        connection.close(VarInt::from_u32(DIRECT_CLOSE_CODE), b"invalid-local-proof");
        endpoint.close();
        return Err(QuicError::InvalidPeerProof);
    }

    let local_record = match encode_peer_proof_frame(ticket, local_proof, local_signature) {
        Ok(record) => record,
        Err(error) => {
            connection.close(VarInt::from_u32(DIRECT_CLOSE_CODE), b"invalid-local-proof");
            endpoint.close();
            return Err(error);
        }
    };
    let handshake = tokio::time::timeout(SHADOW_OPERATION_TIMEOUT, async {
        let (mut send, mut receive) = if local_proof.role == PeerRole::Initiator {
            connection.open_bi().await?
        } else {
            connection.accept_bi().await?
        };
        let remote_record = if local_proof.role == PeerRole::Initiator {
            send.write_all(&local_record).await?;
            super::read_direct_frame(&mut receive).await?
        } else {
            let remote = super::read_direct_frame(&mut receive).await?;
            send.write_all(&local_record).await?;
            remote
        };
        let (remote_proof, remote_signature) =
            decode_peer_proof_frame(&remote_record, binding, &expected_peer)?;
        let authenticated = authenticate_shadow_connection(
            connection.clone(),
            binding,
            expected_peer,
            &remote_proof,
            &remote_signature,
            verifier,
        )?;

        let remote_transcript_sha256 = sha256(&remote_proof.transcript()?);
        let local_result = encode_peer_proof_result_frame(binding, remote_transcript_sha256)?;
        let peer_result = if local_proof.role == PeerRole::Initiator {
            send.write_all(&local_result).await?;
            super::read_direct_frame(&mut receive).await?
        } else {
            let remote = super::read_direct_frame(&mut receive).await?;
            send.write_all(&local_result).await?;
            remote
        };
        let local_transcript_sha256 = sha256(&local_proof.transcript()?);
        decode_peer_proof_result_frame(&peer_result, binding, local_transcript_sha256)?;

        // Production keeps the proof stream alive. Sequence 3 in each direction
        // is a mutually observed ping/pong before the stream becomes available
        // to upper layers. The nonce is exporter-bound and unique to this QUIC
        // handshake without introducing platform RNG into this crate.
        let initial_nonce = initial_liveness_nonce(authenticated.tls_exporter_sha256, binding);
        if local_proof.role == PeerRole::Initiator {
            let ping = encode_direct_path_frame(
                binding,
                INITIAL_DIRECT_LIVENESS_FRAME_SEQ,
                &DirectFrameBody::Ping(DirectPingV1 {
                    nonce: initial_nonce,
                    sent_at_ms: direct_now_ms(),
                }),
            )?;
            send.write_all(&ping).await?;
            let pong_wire = read_production_frame_uncancelled(&mut receive).await?;
            let pong = decode_direct_path_frame(&pong_wire, binding)?;
            match pong {
                DirectPathFrame {
                    frame_seq: INITIAL_DIRECT_LIVENESS_FRAME_SEQ,
                    body: DirectFrameBody::Pong(pong),
                } if pong.nonce == initial_nonce => {}
                _ => return Err(QuicError::UnexpectedPong),
            }
        } else {
            let ping_wire = read_production_frame_uncancelled(&mut receive).await?;
            let ping = decode_direct_path_frame(&ping_wire, binding)?;
            let DirectPathFrame {
                frame_seq: INITIAL_DIRECT_LIVENESS_FRAME_SEQ,
                body: DirectFrameBody::Ping(ping),
            } = ping
            else {
                return Err(QuicError::InvalidDirectSequence);
            };
            let pong = encode_direct_path_frame(
                binding,
                INITIAL_DIRECT_LIVENESS_FRAME_SEQ,
                &DirectFrameBody::Pong(DirectPongV1 {
                    nonce: ping.nonce,
                    received_at_ms: direct_now_ms(),
                }),
            )?;
            send.write_all(&pong).await?;
        }
        Ok::<_, QuicError>((send, receive, authenticated))
    })
    .await;

    let handshake = match handshake {
        Ok(result) => result,
        Err(_) => {
            connection.close(VarInt::from_u32(DIRECT_CLOSE_CODE), b"direct-auth-timeout");
            endpoint.close();
            return Err(QuicError::Timeout);
        }
    };

    let (send, receive, authenticated) = match handshake {
        Ok(value) => value,
        Err(error) => {
            connection.close(VarInt::from_u32(DIRECT_CLOSE_CODE), b"direct-auth-failed");
            endpoint.close();
            return Err(error);
        }
    };
    let (cancellation, _) = watch::channel(false);
    let shared = Arc::new(DirectShared {
        endpoint: endpoint.endpoint.clone(),
        connection: authenticated.connection,
        binding: authenticated.binding,
        cancelled: AtomicBool::new(false),
        cancellation,
        highest_sent: AtomicU64::new(INITIAL_DIRECT_LIVENESS_FRAME_SEQ),
        highest_received: AtomicU64::new(INITIAL_DIRECT_LIVENESS_FRAME_SEQ),
    });
    Ok(AuthenticatedDirectConnection {
        send,
        receive,
        control: DirectConnectionControl { shared },
        tls_exporter_sha256: authenticated.tls_exporter_sha256,
        peer_proof_sha256: authenticated.peer_proof_sha256,
    })
}

fn initial_liveness_nonce(tls_exporter_sha256: [u8; 32], binding: &PeerBindingContext) -> [u8; 16] {
    let mut input = Vec::with_capacity(INITIAL_LIVENESS_NONCE_DOMAIN.len() + 56);
    input.extend_from_slice(INITIAL_LIVENESS_NONCE_DOMAIN);
    input.extend_from_slice(&tls_exporter_sha256);
    input.extend_from_slice(&binding.connection_id);
    input.extend_from_slice(&binding.path_epoch.to_be_bytes());
    sha256(&input)[..16]
        .try_into()
        .expect("SHA-256 prefix has a fixed length")
}

async fn read_production_frame_uncancelled(
    receive: &mut quinn::RecvStream,
) -> Result<Vec<u8>, QuicError> {
    let mut length_bytes = [0_u8; 4];
    receive.read_exact(&mut length_bytes).await?;
    let length = u32::from_be_bytes(length_bytes) as usize;
    if length == 0 || length + 4 > MAX_DIRECT_DATA_FRAME_WIRE_BYTES {
        return Err(QuicError::InvalidDirectFrame);
    }
    let mut wire = vec![0_u8; length + 4];
    wire[..4].copy_from_slice(&length_bytes);
    receive.read_exact(&mut wire[4..]).await?;
    Ok(wire)
}

/// Deterministic keepalive state for the owner of a split direct connection.
/// The owner sends the returned `Ping`, passes received `Pong` values to
/// `observe_pong`, and retires the direct control when `check_timeout` fails.
pub struct DirectLivenessTracker {
    timeout: Duration,
    pending: Option<([u8; 16], Instant)>,
}

impl DirectLivenessTracker {
    pub fn new(timeout: Duration) -> Result<Self, QuicError> {
        if timeout.is_zero() {
            return Err(QuicError::LivenessTimeout);
        }
        Ok(Self {
            timeout,
            pending: None,
        })
    }

    pub fn begin_ping(
        &mut self,
        nonce: [u8; 16],
        sent_at_ms: i64,
        now: Instant,
    ) -> Result<DirectPingV1, QuicError> {
        self.check_timeout(now)?;
        if self.pending.is_some() {
            return Err(QuicError::UnexpectedPong);
        }
        validate_timestamp(sent_at_ms)?;
        self.pending = Some((nonce, now));
        Ok(DirectPingV1 { nonce, sent_at_ms })
    }

    pub fn observe_pong(&mut self, pong: &DirectPongV1, now: Instant) -> Result<(), QuicError> {
        self.check_timeout(now)?;
        match self.pending {
            Some((nonce, _)) if nonce == pong.nonce => {
                validate_timestamp(pong.received_at_ms)?;
                self.pending = None;
                Ok(())
            }
            _ => Err(QuicError::UnexpectedPong),
        }
    }

    pub fn check_timeout(&self, now: Instant) -> Result<(), QuicError> {
        if self
            .pending
            .is_some_and(|(_, started)| now.saturating_duration_since(started) >= self.timeout)
        {
            Err(QuicError::LivenessTimeout)
        } else {
            Ok(())
        }
    }

    pub fn has_pending_ping(&self) -> bool {
        self.pending.is_some()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tls_exporter_sha256;
    use std::net::IpAddr;
    use termlink_direct_nat::DirectUdpSocket;

    struct ExactVerifier(Vec<u8>);

    impl PlatformPeerProofVerifier for ExactVerifier {
        fn verify(&self, _device_id: [u8; 16], transcript: &[u8], signature: &[u8]) -> bool {
            !transcript.is_empty() && signature == self.0
        }
    }

    fn binding() -> PeerBindingContext {
        PeerBindingContext {
            ticket_sha256: [1; 32],
            connection_id: [2; 16],
            path_epoch: 3,
        }
    }

    #[test]
    fn opaque_envelope_round_trips_at_bounds() {
        for envelope in [vec![0xa0], vec![0xa5; MAX_REALTIME_ENVELOPE_BYTES]] {
            let frame = encode_direct_path_frame(
                &binding(),
                FIRST_DIRECT_DATA_FRAME_SEQ,
                &DirectFrameBody::RealtimeEnvelope(envelope.clone()),
            )
            .unwrap();
            assert!(frame.len() <= MAX_DIRECT_DATA_FRAME_WIRE_BYTES);
            let decoded = decode_direct_path_frame(&frame, &binding()).unwrap();
            assert_eq!(
                decoded,
                DirectPathFrame {
                    frame_seq: FIRST_DIRECT_DATA_FRAME_SEQ,
                    body: DirectFrameBody::RealtimeEnvelope(envelope),
                }
            );
        }
        assert!(matches!(
            encode_direct_path_frame(
                &binding(),
                FIRST_DIRECT_DATA_FRAME_SEQ,
                &DirectFrameBody::RealtimeEnvelope(Vec::new())
            ),
            Err(QuicError::InvalidDirectFrame)
        ));
        assert!(matches!(
            encode_direct_path_frame(
                &binding(),
                FIRST_DIRECT_DATA_FRAME_SEQ,
                &DirectFrameBody::RealtimeEnvelope(vec![0; MAX_REALTIME_ENVELOPE_BYTES + 1])
            ),
            Err(QuicError::InvalidDirectFrame)
        ));
    }

    #[test]
    fn opaque_unknown_envelope_bytes_are_never_normalized() {
        let envelope_with_unknown_fields = vec![0x08, 0x01, 0xfa, 0x07, 0x03, 0x80, 0x00, 0xff];
        let frame = encode_direct_path_frame(
            &binding(),
            3,
            &DirectFrameBody::RealtimeEnvelope(envelope_with_unknown_fields.clone()),
        )
        .unwrap();
        let decoded = decode_direct_path_frame(&frame, &binding()).unwrap();
        assert_eq!(
            decoded.body,
            DirectFrameBody::RealtimeEnvelope(envelope_with_unknown_fields)
        );
    }

    #[test]
    fn direct_outer_wire_rejects_truncation_unknown_duplicate_and_noncanonical_varint() {
        let canonical =
            encode_direct_path_frame(&binding(), 3, &DirectFrameBody::RealtimeEnvelope(vec![9]))
                .unwrap();

        let mut truncated = canonical.clone();
        truncated.pop();
        assert!(decode_direct_path_frame(&truncated, &binding()).is_err());

        let mut unknown = canonical.clone();
        unknown.extend_from_slice(&[0x80, 0x01, 0x01]); // field 16
        let body_len = (unknown.len() - 4) as u32;
        unknown[..4].copy_from_slice(&body_len.to_be_bytes());
        assert!(decode_direct_path_frame(&unknown, &binding()).is_err());

        let mut duplicate = canonical.clone();
        duplicate.extend_from_slice(&[0x62, 0x01, 0x09]); // second field 12
        let body_len = (duplicate.len() - 4) as u32;
        duplicate[..4].copy_from_slice(&body_len.to_be_bytes());
        assert!(decode_direct_path_frame(&duplicate, &binding()).is_err());

        let mut noncanonical = canonical;
        // protocol_version key is canonical 0x08; represent it as 0x88 0x00.
        noncanonical.splice(4..5, [0x88, 0x00]);
        let body_len = (noncanonical.len() - 4) as u32;
        noncanonical[..4].copy_from_slice(&body_len.to_be_bytes());
        assert!(decode_direct_path_frame(&noncanonical, &binding()).is_err());

        let mut oversized_prefix = vec![0_u8; 4];
        oversized_prefix.copy_from_slice(&(MAX_DIRECT_DATA_FRAME_WIRE_BYTES as u32).to_be_bytes());
        assert!(decode_direct_path_frame(&oversized_prefix, &binding()).is_err());

        let mut duplicate_header =
            encode_direct_path_frame(&binding(), 3, &DirectFrameBody::RealtimeEnvelope(vec![9]))
                .unwrap();
        duplicate_header.extend_from_slice(&[0x20, 0x03]);
        let body_len = (duplicate_header.len() - 4) as u32;
        duplicate_header[..4].copy_from_slice(&body_len.to_be_bytes());
        assert!(decode_direct_path_frame(&duplicate_header, &binding()).is_err());
    }

    #[test]
    fn direct_binding_and_sequence_are_strict() {
        let frame =
            encode_direct_path_frame(&binding(), 3, &DirectFrameBody::RealtimeEnvelope(vec![1]))
                .unwrap();
        let mut wrong_binding = binding();
        wrong_binding.connection_id[0] ^= 1;
        assert!(decode_direct_path_frame(&frame, &wrong_binding).is_err());
        assert!(matches!(
            encode_direct_path_frame(&binding(), 2, &DirectFrameBody::RealtimeEnvelope(vec![1])),
            Err(QuicError::InvalidDirectSequence)
        ));
        for actual in [3, 5, 2] {
            assert!(matches!(
                validate_incoming_sequence(4, actual),
                Err(QuicError::InvalidDirectSequence)
            ));
        }
        assert!(validate_incoming_sequence(4, 4).is_ok());
        assert!(matches!(
            encode_direct_path_frame(
                &binding(),
                MAX_DIRECT_FRAME_SEQ + 1,
                &DirectFrameBody::RealtimeEnvelope(vec![1])
            ),
            Err(QuicError::InvalidDirectSequence)
        ));
    }

    #[test]
    fn receipt_v1_has_no_missing_ranges_and_never_solicits_receipt() {
        let frame = encode_direct_path_frame(
            &binding(),
            3,
            &DirectFrameBody::TransportReceipt(DirectTransportReceiptV1 {
                highest_contiguous_frame_seq: 2,
            }),
        )
        .unwrap();
        let decoded = decode_direct_path_frame(&frame, &binding()).unwrap();
        let received = ReceivedDirectFrame {
            frame_seq: decoded.frame_seq,
            body: decoded.body,
        };
        assert!(!received.needs_transport_receipt());

        let mut with_missing = frame;
        // Replace nested receipt length 2/body 08 02 with length 4/body plus
        // an explicitly present, empty field-2 range.
        let receipt_key = with_missing.iter().position(|byte| *byte == 0x6a).unwrap();
        assert_eq!(with_missing[receipt_key + 1], 2);
        with_missing[receipt_key + 1] = 4;
        with_missing.splice(receipt_key + 4..receipt_key + 4, [0x12, 0x00]);
        let body_len = (with_missing.len() - 4) as u32;
        with_missing[..4].copy_from_slice(&body_len.to_be_bytes());
        assert!(matches!(
            decode_direct_path_frame(&with_missing, &binding()),
            Err(QuicError::InvalidDirectReceipt)
        ));
        assert!(matches!(
            validate_receipt_upper_bound(
                &DirectTransportReceiptV1 {
                    highest_contiguous_frame_seq: 5,
                },
                4,
            ),
            Err(QuicError::InvalidDirectReceipt)
        ));
    }

    #[test]
    fn ping_pong_tracker_rejects_wrong_nonce_and_timeout() {
        let start = Instant::now();
        let mut tracker = DirectLivenessTracker::new(Duration::from_secs(5)).unwrap();
        tracker.begin_ping([1; 16], 100, start).unwrap();
        assert!(matches!(
            tracker.observe_pong(
                &DirectPongV1 {
                    nonce: [2; 16],
                    received_at_ms: 101,
                },
                start + Duration::from_secs(1)
            ),
            Err(QuicError::UnexpectedPong)
        ));
        assert!(matches!(
            tracker.check_timeout(start + Duration::from_secs(5)),
            Err(QuicError::LivenessTimeout)
        ));
    }

    #[test]
    fn production_auth_keeps_one_stream_alive_and_starts_data_at_four() {
        test_runtime().block_on(async {
            let (client, server) = authenticated_pair().await;
            let (mut client_writer, mut client_reader, client_control) = client.into_parts();
            let (mut server_writer, mut server_reader, server_control) = server.into_parts();
            assert_eq!(client_writer.next_frame_seq(), 4);
            assert_eq!(client_reader.expected_frame_seq(), 4);
            assert_eq!(server_writer.next_frame_seq(), 4);
            assert_eq!(server_reader.expected_frame_seq(), 4);
            assert_eq!(client_control.highest_sent_frame_seq(), 3);
            assert_eq!(client_control.highest_received_frame_seq(), 3);

            let opaque = vec![0x08, 0x01, 0xfa, 0x07, 0x03, 0x80, 0x00, 0xff];
            assert_eq!(
                client_writer
                    .send(DirectFrameBody::RealtimeEnvelope(opaque.clone()))
                    .await
                    .unwrap(),
                4
            );
            let received = server_reader.receive().await.unwrap();
            assert_eq!(received.frame_seq, 4);
            assert_eq!(received.body, DirectFrameBody::RealtimeEnvelope(opaque));
            assert!(received.needs_transport_receipt());

            assert_eq!(
                server_writer
                    .send(DirectFrameBody::TransportReceipt(
                        DirectTransportReceiptV1 {
                            highest_contiguous_frame_seq: 4,
                        },
                    ))
                    .await
                    .unwrap(),
                4
            );
            let receipt = client_reader.receive().await.unwrap();
            assert!(!receipt.needs_transport_receipt());
            assert_eq!(client_control.highest_sent_frame_seq(), 4);
            assert_eq!(server_control.highest_received_frame_seq(), 4);
            let started = Instant::now();
            let mut liveness = DirectLivenessTracker::new(Duration::from_secs(1)).unwrap();
            liveness.begin_ping([7; 16], 200, started).unwrap();
            assert!(matches!(
                client_control.enforce_liveness(&liveness, started + Duration::from_secs(1)),
                Err(QuicError::LivenessTimeout)
            ));
            assert!(client_control.is_cancelled());
        });
    }

    #[test]
    fn blocked_read_is_cancelled_and_peer_close_exits_quickly() {
        test_runtime().block_on(async {
            let (client, server) = authenticated_pair().await;
            let (client_writer, mut client_reader, client_control) = client.into_parts();
            let (server_writer, mut server_reader, server_control) = server.into_parts();

            let waiting = tokio::spawn(async move { server_reader.receive().await });
            tokio::task::yield_now().await;
            server_control.cancel();
            let cancelled = tokio::time::timeout(Duration::from_secs(1), waiting)
                .await
                .expect("local cancellation must unblock the read")
                .unwrap();
            assert!(matches!(cancelled, Err(QuicError::Cancelled)));

            let peer_closed = tokio::time::timeout(Duration::from_secs(1), client_reader.receive())
                .await
                .expect("peer endpoint close must unblock the read");
            assert!(peer_closed.is_err());
            assert!(client_control.is_cancelled());
            drop((client_writer, server_writer));
        });
    }

    #[test]
    fn flow_control_blocked_writer_is_cancelled_without_waiting_for_peer() {
        test_runtime().block_on(async {
            let (client, _server) = authenticated_pair().await;
            let (mut writer, _reader, control) = client.into_parts();
            let sending = tokio::spawn(async move {
                loop {
                    writer
                        .send(DirectFrameBody::RealtimeEnvelope(vec![
                            0x5a;
                            MAX_REALTIME_ENVELOPE_BYTES
                        ]))
                        .await?;
                }
                #[allow(unreachable_code)]
                Ok::<(), QuicError>(())
            });
            tokio::time::sleep(Duration::from_millis(100)).await;
            control.cancel();
            let result = tokio::time::timeout(Duration::from_secs(1), sending)
                .await
                .expect("cancel must unblock a flow-controlled writer")
                .unwrap();
            assert!(result.is_err());
        });
    }

    async fn authenticated_pair() -> (AuthenticatedDirectConnection, AuthenticatedDirectConnection)
    {
        let ip = active_unicast_ip();
        let server_udp = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let client_udp = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let server_addr = server_udp.bound_addr();
        let client_addr = client_udp.bound_addr();
        let server_identity = TemporaryIdentity::generate().unwrap();
        let server_fingerprint = server_identity.certificate_sha256();
        let client_identity = TemporaryIdentity::generate().unwrap();
        let client_fingerprint = client_identity.certificate_sha256();
        let server = Arc::new(
            DirectEndpoint::server(
                server_udp.into_quic_handoff(),
                client_addr,
                server_identity,
                client_fingerprint,
            )
            .unwrap(),
        );
        let client = DirectEndpoint::client(
            client_udp.into_quic_handoff(),
            server_addr,
            client_identity,
            server_fingerprint,
        )
        .unwrap();
        let ticket = vec![0x08, 0x01, 0x12, 0x01, 0x07];
        let common_binding = PeerBindingContext {
            ticket_sha256: sha256(&ticket),
            connection_id: [12; 16],
            path_epoch: 13,
        };

        let server_task = {
            let endpoint = Arc::clone(&server);
            let binding = common_binding.clone();
            let ticket = ticket.clone();
            tokio::spawn(async move {
                let connection = endpoint
                    .accept_pinned(client_addr, client_fingerprint)
                    .await
                    .unwrap();
                let proof = PeerProofRequest {
                    ticket_sha256: binding.ticket_sha256,
                    role: PeerRole::Responder,
                    device_id: [22; 16],
                    peer_device_id: [21; 16],
                    connection_id: binding.connection_id,
                    path_epoch: binding.path_epoch,
                    proof_nonce: vec![23; 16],
                    tls_exporter_sha256: tls_exporter_sha256(&connection, &binding).unwrap(),
                };
                exchange_and_authenticate_direct_connection(
                    &endpoint,
                    connection,
                    &binding,
                    ExpectedPeer {
                        role: PeerRole::Initiator,
                        device_id: [21; 16],
                        local_device_id: [22; 16],
                    },
                    &ticket,
                    &proof,
                    &[8; 64],
                    &ExactVerifier(vec![8; 64]),
                )
                .await
                .unwrap()
            })
        };

        let connection = client
            .connect_pinned(server_addr, server_fingerprint)
            .await
            .unwrap();
        let proof = PeerProofRequest {
            ticket_sha256: common_binding.ticket_sha256,
            role: PeerRole::Initiator,
            device_id: [21; 16],
            peer_device_id: [22; 16],
            connection_id: common_binding.connection_id,
            path_epoch: common_binding.path_epoch,
            proof_nonce: vec![24; 16],
            tls_exporter_sha256: tls_exporter_sha256(&connection, &common_binding).unwrap(),
        };
        let authenticated_client = exchange_and_authenticate_direct_connection(
            &client,
            connection,
            &common_binding,
            ExpectedPeer {
                role: PeerRole::Responder,
                device_id: [22; 16],
                local_device_id: [21; 16],
            },
            &ticket,
            &proof,
            &[8; 64],
            &ExactVerifier(vec![8; 64]),
        )
        .await
        .unwrap();
        let authenticated_server = server_task.await.unwrap();
        (authenticated_client, authenticated_server)
    }

    fn test_runtime() -> tokio::runtime::Runtime {
        tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap()
    }

    fn active_unicast_ip() -> IpAddr {
        let socket = std::net::UdpSocket::bind("0.0.0.0:0").unwrap();
        socket.connect("192.0.2.1:9").unwrap();
        let ip = socket.local_addr().unwrap().ip();
        validate_candidate_endpoint(SocketAddr::new(ip, 49_152)).unwrap();
        ip
    }
}
