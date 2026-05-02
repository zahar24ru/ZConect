// Package turn генерирует short-term TURN credentials согласно RFC 7635
// и coturn's --use-auth-secret mode.
//
// Проблема которую решаем: static TURN username/password хранится в client config
// и на сервере (.env). Если leak'нется — attacker может бесконечно использовать
// наш coturn как anonymous relay до next manual password change.
//
// Решение: signaling server генерирует credentials с expiration timestamp и
// подписывает их HMAC-SHA1 от shared secret. Coturn (который знает тот же
// secret) валидирует credential при каждой TURN auth attempt'е.
//
// Формат:
//
//	username   = "<expiration_unix_ts>:<optional_session_id>"
//	credential = base64(HMAC-SHA1(shared_secret, username))
//
// Coturn config:
//
//	--use-auth-secret
//	--static-auth-secret=<same_secret_as_signaling>
//
// Coturn при TURN Binding Request:
//  1. Парсит username → expiration_ts
//  2. Проверяет expiration_ts > now() (иначе 401)
//  3. Вычисляет HMAC-SHA1(secret, username) и сравнивает с присланным credential
//  4. OK → выдаёт client'у relay slot
//
// Spec: https://datatracker.ietf.org/doc/html/draft-uberti-behave-turn-rest-00
// (ранний draft, но coturn реализует именно его; RFC 7635 — более новый OAuth-based).
package turn

import (
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha1"
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"strconv"
	"strings"
	"time"
)

// CredentialTTL — максимум сколько credential действителен.
// 30 минут — хороший баланс между security и UX:
//   - Leaked credentials бесполезны через <30 мин
//   - Клиенту достаточно редко делать refresh (один раз в полчаса)
//   - WebRTC session обычно короче 30 мин; если длиннее — client делает
//     session refresh и получает новый turn_servers block.
const DefaultCredentialTTL = 30 * time.Minute

// MinCredentialTTL — минимум допустимое TTL. Меньше — слишком частый refresh.
const MinCredentialTTL = 5 * time.Minute

// MaxCredentialTTL — максимум. Больше — security risk от leaked creds.
const MaxCredentialTTL = 24 * time.Hour

// Credential — generated TURN auth pair для передачи клиенту.
type Credential struct {
	// Username формата "<exp_unix_ts>:<session_id>". Coturn извлекает expiry.
	Username string `json:"username"`
	// Credential — base64(HMAC-SHA1(secret, username)). Coturn recomputes и сравнивает.
	Credential string `json:"credential"`
	// ExpiresAtUnix — абсолютный Unix timestamp когда credential перестанет работать.
	// Клиент должен сделать refresh сессии до этого момента.
	ExpiresAtUnix int64 `json:"expires_at_unix"`
	// TTLSeconds — relative duration для клиентского scheduling (refresh timer).
	TTLSeconds int `json:"ttl_seconds"`
}

// Generate создаёт short-term TURN credentials.
//
//	secret:    shared static secret, известный signaling server'у и coturn'у
//	sessionID: optional identifier для audit (coturn не использует, logs показывают)
//	ttl:       сколько credential будет валиден
//
// Возвращает готовую Credential которую можно сериализовать в JSON и отдать клиенту.
//
// Если secret пустой — возвращается zero-value Credential и no error (HMAC не
// применяется; вызывающий должен проверить Username == "" чтобы понять что
// rotating TURN отключён и использовать static credentials).
func Generate(secret, sessionID string, ttl time.Duration) Credential {
	if secret == "" {
		return Credential{}
	}
	if ttl < MinCredentialTTL {
		ttl = MinCredentialTTL
	}
	if ttl > MaxCredentialTTL {
		ttl = MaxCredentialTTL
	}

	expiresAt := time.Now().Add(ttl).Unix()
	// Санитизируем sessionID — не должен содержать ':' (это наш delimiter).
	// Достаточно заменить на '-' (session IDs у нас UUID-like, ':' не используется).
	safeID := strings.ReplaceAll(sessionID, ":", "-")
	username := fmt.Sprintf("%d:%s", expiresAt, safeID)

	mac := hmac.New(sha1.New, []byte(secret))
	_, _ = mac.Write([]byte(username))
	credential := base64.StdEncoding.EncodeToString(mac.Sum(nil))

	return Credential{
		Username:      username,
		Credential:    credential,
		ExpiresAtUnix: expiresAt,
		TTLSeconds:    int(ttl.Seconds()),
	}
}

// Verify проверяет что credential валиден и не expired. Используется в тестах
// и для server-side verification (например, admin panel debug). Coturn делает
// то же самое внутри, нам не нужно звать Verify в runtime API path.
func Verify(secret, username, credential string) bool {
	if secret == "" || username == "" || credential == "" {
		return false
	}
	// Парсим expiration
	colonIdx := strings.Index(username, ":")
	if colonIdx < 0 {
		return false
	}
	expStr := username[:colonIdx]
	expTs, err := strconv.ParseInt(expStr, 10, 64)
	if err != nil {
		return false
	}
	if time.Now().Unix() > expTs {
		return false // expired
	}

	// Recompute HMAC, compare constant-time
	mac := hmac.New(sha1.New, []byte(secret))
	_, _ = mac.Write([]byte(username))
	expected := base64.StdEncoding.EncodeToString(mac.Sum(nil))
	return hmac.Equal([]byte(credential), []byte(expected))
}

// GenerateSecret создаёт random 32-byte secret в hex encoding (64 hex chars).
// Используется deploy script'ом для auto-generation если admin не передал
// -TurnAuthSecret явно. Также CLI tool может его использовать для manual rotation.
//
// Зачем hex (а не base64): hex safe для bash/docker-compose environment variables
// (нет padding '=' и особых символов которые могут quote-escape broken).
func GenerateSecret() (string, error) {
	b := make([]byte, 32)
	if _, err := rand.Read(b); err != nil {
		return "", fmt.Errorf("rand.Read: %w", err)
	}
	return hex.EncodeToString(b), nil
}
