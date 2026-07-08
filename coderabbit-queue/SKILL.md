---
name: coderabbit-queue
description: >-
  Drive every open PR on your GitHub account to a fresh CodeRabbit review,
  patiently working around the Fair-Usage rate limit. A cheap non-LLM poller
  round-robins across your PRs, models the account-level cooldown so it never
  wastes the review "trickle", and keeps a bumpable priority queue cached in your
  profile. Invoke when CodeRabbit reviews are stuck behind "Review limit reached"
  notices, or when you want reviews continually re-kicked until each PR comes back
  clean ("No actionable comments were generated").
---

# coderabbit-queue

CodeRabbit's Fair-Usage limit trickles roughly **one review per window** across your
whole account. Left alone, a PR that got rate-limited never gets re-reviewed unless
something keeps re-posting `@coderabbitai review` after each window opens. This skill
is that something — but as a **cheap, persistent, non-LLM poller**, not an expensive
interactive agent loop.

**Engine:** `lib/crqueue.cs` (a .NET file-app). **Cache:** `~/.claude/coderabbit-queue/queue.json`.

## Prerequisites

- **.NET SDK 10+** — the engine is a [file-based C# app](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-10#run-a-c-file-directly)
  run with `dotnet run crqueue.cs`. Verify with `dotnet --version`.
- **GitHub CLI (`gh`)**, authenticated (`gh auth status`). The tool shells out to `gh`
  for PR discovery, comments, and review state — no token handling of its own.
- **CodeRabbit** installed on the repos whose PRs you want driven.

## What it does each tick

1. **Discovers** your open PRs account-wide (`gh search prs --author=@me --state=open`),
   skipping drafts and anything you've `drop`ped.
2. **Classifies** each PR at its *current head SHA* (head-anchored — a review only counts
   if it covers the current head):
   - `clean` — reviewed at head, "No actionable comments were generated" → **done**.
   - `has-comments` — reviewed at head, actionable comments exist → **done for this head**
     (they need code fixes this tool can't make; it stops retriggering until the SHA changes).
   - `rate-limited` — newest signal is a "Review limit reached" notice; parses
     "Next review available in N" into a wake time.
   - `pending` — we triggered and CodeRabbit hasn't answered yet (within `--grace`, default 20m).
   - `never-reviewed` — needs a trigger (includes rate-limit windows that have elapsed).
3. **Models the account cooldown.** The Fair-Usage cap is per-account, so the tick holds
   **all** triggers until the latest rate-limit window across every PR clears — it never
   burns the trickle on one PR while another is mid-cooldown.
4. **Triggers the next eligible PR**, chosen by **priority (desc) → least-recently-triggered
   → PR number**. That rotation guarantees fairness: no PR gets stuck while others starve.
   Posts `@coderabbitai review` (add `--full` to force `@coderabbitai full review`).
   **Auto-escalates to a full review** when CodeRabbit's newest reply is
   "Review skipped / no new commits to review" — which happens when a head's diff is
   unchanged since the last review (e.g. after a content-neutral rebase), where an
   incremental review is a permanent no-op. Only a full review re-examines the whole diff
   and yields a fresh verdict at the current head.

A PR leaves the active set once it's `clean` or `has-comments`. When every PR is terminal,
`run` exits.

## Install

Drop this directory into your Claude Code skills folder so it's auto-discovered:

```bash
cp -r coderabbit-queue ~/.claude/skills/
```

## Commands

```bash
CRQ=~/.claude/skills/coderabbit-queue/lib/crqueue.cs

dotnet run $CRQ status                    # print the queue (no triggering) — quick reference
dotnet run $CRQ tick                      # one iteration: reconcile + trigger the next eligible PR
dotnet run $CRQ run                       # loop tick until every PR is terminal (the normal way to run it)
dotnet run $CRQ run --full --interval 180 # full reviews, poll every 180s
dotnet run $CRQ bump <owner/repo> <n>     # push a PR to the top of the queue (priority = max+1)
dotnet run $CRQ bump <owner/repo> <n> --priority 5
dotnet run $CRQ add  <owner/repo> <n>     # pin a PR in (also clears an exclude)
dotnet run $CRQ drop <owner/repo> <n>     # exclude a PR from auto-discovery
```

Flags: `--full` (full review vs incremental), `--interval S` (run poll seconds, default 180),
`--max-minutes M` (run safety deadline, default 720), `--grace N` (minutes to wait for a
triggered review before re-eligibility, default 20), `--repos a/b,c/d` (restrict to these repos).

## How to run it

The right home is a **background loop**, not an interactive Claude session — it needs no
model, just `gh`:

```bash
# foreground loop that self-paces (sleeps until the account cooldown clears)
dotnet run ~/.claude/skills/coderabbit-queue/lib/crqueue.cs run
```

Launch it in the background (Claude Code `run_in_background`, `nohup`, tmux, or a cron that
calls `tick` every few minutes). Because it re-derives everything from GitHub each tick, a
`tick` cron and a long-lived `run` are interchangeable — pick one. It self-throttles: most
ticks during a cooldown are a couple of cheap API reads and a no-op.

## Manual reordering

The cache persists `priority`, so you can steer the queue while it runs:

```bash
dotnet run $CRQ bump owner/repo 42   # do PR #42 next when the window opens
dotnet run $CRQ status               # confirm the new order
```

`bump` with no `--priority` sends the PR to the top (max existing priority + 1). Lower a PR
by giving it an explicit smaller `--priority`.

## Notes

- **Account budget is shared.** Don't run two copies pointed at the same account — they'd
  both spend the one-per-window trickle. Run one poller, anywhere with your `gh` auth.
- **This tool never merges.** It only drives reviews to completion; merging stays with you
  (or whatever merge gate you use).
- **If both this and another review-driver run on the same account**, scope this one with
  `--repos` (or `drop` the PRs the other owns) so you don't double-trigger and waste the
  shared budget.
- **Notice-wording assumptions.** Detection keys off CodeRabbit's message wording (rate-limit,
  "review skipped / no new commits", "no actionable comments"), ordered by `updated_at` to
  handle in-place comment edits. If CodeRabbit changes its wording, update the regexes near
  the top of `lib/crqueue.cs`.
