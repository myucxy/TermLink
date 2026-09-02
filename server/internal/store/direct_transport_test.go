package store

import (
	"strings"
	"testing"

	"github.com/google/uuid"
)

func TestDirectTransportAuthoritySQLRetainsEverySecurityFence(t *testing.T) {
	for name, query := range map[string]string{
		"allocate": allocateDirectTransportEpochSQL,
		"recheck":  authorizeDirectTransportPairSQL,
	} {
		normalized := strings.ToLower(strings.Join(strings.Fields(query), " "))
		for _, required := range []string{
			"account.status='active'", "source.user_id=$1", "target.user_id=$1",
			"source.trust_state='trusted'", "source.revoked_at is null",
			"target.trust_state='trusted'", "target.revoked_at is null",
			"pc.owner_user_id=$1", "pc.remote_routing_enabled",
			"android_grant.expires_at>now()", "android_grant.permission in ('view','control','manage')",
			"android_grant.device_id is not null",
		} {
			if !strings.Contains(normalized, required) {
				t.Fatalf("%s direct authority SQL lost fence %q", name, required)
			}
		}
	}
	if !strings.Contains(strings.ToLower(allocateDirectTransportEpochSQL), "on conflict") ||
		!strings.Contains(strings.ToLower(allocateDirectTransportEpochSQL), "last_epoch+1") {
		t.Fatal("epoch allocation is not an atomic persistent increment")
	}
}

func TestCanonicalDirectTransportPairIsSymmetric(t *testing.T) {
	first, second := uuid.New(), uuid.New()
	a, b := canonicalDirectTransportPair(first, second)
	c, d := canonicalDirectTransportPair(second, first)
	if a != c || b != d || a == b {
		t.Fatalf("canonical pairs differ: (%s,%s) and (%s,%s)", a, b, c, d)
	}
}
