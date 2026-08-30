# agents-md-review

A prompt that puts your global agent instructions file on trial.

`CLAUDE.md`, `AGENTS.md` and their equivalents grow by accretion. Every "session learning"
gets a paragraph, nothing ever leaves, and the file loads into every conversation forever. This
prompt asks the agent to review that file adversarially, line by line, with delete as the
default verdict, and to hand back a rewrite plus the enforcement changes (hooks, tools, skills)
that make the cut safe.

## What it produces

1. A verdict on the whole file: letter or manual, and the honest line count.
2. A line-by-line table with KEEP / CUT / MOVE and a one-line reason. MOVE names the
   destination.
3. A glossary of the words you use, with guesses marked.
4. The rewritten file, in full, written to a scratch location.
5. A ranked list of the risky cuts and the signal that would justify putting each back.

## How to use it

1. Open [PROMPT.md](./PROMPT.md) and fill in the `Files` section with your own paths and line
   counts.
2. Paste it into a coding agent session that can read your home directory and, ideally, the git
   history of the file. The "has it been earned" test depends on that history; without it the
   agent has to guess.
3. Read the table before the rewrite. The table is where the argument is.
4. Apply the rewrite yourself, or ask the agent to apply it and open the enforcement changes it
   named.

## What happened when I ran it

My fleet-wide file was 171 lines. The review found five rules with a recorded incident behind
them, two contradictions with the file's own single source of truth, one naming convention that
was already dead on disk, and a batch of "session learnings" added over two days that no
incident ever justified. The rewrite is 50 lines: a short letter, a glossary, and the five
rules. The cut only held because six small mechanisms replaced the deleted prose: a sync script
that globs a docs folder instead of a hand-maintained manifest, a PreToolUse hook that blocks a
raw `open <url>`, two skills that now point at a glossary doc, one skill that carries its own
usage constraint, and a pre-push guard that checks a stacked PR's base branch.

## Notes

- The prompt is written for a *global* (user-level) file and asks the agent to engage with the
  difference between that and a per-project file. It works for per-project files too; delete the
  caveat paragraph.
- The last ground rule assumes you have a skill for stripping AI writing patterns. If you do
  not, the plain-prose instruction still applies.

## License

MIT, same as the rest of this repo.
