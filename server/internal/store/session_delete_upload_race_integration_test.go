package store

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/migrate"
)

func TestSessionDeleteAndHistoryUploadAreMutuallySerialized(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("production infrastructure integration environment is not configured")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	if err := migrate.Up(ctx, databaseURL); err != nil {
		t.Fatal(err)
	}
	repository, err := Open(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer repository.Close()
	repository.AutoTrustDevices = true

	userID, ownerID, recipientID, pcID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	subject := sha256.Sum256([]byte("session-delete-upload-race:" + userID.String()))
	digest := sha256.Sum256([]byte("session-delete-upload-race-ciphertext"))
	var sessionIDs []uuid.UUID
	t.Cleanup(func() {
		cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cleanupCancel()
		if len(sessionIDs) > 0 {
			_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM outbox WHERE aggregate_id=ANY($1::uuid[])`, sessionIDs)
		}
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM audit_events WHERE user_id=$1`, userID)
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM users WHERE id=$1`, userID)
	})
	if _, err := repository.Pool.Exec(ctx, `INSERT INTO users(id,oidc_subject_hash) VALUES($1,$2)`, userID, subject[:]); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO devices(id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state)
		VALUES
		  ($1,$2,'WINDOWS_PC','Delete race owner',decode(repeat('11',32),'hex'),decode(repeat('12',32),'hex'),$3,'TRUSTED'),
		  ($4,$2,'ANDROID','Delete race recipient',decode(repeat('21',32),'hex'),decode(repeat('22',32),'hex'),$5,'TRUSTED')`,
		ownerID, userID, "owner-"+ownerID.String(), recipientID, "android-"+recipientID.String()); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO pcs(id,owner_user_id,device_id,name,agent_version,key_epoch)
		VALUES($1,$2,$3,'Delete race PC','integration-test',1)`, pcID, userID, ownerID); err != nil {
		t.Fatal(err)
	}

	keyID := fmt.Sprintf("history-v1:%s:%08d", strings.ReplaceAll(recipientID.String(), "-", ""), 1)
	for iteration := 0; iteration < 8; iteration++ {
		sessionID := uuid.New()
		sessionIDs = append(sessionIDs, sessionID)
		if _, err := repository.Pool.Exec(ctx, `
			INSERT INTO sessions(id,pc_id,shell_type,state,columns,rows,last_output_seq,ended_at)
			VALUES($1,$2,'powershell','EXITED',120,40,10,now())`, sessionID, pcID); err != nil {
			t.Fatal(err)
		}
		uploadID := uuid.New()
		start, end := int64(1), int64(10)
		objectKey := "accounts/" + userID.String() + "/sessions/" + sessionID.String() + "/chunk/" + uploadID.String() + ".bin"
		upload := HistoryUploadRecord{
			ID: uploadID, SessionID: sessionID, Kind: "CHUNK", ObjectKey: objectKey,
			KeyID: keyID, CiphertextSHA256: digest[:], SizeBytes: 128,
			StartSequence: &start, EndSequence: &end, RecipientDeviceID: recipientID,
			RecipientKeyEpoch: 1, ExpiresAt: time.Now().Add(10 * time.Minute),
		}

		startRace := make(chan struct{})
		createResult, deleteResult := make(chan error, 1), make(chan error, 1)
		go func() {
			<-startRace
			createResult <- repository.CreateHistoryUpload(ctx, userID, ownerID, upload)
		}()
		go func() {
			<-startRace
			deleteResult <- repository.MarkSessionDeleted(ctx, userID, ownerID, sessionID, "delete-upload-race")
		}()
		close(startRace)
		createErr, deleteErr := <-createResult, <-deleteResult
		if deleteErr != nil {
			t.Fatalf("iteration %d delete error=%v", iteration, deleteErr)
		}
		if createErr != nil && !errors.Is(createErr, ErrDenied) {
			t.Fatalf("iteration %d create error=%v, want nil or ErrDenied", iteration, createErr)
		}

		var state string
		var uploads, deleteEvents int
		if err := repository.Pool.QueryRow(ctx, `SELECT state FROM sessions WHERE id=$1`, sessionID).Scan(&state); err != nil {
			t.Fatal(err)
		}
		if err := repository.Pool.QueryRow(ctx, `SELECT count(*) FROM history_uploads WHERE session_id=$1`, sessionID).Scan(&uploads); err != nil {
			t.Fatal(err)
		}
		if err := repository.Pool.QueryRow(ctx, `
			SELECT count(*) FROM outbox
			WHERE event_type='history.object.delete' AND aggregate_id=$1
			  AND payload->>'object_key'=$2`, sessionID, objectKey).Scan(&deleteEvents); err != nil {
			t.Fatal(err)
		}
		wantEvents := 0
		if createErr == nil {
			wantEvents = 1
		}
		if state != "DELETED" || uploads != 0 || deleteEvents != wantEvents {
			t.Fatalf("iteration %d state/uploads/delete-events=%s/%d/%d, want DELETED/0/%d (create=%v)",
				iteration, state, uploads, deleteEvents, wantEvents, createErr)
		}
		if err := repository.CreateHistoryUpload(ctx, userID, ownerID, upload); !errors.Is(err, ErrDenied) {
			t.Fatalf("iteration %d post-delete create error=%v, want ErrDenied", iteration, err)
		}
	}
}
