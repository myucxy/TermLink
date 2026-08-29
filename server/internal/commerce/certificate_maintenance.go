package commerce

import (
	"context"
	"errors"
	"log/slog"
	"time"

	"github.com/termlink/termlink/server/internal/payment"
)

const (
	DefaultCertificateRefreshInterval = 12 * time.Hour
	DefaultCertificateRefreshTimeout  = 30 * time.Second
)

type CertificateHealthObserver interface {
	ObservePaymentCertificate(provider, scene, status string, remainingDays, active, retiring int, refreshOK bool)
}

type CertificateMaintenanceOptions struct {
	Interval time.Duration
	Timeout  time.Duration
	Logger   *slog.Logger
	Observer CertificateHealthObserver
}

// RunCertificateMaintenance keeps the active WeChat platform trust set fresh.
// It resolves the currently active version on every pass, so a configuration
// rotation automatically stops refreshing the old cached gateway. Disabled or
// unconfigured channels are expected and do not produce an operational error.
func RunCertificateMaintenance(ctx context.Context, resolver GatewayResolver, options CertificateMaintenanceOptions) {
	if resolver == nil {
		return
	}
	if options.Interval < time.Minute {
		options.Interval = DefaultCertificateRefreshInterval
	}
	if options.Timeout <= 0 || options.Timeout > 2*time.Minute {
		options.Timeout = DefaultCertificateRefreshTimeout
	}
	if options.Logger == nil {
		options.Logger = slog.Default()
	}
	refreshActiveWeChatCertificate(ctx, resolver, options)
	ticker := time.NewTicker(options.Interval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			refreshActiveWeChatCertificate(ctx, resolver, options)
		}
	}
}

func refreshActiveWeChatCertificate(ctx context.Context, resolver GatewayResolver, options CertificateMaintenanceOptions) {
	const scene = string(payment.FlowNative)
	logger := options.Logger
	if logger == nil {
		logger = slog.Default()
	}
	gateway, err := resolver.Resolve(ctx, string(payment.ProviderWeChatPay), scene)
	if err != nil {
		if !errors.Is(err, ErrPaymentChannelUnavailable) && ctx.Err() == nil {
			logger.Warn("payment certificate maintenance could not resolve active channel", "error_class", "PAYMENT_CERTIFICATE_CHANNEL_RESOLVE_FAILED", "provider", payment.ProviderWeChatPay, "scene", scene)
		}
		return
	}
	weChat, ok := gateway.(*payment.WeChatGateway)
	if !ok {
		logger.Error("payment certificate maintenance resolved unexpected gateway", "error_class", "PAYMENT_CERTIFICATE_GATEWAY_INVALID", "provider", payment.ProviderWeChatPay, "scene", scene)
		return
	}
	refreshContext, cancel := context.WithTimeout(ctx, options.Timeout)
	health, refreshErr := weChat.RefreshPlatformCertificates(refreshContext)
	cancel()
	refreshOK := refreshErr == nil
	if options.Observer != nil {
		options.Observer.ObservePaymentCertificate(string(payment.ProviderWeChatPay), scene, health.Status, health.RemainingDays, health.ActiveCertificates, health.RetiringCertificates, refreshOK)
	}
	if refreshErr != nil && ctx.Err() == nil {
		logger.Warn("payment certificate refresh failed", "error_class", "PAYMENT_CERTIFICATE_REFRESH_FAILED", "provider", payment.ProviderWeChatPay, "scene", scene)
	}
}
