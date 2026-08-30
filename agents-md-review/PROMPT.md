# Adversarial review of my agent instructions file

Paste this into a coding agent (Claude Code, Codex, Cursor, whatever reads your instructions
file) from a session that can read your home directory. Replace the placeholders in the
`Files` section first. Everything else works as written.

---

Adversarially review my agent instructions. I think they are far too verbose and that most of
the content should be deleted. Your job is to prove or disprove that, line by line, and show me
the cut.

## Files

- `<PATH_TO_GLOBAL_INSTRUCTIONS>` (`<N>` lines) - the file every session loads, for example
  `~/.claude/CLAUDE.md`, `~/.codex/AGENTS.md`, or a shared `~/.agents/AGENTS.md` that both
  import
- `<PATH_TO_RUNTIME_SPECIFIC_FILE>` (`<N>` lines) - optional: a thin runtime-only file that
  imports the one above

Read them. They load into every session on this machine, so every line costs context in every
conversation, forever.

## The standard to review against

An instructions file is **a letter to the agent, not a config file or a rulebook**. It tells the
agent how I think, what I am building, and why, so it makes fewer bad assumptions without me
repeating myself.

**In:** project framing and philosophy as prose. A glossary defining the words I use, which is
the highest-leverage part. Optionally a few small rules at the bottom, and even those are
suspect.

**Out:** file paths, because the model should explore. Technical decisions and enforcement
rules, because those belong in code, tests, linters or hooks. Giant global files of everything
you like.

**How it grows:** start small, append a line only when you catch the model making the same
mistake repeatedly. That earned line beats a page of speculative rules. If a rule never earns
its keep, delete it.

**One caveat you must engage with rather than ignore.** That standard describes a
*per-project* file. Mine is *global* (user-level, loaded in every project). Do not apply the
rules mechanically. Tell me which of them genuinely transfer to a user-level file, which do
not, and why. If your honest read is that a global file is the wrong artefact entirely and the
content belongs in per-project files, hooks, or skills, say that plainly.

## How to review

Be adversarial. The default verdict for any line is **delete**. Every line has to earn its place
against these tests:

1. **Has it been earned?** Was this written because a model actually made this mistake, or
   because I imagined it might? Speculative rules are the main thing I want cut. Use the git
   history of the file and any notes or memory you can find to decide; do not guess.
2. **Is it a rule or is it enforcement?** Anything a hook, linter, test or CI check could
   enforce should not be in a letter. Name the mechanism that should own it instead.
3. **Would a competent agent do this anyway?** If yes, cut it. I am not interested in
   restating good practice.
4. **Is it reference, not instruction?** Facts an agent could look up, or that belong in a
   config doc it reads on demand, do not belong loaded into every session.
5. **Is it project-specific hiding in a global file?** Rules that only apply to one repo belong
   in that repo.
6. **Does it survive its own advice?** If the file tells agents how to write, judge the file by
   that standard and quote where it fails.

## What I want back

1. **A verdict on the whole thing** in a few sentences. Is this a letter or a manual? What is
   the honest line count it should be?

2. **A line-by-line table**: quote (or line range), verdict of KEEP / CUT / MOVE, and one line
   of reasoning. For MOVE, name the destination: a hook, a project-level file, a skill, a
   config doc read on demand.

3. **The glossary I do not have** (or the one I have, audited). This is the highest-leverage
   part. Draft one from what you can infer about the words I use, and mark anything you are
   guessing at.

4. **The rewrite.** The actual file you think I should have, in full, ready to use. Not a
   description of it. Write it to a scratch location, not over the live file.

5. **What you would put back after a month**, if my instinct to delete turns out to be wrong.
   Which cuts are the risky ones, and what signal would justify restoring each.

## Ground rules

- Quote what you are cutting. Do not summarise your way past a decision.
- Argue against my premise if you think I am wrong. If the file is roughly right and my instinct
  to delete is the error, say so and defend it.
- Do not preserve a line just because it reads well or because deleting it feels destructive.
- Plain prose. No em dashes, no bold-label bullet lists, no hedging. If you have a skill for
  removing AI writing patterns, apply it to your own output.
