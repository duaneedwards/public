#!/usr/bin/env dotnet
#:package Spectre.Console@*
#:property PublishAot=false

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Spectre.Console;

// ============================================================================
// Branch Manager - Cross-Platform Working Copy CLI Tool
// ============================================================================
// Manages working copy branches across multiple repositories with quick
// spinning up of isolated working copies and automatic terminal launching.
//
// Usage:
//   dotnet run branch-manager.cs                      # Interactive mode
//   dotnet run branch-manager.cs <repo> <branch>      # Create working copy
//   dotnet run branch-manager.cs list [repo]          # List working copies
//   dotnet run branch-manager.cs config               # Configure repositories
//
// Examples:
//   dotnet run branch-manager.cs my-project feature/phase-4-1-api
//   dotnet run branch-manager.cs list
//   dotnet run branch-manager.cs list my-project
//   dotnet run branch-manager.cs config
// ============================================================================

var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var configDir = Path.Combine(homeDir, ".claude", "config");
var configPath = Path.Combine(configDir, "branch-repos.json");
var platform = DetectPlatform();

// JSON options for pretty-printing
var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

// Parse command line arguments
var cmdArgs = args;

try
{
    // Ensure config directory exists
    Directory.CreateDirectory(configDir);

    // Load or create config
    var config = LoadConfig();

    // Handle commands
    if (cmdArgs.Length == 0)
    {
        return RunInteractive(config);
    }

    var command = cmdArgs[0].ToLower();

    return command switch
    {
        "config" => RunConfig(config),
        "list" => RunList(config, cmdArgs.Skip(1).ToArray()),
        "help" or "--help" or "-h" => ShowHelp(),
        _ when cmdArgs.Length >= 2 => RunCreateBranch(config, cmdArgs[0], cmdArgs[1]),
        _ => RunCreateBranchInteractive(config, cmdArgs[0])
    };
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
    return 1;
}

// ============================================================================
// Configuration Management
// ============================================================================

BranchConfig LoadConfig()
{
    if (!File.Exists(configPath))
    {
        return new BranchConfig
        {
            Settings = new ConfigSettings
            {
                RootFolder = new Dictionary<string, string>
                {
                    ["macos"] = Path.Combine(homeDir, "Documents", "code"),
                    ["windows"] = @"D:\Checkouts",
                    ["linux"] = Path.Combine(homeDir, "code")
                },
                DefaultTerminal = "warp"
            },
            Repositories = new List<Repository>()
        };
    }

    var json = File.ReadAllText(configPath);
    return JsonSerializer.Deserialize<BranchConfig>(json, jsonOptions) ?? new BranchConfig();
}

void SaveConfig(BranchConfig config)
{
    var json = JsonSerializer.Serialize(config, jsonOptions);
    File.WriteAllText(configPath, json);
}

string GetRootFolder(BranchConfig config)
{
    if (config.Settings?.RootFolder == null)
        return Path.Combine(homeDir, "Documents", "code");

    if (config.Settings.RootFolder.TryGetValue(platform, out var folder))
        return folder;

    return Path.Combine(homeDir, "Documents", "code");
}

// ============================================================================
// Interactive Mode
// ============================================================================

int RunInteractive(BranchConfig config)
{
    // Check if first run (no repositories configured)
    if (config.Repositories == null || config.Repositories.Count == 0)
    {
        return RunOnboarding(config);
    }

    var rootFolder = GetRootFolder(config);

    AnsiConsole.Clear();
    AnsiConsole.Write(new Rule("[cyan]Branch Manager[/]").RuleStyle("grey"));
    AnsiConsole.WriteLine();

    // Get working copies with remote status check
    var workingCopies = GetWorkingCopies(config, rootFolder, checkRemotes: true);

    if (workingCopies.Count == 0)
    {
        AnsiConsole.MarkupLine("[grey]No working copies found.[/]");
        AnsiConsole.WriteLine();

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("What would you like to do?")
                .AddChoices("+ Create new working copy", "* Configure", "x Exit"));

        return choice switch
        {
            "+ Create new working copy" => RunCreateBranchInteractiveMenu(config),
            "* Configure" => RunConfig(config),
            _ => 0
        };
    }

    var mergedCount = workingCopies.Count(wc => wc.IsMerged);

    // Show table of working copies
    var table = new Table();
    table.Border(TableBorder.Rounded);
    table.AddColumn(new TableColumn("#").Centered());
    table.AddColumn("Folder");
    table.AddColumn("Branch");
    table.AddColumn("Status");
    table.AddColumn("Modified");
    table.AddColumn(new TableColumn("Remote").Centered());

    for (int i = 0; i < workingCopies.Count; i++)
    {
        var wc = workingCopies[i];
        var statusColor = wc.Status == "clean" ? "green" : "yellow";
        var remoteStatus = wc.IsMainRepo ? "[grey]-[/]" :
                          wc.RemoteBranchExists ? "[green]✓[/]" :
                          "[red]merged[/]";
        var folderStyle = wc.IsMerged ? "grey strikethrough" : "blue";

        table.AddRow(
            $"[grey]{i + 1}[/]",
            $"[{folderStyle}]{TruncateString(wc.FolderName, 28)}[/]",
            TruncateString(wc.Branch, 26),
            $"[{statusColor}]{wc.Status}[/]",
            $"[grey]{wc.RelativeTime}[/]",
            remoteStatus
        );
    }

    AnsiConsole.Write(table);
    AnsiConsole.WriteLine();

    if (mergedCount > 0)
    {
        AnsiConsole.MarkupLine($"[grey]Found [/][red]{mergedCount}[/][grey] working cop{(mergedCount == 1 ? "y" : "ies")} with merged/deleted remote branches[/]");
        AnsiConsole.WriteLine();
    }

    // Build selection choices - working copies first, then actions
    var choices = new List<string>();
    foreach (var wc in workingCopies)
    {
        var suffix = wc.IsMerged ? " (merged)" : "";
        choices.Add($"{wc.FolderName}{suffix}");
    }
    choices.Add("─────────────────────────────────────");
    choices.Add("+ Create new working copy");
    if (mergedCount > 0)
    {
        choices.Add($"~ Clean up {mergedCount} merged branch{(mergedCount == 1 ? "" : "es")}");
    }
    choices.Add("* Configure");
    choices.Add("x Exit");

    var selection = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("Select working copy to open:")
            .PageSize(15)
            .HighlightStyle(new Style(Color.Cyan1))
            .AddChoices(choices));

    // Handle selection
    if (selection.StartsWith("+ "))
    {
        return RunCreateBranchInteractiveMenu(config);
    }
    else if (selection.StartsWith("~ "))
    {
        return RunCleanupMerged(config, workingCopies.Where(wc => wc.IsMerged).ToList());
    }
    else if (selection.StartsWith("* "))
    {
        return RunConfig(config);
    }
    else if (selection.StartsWith("x ") || selection.StartsWith("───"))
    {
        return 0;
    }
    else
    {
        // Find the selected working copy and open it (strip " (merged)" suffix if present)
        var folderName = selection.Replace(" (merged)", "");
        var selected = workingCopies.FirstOrDefault(wc => wc.FolderName == folderName);
        if (selected != null)
        {
            OpenTerminal(selected.FullPath, config.Settings?.DefaultTerminal ?? "warp");
        }
        return 0;
    }
}

List<WorkingCopyInfo> GetWorkingCopies(BranchConfig config, string rootFolder, bool checkRemotes = false)
{
    var workingCopies = new List<WorkingCopyInfo>();

    if (!Directory.Exists(rootFolder))
        return workingCopies;

    var directories = Directory.GetDirectories(rootFolder)
        .Select(d => new DirectoryInfo(d))
        .OrderByDescending(d => d.LastWriteTime)
        .ToList();

    // Build main repo names from configured repositories
    // These are the "base" folders that are not working copies
    var mainRepoNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (config.Repositories != null)
    {
        foreach (var repo in config.Repositories)
        {
            if (!string.IsNullOrEmpty(repo.ShortName))
            {
                mainRepoNames.Add(repo.ShortName);
            }
            // Also add the repo name from displayName (e.g., "org/repo" -> "repo")
            if (!string.IsNullOrEmpty(repo.DisplayName))
            {
                var repoName = repo.DisplayName.Split('/').Last();
                mainRepoNames.Add(repoName);
            }
        }
    }

    foreach (var dir in directories)
    {
        var gitDir = Path.Combine(dir.FullName, ".git");
        if (!Directory.Exists(gitDir))
            continue;

        var branch = GetCurrentBranch(dir.FullName);
        var status = GetGitStatusShort(dir.FullName);
        var relativeTime = FormatRelativeTime(dir.LastWriteTime);

        // Determine if this is a main repo or a working copy
        var isMainRepo = mainRepoNames.Contains(dir.Name);

        // Derive expected branch from folder name for working copies
        var expectedBranch = "";
        if (!isMainRepo)
        {
            expectedBranch = DeriveExpectedBranch(dir.Name, mainRepoNames);
        }

        var wc = new WorkingCopyInfo
        {
            FolderName = dir.Name,
            FullPath = dir.FullName,
            Branch = branch,
            ExpectedBranch = expectedBranch,
            Status = status,
            LastModified = dir.LastWriteTime,
            RelativeTime = relativeTime,
            RemoteBranchExists = true, // Default to true, check below if requested
            IsMainRepo = isMainRepo
        };

        workingCopies.Add(wc);
    }

    // Check remote branches if requested (can be slow due to network)
    if (checkRemotes && workingCopies.Count > 0)
    {
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Checking remote branches...", ctx =>
            {
                foreach (var wc in workingCopies)
                {
                    if (!wc.IsMainRepo && !string.IsNullOrEmpty(wc.ExpectedBranch))
                    {
                        // Check if the expected feature branch exists on remote
                        wc.RemoteBranchExists = CheckRemoteBranchExists(wc.FullPath, wc.ExpectedBranch);
                    }
                }
            });
    }

    return workingCopies;
}

string DeriveExpectedBranch(string folderName, HashSet<string> repoNames)
{
    // Try to extract branch name from folder name
    // e.g., "my-project-phase-4-1-3-2-manifest-editor" -> "feature/phase-4-1-3-2-manifest-editor"

    foreach (var repoName in repoNames)
    {
        var prefix = repoName + "-";
        if (folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var branchPart = folderName.Substring(prefix.Length);
            // Return as feature branch
            return $"feature/{branchPart}";
        }
    }

    // If no known repo prefix, try to detect pattern
    // Look for common patterns like "reponame-branchpart"
    var dashIndex = folderName.IndexOf('-');
    if (dashIndex > 0)
    {
        var branchPart = folderName.Substring(dashIndex + 1);
        return $"feature/{branchPart}";
    }

    return "";
}

int RunCleanupMerged(BranchConfig config, List<WorkingCopyInfo> mergedCopies)
{
    AnsiConsole.Clear();
    AnsiConsole.Write(new Rule("[cyan]Clean Up Merged Branches[/]").RuleStyle("grey"));
    AnsiConsole.WriteLine();

    if (mergedCopies.Count == 0)
    {
        AnsiConsole.MarkupLine("[grey]No merged branches to clean up.[/]");
        return 0;
    }

    AnsiConsole.MarkupLine($"Found [red]{mergedCopies.Count}[/] working cop{(mergedCopies.Count == 1 ? "y" : "ies")} with merged/deleted remote branches:");
    AnsiConsole.WriteLine();

    var table = new Table();
    table.Border(TableBorder.Rounded);
    table.AddColumn("Folder");
    table.AddColumn("Branch");
    table.AddColumn("Status");
    table.AddColumn("Modified");

    foreach (var wc in mergedCopies)
    {
        var statusColor = wc.Status == "clean" ? "green" : "yellow";
        table.AddRow(
            $"[grey]{wc.FolderName}[/]",
            wc.Branch,
            $"[{statusColor}]{wc.Status}[/]",
            $"[grey]{wc.RelativeTime}[/]"
        );
    }

    AnsiConsole.Write(table);
    AnsiConsole.WriteLine();

    // Check if any have uncommitted changes
    var withChanges = mergedCopies.Where(wc => wc.Status != "clean").ToList();
    if (withChanges.Count > 0)
    {
        AnsiConsole.MarkupLine($"[yellow]Warning:[/] {withChanges.Count} working cop{(withChanges.Count == 1 ? "y has" : "ies have")} uncommitted changes:");
        foreach (var wc in withChanges)
        {
            AnsiConsole.MarkupLine($"  [yellow]*[/] {wc.FolderName}");
        }
        AnsiConsole.WriteLine();
    }

    // Let user select which to delete
    var toDelete = AnsiConsole.Prompt(
        new MultiSelectionPrompt<string>()
            .Title("Select working copies to delete:")
            .PageSize(15)
            .InstructionsText("[grey](Press [blue]<space>[/] to toggle, [green]<enter>[/] to confirm)[/]")
            .AddChoices(mergedCopies.Select(wc =>
            {
                var warning = wc.Status != "clean" ? " [yellow](has changes)[/]" : "";
                return $"{wc.FolderName}{warning}";
            })));

    if (toDelete.Count == 0)
    {
        AnsiConsole.MarkupLine("[grey]No working copies selected. Nothing deleted.[/]");
        return 0;
    }

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine($"[red]This will permanently delete {toDelete.Count} folder{(toDelete.Count == 1 ? "" : "s")}![/]");

    if (!AnsiConsole.Confirm("Are you sure?", false))
    {
        AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
        return 0;
    }

    // Delete selected folders
    var deleted = 0;
    var failed = 0;

    AnsiConsole.WriteLine();
    foreach (var selection in toDelete)
    {
        // Extract folder name (strip warning suffix)
        var folderName = selection.Split(" [yellow]")[0];
        var wc = mergedCopies.FirstOrDefault(w => w.FolderName == folderName);

        if (wc == null) continue;

        try
        {
            Directory.Delete(wc.FullPath, recursive: true);
            AnsiConsole.MarkupLine($"[green]Deleted:[/] {wc.FolderName}");
            deleted++;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to delete {wc.FolderName}:[/] {ex.Message}");
            failed++;
        }
    }

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine($"[green]Deleted {deleted}[/] working cop{(deleted == 1 ? "y" : "ies")}{(failed > 0 ? $", [red]{failed} failed[/]" : "")}");

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[grey]Press any key to continue...[/]");
    Console.ReadKey(true);

    return RunInteractive(config);
}

string FormatRelativeTime(DateTime dateTime)
{
    var now = DateTime.Now;
    var diff = now - dateTime;

    if (diff.TotalMinutes < 1)
        return "just now";
    if (diff.TotalMinutes < 60)
        return $"{(int)diff.TotalMinutes}m ago";
    if (diff.TotalHours < 24)
        return $"{(int)diff.TotalHours}h ago";
    if (diff.TotalDays < 2)
        return "Yesterday";
    if (diff.TotalDays < 7)
        return $"{(int)diff.TotalDays}d ago";
    if (diff.TotalDays < 30)
        return $"{(int)(diff.TotalDays / 7)}w ago";
    if (diff.TotalDays < 365)
        return $"{(int)(diff.TotalDays / 30)}mo ago";

    return $"{(int)(diff.TotalDays / 365)}y ago";
}

string TruncateString(string str, int maxLength)
{
    if (string.IsNullOrEmpty(str) || str.Length <= maxLength)
        return str;
    return str.Substring(0, maxLength - 3) + "...";
}

int RunOnboarding(BranchConfig config)
{
    AnsiConsole.Clear();
    AnsiConsole.Write(new FigletText("Branch Manager").Color(Color.Cyan1));
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[yellow]Welcome! Let's set up your first repository.[/]");
    AnsiConsole.WriteLine();

    // Step 1: Get GitHub URL
    var url = AnsiConsole.Ask<string>("Paste the GitHub repository URL:");
    url = url.Trim();

    // Extract org/repo from URL
    var displayName = ExtractDisplayName(url);
    if (displayName == null)
    {
        AnsiConsole.MarkupLine("[red]Could not parse repository URL.[/]");
        return 1;
    }

    AnsiConsole.MarkupLine($"[green]Detected:[/] {displayName}");

    // Step 2: Get short name
    var suggestedShortName = displayName.Split('/').Last().ToLower();
    var shortName = AnsiConsole.Prompt(
        new TextPrompt<string>($"Enter a short name (alias):")
            .DefaultValue(suggestedShortName));

    // Step 3: Confirm root folder
    var currentRoot = GetRootFolder(config);
    AnsiConsole.MarkupLine($"[grey]Current code folder:[/] {currentRoot}");

    if (AnsiConsole.Confirm("Use this folder?", true))
    {
        // Keep current
    }
    else
    {
        var newRoot = AnsiConsole.Ask<string>("Enter your code folder path:");
        config.Settings ??= new ConfigSettings();
        config.Settings.RootFolder ??= new Dictionary<string, string>();
        config.Settings.RootFolder[platform] = newRoot;
    }

    // Step 4: Get default branch
    var defaultBranch = AnsiConsole.Prompt(
        new TextPrompt<string>("Default branch:")
            .DefaultValue("main"));

    // Create repository entry
    config.Repositories ??= new List<Repository>();
    config.Repositories.Add(new Repository
    {
        ShortName = shortName,
        Url = url,
        DisplayName = displayName,
        DefaultBranch = defaultBranch,
        CloneSource = "auto"
    });

    // Save config
    SaveConfig(config);

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[green]Configuration saved![/]");
    AnsiConsole.MarkupLine($"[grey]Config file:[/] {configPath}");
    AnsiConsole.WriteLine();

    // Offer shell integration
    if (AnsiConsole.Confirm("Would you like to add the 'branch' alias to your shell profile?", true))
    {
        AddShellAlias();
    }
    else
    {
        ShowShellAliasInstructions();
    }

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[cyan]You're all set! Run 'branch' again to create a working copy.[/]");

    return 0;
}

string? ExtractDisplayName(string url)
{
    // Handle various URL formats:
    // https://github.com/org/repo
    // https://github.com/org/repo.git
    // git@github.com:org/repo.git

    var patterns = new[]
    {
        @"github\.com[:/]([^/]+)/([^/.]+)(?:\.git)?$",
        @"github\.com[:/]([^/]+)/([^/]+)$"
    };

    foreach (var pattern in patterns)
    {
        var match = Regex.Match(url, pattern);
        if (match.Success)
        {
            return $"{match.Groups[1].Value}/{match.Groups[2].Value}";
        }
    }

    return null;
}

// ============================================================================
// Create Working Copy
// ============================================================================

int RunCreateBranchInteractiveMenu(BranchConfig config)
{
    if (config.Repositories == null || config.Repositories.Count == 0)
    {
        AnsiConsole.MarkupLine("[yellow]No repositories configured. Run 'branch config' first.[/]");
        return 1;
    }

    // Select repository
    var repoChoice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("Select repository:")
            .AddChoices(config.Repositories.Select(r => $"{r.ShortName} ({r.DisplayName})")));

    var shortName = repoChoice.Split(' ')[0];

    // Get branch name
    var branch = AnsiConsole.Ask<string>("Branch name (e.g., feature/my-feature):");

    return RunCreateBranch(config, shortName, branch);
}

int RunCreateBranchInteractive(BranchConfig config, string shortName)
{
    var repo = FindRepository(config, shortName);
    if (repo == null)
    {
        AnsiConsole.MarkupLine($"[red]Repository not found:[/] {shortName}");
        SuggestRepositories(config, shortName);
        return 1;
    }

    var branch = AnsiConsole.Ask<string>("Branch name (e.g., feature/my-feature):");
    return RunCreateBranch(config, shortName, branch);
}

int RunCreateBranch(BranchConfig config, string shortName, string branch)
{
    var repo = FindRepository(config, shortName);
    if (repo == null)
    {
        AnsiConsole.MarkupLine($"[red]Repository not found:[/] {shortName}");
        SuggestRepositories(config, shortName);
        return 1;
    }

    var rootFolder = GetRootFolder(config);
    var folderName = DeriveFolderName(repo.ShortName ?? "", branch);
    var targetPath = Path.Combine(rootFolder, folderName);

    AnsiConsole.WriteLine();
    AnsiConsole.Write(new Rule("[cyan]Creating Working Copy[/]").RuleStyle("grey"));
    AnsiConsole.WriteLine();

    // Check if folder exists
    if (Directory.Exists(targetPath))
    {
        return HandleExistingFolder(config, repo, branch, targetPath);
    }

    // Find clone source
    var cloneSource = FindCloneSource(config, repo, rootFolder);

    AnsiConsole.MarkupLine($"[grey]Repository:[/] {repo.DisplayName}");
    AnsiConsole.MarkupLine($"[grey]Branch:[/] {branch}");
    AnsiConsole.MarkupLine($"[grey]Folder:[/] {folderName}");
    AnsiConsole.MarkupLine($"[grey]Clone from:[/] {cloneSource}");
    AnsiConsole.WriteLine();

    // Clone repository
    AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .Start("Cloning repository...", ctx =>
        {
            RunGit($"clone \"{cloneSource}\" \"{targetPath}\"", rootFolder);
        });

    // Configure git
    AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .Start("Configuring git...", ctx =>
        {
            // Set remote to GitHub URL
            RunGit($"remote set-url origin \"{repo.Url}\"", targetPath);

            // Fetch from origin
            RunGit("fetch origin", targetPath);

            // Checkout default branch first
            RunGit($"checkout {repo.DefaultBranch ?? "main"}", targetPath);

            // Create and checkout new branch
            // Check if branch exists on remote
            var remoteBranchExists = CheckRemoteBranchExists(targetPath, branch);
            if (remoteBranchExists)
            {
                RunGit($"checkout {branch}", targetPath);
            }
            else
            {
                RunGit($"checkout -b {branch}", targetPath);
            }
        });

    // Success summary
    AnsiConsole.WriteLine();
    AnsiConsole.Write(new Rule("[green]Working Copy Created[/]").RuleStyle("grey"));
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine($"[grey]Location:[/] {targetPath}");
    AnsiConsole.MarkupLine($"[grey]Branch:[/] {branch}");
    AnsiConsole.MarkupLine($"[grey]Remote:[/] {repo.Url}");
    AnsiConsole.WriteLine();

    // Print CLI equivalent
    AnsiConsole.MarkupLine($"[dim]CLI equivalent: branch {repo.ShortName} {branch}[/]");
    AnsiConsole.WriteLine();

    // Prompt to open terminal
    if (AnsiConsole.Confirm("Open in Warp terminal?", true))
    {
        OpenTerminal(targetPath, config.Settings?.DefaultTerminal ?? "warp");
    }

    return 0;
}

int HandleExistingFolder(BranchConfig config, Repository repo, string branch, string targetPath)
{
    AnsiConsole.MarkupLine($"[yellow]Folder already exists:[/] {targetPath}");
    AnsiConsole.WriteLine();

    // Check if it's a git repo
    var gitDir = Path.Combine(targetPath, ".git");
    if (!Directory.Exists(gitDir))
    {
        AnsiConsole.MarkupLine("[yellow]Warning: Not a git repository[/]");

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("What would you like to do?")
                .AddChoices("Open folder anyway", "Abort"));

        if (choice == "Abort")
            return 1;

        OpenTerminal(targetPath, config.Settings?.DefaultTerminal ?? "warp");
        return 0;
    }

    // Get current branch
    var currentBranch = GetCurrentBranch(targetPath);
    var status = GetGitStatus(targetPath);

    AnsiConsole.MarkupLine($"[green]Found existing working copy[/]");
    AnsiConsole.MarkupLine($"  Branch: {currentBranch}");
    AnsiConsole.MarkupLine($"  Status: {status}");
    AnsiConsole.WriteLine();

    if (currentBranch == branch)
    {
        AnsiConsole.MarkupLine("[green]Branch matches requested branch[/]");
    }
    else
    {
        AnsiConsole.MarkupLine($"[yellow]Branch mismatch:[/] {currentBranch} vs {branch}");
    }

    var action = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("What would you like to do?")
            .AddChoices("Open in terminal", "Switch to requested branch", "Delete and recreate", "Abort"));

    return action switch
    {
        "Open in terminal" => OpenTerminalAndReturn(targetPath, config.Settings?.DefaultTerminal ?? "warp"),
        "Switch to requested branch" => SwitchBranchAndOpen(targetPath, branch, config.Settings?.DefaultTerminal ?? "warp"),
        "Delete and recreate" => DeleteAndRecreate(config, repo, branch, targetPath),
        _ => 1
    };
}

int OpenTerminalAndReturn(string path, string terminal)
{
    OpenTerminal(path, terminal);
    return 0;
}

int SwitchBranchAndOpen(string path, string branch, string terminal)
{
    AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .Start("Switching branch...", ctx =>
        {
            var remoteBranchExists = CheckRemoteBranchExists(path, branch);
            if (remoteBranchExists)
            {
                RunGit($"checkout {branch}", path);
            }
            else
            {
                RunGit($"checkout -b {branch}", path);
            }
        });

    AnsiConsole.MarkupLine($"[green]Switched to branch:[/] {branch}");
    OpenTerminal(path, terminal);
    return 0;
}

int DeleteAndRecreate(BranchConfig config, Repository repo, string branch, string targetPath)
{
    if (!AnsiConsole.Confirm($"[red]Delete[/] {targetPath}?", false))
    {
        return 1;
    }

    Directory.Delete(targetPath, true);
    AnsiConsole.MarkupLine("[yellow]Folder deleted[/]");
    AnsiConsole.WriteLine();

    return RunCreateBranch(config, repo.ShortName ?? "", branch);
}

// ============================================================================
// List Working Copies
// ============================================================================

int RunList(BranchConfig config, string[] args)
{
    var rootFolder = GetRootFolder(config);

    if (!Directory.Exists(rootFolder))
    {
        AnsiConsole.MarkupLine($"[yellow]Root folder not found:[/] {rootFolder}");
        return 1;
    }

    var filter = args.Length > 0 ? args[0].ToLower() : null;

    AnsiConsole.WriteLine();
    AnsiConsole.Write(new Rule("[cyan]Working Copies[/]").RuleStyle("grey"));
    AnsiConsole.MarkupLine($"[grey]Root:[/] {rootFolder}");
    AnsiConsole.WriteLine();

    var directories = Directory.GetDirectories(rootFolder)
        .Select(d => new DirectoryInfo(d))
        .OrderByDescending(d => d.LastWriteTime)
        .ToList();

    var table = new Table();
    table.AddColumn("Folder");
    table.AddColumn("Branch");
    table.AddColumn("Status");
    table.AddColumn("Last Modified");

    var foundCount = 0;

    foreach (var dir in directories)
    {
        // Filter by repo name if specified
        if (filter != null)
        {
            var repo = FindRepository(config, filter);
            if (repo != null && !dir.Name.StartsWith(repo.ShortName ?? "", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
        }

        var gitDir = Path.Combine(dir.FullName, ".git");
        if (!Directory.Exists(gitDir))
        {
            continue;
        }

        var branch = GetCurrentBranch(dir.FullName);
        var status = GetGitStatusShort(dir.FullName);
        var modified = dir.LastWriteTime.ToString("MMM dd, HH:mm");

        var statusColor = status.Contains("clean") ? "green" : "yellow";

        table.AddRow(
            $"[blue]{dir.Name}[/]",
            branch,
            $"[{statusColor}]{status}[/]",
            $"[grey]{modified}[/]"
        );

        foundCount++;
    }

    if (foundCount == 0)
    {
        AnsiConsole.MarkupLine("[grey]No working copies found.[/]");
    }
    else
    {
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[grey]Found {foundCount} working copies[/]");
    }

    return 0;
}

// ============================================================================
// Configuration
// ============================================================================

int RunConfig(BranchConfig config)
{
    while (true)
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule("[cyan]Branch Manager Configuration[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        // Show current config
        AnsiConsole.MarkupLine($"[grey]Config file:[/] {configPath}");
        AnsiConsole.MarkupLine($"[grey]Root folder ({platform}):[/] {GetRootFolder(config)}");
        AnsiConsole.MarkupLine($"[grey]Default terminal:[/] {config.Settings?.DefaultTerminal ?? "warp"}");
        AnsiConsole.WriteLine();

        if (config.Repositories != null && config.Repositories.Count > 0)
        {
            var table = new Table();
            table.AddColumn("Short Name");
            table.AddColumn("Display Name");
            table.AddColumn("Default Branch");
            table.AddColumn("Clone Source");

            foreach (var repo in config.Repositories)
            {
                table.AddRow(
                    $"[cyan]{repo.ShortName}[/]",
                    repo.DisplayName ?? "",
                    repo.DefaultBranch ?? "main",
                    repo.CloneSource ?? "auto"
                );
            }

            AnsiConsole.Write(table);
        }
        else
        {
            AnsiConsole.MarkupLine("[grey]No repositories configured[/]");
        }

        AnsiConsole.WriteLine();

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Configuration options:")
                .AddChoices("Add repository", "Remove repository", "Edit settings", "Show shell alias", "Back to menu", "Exit"));

        switch (choice)
        {
            case "Add repository":
                AddRepository(config);
                break;
            case "Remove repository":
                RemoveRepository(config);
                break;
            case "Edit settings":
                EditSettings(config);
                break;
            case "Show shell alias":
                ShowShellAliasInstructions();
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[grey]Press any key to continue...[/]");
                Console.ReadKey(true);
                break;
            case "Back to menu":
                return RunInteractive(config);
            case "Exit":
                return 0;
        }
    }
}

void AddRepository(BranchConfig config)
{
    AnsiConsole.WriteLine();

    var url = AnsiConsole.Ask<string>("GitHub repository URL:");
    var displayName = ExtractDisplayName(url);

    if (displayName == null)
    {
        AnsiConsole.MarkupLine("[red]Could not parse URL[/]");
        return;
    }

    AnsiConsole.MarkupLine($"[green]Detected:[/] {displayName}");

    var suggestedShortName = displayName.Split('/').Last().ToLower();
    var shortName = AnsiConsole.Prompt(
        new TextPrompt<string>("Short name:")
            .DefaultValue(suggestedShortName));

    var defaultBranch = AnsiConsole.Prompt(
        new TextPrompt<string>("Default branch:")
            .DefaultValue("main"));

    var cloneSource = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("Clone source:")
            .AddChoices("auto", "local", "github"));

    config.Repositories ??= new List<Repository>();
    config.Repositories.Add(new Repository
    {
        ShortName = shortName,
        Url = url,
        DisplayName = displayName,
        DefaultBranch = defaultBranch,
        CloneSource = cloneSource
    });

    SaveConfig(config);
    AnsiConsole.MarkupLine("[green]Repository added![/]");
}

void RemoveRepository(BranchConfig config)
{
    if (config.Repositories == null || config.Repositories.Count == 0)
    {
        AnsiConsole.MarkupLine("[grey]No repositories to remove[/]");
        return;
    }

    var choice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("Select repository to remove:")
            .AddChoices(config.Repositories.Select(r => r.ShortName ?? "").Append("Cancel")));

    if (choice == "Cancel")
        return;

    config.Repositories.RemoveAll(r => r.ShortName == choice);
    SaveConfig(config);
    AnsiConsole.MarkupLine($"[yellow]Removed:[/] {choice}");
}

void EditSettings(BranchConfig config)
{
    var choice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("Edit setting:")
            .AddChoices("Root folder", "Default terminal", "Cancel"));

    switch (choice)
    {
        case "Root folder":
            var newRoot = AnsiConsole.Prompt(
                new TextPrompt<string>($"Root folder for {platform}:")
                    .DefaultValue(GetRootFolder(config)));
            config.Settings ??= new ConfigSettings();
            config.Settings.RootFolder ??= new Dictionary<string, string>();
            config.Settings.RootFolder[platform] = newRoot;
            SaveConfig(config);
            break;
        case "Default terminal":
            var terminal = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Default terminal:")
                    .AddChoices("warp", "iterm", "terminal"));
            config.Settings ??= new ConfigSettings();
            config.Settings.DefaultTerminal = terminal;
            SaveConfig(config);
            break;
    }
}

// ============================================================================
// Repository Matching
// ============================================================================

Repository? FindRepository(BranchConfig config, string input)
{
    if (config.Repositories == null || config.Repositories.Count == 0)
        return null;

    input = input.ToLower();

    // Exact match on short name
    var exact = config.Repositories.FirstOrDefault(r =>
        r.ShortName?.ToLower() == input);
    if (exact != null)
        return exact;

    // Prefix match on short name
    var prefixMatches = config.Repositories.Where(r =>
        r.ShortName?.ToLower().StartsWith(input) == true).ToList();
    if (prefixMatches.Count == 1)
        return prefixMatches[0];

    // Display name contains
    var displayMatches = config.Repositories.Where(r =>
        r.DisplayName?.ToLower().Contains(input) == true).ToList();
    if (displayMatches.Count == 1)
        return displayMatches[0];

    // Multiple matches - prompt user
    var allMatches = prefixMatches.Concat(displayMatches).Distinct().ToList();
    if (allMatches.Count > 1)
    {
        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"Multiple matches for '{input}':")
                .AddChoices(allMatches.Select(r => $"{r.ShortName} ({r.DisplayName})")));

        var shortName = choice.Split(' ')[0];
        return config.Repositories.FirstOrDefault(r => r.ShortName == shortName);
    }

    return null;
}

void SuggestRepositories(BranchConfig config, string input)
{
    if (config.Repositories == null || config.Repositories.Count == 0)
    {
        AnsiConsole.MarkupLine("[grey]No repositories configured. Run 'branch config' to add one.[/]");
        return;
    }

    AnsiConsole.MarkupLine("[grey]Available repositories:[/]");
    foreach (var repo in config.Repositories)
    {
        AnsiConsole.MarkupLine($"  {repo.ShortName} ({repo.DisplayName})");
    }
}

// ============================================================================
// Folder Naming
// ============================================================================

string DeriveFolderName(string repoName, string branch)
{
    // Strip common prefixes
    var cleanBranch = branch;
    var prefixes = new[] { "feature/", "bugfix/", "hotfix/", "release/" };

    foreach (var prefix in prefixes)
    {
        if (cleanBranch.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            cleanBranch = cleanBranch.Substring(prefix.Length);
            break;
        }
    }

    // Sanitize
    cleanBranch = Regex.Replace(cleanBranch.ToLower(), @"[^a-z0-9-]", "-");
    cleanBranch = Regex.Replace(cleanBranch, @"-+", "-");
    cleanBranch = cleanBranch.Trim('-');

    return $"{repoName.ToLower()}-{cleanBranch}";
}

// ============================================================================
// Clone Source Detection
// ============================================================================

string FindCloneSource(BranchConfig config, Repository repo, string rootFolder)
{
    var cloneSource = repo.CloneSource?.ToLower() ?? "auto";

    // Check for local source path override
    if (!string.IsNullOrEmpty(repo.LocalSourcePath) && Directory.Exists(repo.LocalSourcePath))
    {
        if (cloneSource == "local" || cloneSource == "auto")
        {
            return repo.LocalSourcePath;
        }
    }

    // Auto or local: check for local repo
    if (cloneSource == "auto" || cloneSource == "local")
    {
        var repoName = repo.DisplayName?.Split('/').Last() ?? repo.ShortName ?? "";
        var localPath = Path.Combine(rootFolder, repoName);

        if (Directory.Exists(localPath) && Directory.Exists(Path.Combine(localPath, ".git")))
        {
            // Verify it's the right repo (check remote URL)
            var remoteUrl = GetRemoteUrl(localPath);
            if (remoteUrl != null && (remoteUrl.Contains(repo.DisplayName ?? "") ||
                                       remoteUrl.Contains(repoName)))
            {
                return localPath;
            }
        }

        if (cloneSource == "local")
        {
            throw new Exception($"Local source not found for {repo.ShortName}. Expected: {localPath}");
        }
    }

    // Fall back to GitHub
    return repo.Url ?? throw new Exception($"No URL configured for {repo.ShortName}");
}

// ============================================================================
// Git Operations
// ============================================================================

void RunGit(string arguments, string workingDirectory)
{
    var psi = new ProcessStartInfo
    {
        FileName = "git",
        Arguments = arguments,
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    using var process = Process.Start(psi);
    process?.WaitForExit();

    if (process?.ExitCode != 0)
    {
        var error = process?.StandardError.ReadToEnd();
        throw new Exception($"Git command failed: {arguments}\n{error}");
    }
}

string? GetRemoteUrl(string path)
{
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "remote get-url origin",
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        var output = process?.StandardOutput.ReadToEnd().Trim();
        process?.WaitForExit();

        return process?.ExitCode == 0 ? output : null;
    }
    catch
    {
        return null;
    }
}

string GetCurrentBranch(string path)
{
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "rev-parse --abbrev-ref HEAD",
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        var output = process?.StandardOutput.ReadToEnd().Trim();
        process?.WaitForExit();

        return output ?? "unknown";
    }
    catch
    {
        return "unknown";
    }
}

string GetGitStatus(string path)
{
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "status --porcelain",
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        var output = process?.StandardOutput.ReadToEnd().Trim();
        process?.WaitForExit();

        if (string.IsNullOrEmpty(output))
            return "Clean";

        var lines = output.Split('\n').Length;
        return $"{lines} uncommitted change(s)";
    }
    catch
    {
        return "Unknown";
    }
}

string GetGitStatusShort(string path)
{
    var status = GetGitStatus(path);
    return status.Contains("Clean") ? "clean" : status;
}

bool CheckRemoteBranchExists(string path, string branch)
{
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"ls-remote --heads origin {branch}",
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        var output = process?.StandardOutput.ReadToEnd().Trim();
        process?.WaitForExit();

        return !string.IsNullOrEmpty(output);
    }
    catch
    {
        return false;
    }
}

// ============================================================================
// Terminal Launching
// ============================================================================

void OpenTerminal(string path, string terminal)
{
    if (platform == "macos")
    {
        switch (terminal.ToLower())
        {
            case "warp":
                Process.Start("open", $"\"warp://action/new_window?path={path}\"");
                break;
            case "iterm":
                var script = $"tell application \"iTerm\" to create window with default profile command \"cd '{path}' && exec $SHELL\"";
                Process.Start("osascript", $"-e \"{script}\"");
                break;
            case "terminal":
            default:
                Process.Start("open", $"-a Terminal \"{path}\"");
                break;
        }
    }
    else if (platform == "windows")
    {
        switch (terminal.ToLower())
        {
            case "warp":
                var warpUrl = $"warp://action/new_window?path={path.Replace("\\", "/")}";
                Process.Start(new ProcessStartInfo { FileName = warpUrl, UseShellExecute = true });
                break;
            default:
                // Windows Terminal or cmd
                Process.Start(new ProcessStartInfo
                {
                    FileName = "wt",
                    Arguments = $"-d \"{path}\"",
                    UseShellExecute = true
                });
                break;
        }
    }
    else
    {
        // Linux - try common terminals
        var terminals = new[] { "gnome-terminal", "konsole", "xterm" };
        foreach (var term in terminals)
        {
            try
            {
                Process.Start(term, $"--working-directory=\"{path}\"");
                break;
            }
            catch { }
        }
    }

    AnsiConsole.MarkupLine($"[green]Opened terminal in:[/] {path}");
}

// ============================================================================
// Shell Integration
// ============================================================================

void AddShellAlias()
{
    var (shellProfile, shellName) = GetShellProfile();

    if (shellProfile == null)
    {
        AnsiConsole.MarkupLine("[yellow]Could not detect shell profile[/]");
        ShowShellAliasInstructions();
        return;
    }

    var scriptPath = Path.Combine(homeDir, ".claude", "lib", "branch-manager.cs");
    string aliasLine;

    if (platform == "windows")
    {
        aliasLine = $"function branch {{ dotnet run {scriptPath} -- @args }}";
    }
    else
    {
        aliasLine = $"alias branch='dotnet run {scriptPath} --'";
    }

    var lines = File.Exists(shellProfile) ? File.ReadAllLines(shellProfile).ToList() : new List<string>();

    // Check if alias already exists
    if (lines.Any(l => l.Contains("alias branch=") || l.Contains("function branch")))
    {
        AnsiConsole.MarkupLine("[yellow]Alias already exists in shell profile[/]");
        return;
    }

    lines.Add("");
    lines.Add("# Branch Manager CLI");
    lines.Add(aliasLine);

    File.WriteAllLines(shellProfile, lines);

    AnsiConsole.MarkupLine($"[green]Added alias to:[/] {shellProfile}");
    AnsiConsole.MarkupLine($"[grey]Run 'source {shellProfile}' or restart your terminal[/]");
}

void ShowShellAliasInstructions()
{
    var scriptPath = Path.Combine(homeDir, ".claude", "lib", "branch-manager.cs");

    AnsiConsole.WriteLine();
    AnsiConsole.Write(new Rule("[cyan]Shell Alias Setup[/]").RuleStyle("grey"));
    AnsiConsole.WriteLine();

    if (platform == "macos" || platform == "linux")
    {
        AnsiConsole.MarkupLine("[grey]Add to ~/.zshrc or ~/.bashrc:[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[cyan]alias branch='dotnet run {scriptPath} --'[/]");
    }
    else
    {
        AnsiConsole.MarkupLine("[grey]Add to PowerShell profile ($PROFILE):[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[cyan]function branch {{ dotnet run {scriptPath} -- @args }}[/]");
    }

    AnsiConsole.WriteLine();
}

(string? profile, string? name) GetShellProfile()
{
    if (platform == "windows")
    {
        var pwshProfile = Path.Combine(homeDir, "Documents", "PowerShell", "Microsoft.PowerShell_profile.ps1");
        if (File.Exists(pwshProfile))
            return (pwshProfile, "pwsh");

        var winPwshProfile = Path.Combine(homeDir, "Documents", "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1");
        if (File.Exists(winPwshProfile))
            return (winPwshProfile, "powershell");

        return (null, null);
    }

    var zshrc = Path.Combine(homeDir, ".zshrc");
    if (File.Exists(zshrc))
        return (zshrc, "zsh");

    var bashrc = Path.Combine(homeDir, ".bashrc");
    if (File.Exists(bashrc))
        return (bashrc, "bash");

    return (null, null);
}

// ============================================================================
// Help
// ============================================================================

int ShowHelp()
{
    AnsiConsole.Write(new Rule("[cyan]Branch Manager[/]").RuleStyle("grey"));
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[bold]Usage:[/]");
    Console.WriteLine("  branch                        Interactive mode");
    Console.WriteLine("  branch <repo> <branch>        Create working copy");
    Console.WriteLine("  branch list [repo]            List working copies");
    Console.WriteLine("  branch config                 Configure repositories");
    Console.WriteLine("  branch help                   Show this help");
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[bold]Examples:[/]");
    AnsiConsole.MarkupLine("  branch my-project feature/phase-4-1-api");
    AnsiConsole.MarkupLine("  branch list");
    AnsiConsole.MarkupLine("  branch list my-project");
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine($"[grey]Config file: {configPath}[/]");

    return 0;
}

// ============================================================================
// Utilities
// ============================================================================

string DetectPlatform()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        return "windows";
    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        return "macos";
    return "linux";
}

// ============================================================================
// Models
// ============================================================================

class BranchConfig
{
    public ConfigSettings? Settings { get; set; }
    public List<Repository>? Repositories { get; set; }
}

class ConfigSettings
{
    public Dictionary<string, string>? RootFolder { get; set; }
    public string? DefaultTerminal { get; set; }
}

class Repository
{
    public string? ShortName { get; set; }
    public string? Url { get; set; }
    public string? DisplayName { get; set; }
    public string? DefaultBranch { get; set; }
    public string? CloneSource { get; set; }
    public string? LocalSourcePath { get; set; }
}

class WorkingCopyInfo
{
    public string FolderName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string Branch { get; set; } = "";
    public string ExpectedBranch { get; set; } = ""; // Derived from folder name
    public string Status { get; set; } = "";
    public DateTime LastModified { get; set; }
    public string RelativeTime { get; set; } = "";
    public bool RemoteBranchExists { get; set; } = true;
    public bool IsMainRepo { get; set; } = false; // True for main repo folders, not working copies
    public bool IsMerged => !RemoteBranchExists && !IsMainRepo;
}
