package store

import (
	"errors"
	"math"
	"strings"
	"testing"

	"github.com/google/uuid"
)

func TestTranscriptUploadValidationAndRecipientKey(t *testing.T) {
	recipient := uuid.New()
	keyID := "transcript-v1:" + strings.ReplaceAll(recipient.String(), "-", "") + ":00000007"
	gotRecipient, epoch, err := ParseHistoryRecipientKeyID("TRANSCRIPT", HistoryFormatTerminalTranscript, keyID)
	if err != nil || gotRecipient != recipient || epoch != 7 {
		t.Fatalf("parsed transcript key recipient=%s epoch=%d err=%v", gotRecipient, epoch, err)
	}
	for _, invalid := range []string{
		"snapshot-v1:" + strings.ReplaceAll(recipient.String(), "-", "") + ":00000007",
		"transcript-v1:" + strings.ReplaceAll(recipient.String(), "-", "") + ":7",
		"transcript-v1:" + strings.ToUpper(strings.ReplaceAll(recipient.String(), "-", "")) + ":00000007",
	} {
		if _, _, err := ParseHistoryRecipientKeyID("TRANSCRIPT", HistoryFormatTerminalTranscript, invalid); !errors.Is(err, ErrInvalidHistoryRecipient) {
			t.Fatalf("invalid transcript key %q error=%v", invalid, err)
		}
	}

	if err := ValidateHistoryTranscriptMetadata(math.MaxInt64, 1, math.MaxInt64, MaxTranscriptCiphertextBytes, HistoryFormatTerminalTranscript, 1); err != nil {
		t.Fatalf("maximum transcript metadata rejected: %v", err)
	}
	for _, values := range []struct {
		asOf, first, last, size int64
		format                  string
		version                 int
	}{
		{0, 1, 1, 1, HistoryFormatTerminalTranscript, 1},
		{1, 0, 1, 1, HistoryFormatTerminalTranscript, 1},
		{1, 2, 1, 1, HistoryFormatTerminalTranscript, 1},
		{1, 1, 1, MaxTranscriptCiphertextBytes + 1, HistoryFormatTerminalTranscript, 1},
		{1, 1, 1, 1, HistoryFormatTerminalSnapshot, 1},
		{1, 1, 1, 1, HistoryFormatTerminalTranscript, 2},
	} {
		if err := ValidateHistoryTranscriptMetadata(values.asOf, values.first, values.last, values.size, values.format, values.version); !errors.Is(err, ErrInvalidHistoryTranscript) {
			t.Fatalf("invalid transcript metadata %+v error=%v", values, err)
		}
	}
}

func TestTranscriptPublicationMetadataValidation(t *testing.T) {
	sequence, rows, columns, version := int64(42), 25, 80, 1
	terminalFormat := HistoryFormatTerminalSnapshot
	initialBefore, pageCount := int64(1001), 5
	base := HistoryUploadRecord{
		Kind: "SNAPSHOT", SizeBytes: 128, SnapshotSequence: &sequence,
		Rows: &rows, Columns: &columns, FormatName: &terminalFormat, FormatVersion: &version,
	}
	if err := validateHistoryUploadShape(base); err != nil {
		t.Fatalf("legacy terminal snapshot metadata rejected: %v", err)
	}
	manifest := base
	manifest.TranscriptInitialBeforeLineID = &initialBefore
	manifest.TranscriptPageCount = &pageCount
	if err := validateHistoryUploadShape(manifest); err != nil {
		t.Fatalf("publication-aware snapshot metadata rejected: %v", err)
	}

	missingCount := manifest
	missingCount.TranscriptPageCount = nil
	if err := validateHistoryUploadShape(missingCount); !errors.Is(err, ErrInvalidHistorySnapshot) {
		t.Fatalf("unpaired publication metadata error=%v", err)
	}
	negativeCount := -1
	invalidCount := manifest
	invalidCount.TranscriptPageCount = &negativeCount
	if err := validateHistoryUploadShape(invalidCount); !errors.Is(err, ErrInvalidHistorySnapshot) {
		t.Fatalf("negative publication page count error=%v", err)
	}
	zeroBefore := int64(0)
	invalidBefore := manifest
	invalidBefore.TranscriptInitialBeforeLineID = &zeroBefore
	if err := validateHistoryUploadShape(invalidBefore); !errors.Is(err, ErrInvalidHistorySnapshot) {
		t.Fatalf("zero publication cursor error=%v", err)
	}

	boundedFormat := HistoryFormatBoundedEventReplay
	bounded := manifest
	bounded.FormatName = &boundedFormat
	if err := validateHistoryUploadShape(bounded); !errors.Is(err, ErrInvalidHistorySnapshot) {
		t.Fatalf("bounded replay accepted transcript publication metadata: %v", err)
	}

	asOf, first, last := int64(42), int64(1), int64(2)
	transcriptFormat := HistoryFormatTerminalTranscript
	transcript := HistoryUploadRecord{
		Kind: "TRANSCRIPT", SizeBytes: 128, AsOfSnapshotSequence: &asOf,
		FirstLineID: &first, LastLineID: &last, FormatName: &transcriptFormat, FormatVersion: &version,
		TranscriptInitialBeforeLineID: &initialBefore, TranscriptPageCount: &pageCount,
	}
	if err := validateHistoryUploadShape(transcript); !errors.Is(err, ErrInvalidHistoryTranscript) {
		t.Fatalf("transcript page accepted snapshot publication metadata: %v", err)
	}
}
