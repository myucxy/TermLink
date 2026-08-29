package store

import (
	"context"
	"encoding/json"
	"errors"
	"strings"
	"time"
	"unicode"
	"unicode/utf8"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

const (
	AnnouncementInfo     = "INFO"
	AnnouncementWarning  = "WARNING"
	AnnouncementCritical = "CRITICAL"

	AnnouncementDraft     = "DRAFT"
	AnnouncementPublished = "PUBLISHED"
	AnnouncementArchived  = "ARCHIVED"
)

// WebAnnouncement is the display-safe announcement model shared by the
// administrator and account portals. Body is always rendered as plain text.
type WebAnnouncement struct {
	ID          uuid.UUID
	Title       string
	Body        string
	Severity    string
	Status      string
	StartsAt    *time.Time
	EndsAt      *time.Time
	PublishedAt *time.Time
	CreatedBy   *uuid.UUID
	UpdatedBy   *uuid.UUID
	Version     int64
	CreatedAt   time.Time
	UpdatedAt   time.Time
}

type WebAnnouncementInput struct {
	Title    string
	Body     string
	Severity string
	StartsAt *time.Time
	EndsAt   *time.Time
	Reason   string
}

const webAnnouncementColumns = `id,title,body,severity,status,starts_at,ends_at,published_at,created_by,updated_by,version,created_at,updated_at`

func normalizeWebAnnouncementInput(input WebAnnouncementInput) (WebAnnouncementInput, error) {
	input.Title = strings.TrimSpace(input.Title)
	input.Body = strings.TrimSpace(input.Body)
	input.Severity = strings.ToUpper(strings.TrimSpace(input.Severity))
	input.Reason = strings.TrimSpace(input.Reason)
	if !validAnnouncementText(input.Title, 120, false) || !validAnnouncementText(input.Body, 8000, true) ||
		!validAnnouncementText(input.Reason, 500, true) || utf8.RuneCountInString(input.Reason) < 3 {
		return WebAnnouncementInput{}, ErrDenied
	}
	if input.Severity != AnnouncementInfo && input.Severity != AnnouncementWarning && input.Severity != AnnouncementCritical {
		return WebAnnouncementInput{}, ErrDenied
	}
	if input.StartsAt != nil {
		value := input.StartsAt.UTC()
		input.StartsAt = &value
	}
	if input.EndsAt != nil {
		value := input.EndsAt.UTC()
		input.EndsAt = &value
	}
	if input.StartsAt != nil && input.EndsAt != nil && !input.EndsAt.After(*input.StartsAt) {
		return WebAnnouncementInput{}, ErrDenied
	}
	return input, nil
}

func validAnnouncementText(value string, maximum int, multiline bool) bool {
	if !utf8.ValidString(value) {
		return false
	}
	length := utf8.RuneCountInString(value)
	if length < 1 || length > maximum {
		return false
	}
	for _, character := range value {
		if character == '\n' || character == '\r' || character == '\t' {
			if multiline {
				continue
			}
			return false
		}
		if unicode.IsControl(character) {
			return false
		}
	}
	return true
}

func scanWebAnnouncement(row pgx.Row) (WebAnnouncement, error) {
	var item WebAnnouncement
	err := row.Scan(
		&item.ID, &item.Title, &item.Body, &item.Severity, &item.Status,
		&item.StartsAt, &item.EndsAt, &item.PublishedAt, &item.CreatedBy, &item.UpdatedBy,
		&item.Version, &item.CreatedAt, &item.UpdatedAt,
	)
	return item, err
}

func (s *Postgres) ListActiveWebAnnouncements(ctx context.Context, limit int) ([]WebAnnouncement, error) {
	if limit < 1 || limit > 20 {
		return nil, ErrDenied
	}
	rows, err := s.Pool.Query(ctx, `
		SELECT `+webAnnouncementColumns+` FROM web_announcements
		WHERE status='PUBLISHED'
		  AND (starts_at IS NULL OR starts_at <= now())
		  AND (ends_at IS NULL OR ends_at > now())
		ORDER BY CASE severity WHEN 'CRITICAL' THEN 0 WHEN 'WARNING' THEN 1 ELSE 2 END,
		         published_at DESC,id DESC LIMIT $1`, limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	items := make([]WebAnnouncement, 0, limit)
	for rows.Next() {
		item, scanErr := scanWebAnnouncement(rows)
		if scanErr != nil {
			return nil, scanErr
		}
		items = append(items, item)
	}
	return items, rows.Err()
}

func (s *Postgres) ListAdminWebAnnouncements(ctx context.Context, actorID uuid.UUID, limit int) ([]WebAnnouncement, error) {
	if actorID == uuid.Nil || limit < 1 || limit > 200 {
		return nil, ErrDenied
	}
	var role Role
	var status string
	if err := s.Pool.QueryRow(ctx, `SELECT role,status FROM users WHERE id=$1`, actorID).Scan(&role, &status); err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return nil, ErrDenied
		}
		return nil, err
	}
	if status != "ACTIVE" || !role.CanOpenConsole() {
		return nil, ErrDenied
	}
	rows, err := s.Pool.Query(ctx, `SELECT `+webAnnouncementColumns+` FROM web_announcements ORDER BY updated_at DESC,id DESC LIMIT $1`, limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	items := make([]WebAnnouncement, 0, limit)
	for rows.Next() {
		item, scanErr := scanWebAnnouncement(rows)
		if scanErr != nil {
			return nil, scanErr
		}
		items = append(items, item)
	}
	return items, rows.Err()
}

func (s *Postgres) CreateWebAnnouncement(ctx context.Context, actorID uuid.UUID, input WebAnnouncementInput, traceID string) (WebAnnouncement, error) {
	input, err := normalizeWebAnnouncementInput(input)
	if err != nil || actorID == uuid.Nil || !validAnnouncementTraceID(traceID) {
		return WebAnnouncement{}, ErrDenied
	}
	tx, err := beginAnnouncementMutation(ctx, s, actorID)
	if err != nil {
		return WebAnnouncement{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	id := uuid.New()
	item, err := scanWebAnnouncement(tx.QueryRow(ctx, `
		INSERT INTO web_announcements(id,title,body,severity,status,starts_at,ends_at,created_by,updated_by)
		VALUES($1,$2,$3,$4,'DRAFT',$5,$6,$7,$7) RETURNING `+webAnnouncementColumns,
		id, input.Title, input.Body, input.Severity, input.StartsAt, input.EndsAt, actorID))
	if err != nil {
		return WebAnnouncement{}, err
	}
	if err = auditWebAnnouncement(ctx, tx, actorID, "announcement.create", item, traceID, input.Reason); err != nil {
		return WebAnnouncement{}, err
	}
	return item, tx.Commit(ctx)
}

func (s *Postgres) UpdateWebAnnouncement(ctx context.Context, actorID, announcementID uuid.UUID, expectedVersion int64, input WebAnnouncementInput, traceID string) (WebAnnouncement, error) {
	input, err := normalizeWebAnnouncementInput(input)
	if err != nil || actorID == uuid.Nil || announcementID == uuid.Nil || expectedVersion < 1 || !validAnnouncementTraceID(traceID) {
		return WebAnnouncement{}, ErrDenied
	}
	tx, err := beginAnnouncementMutation(ctx, s, actorID)
	if err != nil {
		return WebAnnouncement{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	item, err := scanWebAnnouncement(tx.QueryRow(ctx, `
		UPDATE web_announcements SET title=$1,body=$2,severity=$3,starts_at=$4,ends_at=$5,
		 updated_by=$6,updated_at=now(),version=version+1
		WHERE id=$7 AND version=$8 AND status <> 'ARCHIVED' RETURNING `+webAnnouncementColumns,
		input.Title, input.Body, input.Severity, input.StartsAt, input.EndsAt, actorID, announcementID, expectedVersion))
	if errors.Is(err, pgx.ErrNoRows) {
		return WebAnnouncement{}, announcementMutationMiss(ctx, tx, announcementID)
	}
	if err != nil {
		return WebAnnouncement{}, err
	}
	if err = auditWebAnnouncement(ctx, tx, actorID, "announcement.update", item, traceID, input.Reason); err != nil {
		return WebAnnouncement{}, err
	}
	return item, tx.Commit(ctx)
}

func (s *Postgres) TransitionWebAnnouncement(ctx context.Context, actorID, announcementID uuid.UUID, expectedVersion int64, targetStatus, reason, traceID string) (WebAnnouncement, error) {
	targetStatus = strings.ToUpper(strings.TrimSpace(targetStatus))
	reason = strings.TrimSpace(reason)
	if actorID == uuid.Nil || announcementID == uuid.Nil || expectedVersion < 1 ||
		(targetStatus != AnnouncementPublished && targetStatus != AnnouncementArchived) ||
		!validAnnouncementText(reason, 500, true) || utf8.RuneCountInString(reason) < 3 || !validAnnouncementTraceID(traceID) {
		return WebAnnouncement{}, ErrDenied
	}
	tx, err := beginAnnouncementMutation(ctx, s, actorID)
	if err != nil {
		return WebAnnouncement{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	item, err := scanWebAnnouncement(tx.QueryRow(ctx, `
		UPDATE web_announcements SET status=$1,
		 published_at=CASE WHEN $1='PUBLISHED' THEN now() ELSE published_at END,
		 updated_by=$2,updated_at=now(),version=version+1
		WHERE id=$3 AND version=$4 AND status <> $1 RETURNING `+webAnnouncementColumns,
		targetStatus, actorID, announcementID, expectedVersion))
	if errors.Is(err, pgx.ErrNoRows) {
		return WebAnnouncement{}, announcementMutationMiss(ctx, tx, announcementID)
	}
	if err != nil {
		return WebAnnouncement{}, err
	}
	action := "announcement.archive"
	if targetStatus == AnnouncementPublished {
		action = "announcement.publish"
	}
	if err = auditWebAnnouncement(ctx, tx, actorID, action, item, traceID, reason); err != nil {
		return WebAnnouncement{}, err
	}
	return item, tx.Commit(ctx)
}

func beginAnnouncementMutation(ctx context.Context, store *Postgres, actorID uuid.UUID) (pgx.Tx, error) {
	tx, err := store.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return nil, err
	}
	var role Role
	var status string
	if err = tx.QueryRow(ctx, `SELECT role,status FROM users WHERE id=$1 FOR UPDATE`, actorID).Scan(&role, &status); err != nil {
		_ = tx.Rollback(ctx)
		if errors.Is(err, pgx.ErrNoRows) {
			return nil, ErrDenied
		}
		return nil, err
	}
	if status != "ACTIVE" || !role.IsAdministrator() {
		_ = tx.Rollback(ctx)
		return nil, ErrDenied
	}
	return tx, nil
}

func announcementMutationMiss(ctx context.Context, tx pgx.Tx, announcementID uuid.UUID) error {
	var exists bool
	err := tx.QueryRow(ctx, `SELECT true FROM web_announcements WHERE id=$1`, announcementID).Scan(&exists)
	if errors.Is(err, pgx.ErrNoRows) {
		return ErrNotFound
	}
	if err != nil {
		return err
	}
	return ErrConflict
}

func auditWebAnnouncement(ctx context.Context, tx pgx.Tx, actorID uuid.UUID, action string, item WebAnnouncement, traceID, reason string) error {
	metadata, err := json.Marshal(map[string]any{
		"severity": item.Severity, "status": item.Status, "version": item.Version, "reason": reason,
	})
	if err != nil {
		return err
	}
	_, err = tx.Exec(ctx, `
		INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id,metadata)
		VALUES($1,$2,'announcement',$3,'SUCCEEDED',$4,$5::jsonb)`, actorID, action, item.ID, traceID, metadata)
	return err
}

func validAnnouncementTraceID(value string) bool {
	value = strings.TrimSpace(value)
	return value != "" && len(value) <= 256
}
