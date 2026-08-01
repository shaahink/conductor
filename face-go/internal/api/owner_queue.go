package api

// SF4.1/SF4.2: the owner queue on the wire (GET /owner/queue) — the same entries the engine writes
// to `.conductor/OWNER-QUEUE.md`, so the file and the Face can never disagree about what the owner
// owes. Mirrors Core/Http/ControlPlaneDto.OwnerQueue.cs.

// OwnerQueueItemDto is one obligation only the owner can clear.
//
// AgeSeconds is a POINTER on purpose, and the engine writes it explicitly as null rather than
// omitting it: a plain int64 would decode an absent key as 0 — "just now" — for an obligation the
// engine cannot date at all (tracker rows carry no timestamp). Anything rendering age must branch on
// nil and say "unknown"; see ownerAge in the tui package.
type OwnerQueueItemDto struct {
	// Id is stable for the same obligation across polls, so a face can diff one render against the next.
	Id string `json:"id"`
	// Kind is one of human, ownerGate, park, wait, checkpoint, skippedStage.
	Kind  string `json:"kind"`
	Title string `json:"title"`
	// Unblocks is what moves once this is cleared — the half a hand-written list always forgets.
	Unblocks string `json:"unblocks"`
	// Command is empty when nothing the owner types clears the entry (a blocked-until wait clears itself).
	Command    string  `json:"command"`
	SinceUtc   *string `json:"sinceUtc"`
	AgeSeconds *int64  `json:"ageSeconds"`
	Detail     *string `json:"detail"`
}

// OwnerQueueDto is GET /owner/queue. Count zero is a real answer — the queue was computed and nothing
// is owed — which is why the Face says so out loud instead of hiding the section.
type OwnerQueueDto struct {
	Count        int                 `json:"count"`
	GeneratedUtc string              `json:"generatedUtc"`
	Items        []OwnerQueueItemDto `json:"items"`
}

func (s *liveSource) FetchOwnerQueue() (*OwnerQueueDto, error) {
	var q OwnerQueueDto
	if err := s.getJSON("/owner/queue", &q); err != nil {
		return nil, err
	}
	return &q, nil
}
