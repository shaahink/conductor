package widgets

import (
	"fmt"

	"conductor-face-go/internal/api"
)

// FmtSessionCost renders a session's cost the way its basis allows, and is the ONLY place in the
// Face that turns sessionCostUsd into text — the top bar and the Agent footer both call it, so the
// two readouts cannot disagree in a single frame the way they did before.
//
// "" means "say nothing here": the caller drops the segment rather than printing a zero it cannot
// stand behind. An unpriceable session renders "$—", which reads as "not known yet" instead of
// "free". An older engine serves no basis at all ("" — it predates SC2.3); there is no way to tell
// its zero apart from an unknown, so a zero says nothing and any other figure is taken at face value.
func FmtSessionCost(cost float64, basis string) string {
	switch basis {
	case api.BasisNone:
		return ""
	case api.BasisNoRate:
		return "$—"
	case api.BasisRunRate:
		return "~" + FmtMoney(cost)
	case api.BasisMeasured, api.BasisStreamed:
		return FmtMoney(cost)
	default: // pre-SC2.3 engine: no basis on the wire
		if cost <= 0 {
			return ""
		}
		return FmtMoney(cost)
	}
}

// FmtMoney renders dollars at cent precision, except that a real-but-sub-cent amount says so rather
// than rounding itself away to "$0.00" — money that exists must never render as money that doesn't.
func FmtMoney(v float64) string {
	if v > 0 && v < 0.005 {
		return "<$0.01"
	}
	return fmt.Sprintf("$%.2f", v)
}
