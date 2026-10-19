package store

import (
	"context"
	"errors"
	"testing"

	"github.com/google/uuid"
)

func TestRetiredAppShipSettingsCannotBeWritten(t *testing.T) {
	// A nil pool makes any unexpected database access fail immediately.
	repository := &Postgres{}
	for _, key := range []string{"appship_base_url", "appship_allowed_private_ips"} {
		if _, allowed := RuntimeSettingKeys[key]; allowed {
			t.Fatalf("retired startup policy remains writable: %s", key)
		}
		if err := repository.SetRuntimeSetting(context.Background(), key, "value", uuid.New()); !errors.Is(err, ErrDenied) {
			t.Fatalf("retired setting write returned %v", err)
		}
	}
}
