package adminroute

import (
	"context"
	"testing"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

type MemoryRepository struct {
	settings store.WebPortalSettings
}

func (r *MemoryRepository) GetWebPortalSettings(context.Context) (store.WebPortalSettings, error) {
	return r.settings, nil
}

func (r *MemoryRepository) SaveWebPortalAdminRoute(_ context.Context, _ uuid.UUID, ciphertext, routeHMAC []byte, version int64) (store.WebPortalSettings, error) {
	r.settings = store.WebPortalSettings{AdminRouteCiphertext: append([]byte(nil), ciphertext...), AdminRouteHMAC: append([]byte(nil), routeHMAC...), Version: version + 1}
	return r.settings, nil
}

func TestServiceDefaultsUpdatesAndAuthenticatesRoute(t *testing.T) {
	repository := &MemoryRepository{settings: store.WebPortalSettings{Version: 1}}
	service, err := New(repository, []byte("admin-route-test-key-0123456789abcdef"))
	if err != nil {
		t.Fatal(err)
	}
	view, err := service.Current(context.Background())
	if err != nil || view.BasePath != "/admin" || view.Custom {
		t.Fatalf("default view = %+v, err=%v", view, err)
	}
	view, err = service.Update(context.Background(), uuid.New(), "4hPzK8mQvX2sN7aB", "4hPzK8mQvX2sN7aB", 1)
	if err != nil || view.BasePath != "/control/4hPzK8mQvX2sN7aB" || !view.Custom || view.Version != 2 {
		t.Fatalf("updated view = %+v, err=%v", view, err)
	}
	loaded, err := service.Current(context.Background())
	if err != nil || loaded != view {
		t.Fatalf("loaded view = %+v, err=%v", loaded, err)
	}
	repository.settings.AdminRouteHMAC[0] ^= 0xff
	if _, err = service.Current(context.Background()); err != ErrInvalidRoute {
		t.Fatalf("tampered route error = %v", err)
	}
}

func TestValidSegmentRejectsUnsafeValues(t *testing.T) {
	for _, value := range []string{"admin", "too-short", "abcdefghijklmnop/next", "abcdefghijklmnop?x=1", "abcdefghijklmnop.unsafe"} {
		if ValidSegment(value) {
			t.Errorf("ValidSegment(%q) = true", value)
		}
	}
	if !ValidSegment("abcdefghijklmnop") {
		t.Fatal("valid 16-character route was rejected")
	}
}
