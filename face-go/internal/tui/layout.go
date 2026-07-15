package tui

type LayoutRects struct {
	Width  int
	Height int

	Ticker  Rect
	Footer  Rect
	Main    Rect // area between ticker and footer
	Sidebar Rect // plan tree, zero width when closed
	Transcr Rect // agent transcript
}

type Rect struct {
	X      int
	Y      int
	Width  int
	Height int
}

const (
	sidebarWidthPct = 28
	sidebarMinW     = 24
	sidebarMaxW     = 42
)

func ComputeLayout(width, height int, sidebarOpen bool) LayoutRects {
	layout := LayoutRects{
		Width:  width,
		Height: height,
	}

	if height < 8 {
		return layout
	}

	layout.Ticker = Rect{X: 0, Y: 0, Width: width, Height: 1}

	footerY := height - 1
	layout.Footer = Rect{X: 0, Y: footerY, Width: width, Height: 1}

	mainY := 1
	mainH := height - 2
	if mainH < 3 {
		mainH = 3
	}
	layout.Main = Rect{X: 0, Y: mainY, Width: width, Height: mainH}

	transcrX := 0
	transcrW := width

	if sidebarOpen {
		sw := width * sidebarWidthPct / 100
		if sw < sidebarMinW {
			sw = sidebarMinW
		}
		if sw > sidebarMaxW {
			sw = sidebarMaxW
		}
		if sw > width-20 {
			sw = width - 20
		}
		layout.Sidebar = Rect{X: 0, Y: mainY, Width: sw, Height: mainH}
		transcrX = sw
		transcrW = width - sw
	}

	if transcrW < 10 {
		transcrW = width
		transcrX = 0
	}

	layout.Transcr = Rect{X: transcrX, Y: mainY, Width: transcrW, Height: mainH}

	return layout
}
