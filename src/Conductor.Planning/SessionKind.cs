namespace Conductor.Planning;

/// <summary>The kinds of session a workflow can schedule. Shared vocabulary between the planning
/// library (which decides what runs next) and any consumer that executes those decisions — moved
/// here from the engine's Models in P0 so the library needs no engine reference.</summary>
public enum SessionKind { Deliver, Fix, Resume, Audit, Verify }
