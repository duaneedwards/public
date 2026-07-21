#!/usr/bin/env dotnet
#:package Microsoft.Data.Sqlite@*
#:package SQLitePCLRaw.bundle_e_sqlite3@*
#:property PublishAot=false

// snapshot-warp-sessions.cs
//
// Snapshot the current Warp window - every tab, in on-screen order - into a
// Warp launch configuration so the whole window can be restored after a reboot,
// with each Claude Code tab resuming its exact session (claude -r <session-id>)
// and plain shell tabs reopening in their working directory.
//
// Cross-platform (macOS + Windows): no external tools, no shell-outs.
//
// How it works:
//   1. Reads the session registry at ~/.claude/session-registry/ - one JSON
//      file per Warp pane, keyed by pane uuid, maintained by the
//      SessionStart/SessionEnd hook (warp-session-registry.cs).
//   2. Reads Warp's state DB (warp.sqlite) read-only via Microsoft.Data.Sqlite
//      for the real tab order (tabs.id ascending) and each tab's pane uuid.
//   3. Joins tabs to sessions via pane uuid. Tabs without a session become
//      plain cwd tabs. Registry entries whose pane no longer exists (closed
//      tab) are pruned.
//   4. Reads each session's transcript for its title (last ai-title /
//      custom-title event) and permission mode (last permission-mode event,
//      reconstructed as --permission-mode <mode> on the resume command).
//   5. Emits the launch configuration YAML into Warp's launch_configurations
//      directory.
//
// Restore after reboot: open Warp -> Command Palette -> "Open Launch
// Configuration" -> "Claude sessions snapshot". (Launch configs have no
// working warp:// URI trigger, so the palette is the one manual step.)
//
// Tab titles are deliberately NOT written to the YAML: a launch-config title
// acts like a manual rename and would pin the tab name, blocking the live
// "* <session title>" updates Claude Code sets via terminal escapes.
//
// Usage:  dotnet run ~/.claude/lib/snapshot-warp-sessions.cs

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var registryDir = Path.Combine(home, ".claude", "session-registry");
var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

// --- Per-OS Warp locations ---------------------------------------------------
// Windows candidates are unverified best guesses - confirm on a Windows machine
// and trim this list (the macOS DB wasn't at its documented path either).

string[] dbCandidates = isWindows
    ? new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "warp", "Warp", "data", "warp.sqlite"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "warp", "Warp", "data", "warp.sqlite"),
    }
    : new[]
    {
        Path.Combine(home, "Library", "Group Containers", "2BBY89MBSN.dev.warp", "Library", "Application Support", "dev.warp.Warp-Stable", "warp.sqlite"),
        Path.Combine(home, "Library", "Application Support", "dev.warp.Warp-Stable", "warp.sqlite"),
    };

string[] launchDirCandidates = isWindows
    ? new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "warp", "Warp", "launch_configurations"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "warp", "Warp", "launch_configurations"),
    }
    : new[] { Path.Combine(home, ".warp", "launch_configurations") };

var warpDb = dbCandidates.FirstOrDefault(File.Exists);
var launchDir = launchDirCandidates.FirstOrDefault(Directory.Exists);
if (launchDir == null)
{
    Console.Error.WriteLine("error: no Warp launch_configurations directory found. Probed:");
    foreach (var d in launchDirCandidates) Console.Error.WriteLine($"  {d}");
    return 1;
}
var outputPath = Path.Combine(launchDir, "claude-sessions-snapshot.yaml");

// --- 1. Load the session registry -------------------------------------------

var registry = new Dictionary<string, Entry>(); // pane uuid -> entry
if (Directory.Exists(registryDir))
{
    foreach (var file in Directory.GetFiles(registryDir, "*.json"))
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            var pane = root.GetProperty("pane_uuid").GetString() ?? "";
            var sessionId = root.GetProperty("session_id").GetString() ?? "";
            var cwd = root.TryGetProperty("cwd", out var c) ? c.GetString() ?? home : home;
            if (pane.Length > 0 && sessionId.Length > 0)
                registry[pane] = new Entry(pane, sessionId, cwd, file);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warn: could not parse {file}: {ex.Message}");
        }
    }
}

// --- 2. Read Warp's tab order from its state DB ------------------------------

// One row per (tab, pane); tabs.id ascending matches on-screen tab order.
var warpRows = new List<(long WindowId, long TabId, string PaneUuid, string Cwd)>();
var warpDbOk = false;

if (warpDb != null)
{
    try
    {
        using var conn = new SqliteConnection($"Data Source={warpDb};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT t.window_id, t.id, lower(hex(tp.uuid)), COALESCE(tp.cwd,'') " +
            "FROM tabs t JOIN pane_nodes pn ON pn.tab_id = t.id " +
            "JOIN terminal_panes tp ON tp.id = pn.id ORDER BY t.window_id, t.id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            warpRows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3)));
        warpDbOk = warpRows.Count > 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"warn: could not read Warp state DB at {warpDb}: {ex.Message}");
    }
}
else
{
    Console.Error.WriteLine("warn: no Warp state DB found. Probed:");
    foreach (var d in dbCandidates) Console.Error.WriteLine($"  {d}");
}

// --- 3. Build the ordered tab list ------------------------------------------

var tabs = new List<Tab>(); // in restore order
var prunedEntries = new List<Entry>();

if (warpDbOk)
{
    var livePanes = warpRows.Select(r => r.PaneUuid).ToHashSet();

    // Group panes by tab (splits give multiple rows); prefer a pane with a session
    foreach (var tabGroup in warpRows.GroupBy(r => (r.WindowId, r.TabId)).OrderBy(g => g.Key.WindowId).ThenBy(g => g.Key.TabId))
    {
        var withSession = tabGroup.Where(r => registry.ContainsKey(r.PaneUuid)).Cast<(long, long, string PaneUuid, string)?>().FirstOrDefault();
        if (withSession != null)
            tabs.Add(Tab.ForSession(registry[withSession.Value.PaneUuid]));
        else
        {
            var first = tabGroup.First();
            if (first.Cwd.Length > 0) tabs.Add(Tab.Plain(first.Cwd));
        }
    }

    // Prune entries whose pane no longer exists (tab closed; SessionEnd missed it)
    foreach (var e in registry.Values.Where(e => !livePanes.Contains(e.PaneUuid)))
    {
        prunedEntries.Add(e);
        File.Delete(e.File);
    }
}
else
{
    Console.Error.WriteLine("warn: Warp state DB unavailable - falling back to cwd-sorted order, no pruning");
    foreach (var e in registry.Values.OrderBy(e => e.Cwd).ThenBy(e => e.SessionId))
        tabs.Add(Tab.ForSession(e));
}

if (tabs.Count == 0)
{
    Console.WriteLine("No tabs to snapshot - no registered Claude sessions and no Warp tab data.");
    return 1;
}

// --- 4. Session titles + permission modes from transcripts -------------------

foreach (var t in tabs.Where(t => t.SessionId != null))
{
    var (title, mode) = ScanTranscript(t.SessionId!);
    t.Title = title;
    t.Command = BuildResumeCommand(t.SessionId!, mode);
}

// --- 5. Emit the Warp launch configuration ----------------------------------

var yaml = new StringBuilder();
yaml.AppendLine($"# Generated by snapshot-warp-sessions.cs — {DateTime.Now:yyyy-MM-dd HH:mm zzz}");
yaml.AppendLine("# Restore: Warp -> Command Palette -> Open Launch Configuration -> Claude sessions snapshot");
yaml.AppendLine("name: Claude sessions snapshot");
yaml.AppendLine("windows:");
yaml.AppendLine("  - tabs:");

foreach (var t in tabs)
{
    yaml.AppendLine("      - layout:");
    yaml.AppendLine($"          cwd: {Quote(t.Cwd)}");
    if (t.Command != null)
    {
        yaml.AppendLine("          commands:");
        yaml.AppendLine($"            - exec: {Quote(t.Command)}");
    }
}

File.WriteAllText(outputPath, yaml.ToString());

// --- 6. Summary --------------------------------------------------------------

var sessionCount = tabs.Count(t => t.SessionId != null);
Console.WriteLine($"Snapshot written: {outputPath}");
Console.WriteLine($"  {tabs.Count} tab(s), {sessionCount} with Claude sessions, in restore order:");
Console.WriteLine();
Console.WriteLine($"  {"#",2}  {"title",-52} {"session",-8} cwd");
var n = 0;
foreach (var t in tabs)
{
    n++;
    var title = t.Title ?? (t.SessionId != null ? "(untitled)" : "(shell)");
    if (title.Length > 52) title = title[..49] + "...";
    var sid = t.SessionId != null ? t.SessionId[..8] : "-";
    Console.WriteLine($"  {n,2}  {title,-52} {sid,-8} {Tildify(t.Cwd)}");
}
Console.WriteLine();
if (prunedEntries.Count > 0)
{
    Console.WriteLine($"  pruned {prunedEntries.Count} registry entr{(prunedEntries.Count == 1 ? "y" : "ies")} for closed tabs:");
    foreach (var e in prunedEntries) Console.WriteLine($"    {e.SessionId[..8]}  {Tildify(e.Cwd)}");
}

Console.WriteLine("Restore after reboot: Warp -> Command Palette -> \"Open Launch Configuration\" -> \"Claude sessions snapshot\"");
return 0;

// --- helpers -----------------------------------------------------------------

// Single pass over the session transcript: last ai-title/custom-title event
// wins for the title, last permission-mode event wins for the mode.
(string? Title, string? Mode) ScanTranscript(string sessionId)
{
    var projRoot = Path.Combine(home, ".claude", "projects");
    if (!Directory.Exists(projRoot)) return (null, null);
    foreach (var dir in Directory.EnumerateDirectories(projRoot))
    {
        var f = Path.Combine(dir, sessionId + ".jsonl");
        if (!File.Exists(f)) continue;
        string? title = null, mode = null;
        foreach (var line in File.ReadLines(f))
        {
            var isTitle = line.Contains("\"ai-title\"") || line.Contains("\"custom-title\"");
            var isMode = line.Contains("\"permission-mode\"");
            if (!isTitle && !isMode) continue;
            try
            {
                using var d = JsonDocument.Parse(line);
                var type = d.RootElement.TryGetProperty("type", out var ty) ? ty.GetString() : null;
                if (type == "ai-title" && d.RootElement.TryGetProperty("aiTitle", out var a)) title = a.GetString();
                else if (type == "custom-title" && d.RootElement.TryGetProperty("customTitle", out var ct)) title = ct.GetString();
                else if (type == "permission-mode" && d.RootElement.TryGetProperty("permissionMode", out var pm)) mode = pm.GetString();
            }
            catch { /* not a matching event after all */ }
        }
        return (title, mode);
    }
    return (null, null);
}

static string BuildResumeCommand(string sessionId, string? mode) =>
    "claude -r " + sessionId + (mode is null or "default" ? "" : $" --permission-mode {mode}");

string Tildify(string path) => path.StartsWith(home) ? "~" + path[home.Length..] : path;

static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

record Entry(string PaneUuid, string SessionId, string Cwd, string File);

class Tab
{
    public required string Cwd;
    public string? Command;
    public string? SessionId;
    public string? Title;

    public static Tab ForSession(Entry e) => new() { Cwd = e.Cwd, SessionId = e.SessionId };
    public static Tab Plain(string cwd) => new() { Cwd = cwd };
}
