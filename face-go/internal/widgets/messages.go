package widgets

type WidgetMsg int

// KS2.7 deleted MsgScrollUp/Down/End/PageUp/PageDown. They were the transcript's own scroll
// vocabulary — a FOURTH key namespace that adr/0006 replaced on paper and not in code, and the only
// reason the Agent tab bound `k` (unreachable: the Knowledge mnemonic) and `l` (bound to nothing
// else, documented nowhere). Scrolling is now the one pane set (tui/panescroll.go) applied to the
// transcript's own viewport, so there is nothing left for a scroll MESSAGE to mean.
const (
	MsgToggleFold WidgetMsg = iota
	MsgToggleThinking
	MsgNextMatch
	MsgPrevMatch
)

type MsgAppendLine struct {
	Line interface{}
}

// MsgSetData refreshes the sidebar's glanceable content: plan stages, gate battery, and the live
// MCP task list for the current session.
type MsgSetData struct {
	Stages interface{}
	Gates  interface{}
	Tasks  interface{}
}

type MsgSetSearch struct {
	Query string
}
