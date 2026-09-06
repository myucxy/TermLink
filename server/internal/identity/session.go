package identity

import (
	"time"

	"github.com/ory/fosite"
	oauth2handler "github.com/ory/fosite/handler/oauth2"
	"github.com/ory/fosite/handler/openid"
	"github.com/ory/fosite/token/jwt"
)

// Session is the single credential-free identity snapshot persisted by
// Fosite. It supports both JWT access tokens and OIDC ID tokens.
type Session struct {
	JWTClaims *jwt.JWTClaims                 `json:"jwt_claims"`
	JWTHeader *jwt.Headers                   `json:"jwt_header"`
	IDClaims  *jwt.IDTokenClaims             `json:"id_claims"`
	IDHeader  *jwt.Headers                   `json:"id_header"`
	ExpiresAt map[fosite.TokenType]time.Time `json:"expires_at"`
	Username  string                         `json:"username"`
	Subject   string                         `json:"subject"`
	AuthEpoch int64                          `json:"auth_epoch"`
}

func newSession() *Session {
	return &Session{
		JWTClaims: &jwt.JWTClaims{Extra: make(map[string]interface{})}, JWTHeader: jwt.NewHeaders(),
		IDClaims: &jwt.IDTokenClaims{RequestedAt: time.Now().UTC(), Extra: make(map[string]interface{})}, IDHeader: jwt.NewHeaders(),
		ExpiresAt: make(map[fosite.TokenType]time.Time),
	}
}

func (s *Session) SetExpiresAt(kind fosite.TokenType, value time.Time) {
	if s.ExpiresAt == nil {
		s.ExpiresAt = make(map[fosite.TokenType]time.Time)
	}
	s.ExpiresAt[kind] = value
}

func (s *Session) GetExpiresAt(kind fosite.TokenType) time.Time {
	if s == nil || s.ExpiresAt == nil {
		return time.Time{}
	}
	return s.ExpiresAt[kind]
}

func (s *Session) GetUsername() string {
	if s == nil {
		return ""
	}
	return s.Username
}
func (s *Session) GetSubject() string {
	if s == nil {
		return ""
	}
	return s.Subject
}

func (s *Session) SetSubject(subject string) {
	s.Subject = subject
	if s.JWTClaims == nil {
		s.JWTClaims = &jwt.JWTClaims{}
	}
	if s.IDClaims == nil {
		s.IDClaims = &jwt.IDTokenClaims{}
	}
	s.JWTClaims.Subject = subject
	s.IDClaims.Subject = subject
}

func (s *Session) Clone() fosite.Session {
	if s == nil {
		return nil
	}
	clone := newSession()
	clone.Username, clone.Subject, clone.AuthEpoch = s.Username, s.Subject, s.AuthEpoch
	for kind, value := range s.ExpiresAt {
		clone.ExpiresAt[kind] = value
	}
	if s.JWTClaims != nil {
		copyJWT := *s.JWTClaims
		copyJWT.Audience = append([]string(nil), s.JWTClaims.Audience...)
		copyJWT.Scope = append([]string(nil), s.JWTClaims.Scope...)
		copyJWT.Extra = cloneMap(s.JWTClaims.Extra)
		clone.JWTClaims = &copyJWT
	}
	if s.JWTHeader != nil {
		copyHeader := *s.JWTHeader
		copyHeader.Extra = cloneMap(s.JWTHeader.Extra)
		clone.JWTHeader = &copyHeader
	}
	if s.IDClaims != nil {
		copyID := *s.IDClaims
		copyID.Audience = append([]string(nil), s.IDClaims.Audience...)
		copyID.AuthenticationMethodsReferences = append([]string(nil), s.IDClaims.AuthenticationMethodsReferences...)
		copyID.Extra = cloneMap(s.IDClaims.Extra)
		clone.IDClaims = &copyID
	}
	if s.IDHeader != nil {
		copyHeader := *s.IDHeader
		copyHeader.Extra = cloneMap(s.IDHeader.Extra)
		clone.IDHeader = &copyHeader
	}
	return clone
}

func cloneMap(source map[string]interface{}) map[string]interface{} {
	result := make(map[string]interface{}, len(source))
	for key, value := range source {
		result[key] = value
	}
	return result
}

func (s *Session) GetJWTClaims() jwt.JWTClaimsContainer {
	if s.JWTClaims == nil {
		s.JWTClaims = &jwt.JWTClaims{}
	}
	return s.JWTClaims
}
func (s *Session) GetJWTHeader() *jwt.Headers {
	if s.JWTHeader == nil {
		s.JWTHeader = jwt.NewHeaders()
	}
	return s.JWTHeader
}
func (s *Session) IDTokenClaims() *jwt.IDTokenClaims {
	if s.IDClaims == nil {
		s.IDClaims = &jwt.IDTokenClaims{}
	}
	return s.IDClaims
}
func (s *Session) IDTokenHeaders() *jwt.Headers {
	if s.IDHeader == nil {
		s.IDHeader = jwt.NewHeaders()
	}
	return s.IDHeader
}

var _ fosite.Session = (*Session)(nil)
var _ oauth2handler.JWTSessionContainer = (*Session)(nil)
var _ openid.Session = (*Session)(nil)
