use std::{fs, path::PathBuf};

use serde_json::Value;
use termlink_direct_wire::{CandidatePair, derive_pair_probe_nonce_v2};

#[test]
fn multipair_v2_shared_vector_binds_direction_round_and_pair() {
    let vector_path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../../protocol/test-vectors/direct-transport-multipair-v2.json");
    let vector: Value = serde_json::from_slice(&fs::read(vector_path).unwrap()).unwrap();

    assert_eq!(vector["format_version"].as_u64(), Some(2));
    assert_eq!(
        vector["capability"].as_str(),
        Some("direct_transport_shadow_multipair_v2")
    );
    let key: [u8; 32] = decode_hex(vector["local_send_probe_hmac_key_hex"].as_str().unwrap())
        .try_into()
        .unwrap();
    let connectivity_nonce: [u8; 16] =
        decode_hex(vector["connectivity_nonce_hex"].as_str().unwrap())
            .try_into()
            .unwrap();
    let pair = CandidatePair {
        initiator_candidate_id: decode_hex(
            vector["pair"]["initiator_candidate_id_hex"]
                .as_str()
                .unwrap(),
        )
        .try_into()
        .unwrap(),
        responder_candidate_id: decode_hex(
            vector["pair"]["responder_candidate_id_hex"]
                .as_str()
                .unwrap(),
        )
        .try_into()
        .unwrap(),
    };
    assert_eq!(
        derive_pair_probe_nonce_v2(&key, &connectivity_nonce, &pair).as_slice(),
        decode_hex(vector["local_pair_probe_nonce_hex"].as_str().unwrap())
    );
}

fn decode_hex(value: &str) -> Vec<u8> {
    assert_eq!(value.len() % 2, 0);
    (0..value.len())
        .step_by(2)
        .map(|offset| u8::from_str_radix(&value[offset..offset + 2], 16).unwrap())
        .collect()
}
