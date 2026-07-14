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

func subscribeSSE(ctx context.Context, url string, handler sseHandler, onConnected func(bool)) func() {
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

			err := readSSEStream(ctx, url, handler)
			if err != nil {
				onConnected(false)
				if bIdx < len(backoff)-1 {
					bIdx++
				}
				select {
				case <-ctx.Done():
					return
				case <-time.After(backoff[bIdx] * time.Millisecond):
				}
				continue
			}
			onConnected(true)
			bIdx = 0
		}
	}()
	return func() {
		<-done
	}
}

func readSSEStream(ctx context.Context, url string, handler sseHandler) error {
	req, err := http.NewRequestWithContext(ctx, "GET", url, nil)
	if err != nil {
		return err
	}
	req.Header.Set("Accept", "text/event-stream")
	req.Header.Set("Cache-Control", "no-cache")

	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()

	if resp.StatusCode != 200 {
		return fmt.Errorf("SSE %s: status %d", url, resp.StatusCode)
	}

	reader := bufio.NewReader(resp.Body)
	for {
		select {
		case <-ctx.Done():
			return ctx.Err()
		default:
		}

		line, err := reader.ReadString('\n')
		if err != nil {
			if err == io.EOF {
				return nil
			}
			return err
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

func SubscribeEvents(ctx context.Context, baseURL string, onEvent func(ConductorEventDto), onConnected func(bool)) func() {
	return subscribeSSE(ctx, baseURL+"/events", func(data []byte) {
		var event ConductorEventDto
		if err := json.Unmarshal(data, &event); err != nil {
			return
		}
		onEvent(event)
	}, onConnected)
}

func SubscribeTranscript(ctx context.Context, baseURL string, onLine func(TranscriptLineDto), onConnected func(bool)) func() {
	return subscribeSSE(ctx, baseURL+"/transcript/current", func(data []byte) {
		var line TranscriptLineDto
		if err := json.Unmarshal(data, &line); err != nil {
			return
		}
		onLine(line)
	}, onConnected)
}
