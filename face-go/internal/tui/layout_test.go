package tui

import (
	"testing"
)

func TestComputeLayout(t *testing.T) {
	tests := []struct {
		name         string
		width        int
		height       int
		sidebarOpen  bool
		wantTickerH  int
		wantFooterH  int
		wantMainH    int
		wantSidebarW int
	}{
		{
			name:  "standard 120x40 no sidebar",
			width: 120, height: 40, sidebarOpen: false,
			wantTickerH: 1, wantFooterH: 1, wantMainH: 38, wantSidebarW: 0,
		},
		{
			name:  "standard 120x40 with sidebar",
			width: 120, height: 40, sidebarOpen: true,
			wantTickerH: 1, wantFooterH: 1, wantMainH: 38, wantSidebarW: 33,
		},
		{
			name:  "narrow 80x24 no sidebar",
			width: 80, height: 24, sidebarOpen: false,
			wantTickerH: 1, wantFooterH: 1, wantMainH: 22, wantSidebarW: 0,
		},
		{
			name:  "narrow 80x24 with sidebar",
			width: 80, height: 24, sidebarOpen: true,
			wantTickerH: 1, wantFooterH: 1, wantMainH: 22, wantSidebarW: 24,
		},
		{
			name:  "tiny terminal returns zero layout",
			width: 20, height: 6, sidebarOpen: false,
			wantTickerH: 0, wantFooterH: 0, wantMainH: 0, wantSidebarW: 0,
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			layout := ComputeLayout(tc.width, tc.height, tc.sidebarOpen)

			if layout.Ticker.Height != tc.wantTickerH {
				t.Errorf("ticker height: want %d, got %d", tc.wantTickerH, layout.Ticker.Height)
			}
			if layout.Footer.Height != tc.wantFooterH {
				t.Errorf("footer height: want %d, got %d", tc.wantFooterH, layout.Footer.Height)
			}
			if layout.Main.Height != tc.wantMainH {
				t.Errorf("main height: want %d, got %d", tc.wantMainH, layout.Main.Height)
			}
			if layout.Sidebar.Width != tc.wantSidebarW {
				t.Errorf("sidebar width: want %d, got %d", tc.wantSidebarW, layout.Sidebar.Width)
			}

			if layout.Main.Height+layout.Ticker.Height+layout.Footer.Height != tc.height &&
				layout.Main.Height > 0 {
				t.Errorf("total height doesn't match: %d + %d + %d != %d",
					layout.Main.Height, layout.Ticker.Height, layout.Footer.Height, tc.height)
			}

			if tc.sidebarOpen && layout.Transcr.Width+layout.Sidebar.Width != tc.width &&
				layout.Main.Height > 0 {
				t.Errorf("widths don't add up: transcript %d + sidebar %d != %d",
					layout.Transcr.Width, layout.Sidebar.Width, tc.width)
			}
		})
	}
}

func TestLayoutSidebarMaxWidth(t *testing.T) {
	layout := ComputeLayout(200, 40, true)
	if layout.Sidebar.Width > sidebarMaxW {
		t.Errorf("sidebar width %d exceeds max %d", layout.Sidebar.Width, sidebarMaxW)
	}
}

func TestLayoutSidebarMinWidth(t *testing.T) {
	layout := ComputeLayout(80, 40, true)
	if layout.Sidebar.Width < sidebarMinW {
		t.Errorf("sidebar width %d below min %d", layout.Sidebar.Width, sidebarMinW)
	}
}
