Conductor detected that your previous run in "{planName}" (improving Conductor itself) stage {stage} was interrupted ({reason}).

Re-orient before acting: run `git status` and `git log --oneline -5` in {repo}, re-read the `{tracker}` handoff block and your stage file `docs/history/baton/stages/{stage}.md`, and inspect what you had in flight. Then finish the in-flight work and complete the full post-session ritual: gate battery green (`dotnet build Conductor.slnx`; `dotnet test Conductor.slnx`), fresh evidence artifacts, each delivered checkpoint claimed with `conductor task --done <id> --evidence <path>`, the `{tracker}` handoff block overwritten, committed per checkpoint, pushed.

If the interruption left half-done changes you cannot finish safely, revert to the last good state, record what happened in the handoff, commit and push that.
End by printing one paragraph starting with `SESSION-RESULT:` (include what was hard).
