package realip

import (
	"net/http/httptest"
	"testing"
)

func TestExtract_DirectConnection(t *testing.T) {
	ex := New(nil) // no trusted proxies
	req := httptest.NewRequest("GET", "/", nil)
	req.RemoteAddr = "1.2.3.4:12345"
	req.Header.Set("X-Forwarded-For", "8.8.8.8") // should be ignored (no trusted proxies)

	got := ex.Extract(req)
	if got != "1.2.3.4" {
		t.Errorf("Extract()=%q want 1.2.3.4", got)
	}
}

func TestExtract_TrustedExactIP_UsesXRealIP(t *testing.T) {
	ex := New([]string{"10.0.0.1"})
	req := httptest.NewRequest("GET", "/", nil)
	req.RemoteAddr = "10.0.0.1:54321"
	req.Header.Set("X-Real-IP", "203.0.113.5")
	req.Header.Set("X-Forwarded-For", "198.51.100.2")

	got := ex.Extract(req)
	if got != "203.0.113.5" {
		t.Errorf("X-Real-IP должен иметь приоритет: got %q want 203.0.113.5", got)
	}
}

func TestExtract_TrustedCIDR_UsesXFF(t *testing.T) {
	ex := New([]string{"172.16.0.0/12"})
	req := httptest.NewRequest("GET", "/", nil)
	req.RemoteAddr = "172.18.0.4:8000"
	req.Header.Set("X-Forwarded-For", "77.88.8.8")

	got := ex.Extract(req)
	if got != "77.88.8.8" {
		t.Errorf("Extract()=%q want 77.88.8.8 (XFF from CIDR-trusted proxy)", got)
	}
}

func TestExtract_XFF_ChainTakesFirst(t *testing.T) {
	ex := New([]string{"172.16.0.0/12"})
	req := httptest.NewRequest("GET", "/", nil)
	req.RemoteAddr = "172.18.0.4:8000"
	req.Header.Set("X-Forwarded-For", "1.1.1.1, 2.2.2.2, 3.3.3.3")

	got := ex.Extract(req)
	if got != "1.1.1.1" {
		t.Errorf("Extract()=%q want 1.1.1.1 (first in XFF chain = original client)", got)
	}
}

func TestExtract_UntrustedSource_IgnoresXFF(t *testing.T) {
	ex := New([]string{"10.0.0.1"})
	req := httptest.NewRequest("GET", "/", nil)
	req.RemoteAddr = "99.99.99.99:12345"
	req.Header.Set("X-Forwarded-For", "1.1.1.1")

	got := ex.Extract(req)
	if got != "99.99.99.99" {
		t.Errorf("Extract()=%q want 99.99.99.99 (XFF from untrusted source must be ignored)", got)
	}
}

func TestExtract_EmptyTrustedProxies_AlwaysDirect(t *testing.T) {
	ex := New([]string{})
	req := httptest.NewRequest("GET", "/", nil)
	req.RemoteAddr = "172.18.0.4:8000"
	req.Header.Set("X-Forwarded-For", "1.1.1.1")

	got := ex.Extract(req)
	if got != "172.18.0.4" {
		t.Errorf("Extract()=%q want 172.18.0.4 (empty trustedProxies always uses direct)", got)
	}
}

func TestExtract_InvalidCIDR_SkipsSilently(t *testing.T) {
	ex := New([]string{"not-a-cidr", "garbage", "1.2.3.4", "172.16.0.0/99"})
	req := httptest.NewRequest("GET", "/", nil)
	req.RemoteAddr = "1.2.3.4:12345"
	req.Header.Set("X-Real-IP", "5.6.7.8")

	got := ex.Extract(req)
	if got != "5.6.7.8" {
		t.Errorf("Extract()=%q want 5.6.7.8 (exact IP match in trustedProxies)", got)
	}
}

func TestExtract_IPv6Support(t *testing.T) {
	ex := New([]string{"fd00::/8"})
	req := httptest.NewRequest("GET", "/", nil)
	req.RemoteAddr = "[fd12:3456::1]:8000"
	req.Header.Set("X-Real-IP", "2001:db8::42")

	got := ex.Extract(req)
	if got != "2001:db8::42" {
		t.Errorf("Extract()=%q want 2001:db8::42 (IPv6 XFF through trusted ULA)", got)
	}
}

func TestExtract_MalformedRemoteAddr_FallsBack(t *testing.T) {
	ex := New([]string{"172.16.0.0/12"})
	req := httptest.NewRequest("GET", "/", nil)
	req.RemoteAddr = "malformed"
	got := ex.Extract(req)
	if got != "malformed" {
		t.Errorf("Extract()=%q want 'malformed' (fallback, no panic)", got)
	}
}
