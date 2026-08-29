package commerce

import (
	"bytes"
	"crypto/rsa"
	"crypto/x509"
	"encoding/pem"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
)

const (
	secretScheme       = "docker-secret://"
	defaultSecretLimit = int64(64 << 10)
)

var (
	ErrUnsafeSecretReference        = errors.New("commerce: unsafe secret reference")
	ErrSecretTooLarge               = errors.New("commerce: secret exceeds size limit")
	ErrInvalidSecret                = errors.New("commerce: invalid secret material")
	ErrInvalidChannelConfig         = errors.New("commerce: invalid payment channel configuration")
	ErrPaymentChannelUnavailable    = errors.New("commerce: payment channel unavailable")
	ErrInvalidRequest               = errors.New("commerce: invalid request")
	ErrPaymentResultMismatch        = errors.New("commerce: payment result does not match order")
	ErrPaymentTransactionMissing    = errors.New("commerce: payment transaction missing")
	ErrPaymentStateNotProgressable  = errors.New("commerce: payment state cannot progress")
	ErrGatewayValidationUnavailable = errors.New("commerce: gateway validation unavailable")
)

// SecretRef is a reference to a Docker secret. Its String method is
// deliberately redacted so accidental formatting cannot disclose even the
// secret name.
type SecretRef string

func (SecretRef) String() string { return "docker-secret://[redacted]" }

func (SecretRef) GoString() string { return "commerce.SecretRef(\"[redacted]\")" }

// SecretLoader resolves Docker secret references beneath a single configured
// directory. References are names, not paths; symlinks and non-regular files
// are rejected.
type SecretLoader struct {
	root     string
	maxBytes int64
}

func NewSecretLoader(root string, maxBytes int64) (*SecretLoader, error) {
	root = strings.TrimSpace(root)
	if root == "" {
		return nil, fmt.Errorf("%w: secret root is empty", ErrInvalidChannelConfig)
	}
	abs, err := filepath.Abs(root)
	if err != nil {
		return nil, fmt.Errorf("%w: resolve secret root", ErrInvalidChannelConfig)
	}
	if maxBytes <= 0 {
		maxBytes = defaultSecretLimit
	}
	if maxBytes > 1<<20 {
		return nil, fmt.Errorf("%w: secret size limit is too large", ErrInvalidChannelConfig)
	}
	return &SecretLoader{root: filepath.Clean(abs), maxBytes: maxBytes}, nil
}

func (l *SecretLoader) Read(ref SecretRef) ([]byte, error) {
	name, err := parseSecretRef(ref)
	if err != nil {
		return nil, err
	}
	rootInfo, err := os.Stat(l.root)
	if err != nil || !rootInfo.IsDir() {
		return nil, fmt.Errorf("%w: secret root unavailable", ErrInvalidSecret)
	}
	resolvedRoot, err := filepath.EvalSymlinks(l.root)
	if err != nil {
		return nil, fmt.Errorf("%w: resolve secret root", ErrInvalidSecret)
	}
	resolvedRoot, err = filepath.Abs(resolvedRoot)
	if err != nil {
		return nil, fmt.Errorf("%w: resolve secret root", ErrInvalidSecret)
	}
	target := filepath.Join(resolvedRoot, name)
	if !withinDirectory(resolvedRoot, target) {
		return nil, ErrUnsafeSecretReference
	}
	before, err := os.Lstat(target)
	if err != nil {
		return nil, fmt.Errorf("%w: secret unavailable", ErrInvalidSecret)
	}
	if before.Mode()&os.ModeSymlink != 0 || !before.Mode().IsRegular() {
		return nil, ErrUnsafeSecretReference
	}
	if before.Size() > l.maxBytes {
		return nil, ErrSecretTooLarge
	}
	file, err := os.Open(target)
	if err != nil {
		return nil, fmt.Errorf("%w: open secret", ErrInvalidSecret)
	}
	defer file.Close()
	after, err := file.Stat()
	if err != nil || !after.Mode().IsRegular() || !os.SameFile(before, after) {
		return nil, ErrUnsafeSecretReference
	}
	data, err := io.ReadAll(io.LimitReader(file, l.maxBytes+1))
	if err != nil {
		return nil, fmt.Errorf("%w: read secret", ErrInvalidSecret)
	}
	if int64(len(data)) > l.maxBytes {
		return nil, ErrSecretTooLarge
	}
	return data, nil
}

func parseSecretRef(ref SecretRef) (string, error) {
	value := string(ref)
	if !strings.HasPrefix(value, secretScheme) {
		return "", ErrUnsafeSecretReference
	}
	name := strings.TrimPrefix(value, secretScheme)
	if len(name) < 1 || len(name) > 128 {
		return "", ErrUnsafeSecretReference
	}
	for i, char := range name {
		if (char >= 'a' && char <= 'z') || (char >= 'A' && char <= 'Z') || (char >= '0' && char <= '9') || ((char == '_' || char == '-') && i > 0) {
			continue
		}
		return "", ErrUnsafeSecretReference
	}
	return name, nil
}

func withinDirectory(root, target string) bool {
	rel, err := filepath.Rel(root, target)
	return err == nil && rel != "." && rel != ".." && !strings.HasPrefix(rel, ".."+string(filepath.Separator)) && !filepath.IsAbs(rel)
}

func parseRSAPrivateKey(data []byte) (*rsa.PrivateKey, error) {
	block, rest := pem.Decode(data)
	if block == nil || len(bytes.TrimSpace(rest)) != 0 {
		return nil, ErrInvalidSecret
	}
	var key *rsa.PrivateKey
	switch block.Type {
	case "PRIVATE KEY":
		parsed, err := x509.ParsePKCS8PrivateKey(block.Bytes)
		if err != nil {
			return nil, ErrInvalidSecret
		}
		var ok bool
		key, ok = parsed.(*rsa.PrivateKey)
		if !ok {
			return nil, ErrInvalidSecret
		}
	case "RSA PRIVATE KEY":
		var err error
		key, err = x509.ParsePKCS1PrivateKey(block.Bytes)
		if err != nil {
			return nil, ErrInvalidSecret
		}
	default:
		return nil, ErrInvalidSecret
	}
	if key.N == nil || key.N.BitLen() < 2048 || key.Validate() != nil {
		return nil, ErrInvalidSecret
	}
	return key, nil
}

func parseRSAPublicKey(data []byte) (*rsa.PublicKey, error) {
	block, rest := pem.Decode(data)
	if block == nil || len(bytes.TrimSpace(rest)) != 0 {
		return nil, ErrInvalidSecret
	}
	var key *rsa.PublicKey
	switch block.Type {
	case "PUBLIC KEY":
		parsed, err := x509.ParsePKIXPublicKey(block.Bytes)
		if err != nil {
			return nil, ErrInvalidSecret
		}
		var ok bool
		key, ok = parsed.(*rsa.PublicKey)
		if !ok {
			return nil, ErrInvalidSecret
		}
	case "RSA PUBLIC KEY":
		var err error
		key, err = x509.ParsePKCS1PublicKey(block.Bytes)
		if err != nil {
			return nil, ErrInvalidSecret
		}
	case "CERTIFICATE":
		certificate, err := x509.ParseCertificate(block.Bytes)
		if err != nil {
			return nil, ErrInvalidSecret
		}
		var ok bool
		key, ok = certificate.PublicKey.(*rsa.PublicKey)
		if !ok {
			return nil, ErrInvalidSecret
		}
	default:
		return nil, ErrInvalidSecret
	}
	if key.N == nil || key.N.BitLen() < 2048 || key.E < 3 || key.E%2 == 0 {
		return nil, ErrInvalidSecret
	}
	return key, nil
}
