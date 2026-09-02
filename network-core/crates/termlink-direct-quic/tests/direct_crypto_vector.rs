use std::{fs, path::PathBuf};

use ring::{aead, digest, hkdf};
use serde_json::Value;
use termlink_direct_quic::{
    DirectPathBindingInputs, Ed25519PeerProofVerifier, MAX_DIRECT_FRAME_WIRE_BYTES,
    PeerBindingContext, PeerProofRequest, PeerRole, direct_path_binding_sha256, verify_peer_proof,
};
use termlink_direct_wire::{CandidatePair, ConnectivityDatagram, ProbeFields, sha256};

#[test]
fn all_direct_transport_crypto_domains_match_shared_json_vector() {
    let vector: Value = serde_json::from_slice(
        &fs::read(vector_path()).expect("read common direct-transport crypto vector"),
    )
    .expect("parse common direct-transport crypto vector");
    let connection = uuid_bytes(&vector, "connection_id", "connection_id_network_hex");
    let sender = uuid_bytes(&vector, "sender_device_id", "sender_device_id_network_hex");
    let recipient = uuid_bytes(
        &vector,
        "recipient_device_id",
        "recipient_device_id_network_hex",
    );
    let epoch = number(&vector, &["path_epoch"]);
    let pairwise = bytes(&vector, &["pairwise_secret_hex"]);

    verify_signal(&vector, &pairwise, connection, sender, recipient, epoch);
    verify_probe(&vector, &pairwise, connection, sender, recipient, epoch);
    verify_proofs_exporter_and_binding(&vector, connection, sender, recipient, epoch);
}

fn verify_signal(
    vector: &Value,
    pairwise: &[u8],
    connection: [u8; 16],
    sender: [u8; 16],
    recipient: [u8; 16],
    epoch: u64,
) {
    let domain = b"TERMLINK-TRANSPORT-SIGNAL-AEAD-V1";
    let mut salt_input = domain.to_vec();
    salt_input.push(0);
    assert_eq!(salt_input, bytes(vector, &["signal", "salt_input_hex"]));
    let salt = sha256(&salt_input);
    assert_eq!(salt, fixed(vector, &["signal", "salt_sha256_hex"]));
    let mut info = domain.to_vec();
    info.extend_from_slice(&connection);
    info.extend_from_slice(&epoch.to_be_bytes());
    info.extend_from_slice(&sender);
    info.extend_from_slice(&recipient);
    info.extend_from_slice(string(vector, &["signal", "key_id_utf8"]).as_bytes());
    assert_eq!(info, bytes(vector, &["signal", "hkdf_info_hex"]));
    let key = hkdf_sha256(pairwise, &salt, &info);
    assert_eq!(key, fixed(vector, &["signal", "derived_key_hex"]));

    let nonce: [u8; 12] = fixed(vector, &["signal", "nonce_hex"]);
    let aad = bytes(vector, &["signal", "aad_protobuf_hex"]);
    let mut ciphertext = bytes(vector, &["signal", "ciphertext_and_tag_hex"]);
    let plaintext = aead::LessSafeKey::new(
        aead::UnboundKey::new(&aead::CHACHA20_POLY1305, &key).expect("valid vector key"),
    )
    .open_in_place(
        aead::Nonce::assume_unique_for_key(nonce),
        aead::Aad::from(aad.as_slice()),
        &mut ciphertext,
    )
    .expect("authenticate common signal vector");
    assert_eq!(
        plaintext,
        bytes(vector, &["signal", "plaintext_protobuf_hex"])
    );
    assert_eq!(
        sha256(&bytes(vector, &["signal", "envelope_protobuf_hex"])),
        fixed(vector, &["signal", "envelope_sha256_hex"]),
    );
}

fn verify_probe(
    vector: &Value,
    pairwise: &[u8],
    connection: [u8; 16],
    sender: [u8; 16],
    recipient: [u8; 16],
    epoch: u64,
) {
    let nomination_id = uuid_text_bytes(string(vector, &["probe", "nomination_id"]));
    let domain = b"TERMLINK-DIRECT-PROBE-HMAC-V1";
    let mut salt_input = domain.to_vec();
    salt_input.push(0);
    assert_eq!(salt_input, bytes(vector, &["probe", "salt_input_hex"]));
    let salt = sha256(&salt_input);
    assert_eq!(salt, fixed(vector, &["probe", "salt_sha256_hex"]));
    let mut info = domain.to_vec();
    info.extend_from_slice(&connection);
    info.extend_from_slice(&epoch.to_be_bytes());
    info.extend_from_slice(&nomination_id);
    info.extend_from_slice(&sender);
    info.extend_from_slice(&recipient);
    assert_eq!(info, bytes(vector, &["probe", "hkdf_info_hex"]));
    let key = hkdf_sha256(pairwise, &salt, &info);
    assert_eq!(key, fixed(vector, &["probe", "derived_key_hex"]));

    let probe = ConnectivityDatagram::new_probe(ProbeFields {
        connection_id: connection,
        path_epoch: epoch,
        ticket_id: uuid_text_bytes(string(vector, &["ticket_id"])),
        nomination_id,
        pair: CandidatePair {
            initiator_candidate_id: uuid_text_bytes(string(
                vector,
                &["probe", "initiator_candidate_id"],
            )),
            responder_candidate_id: uuid_text_bytes(string(
                vector,
                &["probe", "responder_candidate_id"],
            )),
        },
        sender_device_id: sender,
        probe_nonce: fixed(vector, &["probe", "probe_nonce_hex"]),
        sent_at_ms: number(vector, &["probe", "sent_at_ms"]) as i64,
        anti_amplification_padding: fixed(vector, &["probe", "anti_amplification_padding_hex"]),
    })
    .expect("valid common probe fields");
    let packet = probe
        .encode_authenticated(&key)
        .expect("encode common probe");
    assert_eq!(packet, bytes(vector, &["probe", "packet_hex"]));
    assert_eq!(
        packet.len() as u64,
        number(vector, &["probe", "packet_length"]),
    );
    assert_eq!(
        sha256(&packet),
        fixed(vector, &["probe", "packet_sha256_hex"]),
    );
}

fn verify_proofs_exporter_and_binding(
    vector: &Value,
    connection: [u8; 16],
    sender: [u8; 16],
    recipient: [u8; 16],
    epoch: u64,
) {
    assert_eq!(
        number(
            vector,
            &["peer_proof_frames", "maximum_peer_proof_frame_bytes"],
        ) as usize,
        MAX_DIRECT_FRAME_WIRE_BYTES,
        "the shared peer-proof frame bound must match the native decoder",
    );
    assert_eq!(
        string(vector, &["peer_proof", "tls_exporter_label_ascii"]),
        "EXPORTER-TERMLINK-DIRECT-V1",
    );
    let binding = PeerBindingContext {
        ticket_sha256: fixed(vector, &["ticket_sha256_hex"]),
        connection_id: connection,
        path_epoch: epoch,
    };
    assert_eq!(
        binding.exporter_context().expect("valid exporter context"),
        fixed(vector, &["peer_proof", "tls_exporter_context_hex"]),
    );
    let exporter_hash = fixed(vector, &["peer_proof", "tls_exporter_sha256_hex"]);
    let initiator = PeerProofRequest {
        ticket_sha256: binding.ticket_sha256,
        role: PeerRole::Initiator,
        device_id: sender,
        peer_device_id: recipient,
        connection_id: connection,
        path_epoch: epoch,
        proof_nonce: bytes(vector, &["peer_proof", "initiator_proof_nonce_hex"]),
        tls_exporter_sha256: exporter_hash,
    };
    let responder = PeerProofRequest {
        role: PeerRole::Responder,
        device_id: recipient,
        peer_device_id: sender,
        proof_nonce: bytes(vector, &["peer_proof", "responder_proof_nonce_hex"]),
        ..initiator.clone()
    };
    let initiator_transcript = initiator.transcript().expect("initiator transcript");
    let responder_transcript = responder.transcript().expect("responder transcript");
    assert_eq!(
        initiator_transcript,
        bytes(vector, &["peer_proof", "initiator_transcript_hex"]),
    );
    assert_eq!(
        responder_transcript,
        bytes(vector, &["peer_proof", "responder_transcript_hex"]),
    );
    let initiator_hash = sha256(&initiator_transcript);
    let responder_hash = sha256(&responder_transcript);
    assert_eq!(
        initiator_hash,
        fixed(vector, &["peer_proof", "initiator_transcript_sha256_hex"],),
    );
    assert_eq!(
        responder_hash,
        fixed(vector, &["peer_proof", "responder_transcript_sha256_hex"],),
    );
    verify_peer_proof(
        &Ed25519PeerProofVerifier::new(fixed(
            vector,
            &[
                "peer_proof_frames",
                "test_only_device_keys",
                "initiator_public_key_hex",
            ],
        )),
        &initiator,
        &bytes(vector, &["peer_proof_frames", "initiator", "signature_hex"]),
    )
    .expect("verify common initiator signature");
    verify_peer_proof(
        &Ed25519PeerProofVerifier::new(fixed(
            vector,
            &[
                "peer_proof_frames",
                "test_only_device_keys",
                "responder_public_key_hex",
            ],
        )),
        &responder,
        &bytes(vector, &["peer_proof_frames", "responder", "signature_hex"]),
    )
    .expect("verify common responder signature");

    let path_binding = direct_path_binding_sha256(DirectPathBindingInputs {
        ticket_sha256: binding.ticket_sha256,
        initiator_candidate_id: uuid_text_bytes(string(
            vector,
            &["probe", "initiator_candidate_id"],
        )),
        responder_candidate_id: uuid_text_bytes(string(
            vector,
            &["probe", "responder_candidate_id"],
        )),
        initiator_certificate_sha256: fixed(
            vector,
            &["path_binding", "initiator_certificate_sha256_hex"],
        ),
        responder_certificate_sha256: fixed(
            vector,
            &["path_binding", "responder_certificate_sha256_hex"],
        ),
        initiator_proof_sha256: initiator_hash,
        responder_proof_sha256: responder_hash,
        tls_exporter_sha256: exporter_hash,
    });
    assert_eq!(path_binding, fixed(vector, &["path_binding", "sha256_hex"]),);

    let proof_record = bytes(
        vector,
        &["peer_proof_frames", "initiator", "peer_proof_record_hex"],
    );
    assert_eq!(
        u32::from_be_bytes(proof_record[..4].try_into().expect("proof length prefix")) as u64,
        number(
            vector,
            &["peer_proof_frames", "initiator", "peer_proof_frame_length",],
        ),
    );
    assert_eq!(
        proof_record[4], 0x08,
        "proof record must be DirectPathFrameV1"
    );
}

#[derive(Clone, Copy)]
struct HkdfLength(usize);

impl hkdf::KeyType for HkdfLength {
    fn len(&self) -> usize {
        self.0
    }
}

fn hkdf_sha256(input: &[u8], salt: &[u8], info: &[u8]) -> [u8; 32] {
    let salt = hkdf::Salt::new(hkdf::HKDF_SHA256, salt);
    let prk = salt.extract(input);
    let info_parts = [info];
    let okm = prk
        .expand(&info_parts, HkdfLength(32))
        .expect("valid HKDF output length");
    let mut output = [0_u8; 32];
    okm.fill(&mut output).expect("fill HKDF output");
    output
}

fn vector_path() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../../protocol/test-vectors/direct-transport-crypto-v1.json")
}

fn at<'a>(value: &'a Value, path: &[&str]) -> &'a Value {
    path.iter().fold(value, |current, key| {
        current
            .get(*key)
            .unwrap_or_else(|| panic!("missing vector field {key}"))
    })
}

fn string<'a>(value: &'a Value, path: &[&str]) -> &'a str {
    at(value, path)
        .as_str()
        .unwrap_or_else(|| panic!("vector field {path:?} is not a string"))
}

fn number(value: &Value, path: &[&str]) -> u64 {
    at(value, path)
        .as_u64()
        .unwrap_or_else(|| panic!("vector field {path:?} is not an unsigned integer"))
}

fn bytes(value: &Value, path: &[&str]) -> Vec<u8> {
    decode_hex(string(value, path))
}

fn fixed<const N: usize>(value: &Value, path: &[&str]) -> [u8; N] {
    bytes(value, path)
        .try_into()
        .unwrap_or_else(|_| panic!("vector field {path:?} is not {N} bytes"))
}

fn uuid_bytes(value: &Value, text_path: &str, hex_path: &str) -> [u8; 16] {
    let from_text = uuid_text_bytes(string(value, &[text_path]));
    assert_eq!(from_text, fixed(value, &[hex_path]));
    from_text
}

fn uuid_text_bytes(value: &str) -> [u8; 16] {
    let compact: String = value
        .chars()
        .filter(|character| *character != '-')
        .collect();
    decode_hex(&compact)
        .try_into()
        .expect("canonical UUID must contain 16 network-order bytes")
}

fn decode_hex(value: &str) -> Vec<u8> {
    assert_eq!(value.len() % 2, 0, "hex vector has odd length");
    value
        .as_bytes()
        .chunks_exact(2)
        .map(|pair| {
            u8::from_str_radix(std::str::from_utf8(pair).expect("ASCII hex"), 16)
                .expect("valid hex vector")
        })
        .collect()
}

#[test]
fn vector_digest_helpers_are_not_accidentally_using_text_hex() {
    assert_eq!(
        digest::digest(&digest::SHA256, b"abc").as_ref(),
        decode_hex("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"),
    );
}
