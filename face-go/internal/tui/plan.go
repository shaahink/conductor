package tui

import (
	"fmt"
	"strconv"
	"strings"

	tea "charm.land/bubbletea/v2"
	"charm.land/lipgloss/v2"

	"conductor-face-go/internal/api"
	"conductor-face-go/internal/widgets"
)

// M6.3: the plan editor. Edit stages, gates, models, workflows, and settings live from the TUI, and
// import/re-import a structured plan doc with a diff — no hand-editing JSON, no spinning up an agent
// to tweak a workflow. Every change round-trips through POST /plan/edit or /plan/import.

// planModel is the Plan tab's own state (K6.3): the plan document it edits, where the cursor is in
// it, and the three modal sub-states (edit, add, delete) the tab captures every key for.
type planModel struct {
	doc      *api.PlanDto
	tab      planTab
	stageIdx int
	gateIdx  int
	fieldIdx int
	drill    bool
	editing  bool
	editBuf  string
	enumIdx  int
	// enumCustom: an enum field's "✎ custom…" option is selected → free-text sub-entry.
	enumCustom bool
	status     string

	importInput  string
	importResult *api.PlanImportResultDto
	importErr    string
	importSource string // what was actually posted (path or prompt) — `a` re-posts it with apply:true
	importBusy   bool   // a prompt is at the advisor — block re-submits, show progress
	promptEditor widgets.TextArea

	adding    bool // add-stage / add-gate form open (id + title/command)
	addField  int  // 0 = id/name, 1 = title/command
	addIdBuf  string
	addValBuf string
	deleting  bool // delete-confirm prompt open for the selected stage/gate
}

// updatePlan handles the load, the edit result and the import result. All three end in this tab's
// own document or its status line, which nothing else reads.
func (m Model) updatePlan(msg tea.Msg) (Model, tea.Cmd, bool) {
	switch msg := msg.(type) {

	case MsgPlanLoaded:
		if msg.Err != "" {
			m.plan.status = "load failed: " + msg.Err
		} else {
			m.plan.doc = msg.Plan
		}
		return m, nil, true

	case MsgPlanEdited:
		if msg.Err != "" {
			m.plan.status = "✗ " + msg.Err
			return m, nil, true
		}
		if msg.Result != nil && !msg.Result.Ok {
			reason := "rejected"
			if msg.Result.Error != nil {
				reason = *msg.Result.Error
			}
			m.plan.status = "✗ " + reason
			return m, nil, true
		}
		m.plan.status = fmt.Sprintf("✓ saved — plan v%d", planVersionOf(msg.Result))
		m.plan.editing = false
		return m, m.cmdFetchPlan(), true

	case MsgPlanImported:
		m.plan.importBusy = false
		if msg.Err != "" {
			m.plan.importErr, m.plan.importResult = msg.Err, nil
			m.plan.status = ""
			return m, nil, true
		}
		m.plan.importErr = ""
		m.plan.importResult = msg.Result
		if msg.Result != nil && !msg.Result.Ok && msg.Result.Error != nil {
			m.plan.importErr, m.plan.importResult = *msg.Result.Error, nil
			m.plan.status = ""
			return m, nil, true
		}
		if msg.Result != nil && msg.Result.Applied {
			m.plan.status = fmt.Sprintf("✓ imported — plan v%d", msg.Result.PlanVersion)
			m.plan.importResult = nil
			return m, m.cmdFetchPlan(), true
		}
		m.plan.status = ""
		return m, nil, true
	}
	return m, nil, false
}

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
	Options []string            // enum only
	Custom  bool                // enum also accepts a free-text value via the "✎ custom…" option
	Target  string              // edit target override (e.g. "limits"); empty = derived from the section
	Display func(string) string // optional browse-view transform of the raw value (edit sees the raw)
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
	qaInheritValue    = "(workflow decides)"
)

// P2: the QA frequency dial — a friendly projection onto the existing workflows (off =
// deliver-only, everySession = deliver-verify, phaseGate = big-dev-then-big-audit). The sentinel
// clears the dial so the stage/plan workflow decides, exactly the classic behavior.
var qaModeChoices = []string{qaInheritValue, "off", "everySession", "phaseGate"}

func (m Model) stageFields() []planField {
	return []planField{
		{Label: "title", Field: "title", Kind: fieldText},
		{Label: "model", Field: "model", Kind: fieldEnum, Options: m.modelChoices(), Custom: true},
		{Label: "persona", Field: "persona", Kind: fieldEnum, Options: personaChoices},
		{Label: "workflow", Field: "workflow", Kind: fieldEnum, Options: m.workflowChoices()},
		{Label: "kind", Field: "kind", Kind: fieldEnum, Options: []string{"deliver", "review"}},
		{Label: "qa", Field: "qamode", Kind: fieldEnum, Options: qaModeChoices},
		{Label: "sessions", Field: "sessions", Kind: fieldInt},
		{Label: "notes", Field: "notes", Kind: fieldText},
		{Label: "dependsOn", Field: "dependson", Kind: fieldText},
	}
}

// modelChoices is the curated list plus the plan's own defaultModel (so it's never absent from the
// picker), de-duplicated, in a stable order.
func (m Model) modelChoices() []string {
	if m.plan.doc == nil || m.plan.doc.DefaultModel == "" {
		return modelChoices
	}
	for _, c := range modelChoices {
		if c == m.plan.doc.DefaultModel {
			return modelChoices
		}
	}
	out := append([]string{m.plan.doc.DefaultModel}, modelChoices...)
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
		// P2 QA dial: saved through the "qa" edit target (pipeline.qa); the threshold is the base
		// verifier bar in limits. Both ride the same live reload as everything else here.
		{Label: "qa", Field: "mode", Kind: fieldEnum, Options: qaModeChoices, Target: "qa"},
		{Label: "verifierThreshold", Field: "verifierthreshold", Kind: fieldInt, Target: "limits"},
		// G3.3 live limits: saved through the "limits" edit target; the engine reloads the plan at
		// its next session boundary, so these steer the CURRENT run. Saving an empty value clears
		// a nullable cap (maxSessions / maxRunCostUsd / maxRunTokens).
		{Label: "maxSessions", Field: "maxsessions", Kind: fieldInt, Target: "limits"},
		{Label: "maxRunCostUsd", Field: "maxruncostusd", Kind: fieldText, Target: "limits"},
		{Label: "maxRunTokens", Field: "maxruntokens", Kind: fieldInt, Target: "limits"},
		{Label: "stallMinutes", Field: "stallminutes", Kind: fieldInt, Target: "limits"},
		{Label: "sessionTimeout", Field: "sessiontimeoutminutes", Kind: fieldInt, Target: "limits"},
		// P5: the session-token rollover, honestly labeled — OFF is the default and stays the
		// default. Editing writes limits.maxSessionTokens (empty = off) and rides the live reload.
		{Label: "sessionRollover", Field: "maxsessiontokens", Kind: fieldInt, Target: "limits", Display: rolloverDisplay},
		{Label: "softBreakRatio", Field: "softbreakratio", Kind: fieldText, Target: "limits", Display: softBreakDisplay},
		// P5: the same knob session-scoped — posts the set-rollover control verb (tokens · off ·
		// clear), which flips the CURRENT run live and never writes the plan file.
		{Label: "rollover (run)", Field: "set-rollover", Kind: fieldText, Target: "control", Display: rolloverThisRunDisplay},
	}
}

// rolloverDisplay renders limits.maxSessionTokens honestly: absent = the feature is OFF, which is
// the default and must read as such (the audit's "honestly surfaced" requirement, P5).
func rolloverDisplay(raw string) string {
	if raw == "" {
		return "OFF — DeepSeek-style session rollover disabled (default)"
	}
	return "ON — roll over past " + raw + " tokens/session"
}

func softBreakDisplay(raw string) string {
	if raw == "" {
		return "(0.8 default — active only when rollover is ON)"
	}
	return raw
}

// rolloverThisRunDisplay renders the ACTIVE set-rollover override from /state (P5 follow-up):
// no override is the default and must read as such — the plan's own rollover row is right above.
func rolloverThisRunDisplay(raw string) string {
	switch raw {
	case "":
		return "none — the plan decides (type tokens · off · clear)"
	case "off":
		return "OFF this run — overriding the plan"
	default:
		return "ON at " + raw + " tokens this run — overriding the plan"
	}
}

func (m Model) workflowChoices() []string {
	if m.plan.doc != nil && len(m.plan.doc.Workflows) > 0 {
		return m.plan.doc.Workflows
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
	if m.plan.editing {
		return m.handlePlanFieldEdit(key)
	}
	if m.plan.adding {
		return m.handlePlanAddKey(key)
	}
	if m.plan.deleting {
		return m.handlePlanDeleteKey(key)
	}
	// A returned diff owns the keys wherever it came from — the Import path box or the Prompt box
	// both land on the same diff view with the same a-apply / esc-back contract.
	if m.plan.importResult != nil {
		return m.handleImportDiffKey(key)
	}
	if m.plan.tab == planTabImport {
		return m.handlePlanImportKey(key)
	}
	if m.plan.tab == planTabPrompt {
		return m.handlePlanPromptKey(key)
	}

	switch key {
	case "esc":
		if m.plan.drill {
			m.plan.drill = false
			return m, nil
		}
		return m.openTab(TabAgent) // leave the Plan tab
	case "n": // new stage/gate — n avoids the 'a' Agent-tab mnemonic
		m.planBeginAdd()
		return m, nil
	case "x": // delete the selected stage/gate (confirm first) — `x` matches the Procs tab's kill
		// key, the codebase's existing "destructive, asks y/N first" mnemonic. It moved off `d` when
		// Dev claimed that letter globally (see tabKey): the list isn't an owning sub-state, so the
		// mnemonic loop would have swallowed `d` before this handler ever saw it.
		m.planBeginDelete()
		return m, nil
	case "right":
		if !m.plan.drill {
			m.plan.tab = (m.plan.tab + 1) % planTabCount
			m.plan.fieldIdx = 0
			m.plan.status = ""
		}
		return m, nil
	case "left":
		if !m.plan.drill {
			m.plan.tab = (m.plan.tab + planTabCount - 1) % planTabCount
			m.plan.fieldIdx = 0
			m.plan.status = ""
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
	case m.plan.tab == planTabSettings:
		m.plan.fieldIdx = clamp(m.plan.fieldIdx+delta, 0, len(m.settingsFields())-1)
	case m.plan.drill && m.plan.tab == planTabStages:
		m.plan.fieldIdx = clamp(m.plan.fieldIdx+delta, 0, len(m.stageFields())-1)
	case m.plan.drill && m.plan.tab == planTabGates:
		m.plan.fieldIdx = clamp(m.plan.fieldIdx+delta, 0, len(gateFields())-1)
	case m.plan.tab == planTabStages && m.plan.doc != nil:
		m.plan.stageIdx = clamp(m.plan.stageIdx+delta, 0, len(m.plan.doc.Stages)-1)
	case m.plan.tab == planTabGates && m.plan.doc != nil:
		m.plan.gateIdx = clamp(m.plan.gateIdx+delta, 0, len(m.plan.doc.Gates)-1)
	}
}

// planEnter drills into a row's fields, or begins editing the selected field.
func (m *Model) planEnter() (tea.Model, tea.Cmd) {
	if m.plan.doc == nil {
		return m, nil
	}
	switch {
	case m.plan.tab == planTabSettings:
		m.beginFieldEdit(m.settingsFields()[m.plan.fieldIdx])
	case m.plan.tab == planTabStages && !m.plan.drill:
		if len(m.plan.doc.Stages) > 0 {
			m.plan.drill = true
			m.plan.fieldIdx = 0
			m.plan.status = ""
		}
	case m.plan.tab == planTabGates && !m.plan.drill:
		if len(m.plan.doc.Gates) > 0 {
			m.plan.drill = true
			m.plan.fieldIdx = 0
			m.plan.status = ""
		}
	case m.plan.drill && m.plan.tab == planTabStages:
		m.beginFieldEdit(m.stageFields()[m.plan.fieldIdx])
	case m.plan.drill && m.plan.tab == planTabGates:
		m.beginFieldEdit(gateFields()[m.plan.fieldIdx])
	}
	return m, nil
}

func (m *Model) beginFieldEdit(f planField) {
	cur := m.currentFieldValue(f.Field)
	m.plan.editing = true
	m.plan.enumCustom = false
	m.plan.status = ""
	if f.Kind == fieldEnum {
		m.plan.enumIdx = indexOfDefault(optionList(f), cur, 0)
		m.plan.editBuf = ""
	} else {
		m.plan.editBuf = cur
	}
}

func (m *Model) handlePlanFieldEdit(key string) (tea.Model, tea.Cmd) {
	f := m.currentField()

	// Free-text sub-entry, reached by picking an enum's "✎ custom…" option.
	if m.plan.enumCustom {
		switch key {
		case "esc":
			m.plan.enumCustom = false // back to the carousel, still editing
		case "enter":
			m.plan.enumCustom = false
			return m.savePlanFieldValue(f, strings.TrimSpace(m.plan.editBuf))
		case "backspace":
			if len(m.plan.editBuf) > 0 {
				m.plan.editBuf = m.plan.editBuf[:len(m.plan.editBuf)-1]
			}
		default:
			if ch, ok := typedChar(key); ok {
				m.plan.editBuf += ch
			}
		}
		return m, nil
	}

	switch key {
	case "esc":
		m.plan.editing = false
		return m, nil
	case "enter":
		if f.Kind == fieldEnum {
			opts := optionList(f)
			if m.plan.enumIdx < len(opts) && opts[m.plan.enumIdx] == customSentinel {
				m.plan.enumCustom, m.plan.editBuf = true, "" // drop into free-text entry
				return m, nil
			}
		}
		return m.savePlanField(f)
	}

	if f.Kind == fieldEnum {
		opts := optionList(f)
		switch key {
		case "left", "h":
			m.plan.enumIdx = (m.plan.enumIdx - 1 + len(opts)) % len(opts)
		case "right", "l", "space":
			m.plan.enumIdx = (m.plan.enumIdx + 1) % len(opts)
		}
		return m, nil
	}

	// text / int
	switch key {
	case "backspace":
		if len(m.plan.editBuf) > 0 {
			m.plan.editBuf = m.plan.editBuf[:len(m.plan.editBuf)-1]
		}
	default:
		if ch, ok := typedChar(key); ok {
			if f.Kind == fieldInt && (ch < "0" || ch > "9") {
				return m, nil // ints accept digits only
			}
			m.plan.editBuf += ch
		}
	}
	return m, nil
}

func (m *Model) savePlanField(f planField) (tea.Model, tea.Cmd) {
	value := m.plan.editBuf
	if f.Kind == fieldEnum {
		if opts := optionList(f); m.plan.enumIdx < len(opts) {
			value = opts[m.plan.enumIdx]
		}
	}
	return m.savePlanFieldValue(f, value)
}

func (m *Model) savePlanFieldValue(f planField, value string) (tea.Model, tea.Cmd) {
	if value == agentDefaultModel || value == noneValue || value == qaInheritValue {
		value = "" // clears the per-stage model override / persona / QA dial
	}
	target, id := m.currentTarget()
	if f.Target != "" { // field-level override (the Settings limits rows post to "limits")
		target, id = f.Target, ""
	}
	// P5: the "rollover (run)" row is not a plan edit at all — it posts the set-rollover control
	// verb, which flips run state at the engine and NEVER writes the plan file.
	if target == "control" {
		m.plan.status = "sending " + f.Field + "…"
		return m, m.cmdPostControl(api.ControlRequestDto{Command: f.Field, Value: value})
	}
	m.plan.status = "saving…"
	v := value
	return m, m.cmdPostPlanEdit(api.PlanEditRequestDto{
		Edits: []api.PlanEditDto{{Target: target, Id: id, Field: f.Field, Value: &v}},
	})
}

// handleImportDiffKey drives the returned diff, shared by the Import and Prompt sections: `a`
// re-posts exactly what was previewed with apply:true (the server applies the cached parse — the
// advisor is not consulted twice), esc discards.
func (m *Model) handleImportDiffKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "esc":
		m.plan.importResult = nil
		return m, nil
	case "a":
		if !m.plan.importResult.Diff.IsEmpty() && !m.plan.importBusy {
			m.plan.status = "applying…"
			m.plan.importBusy = true
			return m, m.cmdPostPlanImport(api.PlanImportRequestDto{Source: m.plan.importSource, Apply: true})
		}
		return m, nil
	}
	return m, nil
}

func (m *Model) handlePlanImportKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "esc":
		m.plan.tab = planTabStages // back to the Stages section, staying in the Plan tab
		return m, nil
	// The path input has no caret, so ←→ keep switching sections (the Prompt box next door needs
	// its arrows for the real editor, so it only offers esc).
	case "right":
		m.plan.tab, m.plan.status = planTabPrompt, ""
		return m, nil
	case "left":
		m.plan.tab, m.plan.status = planTabSettings, ""
		return m, nil
	case "enter":
		if strings.TrimSpace(m.plan.importInput) == "" || m.plan.importBusy {
			return m, nil
		}
		m.plan.importErr = ""
		m.plan.status = "parsing…"
		m.plan.importSource = strings.TrimSpace(m.plan.importInput)
		m.plan.importBusy = true
		return m, m.cmdPostPlanImport(api.PlanImportRequestDto{Source: m.plan.importSource, Apply: false})
	case "backspace":
		if len(m.plan.importInput) > 0 {
			m.plan.importInput = m.plan.importInput[:len(m.plan.importInput)-1]
		}
		return m, nil
	default:
		if ch, ok := typedChar(key); ok {
			m.plan.importInput += ch
		}
		return m, nil
	}
}

// handlePlanPromptKey is the G1.2 prompt box: a multi-line TextArea (enter = newline), ctrl+s
// sends the prose to the advisor via the same POST /plan/import the Import section uses.
func (m *Model) handlePlanPromptKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "esc":
		m.plan.tab = planTabStages
		return m, nil
	case "ctrl+s":
		prompt := strings.TrimSpace(m.plan.promptEditor.Value())
		if prompt == "" || m.plan.importBusy {
			return m, nil
		}
		m.plan.importErr = ""
		m.plan.status = "consulting the advisor…"
		m.plan.importSource = prompt
		m.plan.importBusy = true
		return m, m.cmdPostPlanImport(api.PlanImportRequestDto{Source: prompt, Apply: false})
	default:
		if m.plan.promptEditor.Width == 0 { // lazily sized — the pane width isn't known at construction
			m.plan.promptEditor = widgets.NewTextArea("", max(20, m.paneCols()-4), 5)
		}
		m.plan.promptEditor = m.plan.promptEditor.Update(key)
		return m, nil
	}
}

// --- add / delete a whole stage or gate ---

// planBeginAdd opens the two-field add form, but only in a Stages/Gates list (not Settings/Import,
// not while drilled into a row's fields).
func (m *Model) planBeginAdd() {
	if m.plan.drill || (m.plan.tab != planTabStages && m.plan.tab != planTabGates) {
		return
	}
	m.plan.adding = true
	m.plan.addField = 0
	m.plan.addIdBuf, m.plan.addValBuf = "", ""
	m.plan.status = ""
}

func (m *Model) handlePlanAddKey(key string) (tea.Model, tea.Cmd) {
	switch key {
	case "esc":
		m.plan.adding = false
		return m, nil
	case "tab":
		m.plan.addField = 1 - m.plan.addField
		return m, nil
	case "enter":
		id := strings.TrimSpace(m.plan.addIdBuf)
		if id == "" {
			return m, nil // an id/name is required — stay in the form
		}
		target := "stage"
		if m.plan.tab == planTabGates {
			target = "gate"
		}
		v := strings.TrimSpace(m.plan.addValBuf)
		m.plan.adding = false
		m.plan.status = "adding…"
		return m, m.cmdPostPlanEdit(api.PlanEditRequestDto{
			Edits: []api.PlanEditDto{{Target: target, Op: "add", Id: id, Value: &v}},
		})
	case "backspace":
		if m.plan.addField == 0 {
			if len(m.plan.addIdBuf) > 0 {
				m.plan.addIdBuf = m.plan.addIdBuf[:len(m.plan.addIdBuf)-1]
			}
		} else if len(m.plan.addValBuf) > 0 {
			m.plan.addValBuf = m.plan.addValBuf[:len(m.plan.addValBuf)-1]
		}
	default:
		if ch, ok := typedChar(key); ok {
			if m.plan.addField == 0 {
				m.plan.addIdBuf += ch
			} else {
				m.plan.addValBuf += ch
			}
		}
	}
	return m, nil
}

func (m *Model) planBeginDelete() {
	if m.plan.drill {
		return
	}
	if m.plan.tab == planTabStages && m.plan.doc != nil && len(m.plan.doc.Stages) > 0 {
		m.plan.deleting, m.plan.status = true, ""
	} else if m.plan.tab == planTabGates && m.plan.doc != nil && len(m.plan.doc.Gates) > 0 {
		m.plan.deleting, m.plan.status = true, ""
	}
}

func (m *Model) handlePlanDeleteKey(key string) (tea.Model, tea.Cmd) {
	switch strings.ToLower(key) {
	case "y", "enter":
		target, id := m.currentTarget()
		m.plan.deleting = false
		if id == "" {
			return m, nil
		}
		m.plan.status = "deleting…"
		return m, m.cmdPostPlanEdit(api.PlanEditRequestDto{
			Edits: []api.PlanEditDto{{Target: target, Op: "delete", Id: id}},
		})
	case "n", "esc":
		m.plan.deleting = false
	}
	return m, nil
}

// --- current-selection helpers ---

func (m Model) currentField() planField {
	switch {
	case m.plan.tab == planTabSettings:
		return m.settingsFields()[m.plan.fieldIdx]
	case m.plan.tab == planTabGates:
		return gateFields()[m.plan.fieldIdx]
	default:
		return m.stageFields()[m.plan.fieldIdx]
	}
}

func (m Model) currentTarget() (target, id string) {
	switch m.plan.tab {
	case planTabSettings:
		return "plan", ""
	case planTabGates:
		if m.plan.doc != nil && m.plan.gateIdx < len(m.plan.doc.Gates) {
			return "gate", m.plan.doc.Gates[m.plan.gateIdx].Name
		}
		return "gate", ""
	default:
		if m.plan.doc != nil && m.plan.stageIdx < len(m.plan.doc.Stages) {
			return "stage", m.plan.doc.Stages[m.plan.stageIdx].Id
		}
		return "stage", ""
	}
}

func (m Model) currentFieldValue(field string) string {
	if m.plan.doc == nil {
		return ""
	}
	switch m.plan.tab {
	case planTabSettings:
		switch field {
		case "name":
			return m.plan.doc.Name
		case "gatepolicy":
			return m.plan.doc.GatePolicy
		case "defaultworkflow":
			return m.plan.doc.DefaultWorkflow
		case "mode":
			if m.plan.doc.Qa != nil {
				return m.plan.doc.Qa.Mode
			}
			return qaInheritValue
		case "verifierthreshold":
			return strconv.Itoa(m.plan.doc.Limits.VerifierThreshold)
		case "maxsessions":
			if m.plan.doc.Limits.MaxSessions != nil {
				return strconv.Itoa(*m.plan.doc.Limits.MaxSessions)
			}
			return ""
		case "maxruncostusd":
			if m.plan.doc.Limits.MaxRunCostUsd != nil {
				return strconv.FormatFloat(*m.plan.doc.Limits.MaxRunCostUsd, 'f', -1, 64)
			}
			return ""
		case "maxruntokens":
			if m.plan.doc.Limits.MaxRunTokens != nil {
				return strconv.FormatInt(*m.plan.doc.Limits.MaxRunTokens, 10)
			}
			return ""
		case "stallminutes":
			return strconv.Itoa(m.plan.doc.Limits.StallMinutes)
		case "sessiontimeoutminutes":
			return strconv.Itoa(m.plan.doc.Limits.SessionTimeoutMinutes)
		case "maxsessiontokens":
			if m.plan.doc.Limits.MaxSessionTokens != nil {
				return strconv.FormatInt(*m.plan.doc.Limits.MaxSessionTokens, 10)
			}
			return ""
		case "softbreakratio":
			if m.plan.doc.Limits.SoftBreakRatio != nil {
				return strconv.FormatFloat(*m.plan.doc.Limits.SoftBreakRatio, 'f', -1, 64)
			}
			return ""
		case "set-rollover":
			// Session-scoped — the engine owns the state; /state now surfaces the active
			// override (P5 follow-up), so the row can show it instead of a blind hint.
			if m.data.Plan != nil && m.data.Plan.MaxSessionTokensThisRun != nil {
				if *m.data.Plan.MaxSessionTokensThisRun == 0 {
					return "off"
				}
				return strconv.FormatInt(*m.data.Plan.MaxSessionTokensThisRun, 10)
			}
			return ""
		}
	case planTabGates:
		if m.plan.gateIdx >= len(m.plan.doc.Gates) {
			return ""
		}
		g := m.plan.doc.Gates[m.plan.gateIdx]
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
		if m.plan.stageIdx >= len(m.plan.doc.Stages) {
			return ""
		}
		s := m.plan.doc.Stages[m.plan.stageIdx]
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
		case "qamode":
			return derefOr(s.QaMode, qaInheritValue)
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
	if m.plan.doc == nil {
		return subtleStyle.Render("loading plan…"), "esc back"
	}

	tabs := m.renderPlanSections()
	var body, help string

	switch m.plan.tab {
	case planTabStages:
		body, help = m.renderPlanStages()
	case planTabGates:
		body, help = m.renderPlanGates()
	case planTabSettings:
		body, help = m.renderPlanSettings()
	case planTabImport:
		body, help = m.renderPlanImport()
	case planTabPrompt:
		body, help = m.renderPlanPrompt()
	}

	status := ""
	if m.plan.status != "" {
		st := safeStyle
		if strings.HasPrefix(m.plan.status, "✗") {
			st = destructStyle
		}
		status = "\n\n" + st.Render(m.plan.status)
	}
	meta := subtleStyle.Render(fmt.Sprintf("%s · v%d", m.plan.doc.Name, m.plan.doc.PlanVersion))
	return tabs + "   " + meta + "\n\n" + body + status, help
}

func (m Model) renderPlanSections() string {
	names := []string{"Stages", "Gates", "Settings", "Import", "Prompt"}
	var parts []string
	for i, n := range names {
		if planTab(i) == m.plan.tab {
			parts = append(parts, highlightBg.Render(" "+n+" "))
		} else {
			parts = append(parts, subtleStyle.Render(" "+n+" "))
		}
	}
	return strings.Join(parts, subtleStyle.Render("·"))
}

func (m Model) renderPlanStages() (string, string) {
	if m.plan.drill {
		return m.renderFieldList(m.stageFields(), m.plan.doc.Stages[m.plan.stageIdx].Id)
	}
	if m.plan.adding {
		return m.renderPlanAddForm("stage", "id", "title")
	}
	var lines []string
	for i, s := range m.plan.doc.Stages {
		id := fmt.Sprintf("%-4s", s.Id)
		title := fmt.Sprintf("%-24s", truncate(s.Title, 24))
		meta := fmt.Sprintf("%-10s", fmt.Sprintf("%ds·%s", s.Sessions, s.Kind))
		model := derefOr(s.Model, "—")
		if i == m.plan.stageIdx {
			lines = append(lines, highlightBg.Render(fmt.Sprintf("  %s %s %s %s", id, title, meta, model)))
			continue
		}
		lines = append(lines, "  "+accentStyle.Render(id)+" "+textStyle.Render(title)+" "+subtleStyle.Render(meta)+" "+purpleText(model))
	}
	if m.plan.deleting {
		return strings.Join(append(lines, "", m.renderPlanDeleteConfirm("stage")), "\n"), "y confirm · n cancel"
	}
	return strings.Join(lines, "\n"), "↑↓ select · enter edit · n add · d del · esc"
}

func (m Model) renderPlanGates() (string, string) {
	if m.plan.drill {
		return m.renderFieldList(gateFields(), m.plan.doc.Gates[m.plan.gateIdx].Name)
	}
	if m.plan.adding {
		return m.renderPlanAddForm("gate", "name", "command")
	}
	var lines []string
	for i, g := range m.plan.doc.Gates {
		name := fmt.Sprintf("%-10s", g.Name)
		tier := fmt.Sprintf("%-5s", g.Tier)
		cmd := truncate(g.Command, 36)
		if i == m.plan.gateIdx {
			lines = append(lines, highlightBg.Render(fmt.Sprintf("  %s %s %s", name, tier, cmd)))
			continue
		}
		lines = append(lines, "  "+accentStyle.Render(name)+" "+tierBadge(g.Tier)+strings.Repeat(" ", max(1, 6-lipgloss.Width(g.Tier)))+subtleStyle.Render(cmd))
	}
	if m.plan.deleting {
		return strings.Join(append(lines, "", m.renderPlanDeleteConfirm("gate")), "\n"), "y confirm · n cancel"
	}
	return strings.Join(lines, "\n"), "↑↓ select · enter edit · n add · d del · esc"
}

// renderPlanAddForm is the two-field add form (id/name, then title/command); the active field carries
// the cursor. Tab switches fields, enter submits, esc cancels — handled in handlePlanAddKey.
func (m Model) renderPlanAddForm(kind, idLabel, valLabel string) (string, string) {
	idCur, valCur := "", ""
	if m.plan.addField == 0 {
		idCur = "▏"
	} else {
		valCur = "▏"
	}
	body := "  " + accentStyle.Render("+ new "+kind) + "\n\n" +
		fmt.Sprintf("  %-9s %s%s", idLabel, textStyle.Render(m.plan.addIdBuf), accentStyle.Render(idCur)) + "\n" +
		fmt.Sprintf("  %-9s %s%s", valLabel, textStyle.Render(m.plan.addValBuf), accentStyle.Render(valCur))
	return body, "type · tab field · enter add · esc cancel"
}

func (m Model) renderPlanDeleteConfirm(kind string) string {
	_, id := m.currentTarget()
	return "  " + destructStyle.Render("⚠ delete "+kind+" ") + accentStyle.Render(id) + destructStyle.Render(" ?") + "  " + warnStyle.Render("y/N")
}

func (m Model) renderPlanSettings() (string, string) {
	body, _ := m.renderFieldList(m.settingsFields(), "")
	extra := fmt.Sprintf("\n\n  %s %s",
		subtleStyle.Render("plan file:"), subtleStyle.Render(m.plan.doc.PlanFile))
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
		if m.plan.editing && i == m.plan.fieldIdx {
			lines = append(lines, "  "+m.renderFieldEditor(f))
			continue
		}
		shown := val
		if f.Display != nil { // P5: browse-view transform (e.g. rollover's honest OFF label)
			shown = f.Display(val)
		}
		disp := shown
		if disp == "" {
			disp = subtleStyle.Render("(unset)")
		} else {
			disp = textStyle.Render(disp)
		}
		row := fmt.Sprintf("  %-17s %s", f.Label, disp)
		if i == m.plan.fieldIdx && !m.plan.editing {
			row = highlightBg.Render(fmt.Sprintf("  %-17s %s", f.Label, shown))
		}
		lines = append(lines, row)
	}
	help := "↑↓ field · enter edit · esc back"
	if m.plan.editing {
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
	if m.plan.enumCustom {
		return fmt.Sprintf("%-17s %s", f.Label,
			accentStyle.Render(m.plan.editBuf)+accentStyle.Render("▏")+subtleStyle.Render("  type · enter save · esc back"))
	}
	if f.Kind == fieldEnum {
		opts := optionList(f)
		sel := ""
		if m.plan.enumIdx < len(opts) {
			sel = opts[m.plan.enumIdx]
		}
		carousel := accentStyle.Render("‹") + highlightBg.Render(" "+sel+" ") + accentStyle.Render("›")
		pos := subtleStyle.Render(fmt.Sprintf(" (%d/%d)", m.plan.enumIdx+1, len(opts)))
		return fmt.Sprintf("%-17s %s%s", f.Label, carousel, pos)
	}
	return fmt.Sprintf("%-17s %s", f.Label, accentStyle.Render(m.plan.editBuf)+accentStyle.Render("▏"))
}

func (m Model) renderPlanImport() (string, string) {
	if m.plan.importResult != nil {
		return m.renderImportDiff()
	}
	header := "  " + textStyle.Render("Import a structured plan/tracker doc into the graph.") + "\n" +
		"  " + subtleStyle.Render("Path (relative to repo) or inline markdown — parsed with no model call.") + "\n\n"
	input := "  " + subtleStyle.Render("source: ") + accentStyle.Render(m.plan.importInput) + accentStyle.Render("▏")
	errLine := ""
	if m.plan.importErr != "" {
		errLine = "\n\n  " + destructStyle.Render("✗ "+m.plan.importErr)
	}
	hint := "\n\n  " + subtleStyle.Render("e.g. docs/PLAN.md")
	return header + input + errLine + hint, "type path · enter preview diff · esc back"
}

// renderPlanPrompt is the G1.2 AI-native editor: describe the change in plain English, the plan's
// advisor model turns it into the same diff/confirm/apply flow the Import section uses.
func (m Model) renderPlanPrompt() (string, string) {
	if m.plan.importResult != nil {
		return m.renderImportDiff()
	}
	header := "  " + textStyle.Render("Change the plan by prompt — plain English in, a reviewable diff out.") + "\n" +
		"  " + subtleStyle.Render("The advisor model interprets it; nothing applies until you confirm.") + "\n\n"

	ed := m.plan.promptEditor
	if ed.Width == 0 {
		ed = widgets.NewTextArea("", max(20, m.paneCols()-4), 5)
	}
	box := indent(ed.View(), "  ")

	errLine := ""
	if m.plan.importErr != "" {
		errLine = "\n\n  " + destructStyle.Render("✗ "+m.plan.importErr)
	}
	busy := ""
	if m.plan.importBusy {
		busy = "\n\n  " + warnStyle.Render("● consulting the advisor model…")
	}
	hint := "\n\n  " + subtleStyle.Render(`e.g. "add a lint gate that runs dotnet format" · "split S1 into two stages"`)
	return header + box + errLine + busy + hint, "type · ctrl+s send to advisor · esc back"
}

func (m Model) renderImportDiff() (string, string) {
	d := m.plan.importResult.Diff
	interpreted := ""
	if m.plan.importResult.Interpreter != nil && *m.plan.importResult.Interpreter != "structured" {
		interpreted = "  " + subtleStyle.Render("interpreted by ") + tealStyle.Render(*m.plan.importResult.Interpreter) + "\n"
	}
	var lines []string
	if d.IsEmpty() {
		if interpreted != "" {
			lines = append(lines, strings.TrimSuffix(interpreted, "\n"), "")
		}
		lines = append(lines, "  "+safeStyle.Render("Nothing to change — the plan already matches this import."))
		return strings.Join(lines, "\n"), "esc back"
	}
	if interpreted != "" {
		lines = append(lines, strings.TrimSuffix(interpreted, "\n"))
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
		// Always show the command: a gate is a shell command the engine will execute — the whole
		// point of the confirm step is reviewing exactly that before it lands in the plan.
		lines = append(lines, "  "+safeStyle.Render("+ gate ")+accentStyle.Render(g.Name)+" "+
			subtleStyle.Render(g.Tier)+"  "+textStyle.Render(truncate(g.Command, 48)))
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
