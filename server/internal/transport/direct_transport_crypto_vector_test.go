package transport

import (
	"bytes"
	"crypto/ed25519"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"io"
	"os"
	"path/filepath"
	"testing"

	"github.com/google/uuid"
	"golang.org/x/crypto/chacha20poly1305"
	"golang.org/x/crypto/hkdf"
)

type directCryptoVector struct {
	ConnectionID             string `json:"connection_id"`
	ConnectionIDNetworkHex   string `json:"connection_id_network_hex"`
	TicketID                 string `json:"ticket_id"`
	TicketSHA256Hex          string `json:"ticket_sha256_hex"`
	SenderDeviceID           string `json:"sender_device_id"`
	SenderDeviceIDNetworkHex string `json:"sender_device_id_network_hex"`
	RecipientDeviceID        string `json:"recipient_device_id"`
	RecipientDeviceIDHex     string `json:"recipient_device_id_network_hex"`
	PathEpoch                uint64 `json:"path_epoch"`
	PairwiseSecretHex        string `json:"pairwise_secret_hex"`
	Signal                   struct {
		SaltInputHex        string `json:"salt_input_hex"`
		SaltSHA256Hex       string `json:"salt_sha256_hex"`
		HKDFInfoHex         string `json:"hkdf_info_hex"`
		DerivedKeyHex       string `json:"derived_key_hex"`
		NonceHex            string `json:"nonce_hex"`
		AADProtobufHex      string `json:"aad_protobuf_hex"`
		PlaintextHex        string `json:"plaintext_protobuf_hex"`
		CiphertextAndTagHex string `json:"ciphertext_and_tag_hex"`
		EnvelopeProtobufHex string `json:"envelope_protobuf_hex"`
		EnvelopeSHA256Hex   string `json:"envelope_sha256_hex"`
	} `json:"signal"`
	Probe struct {
		NominationIDHex             string `json:"nomination_id"`
		InitiatorCandidateIDHex     string `json:"initiator_candidate_id"`
		ResponderCandidateIDHex     string `json:"responder_candidate_id"`
		ProbeNonceHex               string `json:"probe_nonce_hex"`
		AntiAmplificationPaddingHex string `json:"anti_amplification_padding_hex"`
		SentAtMS                    int64  `json:"sent_at_ms"`
		SaltInputHex                string `json:"salt_input_hex"`
		SaltSHA256Hex               string `json:"salt_sha256_hex"`
		HKDFInfoHex                 string `json:"hkdf_info_hex"`
		DerivedKeyHex               string `json:"derived_key_hex"`
		UnsignedProtobufHex         string `json:"unsigned_protobuf_hex"`
		HMACInputHex                string `json:"hmac_input_hex"`
		AuthTagHex                  string `json:"auth_tag_hex"`
		PacketHex                   string `json:"packet_hex"`
		PacketSHA256Hex             string `json:"packet_sha256_hex"`
		PacketLength                int    `json:"packet_length"`
	} `json:"probe"`
	PeerProof struct {
		TLSExporterLabelASCII        string `json:"tls_exporter_label_ascii"`
		TLSExporterSHA256Hex         string `json:"tls_exporter_sha256_hex"`
		TLSExporterContextHex        string `json:"tls_exporter_context_hex"`
		InitiatorProofNonceHex       string `json:"initiator_proof_nonce_hex"`
		InitiatorTranscriptHex       string `json:"initiator_transcript_hex"`
		InitiatorTranscriptSHA256Hex string `json:"initiator_transcript_sha256_hex"`
		ResponderProofNonceHex       string `json:"responder_proof_nonce_hex"`
		ResponderTranscriptHex       string `json:"responder_transcript_hex"`
		ResponderTranscriptSHA256Hex string `json:"responder_transcript_sha256_hex"`
	} `json:"peer_proof"`
	PeerProofFrames struct {
		TestOnlyDeviceKeys struct {
			InitiatorPublicKeyHex string `json:"initiator_public_key_hex"`
			ResponderPublicKeyHex string `json:"responder_public_key_hex"`
		} `json:"test_only_device_keys"`
		Initiator struct {
			SignatureHex string `json:"signature_hex"`
		} `json:"initiator"`
		Responder struct {
			SignatureHex string `json:"signature_hex"`
		} `json:"responder"`
	} `json:"peer_proof_frames"`
	PathBinding struct {
		InitiatorCertificateSHA256Hex string `json:"initiator_certificate_sha256_hex"`
		ResponderCertificateSHA256Hex string `json:"responder_certificate_sha256_hex"`
		PreimageHex                   string `json:"preimage_hex"`
		SHA256Hex                     string `json:"sha256_hex"`
	} `json:"path_binding"`
}

func TestDirectTransportCryptoCrossLanguageVector(t *testing.T) {
	vector := loadDirectCryptoVector(t)
	connection := uuidNetworkBytes(t, vector.ConnectionID, vector.ConnectionIDNetworkHex)
	sender := uuidNetworkBytes(t, vector.SenderDeviceID, vector.SenderDeviceIDNetworkHex)
	recipient := uuidNetworkBytes(t, vector.RecipientDeviceID, vector.RecipientDeviceIDHex)
	pairwise := mustHex(t, vector.PairwiseSecretHex)

	t.Run("signal_aead", func(t *testing.T) {
		envelopeWire := mustHex(t, vector.Signal.EnvelopeProtobufHex)
		envelope, err := UnmarshalSignal(envelopeWire)
		if err != nil {
			t.Fatalf("parse production signal envelope: %v", err)
		}
		if !bytes.Equal(envelope.ConnectionID, connection) || !bytes.Equal(envelope.SenderDeviceID, sender) ||
			!bytes.Equal(envelope.RecipientDeviceID, recipient) || envelope.PathEpoch != vector.PathEpoch {
			t.Fatal("signal UUID network order or epoch binding changed")
		}
		aad := marshalSignalMetadata(envelope)
		requireHex(t, "signal AAD", aad, vector.Signal.AADProtobufHex)

		domain := []byte("TERMLINK-TRANSPORT-SIGNAL-AEAD-V1")
		saltInput := append(append([]byte(nil), domain...), 0)
		requireHex(t, "signal salt input", saltInput, vector.Signal.SaltInputHex)
		salt := sha256.Sum256(saltInput)
		requireHex(t, "signal salt", salt[:], vector.Signal.SaltSHA256Hex)
		info := directKDFInfo(domain, connection, vector.PathEpoch, sender, recipient, envelope.KeyID)
		requireHex(t, "signal HKDF info", info, vector.Signal.HKDFInfoHex)
		key := deriveVectorKey(t, pairwise, salt[:], info)
		requireHex(t, "signal key", key, vector.Signal.DerivedKeyHex)

		aead, err := chacha20poly1305.New(key)
		if err != nil {
			t.Fatalf("create ChaCha20-Poly1305: %v", err)
		}
		plaintext, err := aead.Open(nil, envelope.Nonce, envelope.Ciphertext, aad)
		if err != nil {
			t.Fatalf("decrypt common signal vector: %v", err)
		}
		requireHex(t, "signal plaintext", plaintext, vector.Signal.PlaintextHex)
		digest := sha256.Sum256(envelopeWire)
		requireHex(t, "signal envelope digest", digest[:], vector.Signal.EnvelopeSHA256Hex)
	})

	t.Run("probe_hmac", func(t *testing.T) {
		ticketID := mustUUID(t, vector.TicketID)
		nominationID := mustUUID(t, vector.Probe.NominationIDHex)
		initiatorCandidateID := mustUUID(t, vector.Probe.InitiatorCandidateIDHex)
		responderCandidateID := mustUUID(t, vector.Probe.ResponderCandidateIDHex)
		unsigned := marshalVectorProbe(
			connection,
			vector.PathEpoch,
			ticketID,
			nominationID,
			initiatorCandidateID,
			responderCandidateID,
			sender,
			mustHex(t, vector.Probe.ProbeNonceHex),
			vector.Probe.SentAtMS,
			mustHex(t, vector.Probe.AntiAmplificationPaddingHex),
		)
		requireHex(t, "probe unsigned protobuf", unsigned, vector.Probe.UnsignedProtobufHex)
		domain := []byte("TERMLINK-DIRECT-PROBE-HMAC-V1")
		macDomain := append(append([]byte(nil), domain...), 0)
		requireHex(t, "probe salt input", macDomain, vector.Probe.SaltInputHex)
		salt := sha256.Sum256(macDomain)
		requireHex(t, "probe salt", salt[:], vector.Probe.SaltSHA256Hex)
		info := directKDFInfo(domain, connection, vector.PathEpoch, nominationID, sender, recipient)
		requireHex(t, "probe HKDF info", info, vector.Probe.HKDFInfoHex)
		key := deriveVectorKey(t, pairwise, salt[:], info)
		requireHex(t, "probe key", key, vector.Probe.DerivedKeyHex)
		macInput := append(append([]byte(nil), macDomain...), unsigned...)
		requireHex(t, "probe HMAC input", macInput, vector.Probe.HMACInputHex)
		mac := hmac.New(sha256.New, key)
		_, _ = mac.Write(macInput)
		tag := mac.Sum(nil)[:16]
		requireHex(t, "probe auth tag", tag, vector.Probe.AuthTagHex)
		body := append(append([]byte(nil), unsigned...), appendBytesField(nil, 13, tag)...)
		packet := append([]byte("TLD1\n"), body...)
		requireHex(t, "probe packet", packet, vector.Probe.PacketHex)
		if len(packet) != vector.Probe.PacketLength {
			t.Fatalf("probe packet length=%d, want %d", len(packet), vector.Probe.PacketLength)
		}
		digest := sha256.Sum256(packet)
		requireHex(t, "probe packet digest", digest[:], vector.Probe.PacketSHA256Hex)
	})

	t.Run("peer_proof_exporter_and_path_binding", func(t *testing.T) {
		ticketHash := mustHex(t, vector.TicketSHA256Hex)
		exporterHash := mustHex(t, vector.PeerProof.TLSExporterSHA256Hex)
		initiatorTranscript := peerProofTranscript(
			ticketHash, 1, sender, recipient, connection, vector.PathEpoch,
			mustHex(t, vector.PeerProof.InitiatorProofNonceHex), exporterHash,
		)
		requireHex(t, "initiator peer proof transcript", initiatorTranscript, vector.PeerProof.InitiatorTranscriptHex)
		initiatorHash := sha256.Sum256(initiatorTranscript)
		requireHex(t, "initiator peer proof hash", initiatorHash[:], vector.PeerProof.InitiatorTranscriptSHA256Hex)
		responderTranscript := peerProofTranscript(
			ticketHash, 2, recipient, sender, connection, vector.PathEpoch,
			mustHex(t, vector.PeerProof.ResponderProofNonceHex), exporterHash,
		)
		requireHex(t, "responder peer proof transcript", responderTranscript, vector.PeerProof.ResponderTranscriptHex)
		responderHash := sha256.Sum256(responderTranscript)
		requireHex(t, "responder peer proof hash", responderHash[:], vector.PeerProof.ResponderTranscriptSHA256Hex)
		if !ed25519.Verify(
			mustHex(t, vector.PeerProofFrames.TestOnlyDeviceKeys.InitiatorPublicKeyHex),
			initiatorTranscript,
			mustHex(t, vector.PeerProofFrames.Initiator.SignatureHex),
		) || !ed25519.Verify(
			mustHex(t, vector.PeerProofFrames.TestOnlyDeviceKeys.ResponderPublicKeyHex),
			responderTranscript,
			mustHex(t, vector.PeerProofFrames.Responder.SignatureHex),
		) {
			t.Fatal("common peer proof Ed25519 signature was rejected")
		}
		if vector.PeerProof.TLSExporterLabelASCII != "EXPORTER-TERMLINK-DIRECT-V1" {
			t.Fatalf("unexpected TLS exporter label %q", vector.PeerProof.TLSExporterLabelASCII)
		}
		exporterContext := append(append(append([]byte(nil), ticketHash...), connection...), uint64BE(vector.PathEpoch)...)
		requireHex(t, "TLS exporter context", exporterContext, vector.PeerProof.TLSExporterContextHex)
		pathPreimage := append([]byte("TERMLINK-DIRECT-PATH-BINDING-V1\x00"), ticketHash...)
		pathPreimage = append(pathPreimage, mustUUID(t, vector.Probe.InitiatorCandidateIDHex)...)
		pathPreimage = append(pathPreimage, mustUUID(t, vector.Probe.ResponderCandidateIDHex)...)
		pathPreimage = append(pathPreimage, mustHex(t, vector.PathBinding.InitiatorCertificateSHA256Hex)...)
		pathPreimage = append(pathPreimage, mustHex(t, vector.PathBinding.ResponderCertificateSHA256Hex)...)
		pathPreimage = append(pathPreimage, initiatorHash[:]...)
		pathPreimage = append(pathPreimage, responderHash[:]...)
		pathPreimage = append(pathPreimage, exporterHash...)
		requireHex(t, "path binding preimage", pathPreimage, vector.PathBinding.PreimageHex)
		pathBinding := sha256.Sum256(pathPreimage)
		requireHex(t, "path binding", pathBinding[:], vector.PathBinding.SHA256Hex)
	})
}

func loadDirectCryptoVector(t *testing.T) directCryptoVector {
	t.Helper()
	path := filepath.Join("..", "..", "..", "protocol", "test-vectors", "direct-transport-crypto-v1.json")
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read direct crypto vector: %v", err)
	}
	var vector directCryptoVector
	if err := json.Unmarshal(raw, &vector); err != nil {
		t.Fatalf("decode direct crypto vector: %v", err)
	}
	return vector
}

func uuidNetworkBytes(t *testing.T, text, expectedHex string) []byte {
	t.Helper()
	value := mustUUID(t, text)
	requireHex(t, "UUID network order", value, expectedHex)
	return value
}

func mustUUID(t *testing.T, value string) []byte {
	t.Helper()
	parsed, err := uuid.Parse(value)
	if err != nil {
		t.Fatalf("parse UUID %q: %v", value, err)
	}
	return append([]byte(nil), parsed[:]...)
}

func mustHex(t *testing.T, value string) []byte {
	t.Helper()
	decoded, err := hex.DecodeString(value)
	if err != nil {
		t.Fatalf("decode hex: %v", err)
	}
	return decoded
}

func requireHex(t *testing.T, name string, actual []byte, expected string) {
	t.Helper()
	if got := hex.EncodeToString(actual); got != expected {
		t.Fatalf("%s=%s, want %s", name, got, expected)
	}
}

func deriveVectorKey(t *testing.T, input, salt, info []byte) []byte {
	t.Helper()
	key := make([]byte, chacha20poly1305.KeySize)
	if _, err := io.ReadFull(hkdf.New(sha256.New, input, salt, info), key); err != nil {
		t.Fatalf("derive HKDF-SHA256 key: %v", err)
	}
	return key
}

func directKDFInfo(domain, first []byte, epoch uint64, remaining ...[]byte) []byte {
	result := append(append([]byte(nil), domain...), first...)
	result = append(result, uint64BE(epoch)...)
	for _, value := range remaining {
		result = append(result, value...)
	}
	return result
}

func uint64BE(value uint64) []byte {
	result := make([]byte, 8)
	binary.BigEndian.PutUint64(result, value)
	return result
}

func marshalSignalMetadata(value SignalEnvelope) []byte {
	var result []byte
	result = appendVarintField(result, 1, uint64(value.ProtocolVersion))
	result = appendVarintField(result, 2, uint64(value.SignalType))
	result = appendBytesField(result, 3, value.ConnectionID)
	result = appendBytesField(result, 4, value.SenderDeviceID)
	result = appendBytesField(result, 5, value.RecipientDeviceID)
	result = appendVarintField(result, 6, value.PathEpoch)
	result = appendVarintField(result, 7, value.SignalSeq)
	if value.SignalACK != 0 {
		result = appendVarintField(result, 8, value.SignalACK)
	}
	result = appendVarintField(result, 9, uint64(value.SentAtMS))
	result = appendBytesField(result, 10, value.KeyID)
	result = appendBytesField(result, 11, value.Nonce)
	return result
}

func marshalVectorProbe(
	connection []byte,
	epoch uint64,
	ticketID, nominationID, initiatorCandidateID, responderCandidateID, sender, nonce []byte,
	sentAtMS int64,
	padding []byte,
) []byte {
	pair := appendBytesField(nil, 1, initiatorCandidateID)
	pair = appendBytesField(pair, 2, responderCandidateID)
	var result []byte
	result = appendVarintField(result, 1, 1)
	result = appendVarintField(result, 2, 1)
	result = appendBytesField(result, 3, connection)
	result = appendVarintField(result, 4, epoch)
	result = appendBytesField(result, 5, ticketID)
	result = appendBytesField(result, 6, nominationID)
	result = appendBytesField(result, 7, pair)
	result = appendBytesField(result, 8, sender)
	result = appendBytesField(result, 9, nonce)
	result = appendVarintField(result, 11, uint64(sentAtMS))
	result = appendBytesField(result, 12, padding)
	return result
}

func peerProofTranscript(ticketHash []byte, role byte, device, peer, connection []byte, epoch uint64, nonce, exporterHash []byte) []byte {
	result := append([]byte("TERMLINK-DIRECT-PEER-PROOF-V1\n"), ticketHash...)
	result = append(result, role)
	result = append(result, device...)
	result = append(result, peer...)
	result = append(result, connection...)
	result = append(result, uint64BE(epoch)...)
	result = append(result, byte(len(nonce)))
	result = append(result, nonce...)
	return append(result, exporterHash...)
}
