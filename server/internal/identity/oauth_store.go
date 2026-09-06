package identity

import (
	"context"
	"crypto/sha256"
	"encoding/json"
	"errors"
	"net/url"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/ory/fosite"
)

const maximumOAuthSessionBytes = 256 << 10

type oauthStore struct {
	pool     *pgxpool.Pool
	accounts *AccountStore
	clients  map[string]fosite.Client
	clock    func() time.Time
}

type oauthTransactionKey struct{}

type oauthExecutor interface {
	Exec(context.Context, string, ...any) (pgconn.CommandTag, error)
	QueryRow(context.Context, string, ...any) pgx.Row
}

func (s *oauthStore) executor(ctx context.Context) oauthExecutor {
	if transaction, ok := ctx.Value(oauthTransactionKey{}).(pgx.Tx); ok {
		return transaction
	}
	return s.pool
}

func (s *oauthStore) BeginTX(ctx context.Context) (context.Context, error) {
	if _, exists := ctx.Value(oauthTransactionKey{}).(pgx.Tx); exists {
		return ctx, nil
	}
	transaction, err := s.pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return nil, err
	}
	return context.WithValue(ctx, oauthTransactionKey{}, transaction), nil
}

func (s *oauthStore) Commit(ctx context.Context) error {
	transaction, ok := ctx.Value(oauthTransactionKey{}).(pgx.Tx)
	if !ok {
		return errors.New("embedded OAuth transaction is missing")
	}
	return transaction.Commit(ctx)
}

func (s *oauthStore) Rollback(ctx context.Context) error {
	transaction, ok := ctx.Value(oauthTransactionKey{}).(pgx.Tx)
	if !ok {
		return errors.New("embedded OAuth transaction is missing")
	}
	err := transaction.Rollback(ctx)
	if errors.Is(err, pgx.ErrTxClosed) {
		return nil
	}
	return err
}

type persistedRequest struct {
	ID                string              `json:"id"`
	RequestedAt       time.Time           `json:"requested_at"`
	ClientID          string              `json:"client_id"`
	RequestedScopes   []string            `json:"requested_scopes"`
	GrantedScopes     []string            `json:"granted_scopes"`
	RequestedAudience []string            `json:"requested_audience"`
	GrantedAudience   []string            `json:"granted_audience"`
	Form              url.Values          `json:"form"`
	Session           *Session            `json:"session"`
	Authorize         *persistedAuthorize `json:"authorize,omitempty"`
}

type persistedAuthorize struct {
	ResponseTypes        []string                `json:"response_types"`
	RedirectURI          string                  `json:"redirect_uri"`
	State                string                  `json:"state"`
	HandledResponseTypes []string                `json:"handled_response_types"`
	ResponseMode         fosite.ResponseModeType `json:"response_mode"`
	DefaultResponseMode  fosite.ResponseModeType `json:"default_response_mode"`
}

func (s *oauthStore) GetClient(_ context.Context, id string) (fosite.Client, error) {
	client, ok := s.clients[id]
	if !ok {
		return nil, fosite.ErrNotFound
	}
	return client, nil
}

func (s *oauthStore) ClientAssertionJWTValid(ctx context.Context, jti string) error {
	digest := sha256.Sum256([]byte(jti))
	var present bool
	err := s.executor(ctx).QueryRow(ctx, `SELECT EXISTS(SELECT 1 FROM identity_client_assertion_jtis WHERE jti_hash=$1 AND expires_at>now())`, digest[:]).Scan(&present)
	if err != nil {
		return err
	}
	if present {
		return fosite.ErrJTIKnown
	}
	return nil
}

func (s *oauthStore) SetClientAssertionJWT(ctx context.Context, jti string, expiresAt time.Time) error {
	digest := sha256.Sum256([]byte(jti))
	_, err := s.executor(ctx).Exec(ctx, `
		WITH cleanup AS (DELETE FROM identity_client_assertion_jtis WHERE expires_at<=now())
		INSERT INTO identity_client_assertion_jtis(jti_hash,expires_at) VALUES($1,$2)`, digest[:], expiresAt.UTC())
	if isIdentityConflict(err) {
		return fosite.ErrJTIKnown
	}
	return err
}

func (s *oauthStore) Authenticate(ctx context.Context, name, secret string) (string, error) {
	subject, err := s.accounts.Authenticate(ctx, name, secret)
	if errors.Is(err, ErrAccountSetupRequired) {
		// Resource-owner password clients must receive the standard OAuth
		// invalid_grant response. Leaking the migration/setup state here also
		// turns an expected login rejection into Fosite's server_error.
		return "", fosite.ErrNotFound
	}
	return subject, err
}

func (s *oauthStore) CreateAuthorizeCodeSession(ctx context.Context, signature string, request fosite.Requester) error {
	return s.create(ctx, "AUTHORIZE_CODE", signature, "", request, true)
}
func (s *oauthStore) GetAuthorizeCodeSession(ctx context.Context, signature string, _ fosite.Session) (fosite.Requester, error) {
	request, active, err := s.get(ctx, "AUTHORIZE_CODE", signature)
	if err != nil {
		return nil, err
	}
	if !active {
		return request, fosite.ErrInvalidatedAuthorizeCode
	}
	return request, nil
}
func (s *oauthStore) InvalidateAuthorizeCodeSession(ctx context.Context, signature string) error {
	return s.deactivate(ctx, "AUTHORIZE_CODE", signature)
}

func (s *oauthStore) CreatePKCERequestSession(ctx context.Context, signature string, request fosite.Requester) error {
	return s.create(ctx, "PKCE", signature, "", request, true)
}
func (s *oauthStore) GetPKCERequestSession(ctx context.Context, signature string, _ fosite.Session) (fosite.Requester, error) {
	request, active, err := s.get(ctx, "PKCE", signature)
	if err != nil || !active {
		if err == nil {
			err = fosite.ErrNotFound
		}
		return nil, err
	}
	return request, nil
}
func (s *oauthStore) DeletePKCERequestSession(ctx context.Context, signature string) error {
	return s.delete(ctx, "PKCE", signature)
}

func (s *oauthStore) CreateOpenIDConnectSession(ctx context.Context, signature string, request fosite.Requester) error {
	return s.create(ctx, "OPENID", signature, "", request, true)
}
func (s *oauthStore) GetOpenIDConnectSession(ctx context.Context, signature string, _ fosite.Requester) (fosite.Requester, error) {
	request, active, err := s.get(ctx, "OPENID", signature)
	if err != nil || !active {
		if err == nil {
			err = fosite.ErrNotFound
		}
		return nil, err
	}
	return request, nil
}
func (s *oauthStore) DeleteOpenIDConnectSession(ctx context.Context, signature string) error {
	return s.delete(ctx, "OPENID", signature)
}

func (s *oauthStore) CreateAccessTokenSession(ctx context.Context, signature string, request fosite.Requester) error {
	return s.create(ctx, "ACCESS", signature, "", request, true)
}
func (s *oauthStore) GetAccessTokenSession(ctx context.Context, signature string, _ fosite.Session) (fosite.Requester, error) {
	request, active, err := s.get(ctx, "ACCESS", signature)
	if err != nil || !active {
		if err == nil {
			err = fosite.ErrNotFound
		}
		return nil, err
	}
	return request, nil
}
func (s *oauthStore) DeleteAccessTokenSession(ctx context.Context, signature string) error {
	return s.delete(ctx, "ACCESS", signature)
}

func (s *oauthStore) CreateRefreshTokenSession(ctx context.Context, signature, accessSignature string, request fosite.Requester) error {
	return s.create(ctx, "REFRESH", signature, accessSignature, request, true)
}
func (s *oauthStore) GetRefreshTokenSession(ctx context.Context, signature string, _ fosite.Session) (fosite.Requester, error) {
	request, active, err := s.get(ctx, "REFRESH", signature)
	if err != nil {
		return nil, err
	}
	if !active {
		return request, fosite.ErrInactiveToken
	}
	return request, nil
}
func (s *oauthStore) DeleteRefreshTokenSession(ctx context.Context, signature string) error {
	return s.delete(ctx, "REFRESH", signature)
}
func (s *oauthStore) RotateRefreshToken(ctx context.Context, _ string, signature string) error {
	command, err := s.executor(ctx).Exec(ctx, `UPDATE identity_oauth_sessions SET active=false,updated_at=now() WHERE session_type='REFRESH' AND signature=$1 AND active=true AND expires_at>now()`, signature)
	if err != nil {
		return err
	}
	if command.RowsAffected() != 1 {
		return fosite.ErrSerializationFailure
	}
	return nil
}
func (s *oauthStore) RevokeRefreshToken(ctx context.Context, requestID string) error {
	id, err := uuid.Parse(requestID)
	if err != nil {
		return fosite.ErrNotFound
	}
	command, err := s.executor(ctx).Exec(ctx, `UPDATE identity_oauth_sessions SET active=false,updated_at=now() WHERE session_type='REFRESH' AND request_id=$1 AND active=true`, id)
	if err != nil {
		return err
	}
	if command.RowsAffected() == 0 {
		return fosite.ErrNotFound
	}
	return nil
}
func (s *oauthStore) RevokeAccessToken(ctx context.Context, requestID string) error {
	id, err := uuid.Parse(requestID)
	if err != nil {
		return fosite.ErrNotFound
	}
	command, err := s.executor(ctx).Exec(ctx, `UPDATE identity_oauth_sessions SET active=false,updated_at=now() WHERE session_type='ACCESS' AND request_id=$1 AND active=true`, id)
	if err != nil {
		return err
	}
	if command.RowsAffected() == 0 {
		return fosite.ErrNotFound
	}
	return nil
}

func (s *oauthStore) create(ctx context.Context, kind, signature, accessSignature string, request fosite.Requester, active bool) error {
	persisted, expiresAt, err := s.encode(request, kind)
	if err != nil {
		return err
	}
	requestID, err := uuid.Parse(request.GetID())
	if err != nil {
		return err
	}
	_, err = s.executor(ctx).Exec(ctx, `
		INSERT INTO identity_oauth_sessions(session_type,signature,request_id,access_signature,request_payload,active,expires_at)
		VALUES($1,$2,$3,$4,$5,$6,$7)`, kind, signature, requestID, accessSignature, persisted, active, expiresAt)
	if err != nil {
		var pgErr *pgconn.PgError
		if errors.As(err, &pgErr) && pgErr.Code == "23505" {
			return fosite.ErrSerializationFailure
		}
	}
	return err
}

func (s *oauthStore) get(ctx context.Context, kind, signature string) (fosite.Requester, bool, error) {
	var payload []byte
	var active bool
	var expiresAt time.Time
	err := s.executor(ctx).QueryRow(ctx, `SELECT request_payload,active,expires_at FROM identity_oauth_sessions WHERE session_type=$1 AND signature=$2`, kind, signature).Scan(&payload, &active, &expiresAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return nil, false, fosite.ErrNotFound
	}
	if err != nil {
		return nil, false, err
	}
	request, err := s.decode(payload)
	if err != nil {
		return nil, false, err
	}
	if !expiresAt.After(s.clock().UTC()) {
		active = false
	}
	return request, active, nil
}

func (s *oauthStore) deactivate(ctx context.Context, kind, signature string) error {
	command, err := s.executor(ctx).Exec(ctx, `UPDATE identity_oauth_sessions SET active=false,updated_at=now() WHERE session_type=$1 AND signature=$2`, kind, signature)
	if err != nil {
		return err
	}
	if command.RowsAffected() != 1 {
		return fosite.ErrNotFound
	}
	return nil
}
func (s *oauthStore) delete(ctx context.Context, kind, signature string) error {
	command, err := s.executor(ctx).Exec(ctx, `DELETE FROM identity_oauth_sessions WHERE session_type=$1 AND signature=$2`, kind, signature)
	if err != nil {
		return err
	}
	if command.RowsAffected() != 1 {
		return fosite.ErrNotFound
	}
	return nil
}

func (s *oauthStore) encode(request fosite.Requester, kind string) ([]byte, time.Time, error) {
	p := persistedRequest{
		ID: request.GetID(), RequestedAt: request.GetRequestedAt(), ClientID: request.GetClient().GetID(),
		RequestedScopes: append([]string(nil), request.GetRequestedScopes()...), GrantedScopes: append([]string(nil), request.GetGrantedScopes()...),
		RequestedAudience: append([]string(nil), request.GetRequestedAudience()...), GrantedAudience: append([]string(nil), request.GetGrantedAudience()...),
		Form: sanitizeOAuthForm(request.GetRequestForm()),
	}
	session, ok := request.GetSession().(*Session)
	if !ok {
		return nil, time.Time{}, errors.New("unsupported embedded identity session")
	}
	p.Session = session.Clone().(*Session)
	if authorize, ok := request.(fosite.AuthorizeRequester); ok {
		redirect := ""
		if authorize.GetRedirectURI() != nil {
			redirect = authorize.GetRedirectURI().String()
		}
		concrete, concreteOK := request.(*fosite.AuthorizeRequest)
		if !concreteOK {
			return nil, time.Time{}, errors.New("unsupported authorize request")
		}
		p.Authorize = &persistedAuthorize{
			ResponseTypes: append([]string(nil), authorize.GetResponseTypes()...), RedirectURI: redirect, State: authorize.GetState(),
			HandledResponseTypes: append([]string(nil), concrete.HandledResponseTypes...), ResponseMode: concrete.ResponseMode, DefaultResponseMode: concrete.DefaultResponseMode,
		}
	}
	expiresAt := session.GetExpiresAt(fosite.AccessToken)
	if kind == "REFRESH" {
		expiresAt = session.GetExpiresAt(fosite.RefreshToken)
	}
	if kind == "AUTHORIZE_CODE" || kind == "PKCE" || kind == "OPENID" {
		expiresAt = session.GetExpiresAt(fosite.AuthorizeCode)
	}
	if expiresAt.IsZero() {
		expiresAt = s.clock().UTC().Add(time.Minute)
	}
	payload, err := json.Marshal(p)
	if err != nil || len(payload) > maximumOAuthSessionBytes {
		return nil, time.Time{}, errors.New("embedded OAuth session exceeds the supported size")
	}
	return payload, expiresAt, nil
}

func (s *oauthStore) decode(payload []byte) (fosite.Requester, error) {
	if len(payload) == 0 || len(payload) > maximumOAuthSessionBytes {
		return nil, errors.New("embedded OAuth session is invalid")
	}
	var p persistedRequest
	if err := json.Unmarshal(payload, &p); err != nil {
		return nil, err
	}
	client, ok := s.clients[p.ClientID]
	if !ok || p.Session == nil {
		return nil, errors.New("embedded OAuth session client is invalid")
	}
	base := fosite.Request{
		ID: p.ID, RequestedAt: p.RequestedAt, Client: client, RequestedScope: fosite.Arguments(p.RequestedScopes),
		GrantedScope: fosite.Arguments(p.GrantedScopes), Form: p.Form, Session: p.Session,
		RequestedAudience: fosite.Arguments(p.RequestedAudience), GrantedAudience: fosite.Arguments(p.GrantedAudience),
	}
	if p.Authorize == nil {
		request := fosite.NewAccessRequest(p.Session)
		request.Request = base
		request.GrantTypes = fosite.Arguments(strings.Fields(p.Form.Get("grant_type")))
		return request, nil
	}
	redirect, err := url.Parse(p.Authorize.RedirectURI)
	if err != nil {
		return nil, err
	}
	return &fosite.AuthorizeRequest{
		ResponseTypes: fosite.Arguments(p.Authorize.ResponseTypes), RedirectURI: redirect, State: p.Authorize.State,
		HandledResponseTypes: fosite.Arguments(p.Authorize.HandledResponseTypes), ResponseMode: p.Authorize.ResponseMode,
		DefaultResponseMode: p.Authorize.DefaultResponseMode, Request: base,
	}, nil
}

func sanitizeOAuthForm(source url.Values) url.Values {
	allowed := map[string]bool{
		"grant_type": true, "response_type": true, "scope": true, "client_id": true, "redirect_uri": true,
		"code_challenge": true, "code_challenge_method": true, "nonce": true, "prompt": true, "max_age": true,
	}
	result := make(url.Values)
	for key, values := range source {
		if allowed[key] {
			result[key] = append([]string(nil), values...)
		}
	}
	return result
}
