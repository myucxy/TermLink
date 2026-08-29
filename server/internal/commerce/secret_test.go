package commerce

import (
	"context"
	"crypto/rand"
	"crypto/rsa"
	"crypto/x509"
	"encoding/json"
	"encoding/pem"
	"errors"
	"os"
	"path/filepath"
	"testing"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

func TestSecretLoaderRejectsTraversalSymlinkAndOversize(t *testing.T) {
	root := t.TempDir()
	loader, err := NewSecretLoader(root, 16)
	if err != nil {
		t.Fatal(err)
	}
	for _, ref := range []SecretRef{
		"file://secret", "secret://secret", "docker-secret://../secret", "docker-secret://sub/secret",
		"docker-secret://.hidden", "docker-secret://name.txt", "docker-secret://",
	} {
		if _, err := loader.Read(ref); !errors.Is(err, ErrUnsafeSecretReference) {
			t.Fatalf("unsafe reference %q returned %v", string(ref), err)
		}
	}
	if err := os.WriteFile(filepath.Join(root, "large-secret"), make([]byte, 17), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := loader.Read("docker-secret://large-secret"); !errors.Is(err, ErrSecretTooLarge) {
		t.Fatalf("oversize secret returned %v", err)
	}

	outside := filepath.Join(t.TempDir(), "outside-secret")
	if err := os.WriteFile(outside, []byte("secret"), 0o600); err != nil {
		t.Fatal(err)
	}
	link := filepath.Join(root, "linked-secret")
	if err := os.Symlink(outside, link); err != nil {
		t.Skipf("symlink creation unavailable: %v", err)
	}
	if _, err := loader.Read("docker-secret://linked-secret"); !errors.Is(err, ErrUnsafeSecretReference) {
		t.Fatalf("symlink secret returned %v", err)
	}
}

func TestSecretLoaderAndRSAParsers(t *testing.T) {
	root := t.TempDir()
	loader, err := NewSecretLoader(root, defaultSecretLimit)
	if err != nil {
		t.Fatal(err)
	}
	key, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		t.Fatal(err)
	}
	pkcs8, err := x509.MarshalPKCS8PrivateKey(key)
	if err != nil {
		t.Fatal(err)
	}
	privatePEM := pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: pkcs8})
	publicDER, err := x509.MarshalPKIXPublicKey(&key.PublicKey)
	if err != nil {
		t.Fatal(err)
	}
	publicPEM := pem.EncodeToMemory(&pem.Block{Type: "PUBLIC KEY", Bytes: publicDER})
	if err := os.WriteFile(filepath.Join(root, "private-key"), privatePEM, 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(root, "public-key"), publicPEM, 0o600); err != nil {
		t.Fatal(err)
	}
	loadedPrivate, err := loader.Read("docker-secret://private-key")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := parseRSAPrivateKey(loadedPrivate); err != nil {
		t.Fatalf("PKCS8 key rejected: %v", err)
	}
	if _, err := parseRSAPrivateKey(pem.EncodeToMemory(&pem.Block{Type: "RSA PRIVATE KEY", Bytes: x509.MarshalPKCS1PrivateKey(key)})); err != nil {
		t.Fatalf("PKCS1 key rejected: %v", err)
	}
	loadedPublic, err := loader.Read("docker-secret://public-key")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := parseRSAPublicKey(loadedPublic); err != nil {
		t.Fatalf("public key rejected: %v", err)
	}
}

func TestGatewayResolverRejectsDisabledAndUnknownConfiguration(t *testing.T) {
	root := t.TempDir()
	channelID := uuid.New()
	versionID := uuid.New()
	repository := &fakeRepository{
		channel: store.PaymentChannel{ID: channelID, Provider: "ALIPAY", Scene: "NATIVE", Enabled: false, ActiveVersionID: &versionID, HealthStatus: "HEALTHY"},
		version: store.PaymentChannelVersion{ID: versionID, ChannelID: channelID, Environment: "PRODUCTION", Status: "VALIDATED", PublicConfig: json.RawMessage(`{"app_id":"app","seller_id":"seller","notify_url":"https://example.test/webhook"}`), SecretRefs: json.RawMessage(`{"merchant_private_key":"docker-secret://private-key","alipay_public_key":"docker-secret://public-key"}`)},
	}
	resolver, err := NewChannelGatewayResolver(GatewayResolverConfig{Repository: repository, SecretRoot: root})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := resolver.Resolve(context.Background(), "ALIPAY", "NATIVE"); !errors.Is(err, ErrPaymentChannelUnavailable) {
		t.Fatalf("disabled channel returned %v", err)
	}

	repository.channel.Enabled = true
	repository.version.PublicConfig = json.RawMessage(`{"app_id":"app","seller_id":"seller","notify_url":"https://example.test/webhook","private_key":"raw","unexpected":true}`)
	if _, err := resolver.Resolve(context.Background(), "ALIPAY", "NATIVE"); !errors.Is(err, ErrInvalidChannelConfig) {
		t.Fatalf("unknown/sensitive public field returned %v", err)
	}
	repository.version.PublicConfig = json.RawMessage(`{"app_id":"first","app_id":"second","seller_id":"seller","notify_url":"https://example.test/webhook"}`)
	if _, err := resolver.Resolve(context.Background(), "ALIPAY", "NATIVE"); !errors.Is(err, ErrInvalidChannelConfig) {
		t.Fatalf("duplicate configuration field returned %v", err)
	}
}

func TestGatewayResolverCacheIsBoundedAndVersioned(t *testing.T) {
	root := t.TempDir()
	writeTestRSASecrets(t, root)
	channelID := uuid.New()
	versionID := uuid.New()
	activeVersionID := versionID
	repository := &fakeRepository{
		channel: store.PaymentChannel{ID: channelID, Provider: "ALIPAY", Scene: "NATIVE", Enabled: true, ActiveVersionID: &activeVersionID, HealthStatus: "HEALTHY"},
		version: store.PaymentChannelVersion{ID: versionID, ChannelID: channelID, Environment: "PRODUCTION", Status: "VALIDATED", PublicConfig: json.RawMessage(`{"app_id":"app","seller_id":"seller","notify_url":"https://example.test/webhook"}`), SecretRefs: json.RawMessage(`{"merchant_private_key":"docker-secret://private-key","alipay_public_key":"docker-secret://public-key"}`)},
	}
	resolver, err := NewChannelGatewayResolver(GatewayResolverConfig{Repository: repository, SecretRoot: root})
	if err != nil {
		t.Fatal(err)
	}
	first, err := resolver.Resolve(context.Background(), "ALIPAY", "NATIVE")
	if err != nil {
		t.Fatal(err)
	}
	second, err := resolver.Resolve(context.Background(), "ALIPAY", "NATIVE")
	if err != nil {
		t.Fatal(err)
	}
	if first != second || len(resolver.cache) != 1 {
		t.Fatalf("same version was not cached or cache unbounded: same=%t size=%d", first == second, len(resolver.cache))
	}
	newVersionID := uuid.New()
	repository.version.ID = newVersionID
	repository.channel.ActiveVersionID = &newVersionID
	third, err := resolver.Resolve(context.Background(), "ALIPAY", "NATIVE")
	if err != nil {
		t.Fatal(err)
	}
	if third == first || len(resolver.cache) != 1 {
		t.Fatalf("version change did not replace bounded cache entry: same=%t size=%d", third == first, len(resolver.cache))
	}
}

func TestValidatePaymentChannelVersionBuildsDraftLocally(t *testing.T) {
	root := t.TempDir()
	writeTestRSASecrets(t, root)
	channelID := uuid.New()
	channel := store.PaymentChannel{ID: channelID, Provider: "ALIPAY", Scene: "NATIVE"}
	version := store.PaymentChannelVersion{ID: uuid.New(), ChannelID: channelID, Environment: "PRODUCTION", Status: "DRAFT", PublicConfig: json.RawMessage(`{"app_id":"app","seller_id":"seller","notify_url":"https://example.test/webhook"}`), SecretRefs: json.RawMessage(`{"merchant_private_key":"docker-secret://private-key","alipay_public_key":"docker-secret://public-key"}`)}
	repository := &fakeRepository{}
	resolver, err := NewChannelGatewayResolver(GatewayResolverConfig{Repository: repository, SecretRoot: root})
	if err != nil {
		t.Fatal(err)
	}
	service, err := NewService(Config{Repository: repository, Gateways: resolver, DigestKey: []byte("01234567890123456789012345678901")})
	if err != nil {
		t.Fatal(err)
	}
	if err := service.ValidatePaymentChannelVersion(context.Background(), channel, version); err != nil {
		t.Fatalf("valid local draft rejected: %v", err)
	}
	version.Status = "VALIDATED"
	if err := service.ValidatePaymentChannelVersion(context.Background(), channel, version); !errors.Is(err, ErrInvalidChannelConfig) {
		t.Fatalf("non-draft accepted: %v", err)
	}
	version.Status = "DRAFT"
	channel.Scene = "H5"
	if err := service.ValidatePaymentChannelVersion(context.Background(), channel, version); !errors.Is(err, ErrInvalidChannelConfig) {
		t.Fatalf("unsupported provider scene accepted: %v", err)
	}
}

func writeTestRSASecrets(t *testing.T, root string) {
	t.Helper()
	key, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		t.Fatal(err)
	}
	pkcs8, err := x509.MarshalPKCS8PrivateKey(key)
	if err != nil {
		t.Fatal(err)
	}
	publicDER, err := x509.MarshalPKIXPublicKey(&key.PublicKey)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(root, "private-key"), pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: pkcs8}), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(root, "public-key"), pem.EncodeToMemory(&pem.Block{Type: "PUBLIC KEY", Bytes: publicDER}), 0o600); err != nil {
		t.Fatal(err)
	}
}
