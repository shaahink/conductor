using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conductor.Models;

public enum RunStatus { Idle, Running, VerifyingGates, Backoff, Paused, NeedsHuman, AwaitingOwner, Completed, Aborted }

// SessionKind moved to Conductor.Planning (P0) — shared vocabulary the planning library owns.

public enum AwaitingOwnerReason { OwnerGate, ApprovalMode, Budget }
