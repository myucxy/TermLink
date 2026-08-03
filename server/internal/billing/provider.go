// Package billing contains the narrow provider boundary used by the API.
// Provider credentials and raw purchase tokens never cross into the store;
// the store receives only a verified purchase and hashes its reference before
// persistence.
package billing

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"sync"
	"time"
)

const (
	ProviderGooglePlay = "GOOGLE_PLAY"
	ProviderWeb        = "WEB"
)

var (
	ErrUnavailable = errors.New("billing provider unavailable")
	ErrInvalid     = errors.New("invalid purchase credentials")
	ErrNotFound    = errors.New("purchase not found")
)

// VerifiedPurchase is the provider-authoritative result. ProductID and
// Reference are provider values; callers must not populate this from client
// plan/isPro/quota flags.
type VerifiedPurchase struct {
	Provider   string
	ProductID  string
	Reference  string
	Status     string
	RenewsAt   *time.Time
	GraceUntil *time.Time
}

// Provider verifies a provider credential on the server. Implementations
// should reject malformed or expired credentials and must not trust client
// entitlement claims.
type Provider interface {
	Verify(context.Context, string, string) (VerifiedPurchase, error)
}

// Registry selects the configured provider for a purchase request. A nil
// provider returns ErrUnavailable, making an unconfigured production service
// fail closed.
type Registry struct {
	GooglePlay Provider
	Web        Provider
}

func (r Registry) Verify(ctx context.Context, provider, productID, credential string) (VerifiedPurchase, error) {
	provider = strings.ToUpper(strings.TrimSpace(provider))
	if productID == "" || credential == "" {
		return VerifiedPurchase{}, ErrInvalid
	}
	var selected Provider
	switch provider {
	case ProviderGooglePlay:
		selected = r.GooglePlay
	case ProviderWeb:
		selected = r.Web
	default:
		return VerifiedPurchase{}, fmt.Errorf("%w: unsupported provider", ErrInvalid)
	}
	if selected == nil {
		return VerifiedPurchase{}, ErrUnavailable
	}
	verified, err := selected.Verify(ctx, productID, credential)
	if err != nil {
		return VerifiedPurchase{}, err
	}
	if verified.Provider == "" {
		verified.Provider = provider
	}
	if verified.Provider != provider || verified.ProductID != productID || strings.TrimSpace(verified.Reference) == "" {
		return VerifiedPurchase{}, ErrInvalid
	}
	switch verified.Status {
	case "ACTIVE", "GRACE", "CANCELED", "EXPIRED", "REVOKED":
	default:
		return VerifiedPurchase{}, ErrInvalid
	}
	return verified, nil
}

// FakeProvider is deterministic and intentionally small so provider contract
// tests can run without network credentials. Tests may replace a token at any
// time; each result is copied before returning.
type FakeProvider struct {
	mu     sync.RWMutex
	Values map[string]VerifiedPurchase
	Calls  int
}

func (p *FakeProvider) Verify(_ context.Context, productID, credential string) (VerifiedPurchase, error) {
	if p == nil {
		return VerifiedPurchase{}, ErrUnavailable
	}
	p.mu.Lock()
	p.Calls++
	p.mu.Unlock()
	p.mu.RLock()
	value, ok := p.Values[credential]
	p.mu.RUnlock()
	if !ok {
		return VerifiedPurchase{}, ErrNotFound
	}
	if value.ProductID != productID {
		return VerifiedPurchase{}, ErrInvalid
	}
	return value, nil
}
