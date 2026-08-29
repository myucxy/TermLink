package adminroute

import (
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"errors"
	"strings"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

const (
	DefaultBasePath = "/admin"
	secretDomain    = "TermLink web admin route v1"
)

var ErrInvalidRoute = errors.New("admin route is invalid")

type Repository interface {
	GetWebPortalSettings(context.Context) (store.WebPortalSettings, error)
	SaveWebPortalAdminRoute(context.Context, uuid.UUID, []byte, []byte, int64) (store.WebPortalSettings, error)
}

type Service struct {
	repository Repository
	key        [32]byte
	aead       cipher.AEAD
}

type View struct {
	BasePath string
	Custom   bool
	Version  int64
}

func New(repository Repository, key []byte) (*Service, error) {
	if repository == nil || len(key) < 32 {
		return nil, ErrInvalidRoute
	}
	block, err := aes.NewCipher(key[:32])
	if err != nil {
		return nil, err
	}
	aead, err := cipher.NewGCM(block)
	if err != nil {
		return nil, err
	}
	service := &Service{repository: repository, aead: aead}
	copy(service.key[:], key[:32])
	return service, nil
}

func (s *Service) Current(ctx context.Context) (View, error) {
	settings, err := s.repository.GetWebPortalSettings(ctx)
	if err != nil {
		return View{}, err
	}
	if len(settings.AdminRouteCiphertext) == 0 && len(settings.AdminRouteHMAC) == 0 {
		return View{BasePath: DefaultBasePath, Version: settings.Version}, nil
	}
	plain, err := s.open(settings.AdminRouteCiphertext)
	if err != nil || len(settings.AdminRouteHMAC) != sha256.Size || !hmac.Equal(settings.AdminRouteHMAC, s.digest(plain)) {
		return View{}, ErrInvalidRoute
	}
	segment := string(plain)
	if !ValidSegment(segment) {
		return View{}, ErrInvalidRoute
	}
	return View{BasePath: "/control/" + segment, Custom: true, Version: settings.Version}, nil
}

func (s *Service) Update(ctx context.Context, actorID uuid.UUID, segment, confirmation string, expectedVersion int64) (View, error) {
	segment = strings.TrimSpace(segment)
	if segment != strings.TrimSpace(confirmation) || !ValidSegment(segment) {
		return View{}, ErrInvalidRoute
	}
	sealed, err := s.seal([]byte(segment))
	if err != nil {
		return View{}, err
	}
	saved, err := s.repository.SaveWebPortalAdminRoute(ctx, actorID, sealed, s.digest([]byte(segment)), expectedVersion)
	if err != nil {
		return View{}, err
	}
	return View{BasePath: "/control/" + segment, Custom: true, Version: saved.Version}, nil
}

func ValidSegment(value string) bool {
	if len(value) < 16 || len(value) > 64 {
		return false
	}
	switch strings.ToLower(value) {
	case "admin", "account", "api", "auth", "assets", "login", "logout", "register", "control":
		return false
	}
	for _, char := range value {
		if char >= 'a' && char <= 'z' || char >= 'A' && char <= 'Z' || char >= '0' && char <= '9' || char == '-' || char == '_' {
			continue
		}
		return false
	}
	return true
}

func (s *Service) seal(plain []byte) ([]byte, error) {
	nonce := make([]byte, s.aead.NonceSize())
	if _, err := rand.Read(nonce); err != nil {
		return nil, err
	}
	return s.aead.Seal(nonce, nonce, plain, []byte(secretDomain)), nil
}

func (s *Service) open(sealed []byte) ([]byte, error) {
	if len(sealed) < s.aead.NonceSize()+s.aead.Overhead() {
		return nil, ErrInvalidRoute
	}
	return s.aead.Open(nil, sealed[:s.aead.NonceSize()], sealed[s.aead.NonceSize():], []byte(secretDomain))
}

func (s *Service) digest(value []byte) []byte {
	mac := hmac.New(sha256.New, s.key[:])
	_, _ = mac.Write([]byte(secretDomain))
	_, _ = mac.Write([]byte{0})
	_, _ = mac.Write(value)
	return mac.Sum(nil)
}
