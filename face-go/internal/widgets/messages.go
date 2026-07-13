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
	MsgSelectUp
	MsgSelectDown
	MsgSelectExpand
)

type MsgSetLines struct {
	Lines interface{}
}

type MsgAppendLine struct {
	Line interface{}
}

type MsgSetData struct {
	Stages interface{}
	Gates  interface{}
}

type MsgToggleStage struct {
	StageId string
}

type MsgSetSearch struct {
	Query string
}
