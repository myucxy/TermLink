package humanverification

import (
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"errors"
	"net/http"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

const managedSecretDomain = "TermLink managed human verification secret v1"

var ErrInvalidSettings = errors.New("human verification settings are invalid")

type SettingsRepository interface {
	GetHumanVerificationSettings(context.Context) (store.HumanVerificationSettings, error)
	SaveHumanVerificationSettings(context.Context, uuid.UUID, store.HumanVerificationSettingsInput) (store.HumanVerificationSettings, error)
}

type ManagerConfig struct {
	Repository       SettingsRepository
	EncryptionKey    []byte
	ExpectedHostname string
	FallbackSiteKey  string
	FallbackSecret   string
	VerifyURL        string
	HTTPClient       *http.Client
}

type Manager struct {
	repository       SettingsRepository
	aead             cipher.AEAD
	expectedHostname string
	fallback         *runtimeSettings
	verifyURL        string
	httpClient       *http.Client
}

type AdminView struct {
	Enabled          bool
	SiteKey          string
	SecretConfigured bool
	Version          int64
	Source           string
	UpdatedAt        time.Time
}

type Runtime struct {
	Enabled bool
	SiteKey string
}

type UpdateInput struct {
	Enabled         bool
	SiteKey         string
	Secret          string
	ClearSecret     bool
	ExpectedVersion int64
}

type runtimeSettings struct {
	enabled bool
	siteKey string
	secret  string
	source  string
}

func NewManager(config ManagerConfig) (*Manager, error) {
	if config.Repository == nil || len(config.EncryptionKey) < 32 {
		return nil, ErrInvalidSettings
	}
	hostname := strings.ToLower(strings.TrimSpace(config.ExpectedHostname))
	if hostname == "" || len(hostname) > 253 || strings.ContainsAny(hostname, " /\\\t\r\n:") {
		return nil, ErrInvalidSettings
	}
	block, err := aes.NewCipher(config.EncryptionKey[:32])
	if err != nil {
		return nil, err
	}
	aead, err := cipher.NewGCM(block)
	if err != nil {
		return nil, err
	}
	manager := &Manager{
		repository: config.Repository, aead: aead, expectedHostname: hostname,
		verifyURL: strings.TrimSpace(config.VerifyURL), httpClient: config.HTTPClient,
	}
	fallbackSiteKey := strings.TrimSpace(config.FallbackSiteKey)
	fallbackSecret := strings.TrimSpace(config.FallbackSecret)
	if fallbackSiteKey != "" || fallbackSecret != "" {
		if _, err = manager.newTurnstile(fallbackSiteKey, fallbackSecret); err != nil {
			return nil, ErrInvalidSettings
		}
		manager.fallback = &runtimeSettings{enabled: true, siteKey: fallbackSiteKey, secret: fallbackSecret, source: "DEPLOYMENT"}
	}
	return manager, nil
}

func (m *Manager) Admin(ctx context.Context) (AdminView, error) {
	effective, settings, err := m.effective(ctx)
	if err != nil {
		return AdminView{}, err
	}
	return AdminView{
		Enabled: effective.enabled, SiteKey: effective.siteKey, SecretConfigured: effective.secret != "",
		Version: settings.Version, Source: effective.source, UpdatedAt: settings.UpdatedAt,
	}, nil
}

func (m *Manager) Runtime(ctx context.Context) (Runtime, error) {
	effective, _, err := m.effective(ctx)
	if err != nil {
		return Runtime{}, err
	}
	return Runtime{Enabled: effective.enabled, SiteKey: effective.siteKey}, nil
}

func (m *Manager) Required(ctx context.Context) (bool, error) {
	runtime, err := m.Runtime(ctx)
	return runtime.Enabled, err
}

func (m *Manager) Verify(ctx context.Context, token, remoteIP string) error {
	return m.VerifyAction(ctx, token, remoteIP, RegistrationAction)
}

func (m *Manager) VerifyAction(ctx context.Context, token, remoteIP, action string) error {
	effective, _, err := m.effective(ctx)
	if err != nil {
		return ErrUnavailable
	}
	if !effective.enabled {
		return nil
	}
	verifier, err := m.newTurnstile(effective.siteKey, effective.secret)
	if err != nil {
		return ErrUnavailable
	}
	return verifier.VerifyAction(ctx, token, remoteIP, action)
}

func (m *Manager) Update(ctx context.Context, actorID uuid.UUID, input UpdateInput) (AdminView, error) {
	effective, current, err := m.effective(ctx)
	if err != nil {
		return AdminView{}, err
	}
	if input.ExpectedVersion != current.Version {
		return AdminView{}, store.ErrConflict
	}
	if input.ClearSecret && strings.TrimSpace(input.Secret) != "" {
		return AdminView{}, ErrInvalidSettings
	}
	siteKey := strings.TrimSpace(input.SiteKey)
	secret := strings.TrimSpace(input.Secret)
	if input.ClearSecret {
		secret = ""
	} else if secret == "" {
		secret = effective.secret
	}
	if !validManagedSiteKey(siteKey) || !validManagedSecret(secret) || input.Enabled && (siteKey == "" || secret == "") {
		return AdminView{}, ErrInvalidSettings
	}
	if input.Enabled {
		if _, err = m.newTurnstile(siteKey, secret); err != nil {
			return AdminView{}, ErrInvalidSettings
		}
	}
	var ciphertext []byte
	if secret != "" {
		ciphertext, err = m.seal([]byte(secret))
		if err != nil {
			return AdminView{}, ErrUnavailable
		}
	}
	saved, err := m.repository.SaveHumanVerificationSettings(ctx, actorID, store.HumanVerificationSettingsInput{
		Enabled: input.Enabled, SiteKey: siteKey, SecretCiphertext: ciphertext, ExpectedVersion: input.ExpectedVersion,
	})
	if err != nil {
		return AdminView{}, err
	}
	return AdminView{
		Enabled: saved.Enabled, SiteKey: saved.SiteKey, SecretConfigured: len(saved.SecretCiphertext) > 0,
		Version: saved.Version, Source: "MANAGED", UpdatedAt: saved.UpdatedAt,
	}, nil
}

func (m *Manager) effective(ctx context.Context) (runtimeSettings, store.HumanVerificationSettings, error) {
	settings, err := m.repository.GetHumanVerificationSettings(ctx)
	if err != nil {
		return runtimeSettings{}, store.HumanVerificationSettings{}, err
	}
	if settings.Version < 1 {
		return runtimeSettings{}, store.HumanVerificationSettings{}, ErrInvalidSettings
	}
	if settings.Version == 1 && !settings.Enabled && strings.TrimSpace(settings.SiteKey) == "" && len(settings.SecretCiphertext) == 0 && m.fallback != nil {
		return *m.fallback, settings, nil
	}
	effective := runtimeSettings{enabled: settings.Enabled, siteKey: strings.TrimSpace(settings.SiteKey), source: "MANAGED"}
	if len(settings.SecretCiphertext) > 0 {
		plain, openErr := m.open(settings.SecretCiphertext)
		if openErr != nil {
			return runtimeSettings{}, store.HumanVerificationSettings{}, openErr
		}
		effective.secret = string(plain)
		clear(plain)
	}
	if !validManagedSiteKey(effective.siteKey) || !validManagedSecret(effective.secret) ||
		effective.enabled && (effective.siteKey == "" || effective.secret == "") {
		return runtimeSettings{}, store.HumanVerificationSettings{}, ErrInvalidSettings
	}
	return effective, settings, nil
}

func (m *Manager) newTurnstile(siteKey, secret string) (*Turnstile, error) {
	return NewTurnstile(TurnstileConfig{
		SiteKey: siteKey, Secret: secret, ExpectedHostname: m.expectedHostname,
		VerifyURL: m.verifyURL, HTTPClient: m.httpClient,
	})
}

func validManagedSiteKey(value string) bool {
	return len(value) <= 256 && !containsControlOrSpace(value)
}

func validManagedSecret(value string) bool {
	return len(value) <= 512 && !strings.ContainsAny(value, "\r\n")
}

func (m *Manager) seal(plain []byte) ([]byte, error) {
	nonce := make([]byte, m.aead.NonceSize())
	if _, err := rand.Read(nonce); err != nil {
		return nil, err
	}
	return m.aead.Seal(nonce, nonce, plain, []byte(managedSecretDomain)), nil
}

func (m *Manager) open(sealed []byte) ([]byte, error) {
	if len(sealed) < m.aead.NonceSize()+m.aead.Overhead() {
		return nil, ErrUnavailable
	}
	plain, err := m.aead.Open(nil, sealed[:m.aead.NonceSize()], sealed[m.aead.NonceSize():], []byte(managedSecretDomain))
	if err != nil || !validManagedSecret(string(plain)) || len(plain) == 0 {
		clear(plain)
		return nil, ErrUnavailable
	}
	return plain, nil
}
