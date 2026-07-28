package tui

import (
	"testing"

	"conductor-face-go/internal/api"
)

func newProcessModel(procs []api.ProcessDto) Model {
	m := New(api.NewDemoSource(), true, "(demo)")
	m.data.Processes = procs
	m.tab = TabProcesses
	m.processSelected = 0
	return m
}

func TestProcessKillConfirmPostsForSelectedPid(t *testing.T) {
	m := newProcessModel([]api.ProcessDto{{Pid: 4512, Purpose: "session", Alive: true}})

	tm, _ := m.handleProcessesKey("x")
	m = asModel(tm)
	if !m.processKilling {
		t.Fatal("x should open the kill confirm for a live process")
	}

	tm, cmd := m.handleProcessesKey("y")
	m = asModel(tm)
	if m.processKilling {
		t.Error("y should close the confirm")
	}
	if cmd == nil {
		t.Fatal("confirming should post a kill")
	}
	killed, ok := cmd().(MsgProcessKilled)
	if !ok || killed.Pid != 4512 {
		t.Fatalf("expected a kill posted for pid 4512, got %#v", killed)
	}
}

func TestProcessKillCancelPostsNothing(t *testing.T) {
	m := newProcessModel([]api.ProcessDto{{Pid: 4512, Alive: true}})

	tm, _ := m.handleProcessesKey("x")
	m = asModel(tm)
	tm, cmd := m.handleProcessesKey("n")
	m = asModel(tm)
	if m.processKilling {
		t.Error("n should close the confirm")
	}
	if cmd != nil {
		t.Error("cancelling the kill must not post anything")
	}
}

func TestProcessKillIgnoredForExitedProcess(t *testing.T) {
	m := newProcessModel([]api.ProcessDto{{Pid: 8723, Alive: false}})

	tm, _ := m.handleProcessesKey("x")
	if asModel(tm).processKilling {
		t.Error("x on an already-exited process must not open the confirm")
	}
}
