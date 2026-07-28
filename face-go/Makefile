.PHONY: build run run-demo test test-golden fmt lint clean

BINARY := bin/conductor-face.exe
MAIN   := ./cmd/conductor-face/

build:
	go build -o $(BINARY) $(MAIN)

run: build
	./$(BINARY)

run-demo: build
	./$(BINARY) --demo

test:
	go test ./... -count=1 -timeout 30s

test-golden:
	go test ./internal/tui/ -run TestGolden -update

fmt:
	go fmt ./...

lint:
	golangci-lint run ./...

clean:
	rm -rf bin/

all: fmt lint test build
