using Spectre.Console.Cli;

namespace Conductor.Commands;

public sealed class KillCommand() : CtlCommand("kill", "the current agent session will be killed (conductor keeps running)", dangerous: true);
