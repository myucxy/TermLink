package filetransfer

import (
	"bytes"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/store"
)

func TestFileTransferHelloAndRouteAuthorizationAreRoleBound(t *testing.T) {
	valid := clientHello{
		Role: "android", TransferID: uuid.NewString(),
		ProtocolVersions: []uint32{2}, Capabilities: []string{"resume_offset_v2", "e2ee"},
	}
	if !validHello(valid) {
		t.Fatal("valid file-transfer hello was rejected")
	}
	invalid := valid
	invalid.Capabilities = append(invalid.Capabilities, "unknown")
	if validHello(invalid) {
		t.Fatal("hello with an unbounded capability set was accepted")
	}

	now := time.Now()
	transferID, requesterID, pcID := uuid.New(), uuid.New(), uuid.New()
	route := store.FileTransferRouteAuthorization{
		TransferID: transferID, RequesterDevice: requesterID, PCDevice: pcID,
		Status: store.FileOperationPending, ExpiresAt: now.Add(time.Minute),
	}
	if !routeAllows(route, requesterID, "android", now) || routeAllows(route, pcID, "pc", now) {
		t.Fatal("pending route did not allow only its Android requester")
	}
	route.Status = store.FileOperationProcessing
	protocol := store.FileTransferRealtimeV2
	route.TransferProtocol = &protocol
	if !routeAllows(route, requesterID, "android", now) || !routeAllows(route, pcID, "pc", now) ||
		routeAllows(route, uuid.New(), "android", now) {
		t.Fatal("processing route did not remain pair-bound")
	}
	route.Status = store.FileOperationSucceeded
	if routeAllows(route, requesterID, "android", now) || routeAllows(route, pcID, "pc", now) {
		t.Fatal("terminal route remained authorized")
	}
}

func TestPeerRejectsCrossTransferDirectionAndReplay(t *testing.T) {
	transferID, requesterID, pcID := uuid.New(), uuid.New(), uuid.New()
	p := &peer{
		principal: auth.Principal{Device: auth.Device{ID: pcID, Type: "WINDOWS_PC"}},
		route: store.FileTransferRouteAuthorization{
			TransferID: transferID, RequesterDevice: requesterID, PCDevice: pcID,
			Status: store.FileOperationProcessing, ExpiresAt: time.Now().Add(time.Minute),
		},
		role: "pc",
	}
	frame, err := ParseEnvelope(testEnvelopeFrame(transferID, pcID, requesterID, 3, 1))
	if err != nil || !p.validEnvelope(frame) {
		t.Fatalf("valid PC-to-Android envelope rejected: %v", err)
	}
	p.lastSeq = 1
	if p.validEnvelope(frame) {
		t.Fatal("replayed file-transfer sequence was accepted")
	}
	wrongRecipient, _ := ParseEnvelope(testEnvelopeFrame(transferID, pcID, uuid.New(), 3, 2))
	if p.validEnvelope(wrongRecipient) {
		t.Fatal("cross-recipient file-transfer envelope was accepted")
	}
	wrongTransfer, _ := ParseEnvelope(testEnvelopeFrame(uuid.New(), pcID, requesterID, 3, 2))
	if p.validEnvelope(wrongTransfer) {
		t.Fatal("cross-transfer file-transfer envelope was accepted")
	}
}

func TestPeerQueueIsByteBoundedAndCopiesFrames(t *testing.T) {
	p := &peer{send: make(chan []byte, 1), done: make(chan struct{})}
	frame := bytes.Repeat([]byte{7}, 1024)
	if !p.enqueue(frame) {
		t.Fatal("bounded queue rejected an ordinary frame")
	}
	frame[0] = 9
	queued := <-p.send
	if queued[0] != 7 {
		t.Fatal("relay queue retained a caller-owned mutable frame")
	}
	p.queued.Add(-int64(len(queued)))
	if p.enqueue(make([]byte, peerQueueBytes+1)) {
		t.Fatal("relay queue accepted a frame above its byte budget")
	}
}
