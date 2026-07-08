// crqueue.cs — CodeRabbit review queue driver
//
// A cheap, NON-LLM poller that drives every open PR on your GitHub account to a
// fresh CodeRabbit review at its current head, patiently working around the
// Fair-Usage rate limit. It rotates fairly across PRs (least-recently-triggered
// first, honouring a manual priority), models the account-level cooldown so it
// never wastes the "trickle", and persists a bumpable queue cache under
// ~/.claude/coderabbit-queue/queue.json.
//
// A PR is DONE when CodeRabbit has reviewed its current head — either clean
// ("No actionable comments were generated") or with actionable comments (which
// need code fixes this tool can't make; it stops retriggering that head until
// the SHA changes).
//
// Commands:
//   tick   [--full] [--repos a/b,c/d] [--grace N]   one iteration: reconcile + trigger the next eligible PR
//   status [--repos ...]                             print the queue from cache (no triggering)
//   run    [--full] [--interval S] [--max-minutes M] loop tick until every PR is terminal
//   bump   <repo> <number> [--priority N]            raise a PR's priority (default: top of queue)
//   add    <repo> <number>                           pin a PR into the queue (also un-excludes)
//   drop   <repo> <number>                           exclude a PR from auto-discovery
//
// Exit codes: 0 ok; 2 usage error; 3 gh/auth failure.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

// Nullable-flow and trim/AOT advisories are noise for a gh-driven file-app invoked in a loop.
#pragma warning disable CS8602, CS8604, CS8620, CS0162, IL2026, IL3050

var argv = Environment.GetCommandLineArgs().Skip(1).ToArray(); // [0] is the .cs path under dotnet run
// dotnet run passes the script path as argv[0]; the real args follow. Detect and strip.
if (argv.Length > 0 && argv[0].EndsWith(".cs")) argv = argv.Skip(1).ToArray();
if (argv.Length == 0) { Usage(); return 2; }

var cmd = argv[0];
var rest = argv.Skip(1).ToArray();

string HomeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
string CacheDir = Path.Combine(HomeDir, ".claude", "coderabbit-queue");
string CachePath = Path.Combine(CacheDir, "queue.json");
Directory.CreateDirectory(CacheDir);

// ---- flags ---------------------------------------------------------------
bool Flag(string name) => rest.Contains("--" + name);
string? Opt(string name)
{
    var i = Array.IndexOf(rest, "--" + name);
    return (i >= 0 && i + 1 < rest.Length) ? rest[i + 1] : null;
}
string[] Positionals() => rest.Where((a, i) =>
    !a.StartsWith("--") &&
    !(i > 0 && rest[i - 1].StartsWith("--") && rest[i - 1] is "--repos" or "--grace" or "--interval" or "--max-minutes" or "--priority")
).ToArray();

bool full = Flag("full");
// Reject negative/garbage numeric flags rather than silently accepting them —
// e.g. --grace -1 would make a just-triggered PR eligible again far too soon.
// A flag present with no following value (last token, or followed by another --flag).
bool FlagPresentNoValue(string name) => rest.Contains("--" + name) && Opt(name) == null;
int Nat(string name, int dflt, int min)
{
    if (FlagPresentNoValue(name)) { Console.Error.WriteLine($"[crqueue] --{name} requires a value."); Environment.Exit(2); }
    var raw = Opt(name);
    if (raw == null) return dflt;
    if (!int.TryParse(raw, out var v) || v < min)
    {
        Console.Error.WriteLine($"[crqueue] --{name} must be an integer >= {min}; got '{raw}'.");
        Environment.Exit(2);
    }
    return v;
}
int graceMin = Nat("grace", 20, 0);
var repoFilter = (Opt("repos") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
string triggerPhrase = full ? "@coderabbitai full review" : "@coderabbitai review";
// Minimum spacing between any two triggers, even absent a rate-limit notice, so we
// never hammer the account faster than CodeRabbit can answer.
int minTriggerIntervalMin = 2;

// Classification patterns (declared before dispatch because the command handlers
// below close over them).
var RateLimitRx = new System.Text.RegularExpressions.Regex(
    @"rate limit|review limit reached|fair usage", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
// Broad — used ONLY for head-anchoring, where a head-SHA containment guard makes
// loose phrases safe (a walkthrough naming the head sha genuinely covers it).
var ReviewPhraseRx = new System.Text.RegularExpressions.Regex(
    @"no actionable comments were generated|actionable comments posted|summary by coderabbit|walkthrough|reviewing files that changed|files that changed from the base",
    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
// Narrow — a FINISHED review only. Used for the rate-limit-vs-review recency test,
// where a broad match (an in-progress "reviewing files…" status) newer than a
// rate-limit notice would wrongly mask the cooldown and retrigger into the window.
var FinalReviewRx = new System.Text.RegularExpressions.Regex(
    @"no actionable comments were generated|actionable comments posted|summary by coderabbit",
    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
var CleanRx = new System.Text.RegularExpressions.Regex(
    @"no actionable comments were generated", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
// CodeRabbit's response to an INCREMENTAL `@coderabbitai review` when the head's diff
// is unchanged since its last review (e.g. after a content-neutral rebase): it skips
// with "no new commits to review". Incremental will keep being a no-op — only a FULL
// review re-examines the whole diff and yields a fresh verdict at the current head.
var ReviewSkippedRx = new System.Text.RegularExpressions.Regex(
    @"review skipped|no new commits to review", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
var RetryHoursRx = new System.Text.RegularExpressions.Regex(
    @"available in:?[^\n]*?(\d+)\s*hour", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
var RetryMinutesRx = new System.Text.RegularExpressions.Regex(
    @"available in:?[^\n]*?(\d+)\s*minute", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

switch (cmd)
{
    case "tick": return await Tick(trigger: true);
    case "status": return await Tick(trigger: false, statusOnly: true);
    case "run": return await Run();
    case "bump": return Bump();
    case "add": return AddDrop(add: true);
    case "drop": return AddDrop(add: false);
    default: Usage(); return 2;
}

// ==========================================================================
//  gh subprocess helper
// ==========================================================================
(int code, string stdout, string stderr) Gh(params string[] args)
{
    var psi = new ProcessStartInfo("gh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    // Drain both pipes CONCURRENTLY. Reading stdout to completion before touching
    // stderr deadlocks when gh fills the stderr buffer while we're blocked on stdout.
    var soTask = p.StandardOutput.ReadToEndAsync();
    var seTask = p.StandardError.ReadToEndAsync();
    p.WaitForExit();
    return (p.ExitCode, soTask.GetAwaiter().GetResult(), seTask.GetAwaiter().GetResult());
}

// ==========================================================================
//  cache load/save  (persisted: priority, triggerCount, lastTriggeredAt,
//  addedAt, excluded; the rest is a refreshed snapshot for `status`)
// ==========================================================================
JsonObject LoadCache()
{
    if (!File.Exists(CachePath)) return new JsonObject { ["prs"] = new JsonArray() };
    try { return JsonNode.Parse(File.ReadAllText(CachePath))!.AsObject(); }
    catch
    {
        // Don't silently discard priorities/excludes/trigger-spacing on a truncated
        // write — preserve the bad file for inspection and warn loudly.
        var bak = CachePath + ".corrupt";
        try { File.Copy(CachePath, bak, overwrite: true); } catch { /* best effort */ }
        Console.Error.WriteLine($"[crqueue] WARNING: {CachePath} unreadable; backed up to {bak} and starting fresh. Manual state (priority/excluded) was lost.");
        return new JsonObject { ["prs"] = new JsonArray() };
    }
}
void SaveCache(JsonObject cache, DateTimeOffset now)
{
    cache["updatedAt"] = now.ToString("u");
    File.WriteAllText(CachePath, cache.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}
JsonObject? FindEntry(JsonArray prs, string repo, long number) =>
    prs.OfType<JsonObject>().FirstOrDefault(e =>
        (string?)e["repo"] == repo && (long?)e["number"] == number);

// ==========================================================================
//  classification
// ==========================================================================
// Returns (state, headSha, rateLimitedUntil?, note, needsFull). state in:
//   clean | has-comments | rate-limited | pending | never-reviewed | error
// needsFull=true when an incremental review would be a no-op (skipped) and only a
// full review can produce a fresh verdict at the current head.
(string state, string headSha, DateTimeOffset? rlUntil, string note, bool needsFull)
    Classify(string repo, long number, DateTimeOffset? lastTriggeredAt, DateTimeOffset now)
{
    var (hc, hso, hse) = Gh("pr", "view", number.ToString(), "--repo", repo, "--json", "headRefOid,state");
    if (hc != 0) return ("error", "", null, hse.Trim().Split('\n').LastOrDefault() ?? "gh pr view failed", false);
    var head = JsonNode.Parse(hso)!.AsObject();
    var headSha = (string?)head["headRefOid"] ?? "";

    var signals = new List<CrSignal>();
    bool Collect(string endpoint, string tsField, bool withCommit)
    {
        var jq = withCommit
            ? $".[] | select((.user.login // \"\") | ascii_downcase | startswith(\"coderabbit\")) | {{ts: .{tsField}, body: .body, commit: (.commit_id // \"\")}}"
            : $".[] | select((.user.login // \"\") | ascii_downcase | startswith(\"coderabbit\")) | {{ts: .{tsField}, body: .body, commit: \"\"}}";
        var (c, so, _) = Gh("api", $"repos/{repo}/{endpoint}", "--paginate", "--jq", jq);
        if (c != 0) return false; // a fetch failure must NOT look like "no CodeRabbit activity"
        foreach (var line in so.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var o = JsonDocument.Parse(line).RootElement;
                var ts = o.GetProperty("ts").GetString();
                var body = o.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString()! : "";
                var commit = o.TryGetProperty("commit", out var cm) && cm.ValueKind == JsonValueKind.String ? cm.GetString()! : "";
                if (DateTimeOffset.TryParse(ts, out var t)) signals.Add(new CrSignal(t, body, commit));
            }
            catch { /* skip */ }
        }
        return true;
    }
    // If EITHER feed fails we cannot trust the picture — return "error" so the PR is
    // never triggered on a partial view (would risk wasting the shared review budget).
    bool okC = Collect($"issues/{number}/comments", "updated_at", false);
    bool okR = Collect($"pulls/{number}/reviews", "submitted_at", true);
    if (!okC || !okR) return ("error", headSha, null, "gh api failed — skipping to avoid a blind trigger", false);

    // Head-anchored review: a reviews-API submission at head, OR a non-rate-limit
    // review/walkthrough comment whose reviewed-commit range names the head sha.
    bool reviewAtHead = headSha.Length == 40 && signals.Any(s =>
        (s.Commit == headSha) ||
        (!RateLimitRx.IsMatch(s.Body) && ReviewPhraseRx.IsMatch(s.Body) &&
         s.Body.Contains(headSha, StringComparison.OrdinalIgnoreCase)));
    // Clean = reviewed at head with a "no actionable comments" signal. reviewAtHead
    // already pins coverage to the head, so we don't re-require the SHA in the clean
    // body (a clean review-API submission may carry the verdict without the range).
    bool cleanAtHead = reviewAtHead && signals.Any(s => !RateLimitRx.IsMatch(s.Body) && CleanRx.IsMatch(s.Body));

    var latestRate = signals.Where(s => RateLimitRx.IsMatch(s.Body)).OrderByDescending(s => s.Ts).FirstOrDefault();
    // Recency test uses the NARROW finished-review pattern: an in-progress status
    // comment must not out-rank a newer rate-limit notice and unmask the cooldown.
    var latestReview = signals.Where(s => FinalReviewRx.IsMatch(s.Body) && !RateLimitRx.IsMatch(s.Body))
        .OrderByDescending(s => s.Ts).FirstOrDefault();
    var latestSkip = signals.Where(s => ReviewSkippedRx.IsMatch(s.Body)).OrderByDescending(s => s.Ts).FirstOrDefault();
    var reviewTs = latestReview?.Ts ?? DateTimeOffset.MinValue;
    var skipTs = latestSkip?.Ts ?? DateTimeOffset.MinValue;

    if (reviewAtHead)
        return (cleanAtHead ? "clean" : "has-comments", headSha, null, "", false);

    // Skip-stuck (needsFull): a "review skipped / no new commits" notice means THIS head's
    // diff is unchanged since the last review, so incremental is a permanent no-op. That
    // stays true through later RATE-LIMITS (a rate-limit is not a review) — only an actual
    // review (in-progress or final) NEWER than the skip clears it. So compare the skip
    // against the newest REVIEW-ish signal, ignoring rate-limit notices. Computed up front
    // and threaded through every return so a rate-limited PR still escalates when it fires.
    var latestReviewish = signals.Where(s => ReviewPhraseRx.IsMatch(s.Body)
            && !ReviewSkippedRx.IsMatch(s.Body) && !RateLimitRx.IsMatch(s.Body))
        .OrderByDescending(s => s.Ts).FirstOrDefault();
    var reviewishTs = latestReviewish != null ? latestReviewish.Ts : DateTimeOffset.MinValue;
    bool needsFull = latestSkip != null && skipTs >= reviewishTs;

    // Rate-limited if the rate-limit notice is the NEWEST signal (newer than the last real
    // review AND the last skip). We still wait, but carry needsFull so that when the window
    // elapses the retrigger is a FULL review (the head is content-neutral).
    if (latestRate != null && latestRate.Ts >= reviewTs && latestRate.Ts >= skipTs)
    {
        var hm = RetryHoursRx.Match(latestRate.Body);
        var mm = RetryMinutesRx.Match(latestRate.Body);
        var hrs = hm.Success && int.TryParse(hm.Groups[1].Value, out var h) ? h : 0;
        var mns = mm.Success && int.TryParse(mm.Groups[1].Value, out var m) ? m : 0;
        var mins = (hrs > 0 || mns > 0) ? hrs * 60 + mns : 60;
        var until = latestRate.Ts.AddMinutes(mins);
        if (now < until) return ("rate-limited", headSha, until, $"until {until:u}", needsFull);
        // window elapsed -> eligible again
        return ("never-reviewed", headSha, until,
            needsFull ? "needs full review (rate-limit window elapsed)" : "rate-limit window elapsed", needsFull);
    }

    // We triggered and CodeRabbit hasn't answered yet (no newer response), within grace.
    if (lastTriggeredAt is DateTimeOffset lt)
    {
        var newestResp = signals.Select(s => s.Ts).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        if (lt > newestResp && (now - lt).TotalMinutes < graceMin)
            return ("pending", headSha, null, $"triggered {(int)(now - lt).TotalMinutes}m ago", false);
    }
    return ("never-reviewed", headSha, null, needsFull ? "incremental skipped — needs full review" : "", needsFull);
}

// ==========================================================================
//  discovery + reconcile
// ==========================================================================
// Returns ok=false on a gh/search failure instead of hard-exiting: `run` must
// survive a transient failure and retry next interval rather than dying mid-loop.
(bool ok, List<(string repo, long number, string title, bool draft)> prs) Discover()
{
    var empty = new List<(string, long, string, bool)>();
    // 1000 is gh search's max page. Beyond that, account-level cooldown modelling
    // could miss a rate-limited PR; we warn rather than silently under-count.
    var (c, so, se) = Gh("search", "prs", "--author=@me", "--state=open",
        "--json", "number,title,repository,url,isDraft", "--limit", "1000");
    if (c != 0) { Console.Error.WriteLine("gh search prs failed: " + se.Trim()); return (false, empty); }
    JsonArray arr;
    try { arr = JsonNode.Parse(so)!.AsArray(); }
    catch { Console.Error.WriteLine("gh search prs returned unparseable JSON"); return (false, empty); }
    var outp = new List<(string, long, string, bool)>();
    foreach (var n in arr.OfType<JsonObject>())
    {
        var repo = (string?)n["repository"]?["nameWithOwner"] ?? "";
        var num = (long?)n["number"] ?? 0;
        var title = (string?)n["title"] ?? "";
        var draft = (bool?)n["isDraft"] ?? false;
        if (repo != "" && num != 0) outp.Add((repo, num, title, draft));
    }
    if (outp.Count >= 1000)
        Console.Error.WriteLine("[crqueue] WARNING: hit the 1000-PR discovery cap; some open PRs may be invisible to account-cooldown modelling.");
    return (true, outp);
}

// ==========================================================================
//  tick — the core iteration
// ==========================================================================
async Task<int> Tick(bool trigger, bool statusOnly = false)
{
    await Task.CompletedTask;
    var now = DateTimeOffset.UtcNow;
    var cache = LoadCache();
    var prs = cache["prs"]!.AsArray();

    // 1. discover + merge into cache (skip drafts). NB: --repos is applied only to the
    //    trigger SELECTION below, never here — the Fair-Usage cap is account-level, so
    //    cooldown must be modelled across every repo even when triggering is scoped.
    var (discoverOk, discovered) = Discover();
    if (!discoverOk)
    {
        // Never reclassify/close/trigger on a blind discovery — that would risk both
        // wasted triggers and spurious "closed" marks. Skip this tick; run() retries.
        Console.WriteLine("{\"reason\":\"discovery failed — skipping tick\"}");
        return 3;
    }
    foreach (var (repo, number, title, draft) in discovered)
    {
        if (draft) continue;
        var e = FindEntry(prs, repo, number);
        if (e == null)
        {
            prs.Add(new JsonObject
            {
                ["repo"] = repo, ["number"] = number, ["title"] = title,
                ["priority"] = 0L, ["triggerCount"] = 0L, ["lastTriggeredAt"] = null,
                ["addedAt"] = now.ToString("u"), ["excluded"] = false,
                ["state"] = "unknown", ["headSha"] = "", ["rateLimitedUntil"] = null, ["note"] = ""
            });
        }
        else { e["title"] = title; }
    }

    // 2. classify every non-excluded entry that is still an open discovered PR.
    //    `live` is account-wide (no repo filter) so the cooldown below is accurate.
    var open = discovered.Where(d => !d.draft).Select(d => (d.repo, d.number)).ToHashSet();
    var live = prs.OfType<JsonObject>()
        .Where(e => !((bool?)e["excluded"] ?? false))
        .ToList();

    foreach (var e in live)
    {
        var repo = (string?)e["repo"]!; var number = (long?)e["number"] ?? 0;
        if (!open.Contains((repo, number))) { e["state"] = "closed"; continue; }
        DateTimeOffset? lastTrig = DateTimeOffset.TryParse((string?)e["lastTriggeredAt"], out var ltp) ? ltp : null;
        var (state, headSha, rlUntil, note, needsFull) = Classify(repo, number, lastTrig, now);
        if (state == "error")
        {
            // A transient classify failure must NOT erase what we already knew — keep the
            // prior headSha and (critically) the prior rateLimitedUntil, so a gh blip on a
            // rate-limited PR can't wipe the account cooldown and free a trigger.
            e["state"] = "error"; e["note"] = note;
            continue;
        }
        // A new head clears our trigger memory so a fresh push is re-eligible immediately.
        if (headSha != "" && (string?)e["headSha"] != headSha && (string?)e["headSha"] is not (null or ""))
        {
            e["lastTriggeredAt"] = null;
        }
        e["state"] = state; e["headSha"] = headSha; e["note"] = note;
        e["rateLimitedUntil"] = rlUntil?.ToString("u");
        // Escalation is driven solely by CodeRabbit's own skip signal (precise). A
        // same-head retry heuristic was dropped: it false-escalated rate-limit/timeout
        // retries to full review even when incremental would have worked.
        e["needsFull"] = needsFull;
    }
    // Account cooldown = the latest still-future rate-limit window across EVERY tracked PR
    // (including ones preserved through a transient error this tick), not just those freshly
    // classified as rate-limited.
    DateTimeOffset? globalCooldownUntil = null;
    foreach (var e in live)
    {
        if (DateTimeOffset.TryParse((string?)e["rateLimitedUntil"], out var ru) && ru > now &&
            (globalCooldownUntil == null || ru > globalCooldownUntil))
            globalCooldownUntil = ru;
    }

    // 3. status view (no triggering)
    if (statusOnly) { PrintStatus(live, globalCooldownUntil, now); SaveCache(cache, now); return 0; }

    // 4. choose + trigger
    JsonObject? chosen = null; string reason;
    if (globalCooldownUntil is DateTimeOffset gc && now < gc)
    {
        reason = $"account rate-limited until {gc:u} ({(int)(gc - now).TotalMinutes}m) — holding all triggers";
    }
    else
    {
        // global min-spacing floor across the whole account
        var lastGlobal = live.Select(e => DateTimeOffset.TryParse((string?)e["lastTriggeredAt"], out var t) ? t : DateTimeOffset.MinValue)
            .DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        if ((now - lastGlobal).TotalMinutes < minTriggerIntervalMin)
        {
            reason = $"min trigger interval ({minTriggerIntervalMin}m) not elapsed";
        }
        else
        {
            // Cooldown was modelled account-wide; the repo filter scopes only what we TRIGGER.
            var eligible = live
                .Where(e => (string?)e["state"] == "never-reviewed")
                .Where(e => repoFilter.Length == 0 || repoFilter.Contains((string?)e["repo"]))
                .ToList();
            // priority desc, then least-recently-triggered (never-triggered first), then number asc
            chosen = eligible
                .OrderByDescending(e => (long?)e["priority"] ?? 0)
                .ThenBy(e => DateTimeOffset.TryParse((string?)e["lastTriggeredAt"], out var t) ? t : DateTimeOffset.MinValue)
                .ThenBy(e => (long?)e["number"] ?? 0)
                .FirstOrDefault();
            reason = chosen == null ? "no eligible PRs (all clean / has-comments / pending)" : "triggered";
        }
    }

    JsonObject? triggered = null;
    if (chosen != null && !trigger) { reason = "dry-run (trigger disabled): would trigger " + (string?)chosen["repo"] + "#" + (long?)chosen["number"]; }
    else if (chosen != null)
    {
        var repo = (string?)chosen["repo"]!; var number = (long?)chosen["number"] ?? 0;
        // Escalate to a FULL review when incremental would be a no-op (CodeRabbit skipped
        // "no new commits" at this head), or when the user forced --full. A full review
        // re-examines the whole diff and always yields a fresh verdict.
        bool useFull = full || ((bool?)chosen["needsFull"] ?? false);
        var phrase = useFull ? "@coderabbitai full review" : "@coderabbitai review";
        var (c, _, se) = Gh("pr", "comment", number.ToString(), "--repo", repo, "--body", phrase);
        if (c == 0)
        {
            chosen["lastTriggeredAt"] = now.ToString("u");
            chosen["triggerCount"] = ((long?)chosen["triggerCount"] ?? 0) + 1;
            chosen["state"] = "pending";
            triggered = chosen;
            reason = useFull ? "triggered (full review)" : "triggered";
        }
        else { reason = "trigger failed: " + se.Trim().Split('\n').LastOrDefault(); }
    }

    SaveCache(cache, now);

    // machine-readable result
    var result = new JsonObject
    {
        ["now"] = now.ToString("u"),
        ["triggered"] = triggered == null ? null : new JsonObject { ["repo"] = (string?)triggered["repo"], ["number"] = (long?)triggered["number"], ["phrase"] = (full || ((bool?)triggered["needsFull"] ?? false)) ? "@coderabbitai full review" : "@coderabbitai review" },
        ["reason"] = reason,
        ["globalCooldownUntil"] = globalCooldownUntil?.ToString("u"),
        ["counts"] = CountsOf(live),
    };
    Console.WriteLine(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

JsonObject CountsOf(List<JsonObject> live)
{
    var o = new JsonObject();
    foreach (var grp in live.GroupBy(e => (string?)e["state"] ?? "unknown").OrderBy(x => x.Key))
        o[grp.Key] = grp.Count();
    return o;
}

void PrintStatus(List<JsonObject> live, DateTimeOffset? cooldown, DateTimeOffset now)
{
    Console.WriteLine($"CodeRabbit review queue — {live.Count} PR(s)   ({now:HH:mm} UTC)");
    if (cooldown is DateTimeOffset c && now < c)
        Console.WriteLine($"  ACCOUNT COOLDOWN until {c:HH:mm} UTC ({(int)(c - now).TotalMinutes}m) — no triggers until then");
    Console.WriteLine();
    Console.WriteLine($"  {"PRIO",4}  {"PR",-42}  {"STATE",-14}  {"TRIES",5}  NOTE");
    foreach (var e in live
        .OrderByDescending(e => (long?)e["priority"] ?? 0)
        .ThenBy(e => (string?)e["state"])
        .ThenBy(e => (long?)e["number"] ?? 0))
    {
        var pr = $"{(string?)e["repo"]}#{(long?)e["number"]}";
        var st = (string?)e["state"] ?? "?";
        var tries = (long?)e["triggerCount"] ?? 0;
        var note = (string?)e["note"] ?? "";
        var mark = st switch { "clean" => "✓", "has-comments" => "✎", "rate-limited" => "⏳", "pending" => "…", "never-reviewed" => "•", _ => " " };
        Console.WriteLine($"  {(long?)e["priority"],4}  {Trunc(pr, 42),-42}  {mark} {st,-12}  {tries,5}  {note}");
    }
}
string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";

// ==========================================================================
//  run — loop until every PR terminal
// ==========================================================================
async Task<int> Run()
{
    var interval = Nat("interval", 180, 30);
    var maxMin = Nat("max-minutes", 720, 1);
    var deadline = DateTimeOffset.UtcNow.AddMinutes(maxMin);
    Console.WriteLine($"[crqueue] run: phrase='{triggerPhrase}', interval={interval}s, deadline {deadline:HH:mm} UTC");
    while (DateTimeOffset.UtcNow < deadline)
    {
        var rc = await Tick(trigger: true);
        if (rc == 3)
        {
            // discovery failed this pass — do NOT evaluate the completion condition on a
            // stale/empty cache (would falsely exit "all terminal"); just retry next interval.
            Console.WriteLine($"[crqueue] discovery failed; retrying in {interval}s");
            await Task.Delay(TimeSpan.FromSeconds(interval));
            continue;
        }
        // re-read cache to decide whether to keep going / how long to sleep
        var cache = LoadCache();
        var live = cache["prs"]!.AsArray().OfType<JsonObject>()
            .Where(e => !((bool?)e["excluded"] ?? false) && (string?)e["state"] is not ("closed"))
            .Where(e => repoFilter.Length == 0 || repoFilter.Contains((string?)e["repo"]))
            .ToList();
        // "error" is transient (a gh blip during classify) and stays active so it retries;
        // add/bump validate PR existence, so it can't become a permanent stuck entry.
        var active = live.Where(e => (string?)e["state"] is "never-reviewed" or "rate-limited" or "pending" or "unknown" or "error").ToList();
        if (active.Count == 0)
        {
            Console.WriteLine("[crqueue] all PRs terminal (clean / has-comments). Done.");
            return 0;
        }
        // sleep until the account cooldown clears if we're fully blocked, else the interval
        var now = DateTimeOffset.UtcNow;
        var cooldown = live.Select(e => DateTimeOffset.TryParse((string?)e["rateLimitedUntil"], out var t) ? t : (DateTimeOffset?)null)
            .Where(t => t != null).Select(t => t!.Value).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        int sleepS = interval;
        // During an account-level cooldown nothing is triggerable, so sleep straight to
        // the wake time instead of polling. (Also covers the all-blocked case.)
        if (cooldown > now) sleepS = Math.Min((int)(cooldown - now).TotalSeconds + 15, 3600);
        sleepS = Math.Max(sleepS, 30);
        Console.WriteLine($"[crqueue] {active.Count} active; sleeping {sleepS}s");
        await Task.Delay(TimeSpan.FromSeconds(sleepS));
    }
    Console.WriteLine("[crqueue] deadline reached; exiting.");
    return 0;
}

// ==========================================================================
//  bump / add / drop
// ==========================================================================
// Confirm a PR exists and is OPEN before we pin a new live entry for it — a typo'd
// add/bump would otherwise create a junk entry that errors on every tick forever.
bool PrIsOpen(string repo, long number)
{
    var (c, so, _) = Gh("pr", "view", number.ToString(), "--repo", repo, "--json", "state");
    if (c != 0) return false;
    try { return (string?)JsonNode.Parse(so)!["state"] == "OPEN"; } catch { return false; }
}
int Bump()
{
    var pos = Positionals();
    if (pos.Length < 2) { Console.Error.WriteLine("usage: bump <repo> <number> [--priority N]"); return 2; }
    var repo = pos[0]; if (!long.TryParse(pos[1], out var number)) { Console.Error.WriteLine("number must be int"); return 2; }
    var now = DateTimeOffset.UtcNow;
    var cache = LoadCache(); var prs = cache["prs"]!.AsArray();
    var e = FindEntry(prs, repo, number);
    if (e == null)
    {
        if (!PrIsOpen(repo, number)) { Console.Error.WriteLine($"{repo}#{number} is not an open PR — refusing to pin it."); return 2; }
        e = new JsonObject { ["repo"] = repo, ["number"] = number, ["title"] = "", ["priority"] = 0L, ["triggerCount"] = 0L, ["lastTriggeredAt"] = null, ["addedAt"] = now.ToString("u"), ["excluded"] = false, ["state"] = "unknown", ["headSha"] = "", ["rateLimitedUntil"] = null, ["note"] = "" };
        prs.Add(e);
    }
    if (FlagPresentNoValue("priority")) { Console.Error.WriteLine("[crqueue] --priority requires a value."); return 2; }
    var prioRaw = Opt("priority");
    if (prioRaw != null && !long.TryParse(prioRaw, out _))
    {
        Console.Error.WriteLine($"[crqueue] --priority must be an integer; got '{prioRaw}'."); return 2;
    }
    long newPrio = long.TryParse(prioRaw, out var p) ? p
        : (prs.OfType<JsonObject>().Select(x => (long?)x["priority"] ?? 0).DefaultIfEmpty(0).Max() + 1);
    e["priority"] = newPrio; e["excluded"] = false;
    SaveCache(cache, now);
    Console.WriteLine($"bumped {repo}#{number} -> priority {newPrio}");
    return 0;
}
int AddDrop(bool add)
{
    var pos = Positionals();
    if (pos.Length < 2) { Console.Error.WriteLine($"usage: {(add ? "add" : "drop")} <repo> <number>"); return 2; }
    var repo = pos[0]; if (!long.TryParse(pos[1], out var number)) { Console.Error.WriteLine("number must be int"); return 2; }
    var now = DateTimeOffset.UtcNow;
    var cache = LoadCache(); var prs = cache["prs"]!.AsArray();
    var e = FindEntry(prs, repo, number);
    if (e == null)
    {
        // `add` pins a live entry, so validate; `drop` on an unknown PR just records an exclude.
        if (add && !PrIsOpen(repo, number)) { Console.Error.WriteLine($"{repo}#{number} is not an open PR — refusing to add it."); return 2; }
        e = new JsonObject { ["repo"] = repo, ["number"] = number, ["title"] = "", ["priority"] = 0L, ["triggerCount"] = 0L, ["lastTriggeredAt"] = null, ["addedAt"] = now.ToString("u"), ["excluded"] = !add, ["state"] = "unknown", ["headSha"] = "", ["rateLimitedUntil"] = null, ["note"] = "" };
        prs.Add(e);
    }
    else e["excluded"] = !add;
    SaveCache(cache, now);
    Console.WriteLine($"{(add ? "added" : "dropped")} {repo}#{number}");
    return 0;
}

void Usage() => Console.Error.WriteLine(
@"crqueue — CodeRabbit review queue driver
  dotnet run crqueue.cs tick   [--full] [--repos a/b,c/d] [--grace N]
  dotnet run crqueue.cs status [--repos a/b,c/d]
  dotnet run crqueue.cs run    [--full] [--interval S] [--max-minutes M]
  dotnet run crqueue.cs bump   <repo> <number> [--priority N]
  dotnet run crqueue.cs add    <repo> <number>
  dotnet run crqueue.cs drop   <repo> <number>");

// ==========================================================================
//  types (must follow all top-level statements in a file-based app)
// ==========================================================================
record CrSignal(DateTimeOffset Ts, string Body, string Commit);
