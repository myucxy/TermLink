use std::{
    fmt, io,
    io::IoSliceMut,
    net::{SocketAddr, UdpSocket},
    pin::Pin,
    sync::Arc,
    task::{Context, Poll},
    time::{Duration, SystemTime, UNIX_EPOCH},
};

use quinn::{
    AsyncUdpSocket, ClientConfig, Connection, Endpoint, EndpointConfig, ServerConfig,
    TransportConfig, UdpPoller, VarInt,
    crypto::rustls::{QuicClientConfig, QuicServerConfig},
    udp::{RecvMeta, Transmit},
};
use ring::digest;
use rustls::{
    DigitallySignedStruct, DistinguishedName, Error as RustlsError, SignatureScheme,
    client::{
        Resumption,
        danger::{HandshakeSignatureValid, ServerCertVerified, ServerCertVerifier},
    },
    pki_types::{CertificateDer, PrivatePkcs8KeyDer, ServerName, UnixTime},
    server::danger::{ClientCertVerified, ClientCertVerifier},
};
use termlink_direct_nat::{
    QuicSocketHandoff, STUN_HEADER_BYTES, STUN_MAGIC_COOKIE, is_retryable_udp_receive_error,
    validate_candidate_endpoint,
};

mod direct_data;

pub use direct_data::{
    AuthenticatedDirectConnection, DIRECT_FLOW_WINDOW_BYTES, DirectConnectionControl,
    DirectDataReader, DirectDataWriter, DirectEndpoint, DirectFrameBody, DirectLivenessTracker,
    DirectPathFrame, DirectPingV1, DirectPongV1, DirectTransportReceiptV1,
    FIRST_DIRECT_DATA_FRAME_SEQ, INITIAL_DIRECT_LIVENESS_FRAME_SEQ,
    MAX_DIRECT_DATA_FRAME_WIRE_BYTES, MAX_DIRECT_FRAME_SEQ, MAX_REALTIME_ENVELOPE_BYTES,
    ReceivedDirectFrame, decode_direct_path_frame, encode_direct_path_frame,
    exchange_and_authenticate_direct_connection,
};

pub const ALPN: &[u8] = b"termlink-direct/1";
pub const TLS_EXPORTER_LABEL: &[u8] = b"EXPORTER-TERMLINK-DIRECT-V1";
pub const QUIC_MTU: u16 = 1200;
pub const MAX_PING_BYTES: usize = 64;
pub const MAX_DIRECT_TICKET_WIRE_BYTES: usize = 2_048;
pub const MAX_PEER_PROOF_WIRE_BYTES: usize = 2_560;
pub const MAX_DIRECT_FRAME_WIRE_BYTES: usize = 2_640;
pub const MAX_PEER_SIGNATURE_BYTES: usize = 128;
pub const SHADOW_OPERATION_TIMEOUT: Duration = Duration::from_secs(5);
pub const PATH_BINDING_DOMAIN: &[u8] = b"TERMLINK-DIRECT-PATH-BINDING-V1\0";

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct SafeQuicPolicy {
    pub initial_mtu: u16,
    pub min_mtu: u16,
    pub mtu_discovery: bool,
    pub migration: bool,
    pub zero_rtt: bool,
    pub session_resumption: bool,
    pub portable_gso: bool,
    pub portable_ecn: bool,
}

impl Default for SafeQuicPolicy {
    fn default() -> Self {
        Self {
            initial_mtu: QUIC_MTU,
            min_mtu: QUIC_MTU,
            mtu_discovery: false,
            migration: false,
            zero_rtt: false,
            session_resumption: false,
            portable_gso: false,
            portable_ecn: false,
        }
    }
}

impl SafeQuicPolicy {
    pub fn validate(self) -> Result<Self, QuicError> {
        if self != Self::default() {
            return Err(QuicError::UnsafeConfiguration);
        }
        Ok(self)
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PeerProofRequest {
    pub ticket_sha256: [u8; 32],
    pub role: PeerRole,
    pub device_id: [u8; 16],
    pub peer_device_id: [u8; 16],
    pub connection_id: [u8; 16],
    pub path_epoch: u64,
    pub proof_nonce: Vec<u8>,
    pub tls_exporter_sha256: [u8; 32],
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PeerRole {
    Initiator,
    Responder,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ExpectedPeer {
    pub role: PeerRole,
    pub device_id: [u8; 16],
    pub local_device_id: [u8; 16],
}

impl PeerProofRequest {
    pub fn transcript(&self) -> Result<Vec<u8>, QuicError> {
        if self.path_epoch == 0
            || self.path_epoch > i64::MAX as u64
            || !(16..=32).contains(&self.proof_nonce.len())
            || self.device_id == self.peer_device_id
        {
            return Err(QuicError::InvalidPeerProof);
        }
        let mut bytes = Vec::with_capacity(157 + self.proof_nonce.len());
        bytes.extend_from_slice(b"TERMLINK-DIRECT-PEER-PROOF-V1\n");
        bytes.extend_from_slice(&self.ticket_sha256);
        bytes.push(match self.role {
            PeerRole::Initiator => 1,
            PeerRole::Responder => 2,
        });
        bytes.extend_from_slice(&self.device_id);
        bytes.extend_from_slice(&self.peer_device_id);
        bytes.extend_from_slice(&self.connection_id);
        bytes.extend_from_slice(&self.path_epoch.to_be_bytes());
        bytes.push(self.proof_nonce.len() as u8);
        bytes.extend_from_slice(&self.proof_nonce);
        bytes.extend_from_slice(&self.tls_exporter_sha256);
        Ok(bytes)
    }
}

pub trait PlatformPeerProofVerifier: Send + Sync {
    /// Verifies with the approved device public key. Private keys never enter Rust.
    fn verify(&self, device_id: [u8; 16], transcript: &[u8], signature: &[u8]) -> bool;
}

pub struct Ed25519PeerProofVerifier {
    public_key: [u8; 32],
}

impl Ed25519PeerProofVerifier {
    pub fn new(public_key: [u8; 32]) -> Self {
        Self { public_key }
    }
}

impl PlatformPeerProofVerifier for Ed25519PeerProofVerifier {
    fn verify(&self, _device_id: [u8; 16], transcript: &[u8], signature: &[u8]) -> bool {
        ring::signature::UnparsedPublicKey::new(&ring::signature::ED25519, self.public_key)
            .verify(transcript, signature)
            .is_ok()
    }
}

#[derive(Clone, Debug)]
pub struct PeerBindingContext {
    pub ticket_sha256: [u8; 32],
    pub connection_id: [u8; 16],
    pub path_epoch: u64,
}

impl PeerBindingContext {
    pub fn exporter_context(&self) -> Result<[u8; 56], QuicError> {
        if self.path_epoch == 0 || self.path_epoch > i64::MAX as u64 {
            return Err(QuicError::InvalidPeerProof);
        }
        let mut context = [0_u8; 56];
        context[..32].copy_from_slice(&self.ticket_sha256);
        context[32..48].copy_from_slice(&self.connection_id);
        context[48..].copy_from_slice(&self.path_epoch.to_be_bytes());
        Ok(context)
    }
}

pub struct TemporaryIdentity {
    certificate: CertificateDer<'static>,
    private_key_pkcs8: Vec<u8>,
    certificate_sha256: [u8; 32],
}

impl TemporaryIdentity {
    pub fn generate() -> Result<Self, QuicError> {
        let generated = rcgen::generate_simple_self_signed(vec!["termlink-direct.invalid".into()])
            .map_err(|_| QuicError::Certificate)?;
        let certificate = CertificateDer::from(generated.cert);
        let certificate_sha256 = sha256(certificate.as_ref());
        Ok(Self {
            certificate,
            private_key_pkcs8: generated.key_pair.serialize_der(),
            certificate_sha256,
        })
    }

    pub fn certificate_der(&self) -> CertificateDer<'static> {
        self.certificate.clone()
    }

    pub fn certificate_sha256(&self) -> [u8; 32] {
        self.certificate_sha256
    }
}

pub struct ShadowEndpoint {
    endpoint: Endpoint,
    local_addr: std::net::SocketAddr,
    expected_remote: std::net::SocketAddr,
    certificate_sha256: [u8; 32],
    certificate_der: Option<CertificateDer<'static>>,
}

#[derive(Debug)]
struct FingerprintServerVerifier {
    expected: [u8; 32],
}

impl ServerCertVerifier for FingerprintServerVerifier {
    fn verify_server_cert(
        &self,
        end_entity: &CertificateDer<'_>,
        intermediates: &[CertificateDer<'_>],
        _server_name: &ServerName<'_>,
        _ocsp_response: &[u8],
        _now: UnixTime,
    ) -> Result<ServerCertVerified, RustlsError> {
        verify_fingerprint(end_entity, intermediates, self.expected)?;
        Ok(ServerCertVerified::assertion())
    }

    fn verify_tls12_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, RustlsError> {
        rustls::crypto::verify_tls12_signature(
            message,
            cert,
            dss,
            &rustls::crypto::ring::default_provider().signature_verification_algorithms,
        )
    }

    fn verify_tls13_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, RustlsError> {
        rustls::crypto::verify_tls13_signature(
            message,
            cert,
            dss,
            &rustls::crypto::ring::default_provider().signature_verification_algorithms,
        )
    }

    fn supported_verify_schemes(&self) -> Vec<SignatureScheme> {
        rustls::crypto::ring::default_provider()
            .signature_verification_algorithms
            .supported_schemes()
    }
}

#[derive(Debug)]
struct FingerprintClientVerifier {
    expected: [u8; 32],
    hints: Vec<DistinguishedName>,
}

impl ClientCertVerifier for FingerprintClientVerifier {
    fn root_hint_subjects(&self) -> &[DistinguishedName] {
        &self.hints
    }

    fn verify_client_cert(
        &self,
        end_entity: &CertificateDer<'_>,
        intermediates: &[CertificateDer<'_>],
        _now: UnixTime,
    ) -> Result<ClientCertVerified, RustlsError> {
        verify_fingerprint(end_entity, intermediates, self.expected)?;
        Ok(ClientCertVerified::assertion())
    }

    fn verify_tls12_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, RustlsError> {
        rustls::crypto::verify_tls12_signature(
            message,
            cert,
            dss,
            &rustls::crypto::ring::default_provider().signature_verification_algorithms,
        )
    }

    fn verify_tls13_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, RustlsError> {
        rustls::crypto::verify_tls13_signature(
            message,
            cert,
            dss,
            &rustls::crypto::ring::default_provider().signature_verification_algorithms,
        )
    }

    fn supported_verify_schemes(&self) -> Vec<SignatureScheme> {
        rustls::crypto::ring::default_provider()
            .signature_verification_algorithms
            .supported_schemes()
    }
}

fn verify_fingerprint(
    end_entity: &CertificateDer<'_>,
    intermediates: &[CertificateDer<'_>],
    expected: [u8; 32],
) -> Result<(), RustlsError> {
    if !intermediates.is_empty() || sha256(end_entity.as_ref()) != expected {
        return Err(RustlsError::InvalidCertificate(
            rustls::CertificateError::ApplicationVerificationFailure,
        ));
    }
    Ok(())
}

#[derive(Debug)]
struct PortableSingleDatagramSocket {
    inner: Arc<tokio::net::UdpSocket>,
    expected_remote: SocketAddr,
}

impl PortableSingleDatagramSocket {
    fn wrap(socket: UdpSocket, expected_remote: SocketAddr) -> Result<Arc<Self>, io::Error> {
        Ok(Arc::new(Self {
            inner: Arc::new(tokio::net::UdpSocket::from_std(socket)?),
            expected_remote,
        }))
    }
}

#[derive(Debug)]
struct PortableUdpPoller {
    socket: Arc<tokio::net::UdpSocket>,
}

impl UdpPoller for PortableUdpPoller {
    fn poll_writable(self: Pin<&mut Self>, context: &mut Context<'_>) -> Poll<io::Result<()>> {
        self.socket.poll_send_ready(context)
    }
}

impl AsyncUdpSocket for PortableSingleDatagramSocket {
    fn create_io_poller(self: Arc<Self>) -> Pin<Box<dyn UdpPoller>> {
        Box::pin(PortableUdpPoller {
            socket: Arc::clone(&self.inner),
        })
    }

    fn try_send(&self, transmit: &Transmit<'_>) -> io::Result<()> {
        if transmit.destination != self.expected_remote {
            return Err(io::Error::new(
                io::ErrorKind::PermissionDenied,
                "QUIC destination is not the nominated peer",
            ));
        }
        if transmit.contents.len() > usize::from(QUIC_MTU) {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "QUIC datagram exceeds the portable MTU",
            ));
        }
        if transmit.segment_size.is_some() {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "UDP GSO is disabled",
            ));
        }
        self.inner
            .try_send_to(transmit.contents, transmit.destination)
            .and_then(|written| {
                if written == transmit.contents.len() {
                    Ok(())
                } else {
                    Err(io::Error::new(
                        io::ErrorKind::WriteZero,
                        "partial UDP datagram write",
                    ))
                }
            })
    }

    fn poll_recv(
        &self,
        context: &mut Context<'_>,
        buffers: &mut [IoSliceMut<'_>],
        metadata: &mut [RecvMeta],
    ) -> Poll<io::Result<usize>> {
        let Some(buffer) = buffers.first_mut() else {
            return Poll::Ready(Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "QUIC receive buffer is empty",
            )));
        };
        let Some(meta) = metadata.first_mut() else {
            return Poll::Ready(Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "QUIC receive metadata is empty",
            )));
        };
        loop {
            let mut read_buffer = tokio::io::ReadBuf::new(buffer);
            match self.inner.poll_recv_from(context, &mut read_buffer) {
                Poll::Ready(Ok(source)) if source != self.expected_remote => {
                    // Discard before Quinn sees the datagram. A foreign Initial
                    // therefore cannot consume max_incoming or its buffer.
                    continue;
                }
                Poll::Ready(Ok(source)) => {
                    let length = read_buffer.filled().len();
                    if is_pre_quic_control_datagram(&read_buffer.filled()[..length]) {
                        // The nominated endpoint can still have authenticated
                        // connectivity-check or STUN packets queued when the
                        // exclusive socket moves into Quinn. Consume only
                        // unambiguous control formats; the first non-control
                        // datagram must remain available to the QUIC handshake.
                        continue;
                    }
                    if length > usize::from(QUIC_MTU) {
                        return Poll::Ready(Err(io::Error::new(
                            io::ErrorKind::InvalidData,
                            "oversized QUIC datagram rejected",
                        )));
                    }
                    *meta = RecvMeta {
                        addr: source,
                        len: length,
                        stride: length,
                        ecn: None,
                        dst_ip: None,
                    };
                    return Poll::Ready(Ok(1));
                }
                Poll::Ready(Err(error)) if is_retryable_udp_receive_error(&error) => continue,
                Poll::Ready(Err(error)) => return Poll::Ready(Err(error)),
                Poll::Pending => return Poll::Pending,
            }
        }
    }

    fn local_addr(&self) -> io::Result<SocketAddr> {
        self.inner.local_addr()
    }

    fn max_transmit_segments(&self) -> usize {
        1
    }

    fn max_receive_segments(&self) -> usize {
        1
    }

    fn may_fragment(&self) -> bool {
        false
    }
}

fn is_pre_quic_control_datagram(packet: &[u8]) -> bool {
    if packet.starts_with(b"TLD1\n") || packet.starts_with(b"TLD2\n") {
        return true;
    }
    if packet.len() < STUN_HEADER_BYTES
        || packet[0] & 0xc0 != 0
        || packet[4..8] != STUN_MAGIC_COOKIE.to_be_bytes()
    {
        return false;
    }
    let attributes_length = usize::from(u16::from_be_bytes([packet[2], packet[3]]));
    attributes_length % 4 == 0 && attributes_length + STUN_HEADER_BYTES == packet.len()
}

impl ShadowEndpoint {
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
        let server_config = server_config(identity, pinned_client_certificate_sha256)?;
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
        let mut endpoint = Self {
            endpoint,
            local_addr: original_addr,
            expected_remote,
            certificate_sha256,
            certificate_der: None,
        };
        endpoint
            .endpoint
            .set_default_client_config(client_config(identity, pinned_server_certificate_sha256)?);
        Ok(endpoint)
    }

    pub fn local_addr(&self) -> std::net::SocketAddr {
        self.local_addr
    }

    pub fn certificate_sha256(&self) -> [u8; 32] {
        self.certificate_sha256
    }

    pub fn certificate_der(&self) -> Option<CertificateDer<'static>> {
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
        remote: std::net::SocketAddr,
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
        self.endpoint.close(VarInt::from_u32(0), b"shadow-complete");
    }
}

pub fn tls_exporter_sha256(
    connection: &Connection,
    binding: &PeerBindingContext,
) -> Result<[u8; 32], QuicError> {
    let mut exporter = [0_u8; 32];
    connection
        .export_keying_material(
            &mut exporter,
            TLS_EXPORTER_LABEL,
            &binding.exporter_context()?,
        )
        .map_err(|_| QuicError::TlsExporter)?;
    Ok(sha256(&exporter))
}

pub fn verify_peer_proof(
    verifier: &dyn PlatformPeerProofVerifier,
    proof: &PeerProofRequest,
    signature: &[u8],
) -> Result<(), QuicError> {
    if signature.is_empty() || !verifier.verify(proof.device_id, &proof.transcript()?, signature) {
        return Err(QuicError::InvalidPeerProof);
    }
    Ok(())
}

pub struct AuthenticatedShadowConnection {
    connection: Connection,
    binding: PeerBindingContext,
    tls_exporter_sha256: [u8; 32],
    peer_proof_sha256: [u8; 32],
}

impl AuthenticatedShadowConnection {
    pub fn tls_exporter_sha256(&self) -> [u8; 32] {
        self.tls_exporter_sha256
    }

    pub fn peer_proof_sha256(&self) -> [u8; 32] {
        self.peer_proof_sha256
    }
}

pub fn authenticate_shadow_connection(
    connection: Connection,
    binding: &PeerBindingContext,
    expected_peer: ExpectedPeer,
    proof: &PeerProofRequest,
    signature: &[u8],
    verifier: &dyn PlatformPeerProofVerifier,
) -> Result<AuthenticatedShadowConnection, QuicError> {
    let exporter = tls_exporter_sha256(&connection, binding)?;
    if proof.role != expected_peer.role
        || proof.device_id != expected_peer.device_id
        || proof.peer_device_id != expected_peer.local_device_id
        || proof.ticket_sha256 != binding.ticket_sha256
        || proof.connection_id != binding.connection_id
        || proof.path_epoch != binding.path_epoch
        || proof.tls_exporter_sha256 != exporter
    {
        connection.close(VarInt::from_u32(2), b"peer-proof-binding-mismatch");
        return Err(QuicError::InvalidPeerProof);
    }
    let proof_transcript = proof.transcript()?;
    verify_peer_proof(verifier, proof, signature)?;
    Ok(AuthenticatedShadowConnection {
        connection,
        binding: binding.clone(),
        tls_exporter_sha256: exporter,
        peer_proof_sha256: sha256(&proof_transcript),
    })
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct DirectPathBindingInputs {
    pub ticket_sha256: [u8; 32],
    pub initiator_candidate_id: [u8; 16],
    pub responder_candidate_id: [u8; 16],
    pub initiator_certificate_sha256: [u8; 32],
    pub responder_certificate_sha256: [u8; 32],
    pub initiator_proof_sha256: [u8; 32],
    pub responder_proof_sha256: [u8; 32],
    pub tls_exporter_sha256: [u8; 32],
}

pub fn direct_path_binding_sha256(inputs: DirectPathBindingInputs) -> [u8; 32] {
    let mut preimage = Vec::with_capacity(PATH_BINDING_DOMAIN.len() + 224);
    preimage.extend_from_slice(PATH_BINDING_DOMAIN);
    preimage.extend_from_slice(&inputs.ticket_sha256);
    preimage.extend_from_slice(&inputs.initiator_candidate_id);
    preimage.extend_from_slice(&inputs.responder_candidate_id);
    preimage.extend_from_slice(&inputs.initiator_certificate_sha256);
    preimage.extend_from_slice(&inputs.responder_certificate_sha256);
    preimage.extend_from_slice(&inputs.initiator_proof_sha256);
    preimage.extend_from_slice(&inputs.responder_proof_sha256);
    preimage.extend_from_slice(&inputs.tls_exporter_sha256);
    sha256(&preimage)
}

/// Exchanges one bounded proof record on the authenticated/pinned QUIC
/// connection and verifies the remote device proof before returning a usable
/// shadow connection. Initiator opens the stream; responder accepts it, which
/// avoids two independent proof streams racing each other.
pub async fn exchange_and_authenticate_shadow_connection(
    connection: Connection,
    binding: &PeerBindingContext,
    expected_peer: ExpectedPeer,
    ticket: &[u8],
    local_proof: &PeerProofRequest,
    local_signature: &[u8],
    verifier: &dyn PlatformPeerProofVerifier,
) -> Result<AuthenticatedShadowConnection, QuicError> {
    if local_signature.is_empty() || local_signature.len() > MAX_PEER_SIGNATURE_BYTES {
        return Err(QuicError::InvalidPeerProof);
    }
    if ticket.is_empty()
        || ticket.len() > MAX_DIRECT_TICKET_WIRE_BYTES
        || sha256(ticket) != binding.ticket_sha256
    {
        return Err(QuicError::InvalidPeerProof);
    }
    let local_record = encode_peer_proof_frame(ticket, local_proof, local_signature)?;
    tokio::time::timeout(SHADOW_OPERATION_TIMEOUT, async {
        let (mut send, mut receive) = if local_proof.role == PeerRole::Initiator {
            connection.open_bi().await?
        } else {
            connection.accept_bi().await?
        };
        let remote_record = if local_proof.role == PeerRole::Initiator {
            send.write_all(&local_record).await?;
            read_direct_frame(&mut receive).await?
        } else {
            let remote = read_direct_frame(&mut receive).await?;
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
            read_direct_frame(&mut receive).await?
        } else {
            let remote = read_direct_frame(&mut receive).await?;
            send.write_all(&local_result).await?;
            remote
        };
        send.finish()?;
        let local_transcript_sha256 = sha256(&local_proof.transcript()?);
        decode_peer_proof_result_frame(&peer_result, binding, local_transcript_sha256)?;
        // Consume the peer FIN so the single permitted bidirectional proof
        // stream releases its credit before shadow ping opens the next stream.
        let trailing = receive.read_to_end(0).await?;
        if !trailing.is_empty() {
            return Err(QuicError::InvalidPeerProof);
        }
        Ok(authenticated)
    })
    .await
    .map_err(|_| QuicError::Timeout)?
}

async fn read_direct_frame(receive: &mut quinn::RecvStream) -> Result<Vec<u8>, QuicError> {
    let mut length_bytes = [0_u8; 4];
    receive
        .read_exact(&mut length_bytes)
        .await
        .map_err(|_| QuicError::InvalidPeerProof)?;
    let length = u32::from_be_bytes(length_bytes) as usize;
    if length == 0 || length + 4 > MAX_DIRECT_FRAME_WIRE_BYTES {
        return Err(QuicError::InvalidPeerProof);
    }
    let mut frame = Vec::with_capacity(length + 4);
    frame.extend_from_slice(&length_bytes);
    frame.resize(length + 4, 0);
    receive
        .read_exact(&mut frame[4..])
        .await
        .map_err(|_| QuicError::InvalidPeerProof)?;
    Ok(frame)
}

fn encode_peer_proof_frame(
    ticket: &[u8],
    proof: &PeerProofRequest,
    signature: &[u8],
) -> Result<Vec<u8>, QuicError> {
    let _ = proof.transcript()?;
    if ticket.is_empty()
        || ticket.len() > MAX_DIRECT_TICKET_WIRE_BYTES
        || sha256(ticket) != proof.ticket_sha256
        || signature.is_empty()
        || signature.len() > MAX_PEER_SIGNATURE_BYTES
        || proof.proof_nonce.len() > u8::MAX as usize
    {
        return Err(QuicError::InvalidPeerProof);
    }
    // Canonical DirectPeerProofV1 (fields 1..7) inside DirectPathFrameV1
    // (fields 1..4 and oneof field 10), with the protocol's four-byte
    // unsigned big-endian frame prefix. No private shadow-only wire format is
    // introduced here.
    let mut peer_proof =
        Vec::with_capacity(ticket.len() + proof.proof_nonce.len() + signature.len() + 128);
    put_bytes_field(1, ticket, &mut peer_proof);
    put_varint_field(
        2,
        match proof.role {
            PeerRole::Initiator => 1,
            PeerRole::Responder => 2,
        },
        &mut peer_proof,
    );
    put_bytes_field(3, &proof.device_id, &mut peer_proof);
    put_bytes_field(4, &proof.peer_device_id, &mut peer_proof);
    put_bytes_field(5, &proof.proof_nonce, &mut peer_proof);
    put_bytes_field(6, &proof.tls_exporter_sha256, &mut peer_proof);
    put_bytes_field(7, signature, &mut peer_proof);
    if peer_proof.len() > MAX_PEER_PROOF_WIRE_BYTES {
        return Err(QuicError::InvalidPeerProof);
    }

    let mut body = Vec::with_capacity(peer_proof.len() + 64);
    put_varint_field(1, 1, &mut body);
    put_bytes_field(2, &proof.connection_id, &mut body);
    put_varint_field(3, proof.path_epoch, &mut body);
    put_varint_field(4, 1, &mut body);
    put_bytes_field(10, &peer_proof, &mut body);
    if body.len() + 4 > MAX_DIRECT_FRAME_WIRE_BYTES {
        return Err(QuicError::InvalidPeerProof);
    }
    let mut framed = Vec::with_capacity(body.len() + 4);
    framed.extend_from_slice(&(body.len() as u32).to_be_bytes());
    framed.extend_from_slice(&body);
    Ok(framed)
}

fn decode_peer_proof_frame(
    frame: &[u8],
    binding: &PeerBindingContext,
    expected_peer: &ExpectedPeer,
) -> Result<(PeerProofRequest, Vec<u8>), QuicError> {
    if frame.len() < 5 || frame.len() > MAX_DIRECT_FRAME_WIRE_BYTES {
        return Err(QuicError::InvalidPeerProof);
    }
    let declared = u32::from_be_bytes(
        frame[..4]
            .try_into()
            .map_err(|_| QuicError::InvalidPeerProof)?,
    ) as usize;
    if declared != frame.len() - 4 {
        return Err(QuicError::InvalidPeerProof);
    }
    let mut cursor = ProofCursor::new(&frame[4..]);
    let protocol = cursor.required_varint(1)?;
    let connection_id: [u8; 16] = cursor
        .required_bytes(2, 16)?
        .try_into()
        .map_err(|_| QuicError::InvalidPeerProof)?;
    let path_epoch = cursor.required_varint(3)?;
    let frame_seq = cursor.required_varint(4)?;
    let peer_proof_bytes = cursor.required_bytes(10, MAX_PEER_PROOF_WIRE_BYTES)?;
    cursor.finish()?;
    if protocol != 1
        || connection_id != binding.connection_id
        || path_epoch != binding.path_epoch
        || frame_seq != 1
    {
        return Err(QuicError::InvalidPeerProof);
    }

    let mut proof_cursor = ProofCursor::new(peer_proof_bytes);
    let ticket = proof_cursor.required_bytes(1, MAX_DIRECT_TICKET_WIRE_BYTES)?;
    let role = match proof_cursor.required_varint(2)? {
        1 => PeerRole::Initiator,
        2 => PeerRole::Responder,
        _ => return Err(QuicError::InvalidPeerProof),
    };
    let device_id = proof_cursor
        .required_bytes(3, 16)?
        .try_into()
        .map_err(|_| QuicError::InvalidPeerProof)?;
    let peer_device_id = proof_cursor
        .required_bytes(4, 16)?
        .try_into()
        .map_err(|_| QuicError::InvalidPeerProof)?;
    let proof_nonce = proof_cursor.required_bytes(5, 32)?;
    if !(16..=32).contains(&proof_nonce.len()) {
        return Err(QuicError::InvalidPeerProof);
    }
    let exporter: [u8; 32] = proof_cursor
        .required_bytes(6, 32)?
        .try_into()
        .map_err(|_| QuicError::InvalidPeerProof)?;
    let signature = proof_cursor.required_bytes(7, MAX_PEER_SIGNATURE_BYTES)?;
    if signature.is_empty() {
        return Err(QuicError::InvalidPeerProof);
    }
    proof_cursor.finish()?;
    let proof = PeerProofRequest {
        ticket_sha256: binding.ticket_sha256,
        role,
        device_id,
        peer_device_id,
        connection_id: binding.connection_id,
        path_epoch: binding.path_epoch,
        proof_nonce: proof_nonce.to_vec(),
        tls_exporter_sha256: exporter,
    };
    if sha256(ticket) != binding.ticket_sha256
        || proof.role != expected_peer.role
        || proof.device_id != expected_peer.device_id
        || proof.peer_device_id != expected_peer.local_device_id
    {
        return Err(QuicError::InvalidPeerProof);
    }
    Ok((proof, signature.to_vec()))
}

fn encode_peer_proof_result_frame(
    binding: &PeerBindingContext,
    peer_proof_sha256: [u8; 32],
) -> Result<Vec<u8>, QuicError> {
    let mut result = Vec::with_capacity(40);
    put_varint_field(1, 1, &mut result);
    put_bytes_field(3, &peer_proof_sha256, &mut result);
    let mut body = Vec::with_capacity(80);
    put_varint_field(1, 1, &mut body);
    put_bytes_field(2, &binding.connection_id, &mut body);
    put_varint_field(3, binding.path_epoch, &mut body);
    put_varint_field(4, 2, &mut body);
    put_bytes_field(11, &result, &mut body);
    if body.len() + 4 > MAX_DIRECT_FRAME_WIRE_BYTES {
        return Err(QuicError::InvalidPeerProof);
    }
    let mut frame = Vec::with_capacity(body.len() + 4);
    frame.extend_from_slice(&(body.len() as u32).to_be_bytes());
    frame.extend_from_slice(&body);
    Ok(frame)
}

fn decode_peer_proof_result_frame(
    frame: &[u8],
    binding: &PeerBindingContext,
    expected_peer_proof_sha256: [u8; 32],
) -> Result<(), QuicError> {
    if frame.len() < 5 || frame.len() > MAX_DIRECT_FRAME_WIRE_BYTES {
        return Err(QuicError::InvalidPeerProof);
    }
    let declared = u32::from_be_bytes(
        frame[..4]
            .try_into()
            .map_err(|_| QuicError::InvalidPeerProof)?,
    ) as usize;
    if declared != frame.len() - 4 {
        return Err(QuicError::InvalidPeerProof);
    }
    let mut cursor = ProofCursor::new(&frame[4..]);
    let protocol = cursor.required_varint(1)?;
    let connection_id: [u8; 16] = cursor
        .required_bytes(2, 16)?
        .try_into()
        .map_err(|_| QuicError::InvalidPeerProof)?;
    let path_epoch = cursor.required_varint(3)?;
    let frame_seq = cursor.required_varint(4)?;
    let result = cursor.required_bytes(11, 64)?;
    cursor.finish()?;
    if protocol != 1
        || connection_id != binding.connection_id
        || path_epoch != binding.path_epoch
        || frame_seq != 2
    {
        return Err(QuicError::InvalidPeerProof);
    }
    let mut result_cursor = ProofCursor::new(result);
    if result_cursor.required_varint(1)? != 1 {
        return Err(QuicError::InvalidPeerProof);
    }
    let peer_proof_sha256: [u8; 32] = result_cursor
        .required_bytes(3, 32)?
        .try_into()
        .map_err(|_| QuicError::InvalidPeerProof)?;
    result_cursor.finish()?;
    if peer_proof_sha256 != expected_peer_proof_sha256 {
        return Err(QuicError::InvalidPeerProof);
    }
    Ok(())
}

struct ProofCursor<'a> {
    bytes: &'a [u8],
    offset: usize,
    last_field: u64,
}

impl<'a> ProofCursor<'a> {
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
        let length = usize::try_from(self.varint()?).map_err(|_| QuicError::InvalidPeerProof)?;
        if length > maximum {
            return Err(QuicError::InvalidPeerProof);
        }
        let end = self
            .offset
            .checked_add(length)
            .ok_or(QuicError::InvalidPeerProof)?;
        let value = self
            .bytes
            .get(self.offset..end)
            .ok_or(QuicError::InvalidPeerProof)?;
        self.offset = end;
        Ok(value)
    }

    fn require_key(&mut self, field: u64, wire_type: u64) -> Result<(), QuicError> {
        let key = self.varint()?;
        if key >> 3 != field || key & 7 != wire_type || field <= self.last_field {
            return Err(QuicError::InvalidPeerProof);
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
                .ok_or(QuicError::InvalidPeerProof)?;
            self.offset += 1;
            if shift == 63 && byte > 1 {
                return Err(QuicError::InvalidPeerProof);
            }
            value |= u64::from(byte & 0x7f) << shift;
            if byte & 0x80 == 0 {
                if self.offset - start != proof_varint_len(value) {
                    return Err(QuicError::InvalidPeerProof);
                }
                return Ok(value);
            }
        }
        Err(QuicError::InvalidPeerProof)
    }

    fn finish(&self) -> Result<(), QuicError> {
        if self.offset == self.bytes.len() {
            Ok(())
        } else {
            Err(QuicError::InvalidPeerProof)
        }
    }
}

fn put_varint_field(field: u64, value: u64, output: &mut Vec<u8>) {
    put_proof_varint(field << 3, output);
    put_proof_varint(value, output);
}

fn put_bytes_field(field: u64, value: &[u8], output: &mut Vec<u8>) {
    put_proof_varint((field << 3) | 2, output);
    put_proof_varint(value.len() as u64, output);
    output.extend_from_slice(value);
}

fn put_proof_varint(mut value: u64, output: &mut Vec<u8>) {
    while value >= 0x80 {
        output.push((value as u8) | 0x80);
        value >>= 7;
    }
    output.push(value as u8);
}

fn proof_varint_len(mut value: u64) -> usize {
    let mut length = 1;
    while value >= 0x80 {
        value >>= 7;
        length += 1;
    }
    length
}

pub async fn shadow_ping(
    connection: &AuthenticatedShadowConnection,
    nonce: [u8; 16],
) -> Result<(), QuicError> {
    tokio::time::timeout(SHADOW_OPERATION_TIMEOUT, async {
        let (mut send, mut receive) = connection.connection.open_bi().await?;
        let binding = connection.binding();
        let request = encode_ping_frame(&binding, nonce, direct_now_ms())?;
        send.write_all(&request).await?;
        let response = read_direct_frame(&mut receive).await?;
        decode_pong_frame(&response, &binding, nonce)?;
        // This FIN is the responder's application-level confirmation that the
        // canonical pong was received. It carries no private record.
        send.finish()?;
        if !receive.read_to_end(0).await?.is_empty() {
            return Err(QuicError::InvalidPing);
        }
        Ok(())
    })
    .await
    .map_err(|_| QuicError::Timeout)?
}

pub async fn shadow_echo_once(connection: &AuthenticatedShadowConnection) -> Result<(), QuicError> {
    tokio::time::timeout(SHADOW_OPERATION_TIMEOUT, async {
        let (mut send, mut receive) = connection.connection.accept_bi().await?;
        let binding = connection.binding();
        let request = read_direct_frame(&mut receive).await?;
        let nonce = decode_ping_frame(&request, &binding)?;
        let response = encode_pong_frame(&binding, nonce, direct_now_ms())?;
        send.write_all(&response).await?;
        // The initiator closes its direction only after validating this pong.
        // Consume that FIN before retiring the responder endpoint.
        if !receive.read_to_end(0).await?.is_empty() {
            return Err(QuicError::InvalidPing);
        }
        send.finish()?;
        // The initiator deliberately waits for this FIN before retiring its
        // endpoint. Waiting for acknowledgement here fences the responder's
        // endpoint retirement and prevents a queued FIN from being discarded.
        send.stopped().await.map_err(|_| QuicError::InvalidPing)?;
        Ok(())
    })
    .await
    .map_err(|_| QuicError::Timeout)?
}

impl AuthenticatedShadowConnection {
    fn binding(&self) -> PeerBindingContext {
        self.binding.clone()
    }
}

fn encode_ping_frame(
    binding: &PeerBindingContext,
    nonce: [u8; 16],
    sent_at_ms: i64,
) -> Result<Vec<u8>, QuicError> {
    encode_ping_or_pong_frame(binding, 14, nonce, sent_at_ms)
}

fn encode_pong_frame(
    binding: &PeerBindingContext,
    nonce: [u8; 16],
    received_at_ms: i64,
) -> Result<Vec<u8>, QuicError> {
    encode_ping_or_pong_frame(binding, 15, nonce, received_at_ms)
}

fn encode_ping_or_pong_frame(
    binding: &PeerBindingContext,
    body_field: u64,
    nonce: [u8; 16],
    timestamp_ms: i64,
) -> Result<Vec<u8>, QuicError> {
    if timestamp_ms < 0 || !matches!(body_field, 14 | 15) {
        return Err(QuicError::InvalidPing);
    }
    let mut ping_or_pong = Vec::with_capacity(32);
    put_bytes_field(1, &nonce, &mut ping_or_pong);
    put_varint_field(2, timestamp_ms as u64, &mut ping_or_pong);
    let mut body = Vec::with_capacity(80);
    put_varint_field(1, 1, &mut body);
    put_bytes_field(2, &binding.connection_id, &mut body);
    put_varint_field(3, binding.path_epoch, &mut body);
    put_varint_field(4, 3, &mut body);
    put_bytes_field(body_field, &ping_or_pong, &mut body);
    if body.len() + 4 > MAX_DIRECT_FRAME_WIRE_BYTES {
        return Err(QuicError::InvalidPing);
    }
    let mut frame = Vec::with_capacity(body.len() + 4);
    frame.extend_from_slice(&(body.len() as u32).to_be_bytes());
    frame.extend_from_slice(&body);
    Ok(frame)
}

fn decode_ping_frame(frame: &[u8], binding: &PeerBindingContext) -> Result<[u8; 16], QuicError> {
    decode_ping_or_pong_frame(frame, binding, 14, None)
}

fn decode_pong_frame(
    frame: &[u8],
    binding: &PeerBindingContext,
    expected_nonce: [u8; 16],
) -> Result<[u8; 16], QuicError> {
    decode_ping_or_pong_frame(frame, binding, 15, Some(expected_nonce))
}

fn decode_ping_or_pong_frame(
    frame: &[u8],
    binding: &PeerBindingContext,
    body_field: u64,
    expected_nonce: Option<[u8; 16]>,
) -> Result<[u8; 16], QuicError> {
    if frame.len() < 5 || frame.len() > MAX_DIRECT_FRAME_WIRE_BYTES {
        return Err(QuicError::InvalidPing);
    }
    let declared =
        u32::from_be_bytes(frame[..4].try_into().map_err(|_| QuicError::InvalidPing)?) as usize;
    if declared != frame.len() - 4 {
        return Err(QuicError::InvalidPing);
    }
    let mut cursor = ProofCursor::new(&frame[4..]);
    let protocol = cursor.required_varint(1)?;
    let connection_id: [u8; 16] = cursor
        .required_bytes(2, 16)?
        .try_into()
        .map_err(|_| QuicError::InvalidPing)?;
    let path_epoch = cursor.required_varint(3)?;
    let frame_seq = cursor.required_varint(4)?;
    let body = cursor.required_bytes(body_field, 40)?;
    cursor.finish()?;
    if protocol != 1
        || connection_id != binding.connection_id
        || path_epoch != binding.path_epoch
        || frame_seq != 3
    {
        return Err(QuicError::InvalidPing);
    }
    let mut body_cursor = ProofCursor::new(body);
    let nonce: [u8; 16] = body_cursor
        .required_bytes(1, 16)?
        .try_into()
        .map_err(|_| QuicError::InvalidPing)?;
    let timestamp_ms = body_cursor.required_varint(2)?;
    body_cursor.finish()?;
    if timestamp_ms > i64::MAX as u64 || expected_nonce.is_some_and(|expected| expected != nonce) {
        return Err(QuicError::InvalidPing);
    }
    Ok(nonce)
}

fn direct_now_ms() -> i64 {
    i64::try_from(
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_millis(),
    )
    .unwrap_or(i64::MAX)
}

fn endpoint_config() -> Result<EndpointConfig, QuicError> {
    SafeQuicPolicy::default().validate()?;
    let mut config = EndpointConfig::default();
    config.max_udp_payload_size(QUIC_MTU)?;
    Ok(config)
}

fn transport_config() -> Arc<TransportConfig> {
    let mut config = TransportConfig::default();
    config
        .initial_mtu(QUIC_MTU)
        .min_mtu(QUIC_MTU)
        .mtu_discovery_config(None)
        // One stream is reserved for peer-proof/result exchange and one for
        // the post-authentication shadow health check.
        .max_concurrent_bidi_streams(2_u8.into())
        .max_concurrent_uni_streams(0_u8.into())
        .stream_receive_window(VarInt::from_u32(16 * 1024))
        .receive_window(VarInt::from_u32(32 * 1024))
        .send_window(32 * 1024)
        .max_idle_timeout(Some(VarInt::from_u32(10_000).into()))
        .keep_alive_interval(None);
    Arc::new(config)
}

fn server_config(
    identity: TemporaryIdentity,
    pinned_client_certificate_sha256: [u8; 32],
) -> Result<ServerConfig, QuicError> {
    server_config_with_transport(
        identity,
        pinned_client_certificate_sha256,
        transport_config(),
    )
}

fn server_config_with_transport(
    identity: TemporaryIdentity,
    pinned_client_certificate_sha256: [u8; 32],
    transport: Arc<TransportConfig>,
) -> Result<ServerConfig, QuicError> {
    let client_verifier = Arc::new(FingerprintClientVerifier {
        expected: pinned_client_certificate_sha256,
        hints: Vec::new(),
    });
    let private_key = PrivatePkcs8KeyDer::from(identity.private_key_pkcs8);
    let mut tls = rustls::ServerConfig::builder_with_protocol_versions(&[&rustls::version::TLS13])
        .with_client_cert_verifier(client_verifier)
        .with_single_cert(vec![identity.certificate], private_key.into())
        .map_err(|_| QuicError::Certificate)?;
    tls.alpn_protocols = vec![ALPN.to_vec()];
    tls.max_early_data_size = 0;
    tls.send_half_rtt_data = false;
    tls.session_storage = Arc::new(rustls::server::NoServerSessionStorage {});
    let crypto = QuicServerConfig::try_from(tls).map_err(|_| QuicError::Certificate)?;
    let mut config = ServerConfig::with_crypto(Arc::new(crypto));
    config.migration(false);
    config
        .max_incoming(1)
        .incoming_buffer_size(4 * 1024)
        .incoming_buffer_size_total(4 * 1024);
    config.transport = transport;
    Ok(config)
}

fn client_config(
    identity: TemporaryIdentity,
    pinned_server_certificate_sha256: [u8; 32],
) -> Result<ClientConfig, QuicError> {
    client_config_with_transport(
        identity,
        pinned_server_certificate_sha256,
        transport_config(),
    )
}

fn client_config_with_transport(
    identity: TemporaryIdentity,
    pinned_server_certificate_sha256: [u8; 32],
    transport: Arc<TransportConfig>,
) -> Result<ClientConfig, QuicError> {
    let mut tls = rustls::ClientConfig::builder_with_protocol_versions(&[&rustls::version::TLS13])
        .dangerous()
        .with_custom_certificate_verifier(Arc::new(FingerprintServerVerifier {
            expected: pinned_server_certificate_sha256,
        }))
        .with_client_auth_cert(
            vec![identity.certificate],
            PrivatePkcs8KeyDer::from(identity.private_key_pkcs8).into(),
        )
        .map_err(|_| QuicError::Certificate)?;
    tls.alpn_protocols = vec![ALPN.to_vec()];
    tls.enable_early_data = false;
    tls.resumption = Resumption::disabled();
    let crypto = QuicClientConfig::try_from(tls).map_err(|_| QuicError::Certificate)?;
    let mut config = ClientConfig::new(Arc::new(crypto));
    config.transport_config(transport);
    Ok(config)
}

fn verify_peer_certificate(connection: &Connection, expected: [u8; 32]) -> Result<(), QuicError> {
    let identity = connection.peer_identity().ok_or(QuicError::Certificate)?;
    let certificates = identity
        .downcast::<Vec<CertificateDer<'static>>>()
        .map_err(|_| QuicError::Certificate)?;
    let leaf = certificates.first().ok_or(QuicError::Certificate)?;
    if sha256(leaf.as_ref()) != expected {
        connection.close(VarInt::from_u32(1), b"certificate-pin-mismatch");
        return Err(QuicError::CertificatePinMismatch);
    }
    Ok(())
}

fn require_same_socket(
    expected: std::net::SocketAddr,
    actual: std::net::SocketAddr,
) -> Result<(), QuicError> {
    if expected != actual {
        return Err(QuicError::SocketIdentityChanged);
    }
    Ok(())
}

fn sha256(bytes: &[u8]) -> [u8; 32] {
    digest::digest(&digest::SHA256, bytes)
        .as_ref()
        .try_into()
        .expect("SHA-256 has a fixed 32-byte output")
}

#[derive(Debug)]
pub enum QuicError {
    Nat(termlink_direct_nat::NatError),
    Io(io::Error),
    Configuration(quinn::ConfigError),
    Connect(quinn::ConnectError),
    Connection(quinn::ConnectionError),
    Write(quinn::WriteError),
    Read(quinn::ReadToEndError),
    ClosedStream(quinn::ClosedStream),
    DirectRead(quinn::ReadExactError),
    EndpointClosed,
    UnsafeConfiguration,
    SocketIdentityChanged,
    Certificate,
    CertificatePinMismatch,
    TlsExporter,
    InvalidPeerProof,
    InvalidPing,
    InvalidDirectFrame,
    InvalidDirectSequence,
    InvalidDirectReceipt,
    UnexpectedPong,
    LivenessTimeout,
    Cancelled,
    Timeout,
    UnexpectedRemote,
    InvalidCandidate,
}

impl fmt::Display for QuicError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(match self {
            Self::Nat(_) => "direct UDP ownership failed",
            Self::Io(_) => "QUIC UDP I/O failed",
            Self::Configuration(_) => "QUIC configuration is invalid",
            Self::Connect(_) => "QUIC connection start failed",
            Self::Connection(_) => "QUIC handshake or connection failed",
            Self::Write(_) => "QUIC shadow write failed",
            Self::Read(_) => "QUIC shadow read failed",
            Self::ClosedStream(_) => "QUIC shadow stream closed",
            Self::DirectRead(_) => "QUIC direct stream read failed",
            Self::EndpointClosed => "QUIC endpoint closed",
            Self::UnsafeConfiguration => "QUIC safety policy was weakened",
            Self::SocketIdentityChanged => "QUIC did not consume the probed UDP socket",
            Self::Certificate => "QUIC temporary certificate is invalid",
            Self::CertificatePinMismatch => "QUIC peer certificate fingerprint does not match",
            Self::TlsExporter => "QUIC TLS exporter failed",
            Self::InvalidPeerProof => "QUIC peer device proof is invalid",
            Self::InvalidPing => "QUIC shadow ping is invalid",
            Self::InvalidDirectFrame => "QUIC direct data frame is invalid",
            Self::InvalidDirectSequence => "QUIC direct frame sequence is invalid",
            Self::InvalidDirectReceipt => "QUIC direct transport receipt is invalid",
            Self::UnexpectedPong => "QUIC direct pong does not match the active ping",
            Self::LivenessTimeout => "QUIC direct path liveness timed out",
            Self::Cancelled => "QUIC direct operation was cancelled",
            Self::Timeout => "QUIC shadow operation timed out",
            Self::UnexpectedRemote => "QUIC peer endpoint does not match the nominated candidate",
            Self::InvalidCandidate => "QUIC nominated candidate is invalid",
        })
    }
}

impl std::error::Error for QuicError {}

macro_rules! from_error {
    ($source:ty, $variant:ident) => {
        impl From<$source> for QuicError {
            fn from(value: $source) -> Self {
                Self::$variant(value)
            }
        }
    };
}

from_error!(termlink_direct_nat::NatError, Nat);
from_error!(io::Error, Io);
from_error!(quinn::ConfigError, Configuration);
from_error!(quinn::ConnectError, Connect);
from_error!(quinn::ConnectionError, Connection);
from_error!(quinn::WriteError, Write);
from_error!(quinn::ReadToEndError, Read);
from_error!(quinn::ClosedStream, ClosedStream);
from_error!(quinn::ReadExactError, DirectRead);

#[cfg(test)]
mod tests {
    use super::*;
    use std::{net::IpAddr, thread, time::Duration};
    use termlink_direct_nat::DirectUdpSocket;
    use termlink_direct_wire::{
        CandidatePair, ConnectivityDatagram, DatagramKind, ExpectedDatagram, ProbeFields,
    };

    struct ExactVerifier(Vec<u8>);

    impl PlatformPeerProofVerifier for ExactVerifier {
        fn verify(&self, _device_id: [u8; 16], transcript: &[u8], signature: &[u8]) -> bool {
            !transcript.is_empty() && signature == self.0
        }
    }

    #[test]
    fn unsafe_quic_options_cannot_be_enabled() {
        let unsafe_policy = SafeQuicPolicy {
            zero_rtt: true,
            ..SafeQuicPolicy::default()
        };
        assert!(matches!(
            unsafe_policy.validate(),
            Err(QuicError::UnsafeConfiguration)
        ));
    }

    #[test]
    fn portable_adapter_discards_foreign_datagrams_before_quinn() {
        let ip = active_unicast_ip();
        let owner = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        owner.set_nonblocking(true).unwrap();
        let owner_addr = owner.local_addr().unwrap();
        let expected = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let expected_addr = expected.local_addr().unwrap();
        let foreign = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        foreign.send_to(b"foreign-initial", owner_addr).unwrap();
        expected.send_to(b"nominated-peer", owner_addr).unwrap();

        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap();
        runtime.block_on(async {
            let adapter = PortableSingleDatagramSocket::wrap(owner, expected_addr).unwrap();
            let mut bytes = [0_u8; 64];
            let mut buffers = [IoSliceMut::new(&mut bytes)];
            let mut metadata = [RecvMeta::default()];
            let count = std::future::poll_fn(|context| {
                adapter.poll_recv(context, &mut buffers, &mut metadata)
            })
            .await
            .unwrap();
            assert_eq!(count, 1);
            assert_eq!(metadata[0].addr, expected_addr);
            assert_eq!(&bytes[..metadata[0].len], b"nominated-peer");
        });
    }

    #[test]
    fn portable_adapter_discards_same_peer_stun_and_tld_but_preserves_first_quic_datagram() {
        let ip = active_unicast_ip();
        let owner = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        owner.set_nonblocking(true).unwrap();
        let owner_addr = owner.local_addr().unwrap();
        let expected = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let expected_addr = expected.local_addr().unwrap();

        let mut stun = vec![0x01, 0x01, 0, 0];
        stun.extend_from_slice(&STUN_MAGIC_COOKIE.to_be_bytes());
        stun.extend_from_slice(&[7; 12]);
        expected.send_to(&stun, owner_addr).unwrap();
        expected
            .send_to(b"TLD2\nresidual-check", owner_addr)
            .unwrap();
        expected.send_to(b"quic-initial", owner_addr).unwrap();

        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap();
        runtime.block_on(async {
            let adapter = PortableSingleDatagramSocket::wrap(owner, expected_addr).unwrap();
            let mut bytes = [0_u8; 64];
            let mut buffers = [IoSliceMut::new(&mut bytes)];
            let mut metadata = [RecvMeta::default()];
            let count = std::future::poll_fn(|context| {
                adapter.poll_recv(context, &mut buffers, &mut metadata)
            })
            .await
            .unwrap();
            assert_eq!(count, 1);
            assert_eq!(metadata[0].addr, expected_addr);
            assert_eq!(&bytes[..metadata[0].len], b"quic-initial");
        });
    }

    #[test]
    fn peer_proof_stays_platform_verified() {
        let proof = PeerProofRequest {
            ticket_sha256: [1; 32],
            role: PeerRole::Initiator,
            device_id: [2; 16],
            peer_device_id: [3; 16],
            connection_id: [4; 16],
            path_epoch: 5,
            proof_nonce: vec![6; 16],
            tls_exporter_sha256: [7; 32],
        };
        assert!(verify_peer_proof(&ExactVerifier(vec![8; 64]), &proof, &[8; 64]).is_ok());
        assert!(matches!(
            verify_peer_proof(&ExactVerifier(vec![8; 64]), &proof, &[9; 64]),
            Err(QuicError::InvalidPeerProof)
        ));
    }

    #[test]
    fn peer_proof_uses_canonical_direct_path_frame_wire() {
        let ticket = vec![0x08, 0x01, 0x12, 0x01, 0x07];
        let binding = PeerBindingContext {
            ticket_sha256: sha256(&ticket),
            connection_id: [4; 16],
            path_epoch: 5,
        };
        let proof = PeerProofRequest {
            ticket_sha256: binding.ticket_sha256,
            role: PeerRole::Initiator,
            device_id: [2; 16],
            peer_device_id: [3; 16],
            connection_id: binding.connection_id,
            path_epoch: binding.path_epoch,
            proof_nonce: vec![6; 16],
            tls_exporter_sha256: [7; 32],
        };
        let frame = encode_peer_proof_frame(&ticket, &proof, &[8; 64]).unwrap();
        assert_eq!(
            u32::from_be_bytes(frame[..4].try_into().unwrap()) as usize,
            frame.len() - 4
        );
        // DirectPathFrameV1 begins with protocol_version field 1, not a
        // private application magic.
        assert_eq!(frame[4], 0x08);
        let (decoded, signature) = decode_peer_proof_frame(
            &frame,
            &binding,
            &ExpectedPeer {
                role: PeerRole::Initiator,
                device_id: proof.device_id,
                local_device_id: proof.peer_device_id,
            },
        )
        .unwrap();
        assert_eq!(decoded, proof);
        assert_eq!(signature, vec![8; 64]);

        let mut noncanonical = frame;
        noncanonical.push(0);
        assert!(matches!(
            decode_peer_proof_frame(
                &noncanonical,
                &binding,
                &ExpectedPeer {
                    role: PeerRole::Initiator,
                    device_id: proof.device_id,
                    local_device_id: proof.peer_device_id,
                },
            ),
            Err(QuicError::InvalidPeerProof)
        ));
    }

    #[test]
    fn maximum_legal_peer_proof_fits_the_native_record_bound() {
        let ticket = vec![0xa5; MAX_DIRECT_TICKET_WIRE_BYTES];
        let binding = PeerBindingContext {
            ticket_sha256: sha256(&ticket),
            connection_id: [4; 16],
            path_epoch: 5,
        };
        let proof = PeerProofRequest {
            ticket_sha256: binding.ticket_sha256,
            role: PeerRole::Responder,
            device_id: [2; 16],
            peer_device_id: [3; 16],
            connection_id: binding.connection_id,
            path_epoch: binding.path_epoch,
            proof_nonce: vec![6; 32],
            tls_exporter_sha256: [7; 32],
        };
        let frame =
            encode_peer_proof_frame(&ticket, &proof, &[8; MAX_PEER_SIGNATURE_BYTES]).unwrap();
        assert!(frame.len() <= MAX_DIRECT_FRAME_WIRE_BYTES);
        assert_eq!(
            u32::from_be_bytes(frame[..4].try_into().unwrap()) as usize,
            frame.len() - 4
        );
        let (decoded, signature) = decode_peer_proof_frame(
            &frame,
            &binding,
            &ExpectedPeer {
                role: PeerRole::Responder,
                device_id: proof.device_id,
                local_device_id: proof.peer_device_id,
            },
        )
        .unwrap();
        assert_eq!(decoded, proof);
        assert_eq!(signature.len(), MAX_PEER_SIGNATURE_BYTES);
    }

    #[test]
    fn peer_transcripts_and_path_binding_match_cross_platform_vector() {
        let initiator = PeerProofRequest {
            ticket_sha256: hex("92a306fb506241f2075beaeffb6e174eddf39a6381d38022de58145b63fa387a"),
            role: PeerRole::Initiator,
            device_id: hex("20314253647548698a9bacbdcedff001"),
            peer_device_id: hex("304152637485496a8b9cadbecfe0f102"),
            connection_id: hex("4051627384954a6b8c9daebfd0e1f203"),
            path_epoch: 7,
            proof_nonce: hex::<16>("e0e1e2e3e4e5e6e7e8e9eaebecedeeef").to_vec(),
            tls_exporter_sha256: hex(
                "808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f",
            ),
        };
        let responder = PeerProofRequest {
            role: PeerRole::Responder,
            device_id: initiator.peer_device_id,
            peer_device_id: initiator.device_id,
            proof_nonce: hex::<16>("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff").to_vec(),
            ..initiator.clone()
        };
        let initiator_proof_sha256 = sha256(&initiator.transcript().unwrap());
        let responder_proof_sha256 = sha256(&responder.transcript().unwrap());
        assert_eq!(
            initiator_proof_sha256,
            hex("371974fb9e7160a43d973f24a5228b5485c98c9d6771fe7a5967798aa25ba63e")
        );
        assert_eq!(
            responder_proof_sha256,
            hex("921778b3c0ea8f115d210cda9fb463dd166405639b28b77d90122fc0c41e2bb7")
        );
        assert_eq!(
            direct_path_binding_sha256(DirectPathBindingInputs {
                ticket_sha256: initiator.ticket_sha256,
                initiator_candidate_id: hex("60718293a4b54c6d8e9fb0c1d2e3f405"),
                responder_candidate_id: hex("708192a3b4c54d6e8fa0c1d2e3f40516"),
                initiator_certificate_sha256: hex(
                    "202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f",
                ),
                responder_certificate_sha256: hex(
                    "404142434445464748494a4b4c4d4e4f505152535455565758595a5b5c5d5e5f",
                ),
                initiator_proof_sha256,
                responder_proof_sha256,
                tls_exporter_sha256: initiator.tls_exporter_sha256,
            }),
            hex("273f8d1452240bf4bb2f0496634532081ae7dec8db9ccfeb13b29e02bfd68f33")
        );
    }

    #[test]
    fn same_socket_quic_certificate_exporter_and_ping_echo() {
        let (server_udp, client_udp) = prepared_stun_and_probe_sockets();
        let server_addr = server_udp.bound_addr();
        let client_addr = client_udp.bound_addr();
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap();
        runtime.block_on(async {
            let server_identity = TemporaryIdentity::generate().unwrap();
            let server_fingerprint = server_identity.certificate_sha256();
            let client_identity = TemporaryIdentity::generate().unwrap();
            let client_fingerprint = client_identity.certificate_sha256();
            let server = Arc::new(
                ShadowEndpoint::server(
                    server_udp.into_quic_handoff(),
                    client_addr,
                    server_identity,
                    client_fingerprint,
                )
                .unwrap(),
            );
            assert_eq!(server.local_addr(), server_addr);
            assert_eq!(server.certificate_sha256(), server_fingerprint);

            let client = ShadowEndpoint::client(
                client_udp.into_quic_handoff(),
                server_addr,
                client_identity,
                server_fingerprint,
            )
            .unwrap();
            assert_eq!(client.local_addr(), client_addr);

            let server_endpoint = Arc::clone(&server);
            let (done_tx, done_rx) = tokio::sync::oneshot::channel();
            let accept = tokio::spawn(async move {
                let connection = server_endpoint
                    .accept_pinned(client_addr, client_fingerprint)
                    .await
                    .unwrap();
                let binding = PeerBindingContext {
                    ticket_sha256: [11; 32],
                    connection_id: [12; 16],
                    path_epoch: 13,
                };
                let exporter = tls_exporter_sha256(&connection, &binding).unwrap();
                let proof = PeerProofRequest {
                    ticket_sha256: binding.ticket_sha256,
                    role: PeerRole::Initiator,
                    device_id: [21; 16],
                    peer_device_id: [22; 16],
                    connection_id: binding.connection_id,
                    path_epoch: binding.path_epoch,
                    proof_nonce: vec![23; 16],
                    tls_exporter_sha256: exporter,
                };
                let authenticated = authenticate_shadow_connection(
                    connection,
                    &binding,
                    ExpectedPeer {
                        role: PeerRole::Initiator,
                        device_id: [21; 16],
                        local_device_id: [22; 16],
                    },
                    &proof,
                    &[8; 64],
                    &ExactVerifier(vec![8; 64]),
                )
                .unwrap();
                shadow_echo_once(&authenticated).await.unwrap();
                let _ = done_rx.await;
                exporter
            });
            let connection = client
                .connect_pinned(server_addr, server.certificate_sha256())
                .await
                .unwrap();
            let binding = PeerBindingContext {
                ticket_sha256: [11; 32],
                connection_id: [12; 16],
                path_epoch: 13,
            };
            let client_exporter = tls_exporter_sha256(&connection, &binding).unwrap();
            let proof = PeerProofRequest {
                ticket_sha256: binding.ticket_sha256,
                role: PeerRole::Responder,
                device_id: [22; 16],
                peer_device_id: [21; 16],
                connection_id: binding.connection_id,
                path_epoch: binding.path_epoch,
                proof_nonce: vec![24; 16],
                tls_exporter_sha256: client_exporter,
            };
            let authenticated = authenticate_shadow_connection(
                connection,
                &binding,
                ExpectedPeer {
                    role: PeerRole::Responder,
                    device_id: [22; 16],
                    local_device_id: [21; 16],
                },
                &proof,
                &[8; 64],
                &ExactVerifier(vec![8; 64]),
            )
            .unwrap();
            shadow_ping(&authenticated, [14; 16]).await.unwrap();
            let _ = done_tx.send(());
            let server_exporter = accept.await.unwrap();
            assert_eq!(server_exporter, client_exporter);
            client.close();
            server.close();
        });
    }

    #[test]
    fn tls_leaf_fingerprint_mismatch_never_connects() {
        let ip = active_unicast_ip();
        let server_udp = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let client_udp = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let server_addr = server_udp.bound_addr();
        let client_addr = client_udp.bound_addr();
        let server_identity = TemporaryIdentity::generate().unwrap();
        let client_identity = TemporaryIdentity::generate().unwrap();
        let client_fingerprint = client_identity.certificate_sha256();
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap();
        runtime.block_on(async {
            let server = Arc::new(
                ShadowEndpoint::server(
                    server_udp.into_quic_handoff(),
                    client_addr,
                    server_identity,
                    client_fingerprint,
                )
                .unwrap(),
            );
            let client = ShadowEndpoint::client(
                client_udp.into_quic_handoff(),
                server_addr,
                client_identity,
                [0x55; 32],
            )
            .unwrap();
            let accepting = {
                let server = Arc::clone(&server);
                tokio::spawn(
                    async move { server.accept_pinned(client_addr, client_fingerprint).await },
                )
            };
            assert!(matches!(
                client.connect_pinned(server_addr, [0x55; 32]).await,
                Err(QuicError::Connection(_)) | Err(QuicError::CertificatePinMismatch)
            ));
            accepting.abort();
            client.close();
            server.close();
        });
    }

    fn prepared_stun_and_probe_sockets() -> (DirectUdpSocket, DirectUdpSocket) {
        let ip = active_unicast_ip();
        let stun = std::net::UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let stun_addr = stun.local_addr().unwrap();
        let stun_thread = thread::spawn(move || {
            let mut request = [0_u8; 20];
            let (_, source) = stun.recv_from(&mut request).unwrap();
            let transaction_id = request[8..20].try_into().unwrap();
            stun.send_to(&stun_response(source, transaction_id), source)
                .unwrap();
        });

        let client = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let transaction = termlink_direct_nat::StunTransaction::from_id(*b"stunworkflow");
        client.send_stun_request(stun_addr, &transaction).unwrap();
        assert_eq!(
            client
                .receive_stun_response(stun_addr, &transaction, Duration::from_secs(1))
                .unwrap(),
            client.bound_addr()
        );
        stun_thread.join().unwrap();

        let server = DirectUdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let key = [42_u8; 32];
        let pair = CandidatePair {
            initiator_candidate_id: [1; 16],
            responder_candidate_id: [2; 16],
        };
        let client_probe = probe(pair.clone(), [7; 16], [8; 16], [9; 16]);
        let server_probe = probe(pair, [10; 16], [11; 16], [12; 16]);
        let client_wire = client_probe.encode_authenticated(&key).unwrap();
        let server_wire = server_probe.encode_authenticated(&key).unwrap();
        let server_ack =
            ConnectivityDatagram::acknowledgement(&client_probe, [10; 16], [13; 16], 10_001)
                .unwrap()
                .encode_ack_without_amplification(client_wire.len(), &key)
                .unwrap();
        let client_ack =
            ConnectivityDatagram::acknowledgement(&server_probe, [7; 16], [14; 16], 10_001)
                .unwrap()
                .encode_ack_without_amplification(server_wire.len(), &key)
                .unwrap();
        let client_addr = client.bound_addr();
        let server_addr = server.bound_addr();
        let expect_client = expected(&client_probe, DatagramKind::Probe, [7; 16], 10_000);
        let expect_server = expected(&server_probe, DatagramKind::Probe, [10; 16], 10_000);

        let server_task = thread::spawn(move || {
            server
                .simultaneous_probe(client_addr, &[server_wire], |packet| {
                    ConnectivityDatagram::decode_authenticated(packet, &key, &expect_client)
                        .is_ok()
                        .then(|| server_ack.clone())
                })
                .unwrap();
            server
        });
        let client_task = thread::spawn(move || {
            client
                .simultaneous_probe(server_addr, &[client_wire], |packet| {
                    ConnectivityDatagram::decode_authenticated(packet, &key, &expect_server)
                        .is_ok()
                        .then(|| client_ack.clone())
                })
                .unwrap();
            client
        });
        let server = server_task.join().unwrap();
        let client = client_task.join().unwrap();
        let mut buffer = [0_u8; termlink_direct_nat::MAX_PROBE_PACKET_BYTES];
        let (server_ack_len, server_ack_source) = server
            .receive_probe(&mut buffer, Duration::from_secs(1))
            .unwrap();
        assert_eq!(server_ack_source, client_addr);
        ConnectivityDatagram::decode_authenticated(
            &buffer[..server_ack_len],
            &key,
            &expected(&server_probe, DatagramKind::ProbeAck, [7; 16], 10_001),
        )
        .unwrap();
        let (client_ack_len, client_ack_source) = client
            .receive_probe(&mut buffer, Duration::from_secs(1))
            .unwrap();
        assert_eq!(client_ack_source, server_addr);
        ConnectivityDatagram::decode_authenticated(
            &buffer[..client_ack_len],
            &key,
            &expected(&client_probe, DatagramKind::ProbeAck, [10; 16], 10_001),
        )
        .unwrap();
        (server, client)
    }

    fn probe(
        pair: CandidatePair,
        sender_device_id: [u8; 16],
        probe_nonce: [u8; 16],
        padding: [u8; 16],
    ) -> ConnectivityDatagram {
        ConnectivityDatagram::new_probe(ProbeFields {
            connection_id: [3; 16],
            path_epoch: 4,
            ticket_id: [5; 16],
            nomination_id: [6; 16],
            pair,
            sender_device_id,
            probe_nonce,
            sent_at_ms: 10_000,
            anti_amplification_padding: padding,
        })
        .unwrap()
    }

    fn expected(
        datagram: &ConnectivityDatagram,
        kind: DatagramKind,
        sender_device_id: [u8; 16],
        now_ms: i64,
    ) -> ExpectedDatagram {
        ExpectedDatagram {
            kind,
            connection_id: datagram.connection_id,
            path_epoch: datagram.path_epoch,
            ticket_id: datagram.ticket_id,
            nomination_id: datagram.nomination_id,
            pair: datagram.pair.clone(),
            sender_device_id,
            probe_nonce: Some(datagram.probe_nonce),
            now_ms,
            max_clock_skew_ms: 30_000,
        }
    }

    fn active_unicast_ip() -> IpAddr {
        let socket = std::net::UdpSocket::bind("0.0.0.0:0").unwrap();
        socket.connect("192.0.2.1:9").unwrap();
        let ip = socket.local_addr().unwrap().ip();
        termlink_direct_nat::validate_candidate_endpoint(SocketAddr::new(ip, 49_152)).unwrap();
        ip
    }

    fn stun_response(mapped: SocketAddr, transaction_id: [u8; 12]) -> Vec<u8> {
        let IpAddr::V4(ip) = mapped.ip() else {
            panic!("test workflow currently requires IPv4")
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

    fn hex<const N: usize>(value: &str) -> [u8; N] {
        assert_eq!(value.len(), N * 2);
        let mut result = [0_u8; N];
        for (index, slot) in result.iter_mut().enumerate() {
            *slot = u8::from_str_radix(&value[index * 2..index * 2 + 2], 16).unwrap();
        }
        result
    }
}
