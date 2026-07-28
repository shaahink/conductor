using Spectre.Console.Cli;

namespace Conductor.Commands;

public sealed class SkipCommand() : CtlCommand("skip", "the current stage will be skipped and flagged for review", dangerous: true);
