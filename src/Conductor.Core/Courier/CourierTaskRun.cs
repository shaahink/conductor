using System.Globalization;

namespace Conductor.Core.Courier;

/// <summary>PK2.1 / D2(b) - what the scheduler remembers of the task's last run.
///
/// <para><b>Measured, and it limits what this can prove:</b> while an instance runs, Last Result reads
/// <c>267009</c> (0x41301, "the task is currently running"), and Last Run Time is that instance's start.
/// A courier restarted BY ITS OWN TASK therefore finds its predecessor's exit code already overwritten
/// by its own start; the number is only the predecessor's when something other than the task started
/// the new one. The description says so instead of passing 267009 off as a cause.</para></summary>
/// <param name="LastRunTime">As the scheduler printed it, in the machine's locale; "N/A" when never run.</param>
/// <param name="LastResult">The exit code, or a scheduler status code.</param>
public sealed record CourierTaskRun(string LastRunTime, long LastResult)
{
    /// <summary>SCHED_S_TASK_RUNNING.</summary>
    public const long Running = 0x41301;

    /// <summary>SCHED_S_TASK_HAS_NOT_RUN.</summary>
    public const long NeverRun = 0x41303;

    public string Describe()
    {
        var code = LastResult.ToString(CultureInfo.InvariantCulture)
                 + " (0x" + unchecked((uint)LastResult).ToString("X8", CultureInfo.InvariantCulture) + ")";
        var meaning = LastResult switch
        {
            Running => ", running now - the task's current instance; the previous exit code is not kept",
            NeverRun => ", the task has not run",
            _ => "",
        };
        return code + meaning + " at " + LastRunTime;
    }
}
