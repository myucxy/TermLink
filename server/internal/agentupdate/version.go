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
	parts := strings.Split(strings.TrimSpace(value), ".")
	if len(parts) < 3 || len(parts) > 4 {
		return 0, errors.New("unsupported Windows Agent version")
	}
	parsed := make([]int64, len(parts))
	for index, part := range parts {
		number, err := strconv.ParseInt(part, 10, 31)
		if err != nil || number < 0 {
			return 0, errors.New("unsupported Windows Agent version")
		}
		parsed[index] = number
	}
	if len(parsed) == 4 {
		return parsed[3], nil
	}
	return parsed[2], nil
}
