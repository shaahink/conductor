package tui

// v3 dashboard geometry:
//
//	┌ Top bar (status) ────────────────────────────┐  row 0
//	│ Tab strip                                     │  row 1
//	│ Sidebar │ Content (active pane)               │  rows 2 … H-2
//	│ Bottom bar (hints / command line)             │  row H-1
//	└───────────────────────────────────────────────┘
//
// The sidebar is always present unless collapsed; the content pane fills whatever is left.

type LayoutRects struct {
	Width  int
	Height int

	Top     Rect
	Tabs    Rect
	Bottom  Rect
	Sidebar Rect // plan tree + gates; zero width when collapsed
	Content Rect // the active main pane
}

type Rect struct {
	X      int
	Y      int
	Width  int
	Height int
}

const (
	sidebarWidthPct = 26
	sidebarMinW     = 22
	sidebarMaxW     = 38
)

func ComputeLayout(width, height int, sidebarCollapsed bool) LayoutRects {
	layout := LayoutRects{Width: width, Height: height}
	if height < 8 || width < 10 {
		return layout
	}

	layout.Top = Rect{X: 0, Y: 0, Width: width, Height: 1}
	layout.Tabs = Rect{X: 0, Y: 1, Width: width, Height: 1}
	layout.Bottom = Rect{X: 0, Y: height - 1, Width: width, Height: 1}

	contentY := 2
	contentH := height - 3
	if contentH < 3 {
		contentH = 3
	}

	sidebarW := 0
	if !sidebarCollapsed {
		sidebarW = width * sidebarWidthPct / 100
		if sidebarW < sidebarMinW {
			sidebarW = sidebarMinW
		}
		if sidebarW > sidebarMaxW {
			sidebarW = sidebarMaxW
		}
		if sidebarW > width-24 {
			sidebarW = 0 // too narrow to show both — hide the sidebar
		}
	}

	if sidebarW > 0 {
		layout.Sidebar = Rect{X: 0, Y: contentY, Width: sidebarW, Height: contentH}
	}
	layout.Content = Rect{X: sidebarW, Y: contentY, Width: width - sidebarW, Height: contentH}
	return layout
}
