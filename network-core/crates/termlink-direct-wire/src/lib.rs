use std::fmt;

use ring::{digest, hmac, signature};
use subtle::ConstantTimeEq;

pub const MAGIC: &[u8; 5] = b"TLD1\n";
pub const MAGIC_V2: &[u8; 5] = b"TLD2\n";
pub const MAX_DATAGRAM_BYTES: usize = 512;
pub const PROTOCOL_VERSION: u32 = 1;
pub const PROTOCOL_VERSION_V2: u32 = 2;
pub const FIXED_ID_BYTES: usize = 16;
pub const AUTH_TAG_BYTES: usize = 16;
pub const PROBE_HMAC_DOMAIN: &[u8] = b"TERMLINK-DIRECT-PROBE-HMAC-V1\0";
pub const PROBE_HMAC_DOMAIN_V2: &[u8] = b"TERMLINK-DIRECT-PROBE-HMAC-V2\0";
pub const PAIR_PROBE_NONCE_DOMAIN_V2: &[u8] = b"TERMLINK-DIRECT-PAIR-PROBE-NONCE-V2\0";
pub const DIRECT_TICKET_DOMAIN: &[u8] = b"TERMLINK-DIRECT-TICKET-V1\n";
pub const MAX_DIRECT_TICKET_BYTES: usize = 2_048;
// Renewable 20-minute tickets (transport audit #2). The cap admits exactly
// one full lifetime; renewals slide the applied expiry forward instead of
// extending a single ticket beyond one generation.
pub const MAX_DIRECT_TICKET_LIFETIME_MS: i64 = 20 * 60 * 1_000;
pub const DIRECT_TICKET_CLOCK_SKEW_MS: i64 = 30_000;

/// Derives the direction-specific probe nonce for exactly one canonical
/// initiator/responder candidate pair in a TLD2 connectivity-check round.
///
/// `direction_key` is the sender's probe key. Callers must keep initiator and
/// responder candidate IDs in their protocol roles; swapping them deliberately
/// produces a different nonce and prevents a probe from being replayed onto a
/// different pair or direction.
pub fn derive_pair_probe_nonce_v2(
    direction_key: &[u8; 32],
    connectivity_nonce: &[u8; FIXED_ID_BYTES],
    pair: &CandidatePair,
) -> [u8; FIXED_ID_BYTES] {
    let mut input = Vec::with_capacity(PAIR_PROBE_NONCE_DOMAIN_V2.len() + FIXED_ID_BYTES * 3);
    input.extend_from_slice(PAIR_PROBE_NONCE_DOMAIN_V2);
    input.extend_from_slice(connectivity_nonce);
    input.extend_from_slice(&pair.initiator_candidate_id);
    input.extend_from_slice(&pair.responder_candidate_id);
    hmac_sha256(direction_key, &input)[..FIXED_ID_BYTES]
        .try_into()
        .expect("a truncated HMAC-SHA256 nonce is exactly 16 bytes")
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[repr(u32)]
pub enum DatagramKind {
    Probe = 1,
    ProbeAck = 2,
}

impl DatagramKind {
    fn from_wire(value: u64) -> Result<Self, WireError> {
        match value {
            1 => Ok(Self::Probe),
            2 => Ok(Self::ProbeAck),
            _ => Err(WireError::InvalidKind),
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CandidatePair {
    pub initiator_candidate_id: [u8; FIXED_ID_BYTES],
    pub responder_candidate_id: [u8; FIXED_ID_BYTES],
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ConnectivityDatagram {
    pub kind: DatagramKind,
    pub connection_id: [u8; FIXED_ID_BYTES],
    pub path_epoch: u64,
    pub ticket_id: [u8; FIXED_ID_BYTES],
    pub nomination_id: [u8; FIXED_ID_BYTES],
    pub pair: CandidatePair,
    pub sender_device_id: [u8; FIXED_ID_BYTES],
    pub probe_nonce: [u8; FIXED_ID_BYTES],
    pub response_nonce: Vec<u8>,
    pub sent_at_ms: i64,
    pub anti_amplification_padding: Vec<u8>,
    pub auth_tag: [u8; AUTH_TAG_BYTES],
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ProbeFields {
    pub connection_id: [u8; FIXED_ID_BYTES],
    pub path_epoch: u64,
    pub ticket_id: [u8; FIXED_ID_BYTES],
    pub nomination_id: [u8; FIXED_ID_BYTES],
    pub pair: CandidatePair,
    pub sender_device_id: [u8; FIXED_ID_BYTES],
    pub probe_nonce: [u8; FIXED_ID_BYTES],
    pub sent_at_ms: i64,
    pub anti_amplification_padding: [u8; FIXED_ID_BYTES],
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ExpectedDatagram {
    pub kind: DatagramKind,
    pub connection_id: [u8; FIXED_ID_BYTES],
    pub path_epoch: u64,
    pub ticket_id: [u8; FIXED_ID_BYTES],
    pub nomination_id: [u8; FIXED_ID_BYTES],
    pub pair: CandidatePair,
    pub sender_device_id: [u8; FIXED_ID_BYTES],
    pub probe_nonce: Option<[u8; FIXED_ID_BYTES]>,
    pub now_ms: i64,
    pub max_clock_skew_ms: u64,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum WireError {
    TooLarge,
    InvalidMagic,
    Truncated,
    NonCanonical,
    UnsupportedField,
    DuplicateField,
    InvalidProtocol,
    InvalidKind,
    InvalidLength,
    InvalidEpoch,
    StaleEpoch,
    InvalidTimestamp,
    ContextMismatch,
    AuthenticationFailed,
    AmplificationRisk,
}

impl fmt::Display for WireError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(match self {
            Self::TooLarge => "direct datagram exceeds the wire limit",
            Self::InvalidMagic => "direct datagram magic is invalid",
            Self::Truncated => "direct datagram is truncated",
            Self::NonCanonical => "direct datagram is not canonical",
            Self::UnsupportedField => "direct datagram contains an unsupported field",
            Self::DuplicateField => "direct datagram contains a duplicate field",
            Self::InvalidProtocol => "direct datagram protocol version is invalid",
            Self::InvalidKind => "direct datagram kind is invalid",
            Self::InvalidLength => "direct datagram field length is invalid",
            Self::InvalidEpoch => "direct datagram path epoch is invalid",
            Self::StaleEpoch => "direct datagram path epoch is stale",
            Self::InvalidTimestamp => "direct datagram timestamp is invalid",
            Self::ContextMismatch => "direct datagram does not match the active path",
            Self::AuthenticationFailed => "direct datagram authentication failed",
            Self::AmplificationRisk => "direct acknowledgement would amplify traffic",
        })
    }
}

impl std::error::Error for WireError {}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DirectTicketRole {
    Initiator,
    Responder,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct DirectTicketExpectation {
    pub connection_id: [u8; FIXED_ID_BYTES],
    pub path_epoch: u64,
    pub local_device_id: [u8; FIXED_ID_BYTES],
    pub peer_device_id: [u8; FIXED_ID_BYTES],
    pub local_role: DirectTicketRole,
    pub now_ms: i64,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct VerifiedDirectTicket {
    pub ticket_id: [u8; FIXED_ID_BYTES],
    pub account_id: [u8; FIXED_ID_BYTES],
    pub initiator_device_id: [u8; FIXED_ID_BYTES],
    pub responder_device_id: [u8; FIXED_ID_BYTES],
    pub connection_id: [u8; FIXED_ID_BYTES],
    pub path_epoch: u64,
    pub issued_at_ms: i64,
    pub expires_at_ms: i64,
    pub server_nonce: Vec<u8>,
    pub server_signing_key_id: Vec<u8>,
    pub ticket_sha256: [u8; 32],
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TicketError {
    TooLarge,
    Truncated,
    NonCanonical,
    InvalidShape,
    InvalidEpoch,
    InvalidTime,
    NotYetValid,
    Expired,
    ContextMismatch,
    Signature,
}

impl fmt::Display for TicketError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(match self {
            Self::TooLarge => "direct ticket exceeds the wire limit",
            Self::Truncated => "direct ticket is truncated",
            Self::NonCanonical => "direct ticket is not canonical",
            Self::InvalidShape => "direct ticket fields are invalid",
            Self::InvalidEpoch => "direct ticket path epoch is invalid",
            Self::InvalidTime => "direct ticket lifetime is invalid",
            Self::NotYetValid => "direct ticket is not yet valid",
            Self::Expired => "direct ticket has expired",
            Self::ContextMismatch => "direct ticket does not match the path attempt",
            Self::Signature => "direct ticket signature is invalid",
        })
    }
}

impl std::error::Error for TicketError {}

#[derive(Default)]
struct ParsedTicket {
    format_version: Option<u64>,
    ticket_id: Option<Vec<u8>>,
    account_id: Option<Vec<u8>>,
    initiator_device_id: Option<Vec<u8>>,
    responder_device_id: Option<Vec<u8>>,
    connection_id: Option<Vec<u8>>,
    path_epoch: Option<u64>,
    issued_at_ms: Option<u64>,
    expires_at_ms: Option<u64>,
    allowed_carriers: Option<Vec<u8>>,
    server_nonce: Option<Vec<u8>>,
    server_signing_key_id: Option<Vec<u8>>,
    server_signature: Option<Vec<u8>>,
}

/// Verifies the canonical protobuf ticket, its Ed25519 server signature, its
/// bounded lifetime, and the exact device-pair/connection/epoch binding.
/// Callers must not use an externally supplied ticket hash in place of this
/// verification.
pub fn verify_direct_path_ticket(
    packet: &[u8],
    server_signing_key: &[u8; 32],
    expected: &DirectTicketExpectation,
) -> Result<VerifiedDirectTicket, TicketError> {
    if packet.is_empty() {
        return Err(TicketError::Truncated);
    }
    if packet.len() > MAX_DIRECT_TICKET_BYTES {
        return Err(TicketError::TooLarge);
    }
    if expected.path_epoch == 0 || expected.path_epoch > i64::MAX as u64 {
        return Err(TicketError::InvalidEpoch);
    }

    let mut cursor = Cursor::new(packet);
    let mut parsed = ParsedTicket::default();
    let mut previous_field = 0_u64;
    let mut claims_end = None;
    while !cursor.is_empty() {
        let field_start = cursor.offset;
        let key = cursor.varint().map_err(map_ticket_wire_error)?;
        let number = key >> 3;
        let wire_type = key & 7;
        if number <= previous_field || !(1..=13).contains(&number) {
            return Err(TicketError::NonCanonical);
        }
        previous_field = number;
        match number {
            1 => {
                parsed.format_version = Some(ticket_varint(
                    &mut cursor,
                    wire_type,
                    parsed.format_version,
                )?)
            }
            2 => {
                parsed.ticket_id = Some(ticket_bytes(
                    &mut cursor,
                    wire_type,
                    parsed.ticket_id.as_deref(),
                )?)
            }
            3 => {
                parsed.account_id = Some(ticket_bytes(
                    &mut cursor,
                    wire_type,
                    parsed.account_id.as_deref(),
                )?)
            }
            4 => {
                parsed.initiator_device_id = Some(ticket_bytes(
                    &mut cursor,
                    wire_type,
                    parsed.initiator_device_id.as_deref(),
                )?)
            }
            5 => {
                parsed.responder_device_id = Some(ticket_bytes(
                    &mut cursor,
                    wire_type,
                    parsed.responder_device_id.as_deref(),
                )?)
            }
            6 => {
                parsed.connection_id = Some(ticket_bytes(
                    &mut cursor,
                    wire_type,
                    parsed.connection_id.as_deref(),
                )?)
            }
            7 => {
                parsed.path_epoch = Some(ticket_varint(&mut cursor, wire_type, parsed.path_epoch)?)
            }
            8 => {
                parsed.issued_at_ms =
                    Some(ticket_varint(&mut cursor, wire_type, parsed.issued_at_ms)?)
            }
            9 => {
                parsed.expires_at_ms =
                    Some(ticket_varint(&mut cursor, wire_type, parsed.expires_at_ms)?)
            }
            10 => {
                parsed.allowed_carriers = Some(ticket_bytes(
                    &mut cursor,
                    wire_type,
                    parsed.allowed_carriers.as_deref(),
                )?)
            }
            11 => {
                parsed.server_nonce = Some(ticket_bytes(
                    &mut cursor,
                    wire_type,
                    parsed.server_nonce.as_deref(),
                )?)
            }
            12 => {
                parsed.server_signing_key_id = Some(ticket_bytes(
                    &mut cursor,
                    wire_type,
                    parsed.server_signing_key_id.as_deref(),
                )?)
            }
            13 => {
                claims_end = Some(field_start);
                parsed.server_signature = Some(ticket_bytes(
                    &mut cursor,
                    wire_type,
                    parsed.server_signature.as_deref(),
                )?)
            }
            _ => return Err(TicketError::NonCanonical),
        }
    }

    let claims_end = claims_end.ok_or(TicketError::Truncated)?;
    if parsed.format_version != Some(1)
        || parsed.allowed_carriers.as_deref() != Some(&[1])
        || parsed
            .server_nonce
            .as_ref()
            .is_none_or(|value| value.len() != 16)
        || parsed
            .server_signing_key_id
            .as_ref()
            .is_none_or(|value| value.is_empty() || value.len() > 64)
        || parsed
            .server_signature
            .as_ref()
            .is_none_or(|value| value.len() != 64)
    {
        return Err(TicketError::InvalidShape);
    }

    let ticket_id = ticket_fixed(parsed.ticket_id)?;
    let account_id = ticket_fixed(parsed.account_id)?;
    let initiator_device_id = ticket_fixed(parsed.initiator_device_id)?;
    let responder_device_id = ticket_fixed(parsed.responder_device_id)?;
    let connection_id = ticket_fixed(parsed.connection_id)?;
    if [
        ticket_id,
        account_id,
        initiator_device_id,
        responder_device_id,
        connection_id,
    ]
    .contains(&[0; FIXED_ID_BYTES])
        || initiator_device_id == responder_device_id
    {
        return Err(TicketError::InvalidShape);
    }
    let path_epoch = parsed.path_epoch.ok_or(TicketError::Truncated)?;
    if path_epoch == 0 || path_epoch > i64::MAX as u64 {
        return Err(TicketError::InvalidEpoch);
    }
    let issued_at_ms = i64::try_from(parsed.issued_at_ms.ok_or(TicketError::Truncated)?)
        .map_err(|_| TicketError::InvalidTime)?;
    let expires_at_ms = i64::try_from(parsed.expires_at_ms.ok_or(TicketError::Truncated)?)
        .map_err(|_| TicketError::InvalidTime)?;
    if issued_at_ms < 0
        || expires_at_ms <= issued_at_ms
        || expires_at_ms - issued_at_ms > MAX_DIRECT_TICKET_LIFETIME_MS
    {
        return Err(TicketError::InvalidTime);
    }
    if expected.now_ms < issued_at_ms.saturating_sub(DIRECT_TICKET_CLOCK_SKEW_MS) {
        return Err(TicketError::NotYetValid);
    }
    if expected.now_ms > expires_at_ms {
        return Err(TicketError::Expired);
    }

    let (expected_local, expected_peer) = match expected.local_role {
        DirectTicketRole::Initiator => (initiator_device_id, responder_device_id),
        DirectTicketRole::Responder => (responder_device_id, initiator_device_id),
    };
    if path_epoch != expected.path_epoch
        || connection_id != expected.connection_id
        || expected_local != expected.local_device_id
        || expected_peer != expected.peer_device_id
    {
        return Err(TicketError::ContextMismatch);
    }

    let signature = parsed.server_signature.ok_or(TicketError::Truncated)?;
    let mut signed = Vec::with_capacity(DIRECT_TICKET_DOMAIN.len() + claims_end);
    signed.extend_from_slice(DIRECT_TICKET_DOMAIN);
    signed.extend_from_slice(&packet[..claims_end]);
    signature::UnparsedPublicKey::new(&signature::ED25519, server_signing_key)
        .verify(&signed, &signature)
        .map_err(|_| TicketError::Signature)?;

    Ok(VerifiedDirectTicket {
        ticket_id,
        account_id,
        initiator_device_id,
        responder_device_id,
        connection_id,
        path_epoch,
        issued_at_ms,
        expires_at_ms,
        server_nonce: parsed.server_nonce.ok_or(TicketError::Truncated)?,
        server_signing_key_id: parsed.server_signing_key_id.ok_or(TicketError::Truncated)?,
        ticket_sha256: sha256(packet),
    })
}

fn ticket_varint(
    cursor: &mut Cursor<'_>,
    wire_type: u64,
    existing: Option<u64>,
) -> Result<u64, TicketError> {
    read_unique_varint(cursor, wire_type, existing).map_err(map_ticket_wire_error)
}

fn ticket_bytes(
    cursor: &mut Cursor<'_>,
    wire_type: u64,
    existing: Option<&[u8]>,
) -> Result<Vec<u8>, TicketError> {
    read_unique_bytes(cursor, wire_type, existing).map_err(map_ticket_wire_error)
}

fn ticket_fixed(value: Option<Vec<u8>>) -> Result<[u8; FIXED_ID_BYTES], TicketError> {
    value
        .ok_or(TicketError::Truncated)?
        .try_into()
        .map_err(|_| TicketError::InvalidShape)
}

fn map_ticket_wire_error(error: WireError) -> TicketError {
    match error {
        WireError::TooLarge => TicketError::TooLarge,
        WireError::Truncated => TicketError::Truncated,
        _ => TicketError::NonCanonical,
    }
}

impl ConnectivityDatagram {
    pub fn new_probe(fields: ProbeFields) -> Result<Self, WireError> {
        let datagram = Self {
            kind: DatagramKind::Probe,
            connection_id: fields.connection_id,
            path_epoch: fields.path_epoch,
            ticket_id: fields.ticket_id,
            nomination_id: fields.nomination_id,
            pair: fields.pair,
            sender_device_id: fields.sender_device_id,
            probe_nonce: fields.probe_nonce,
            response_nonce: Vec::new(),
            sent_at_ms: fields.sent_at_ms,
            anti_amplification_padding: fields.anti_amplification_padding.to_vec(),
            auth_tag: [0; AUTH_TAG_BYTES],
        };
        datagram.validate_shape()?;
        Ok(datagram)
    }

    pub fn acknowledgement(
        probe: &Self,
        sender_device_id: [u8; FIXED_ID_BYTES],
        response_nonce: [u8; FIXED_ID_BYTES],
        sent_at_ms: i64,
    ) -> Result<Self, WireError> {
        if probe.kind != DatagramKind::Probe {
            return Err(WireError::InvalidKind);
        }
        probe.validate_shape()?;
        let datagram = Self {
            kind: DatagramKind::ProbeAck,
            connection_id: probe.connection_id,
            path_epoch: probe.path_epoch,
            ticket_id: probe.ticket_id,
            nomination_id: probe.nomination_id,
            pair: probe.pair.clone(),
            sender_device_id,
            probe_nonce: probe.probe_nonce,
            response_nonce: response_nonce.to_vec(),
            sent_at_ms,
            anti_amplification_padding: Vec::new(),
            auth_tag: [0; AUTH_TAG_BYTES],
        };
        datagram.validate_shape()?;
        Ok(datagram)
    }

    pub fn encode_authenticated(&self, key: &[u8; 32]) -> Result<Vec<u8>, WireError> {
        self.encode_authenticated_for(key, MAGIC, PROBE_HMAC_DOMAIN, PROTOCOL_VERSION)
    }

    pub fn encode_authenticated_v2(&self, key: &[u8; 32]) -> Result<Vec<u8>, WireError> {
        self.encode_authenticated_for(key, MAGIC_V2, PROBE_HMAC_DOMAIN_V2, PROTOCOL_VERSION_V2)
    }

    fn encode_authenticated_for(
        &self,
        key: &[u8; 32],
        magic: &[u8; 5],
        domain: &[u8],
        protocol_version: u32,
    ) -> Result<Vec<u8>, WireError> {
        self.validate_shape()?;
        let unsigned = self.encode_unsigned_for(protocol_version);
        let tag = hmac_sha256(key, &[domain, &unsigned].concat());
        let mut body = unsigned;
        put_bytes(13, &tag[..AUTH_TAG_BYTES], &mut body);
        let mut packet = Vec::with_capacity(magic.len() + body.len());
        packet.extend_from_slice(magic);
        packet.extend_from_slice(&body);
        if packet.len() > MAX_DATAGRAM_BYTES {
            return Err(WireError::TooLarge);
        }
        Ok(packet)
    }

    pub fn decode_authenticated(
        packet: &[u8],
        key: &[u8; 32],
        expected: &ExpectedDatagram,
    ) -> Result<Self, WireError> {
        Self::decode_authenticated_for(
            packet,
            key,
            expected,
            MAGIC,
            PROBE_HMAC_DOMAIN,
            PROTOCOL_VERSION,
        )
    }

    pub fn decode_authenticated_v2(
        packet: &[u8],
        key: &[u8; 32],
        expected: &ExpectedDatagram,
    ) -> Result<Self, WireError> {
        Self::decode_authenticated_for(
            packet,
            key,
            expected,
            MAGIC_V2,
            PROBE_HMAC_DOMAIN_V2,
            PROTOCOL_VERSION_V2,
        )
    }

    fn decode_authenticated_for(
        packet: &[u8],
        key: &[u8; 32],
        expected: &ExpectedDatagram,
        magic: &[u8; 5],
        domain: &[u8],
        protocol_version: u32,
    ) -> Result<Self, WireError> {
        let datagram = decode_for(packet, magic, protocol_version)?;
        // Epoch fencing deliberately precedes authentication. A packet is first
        // parsed by the bounded, canonical decoder above, then an older attempt
        // is discarded without trying the current attempt's key. Otherwise a
        // legitimate delayed epoch-N packet authenticated with epoch-N's key
        // would look like an authentication attack against epoch N+1 and could
        // incorrectly retire the live direct attempt.
        if datagram.path_epoch < expected.path_epoch {
            return Err(WireError::StaleEpoch);
        }
        if datagram.path_epoch > expected.path_epoch {
            return Err(WireError::InvalidEpoch);
        }
        let unsigned = datagram.encode_unsigned_for(protocol_version);
        let expected_tag = hmac_sha256(key, &[domain, &unsigned].concat());
        if !bool::from(datagram.auth_tag.ct_eq(&expected_tag[..AUTH_TAG_BYTES])) {
            return Err(WireError::AuthenticationFailed);
        }
        datagram.validate_expected(expected)?;
        Ok(datagram)
    }

    pub fn encode_ack_without_amplification(
        &self,
        request_wire_len: usize,
        key: &[u8; 32],
    ) -> Result<Vec<u8>, WireError> {
        self.encode_ack_without_amplification_for(request_wire_len, key, false)
    }

    pub fn encode_ack_without_amplification_v2(
        &self,
        request_wire_len: usize,
        key: &[u8; 32],
    ) -> Result<Vec<u8>, WireError> {
        self.encode_ack_without_amplification_for(request_wire_len, key, true)
    }

    fn encode_ack_without_amplification_for(
        &self,
        request_wire_len: usize,
        key: &[u8; 32],
        v2: bool,
    ) -> Result<Vec<u8>, WireError> {
        if self.kind != DatagramKind::ProbeAck {
            return Err(WireError::InvalidKind);
        }
        let encoded = if v2 {
            self.encode_authenticated_v2(key)?
        } else {
            self.encode_authenticated(key)?
        };
        if encoded.len() > request_wire_len {
            return Err(WireError::AmplificationRisk);
        }
        Ok(encoded)
    }

    fn validate_shape(&self) -> Result<(), WireError> {
        if self.path_epoch == 0 || self.path_epoch > i64::MAX as u64 {
            return Err(WireError::InvalidEpoch);
        }
        if self.sent_at_ms < 0 {
            return Err(WireError::InvalidTimestamp);
        }
        match self.kind {
            DatagramKind::Probe => {
                if !self.response_nonce.is_empty()
                    || self.anti_amplification_padding.len() != FIXED_ID_BYTES
                {
                    return Err(WireError::InvalidLength);
                }
            }
            DatagramKind::ProbeAck => {
                if self.response_nonce.len() != FIXED_ID_BYTES
                    || self.response_nonce.iter().all(|value| *value == 0)
                    || !self.anti_amplification_padding.is_empty()
                {
                    return Err(WireError::InvalidLength);
                }
            }
        }
        Ok(())
    }

    fn validate_expected(&self, expected: &ExpectedDatagram) -> Result<(), WireError> {
        self.validate_shape()?;
        if self.path_epoch < expected.path_epoch {
            return Err(WireError::StaleEpoch);
        }
        if self.path_epoch > expected.path_epoch {
            return Err(WireError::InvalidEpoch);
        }
        if self.kind != expected.kind
            || self.connection_id != expected.connection_id
            || self.ticket_id != expected.ticket_id
            || self.nomination_id != expected.nomination_id
            || self.pair != expected.pair
            || self.sender_device_id != expected.sender_device_id
            || expected
                .probe_nonce
                .is_some_and(|nonce| nonce != self.probe_nonce)
        {
            return Err(WireError::ContextMismatch);
        }
        if self.sent_at_ms.abs_diff(expected.now_ms) > expected.max_clock_skew_ms {
            return Err(WireError::InvalidTimestamp);
        }
        Ok(())
    }

    fn encode_unsigned_for(&self, protocol_version: u32) -> Vec<u8> {
        let mut body = Vec::with_capacity(192);
        put_varint_field(1, u64::from(protocol_version), &mut body);
        put_varint_field(2, self.kind as u64, &mut body);
        put_bytes(3, &self.connection_id, &mut body);
        put_varint_field(4, self.path_epoch, &mut body);
        put_bytes(5, &self.ticket_id, &mut body);
        put_bytes(6, &self.nomination_id, &mut body);
        let mut pair = Vec::with_capacity(36);
        put_bytes(1, &self.pair.initiator_candidate_id, &mut pair);
        put_bytes(2, &self.pair.responder_candidate_id, &mut pair);
        put_bytes(7, &pair, &mut body);
        put_bytes(8, &self.sender_device_id, &mut body);
        put_bytes(9, &self.probe_nonce, &mut body);
        if !self.response_nonce.is_empty() {
            put_bytes(10, &self.response_nonce, &mut body);
        }
        put_varint_field(11, self.sent_at_ms as u64, &mut body);
        if !self.anti_amplification_padding.is_empty() {
            put_bytes(12, &self.anti_amplification_padding, &mut body);
        }
        body
    }
}

#[cfg(test)]
fn decode(packet: &[u8]) -> Result<ConnectivityDatagram, WireError> {
    decode_for(packet, MAGIC, PROTOCOL_VERSION)
}

fn decode_for(
    packet: &[u8],
    magic: &[u8; 5],
    protocol_version: u32,
) -> Result<ConnectivityDatagram, WireError> {
    if packet.len() > MAX_DATAGRAM_BYTES {
        return Err(WireError::TooLarge);
    }
    let body = packet.strip_prefix(magic).ok_or(WireError::InvalidMagic)?;
    let mut cursor = Cursor::new(body);
    let mut fields = ParsedFields::default();
    while !cursor.is_empty() {
        let key = cursor.varint()?;
        if key == 0 {
            return Err(WireError::NonCanonical);
        }
        let number = key >> 3;
        let wire_type = key & 7;
        match number {
            1 => {
                fields.protocol = Some(read_unique_varint(&mut cursor, wire_type, fields.protocol)?)
            }
            2 => fields.kind = Some(read_unique_varint(&mut cursor, wire_type, fields.kind)?),
            3 => {
                fields.connection_id = Some(read_unique_bytes(
                    &mut cursor,
                    wire_type,
                    fields.connection_id.as_deref(),
                )?)
            }
            4 => fields.epoch = Some(read_unique_varint(&mut cursor, wire_type, fields.epoch)?),
            5 => {
                fields.ticket_id = Some(read_unique_bytes(
                    &mut cursor,
                    wire_type,
                    fields.ticket_id.as_deref(),
                )?)
            }
            6 => {
                fields.nomination_id = Some(read_unique_bytes(
                    &mut cursor,
                    wire_type,
                    fields.nomination_id.as_deref(),
                )?)
            }
            7 => {
                fields.pair = Some(read_unique_bytes(
                    &mut cursor,
                    wire_type,
                    fields.pair.as_deref(),
                )?)
            }
            8 => {
                fields.sender = Some(read_unique_bytes(
                    &mut cursor,
                    wire_type,
                    fields.sender.as_deref(),
                )?)
            }
            9 => {
                fields.probe_nonce = Some(read_unique_bytes(
                    &mut cursor,
                    wire_type,
                    fields.probe_nonce.as_deref(),
                )?)
            }
            10 => {
                fields.response_nonce = Some(read_unique_bytes(
                    &mut cursor,
                    wire_type,
                    fields.response_nonce.as_deref(),
                )?)
            }
            11 => {
                fields.sent_at_ms = Some(read_unique_varint(
                    &mut cursor,
                    wire_type,
                    fields.sent_at_ms,
                )?)
            }
            12 => {
                fields.padding = Some(read_unique_bytes(
                    &mut cursor,
                    wire_type,
                    fields.padding.as_deref(),
                )?)
            }
            13 => {
                fields.auth_tag = Some(read_unique_bytes(
                    &mut cursor,
                    wire_type,
                    fields.auth_tag.as_deref(),
                )?)
            }
            _ => return Err(WireError::UnsupportedField),
        }
    }

    if fields.protocol != Some(u64::from(protocol_version)) {
        return Err(WireError::InvalidProtocol);
    }
    let sent_at = fields.sent_at_ms.ok_or(WireError::Truncated)?;
    let sent_at_ms = i64::try_from(sent_at).map_err(|_| WireError::InvalidTimestamp)?;
    let datagram = ConnectivityDatagram {
        kind: DatagramKind::from_wire(fields.kind.ok_or(WireError::Truncated)?)?,
        connection_id: fixed(fields.connection_id)?,
        path_epoch: fields.epoch.ok_or(WireError::Truncated)?,
        ticket_id: fixed(fields.ticket_id)?,
        nomination_id: fixed(fields.nomination_id)?,
        pair: decode_pair(&fields.pair.ok_or(WireError::Truncated)?)?,
        sender_device_id: fixed(fields.sender)?,
        probe_nonce: fixed(fields.probe_nonce)?,
        response_nonce: fields.response_nonce.unwrap_or_default(),
        sent_at_ms,
        anti_amplification_padding: fields.padding.unwrap_or_default(),
        auth_tag: fixed(fields.auth_tag)?,
    };
    datagram.validate_shape()?;
    let mut canonical = Vec::with_capacity(packet.len());
    canonical.extend_from_slice(magic);
    canonical.extend_from_slice(&datagram.encode_unsigned_for(protocol_version));
    put_bytes(13, &datagram.auth_tag, &mut canonical);
    if canonical != packet {
        return Err(WireError::NonCanonical);
    }
    Ok(datagram)
}

#[derive(Default)]
struct ParsedFields {
    protocol: Option<u64>,
    kind: Option<u64>,
    connection_id: Option<Vec<u8>>,
    epoch: Option<u64>,
    ticket_id: Option<Vec<u8>>,
    nomination_id: Option<Vec<u8>>,
    pair: Option<Vec<u8>>,
    sender: Option<Vec<u8>>,
    probe_nonce: Option<Vec<u8>>,
    response_nonce: Option<Vec<u8>>,
    sent_at_ms: Option<u64>,
    padding: Option<Vec<u8>>,
    auth_tag: Option<Vec<u8>>,
}

fn decode_pair(bytes: &[u8]) -> Result<CandidatePair, WireError> {
    let mut cursor = Cursor::new(bytes);
    let mut initiator = None;
    let mut responder = None;
    while !cursor.is_empty() {
        let key = cursor.varint()?;
        match (key >> 3, key & 7) {
            (1, wire_type) => {
                initiator = Some(read_unique_bytes(
                    &mut cursor,
                    wire_type,
                    initiator.as_deref(),
                )?)
            }
            (2, wire_type) => {
                responder = Some(read_unique_bytes(
                    &mut cursor,
                    wire_type,
                    responder.as_deref(),
                )?)
            }
            _ => return Err(WireError::UnsupportedField),
        }
    }
    Ok(CandidatePair {
        initiator_candidate_id: fixed(initiator)?,
        responder_candidate_id: fixed(responder)?,
    })
}

fn read_unique_varint(
    cursor: &mut Cursor<'_>,
    wire_type: u64,
    existing: Option<u64>,
) -> Result<u64, WireError> {
    if existing.is_some() {
        return Err(WireError::DuplicateField);
    }
    if wire_type != 0 {
        return Err(WireError::NonCanonical);
    }
    cursor.varint()
}

fn read_unique_bytes(
    cursor: &mut Cursor<'_>,
    wire_type: u64,
    existing: Option<&[u8]>,
) -> Result<Vec<u8>, WireError> {
    if existing.is_some() {
        return Err(WireError::DuplicateField);
    }
    if wire_type != 2 {
        return Err(WireError::NonCanonical);
    }
    Ok(cursor.bytes()?.to_vec())
}

fn fixed<const N: usize>(value: Option<Vec<u8>>) -> Result<[u8; N], WireError> {
    value
        .ok_or(WireError::Truncated)?
        .try_into()
        .map_err(|_| WireError::InvalidLength)
}

fn put_varint_field(field: u64, value: u64, output: &mut Vec<u8>) {
    put_varint(field << 3, output);
    put_varint(value, output);
}

fn put_bytes(field: u64, value: &[u8], output: &mut Vec<u8>) {
    put_varint((field << 3) | 2, output);
    put_varint(value.len() as u64, output);
    output.extend_from_slice(value);
}

fn put_varint(mut value: u64, output: &mut Vec<u8>) {
    while value >= 0x80 {
        output.push((value as u8) | 0x80);
        value >>= 7;
    }
    output.push(value as u8);
}

struct Cursor<'a> {
    bytes: &'a [u8],
    offset: usize,
}

impl<'a> Cursor<'a> {
    fn new(bytes: &'a [u8]) -> Self {
        Self { bytes, offset: 0 }
    }

    fn is_empty(&self) -> bool {
        self.offset == self.bytes.len()
    }

    fn varint(&mut self) -> Result<u64, WireError> {
        let start = self.offset;
        let mut value = 0_u64;
        for shift in (0..70).step_by(7) {
            let byte = *self.bytes.get(self.offset).ok_or(WireError::Truncated)?;
            self.offset += 1;
            if shift == 63 && byte > 1 {
                return Err(WireError::NonCanonical);
            }
            value |= u64::from(byte & 0x7f) << shift;
            if byte & 0x80 == 0 {
                if self.offset - start != varint_len(value) {
                    return Err(WireError::NonCanonical);
                }
                return Ok(value);
            }
        }
        Err(WireError::NonCanonical)
    }

    fn bytes(&mut self) -> Result<&'a [u8], WireError> {
        let length = usize::try_from(self.varint()?).map_err(|_| WireError::TooLarge)?;
        let end = self.offset.checked_add(length).ok_or(WireError::TooLarge)?;
        let value = self
            .bytes
            .get(self.offset..end)
            .ok_or(WireError::Truncated)?;
        self.offset = end;
        Ok(value)
    }
}

fn varint_len(mut value: u64) -> usize {
    let mut length = 1;
    while value >= 0x80 {
        value >>= 7;
        length += 1;
    }
    length
}

fn hmac_sha256(key: &[u8], message: &[u8]) -> [u8; 32] {
    hmac::sign(&hmac::Key::new(hmac::HMAC_SHA256, key), message)
        .as_ref()
        .try_into()
        .expect("HMAC-SHA256 has a fixed 32-byte output")
}

pub fn sha256(message: &[u8]) -> [u8; 32] {
    digest::digest(&digest::SHA256, message)
        .as_ref()
        .try_into()
        .expect("SHA-256 has a fixed 32-byte output")
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fields() -> ProbeFields {
        ProbeFields {
            connection_id: [1; 16],
            path_epoch: 9,
            ticket_id: [2; 16],
            nomination_id: [3; 16],
            pair: CandidatePair {
                initiator_candidate_id: [4; 16],
                responder_candidate_id: [5; 16],
            },
            sender_device_id: [6; 16],
            probe_nonce: [7; 16],
            sent_at_ms: 1_000,
            anti_amplification_padding: [8; 16],
        }
    }

    fn expected(datagram: &ConnectivityDatagram, now_ms: i64) -> ExpectedDatagram {
        ExpectedDatagram {
            kind: datagram.kind,
            connection_id: datagram.connection_id,
            path_epoch: datagram.path_epoch,
            ticket_id: datagram.ticket_id,
            nomination_id: datagram.nomination_id,
            pair: datagram.pair.clone(),
            sender_device_id: datagram.sender_device_id,
            probe_nonce: Some(datagram.probe_nonce),
            now_ms,
            max_clock_skew_ms: 30_000,
        }
    }

    #[test]
    fn sha256_and_hmac_match_standard_vectors() {
        assert_eq!(
            sha256(b"abc"),
            hex("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")
        );
        assert_eq!(
            hmac_sha256(&[0x0b; 20], b"Hi There"),
            hex("b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7")
        );
    }

    #[test]
    fn probe_and_ack_are_authenticated_without_amplification() {
        let key = [42; 32];
        let probe = ConnectivityDatagram::new_probe(fields()).unwrap();
        let probe_wire = probe.encode_authenticated(&key).unwrap();
        let decoded =
            ConnectivityDatagram::decode_authenticated(&probe_wire, &key, &expected(&probe, 1_020))
                .unwrap();
        let ack =
            ConnectivityDatagram::acknowledgement(&decoded, [9; 16], [10; 16], 1_021).unwrap();
        let ack_wire = ack
            .encode_ack_without_amplification(probe_wire.len(), &key)
            .unwrap();
        assert!(ack_wire.len() <= probe_wire.len());
        assert_eq!(ack.anti_amplification_padding, Vec::<u8>::new());
        assert_eq!(ack.response_nonce.len(), 16);
    }

    #[test]
    fn tld2_pair_nonce_is_deterministic_directional_and_pair_bound() {
        let pair = fields().pair;
        let connectivity_nonce = [0x31; FIXED_ID_BYTES];
        let initiator_key = [0x41; 32];
        let responder_key = [0x42; 32];
        let nonce = derive_pair_probe_nonce_v2(&initiator_key, &connectivity_nonce, &pair);

        assert_eq!(nonce, hex("af42560a1914b262f2555f24c7598ddc"));
        assert_eq!(
            nonce,
            derive_pair_probe_nonce_v2(&initiator_key, &connectivity_nonce, &pair)
        );
        assert_ne!(
            nonce,
            derive_pair_probe_nonce_v2(&responder_key, &connectivity_nonce, &pair)
        );

        let mut wrong_round = connectivity_nonce;
        wrong_round[0] ^= 1;
        assert_ne!(
            nonce,
            derive_pair_probe_nonce_v2(&initiator_key, &wrong_round, &pair)
        );

        let swapped = CandidatePair {
            initiator_candidate_id: pair.responder_candidate_id,
            responder_candidate_id: pair.initiator_candidate_id,
        };
        assert_ne!(
            nonce,
            derive_pair_probe_nonce_v2(&initiator_key, &connectivity_nonce, &swapped)
        );
    }

    #[test]
    fn tld1_and_tld2_are_magic_version_and_hmac_domain_isolated() {
        let key = [42; 32];
        let probe = ConnectivityDatagram::new_probe(fields()).unwrap();
        let expected = expected(&probe, probe.sent_at_ms);
        let v1 = probe.encode_authenticated(&key).unwrap();
        let v2 = probe.encode_authenticated_v2(&key).unwrap();

        assert!(v1.starts_with(MAGIC));
        assert!(v2.starts_with(MAGIC_V2));
        assert_ne!(v1, v2);
        assert_eq!(
            ConnectivityDatagram::decode_authenticated_v2(&v1, &key, &expected),
            Err(WireError::InvalidMagic)
        );
        assert_eq!(
            ConnectivityDatagram::decode_authenticated(&v2, &key, &expected),
            Err(WireError::InvalidMagic)
        );

        let mut v1_with_v2_magic = v1;
        v1_with_v2_magic[..MAGIC_V2.len()].copy_from_slice(MAGIC_V2);
        assert_eq!(
            ConnectivityDatagram::decode_authenticated_v2(&v1_with_v2_magic, &key, &expected,),
            Err(WireError::InvalidProtocol)
        );
    }

    #[test]
    fn tld2_rejects_wrong_pair_and_nonce_and_ack_never_amplifies() {
        let send_key = [42; 32];
        let receive_key = [43; 32];
        let connectivity_nonce = [0x31; FIXED_ID_BYTES];
        let mut probe_fields = fields();
        probe_fields.probe_nonce =
            derive_pair_probe_nonce_v2(&send_key, &connectivity_nonce, &probe_fields.pair);
        let probe = ConnectivityDatagram::new_probe(probe_fields).unwrap();
        let probe_wire = probe.encode_authenticated_v2(&send_key).unwrap();

        let mut wrong_pair = expected(&probe, probe.sent_at_ms);
        wrong_pair.pair.responder_candidate_id[0] ^= 1;
        assert_eq!(
            ConnectivityDatagram::decode_authenticated_v2(&probe_wire, &send_key, &wrong_pair,),
            Err(WireError::ContextMismatch)
        );

        let mut wrong_nonce = expected(&probe, probe.sent_at_ms);
        wrong_nonce.probe_nonce.as_mut().unwrap()[0] ^= 1;
        assert_eq!(
            ConnectivityDatagram::decode_authenticated_v2(&probe_wire, &send_key, &wrong_nonce,),
            Err(WireError::ContextMismatch)
        );

        let decoded = ConnectivityDatagram::decode_authenticated_v2(
            &probe_wire,
            &send_key,
            &expected(&probe, probe.sent_at_ms),
        )
        .unwrap();
        let ack = ConnectivityDatagram::acknowledgement(
            &decoded,
            [9; FIXED_ID_BYTES],
            [10; FIXED_ID_BYTES],
            probe.sent_at_ms + 1,
        )
        .unwrap();
        let ack_wire = ack
            .encode_ack_without_amplification_v2(probe_wire.len(), &receive_key)
            .unwrap();
        assert!(ack_wire.len() <= probe_wire.len());

        let mut expected_ack = expected(&ack, ack.sent_at_ms);
        expected_ack.sender_device_id = [9; FIXED_ID_BYTES];
        ConnectivityDatagram::decode_authenticated_v2(&ack_wire, &receive_key, &expected_ack)
            .unwrap();
        assert_eq!(
            ConnectivityDatagram::decode_authenticated_v2(&ack_wire, &send_key, &expected_ack,),
            Err(WireError::AuthenticationFailed)
        );
    }

    #[test]
    fn rejects_wrong_authentication_context_and_stale_epoch() {
        let key = [42; 32];
        let probe = ConnectivityDatagram::new_probe(fields()).unwrap();
        let mut wire = probe.encode_authenticated(&key).unwrap();
        *wire.last_mut().unwrap() ^= 1;
        assert_eq!(
            ConnectivityDatagram::decode_authenticated(&wire, &key, &expected(&probe, 1_000)),
            Err(WireError::AuthenticationFailed)
        );

        let wire = probe.encode_authenticated(&key).unwrap();
        let mut wrong_epoch = expected(&probe, 1_000);
        wrong_epoch.path_epoch += 1;
        assert_eq!(
            ConnectivityDatagram::decode_authenticated(&wire, &key, &wrong_epoch),
            Err(WireError::StaleEpoch)
        );
    }

    #[test]
    fn old_probe_and_ack_are_fenced_before_the_current_epoch_key() {
        let old_key = [41; 32];
        let current_key = [42; 32];
        let old_probe = ConnectivityDatagram::new_probe(fields()).unwrap();
        let old_probe_wire = old_probe.encode_authenticated(&old_key).unwrap();
        let old_ack = ConnectivityDatagram::acknowledgement(
            &old_probe,
            [9; 16],
            [10; 16],
            old_probe.sent_at_ms + 1,
        )
        .unwrap();
        let old_ack_wire = old_ack
            .encode_ack_without_amplification(old_probe_wire.len(), &old_key)
            .unwrap();

        let mut current_probe = expected(&old_probe, old_probe.sent_at_ms);
        current_probe.path_epoch = old_probe.path_epoch + 1;
        assert_eq!(
            ConnectivityDatagram::decode_authenticated(
                &old_probe_wire,
                &current_key,
                &current_probe,
            ),
            Err(WireError::StaleEpoch)
        );

        let mut current_ack = expected(&old_ack, old_ack.sent_at_ms);
        current_ack.path_epoch = old_ack.path_epoch + 1;
        assert_eq!(
            ConnectivityDatagram::decode_authenticated(&old_ack_wire, &current_key, &current_ack),
            Err(WireError::StaleEpoch)
        );
    }

    #[test]
    fn verifies_the_server_ticket_vector_and_exact_path_binding() {
        let ticket = base64url(
            "CAESEAARIjNEVUZ3iJmqu8zd7v8aEBAhMkNUZUdoiZqrvM3e7_AiECAxQlNkdUhpipusvc7f8AEqEDBBUmN0hUlqi5ytvs_g8QIyEEBRYnOElUprjJ2uv9Dh8gM4B0CA8MyGpjdIwJnUhqY3UgEBWhAAAQIDBAUGBwgJCgsMDQ4PYhBkaXJlY3QtdjEtdmVjdG9yakCwJTg3vrjJ8h6M6z4BR3XkogSgH3TQI4jp2mBhQTUyJWcO36MFe3MVh3Zxk-uAFShR9t2MynoQJEk4LrR53KkI",
        );
        let server_key = base64url("A6EHv_POEL4dcN0Y50vAmWfk1jCbpQ1fHdyGZBJVMbg");
        let expectation = DirectTicketExpectation {
            connection_id: [
                0x40, 0x51, 0x62, 0x73, 0x84, 0x95, 0x4a, 0x6b, 0x8c, 0x9d, 0xae, 0xbf, 0xd0, 0xe1,
                0xf2, 0x03,
            ],
            path_epoch: 7,
            local_device_id: [
                0x20, 0x31, 0x42, 0x53, 0x64, 0x75, 0x48, 0x69, 0x8a, 0x9b, 0xac, 0xbd, 0xce, 0xdf,
                0xf0, 0x01,
            ],
            peer_device_id: [
                0x30, 0x41, 0x52, 0x63, 0x74, 0x85, 0x49, 0x6a, 0x8b, 0x9c, 0xad, 0xbe, 0xcf, 0xe0,
                0xf1, 0x02,
            ],
            local_role: DirectTicketRole::Initiator,
            now_ms: 1_900_000_060_000,
        };
        let verified = verify_direct_path_ticket(
            &ticket,
            server_key.as_slice().try_into().unwrap(),
            &expectation,
        )
        .unwrap();
        assert_eq!(
            verified.ticket_sha256,
            hex("92a306fb506241f2075beaeffb6e174eddf39a6381d38022de58145b63fa387a")
        );
        assert_eq!(verified.path_epoch, 7);

        let mut tampered = ticket;
        *tampered.last_mut().unwrap() ^= 1;
        assert_eq!(
            verify_direct_path_ticket(
                &tampered,
                server_key.as_slice().try_into().unwrap(),
                &expectation,
            ),
            Err(TicketError::Signature)
        );
    }

    #[test]
    fn ticket_context_expiry_and_epoch_mismatch_never_validate() {
        let ticket = base64url(
            "CAESEAARIjNEVUZ3iJmqu8zd7v8aEBAhMkNUZUdoiZqrvM3e7_AiECAxQlNkdUhpipusvc7f8AEqEDBBUmN0hUlqi5ytvs_g8QIyEEBRYnOElUprjJ2uv9Dh8gM4B0CA8MyGpjdIwJnUhqY3UgEBWhAAAQIDBAUGBwgJCgsMDQ4PYhBkaXJlY3QtdjEtdmVjdG9yakCwJTg3vrjJ8h6M6z4BR3XkogSgH3TQI4jp2mBhQTUyJWcO36MFe3MVh3Zxk-uAFShR9t2MynoQJEk4LrR53KkI",
        );
        let server_key = base64url("A6EHv_POEL4dcN0Y50vAmWfk1jCbpQ1fHdyGZBJVMbg");
        let mut expectation = DirectTicketExpectation {
            connection_id: [
                0x40, 0x51, 0x62, 0x73, 0x84, 0x95, 0x4a, 0x6b, 0x8c, 0x9d, 0xae, 0xbf, 0xd0, 0xe1,
                0xf2, 0x03,
            ],
            path_epoch: 8,
            local_device_id: [
                0x20, 0x31, 0x42, 0x53, 0x64, 0x75, 0x48, 0x69, 0x8a, 0x9b, 0xac, 0xbd, 0xce, 0xdf,
                0xf0, 0x01,
            ],
            peer_device_id: [
                0x30, 0x41, 0x52, 0x63, 0x74, 0x85, 0x49, 0x6a, 0x8b, 0x9c, 0xad, 0xbe, 0xcf, 0xe0,
                0xf1, 0x02,
            ],
            local_role: DirectTicketRole::Initiator,
            now_ms: 1_900_000_060_000,
        };
        assert_eq!(
            verify_direct_path_ticket(
                &ticket,
                server_key.as_slice().try_into().unwrap(),
                &expectation,
            ),
            Err(TicketError::ContextMismatch)
        );
        expectation.path_epoch = 7;
        expectation.now_ms = 1_900_000_120_001;
        assert_eq!(
            verify_direct_path_ticket(
                &ticket,
                server_key.as_slice().try_into().unwrap(),
                &expectation,
            ),
            Err(TicketError::Expired)
        );
    }

    #[test]
    fn rejects_noncanonical_unknown_and_duplicate_fields() {
        let key = [42; 32];
        let probe = ConnectivityDatagram::new_probe(fields()).unwrap();
        let mut unknown = probe.encode_authenticated(&key).unwrap();
        unknown.extend_from_slice(&[0xa0, 0x06, 0x01]);
        assert_eq!(decode(&unknown), Err(WireError::UnsupportedField));

        let mut duplicate = probe.encode_authenticated(&key).unwrap();
        duplicate.extend_from_slice(&[0x08, 0x01]);
        assert_eq!(decode(&duplicate), Err(WireError::DuplicateField));

        let canonical = probe.encode_authenticated(&key).unwrap();
        let mut reordered = Vec::with_capacity(canonical.len());
        reordered.extend_from_slice(MAGIC);
        reordered.extend_from_slice(&canonical[MAGIC.len() + 2..MAGIC.len() + 4]);
        reordered.extend_from_slice(&canonical[MAGIC.len()..MAGIC.len() + 2]);
        reordered.extend_from_slice(&canonical[MAGIC.len() + 4..]);
        assert_eq!(decode(&reordered), Err(WireError::NonCanonical));

        let mut reordered_pair = canonical;
        let pair_header = reordered_pair
            .windows(4)
            .position(|window| window == [0x3a, 0x24, 0x0a, 0x10])
            .expect("canonical candidate pair");
        let pair_start = pair_header + 2;
        let first = reordered_pair[pair_start..pair_start + 18].to_vec();
        let second = reordered_pair[pair_start + 18..pair_start + 36].to_vec();
        reordered_pair[pair_start..pair_start + 18].copy_from_slice(&second);
        reordered_pair[pair_start + 18..pair_start + 36].copy_from_slice(&first);
        assert_eq!(decode(&reordered_pair), Err(WireError::NonCanonical));
    }

    fn hex<const N: usize>(value: &str) -> [u8; N] {
        let mut bytes = [0_u8; N];
        for (index, slot) in bytes.iter_mut().enumerate() {
            *slot = u8::from_str_radix(&value[index * 2..index * 2 + 2], 16).unwrap();
        }
        bytes
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
