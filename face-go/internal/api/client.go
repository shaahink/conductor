package api

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
	"sync/atomic"
	"time"
)

type liveSource struct {
	baseURL    string
	httpClient *http.Client
	ctx        context.Context
	cancel     context.CancelFunc

	lastEventSeq   atomic.Int64
	lastTxSeq      atomic.Int64
	lastConsoleSeq atomic.Int64
}

func NewLiveSource(baseURL string) DataSource {
	ctx, cancel := context.WithCancel(context.Background())
	return &liveSource{
		baseURL: strings.TrimRight(baseURL, "/"),
		httpClient: &http.Client{
			Timeout: 10 * time.Second,
		},
		ctx:    ctx,
		cancel: cancel,
	}
}

func (s *liveSource) get(path string) (*http.Response, error) {
	req, err := http.NewRequestWithContext(s.ctx, "GET", s.baseURL+path, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("Accept", "application/json")
	return s.httpClient.Do(req)
}

func (s *liveSource) getJSON(path string, v any) error {
	resp, err := s.get(path)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode >= 400 {
		body, _ := io.ReadAll(io.LimitReader(resp.Body, 1024))
		return fmt.Errorf("GET %s: %d %s", path, resp.StatusCode, string(body))
	}
	return json.NewDecoder(resp.Body).Decode(v)
}

func (s *liveSource) postJSON(path string, body any, v any) error {
	b, err := json.Marshal(body)
	if err != nil {
		return err
	}
	req, err := http.NewRequestWithContext(s.ctx, "POST", s.baseURL+path, bytes.NewReader(b))
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Accept", "application/json")
	resp, err := s.httpClient.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode >= 400 {
		body, _ := io.ReadAll(io.LimitReader(resp.Body, 1024))
		return fmt.Errorf("POST %s: %d %s", path, resp.StatusCode, string(body))
	}
	if v != nil {
		return json.NewDecoder(resp.Body).Decode(v)
	}
	return nil
}

func (s *liveSource) FetchState() (*StateDto, error) {
	var state StateDto
	if err := s.getJSON("/state", &state); err != nil {
		return nil, err
	}
	return &state, nil
}

func (s *liveSource) FetchTasks() (*TasksDto, error) {
	var tasks TasksDto
	if err := s.getJSON("/tasks", &tasks); err != nil {
		return nil, err
	}
	return &tasks, nil
}

func (s *liveSource) FetchProcesses() (*ProcessesDto, error) {
	var procs ProcessesDto
	if err := s.getJSON("/processes", &procs); err != nil {
		return nil, err
	}
	return &procs, nil
}

func (s *liveSource) FetchSessions() (*SessionsDto, error) {
	var sessions SessionsDto
	if err := s.getJSON("/sessions", &sessions); err != nil {
		return nil, err
	}
	return &sessions, nil
}

func (s *liveSource) FetchTimeline() (*TimelineDto, error) {
	var timeline TimelineDto
	if err := s.getJSON("/timeline", &timeline); err != nil {
		return nil, err
	}
	return &timeline, nil
}

func (s *liveSource) FetchLedger() (*LedgerDto, error) {
	var ledger LedgerDto
	if err := s.getJSON("/ledger", &ledger); err != nil {
		return nil, err
	}
	return &ledger, nil
}

func (s *liveSource) FetchBugs() (*BugsDto, error) {
	var bugs BugsDto
	if err := s.getJSON("/bugs", &bugs); err != nil {
		return nil, err
	}
	return &bugs, nil
}

func (s *liveSource) FetchPromptPreview(stageId, kind string) (*PromptPreviewDto, error) {
	path := "/prompt/preview?stage=" + urlEncode(stageId) + "&kind=" + urlEncode(kind)
	var preview PromptPreviewDto
	if err := s.getJSON(path, &preview); err != nil {
		return nil, err
	}
	return &preview, nil
}

func (s *liveSource) QueryReport(sql string) (*QueryResultDto, error) {
	path := "/report/query?sql=" + urlEncode(sql)
	var result QueryResultDto
	if err := s.getJSON(path, &result); err != nil {
		return nil, err
	}
	return &result, nil
}

func (s *liveSource) PostControl(cmd ControlRequestDto) (*ControlAcceptedDto, error) {
	var accepted ControlAcceptedDto
	if err := s.postJSON("/control", cmd, &accepted); err != nil {
		return nil, err
	}
	return &accepted, nil
}

func (s *liveSource) PostInject(req InjectRequestDto) (*InjectAcceptedDto, error) {
	var accepted InjectAcceptedDto
	if err := s.postJSON("/inject", req, &accepted); err != nil {
		return nil, err
	}
	return &accepted, nil
}

func (s *liveSource) FetchPlan() (*PlanDto, error) {
	var plan PlanDto
	if err := s.getJSON("/plan", &plan); err != nil {
		return nil, err
	}
	return &plan, nil
}

func (s *liveSource) PostPlanEdit(req PlanEditRequestDto) (*PlanMutationResultDto, error) {
	// A rejected edit answers 400 with a body — decode it rather than surfacing a raw HTTP error, so
	// the TUI can show the engine's reason ("unknown stage", "would make the plan invalid: …").
	var res PlanMutationResultDto
	if err := s.postJSONAllowError("/plan/edit", req, &res); err != nil {
		return nil, err
	}
	return &res, nil
}

func (s *liveSource) PostPlanImport(req PlanImportRequestDto) (*PlanImportResultDto, error) {
	var res PlanImportResultDto
	if err := s.postJSONAllowError("/plan/import", req, &res); err != nil {
		return nil, err
	}
	return &res, nil
}

// postJSONAllowError posts and decodes the response body even on a 4xx (the plan endpoints return a
// structured {ok,error,…} on rejection); only a transport error or a 5xx surfaces as a Go error.
func (s *liveSource) postJSONAllowError(path string, body any, v any) error {
	b, err := json.Marshal(body)
	if err != nil {
		return err
	}
	req, err := http.NewRequestWithContext(s.ctx, "POST", s.baseURL+path, bytes.NewReader(b))
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Accept", "application/json")
	resp, err := s.httpClient.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode >= 500 {
		msg, _ := io.ReadAll(io.LimitReader(resp.Body, 1024))
		return fmt.Errorf("POST %s: %d %s", path, resp.StatusCode, string(msg))
	}
	return json.NewDecoder(resp.Body).Decode(v)
}

func (s *liveSource) SubscribeEvents(onEvent func(ConductorEventDto), onConnected func(bool)) func() {
	return SubscribeEvents(s.ctx, s.baseURL, func(e ConductorEventDto) {
		if e.Seq > 0 {
			s.lastEventSeq.Store(e.Seq)
		}
		onEvent(e)
	}, onConnected, func() int64 { return s.lastEventSeq.Load() })
}

func (s *liveSource) SubscribeTranscript(onLine func(TranscriptLineDto), onConnected func(bool)) func() {
	return SubscribeTranscript(s.ctx, s.baseURL, func(l TranscriptLineDto) {
		if l.Seq > 0 {
			s.lastTxSeq.Store(l.Seq)
		}
		onLine(l)
	}, onConnected, func() int64 { return s.lastTxSeq.Load() })
}

func (s *liveSource) SubscribeConsole(onLine func(ConsoleLineDto), onConnected func(bool)) func() {
	return SubscribeConsole(s.ctx, s.baseURL, func(l ConsoleLineDto) {
		if l.Seq > 0 {
			s.lastConsoleSeq.Store(l.Seq)
		}
		onLine(l)
	}, onConnected, func() int64 { return s.lastConsoleSeq.Load() })
}

func (s *liveSource) Close() {
	s.cancel()
}

func urlEncode(s string) string {
	var buf bytes.Buffer
	for i := 0; i < len(s); i++ {
		c := s[i]
		if (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
			c == '-' || c == '_' || c == '.' || c == '~' {
			buf.WriteByte(c)
		} else {
			buf.WriteByte('%')
			hi := c >> 4
			lo := c & 0x0f
			buf.WriteByte("0123456789ABCDEF"[hi])
			buf.WriteByte("0123456789ABCDEF"[lo])
		}
	}
	return buf.String()
}
