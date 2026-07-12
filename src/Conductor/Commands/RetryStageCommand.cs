using Spectre.Console.Cli;

namespace Conductor.Commands;

public sealed class RetryStageCommand() : CtlCommand("retry-stage", "reset the attempt counter and re-queue a deliver session for the current stage");
