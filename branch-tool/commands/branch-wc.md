# Branch Working Copy Command

Create a new working copy from any configured repository, set up for work on a specific feature branch.

## Usage

```
/branch-wc [repo] [branch-name]
```

**Arguments:**
- `[repo]`: Repository short name (optional - interactive if omitted)
- `[branch-name]`: The feature branch name to create (optional - prompted if omitted)

## Task

Invoke the branch-manager.cs tool to create an isolated working copy for parallel development.

## Instructions

1. **Run the branch manager tool** with provided arguments:
   ```bash
   dotnet run ~/.claude/lib/branch-manager.cs -- [args]
   ```

2. **Handle different invocation modes:**

   **No arguments (interactive mode):**
   ```bash
   dotnet run ~/.claude/lib/branch-manager.cs
   ```

   **With repo and branch:**
   ```bash
   dotnet run ~/.claude/lib/branch-manager.cs -- my-project feature/phase-4-1-api
   ```

   **List working copies:**
   ```bash
   dotnet run ~/.claude/lib/branch-manager.cs -- list [repo]
   ```

   **Configure repositories:**
   ```bash
   dotnet run ~/.claude/lib/branch-manager.cs -- config
   ```

3. **Report results** to user based on tool output

## Examples

**Create a working copy for a feature:**
```
/branch-wc my-project feature/phase-4-1-api
```

Result:
- New folder: `/Users/you/code/my-project-phase-4-1-api`
- Branch: `feature/phase-4-1-api`
- Terminal opened in new folder

**Interactive mode:**
```
/branch-wc
```

Prompts for repository selection and branch name.

**List all working copies:**
```
/branch-wc list
```

**List working copies for specific repo:**
```
/branch-wc list my-project
```

**Configure repositories:**
```
/branch-wc config
```

## First Run

On first run with no configuration, the tool enters onboarding mode:
1. Prompts for GitHub repository URL
2. Extracts org/repo display name
3. Asks for short name alias
4. Confirms code folder location
5. Offers to add shell alias

## Configuration

Configuration stored in `~/.claude/config/branch-repos.json`:

```json
{
  "settings": {
    "rootFolder": {
      "macos": "/Users/you/code",
      "windows": "D:\\Code"
    },
    "defaultTerminal": "warp"
  },
  "repositories": [
    {
      "shortName": "my-project",
      "url": "https://github.com/your-org/my-project",
      "displayName": "your-org/my-project",
      "defaultBranch": "main",
      "cloneSource": "auto"
    }
  ]
}
```

## Shell Alias

For direct command-line access (outside Claude Code):

**macOS/Linux (~/.zshrc):**
```bash
alias branch='dotnet run ~/.claude/lib/branch-manager.cs --'
```

**Windows (PowerShell profile):**
```powershell
function branch { dotnet run $env:USERPROFILE\.claude\lib\branch-manager.cs -- @args }
```

## Notes

- Uses local clone when available (faster), falls back to GitHub
- Opens Warp terminal in new working copy by default
- Handles existing folders intelligently (check branch, offer options)
- Cross-platform: macOS, Windows, and Linux support
