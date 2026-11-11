package agentupdate

import (
	"errors"
	"strconv"
	"strings"
)

// WindowsAgentBuild extracts the AppShip build identity from a Windows
// assembly version. Release binaries use the fourth component; legacy
// three-component versions use the third component.
func WindowsAgentBuild(value string) (int64, error) {
	return agentBuild(value, "unsupported Windows Agent version")
}

// AgentBuild reads the shared desktop AssemblyVersion build identity. Four
// components use the release revision; three-component legacy versions retain
// their existing third-component behavior. Platform eligibility is a separate
// policy and must not reuse Windows-specific bootstrap thresholds.
func AgentBuild(value string) (int64, error) {
	return agentBuild(value, "unsupported Agent version")
}

func agentBuild(value, errorMessage string) (int64, error) {
	parts := strings.Split(strings.TrimSpace(value), ".")
	if len(parts) < 3 || len(parts) > 4 {
		return 0, errors.New(errorMessage)
	}
	parsed := make([]int64, len(parts))
	for index, part := range parts {
		number, err := strconv.ParseInt(part, 10, 31)
		if err != nil || number < 0 {
			return 0, errors.New(errorMessage)
		}
		parsed[index] = number
	}
	if len(parsed) == 4 {
		return parsed[3], nil
	}
	return parsed[2], nil
}
