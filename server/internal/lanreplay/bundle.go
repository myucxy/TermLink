package lanreplay

import (
	"bufio"
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
)

const (
	formatName    = "termlink.golden-stream"
	formatVersion = 1
	maxJSONLLine  = 1024 * 1024
)

var caseNamePattern = regexp.MustCompile(`^[a-z0-9][a-z0-9._-]*$`)

type Bundle struct {
	Manifest     Manifest
	Events       []Event
	Output       []byte
	Expectations Expectations
}

type Manifest struct {
	Format        string     `json:"format"`
	FormatVersion int        `json:"format_version"`
	CaseName      string     `json:"case_name"`
	Clock         string     `json:"clock"`
	Files         Files      `json:"files"`
	Statistics    Statistics `json:"statistics"`
	SHA256        Hashes     `json:"sha256"`
}

type Files struct {
	Events       string `json:"events"`
	Output       string `json:"output"`
	Expectations string `json:"expectations"`
}

type Statistics struct {
	EventCount       int `json:"event_count"`
	OutputEventCount int `json:"output_event_count"`
	OutputBytes      int `json:"output_bytes"`
}

type Hashes struct {
	Events       string `json:"events"`
	Output       string `json:"output"`
	Expectations string `json:"expectations"`
}

type Event struct {
	Sequence   uint64 `json:"seq"`
	ElapsedUS  uint64 `json:"elapsed_us"`
	Type       string `json:"type"`
	DataOffset *int64 `json:"data_offset,omitempty"`
	DataLength *int   `json:"data_length,omitempty"`
	Columns    *int   `json:"columns,omitempty"`
	Rows       *int   `json:"rows,omitempty"`
	Reason     string `json:"reason,omitempty"`
	State      string `json:"state,omitempty"`
	ExitCode   *int32 `json:"exit_code,omitempty"`
}

type Expectations struct {
	ExitCode          int32        `json:"exit_code"`
	FinalSize         TerminalSize `json:"final_size"`
	OutputContainsUTF []string     `json:"output_contains_utf8"`
}

type TerminalSize struct {
	Columns int `json:"columns"`
	Rows    int `json:"rows"`
}

func LoadBundle(root, caseName string) (*Bundle, error) {
	if !caseNamePattern.MatchString(caseName) {
		return nil, fmt.Errorf("invalid case name")
	}

	bundleDir := filepath.Join(root, caseName)
	manifestBytes, err := os.ReadFile(filepath.Join(bundleDir, "manifest.json"))
	if err != nil {
		return nil, fmt.Errorf("read manifest: %w", err)
	}

	var manifest Manifest
	if err := json.Unmarshal(manifestBytes, &manifest); err != nil {
		return nil, fmt.Errorf("decode manifest: %w", err)
	}
	if manifest.Format != formatName || manifest.FormatVersion != formatVersion {
		return nil, fmt.Errorf("unsupported Golden Stream format")
	}
	if manifest.CaseName != caseName || manifest.Clock != "monotonic-microseconds" {
		return nil, fmt.Errorf("manifest identity mismatch")
	}
	if manifest.Files != (Files{
		Events:       "events.jsonl",
		Output:       "output.bin",
		Expectations: "expectations.json",
	}) {
		return nil, fmt.Errorf("unexpected V1 file names")
	}

	eventsBytes, err := os.ReadFile(filepath.Join(bundleDir, manifest.Files.Events))
	if err != nil {
		return nil, fmt.Errorf("read events: %w", err)
	}
	output, err := os.ReadFile(filepath.Join(bundleDir, manifest.Files.Output))
	if err != nil {
		return nil, fmt.Errorf("read output: %w", err)
	}
	expectationsBytes, err := os.ReadFile(filepath.Join(bundleDir, manifest.Files.Expectations))
	if err != nil {
		return nil, fmt.Errorf("read expectations: %w", err)
	}

	if err := checkHash("events.jsonl", manifest.SHA256.Events, eventsBytes); err != nil {
		return nil, err
	}
	if err := checkHash("output.bin", manifest.SHA256.Output, output); err != nil {
		return nil, err
	}
	if err := checkHash("expectations.json", manifest.SHA256.Expectations, expectationsBytes); err != nil {
		return nil, err
	}

	events, err := decodeEvents(eventsBytes)
	if err != nil {
		return nil, err
	}
	var expectations Expectations
	if err := json.Unmarshal(expectationsBytes, &expectations); err != nil {
		return nil, fmt.Errorf("decode expectations: %w", err)
	}

	bundle := &Bundle{
		Manifest:     manifest,
		Events:       events,
		Output:       output,
		Expectations: expectations,
	}
	if err := bundle.validate(); err != nil {
		return nil, err
	}
	return bundle, nil
}

func decodeEvents(data []byte) ([]Event, error) {
	scanner := bufio.NewScanner(bytes.NewReader(data))
	scanner.Buffer(make([]byte, 4096), maxJSONLLine)
	var events []Event
	for scanner.Scan() {
		if len(bytes.TrimSpace(scanner.Bytes())) == 0 {
			continue
		}
		var event Event
		if err := json.Unmarshal(scanner.Bytes(), &event); err != nil {
			return nil, fmt.Errorf("decode event %d: %w", len(events)+1, err)
		}
		events = append(events, event)
	}
	if err := scanner.Err(); err != nil {
		return nil, fmt.Errorf("scan events: %w", err)
	}
	return events, nil
}

func (bundle *Bundle) validate() error {
	if len(bundle.Events) == 0 {
		return fmt.Errorf("Golden Stream contains no events")
	}
	if bundle.Manifest.Statistics.EventCount != len(bundle.Events) ||
		bundle.Manifest.Statistics.OutputBytes != len(bundle.Output) {
		return fmt.Errorf("manifest statistics mismatch")
	}

	var (
		nextOffset       int64
		lastElapsed      uint64
		outputEventCount int
		startedCount     int
		endedCount       int
		lastSize         TerminalSize
		exitCode         int32
	)
	for index, event := range bundle.Events {
		expectedSequence := uint64(index + 1)
		if event.Sequence != expectedSequence {
			return fmt.Errorf("event sequence gap at %d", expectedSequence)
		}
		if index > 0 && event.ElapsedUS < lastElapsed {
			return fmt.Errorf("event time moved backwards at %d", event.Sequence)
		}

		switch event.Type {
		case "output":
			if event.DataOffset == nil || event.DataLength == nil ||
				*event.DataOffset != nextOffset || *event.DataLength <= 0 {
				return fmt.Errorf("invalid output range at %d", event.Sequence)
			}
			nextOffset += int64(*event.DataLength)
			if nextOffset > int64(len(bundle.Output)) {
				return fmt.Errorf("output range exceeds output.bin")
			}
			outputEventCount++
		case "resize":
			if event.Columns == nil || event.Rows == nil ||
				*event.Columns <= 0 || *event.Rows <= 0 ||
				*event.Columns > 65535 || *event.Rows > 65535 || event.Reason == "" {
				return fmt.Errorf("invalid resize at %d", event.Sequence)
			}
			lastSize = TerminalSize{Columns: *event.Columns, Rows: *event.Rows}
		case "lifecycle":
			switch event.State {
			case "started":
				startedCount++
			case "ended":
				if event.ExitCode == nil {
					return fmt.Errorf("ended event has no exit code")
				}
				endedCount++
				exitCode = *event.ExitCode
			default:
				return fmt.Errorf("unsupported lifecycle state")
			}
		default:
			return fmt.Errorf("unsupported event type")
		}
		lastElapsed = event.ElapsedUS
	}

	first := bundle.Events[0]
	last := bundle.Events[len(bundle.Events)-1]
	if first.Type != "lifecycle" || first.State != "started" || startedCount != 1 ||
		last.Type != "lifecycle" || last.State != "ended" || endedCount != 1 {
		return fmt.Errorf("invalid lifecycle boundaries")
	}
	if nextOffset != int64(len(bundle.Output)) ||
		outputEventCount != bundle.Manifest.Statistics.OutputEventCount {
		return fmt.Errorf("output coverage mismatch")
	}
	if exitCode != bundle.Expectations.ExitCode || lastSize != bundle.Expectations.FinalSize {
		return fmt.Errorf("expectations mismatch")
	}
	for _, marker := range bundle.Expectations.OutputContainsUTF {
		if !bytes.Contains(bundle.Output, []byte(marker)) {
			return fmt.Errorf("expected marker missing")
		}
	}
	return nil
}

func checkHash(label, expected string, data []byte) error {
	sum := sha256.Sum256(data)
	actual := hex.EncodeToString(sum[:])
	if actual != expected {
		return fmt.Errorf("SHA-256 mismatch for %s", label)
	}
	return nil
}
