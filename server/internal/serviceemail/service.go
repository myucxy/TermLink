package serviceemail

import (
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"crypto/tls"
	"errors"
	"fmt"
	"math/big"
	"net"
	"net/mail"
	"net/smtp"
	"net/url"
	"strconv"
	"strings"
	"time"
	"unicode/utf8"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

const (
	challengeTTL      = 10 * time.Minute
	challengeResend   = time.Minute
	mailSendTimeout   = 12 * time.Second
	secretDomain      = "termlink:service-email-secret:v1"
	emailDigestDomain = "termlink:registration-email:v1"
	codeDigestDomain  = "termlink:registration-email-code:v1"
)

var (
	ErrUnavailable          = errors.New("service email is unavailable")
	ErrInvalidConfiguration = errors.New("service email configuration is invalid")
	ErrDeliveryFailed       = errors.New("service email delivery failed")
	ErrVerificationDisabled = errors.New("registration email verification is disabled")
	ErrVerificationInvalid  = errors.New("registration email verification is invalid")
	ErrIdentityUnavailable  = errors.New("identity email configuration is unavailable")
)

type Repository interface {
	GetServiceEmailSettings(context.Context) (store.ServiceEmailSettings, error)
	SaveVerifiedServiceEmailSettings(context.Context, uuid.UUID, store.ServiceEmailSettingsInput) (store.ServiceEmailSettings, error)
	CreateRegistrationEmailChallenge(context.Context, store.RegistrationEmailChallenge, time.Duration) error
	ConsumeRegistrationEmailChallenge(context.Context, uuid.UUID, [32]byte, [32]byte, time.Time) error
	CreateAccountEmailChallenge(context.Context, store.AccountEmailChallenge, time.Duration) error
	ConsumeAccountEmailChallenge(context.Context, uuid.UUID, uuid.UUID, [32]byte, [32]byte, time.Time) error
}

type SMTPConfig struct {
	Host        string
	Port        int
	Security    string
	FromName    string
	FromAddress string
	ReplyTo     string
	Username    string
	Password    string
}

type Sender interface {
	Send(context.Context, SMTPConfig, string, string, string) error
}

type RealmEmailConfigurator interface {
	ConfigureRealmEmail(context.Context, SMTPConfig, bool) error
}

type Service struct {
	repository Repository
	key        [32]byte
	sender     Sender
	realm      RealmEmailConfigurator
	clock      func() time.Time
}

type AdminView struct {
	ServiceEnabled                  bool
	RegistrationVerificationEnabled bool
	PasswordResetEnabled            bool
	SMTPHost                        string
	SMTPPort                        int
	SMTPSecurity                    string
	FromName                        string
	FromAddress                     string
	ReplyTo                         string
	SMTPUsername                    string
	PasswordConfigured              bool
	Status                          string
	Version                         int64
	TestedAt                        *time.Time
	UpdatedAt                       time.Time
}

type Runtime struct {
	ServiceAvailable                 bool
	RegistrationVerificationRequired bool
	PasswordResetAvailable           bool
	Status                           string
}

type UpdateInput struct {
	ServiceEnabled                  bool
	RegistrationVerificationEnabled bool
	PasswordResetEnabled            bool
	SMTPHost                        string
	SMTPPort                        int
	SMTPSecurity                    string
	FromName                        string
	FromAddress                     string
	ReplyTo                         string
	SMTPUsername                    string
	SMTPPassword                    string
	TestRecipient                   string
	ExpectedVersion                 int64
}

type Challenge struct {
	ID        uuid.UUID
	ExpiresAt time.Time
}

func New(repository Repository, key []byte, sender Sender, realm RealmEmailConfigurator) (*Service, error) {
	if repository == nil || len(key) < 32 {
		return nil, ErrUnavailable
	}
	service := &Service{repository: repository, sender: sender, realm: realm, clock: time.Now}
	copy(service.key[:], key[:32])
	if service.sender == nil {
		service.sender = smtpSender{}
	}
	return service, nil
}

func (s *Service) Admin(ctx context.Context) (AdminView, error) {
	settings, err := s.repository.GetServiceEmailSettings(ctx)
	if err != nil {
		return AdminView{}, err
	}
	return adminView(settings), nil
}

func adminView(settings store.ServiceEmailSettings) AdminView {
	return AdminView{
		ServiceEnabled: settings.ServiceEnabled, RegistrationVerificationEnabled: settings.RegistrationVerificationEnabled,
		PasswordResetEnabled: settings.PasswordResetEnabled, SMTPHost: settings.SMTPHost, SMTPPort: settings.SMTPPort,
		SMTPSecurity: settings.SMTPSecurity, FromName: settings.FromName, FromAddress: settings.FromAddress,
		ReplyTo: settings.ReplyTo, SMTPUsername: settings.SMTPUsername,
		PasswordConfigured: len(settings.SMTPPasswordCiphertext) > 0, Status: settings.Status,
		Version: settings.Version, TestedAt: settings.TestedAt, UpdatedAt: settings.UpdatedAt,
	}
}

func (s *Service) Runtime(ctx context.Context) Runtime {
	settings, err := s.repository.GetServiceEmailSettings(ctx)
	if err != nil {
		return Runtime{Status: "UNAVAILABLE"}
	}
	available := settings.ServiceEnabled && settings.Status == "VERIFIED" && len(settings.SMTPPasswordCiphertext) > 0
	return Runtime{
		ServiceAvailable:                 available,
		RegistrationVerificationRequired: available && settings.RegistrationVerificationEnabled,
		PasswordResetAvailable:           available && settings.PasswordResetEnabled,
		Status:                           settings.Status,
	}
}

// SendPasswordResetLink delivers an opaque, single-use reset link generated
// by the identity provider. The Service never receives or stores a password,
// token hash, or identity credential. Callers must return the same public
// response whether or not an account exists for the supplied address.
func (s *Service) SendPasswordResetLink(ctx context.Context, rawEmail, resetLink string) error {
	settings, config, err := s.deliveryConfig(ctx)
	if err != nil || !settings.PasswordResetEnabled {
		return ErrVerificationDisabled
	}
	email, err := normalizeEmail(rawEmail)
	if err != nil {
		return ErrVerificationInvalid
	}
	link, err := url.Parse(strings.TrimSpace(resetLink))
	if err != nil || link.Scheme != "https" || link.Host == "" || link.User != nil || link.Fragment != "" || len(resetLink) > 4096 {
		return ErrInvalidConfiguration
	}
	body := fmt.Sprintf("请使用下面的链接重置 TermLink 密码。链接 15 分钟内有效，只能使用一次。\r\n\r\n%s\r\n\r\n如果不是你发起的请求，请忽略此邮件。\r\n\r\nUse the link below to reset your TermLink password. It expires in 15 minutes and can be used only once.\r\n\r\n%s", resetLink, resetLink)
	if err = s.sender.Send(ctx, config, email, "TermLink 密码重置 / Password reset", body); err != nil {
		return ErrDeliveryFailed
	}
	return nil
}

func (s *Service) RegistrationVerificationPolicy(ctx context.Context) (bool, error) {
	settings, err := s.repository.GetServiceEmailSettings(ctx)
	if err != nil {
		return false, err
	}
	return settings.RegistrationVerificationEnabled, nil
}

func (s *Service) UpdateAndTest(ctx context.Context, actorID uuid.UUID, input UpdateInput) (AdminView, error) {
	current, err := s.repository.GetServiceEmailSettings(ctx)
	if err != nil {
		return AdminView{}, err
	}
	password := strings.TrimSpace(input.SMTPPassword)
	if password == "" && len(current.SMTPPasswordCiphertext) > 0 {
		password, err = s.openSecret(current.SMTPPasswordCiphertext)
		if err != nil {
			return AdminView{}, ErrUnavailable
		}
	}
	config := SMTPConfig{
		Host: strings.TrimSpace(input.SMTPHost), Port: input.SMTPPort,
		Security: strings.ToUpper(strings.TrimSpace(input.SMTPSecurity)), FromName: strings.TrimSpace(input.FromName),
		FromAddress: strings.TrimSpace(input.FromAddress), ReplyTo: strings.TrimSpace(input.ReplyTo),
		Username: strings.TrimSpace(input.SMTPUsername), Password: password,
	}
	testRecipient, err := normalizeEmail(input.TestRecipient)
	if err != nil || validateSMTPConfig(config) != nil || input.ExpectedVersion != current.Version ||
		(input.RegistrationVerificationEnabled || input.PasswordResetEnabled) && !input.ServiceEnabled {
		return AdminView{}, ErrInvalidConfiguration
	}
	if err = s.sender.Send(ctx, config, testRecipient, "TermLink 服务邮箱测试", "这是一封 TermLink 服务邮箱配置测试邮件。\r\n\r\n如果你收到此邮件，说明当前 SMTP 配置可以完成投递。"); err != nil {
		return AdminView{}, ErrDeliveryFailed
	}
	if input.PasswordResetEnabled || current.PasswordResetEnabled {
		if s.realm == nil {
			return AdminView{}, ErrIdentityUnavailable
		}
		if err = s.realm.ConfigureRealmEmail(ctx, config, input.PasswordResetEnabled); err != nil {
			return AdminView{}, ErrIdentityUnavailable
		}
	}
	ciphertext, err := s.sealSecret(password)
	if err != nil {
		return AdminView{}, ErrUnavailable
	}
	saved, err := s.repository.SaveVerifiedServiceEmailSettings(ctx, actorID, store.ServiceEmailSettingsInput{
		ServiceEnabled: input.ServiceEnabled, RegistrationVerificationEnabled: input.RegistrationVerificationEnabled,
		PasswordResetEnabled: input.PasswordResetEnabled, SMTPHost: config.Host, SMTPPort: config.Port,
		SMTPSecurity: config.Security, FromName: config.FromName, FromAddress: config.FromAddress,
		ReplyTo: config.ReplyTo, SMTPUsername: config.Username, SMTPPasswordCiphertext: ciphertext,
		ExpectedVersion: input.ExpectedVersion,
	})
	if err != nil {
		// The provider may already have accepted the new setting. Best-effort
		// rollback preserves the previously published behavior without exposing
		// either provider error to the browser.
		_ = s.restoreRealm(ctx, current)
		return AdminView{}, err
	}
	return adminView(saved), nil
}

func (s *Service) restoreRealm(ctx context.Context, previous store.ServiceEmailSettings) error {
	if s.realm == nil || len(previous.SMTPPasswordCiphertext) == 0 || previous.Status != "VERIFIED" {
		return nil
	}
	password, err := s.openSecret(previous.SMTPPasswordCiphertext)
	if err != nil {
		return err
	}
	return s.realm.ConfigureRealmEmail(ctx, SMTPConfig{
		Host: previous.SMTPHost, Port: previous.SMTPPort, Security: previous.SMTPSecurity,
		FromName: previous.FromName, FromAddress: previous.FromAddress, ReplyTo: previous.ReplyTo,
		Username: previous.SMTPUsername, Password: password,
	}, previous.PasswordResetEnabled)
}

func (s *Service) RequestRegistrationCode(ctx context.Context, rawEmail string) (Challenge, error) {
	settings, config, err := s.deliveryConfig(ctx)
	if err != nil || !settings.RegistrationVerificationEnabled {
		return Challenge{}, ErrVerificationDisabled
	}
	email, err := normalizeEmail(rawEmail)
	if err != nil {
		return Challenge{}, ErrVerificationInvalid
	}
	code, err := randomCode()
	if err != nil {
		return Challenge{}, ErrUnavailable
	}
	now := s.clock().UTC()
	challenge := Challenge{ID: uuid.New(), ExpiresAt: now.Add(challengeTTL)}
	emailHash := s.emailHash(email)
	codeHash := s.codeHash(challenge.ID, emailHash, code)
	if err = s.repository.CreateRegistrationEmailChallenge(ctx, store.RegistrationEmailChallenge{
		ID: challenge.ID, EmailHash: emailHash, CodeHash: codeHash, ExpiresAt: challenge.ExpiresAt,
	}, challengeResend); err != nil {
		if errors.Is(err, store.ErrEmailChallengeRateLimit) {
			return Challenge{}, err
		}
		return Challenge{}, ErrUnavailable
	}
	body := fmt.Sprintf("你的 TermLink 注册验证码是：%s\r\n\r\n验证码 10 分钟内有效，只能使用一次。请勿转发给任何人。", code)
	if err = s.sender.Send(ctx, config, email, "TermLink 注册验证码", body); err != nil {
		return Challenge{}, ErrDeliveryFailed
	}
	return challenge, nil
}

func (s *Service) VerifyAndConsume(ctx context.Context, challengeID uuid.UUID, rawEmail, code string) error {
	settings, _, err := s.deliveryConfig(ctx)
	if err != nil || !settings.RegistrationVerificationEnabled {
		return ErrVerificationDisabled
	}
	email, err := normalizeEmail(rawEmail)
	code = strings.TrimSpace(code)
	if err != nil || len(code) != 6 {
		return ErrVerificationInvalid
	}
	for _, char := range code {
		if char < '0' || char > '9' {
			return ErrVerificationInvalid
		}
	}
	emailHash := s.emailHash(email)
	codeHash := s.codeHash(challengeID, emailHash, code)
	if err = s.repository.ConsumeRegistrationEmailChallenge(ctx, challengeID, emailHash, codeHash, s.clock().UTC()); err != nil {
		return ErrVerificationInvalid
	}
	return nil
}

func (s *Service) RequestAccountEmailCode(ctx context.Context, accountID uuid.UUID, rawEmail string) (Challenge, error) {
	if accountID == uuid.Nil {
		return Challenge{}, ErrVerificationInvalid
	}
	_, config, err := s.deliveryConfig(ctx)
	if err != nil {
		return Challenge{}, ErrUnavailable
	}
	email, err := normalizeEmail(rawEmail)
	if err != nil {
		return Challenge{}, ErrVerificationInvalid
	}
	code, err := randomCode()
	if err != nil {
		return Challenge{}, ErrUnavailable
	}
	now := s.clock().UTC()
	challenge := Challenge{ID: uuid.New(), ExpiresAt: now.Add(challengeTTL)}
	emailHash := s.emailHash(email)
	codeHash := s.codeHash(challenge.ID, emailHash, code)
	if err = s.repository.CreateAccountEmailChallenge(ctx, store.AccountEmailChallenge{
		ID: challenge.ID, AccountID: accountID, EmailHash: emailHash, CodeHash: codeHash, ExpiresAt: challenge.ExpiresAt,
	}, challengeResend); err != nil {
		if errors.Is(err, store.ErrEmailChallengeRateLimit) {
			return Challenge{}, err
		}
		return Challenge{}, ErrUnavailable
	}
	body := fmt.Sprintf("你的 TermLink 邮箱变更验证码是：%s\r\n\r\n验证码 10 分钟内有效，只能使用一次。如果不是你发起的请求，请忽略此邮件。", code)
	if err = s.sender.Send(ctx, config, email, "TermLink 邮箱变更验证码", body); err != nil {
		return Challenge{}, ErrDeliveryFailed
	}
	return challenge, nil
}

func (s *Service) VerifyAndConsumeAccountEmail(ctx context.Context, accountID, challengeID uuid.UUID, rawEmail, code string) error {
	if accountID == uuid.Nil || challengeID == uuid.Nil {
		return ErrVerificationInvalid
	}
	if _, _, err := s.deliveryConfig(ctx); err != nil {
		return ErrUnavailable
	}
	email, err := normalizeEmail(rawEmail)
	code = strings.TrimSpace(code)
	if err != nil || len(code) != 6 {
		return ErrVerificationInvalid
	}
	for _, char := range code {
		if char < '0' || char > '9' {
			return ErrVerificationInvalid
		}
	}
	emailHash := s.emailHash(email)
	codeHash := s.codeHash(challengeID, emailHash, code)
	if err = s.repository.ConsumeAccountEmailChallenge(ctx, accountID, challengeID, emailHash, codeHash, s.clock().UTC()); err != nil {
		return ErrVerificationInvalid
	}
	return nil
}

func (s *Service) deliveryConfig(ctx context.Context) (store.ServiceEmailSettings, SMTPConfig, error) {
	settings, err := s.repository.GetServiceEmailSettings(ctx)
	if err != nil || !settings.ServiceEnabled || settings.Status != "VERIFIED" || len(settings.SMTPPasswordCiphertext) == 0 {
		return store.ServiceEmailSettings{}, SMTPConfig{}, ErrUnavailable
	}
	password, err := s.openSecret(settings.SMTPPasswordCiphertext)
	if err != nil {
		return store.ServiceEmailSettings{}, SMTPConfig{}, ErrUnavailable
	}
	config := SMTPConfig{Host: settings.SMTPHost, Port: settings.SMTPPort, Security: settings.SMTPSecurity,
		FromName: settings.FromName, FromAddress: settings.FromAddress, ReplyTo: settings.ReplyTo,
		Username: settings.SMTPUsername, Password: password}
	if validateSMTPConfig(config) != nil {
		return store.ServiceEmailSettings{}, SMTPConfig{}, ErrUnavailable
	}
	return settings, config, nil
}

func (s *Service) sealSecret(value string) ([]byte, error) {
	if strings.TrimSpace(value) == "" || len(value) > 4096 {
		return nil, ErrInvalidConfiguration
	}
	block, err := aes.NewCipher(s.key[:])
	if err != nil {
		return nil, err
	}
	aead, err := cipher.NewGCM(block)
	if err != nil {
		return nil, err
	}
	nonce := make([]byte, aead.NonceSize())
	if _, err = rand.Read(nonce); err != nil {
		return nil, err
	}
	return aead.Seal(nonce, nonce, []byte(value), []byte(secretDomain)), nil
}

func (s *Service) openSecret(value []byte) (string, error) {
	block, err := aes.NewCipher(s.key[:])
	if err != nil {
		return "", err
	}
	aead, err := cipher.NewGCM(block)
	if err != nil || len(value) < aead.NonceSize()+aead.Overhead() {
		return "", ErrUnavailable
	}
	plain, err := aead.Open(nil, value[:aead.NonceSize()], value[aead.NonceSize():], []byte(secretDomain))
	if err != nil || len(plain) == 0 || len(plain) > 4096 {
		return "", ErrUnavailable
	}
	return string(plain), nil
}

func (s *Service) emailHash(email string) [32]byte {
	mac := hmac.New(sha256.New, s.key[:])
	_, _ = mac.Write([]byte(emailDigestDomain))
	_, _ = mac.Write([]byte{0})
	_, _ = mac.Write([]byte(email))
	var result [32]byte
	copy(result[:], mac.Sum(nil))
	return result
}

func (s *Service) codeHash(id uuid.UUID, emailHash [32]byte, code string) [32]byte {
	mac := hmac.New(sha256.New, s.key[:])
	_, _ = mac.Write([]byte(codeDigestDomain))
	_, _ = mac.Write([]byte{0})
	_, _ = mac.Write(id[:])
	_, _ = mac.Write(emailHash[:])
	_, _ = mac.Write([]byte(code))
	var result [32]byte
	copy(result[:], mac.Sum(nil))
	return result
}

func randomCode() (string, error) {
	value, err := rand.Int(rand.Reader, big.NewInt(1_000_000))
	if err != nil {
		return "", err
	}
	return fmt.Sprintf("%06d", value.Int64()), nil
}

func normalizeEmail(value string) (string, error) {
	value = strings.TrimSpace(value)
	if value == "" || len(value) > 320 || strings.ContainsAny(value, "\r\n\t ") {
		return "", ErrInvalidConfiguration
	}
	address, err := mail.ParseAddress(value)
	if err != nil || !strings.EqualFold(address.Address, value) {
		return "", ErrInvalidConfiguration
	}
	local, domain, ok := strings.Cut(address.Address, "@")
	if !ok || local == "" || domain == "" {
		return "", ErrInvalidConfiguration
	}
	return local + "@" + strings.ToLower(domain), nil
}

func validateSMTPConfig(config SMTPConfig) error {
	if !validSMTPHost(config.Host) || config.Port < 1 || config.Port > 65535 ||
		(config.Security != "STARTTLS" && config.Security != "TLS") ||
		strings.TrimSpace(config.Password) == "" || len(config.Password) > 4096 ||
		len(config.Username) > 320 || strings.ContainsAny(config.Username, "\r\n") ||
		len(config.FromName) > 128 || !utf8.ValidString(config.FromName) || strings.ContainsAny(config.FromName, "\r\n") {
		return ErrInvalidConfiguration
	}
	if _, err := normalizeEmail(config.FromAddress); err != nil {
		return err
	}
	if config.ReplyTo != "" {
		if _, err := normalizeEmail(config.ReplyTo); err != nil {
			return err
		}
	}
	return nil
}

func validSMTPHost(host string) bool {
	host = strings.TrimSpace(host)
	if host == "" || len(host) > 253 || strings.ContainsAny(host, "/\\:@?#[]\r\n\t ") {
		return false
	}
	if ip := net.ParseIP(host); ip != nil {
		return !ip.IsUnspecified() && !ip.IsMulticast() && !ip.IsLinkLocalUnicast()
	}
	for _, label := range strings.Split(host, ".") {
		if label == "" || len(label) > 63 || label[0] == '-' || label[len(label)-1] == '-' {
			return false
		}
		for _, char := range label {
			if (char < 'a' || char > 'z') && (char < 'A' || char > 'Z') && (char < '0' || char > '9') && char != '-' {
				return false
			}
		}
	}
	return true
}

type smtpSender struct{}

func (smtpSender) Send(ctx context.Context, config SMTPConfig, recipient, subject, body string) error {
	if validateSMTPConfig(config) != nil || len(subject) == 0 || len(subject) > 200 ||
		strings.ContainsAny(subject, "\r\n") || len(body) > 64<<10 {
		return ErrDeliveryFailed
	}
	recipient, err := normalizeEmail(recipient)
	if err != nil {
		return ErrDeliveryFailed
	}
	sendCtx, cancel := context.WithTimeout(ctx, mailSendTimeout)
	defer cancel()
	address := net.JoinHostPort(config.Host, strconv.Itoa(config.Port))
	dialer := net.Dialer{Timeout: mailSendTimeout}
	var connection net.Conn
	if config.Security == "TLS" {
		connection, err = tlsDialContext(sendCtx, &dialer, address, config.Host)
	} else {
		connection, err = dialer.DialContext(sendCtx, "tcp", address)
	}
	if err != nil {
		return ErrDeliveryFailed
	}
	defer connection.Close()
	if deadline, ok := sendCtx.Deadline(); ok {
		_ = connection.SetDeadline(deadline)
	}
	client, err := smtp.NewClient(connection, config.Host)
	if err != nil {
		return ErrDeliveryFailed
	}
	defer client.Close()
	if config.Security == "STARTTLS" {
		if ok, _ := client.Extension("STARTTLS"); !ok {
			return ErrDeliveryFailed
		}
		if err = client.StartTLS(tlsConfig(config.Host)); err != nil {
			return ErrDeliveryFailed
		}
	}
	if config.Username != "" {
		if err = client.Auth(smtp.PlainAuth("", config.Username, config.Password, config.Host)); err != nil {
			return ErrDeliveryFailed
		}
	}
	if err = client.Mail(config.FromAddress); err != nil {
		return ErrDeliveryFailed
	}
	if err = client.Rcpt(recipient); err != nil {
		return ErrDeliveryFailed
	}
	writer, err := client.Data()
	if err != nil {
		return ErrDeliveryFailed
	}
	from := (&mail.Address{Name: config.FromName, Address: config.FromAddress}).String()
	message := "From: " + from + "\r\nTo: " + recipient + "\r\nSubject: " + subject +
		"\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=UTF-8\r\n"
	if config.ReplyTo != "" {
		message += "Reply-To: " + config.ReplyTo + "\r\n"
	}
	normalizedBody := strings.ReplaceAll(body, "\r\n", "\n")
	message += "\r\n" + strings.ReplaceAll(normalizedBody, "\n", "\r\n")
	if _, err = writer.Write([]byte(message)); err != nil {
		_ = writer.Close()
		return ErrDeliveryFailed
	}
	if err = writer.Close(); err != nil {
		return ErrDeliveryFailed
	}
	if err = client.Quit(); err != nil {
		return ErrDeliveryFailed
	}
	return nil
}

// Small seams keep TLS policy testable without exporting transport details.
var tlsDialContext = func(ctx context.Context, dialer *net.Dialer, address, serverName string) (net.Conn, error) {
	return (&tls.Dialer{NetDialer: dialer, Config: tlsConfig(serverName)}).DialContext(ctx, "tcp", address)
}

func tlsConfig(serverName string) *tls.Config {
	return &tls.Config{MinVersion: tls.VersionTLS12, ServerName: serverName}
}
