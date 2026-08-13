package widgets

import "charm.land/bubbles/v2/viewport"

// NewPaneViewport is the ONE constructor every scrollable pane in the Face uses, so none of them can
// disagree about wrapping or padding. The scroll idiom itself (the key set, the clamp rule, the
// percent readout) lives in tui/panescroll.go and is documented there; only the constructor is here,
// because the transcript widget owns a pane viewport of its own and `widgets` cannot import `tui`.
// One constructor, two callers, no drift — the alternative was a second `viewport.New()` in this
// package that would silently disagree the day either flag changes.
//
// SoftWrap stays OFF: these bodies are already clipped to the pane width by the renderer that builds
// them (STYLE.md — pad plain, style after), and re-wrapping a styled row would break its columns and
// can cut mid-escape.
func NewPaneViewport() viewport.Model {
	vp := viewport.New()
	vp.SoftWrap = false
	return vp
}
