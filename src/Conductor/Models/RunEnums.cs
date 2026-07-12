using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conductor.Models;

public enum RunStatus { Idle, Running, VerifyingGates, Backoff, Paused, NeedsHuman, AwaitingOwner, Completed, Aborted }

public enum SessionKind { Deliver, Fix, Resume, Audit, Verify }

public enum AwaitingOwnerReason { OwnerGate, ApprovalMode, Budget }
