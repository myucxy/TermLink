package store

import (
	"strings"
	"testing"
)

func TestAgentUpdateJobColumnsMatchScanOrderAndAreQualified(t *testing.T) {
	plain := strings.Split(strings.ReplaceAll(agentUpdateColumns, "\n", ""), ",")
	qualified := strings.Split(strings.ReplaceAll(agentUpdateJobColumns, "\n", ""), ",")
	if len(plain) != len(qualified) {
		t.Fatalf("qualified column count=%d, want %d", len(qualified), len(plain))
	}
	for i := range plain {
		plainColumn := strings.TrimSpace(plain[i])
		qualifiedColumn := strings.TrimSpace(qualified[i])
		if qualifiedColumn != "job."+plainColumn {
			t.Fatalf("qualified column[%d]=%q, want %q", i, qualifiedColumn, "job."+plainColumn)
		}
	}
}

func TestTerminalAgentUpdatePhaseAllowsPreInstallDrainFailure(t *testing.T) {
	phase, progress, valid := terminalAgentUpdatePhase(AgentUpdateDrainingSessions, AgentUpdateFailed)
	if !valid || phase != AgentUpdateFailed || progress != 100 {
		t.Fatalf("drain failure phase=%q progress=%d valid=%t", phase, progress, valid)
	}
}

func TestTerminalAgentUpdatePhaseRejectsPostInstallFailureWithoutRollback(t *testing.T) {
	for _, phase := range []string{
		AgentUpdateInstalling,
		AgentUpdateStartingNewAgent,
		AgentUpdateVerifyingConnection,
		AgentUpdateRollingBack,
	} {
		if _, _, valid := terminalAgentUpdatePhase(phase, AgentUpdateFailed); valid {
			t.Fatalf("post-install phase %q accepted FAILED instead of requiring rollback", phase)
		}
	}
}

func TestAgentUpdateCompletionUsesBuildIdentity(t *testing.T) {
	targetBuild := int64(38)
	currentBuild := int64(37)
	observedVersion := "0.1.32.38"
	errorCode := "AUTOMATIC_ROLLBACK"
	item := AgentUpdateRecord{
		CurrentVersion: "0.1.31.37", CurrentBuild: currentBuild,
		TargetVersion: "0.1.32", TargetBuild: targetBuild,
	}
	if !validAgentUpdateCompletion(item, AgentUpdateCompleteInput{
		Status: AgentUpdateSucceeded, ObservedAgentVersion: &observedVersion, ObservedAgentBuild: &targetBuild,
	}, observedVersion) {
		t.Fatal("matching target build was rejected because the display version differs")
	}
	if validAgentUpdateCompletion(item, AgentUpdateCompleteInput{
		Status: AgentUpdateSucceeded, ObservedAgentVersion: &observedVersion, ObservedAgentBuild: &currentBuild,
	}, observedVersion) {
		t.Fatal("current build was accepted as the upgraded Agent")
	}
	if !validAgentUpdateCompletion(item, AgentUpdateCompleteInput{
		Status: AgentUpdateRolledBack, ErrorCode: &errorCode,
		ObservedAgentVersion: &observedVersion, ObservedAgentBuild: &currentBuild,
	}, observedVersion) {
		t.Fatal("matching rollback build was rejected")
	}
	if validAgentUpdateCompletion(item, AgentUpdateCompleteInput{
		Status: AgentUpdateSucceeded, ObservedAgentVersion: &observedVersion, ObservedAgentBuild: &targetBuild,
	}, "0.1.31.37") {
		t.Fatal("completion succeeded before the PC record advertised the new Agent version")
	}
}
