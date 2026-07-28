using Spectre.Console.Cli;

namespace Conductor.Commands;

public sealed class ApproveCommand() : CtlCommand("approve", "approve the currently owner-gated stage so the conductor advances past it");
