---
description: Project status from progress.md (current, previous and next tasks, what's waiting on you)
model: haiku
allowed-tools: Bash(cat progress.md)
---
!`cat progress.md`

Using only the progress.md content above (don't read any other files), reply in this format and keep it short:

**Now:** task ID and title, status, last milestone reached
**Previous:** the last 3 completed tasks, one line each, with the most relevant log note
**Next:** next task ID and title
**Waiting on you:** manual.md IDs (M…/G…) and what each one means in a few words, or "nothing"
**Blocked:** the line, or "nothing"
**Overall:** X of Y tasks done, current phase
**Key decisions:** at most 3 bullets that matter for the current or next task

End with the one thing Egor should type or do next (for example "/next", or "do G0 in manual.md, then paste the output").
