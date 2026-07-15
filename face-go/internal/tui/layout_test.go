package tui

import "testing"

func TestComputeLayout(t *testing.T) {
	tests := []struct {
		name         string
		width        int
		height       int
		collapsed    bool
		wantContentH int
		wantSidebarW int
	}{
		{"120x40 sidebar", 120, 40, false, 37, 31},
		{"120x40 collapsed", 120, 40, true, 37, 0},
		{"80x24 sidebar", 80, 24, false, 21, 22},
		{"80x24 collapsed", 80, 24, true, 21, 0},
		{"tiny returns zero", 20, 6, false, 0, 0},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			l := ComputeLayout(tc.width, tc.height, tc.collapsed)
			if tc.wantContentH == 0 {
				if l.Content.Height != 0 {
					t.Errorf("expected zero layout, got content height %d", l.Content.Height)
				}
				return
			}
			if l.Top.Height != 1 || l.Tabs.Height != 1 || l.Bottom.Height != 1 {
				t.Errorf("chrome rows: top=%d tabs=%d bottom=%d (want 1,1,1)", l.Top.Height, l.Tabs.Height, l.Bottom.Height)
			}
			if l.Content.Height != tc.wantContentH {
				t.Errorf("content height: want %d, got %d", tc.wantContentH, l.Content.Height)
			}
			if l.Sidebar.Width != tc.wantSidebarW {
				t.Errorf("sidebar width: want %d, got %d", tc.wantSidebarW, l.Sidebar.Width)
			}
			if l.Sidebar.Width+l.Content.Width != tc.width {
				t.Errorf("widths don't sum: sidebar %d + content %d != %d", l.Sidebar.Width, l.Content.Width, tc.width)
			}
		})
	}
}

func TestLayoutSidebarBounds(t *testing.T) {
	if w := ComputeLayout(200, 40, false).Sidebar.Width; w > sidebarMaxW {
		t.Errorf("sidebar width %d exceeds max %d", w, sidebarMaxW)
	}
	if w := ComputeLayout(80, 40, false).Sidebar.Width; w < sidebarMinW {
		t.Errorf("sidebar width %d below min %d", w, sidebarMinW)
	}
}
