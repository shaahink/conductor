using Spectre.Console.Cli;

namespace Conductor.Commands;

public sealed class HeartbeatCommand() : CtlCommand("heartbeat", "the running conductor refreshes .conductor/REPORT.md now (only while a session is live)");
