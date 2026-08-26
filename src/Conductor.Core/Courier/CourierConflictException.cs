namespace Conductor.Core.Courier;

/// <summary>Somebody else is already consuming this source's messages.
///
/// <para>Findings §6.9's transition, in one type. Telegram allows exactly one <c>getUpdates</c>
/// consumer per bot token: the day the courier takes the token, any plan whose messenger block still
/// polls in-run fights it, and the two steal each other's updates. The daemon has to back off and
/// say so rather than treat it as one more transport hiccup — but it must do that WITHOUT knowing
/// which messenger imposed the rule, which is why the source translates its own 409 into this
/// instead of letting an adapter exception cross the seam.</para></summary>
public sealed class CourierConflictException : InvalidOperationException
{
    public CourierConflictException() { }
    public CourierConflictException(string message) : base(message) { }
    public CourierConflictException(string message, Exception innerException) : base(message, innerException) { }
}
