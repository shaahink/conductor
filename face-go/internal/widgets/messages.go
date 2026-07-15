package widgets

type WidgetMsg int

const (
	MsgScrollUp WidgetMsg = iota
	MsgScrollDown
	MsgScrollEnd
	MsgScrollPageUp
	MsgScrollPageDown
	MsgToggleFold
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
