using Spectre.Console.Cli;

namespace Conductor.Commands;

public sealed class RollbackCommand() : CtlCommand("rollback", "reset the working tree to the stage's checkpoint commit (refuses if dirty)", dangerous: true);
