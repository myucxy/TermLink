package store

import (
	"strings"
	"testing"
	"time"
)

func TestNormalizeWebAnnouncementInputAcceptsBoundedPlainText(t *testing.T) {
	startsAt := time.Date(2026, 8, 28, 1, 0, 0, 0, time.FixedZone("CST", 8*60*60))
	endsAt := startsAt.Add(2 * time.Hour)
	input, err := normalizeWebAnnouncementInput(WebAnnouncementInput{
		Title: " 维护通知 ", Body: "第一行\n第二行", Severity: " warning ",
		StartsAt: &startsAt, EndsAt: &endsAt, Reason: " 例行维护窗口 ",
	})
	if err != nil {
		t.Fatal(err)
	}
	if input.Title != "维护通知" || input.Body != "第一行\n第二行" || input.Severity != AnnouncementWarning ||
		input.Reason != "例行维护窗口" || input.StartsAt.Location() != time.UTC || input.EndsAt.Location() != time.UTC {
		t.Fatalf("normalized announcement=%+v", input)
	}
}

func TestNormalizeWebAnnouncementInputRejectsUnsafeOrAmbiguousContent(t *testing.T) {
	now := time.Now().UTC()
	past := now.Add(-time.Minute)
	for name, input := range map[string]WebAnnouncementInput{
		"empty title":      {Body: "正文", Severity: AnnouncementInfo, Reason: "创建公告"},
		"title control":    {Title: "标题\n伪造", Body: "正文", Severity: AnnouncementInfo, Reason: "创建公告"},
		"body control":     {Title: "标题", Body: "正文\x00", Severity: AnnouncementInfo, Reason: "创建公告"},
		"body too long":    {Title: "标题", Body: strings.Repeat("好", 8001), Severity: AnnouncementInfo, Reason: "创建公告"},
		"unknown severity": {Title: "标题", Body: "正文", Severity: "EMERGENCY", Reason: "创建公告"},
		"short reason":     {Title: "标题", Body: "正文", Severity: AnnouncementInfo, Reason: "短"},
		"reversed window":  {Title: "标题", Body: "正文", Severity: AnnouncementInfo, Reason: "创建公告", StartsAt: &now, EndsAt: &past},
	} {
		t.Run(name, func(t *testing.T) {
			if _, err := normalizeWebAnnouncementInput(input); err == nil {
				t.Fatal("invalid announcement input was accepted")
			}
		})
	}
}
