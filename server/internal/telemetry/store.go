package telemetry

import (
	"context"
	"database/sql"
	"time"

	_ "modernc.org/sqlite"
)

// Device represents a single client installation.
type Device struct {
	MachineID   string `json:"machine_id"`
	FirstSeen   string `json:"first_seen"`
	LastSeen    string `json:"last_seen"`
	OSVersion   string `json:"os_version"`
	OSLanguage  string `json:"os_language"`
	AppVersion  string `json:"app_version"`
	Country     string `json:"country"`
	IP          string `json:"ip"`
	Platform    string `json:"platform"`     // "windows" | "android" (empty = legacy Windows)
	DeviceModel string `json:"device_model"` // e.g. "Pixel 8 Pro" — only Android sends this
}

// DistEntry represents a label+count pair for distribution charts.
type DistEntry struct {
	Label string `json:"label"`
	Count int    `json:"count"`
}

// Store manages telemetry data in SQLite.
type Store struct {
	db *sql.DB
}

// NewStore opens (or creates) the SQLite database at path.
func NewStore(path string) (*Store, error) {
	db, err := sql.Open("sqlite", path+"?_pragma=journal_mode(wal)")
	if err != nil {
		return nil, err
	}
	if err := db.Ping(); err != nil {
		db.Close()
		return nil, err
	}
	if err := migrate(db); err != nil {
		db.Close()
		return nil, err
	}
	return &Store{db: db}, nil
}

func migrate(db *sql.DB) error {
	// Base schema (idempotent)
	if _, err := db.Exec(`
		CREATE TABLE IF NOT EXISTS devices (
			machine_id   TEXT PRIMARY KEY,
			first_seen   DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
			last_seen    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
			os_version   TEXT NOT NULL DEFAULT '',
			os_language  TEXT NOT NULL DEFAULT '',
			app_version  TEXT NOT NULL DEFAULT '',
			country      TEXT NOT NULL DEFAULT '',
			ip           TEXT NOT NULL DEFAULT ''
		);
		CREATE INDEX IF NOT EXISTS idx_devices_last_seen ON devices(last_seen);
	`); err != nil {
		return err
	}
	// Additive columns — ALTER errors ignored if column already exists.
	// Existing Windows devices keep platform='' which is treated as Windows for backward compat.
	_, _ = db.Exec(`ALTER TABLE devices ADD COLUMN platform     TEXT NOT NULL DEFAULT ''`)
	_, _ = db.Exec(`ALTER TABLE devices ADD COLUMN device_model TEXT NOT NULL DEFAULT ''`)
	_, _ = db.Exec(`CREATE INDEX IF NOT EXISTS idx_devices_platform ON devices(platform)`)
	return nil
}

// Close closes the database.
func (s *Store) Close() error {
	return s.db.Close()
}

// Heartbeat inserts or updates a device record.
// platform and deviceModel are optional (empty = legacy Windows client).
func (s *Store) Heartbeat(ctx context.Context, machineID, osVersion, osLanguage, appVersion, country, ip, platform, deviceModel string) error {
	_, err := s.db.ExecContext(ctx, `
		INSERT INTO devices (machine_id, first_seen, last_seen, os_version, os_language, app_version, country, ip, platform, device_model)
		VALUES (?, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, ?, ?, ?, ?, ?, ?, ?)
		ON CONFLICT(machine_id) DO UPDATE SET
			last_seen    = CURRENT_TIMESTAMP,
			os_version   = excluded.os_version,
			os_language  = excluded.os_language,
			app_version  = excluded.app_version,
			country      = CASE WHEN excluded.country != '' THEN excluded.country ELSE devices.country END,
			ip           = excluded.ip,
			platform     = CASE WHEN excluded.platform != '' THEN excluded.platform ELSE devices.platform END,
			device_model = CASE WHEN excluded.device_model != '' THEN excluded.device_model ELSE devices.device_model END
	`, machineID, osVersion, osLanguage, appVersion, country, ip, platform, deviceModel)
	return err
}

// platformFilterClause returns a SQL WHERE snippet and args to filter by platform.
// "windows" matches both "windows" and "" (legacy records before platform was introduced).
// "android" matches only "android". "" (empty filter) returns no filter.
func platformFilterClause(platform string) (string, []interface{}) {
	switch platform {
	case "windows":
		return " WHERE (platform = 'windows' OR platform = '')", nil
	case "android":
		return " WHERE platform = 'android'", nil
	default:
		return "", nil
	}
}

// OnlineCount returns devices seen within the last 10 minutes.
// Optional platform filter: "windows", "android", or "" for all.
func (s *Store) OnlineCount(ctx context.Context, platform string) (int, error) {
	filter, _ := platformFilterClause(platform)
	// Combine with time filter
	query := `SELECT COUNT(*) FROM devices`
	if filter != "" {
		query += filter + ` AND last_seen > datetime('now', '-10 minutes')`
	} else {
		query += ` WHERE last_seen > datetime('now', '-10 minutes')`
	}
	var count int
	err := s.db.QueryRowContext(ctx, query).Scan(&count)
	return count, err
}

// TotalInstalled returns total unique device count (optionally filtered by platform).
func (s *Store) TotalInstalled(ctx context.Context, platform string) (int, error) {
	filter, _ := platformFilterClause(platform)
	var count int
	err := s.db.QueryRowContext(ctx, `SELECT COUNT(*) FROM devices`+filter).Scan(&count)
	return count, err
}

// OSDistribution returns device counts grouped by OS version (optionally filtered by platform).
func (s *Store) OSDistribution(ctx context.Context, platform string) ([]DistEntry, error) {
	return s.distribution(ctx, "os_version", platform)
}

// LanguageDistribution returns device counts grouped by UI language.
func (s *Store) LanguageDistribution(ctx context.Context, platform string) ([]DistEntry, error) {
	return s.distribution(ctx, "os_language", platform)
}

// CountryDistribution returns device counts grouped by country code.
func (s *Store) CountryDistribution(ctx context.Context, platform string) ([]DistEntry, error) {
	return s.distribution(ctx, "country", platform)
}

// VersionDistribution returns device counts grouped by app version.
func (s *Store) VersionDistribution(ctx context.Context, platform string) ([]DistEntry, error) {
	return s.distribution(ctx, "app_version", platform)
}

// DeviceModelDistribution returns Android device models (only Android has this field populated).
func (s *Store) DeviceModelDistribution(ctx context.Context, platform string) ([]DistEntry, error) {
	return s.distribution(ctx, "device_model", platform)
}

func (s *Store) distribution(ctx context.Context, column, platform string) ([]DistEntry, error) {
	filter, _ := platformFilterClause(platform)
	query := `SELECT COALESCE(NULLIF(` + column + `, ''), 'Unknown'), COUNT(*) FROM devices` +
		filter + ` GROUP BY ` + column + ` ORDER BY COUNT(*) DESC LIMIT 50`
	rows, err := s.db.QueryContext(ctx, query)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var result []DistEntry
	for rows.Next() {
		var e DistEntry
		if err := rows.Scan(&e.Label, &e.Count); err != nil {
			return nil, err
		}
		result = append(result, e)
	}
	return result, rows.Err()
}

// RecentDevices returns the most recently seen devices (optionally filtered by platform).
func (s *Store) RecentDevices(ctx context.Context, limit int, platform string) ([]Device, error) {
	filter, _ := platformFilterClause(platform)
	query := `SELECT machine_id, first_seen, last_seen, os_version, os_language, app_version, country, ip, platform, device_model FROM devices` +
		filter + ` ORDER BY last_seen DESC LIMIT ?`
	rows, err := s.db.QueryContext(ctx, query, limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var result []Device
	for rows.Next() {
		var d Device
		if err := rows.Scan(&d.MachineID, &d.FirstSeen, &d.LastSeen, &d.OSVersion, &d.OSLanguage, &d.AppVersion, &d.Country, &d.IP, &d.Platform, &d.DeviceModel); err != nil {
			return nil, err
		}
		result = append(result, d)
	}
	return result, rows.Err()
}

// OnlineSince returns the time threshold for "online" status.
func OnlineSince() time.Time {
	return time.Now().UTC().Add(-10 * time.Minute)
}
