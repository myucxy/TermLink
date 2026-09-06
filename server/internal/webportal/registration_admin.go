package webportal

import (
	"context"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"errors"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
	"unicode"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

const (
	registrationPageLimit   = 100
	maxGeneratedInvitations = 200
)

type registrationAdminRepository interface {
	GetRegistrationPolicy(context.Context) (store.RegistrationPolicy, error)
	UpdateRegistrationPolicy(context.Context, uuid.UUID, string, int64, string) (store.RegistrationPolicy, error)
	CreateInvitationBatch(context.Context, uuid.UUID, store.InvitationBatchInput, string) (store.InvitationBatch, error)
	AddInvitationCodes(context.Context, uuid.UUID, uuid.UUID, []store.CodeDigestRecord, string) ([]store.InvitationCode, error)
	ListInvitationBatches(context.Context, uuid.UUID, int) ([]store.InvitationBatch, error)
	ListInvitationCodes(context.Context, uuid.UUID, uuid.UUID, int) ([]store.InvitationCode, error)
	ListInvitationRedemptions(context.Context, uuid.UUID, int) ([]store.InvitationRedemption, error)
	SetInvitationBatchStatus(context.Context, uuid.UUID, uuid.UUID, string, string) error
	SetInvitationCodeStatus(context.Context, uuid.UUID, uuid.UUID, string, string) error
}

type invitationUseView struct {
	Reference          string
	CodePrefix         string
	BatchName          string
	AccountReference   string
	Username           string
	Nickname           string
	EmailMasked        string
	HasIdentityProfile bool
	HasUser            bool
	State              string
	ReservedUntil      time.Time
	ProviderAcceptedAt *time.Time
	RegisteredAt       *time.Time
	ReleasedAt         *time.Time
	CreatedAt          time.Time
}

type registrationAdminPageData struct {
	Username               string
	Role                   store.Role
	RoleLabel              string
	CSRF                   string
	AdminBasePath          string
	Section                string
	SectionTitle           string
	CanOperate             bool
	CanConfigureSystem     bool
	RegistrationNavigation bool
	Policy                 store.RegistrationPolicy
	InvitationBatches      []store.InvitationBatch
	InvitationCodes        []store.InvitationCode
	InvitationUses         []invitationUseView
	SelectedID             uuid.UUID
	GeneratedCodes         []string
	GeneratedBatchName     string
	GeneratedAt            time.Time
	Notice                 string
	ActionNonce            string
}

func (p *Portal) adminRegistrationDashboard(w http.ResponseWriter, r *http.Request) {
	session, account, ok := p.requireConsole(w, r)
	if !ok {
		return
	}
	repository, available := p.config.Repository.(registrationAdminRepository)
	if !available {
		p.renderError(w, http.StatusServiceUnavailable, "注册管理暂不可用", "服务端尚未完成注册管理存储配置。")
		return
	}
	adminBasePath, err := p.adminBasePath(r.Context())
	if err != nil {
		p.internalError(w, err)
		return
	}
	actionNonce, err := randomText(24)
	if err != nil {
		p.internalError(w, err)
		return
	}
	data := registrationAdminPageData{
		Username: session.Username, Role: account.Role, RoleLabel: roleLabel(account.Role), CSRF: session.CSRF,
		AdminBasePath: adminBasePath,
		Section:       "registration", SectionTitle: "注册与邀请码",
		CanOperate:         account.Role == store.RoleAdmin || account.Role == store.RoleSuperAdmin,
		CanConfigureSystem: account.Role == store.RoleSuperAdmin, RegistrationNavigation: true,
		Notice: registrationAdminNotice(r.URL.Query().Get("notice")), ActionNonce: actionNonce,
	}
	data.Policy, err = repository.GetRegistrationPolicy(r.Context())
	if err == nil {
		data.InvitationBatches, err = repository.ListInvitationBatches(r.Context(), account.ID, registrationPageLimit)
	}
	if err == nil {
		data.SelectedID, data.InvitationCodes, err = selectedInvitationCodes(r, repository, account.ID)
	}
	if err == nil {
		var uses []store.InvitationRedemption
		uses, err = repository.ListInvitationRedemptions(r.Context(), account.ID, registrationPageLimit)
		data.InvitationUses = invitationUseViews(uses)
	}
	if err != nil {
		if errors.Is(err, store.ErrNotFound) || errors.Is(err, store.ErrDenied) {
			p.renderError(w, http.StatusNotFound, "邀请码批次不存在", "所选批次不存在或当前账号无权查看。")
			return
		}
		p.internalError(w, err)
		return
	}
	p.render(w, http.StatusOK, "registration-admin.html", data)
}

func selectedInvitationCodes(r *http.Request, repository registrationAdminRepository, actorID uuid.UUID) (uuid.UUID, []store.InvitationCode, error) {
	value := strings.TrimSpace(r.URL.Query().Get("id"))
	if value == "" {
		return uuid.Nil, nil, nil
	}
	id, err := uuid.Parse(value)
	if err != nil {
		return uuid.Nil, nil, store.ErrNotFound
	}
	codes, err := repository.ListInvitationCodes(r.Context(), actorID, id, registrationPageLimit)
	return id, codes, err
}

func invitationUseViews(items []store.InvitationRedemption) []invitationUseView {
	views := make([]invitationUseView, 0, len(items))
	for _, item := range items {
		accountReference := "-"
		hasUser := item.UserID != nil
		if hasUser {
			accountReference = "USR-" + strings.ToUpper(strings.ReplaceAll(item.UserID.String(), "-", "")[:12])
		}
		views = append(views, invitationUseView{
			Reference:  "REG-" + strings.ToUpper(strings.ReplaceAll(item.ID.String(), "-", "")[:12]),
			CodePrefix: item.CodePrefix, BatchName: item.BatchName, AccountReference: accountReference,
			Username: item.Username, Nickname: item.Nickname, EmailMasked: item.EmailMasked,
			HasIdentityProfile: item.ProfileSource != "", HasUser: hasUser, State: item.State,
			ReservedUntil: item.ReservedUntil, ProviderAcceptedAt: item.ProviderAcceptedAt,
			RegisteredAt: item.RedeemedAt, ReleasedAt: item.ReleasedAt, CreatedAt: item.CreatedAt,
		})
	}
	return views
}

func (p *Portal) adminUpdateRegistrationPolicy(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireSensitiveAdminAction(w, r, true)
	if !ok {
		return
	}
	repository, available := p.config.Repository.(registrationAdminRepository)
	version, err := strconv.ParseInt(strings.TrimSpace(r.Form.Get("version")), 10, 64)
	if !available || err != nil || version < 1 || validatePortalFormValues(r.Form, registrationSecurityFields("mode", "version")) != nil {
		p.renderError(w, http.StatusBadRequest, "注册策略无效", "策略模式、版本或服务配置无效。")
		return
	}
	traceID, err := randomText(12)
	if err == nil {
		_, err = repository.UpdateRegistrationPolicy(r.Context(), account.ID, r.Form.Get("mode"), version, traceID)
	}
	if err != nil {
		p.handleRegistrationMutationError(w, err)
		return
	}
	p.redirectRegistrationAdmin(w, r, "policy-updated", uuid.Nil)
}

func (p *Portal) adminCreateInvitationBatch(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireSensitiveAdminAction(w, r, false)
	if !ok {
		return
	}
	repository, available := p.config.Repository.(registrationAdminRepository)
	maxUses, maxErr := parseBoundedInt(r.Form.Get("max_uses"), 1, 1000000)
	validUntil, dateErr := parseOptionalPortalDate(r.Form.Get("valid_until"))
	name := strings.TrimSpace(r.Form.Get("name"))
	domain := strings.ToLower(strings.TrimSpace(r.Form.Get("email_domain")))
	note := strings.TrimSpace(r.Form.Get("note"))
	if !available || maxErr != nil || dateErr != nil || name == "" || len(name) > 128 || len(domain) > 255 || len(note) > 1000 ||
		validatePortalFormValues(r.Form, registrationSecurityFields("name", "max_uses", "email_domain", "valid_until", "note")) != nil {
		p.renderError(w, http.StatusBadRequest, "邀请码批次无效", "请检查批次名称、使用次数、邮箱域和有效期。")
		return
	}
	input := store.InvitationBatchInput{
		Name: name, Status: "ACTIVE", DefaultMaxUses: maxUses, EmailDomain: domain,
		ValidUntil: validUntil, Note: note,
	}
	traceID, err := randomText(12)
	var batch store.InvitationBatch
	if err == nil {
		batch, err = repository.CreateInvitationBatch(r.Context(), account.ID, input, traceID)
	}
	if err != nil {
		p.handleRegistrationMutationError(w, err)
		return
	}
	p.redirectRegistrationAdmin(w, r, "invitation-batch-created", batch.ID)
}

func (p *Portal) adminGenerateInvitationCodes(w http.ResponseWriter, r *http.Request) {
	session, account, ok := p.requireSensitiveAdminAction(w, r, false)
	if !ok {
		return
	}
	repository, available := p.config.Repository.(registrationAdminRepository)
	batchID, idErr := uuid.Parse(r.PathValue("id"))
	count, countErr := parseBoundedInt(r.Form.Get("count"), 1, maxGeneratedInvitations)
	maxUses, usesErr := parseBoundedInt(r.Form.Get("max_uses"), 1, 1000000)
	if !available || len(p.config.RegistrationCodeKey) < 32 || idErr != nil || countErr != nil || usesErr != nil ||
		validatePortalFormValues(r.Form, registrationSecurityFields("count", "max_uses")) != nil {
		p.renderError(w, http.StatusBadRequest, "邀请码生成请求无效", "批次、数量、使用次数或邀请码密钥无效。")
		return
	}
	batchName := ""
	if batches, listErr := repository.ListInvitationBatches(r.Context(), account.ID, registrationPageLimit); listErr == nil {
		for _, batch := range batches {
			if batch.ID == batchID {
				batchName = batch.Name
				break
			}
		}
	}
	raw, records, err := p.generateInvitationCodes(count, maxUses)
	traceID, traceErr := randomText(12)
	if err == nil {
		err = traceErr
	}
	if err == nil {
		_, err = repository.AddInvitationCodes(r.Context(), account.ID, batchID, records, traceID)
	}
	if err != nil {
		p.handleRegistrationMutationError(w, err)
		return
	}
	p.renderGeneratedInvitationCodes(w, r, session, account, batchID, batchName, raw)
}

func (p *Portal) adminSetInvitationBatchStatus(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireSensitiveAdminAction(w, r, false)
	if !ok {
		return
	}
	repository, available := p.config.Repository.(registrationAdminRepository)
	p.setInvitationStatus(w, r, available, func(id uuid.UUID, status, trace string) error {
		return repository.SetInvitationBatchStatus(r.Context(), account.ID, id, status, trace)
	})
}

func (p *Portal) adminSetInvitationCodeStatus(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireSensitiveAdminAction(w, r, false)
	if !ok {
		return
	}
	repository, available := p.config.Repository.(registrationAdminRepository)
	p.setInvitationStatus(w, r, available, func(id uuid.UUID, status, trace string) error {
		return repository.SetInvitationCodeStatus(r.Context(), account.ID, id, status, trace)
	})
}

func (p *Portal) setInvitationStatus(w http.ResponseWriter, r *http.Request, available bool, mutate func(uuid.UUID, string, string) error) {
	id, err := uuid.Parse(r.PathValue("id"))
	status := strings.ToUpper(strings.TrimSpace(r.Form.Get("status")))
	if !available || err != nil || (status != "ACTIVE" && status != "DISABLED") ||
		validatePortalFormValues(r.Form, registrationSecurityFields("status")) != nil {
		p.renderError(w, http.StatusBadRequest, "状态请求无效", "目标记录、状态或服务配置无效。")
		return
	}
	traceID, err := randomText(12)
	if err == nil {
		err = mutate(id, status, traceID)
	}
	if err != nil {
		p.handleRegistrationMutationError(w, err)
		return
	}
	p.redirectRegistrationAdmin(w, r, "status-updated", uuid.Nil)
}

func (p *Portal) generateInvitationCodes(count, maxUses int) ([]string, []store.CodeDigestRecord, error) {
	raw := make([]string, 0, count)
	records := make([]store.CodeDigestRecord, 0, count)
	seen := make(map[[32]byte]struct{}, count)
	for len(raw) < count {
		code, err := randomInvitationCode()
		if err != nil {
			return nil, nil, err
		}
		normalized := normalizeInvitationCode(code)
		digest := invitationCodeDigest(p.config.RegistrationCodeKey, "invitation-code", normalized)
		if _, exists := seen[digest]; exists {
			continue
		}
		seen[digest] = struct{}{}
		raw = append(raw, code)
		records = append(records, store.CodeDigestRecord{
			ID: uuid.New(), Digest: digest, Prefix: normalized[:8], MaxUses: maxUses,
		})
	}
	return raw, records, nil
}

func randomInvitationCode() (string, error) {
	const alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
	randomBytes := make([]byte, 12)
	if _, err := rand.Read(randomBytes); err != nil {
		return "", err
	}
	var display strings.Builder
	display.WriteString("TLI")
	for index, value := range randomBytes {
		if index%4 == 0 {
			display.WriteByte('-')
		}
		display.WriteByte(alphabet[int(value)%len(alphabet)])
	}
	return display.String(), nil
}

func invitationCodeDigest(key []byte, purpose, value string) [32]byte {
	mac := hmac.New(sha256.New, key)
	// Keep this historical domain so already-issued invitation codes remain valid.
	_, _ = mac.Write([]byte("TermLink commerce v1\x00"))
	_, _ = mac.Write([]byte(purpose))
	_, _ = mac.Write([]byte{0})
	_, _ = mac.Write([]byte(value))
	var result [32]byte
	copy(result[:], mac.Sum(nil))
	return result
}

func normalizeInvitationCode(value string) string {
	value = strings.ToUpper(strings.TrimSpace(value))
	var output strings.Builder
	output.Grow(len(value))
	for _, character := range value {
		if character == '-' || unicode.IsSpace(character) {
			continue
		}
		if character >= 'A' && character <= 'Z' || character >= '2' && character <= '9' {
			output.WriteRune(character)
			continue
		}
		return ""
	}
	return output.String()
}

func (p *Portal) renderGeneratedInvitationCodes(w http.ResponseWriter, r *http.Request, session webSession, account store.PortalAccount, selectedID uuid.UUID, batchName string, raw []string) {
	adminBasePath, err := p.adminBasePath(r.Context())
	if err != nil {
		p.internalError(w, err)
		return
	}
	actionNonce, err := randomText(24)
	if err != nil {
		p.internalError(w, err)
		return
	}
	p.render(w, http.StatusCreated, "registration-admin.html", registrationAdminPageData{
		Username: session.Username, Role: account.Role, RoleLabel: roleLabel(account.Role), CSRF: session.CSRF,
		AdminBasePath: adminBasePath,
		Section:       "registration", SectionTitle: "注册与邀请码",
		CanOperate:         account.Role == store.RoleAdmin || account.Role == store.RoleSuperAdmin,
		CanConfigureSystem: account.Role == store.RoleSuperAdmin, RegistrationNavigation: true,
		SelectedID: selectedID, GeneratedCodes: raw, GeneratedBatchName: batchName,
		GeneratedAt: p.now(), ActionNonce: actionNonce,
	})
}

func (p *Portal) redirectRegistrationAdmin(w http.ResponseWriter, r *http.Request, notice string, id uuid.UUID) {
	adminBasePath, err := p.adminBasePath(r.Context())
	if err != nil {
		p.internalError(w, err)
		return
	}
	query := url.Values{"notice": {notice}}
	if id != uuid.Nil {
		query.Set("id", id.String())
	}
	http.Redirect(w, r, adminBasePath+"/registration?"+query.Encode(), http.StatusSeeOther)
}

func (p *Portal) handleRegistrationMutationError(w http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, store.ErrDenied):
		p.renderError(w, http.StatusForbidden, "操作被拒绝", "当前角色或记录状态不允许该操作。")
	case errors.Is(err, store.ErrNotFound):
		p.renderError(w, http.StatusNotFound, "记录不存在", "目标记录不存在或当前账号无权访问。")
	case errors.Is(err, store.ErrConflict):
		p.renderError(w, http.StatusConflict, "状态已变化", "其他管理员已更新该记录，请刷新后重试。")
	default:
		p.internalError(w, err)
	}
}

func parseBoundedInt(value string, minimum, maximum int) (int, error) {
	parsed, err := strconv.Atoi(strings.TrimSpace(value))
	if err != nil || parsed < minimum || parsed > maximum {
		return 0, errors.New("integer is outside allowed range")
	}
	return parsed, nil
}

func parseOptionalPortalDate(value string) (*time.Time, error) {
	value = strings.TrimSpace(value)
	if value == "" {
		return nil, nil
	}
	parsed, err := time.Parse("2006-01-02", value)
	if err != nil {
		return nil, err
	}
	parsed = parsed.UTC().Add(24*time.Hour - time.Nanosecond)
	return &parsed, nil
}

func registrationSecurityFields(names ...string) map[string]bool {
	fields := map[string]bool{"csrf": true, "idempotency_key": true, "reason": true}
	for _, name := range names {
		fields[name] = true
	}
	return fields
}

func registrationAdminNotice(value string) string {
	return map[string]string{
		"policy-updated":           bilingualPortalNotice("注册策略已更新并写入操作记录。", "The registration policy was updated and recorded."),
		"invitation-batch-created": bilingualPortalNotice("邀请码批次已创建。", "The invitation-code batch was created."),
		"status-updated":           bilingualPortalNotice("邀请码状态已更新并写入操作记录。", "The invitation status was updated and recorded."),
	}[value]
}
