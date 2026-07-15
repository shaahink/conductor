namespace Conductor.Core.Http;

// Kill a supervised child process from the Face's Procs tab (POST /processes/kill). Only a PID this
// run tracked and still alive can be killed — never an arbitrary system process, never conductor
// itself. Mirrors the `conductor bg stop <pid>` CLI path.

public sealed record ProcessKillRequestDto(int Pid);

public sealed record ProcessKillResultDto(bool Ok, string? Error, int Pid);
