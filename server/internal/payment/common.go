package payment

import (
	"crypto"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
)

const (
	defaultResponseLimit int64 = 1 << 20
	defaultWebhookLimit        = 1 << 20
	defaultReplayWindow        = 10 * time.Minute
)

type HTTPClient interface {
	Do(*http.Request) (*http.Response, error)
}

type Clock func() time.Time

func normalizeCurrency(currency string) (string, error) {
	currency = strings.ToUpper(strings.TrimSpace(currency))
	if currency == "" {
		currency = CurrencyCNY
	}
	if currency != CurrencyCNY {
		return "", fmt.Errorf("%w: only CNY is supported", ErrInvalidRequest)
	}
	return currency, nil
}

func validateOrder(order OrderRequest) (OrderRequest, error) {
	order.MerchantOrderID = strings.TrimSpace(order.MerchantOrderID)
	order.Subject = strings.TrimSpace(order.Subject)
	if !validMerchantIdentifier(order.MerchantOrderID) || order.Subject == "" || len([]rune(order.Subject)) > 127 || order.AmountMinor <= 0 {
		return OrderRequest{}, ErrInvalidRequest
	}
	var err error
	order.Currency, err = normalizeCurrency(order.Currency)
	if err != nil {
		return OrderRequest{}, err
	}
	if order.Flow == "" {
		order.Flow = FlowNative
	}
	return order, nil
}

func validateRefund(refund RefundRequest) (RefundRequest, error) {
	refund.MerchantOrderID = strings.TrimSpace(refund.MerchantOrderID)
	refund.MerchantRefundID = strings.TrimSpace(refund.MerchantRefundID)
	refund.Reason = strings.TrimSpace(refund.Reason)
	if !validMerchantIdentifier(refund.MerchantOrderID) || !validMerchantIdentifier(refund.MerchantRefundID) || refund.AmountMinor <= 0 || refund.TotalAmountMinor <= 0 || refund.AmountMinor > refund.TotalAmountMinor {
		return RefundRequest{}, ErrInvalidRequest
	}
	var err error
	refund.Currency, err = normalizeCurrency(refund.Currency)
	if err != nil {
		return RefundRequest{}, err
	}
	return refund, nil
}

func randomHealthMerchantID() (string, error) {
	value := make([]byte, 16)
	if _, err := rand.Read(value); err != nil {
		return "", err
	}
	defer clear(value)
	return "TLHC" + strings.ToUpper(hex.EncodeToString(value)), nil
}

func amountString(minor int64) string {
	return fmt.Sprintf("%d.%02d", minor/100, minor%100)
}

func parseAmountMinor(value string) (int64, error) {
	value = strings.TrimSpace(value)
	parts := strings.Split(value, ".")
	if len(parts) > 2 || parts[0] == "" || strings.HasPrefix(parts[0], "-") || strings.HasPrefix(parts[0], "+") {
		return 0, ErrMalformedPayload
	}
	whole, err := strconv.ParseInt(parts[0], 10, 64)
	if err != nil {
		return 0, ErrMalformedPayload
	}
	fraction := ""
	if len(parts) == 2 {
		fraction = parts[1]
	}
	if len(fraction) > 2 {
		return 0, ErrMalformedPayload
	}
	for len(fraction) < 2 {
		fraction += "0"
	}
	frac := int64(0)
	if fraction != "" {
		frac, err = strconv.ParseInt(fraction, 10, 64)
		if err != nil {
			return 0, ErrMalformedPayload
		}
	}
	if whole > (int64(^uint64(0)>>1)-frac)/100 {
		return 0, ErrMalformedPayload
	}
	return whole*100 + frac, nil
}

func rsaSign(privateKey *rsa.PrivateKey, message []byte) (string, error) {
	hash := sha256.Sum256(message)
	signature, err := rsa.SignPKCS1v15(rand.Reader, privateKey, crypto.SHA256, hash[:])
	if err != nil {
		return "", fmt.Errorf("sign payment request: %w", err)
	}
	return base64.StdEncoding.EncodeToString(signature), nil
}

func rsaVerify(publicKey *rsa.PublicKey, message []byte, signature string) error {
	decoded, err := base64.StdEncoding.DecodeString(signature)
	if err != nil {
		return ErrInvalidSignature
	}
	hash := sha256.Sum256(message)
	if err := rsa.VerifyPKCS1v15(publicKey, crypto.SHA256, hash[:], decoded); err != nil {
		return ErrInvalidSignature
	}
	return nil
}

func readBounded(body io.Reader, limit int64) ([]byte, error) {
	if limit <= 0 {
		limit = defaultResponseLimit
	}
	reader := io.LimitReader(body, limit+1)
	data, err := io.ReadAll(reader)
	if err != nil {
		return nil, fmt.Errorf("read provider response: %w", err)
	}
	if int64(len(data)) > limit {
		return nil, ErrPayloadTooLarge
	}
	return data, nil
}

func checkReplay(now time.Time, timestamp time.Time, window time.Duration) error {
	if window <= 0 {
		window = defaultReplayWindow
	}
	delta := now.Sub(timestamp)
	if delta < -window || delta > window {
		return ErrReplay
	}
	return nil
}

// Both providers document merchant order/refund identifiers as 1-64 ASCII
// letters, digits, underscores, and hyphens. Restricting this at the boundary
// also keeps request-target signing unambiguous.
func validMerchantIdentifier(value string) bool {
	if len(value) == 0 || len(value) > 64 {
		return false
	}
	for _, char := range value {
		if (char >= 'a' && char <= 'z') || (char >= 'A' && char <= 'Z') || (char >= '0' && char <= '9') || char == '_' || char == '-' {
			continue
		}
		return false
	}
	return true
}

func headerValue(headers http.Header, name string) (string, error) {
	values := headers.Values(name)
	if len(values) != 1 || strings.TrimSpace(values[0]) == "" {
		return "", ErrMalformedPayload
	}
	return strings.TrimSpace(values[0]), nil
}

func secureEqualString(a, b string) bool {
	if len(a) != len(b) {
		return false
	}
	return subtle.ConstantTimeCompare([]byte(a), []byte(b)) == 1
}

func validHTTPSURL(value string) bool {
	parsed, err := url.Parse(strings.TrimSpace(value))
	return err == nil && parsed.Scheme == "https" && parsed.Host != "" && parsed.User == nil && parsed.Fragment == ""
}

func validRSAPrivateKey(key *rsa.PrivateKey) bool {
	return key != nil && key.N != nil && key.N.BitLen() >= 2048 && key.Validate() == nil
}

func validRSAPublicKey(key *rsa.PublicKey) bool {
	return key != nil && key.N != nil && key.N.Sign() > 0 && key.N.BitLen() >= 2048 && key.E >= 3 && key.E%2 == 1
}
