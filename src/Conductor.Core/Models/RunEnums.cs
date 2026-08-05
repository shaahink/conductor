using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conductor.Models;

// SC5.1 added Waiting: the run is asleep on an agent-declared blocked-until window. It is neither
// Idle (nothing is pending) nor Paused (a human stopped it) — appended at the end so the ordinal of
// every pre-existing member is unchanged for any state.json written before SC5.1.
public enum RunStatus { Idle, Running, VerifyingGates, Backoff, Paused, NeedsHuman, AwaitingOwner, Completed, Aborted, Waiting }

// SessionKind moved to Conductor.Planning (P0) — shared vocabulary the planning library owns.

public enum AwaitingOwnerReason { OwnerGate, ApprovalMode, Budget }
