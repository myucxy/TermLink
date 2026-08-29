package commerce

import (
	"context"
	"errors"
	"testing"

	"github.com/termlink/termlink/server/internal/payment"
)

type certificateResolver struct {
	gateway payment.Gateway
	err     error
	calls   int
}

func (r *certificateResolver) Resolve(context.Context, string, string) (payment.Gateway, error) {
	r.calls++
	return r.gateway, r.err
}

type certificateObserver struct{ calls int }

func (o *certificateObserver) ObservePaymentCertificate(string, string, string, int, int, int, bool) {
	o.calls++
}

func TestCertificateMaintenanceIgnoresUnavailableChannel(t *testing.T) {
	resolver := &certificateResolver{err: ErrPaymentChannelUnavailable}
	refreshActiveWeChatCertificate(context.Background(), resolver, CertificateMaintenanceOptions{})
	if resolver.calls != 1 {
		t.Fatalf("resolve calls=%d", resolver.calls)
	}
}

func TestCertificateMaintenanceRejectsUnexpectedGateway(t *testing.T) {
	resolver := &certificateResolver{gateway: &fakeGateway{provider: payment.ProviderAlipay}}
	observer := &certificateObserver{}
	refreshActiveWeChatCertificate(context.Background(), resolver, CertificateMaintenanceOptions{Observer: observer})
	if observer.calls != 0 {
		t.Fatal("unexpected gateway was observed as healthy")
	}
}

func TestCertificateMaintenanceDoesNotTreatOtherResolutionFailureAsUnavailable(t *testing.T) {
	resolver := &certificateResolver{err: errors.New("provider secret must not be logged")}
	refreshActiveWeChatCertificate(context.Background(), resolver, CertificateMaintenanceOptions{})
	if resolver.calls != 1 {
		t.Fatalf("resolve calls=%d", resolver.calls)
	}
}
