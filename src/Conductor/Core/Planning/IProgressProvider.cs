using Conductor.Models;

namespace Conductor.Core.Planning;

/// <summary>
/// Reads a plan's progress state (checkpoint rows + handoff block) into a <see cref="TrackerSnapshot"/>.
/// This is the decoupling seam (F-1, D-2): the engine depends on this abstraction, never on a specific
/// tracker shape. The default <see cref="MarkdownTableProvider"/> preserves Conductor's original
/// <c>TRACKER.md</c> parsing byte-for-byte; alternate providers (script, plan-declared) are the escape
/// hatch delivered in B1.3.
/// </summary>
public interface IProgressProvider
{
    /// <summary>Stable provider id used for plan-config selection and diagnostics.</summary>
    string Name { get; }

    /// <summary>Read the current progress snapshot for the given plan.</summary>
    TrackerSnapshot Read(PlanConfig plan);
}
