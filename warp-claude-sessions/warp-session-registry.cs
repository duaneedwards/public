#!/usr/bin/env dotnet
#:property PublishAot=false

// warp-session-registry.cs
//
// Claude Code hook (SessionStart + SessionEnd): maintains the Warp session
// registry used by snapshot-warp-sessions.cs. One JSON file per Warp pane,
// keyed by WARP_TERMINAL_SESSION_UUID:
//
//   ~/.claude/session-registry/<pane-uuid>.json
//   { "pane_uuid": "...", "session_id": "...", "cwd": "..." }
//
// SessionStart (startup/resume/clear) writes or overwrites the pane's entry;
// SessionEnd deletes it (only if the session id still matches, to avoid
// racing a newer session in the same tab). File mtime is the freshness
// timestamp. Outside Warp (no pane uuid) the hook is a silent no-op.
//
// Cross-platform: no shell-outs, no process inspection; the same file and
// the same settings.json command string ("dotnet run \"$HOME/...\"") work on
// macOS (bash) and Windows (PowerShell expands $HOME too).
//
// Wired in ~/.claude/settings.json under hooks.SessionStart / hooks.SessionEnd.
// Never blocks the session: prints nothing, always exits 0.

using System.Text.Json;

try
{
    var paneUuid = Environment.GetEnvironmentVariable("WARP_TERMINAL_SESSION_UUID");
    if (string.IsNullOrWhiteSpace(paneUuid)) return 0;
    // Defensive: pane uuid becomes a filename
    foreach (var ch in paneUuid) if (!char.IsAsciiLetterOrDigit(ch) && ch != '-') return 0;

    using var doc = JsonDocument.Parse(Console.In.ReadToEnd());
    var root = doc.RootElement;
    var eventName = root.TryGetProperty("hook_event_name", out var ev) ? ev.GetString() : null;
    var sessionId = root.TryGetProperty("session_id", out var sid) ? sid.GetString() : null;
    if (string.IsNullOrEmpty(sessionId)) return 0;

    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var registryDir = Path.Combine(home, ".claude", "session-registry");
    var file = Path.Combine(registryDir, paneUuid + ".json");

    if (eventName == "SessionEnd")
    {
        if (File.Exists(file))
        {
            using var existing = JsonDocument.Parse(File.ReadAllText(file));
            if (existing.RootElement.TryGetProperty("session_id", out var esid) && esid.GetString() == sessionId)
                File.Delete(file);
        }
        return 0;
    }

    // SessionStart (source: startup / resume / clear / compact)
    var cwd = root.TryGetProperty("cwd", out var c) ? c.GetString() ?? "" : "";
    Directory.CreateDirectory(registryDir);
    File.WriteAllText(file, JsonSerializer.Serialize(new
    {
        pane_uuid = paneUuid,
        session_id = sessionId,
        cwd,
    }));
    return 0;
}
catch
{
    return 0; // never block or noise up the session
}
