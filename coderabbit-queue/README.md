# coderabbit-queue

A cheap, non-LLM poller that drives every open PR on your GitHub account to a fresh
[CodeRabbit](https://coderabbit.ai) review, patiently working around CodeRabbit's
Fair-Usage rate limit.

CodeRabbit's Fair-Usage limit trickles roughly **one review per window** across your whole
account. Left alone, a rate-limited PR never gets re-reviewed unless something keeps
re-posting `@coderabbitai review` after each window opens. This tool is that something —
without burning an interactive AI agent to do mechanical polling.

## Features

- **Account-wide discovery** — finds all your open PRs (`gh search prs --author=@me`).
- **Account-cooldown model** — the Fair-Usage cap is per-account, so it holds *all* triggers
  until the latest rate-limit window clears; it never wastes the trickle on one PR while
  another is mid-cooldown.
- **Fair round-robin** — priority → least-recently-triggered → PR number, so no PR starves.
- **Full-review auto-escalation** — when an incremental review is skipped ("no new commits"
  after a content-neutral rebase), it escalates to `@coderabbitai full review` instead of
  looping forever on a no-op.
- **Bumpable priority queue** cached under `~/.claude/coderabbit-queue/queue.json` — bump a PR
  to the top, pin/exclude PRs, or check status at a glance.
- **Head-anchored** — a review only counts when it covers the PR's current head SHA.
- **Never merges** — it only drives reviews to completion.

## Prerequisites

- [.NET SDK 10+](https://dotnet.microsoft.com/download) (`dotnet --version`)
- [GitHub CLI](https://cli.github.com/), authenticated (`gh auth status`)
- CodeRabbit installed on the repos you want reviewed

## Install

It's packaged as a [Claude Code skill](https://docs.claude.com/en/docs/claude-code/skills).
Drop it into your skills folder:

```bash
cp -r coderabbit-queue ~/.claude/skills/
```

Then invoke it as `/coderabbit-queue`, or run the engine directly (it needs no AI — just `gh`):

```bash
dotnet run ~/.claude/skills/coderabbit-queue/lib/crqueue.cs run
```

You can also run it outside Claude Code entirely — via `nohup`, `tmux`, or a cron calling
`tick` every few minutes.

## Usage

See [SKILL.md](./SKILL.md) for the full command reference. Quick tour:

```bash
CRQ=~/.claude/skills/coderabbit-queue/lib/crqueue.cs
dotnet run $CRQ status              # print the queue, no triggering
dotnet run $CRQ run                 # loop until every PR is reviewed
dotnet run $CRQ bump owner/repo 42  # review PR #42 next
```

## License

MIT — see [LICENSE](../LICENSE).
