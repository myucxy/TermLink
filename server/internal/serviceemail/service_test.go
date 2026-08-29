package serviceemail

import (
	"context"
	"errors"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

type testRepository struct {
	settings              store.ServiceEmailSettings
	saved                 store.ServiceEmailSettingsInput
	saveErr               error
	registrationChallenge store.RegistrationEmailChallenge
	accountChallenge      store.AccountEmailChallenge
	consumedRegistration  bool
	consumedAccount       bool
}

func (r *testRepository) GetServiceEmailSettings(context.Context) (store.ServiceEmailSettings, error) {
	return r.settings, nil
}

func (r *testRepository) SaveVerifiedServiceEmailSettings(_ context.Context, _ uuid.UUID, input store.ServiceEmailSettingsInput) (store.ServiceEmailSettings, error) {
	r.saved = input
	if r.saveErr != nil {
		return store.ServiceEmailSettings{}, r.saveErr
	}
	r.settings = store.ServiceEmailSettings{
		ServiceEnabled: input.ServiceEnabled, RegistrationVerificationEnabled: input.RegistrationVerificationEnabled,
		PasswordResetEnabled: input.PasswordResetEnabled, SMTPHost: input.SMTPHost, SMTPPort: input.SMTPPort,
		SMTPSecurity: input.SMTPSecurity, FromName: input.FromName, FromAddress: input.FromAddress,
		ReplyTo: input.ReplyTo, SMTPUsername: input.SMTPUsername, SMTPPasswordCiphertext: append([]byte(nil), input.SMTPPasswordCiphertext...),
		Status: "VERIFIED", Version: input.ExpectedVersion + 1,
	}
	return r.settings, nil
}

func (r *testRepository) CreateRegistrationEmailChallenge(_ context.Context, challenge store.RegistrationEmailChallenge, _ time.Duration) error {
	r.registrationChallenge = challenge
	return nil
}

func (r *testRepository) ConsumeRegistrationEmailChallenge(_ context.Context, id uuid.UUID, emailHash, codeHash [32]byte, _ time.Time) error {
	if id != r.registrationChallenge.ID || emailHash != r.registrationChallenge.EmailHash || codeHash != r.registrationChallenge.CodeHash {
		return store.ErrEmailChallengeInvalid
	}
	r.consumedRegistration = true
	return nil
}

func (r *testRepository) CreateAccountEmailChallenge(_ context.Context, challenge store.AccountEmailChallenge, _ time.Duration) error {
	r.accountChallenge = challenge
	return nil
}

func (r *testRepository) ConsumeAccountEmailChallenge(_ context.Context, accountID, id uuid.UUID, emailHash, codeHash [32]byte, _ time.Time) error {
	if accountID != r.accountChallenge.AccountID || id != r.accountChallenge.ID ||
		emailHash != r.accountChallenge.EmailHash || codeHash != r.accountChallenge.CodeHash {
		return store.ErrEmailChallengeInvalid
	}
	r.consumedAccount = true
	return nil
}

type testSender struct {
	config    SMTPConfig
	recipient string
	subject   string
	body      string
	err       error
}

func (s *testSender) Send(_ context.Context, config SMTPConfig, recipient, subject, body string) error {
	s.config, s.recipient, s.subject, s.body = config, recipient, subject, body
	return s.err
}

type testRealm struct {
	calls         int
	config        SMTPConfig
	passwordReset bool
	err           error
}

func (r *testRealm) ConfigureRealmEmail(_ context.Context, config SMTPConfig, passwordReset bool) error {
	r.calls++
	r.config, r.passwordReset = config, passwordReset
	return r.err
}

func verifiedSettings() store.ServiceEmailSettings {
	return store.ServiceEmailSettings{
		ServiceEnabled: true, RegistrationVerificationEnabled: true, SMTPHost: "smtp.example.test", SMTPPort: 587,
		SMTPSecurity: "STARTTLS", FromName: "TermLink", FromAddress: "no-reply@example.test",
		SMTPUsername: "smtp-user", Status: "VERIFIED", Version: 2,
	}
}

func TestUpdateAndTestPublishesEncryptedRegistrationOnlyConfiguration(t *testing.T) {
	repository := &testRepository{settings: store.ServiceEmailSettings{Version: 1, Status: "DRAFT"}}
	sender := &testSender{}
	service, err := New(repository, []byte("service-email-test-key-0123456789abcdef"), sender, nil)
	if err != nil {
		t.Fatal(err)
	}
	view, err := service.UpdateAndTest(context.Background(), uuid.New(), UpdateInput{
		ServiceEnabled: true, RegistrationVerificationEnabled: true,
		SMTPHost: "smtp.example.test", SMTPPort: 587, SMTPSecurity: "STARTTLS",
		FromName: "TermLink", FromAddress: "no-reply@example.test", SMTPUsername: "smtp-user",
		SMTPPassword: "smtp-secret-value", TestRecipient: "test@example.test", ExpectedVersion: 1,
	})
	if err != nil || view.Status != "VERIFIED" || sender.recipient != "test@example.test" {
		t.Fatalf("view=%+v recipient=%q err=%v", view, sender.recipient, err)
	}
	if len(repository.saved.SMTPPasswordCiphertext) <= len("smtp-secret-value") ||
		strings.Contains(string(repository.saved.SMTPPasswordCiphertext), "smtp-secret-value") {
		t.Fatal("SMTP password was not stored as authenticated ciphertext")
	}
}

func TestPasswordResetConfigurationRequiresRealmAndRollsBackOnSaveFailure(t *testing.T) {
	repository := &testRepository{settings: store.ServiceEmailSettings{Version: 1, Status: "DRAFT"}, saveErr: store.ErrConflict}
	sender := &testSender{}
	realm := &testRealm{}
	service, err := New(repository, []byte("service-email-test-key-0123456789abcdef"), sender, realm)
	if err != nil {
		t.Fatal(err)
	}
	_, err = service.UpdateAndTest(context.Background(), uuid.New(), UpdateInput{
		ServiceEnabled: true, PasswordResetEnabled: true, SMTPHost: "smtp.example.test", SMTPPort: 465,
		SMTPSecurity: "TLS", FromName: "TermLink", FromAddress: "no-reply@example.test",
		SMTPUsername: "smtp-user", SMTPPassword: "smtp-secret-value", TestRecipient: "test@example.test", ExpectedVersion: 1,
	})
	if !errors.Is(err, store.ErrConflict) || realm.calls != 1 || !realm.passwordReset {
		t.Fatalf("err=%v realm calls/reset=%d/%v", err, realm.calls, realm.passwordReset)
	}
}

func TestRegistrationAndAccountCodesAreHashedBoundAndConsumable(t *testing.T) {
	repository := &testRepository{settings: verifiedSettings()}
	sender := &testSender{}
	service, err := New(repository, []byte("service-email-test-key-0123456789abcdef"), sender, nil)
	if err != nil {
		t.Fatal(err)
	}
	repository.settings.SMTPPasswordCiphertext, err = service.sealSecret("smtp-secret-value")
	if err != nil {
		t.Fatal(err)
	}
	now := time.Date(2026, 8, 27, 10, 0, 0, 0, time.UTC)
	service.clock = func() time.Time { return now }
	registration, err := service.RequestRegistrationCode(context.Background(), "User@Example.Test")
	if err != nil || registration.ExpiresAt != now.Add(challengeTTL) {
		t.Fatalf("registration challenge=%+v err=%v", registration, err)
	}
	registrationCode := codeFromBody(t, sender.body)
	if strings.Contains(string(repository.registrationChallenge.CodeHash[:]), registrationCode) {
		t.Fatal("registration code appeared in persisted hash bytes")
	}
	if err = service.VerifyAndConsume(context.Background(), registration.ID, "User@example.test", registrationCode); err != nil || !repository.consumedRegistration {
		t.Fatalf("registration consume err=%v", err)
	}

	accountID := uuid.New()
	account, err := service.RequestAccountEmailCode(context.Background(), accountID, "New@Example.Test")
	if err != nil || repository.accountChallenge.AccountID != accountID {
		t.Fatalf("account challenge=%+v err=%v", account, err)
	}
	accountCode := codeFromBody(t, sender.body)
	if err = service.VerifyAndConsumeAccountEmail(context.Background(), accountID, account.ID, "New@example.test", accountCode); err != nil || !repository.consumedAccount {
		t.Fatalf("account consume err=%v", err)
	}
	if err = service.VerifyAndConsumeAccountEmail(context.Background(), uuid.New(), account.ID, "New@example.test", accountCode); !errors.Is(err, ErrVerificationInvalid) {
		t.Fatalf("cross-account challenge error=%v", err)
	}
}

func TestSMTPSenderRejectsHeaderInjectionBeforeNetwork(t *testing.T) {
	config := SMTPConfig{Host: "smtp.example.test", Port: 587, Security: "STARTTLS", FromAddress: "no-reply@example.test", Password: "secret"}
	if err := (smtpSender{}).Send(context.Background(), config, "user@example.test", "safe\r\nBcc: victim@example.test", "body"); !errors.Is(err, ErrDeliveryFailed) {
		t.Fatalf("header injection error=%v", err)
	}
	if tlsConfig("smtp.example.test").MinVersion == 0 {
		t.Fatal("SMTP TLS minimum version is not pinned")
	}
}

func codeFromBody(t *testing.T, body string) string {
	t.Helper()
	marker := "："
	index := strings.Index(body, marker)
	if index < 0 {
		t.Fatalf("code marker missing from %q", body)
	}
	code := []rune(body[index+len(marker):])
	if len(code) < 6 {
		t.Fatalf("code missing from %q", body)
	}
	return string(code[:6])
}
