using Spectre.Console.Cli;

namespace Conductor.Commands;

public sealed class PauseAfterStageCommand() : CtlCommand("pause-after-stage", "park at Paused after the current stage completes rather than advancing");
