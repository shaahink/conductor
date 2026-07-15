package api

import (
	"bufio"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"
)

type sseHandler func(data []byte)

// subscribeSSE reconnects with backoff on any drop. since, if non-nil, is consulted on every
// (re)connect attempt and appended as a ?since= query param so a reconnect resumes from the
// last-seen seq instead of replaying the whole backlog — the server honors this for both /events
// and /transcript/current (ControlPlaneServer.Endpoints.cs ParseSince).
func subscribeSSE(ctx context.Context, url string, handler sseHandler, onConnected func(bool), since func() int64) func() {
	done := make(chan struct{})
	go func() {
		defer close(done)
		backoff := []time.Duration{500, 1000, 2000, 4000, 8000, 8000, 8000}
		bIdx := 0

		for {
			select {
			case <-ctx.Done():
				return
			default:
			}

			target := url
			if since != nil {
				if s := since(); s > 0 {
					sep := "?"
					if strings.Contains(url, "?") {
						sep = "&"
					}
					target = fmt.Sprintf("%s%ssince=%d", url, sep, s)
				}
			}

			connected, err := readSSEStream(ctx, target, handler, onConnected)
			onConnected(false)
			if connected {
				bIdx = 0
			}
			if ctx.Err() != nil {
				return
			}
			_ = err

			select {
			case <-ctx.Done():
				return
			case <-time.After(backoff[bIdx] * time.Millisecond):
			}
			if bIdx < len(backoff)-1 {
				bIdx++
			}
		}
	}()
	return func() {
		<-done
	}
}

// readSSEStream returns connected=true if the HTTP connection was established (status 200), even
// if it was later dropped mid-stream — the caller uses that to decide whether to reset backoff.
func readSSEStream(ctx context.Context, url string, handler sseHandler, onConnected func(bool)) (connected bool, err error) {
	req, err := http.NewRequestWithContext(ctx, "GET", url, nil)
	if err != nil {
		return false, err
	}
	req.Header.Set("Accept", "text/event-stream")
	req.Header.Set("Cache-Control", "no-cache")

	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return false, err
	}
	defer resp.Body.Close()

	if resp.StatusCode != 200 {
		return false, fmt.Errorf("SSE %s: status %d", url, resp.StatusCode)
	}

	onConnected(true)

	reader := bufio.NewReader(resp.Body)
	for {
		select {
		case <-ctx.Done():
			return true, ctx.Err()
		default:
		}

		line, err := reader.ReadString('\n')
		if err != nil {
			if err == io.EOF {
				return true, nil
			}
			return true, err
		}
		line = strings.TrimRight(line, "\r\n")
		if line == "" {
			continue
		}
		if strings.HasPrefix(line, "data: ") {
			data := strings.TrimPrefix(line, "data: ")
			handler([]byte(data))
		}
	}
}

func SubscribeEvents(ctx context.Context, baseURL string, onEvent func(ConductorEventDto), onConnected func(bool), since func() int64) func() {
	return subscribeSSE(ctx, baseURL+"/events", func(data []byte) {
		var event ConductorEventDto
		if err := json.Unmarshal(data, &event); err != nil {
			return
		}
		onEvent(event)
	}, onConnected, since)
}

func SubscribeTranscript(ctx context.Context, baseURL string, onLine func(TranscriptLineDto), onConnected func(bool), since func() int64) func() {
	return subscribeSSE(ctx, baseURL+"/transcript/current", func(data []byte) {
		var line TranscriptLineDto
		if err := json.Unmarshal(data, &line); err != nil {
			return
		}
		onLine(line)
	}, onConnected, since)
}
