namespace Conductor.Core.Courier;

/// <summary>PK3.3 / D4 - a live run naming its own project to the courier: <c>POST /hello</c>.
///
/// <para>F-COUR-4: the allowlist keys on the plan's NAME, so a new plan in a repository the courier
/// already files for was a project nobody could send a note to until the owner remembered
/// <c>courier allow --repo --plan</c> - and pdf-challenge ran for days without it. The run knows its
/// own name and checkout; it says them, and the courier adds the entry, marked as the run's.</para>
///
/// <para><b>Security note, recorded with the decision.</b> The allowlist exists so a daemon holding the
/// bot token cannot be made to write into arbitrary checkouts on this disk. A run already has write
/// access to its own checkout - it is running there - and the request carries the install's shared
/// secret like every other verb, so a hello adds no reach that the caller did not already have.
/// <c>courier allow</c> is unchanged and stays the way to allow a project with no run live.</para></summary>
/// <param name="Repo">The run's checkout, as a full path. Must be a directory on this machine.</param>
/// <param name="Plan">The plan's name - what the identity line on every push carries and what a reply
/// is routed by.</param>
/// <param name="RunId">The run, for the entry's <c>by</c> marker.</param>
/// <param name="Protocol">What the run speaks. A newer one is refused by name.</param>
public sealed record CourierHello(string Repo, string Plan, string? RunId = null,
    int Protocol = CourierProtocol.Version);
