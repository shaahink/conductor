package tui

import (
	"fmt"
	"strconv"
	"strings"

	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
)

// M6.3: the plan editor. Edit stages, gates, models, workflows, and settings live from the TUI, and
// import/re-import a structured plan doc with a diff — no hand-editing JSON, no spinning up an agent
// to tweak a workflow. Every change round-trips through POST /plan/edit or /plan/import.

type planFieldKind int

const (
	fieldText planFieldKind = iota
	fieldInt
	fieldEnum
)

type planField struct {
	Label   string
	Field   string
	Kind    planFieldKind
	Options []string // enum only
	Custom  bool     // enum also accepts a free-text value via the "✎ custom…" option
}

// Curated model choices for the picker; "(agent default)" clears the per-stage override, and the
// "✎ custom…" sentinel (appended for any Custom field) drops into free-text so an arbitrary model id
// is reachable — the plan schema allows any string, not just these five.
var modelChoices = []string{"claude-opus-4-8", "claude-sonnet-5", "claude-haiku-4-5", "deepseek-v4-pro", "(agent default)"}

// The persona vocabulary the engine ships (StageConfig.Persona); "(none)" clears it.
var personaChoices = []string{"(none)", "architect", "planner", "qa", "docs", "reviewer", "refactor", "test-writer", "git-cleanup", "security-audit"}

const (
	agentDefaultModel = "(agent default)"
	noneValue         = "(none)"
	customSentinel    = "✎ custom…"
)

func (m Model) stageFields() []planField {
	return []planField{
		{Label: "title", Field: "title", Kind: fieldText},
		{Label: "model", Field: "model", Kind: fieldEnum, Options: m.modelChoices(), Custom: true},
		{Label: "persona", Field: "persona", Kind: fieldEnum, Options: personaChoices},
		{Label: "workflow", Field: "workflow", Kind: fieldEnum, Options: m.workflowChoices()},
		{Label: "kind", Field: "kind", Kind: fieldEnum, Options: []string{"deliver", "review"}},
		{Label: "sessions", Field: "sessions", Kind: fieldInt},
		{Label: "notes", Field: "notes", Kind: fieldText},
		{Label: "dependsOn", Field: "dependson", Kind: fieldText},
	}
}

// modelChoices is the curated list plus the plan's own defaultModel (so it's never absent from the
// picker), de-duplicated, in a stable order.
func (m Model) modelChoices() []string {
	if m.plan == nil || m.plan.DefaultModel == "" {
		return modelChoices
	}
	for _, c := range modelChoices {
		if c == m.plan.DefaultModel {
			return modelChoices
		}
	}
	out := append([]string{m.plan.DefaultModel}, modelChoices...)
	return out
}

// optionList returns a field's enum options plus the "✎ custom…" sentinel when it accepts free text.
func optionList(f planField) []string {
	if f.Custom {
		return append(append([]string{}, f.Options...), customSentinel)
	}
	return f.Options
}

func gateFields() []planField {
	return []planField{
		{Label: "command", Field: "command", Kind: fieldText},
		{Label: "tier", Field: "tier", Kind: fieldEnum, Options: []string{"fast", "full", "truth"}},
		{Label: "timeout (min)", Field: "timeout", Kind: fieldInt},
		{Label: "optional", Field: "optional", Kind: fieldEnum, Options: []string{"false", "true"}},
	}
}

func (m Model) settingsFields() []planField {
	return []planField{
		{Label: "name", Field: "name", Kind: fieldText},
		{Label: "gatePolicy", Field: "gatepolicy", Kind: fieldEnum, Options: []string{"perSession", "perPhase"}},
		{Label: "defaultWorkflow", Field: "defaultworkflow", Kind: fieldEnum, Options: m.workflowChoices()},
	}
}

func (m Model) workflowChoices() []string {
	if m.plan != nil && len(m.plan.Workflows) > 0 {
		return m.plan.Workflows
	}
	return []string{"deliver-verify", "big-dev-then-big-audit", "docs-only", "spike"}
}

func planVersionOf(r *api.PlanMutationResultDto) int {
	if r == nil {
		return 0
	}
	return r.PlanVersion
}

func (m *Model) handlePlanKey(key string) (tea.Model, tea.Cmd) {
	if m.planEditing {
		return m.handlePlanFieldEdit(key)
	}
	if m.planTab == planTabImport {
		return m.handlePlanImportKey(key)
	}

	switch key {
	case "esc":
		if m.planDrill {
			m.planDrill = false
			return m, nil
		}
		return m.openTab(TabAgent) // leave the Plan tab
	case "right":
		if !m.planDrill {
			m.planTab = (m.planTab + 1) % 4
			m.planFieldIdx = 0
			m.planStatus = ""
		}
		return m, nil
	case "left":
		if !m.planDrill {
			m.planTab = (m.planTab + 3) % 4
			m.planFieldIdx = 0
			m.planStatus = ""
		}
		return m, nil
	case "up", "k":
		m.planMoveSelection(-1)
		return m, nil
	case "down", "j":
		m.planMoveSelection(1)
		return m, nil
	case "enter":
		return m.planEnter()
	}
	return m, nil
}

func (m *Model) planMoveSelection(delta int) {
	switch {
	case m.planTab == planTabSettings:
		m.planFieldIdx = clamp(m.planFieldIdx+delta, 0, len(m.settingsFields())-1)
	case m.planDrill && m.planTab == planTabStages:
		m.planFieldIdx = clamp(m.planFieldIdx+delta, 0, len(m.stageFields())-1)
	case m.planDrill && m.planTab == planTabGates:
		m.planFieldIdx = clamp(m.planFieldIdx+delta, 0, len(gateFields())-1)
	case m.planTab == planTabStages && m.plan != nil:
		m.planStageIdx = clamp(m.planStageIdx+delta, 0, len(m.plan.Stages)-1)
	case m.planTab == planTabGates && m.plan != nil:
		m.planGateIdx = clamp(m.planGateIdx+delta, 0, len(m.plan.Gates)-1)
	}
}

// planEnter drills into a row's fields, or begins editing the selected field.
func (m *Model) planEnter() (tea.Model, tea.Cmd) {
	if m.plan == nil {
		return m, nil
	}
	switch {
	case m.planTab == planTabSettings:
		m.beginFieldEdit(m.settingsFields()[m.planFieldIdx])
	case m.planTab == planTabStages && !m.planDrill:
		if len(m.plan.Stages) > 0 {
			m.planDrill = true
			m.planFieldIdx = 0
			m.planStatus = ""
		}
	case m.planTab == planTabGates && !m.planDrill:
		if len(m.plan.Gates) > 0 {
			m.planDrill = true
			m.planFieldIdx = 0
			m.planStatus = ""
		}
	case m.planDrill && m.planTab == planTabStages:
		m.beginFieldEdit(m.stageFields()[m.planFieldIdx])
	case m.planDrill && m.planTab == planTabGates:
		m.beginFieldEdit(gateFields()[m.planFieldIdx])
	}
	return m, nil
}

func (m *Model) beginFieldEdit(f planField) {
	cur := m.currentFieldValue(f.Field)
	m.planEditing = true
	m.planEnumCustom = false
	m.planStatus = ""
	if f.Kind == fieldEnum {
		m.planEnumIdx = indexOfDefault(optionList(f), cur, 0)
		m.planEditBuf = ""
	} else {
		m.planEditBuf = cur
	}
}

func (m *Model) handlePlanFieldEdit(key string) (tea.Model, tea.Cmd) {
	f := m.currentField()

	// Free-text sub-entry, reached by picking an enum's "✎ custom…" option.
	if m.planEnumCustom {
		switch key {
		case "esc":
			m.planEnumCustom = false // back to the carousel, still editing
		case "enter":
			m.planEnumCustom = false
			return m.savePlanFieldValue(f, strings.TrimSpace(m.planEditBuf))
		case "backspace":
			if len(m.planEditBuf) > 0 {
				m.planEditBuf = m.planEditBuf[:len(m.planEditBuf)-1]
			}
		default:
			if ch, ok := typedChar(key); ok {
				m.planEditBuf += ch
			}
		}
		return m, nil
	}

	switch key {
	case "esc":
		m.planEditing = false
		return m, nil
	case "enter":
		if f.Kind == fieldEnum {
			opts := optionList(f)
			if m.planEnumIdx < len(opts) && opts[m.planEnumIdx] == customSentinel {
				m.planEnumCustom, m.planEditBuf = true, "" // drop into free-text entry
				return m, nil
			}
		}
		return m.savePlanField(f)
	}

	if f.Kind == fieldEnum {
		opts := optionList(f)
		switch key {
		case "left", "h":
			m.planEnumIdx = (m.planEnumIdx - 1 + len(opts)) % len(opts)
		case "right", "l", "space":
			m.planEnumIdx = (m.planEnumIdx + 1) % len(opts)
		}
		return m, nil
	}

	// text / int
	switch key {
	case "backspace":
		if len(m.planEditBuf) > 0 {
			m.planEditBuf = m.planEditBuf[:len(m.planEditBuf)-1]
		}
	default:
		if ch, ok := typedChar(key); ok {
			if f.Kind == fieldInt && (ch < "0" || ch > "9") {
				return m, nil // ints accept digits only
			}
			m.planEditBuf += ch
		}
	}
	return m, nil
}

func (m *Model) savePlanField(f planField) (tea.Model, tea.Cmd) {
	value := m.planEditBuf
	if f.Kind == fieldEnum {
		if opts := optionList(f); m.planEnumIdx < len(opts) {
			value = opts[m.planEnumIdx]
		}
	}
	return m.savePlanFieldValue(f, value)
}

func (m *Model) savePlanFieldValue(f planField, value string) (tea.Model, tea.Cmd) {
	if value == agentDefaultModel || value == noneValue {
		value = "" // clears the per-stage model override / persona
	}
	target, id := m.currentTarget()
	m.planStatus = "saving…"
	v := value
	return m, m.cmdPostPlanEdit(api.PlanEditRequestDto{
		Edits: []api.PlanEditDto{{Target: target, Id: id, Field: f.Field, Value: &v}},
	})
}

func (m *Model) handlePlanImportKey(key string) (tea.Model, tea.Cmd) {
	if m.planImportResult != nil {
		switch key {
		case "esc":
			m.planImportResult = nil
			return m, nil
		case "a":
			if !m.planImportResult.Diff.IsEmpty() {
				m.planStatus = "applying…"
				return m, m.cmdPostPlanImport(api.PlanImportRequestDto{Source: m.planImportInput, Apply: true})
			}
			return m, nil
		}
		return m, nil
	}

	switch key {
	case "esc":
		m.planTab = planTabStages // back to the Stages section, staying in the Plan tab
		return m, nil
	case "enter":
		if strings.TrimSpace(m.planImportInput) == "" {
			return m, nil
		}
		m.planImportErr = ""
		m.planStatus = "parsing…"
		return m, m.cmdPostPlanImport(api.PlanImportRequestDto{Source: strings.TrimSpace(m.planImportInput), Apply: false})
	case "backspace":
		if len(m.planImportInput) > 0 {
			m.planImportInput = m.planImportInput[:len(m.planImportInput)-1]
		}
		return m, nil
	default:
		if ch, ok := typedChar(key); ok {
			m.planImportInput += ch
		}
		return m, nil
	}
}

// --- current-selection helpers ---

func (m Model) currentField() planField {
	switch {
	case m.planTab == planTabSettings:
		return m.settingsFields()[m.planFieldIdx]
	case m.planTab == planTabGates:
		return gateFields()[m.planFieldIdx]
	default:
		return m.stageFields()[m.planFieldIdx]
	}
}

func (m Model) currentTarget() (target, id string) {
	switch m.planTab {
	case planTabSettings:
		return "plan", ""
	case planTabGates:
		if m.plan != nil && m.planGateIdx < len(m.plan.Gates) {
			return "gate", m.plan.Gates[m.planGateIdx].Name
		}
		return "gate", ""
	default:
		if m.plan != nil && m.planStageIdx < len(m.plan.Stages) {
			return "stage", m.plan.Stages[m.planStageIdx].Id
		}
		return "stage", ""
	}
}

func (m Model) currentFieldValue(field string) string {
	if m.plan == nil {
		return ""
	}
	switch m.planTab {
	case planTabSettings:
		switch field {
		case "name":
			return m.plan.Name
		case "gatepolicy":
			return m.plan.GatePolicy
		case "defaultworkflow":
			return m.plan.DefaultWorkflow
		}
	case planTabGates:
		if m.planGateIdx >= len(m.plan.Gates) {
			return ""
		}
		g := m.plan.Gates[m.planGateIdx]
		switch field {
		case "command":
			return g.Command
		case "tier":
			return g.Tier
		case "timeout":
			return strconv.Itoa(g.TimeoutMinutes)
		case "optional":
			return strconv.FormatBool(g.Optional)
		}
	default:
		if m.planStageIdx >= len(m.plan.Stages) {
			return ""
		}
		s := m.plan.Stages[m.planStageIdx]
		switch field {
		case "title":
			return s.Title
		case "model":
			return derefOr(s.Model, agentDefaultModel)
		case "persona":
			return derefOr(s.Persona, noneValue)
		case "workflow":
			return derefOr(s.Workflow, "")
		case "kind":
			return s.Kind
		case "sessions":
			return strconv.Itoa(s.Sessions)
		case "notes":
			return derefOr(s.Notes, "")
		case "dependson":
			return strings.Join(s.DependsOn, ",")
		}
	}
	return ""
}

func clamp(v, lo, hi int) int {
	if hi < lo {
		return lo
	}
	if v < lo {
		return lo
	}
	if v > hi {
		return hi
	}
	return v
}

func indexOfDefault(opts []string, val string, def int) int {
	for i, o := range opts {
		if o == val {
			return i
		}
	}
	return def
}

func derefOr(p *string, def string) string {
	if p == nil || *p == "" {
		return def
	}
	return *p
}

// --- rendering ---

func (m Model) renderPlanPane() (string, string) {
	if m.plan == nil {
		return subtleStyle.Render("loading plan…"), "esc back"
	}

	tabs := m.renderPlanSections()
	var body, help string

	switch m.planTab {
	case planTabStages:
		body, help = m.renderPlanStages()
	case planTabGates:
		body, help = m.renderPlanGates()
	case planTabSettings:
		body, help = m.renderPlanSettings()
	case planTabImport:
		body, help = m.renderPlanImport()
	}

	status := ""
	if m.planStatus != "" {
		st := safeStyle
		if strings.HasPrefix(m.planStatus, "✗") {
			st = destructStyle
		}
		status = "\n\n" + st.Render(m.planStatus)
	}
	meta := subtleStyle.Render(fmt.Sprintf("%s · v%d", m.plan.Name, m.plan.PlanVersion))
	return tabs + "   " + meta + "\n\n" + body + status, help
}

func (m Model) renderPlanSections() string {
	names := []string{"Stages", "Gates", "Settings", "Import"}
	var parts []string
	for i, n := range names {
		if planTab(i) == m.planTab {
			parts = append(parts, highlightBg.Render(" "+n+" "))
		} else {
			parts = append(parts, subtleStyle.Render(" "+n+" "))
		}
	}
	return strings.Join(parts, subtleStyle.Render("·"))
}

func (m Model) renderPlanStages() (string, string) {
	if m.planDrill {
		return m.renderFieldList(m.stageFields(), m.plan.Stages[m.planStageIdx].Id)
	}
	var lines []string
	for i, s := range m.plan.Stages {
		id := fmt.Sprintf("%-4s", s.Id)
		title := fmt.Sprintf("%-24s", truncate(s.Title, 24))
		meta := fmt.Sprintf("%-10s", fmt.Sprintf("%ds·%s", s.Sessions, s.Kind))
		model := derefOr(s.Model, "—")
		if i == m.planStageIdx {
			lines = append(lines, highlightBg.Render(fmt.Sprintf("  %s %s %s %s", id, title, meta, model)))
			continue
		}
		lines = append(lines, "  "+accentStyle.Render(id)+" "+textStyle.Render(title)+" "+subtleStyle.Render(meta)+" "+purpleText(model))
	}
	return strings.Join(lines, "\n"), "←→ section · ↑↓ select · enter edit · esc back"
}

func (m Model) renderPlanGates() (string, string) {
	if m.planDrill {
		return m.renderFieldList(gateFields(), m.plan.Gates[m.planGateIdx].Name)
	}
	var lines []string
	for i, g := range m.plan.Gates {
		name := fmt.Sprintf("%-10s", g.Name)
		tier := fmt.Sprintf("%-5s", g.Tier)
		cmd := truncate(g.Command, 36)
		if i == m.planGateIdx {
			lines = append(lines, highlightBg.Render(fmt.Sprintf("  %s %s %s", name, tier, cmd)))
			continue
		}
		lines = append(lines, "  "+accentStyle.Render(name)+" "+tierBadge(g.Tier)+strings.Repeat(" ", max(1, 6-lipgloss.Width(g.Tier)))+subtleStyle.Render(cmd))
	}
	return strings.Join(lines, "\n"), "←→ section · ↑↓ select · enter edit · esc back"
}

func (m Model) renderPlanSettings() (string, string) {
	body, _ := m.renderFieldList(m.settingsFields(), "")
	extra := fmt.Sprintf("\n\n  %s %s",
		subtleStyle.Render("plan file:"), subtleStyle.Render(m.plan.PlanFile))
	return body + extra, "←→ section · ↑↓ select · enter edit · esc back"
}

// renderFieldList shows a target's editable fields; the selected one enters an inline editor when active.
func (m Model) renderFieldList(fields []planField, ownerLabel string) (string, string) {
	var lines []string
	if ownerLabel != "" {
		lines = append(lines, "  "+subtleStyle.Render("editing")+" "+accentStyle.Render(ownerLabel), "")
	}
	for i, f := range fields {
		val := m.currentFieldValue(f.Field)
		if m.planEditing && i == m.planFieldIdx {
			lines = append(lines, "  "+m.renderFieldEditor(f))
			continue
		}
		disp := val
		if disp == "" {
			disp = subtleStyle.Render("(unset)")
		} else {
			disp = textStyle.Render(disp)
		}
		row := fmt.Sprintf("  %-16s %s", f.Label, disp)
		if i == m.planFieldIdx && !m.planEditing {
			row = highlightBg.Render(fmt.Sprintf("  %-16s %s", f.Label, val))
		}
		lines = append(lines, row)
	}
	help := "↑↓ field · enter edit · esc back"
	if m.planEditing {
		f := m.currentField()
		if f.Kind == fieldEnum {
			help = "←→ cycle · enter save · esc cancel"
		} else {
			help = "type · enter save · esc cancel"
		}
	}
	return strings.Join(lines, "\n"), help
}

func (m Model) renderFieldEditor(f planField) string {
	if m.planEnumCustom {
		return fmt.Sprintf("%-16s %s", f.Label,
			accentStyle.Render(m.planEditBuf)+accentStyle.Render("▏")+subtleStyle.Render("  type · enter save · esc back"))
	}
	if f.Kind == fieldEnum {
		opts := optionList(f)
		sel := ""
		if m.planEnumIdx < len(opts) {
			sel = opts[m.planEnumIdx]
		}
		carousel := accentStyle.Render("‹") + highlightBg.Render(" "+sel+" ") + accentStyle.Render("›")
		pos := subtleStyle.Render(fmt.Sprintf(" (%d/%d)", m.planEnumIdx+1, len(opts)))
		return fmt.Sprintf("%-16s %s%s", f.Label, carousel, pos)
	}
	return fmt.Sprintf("%-16s %s", f.Label, accentStyle.Render(m.planEditBuf)+accentStyle.Render("▏"))
}

func (m Model) renderPlanImport() (string, string) {
	if m.planImportResult != nil {
		return m.renderImportDiff()
	}
	header := "  " + textStyle.Render("Import a structured plan/tracker doc into the graph.") + "\n" +
		"  " + subtleStyle.Render("Path (relative to repo) or inline markdown — parsed with no model call.") + "\n\n"
	input := "  " + subtleStyle.Render("source: ") + accentStyle.Render(m.planImportInput) + accentStyle.Render("▏")
	errLine := ""
	if m.planImportErr != "" {
		errLine = "\n\n  " + destructStyle.Render("✗ "+m.planImportErr)
	}
	hint := "\n\n  " + subtleStyle.Render("e.g. docs/MAESTRO-PLAN.md")
	return header + input + errLine + hint, "type path · enter preview diff · esc back"
}

func (m Model) renderImportDiff() (string, string) {
	d := m.planImportResult.Diff
	var lines []string
	if d.IsEmpty() {
		lines = append(lines, "  "+safeStyle.Render("Nothing to change — the plan already matches this import."))
		return strings.Join(lines, "\n"), "esc back"
	}
	lines = append(lines, "  "+accentStyle.Render(fmt.Sprintf("%d change(s):", d.TotalChanges())), "")
	for _, s := range d.AddedStages {
		lines = append(lines, "  "+safeStyle.Render("+ stage ")+accentStyle.Render(s.Id)+" "+textStyle.Render(truncate(s.Title, 40)))
	}
	for _, c := range d.ChangedStages {
		for _, f := range c.Fields {
			lines = append(lines, fmt.Sprintf("  %s %s.%s %s→ %s",
				warnStyle.Render("~"), accentStyle.Render(c.Id), f.Field,
				subtleStyle.Render(derefOr(f.Old, "-")+" "), safeStyle.Render(derefOr(f.New, "-"))))
		}
	}
	for _, g := range d.AddedGates {
		lines = append(lines, "  "+safeStyle.Render("+ gate ")+accentStyle.Render(g.Name)+" "+subtleStyle.Render(g.Tier))
	}
	for _, c := range d.ChangedGates {
		for _, f := range c.Fields {
			lines = append(lines, fmt.Sprintf("  %s gate %s.%s %s→ %s",
				warnStyle.Render("~"), accentStyle.Render(c.Id), f.Field,
				subtleStyle.Render(derefOr(f.Old, "-")+" "), safeStyle.Render(derefOr(f.New, "-"))))
		}
	}
	return strings.Join(lines, "\n"), "a apply · esc back"
}

func purpleText(s string) string {
	return tealStyle.Render(s) // model names read as "tool-ish" — teal, from the one palette
}

func tierBadge(tier string) string {
	switch tier {
	case "truth":
		return warnStyle.Render("truth")
	case "fast":
		return safeStyle.Render("fast")
	default:
		return subtleStyle.Render("full")
	}
}
