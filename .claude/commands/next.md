---
description: Start or resume the current task from progress.md (use after /clear)
allowed-tools: Bash(cat progress.md)
argument-hint: "[optional task id, e.g. T0.3]"
---
!`cat progress.md`

Follow CLAUDE.md section 1 ("Start of every session"), using the progress.md content above. Don't read it again.

- Task to work on: $ARGUMENTS if given. Otherwise the `Now:` task if it isn't done, or the `Next:` task if it is.
- If it was in progress, resume after the last milestone recorded in the log.
- If it's a USER TEST gate, tell Egor which manual.md gate to run and wait for his results.

Start with one line, "Working on Tx.y: <title>, starting at m<n>", then begin. Follow CLAUDE.md sections 2–4 for delegation, milestones and stopping at the end of the task.
