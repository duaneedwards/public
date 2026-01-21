# Branch Working Copy Command

Create a new working copy of the current repository in a sister folder, set up for work on a specific feature branch.

## Usage

```
/branch <branch-name>
```

**Arguments:**
- `<branch-name>`: The feature branch name to create (e.g., `feature/phase-4-1-admin-api-foundation`)

## Task

Create an isolated working copy for parallel development on a feature branch.

## Instructions

1. **Validate prerequisites**:
   - Confirm current directory is a git repository
   - Get the current branch name (this will be the base branch)
   - Get the git remote URL for origin

2. **Determine folder name**:
   - Strip `feature/` prefix from branch name if present
   - Use the remaining part as the folder name
   - Sister folder path: `{parent-directory}/{folder-name}`

   Example: `feature/phase-4-1-admin-api-foundation` → `my-project-phase-4-1-admin-api-foundation`

3. **Check for conflicts**:
   - If the sister folder already exists, ask user what to do:
     - Use existing folder and just create/checkout branch
     - Abort

4. **Clone the repository**:
   ```bash
   git clone <current-repo-path> <sister-folder-path>
   ```

5. **Configure the clone**:
   ```bash
   cd <sister-folder-path>
   # Set remote to the actual origin (not the local clone)
   git remote set-url origin <original-remote-url>
   # Fetch from real origin
   git fetch origin
   # Checkout the base branch
   git checkout <base-branch>
   # Create and checkout the new feature branch
   git checkout -b <branch-name>
   ```

6. **Report success**:
   - Show the new working copy location
   - Show the branch name
   - Show the base branch it was created from
   - Show the remote URL

## Examples

**From a tech-stack branch, create an implementation branch:**
```
/branch feature/phase-4-1-admin-api-foundation
```

Result:
- New folder: `/Users/you/code/my-project-phase-4-1-admin-api-foundation`
- Branch: `feature/phase-4-1-admin-api-foundation`
- Based on: `feature/phase-4-react-admin-tech-stack`

**Create a branch with full path:**
```
/branch feature/add-user-authentication
```

Result:
- New folder: `{parent}/my-project-add-user-authentication`
- Branch: `feature/add-user-authentication`

## Folder Naming Rules

The folder name is derived from:
1. Take the branch name argument
2. Remove `feature/` prefix if present
3. If the current repo folder starts with the original repo name, use that as prefix
4. Otherwise, use current folder name as prefix

Examples:
| Current Folder | Branch | New Folder |
|----------------|--------|------------|
| `my-project-phase-4-react-admin` | `feature/phase-4-1-api` | `my-project-phase-4-1-api` |
| `my-project` | `feature/new-feature` | `my-project-new-feature` |
| `another-repo` | `feature/auth` | `another-repo-auth` |

## Error Handling

- If not in a git repository: Report error and exit
- If no remote origin: Report error and exit
- If sister folder exists: Prompt user for action
- If branch already exists on remote: Offer to check it out instead of creating

## Notes

- The clone uses the local repo for speed, then updates the remote to point to the actual origin
- This enables parallel work on different phases/features without branch switching
- Works with the stacked PR workflow where branches target other feature branches
