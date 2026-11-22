# Agent instructions

## Project

- Oshi Tuner is a Windows WPF application targeting .NET 10. Build it with `dotnet build src/oshituner/oshituner.csproj`.
- For Twitch OAuth, Helix requests, token storage, or followed-channel changes, read [.github/skills/twitch-api/SKILL.md](.github/skills/twitch-api/SKILL.md).

## Skills

- Read the relevant `SKILL.md` from [.agents/skills/](.agents/skills/) if the skill is absent from the session's catalog. Use the same file-based approach for skill-to-skill steps when the session has no skill launcher. Honor user-only invocation flags.
- Use [git-workflow-and-versioning](.agents/skills/git-workflow-and-versioning/SKILL.md) for code changes and Git workflows. Use [git-commit](.agents/skills/git-commit/SKILL.md) when the user requests a commit.
- Keep commits limited to build inputs and shared development files. [`.gitignore`](.gitignore) lists the allowed paths. Add a narrow exception when a change needs a new tracked path. Keep generated Graphify output local.
- Use `diagnosing-bugs` for debugging, `tdd` for test-first work, and `code-review` for diff review. For user-requested requirements or implementation workflows, read the named skill, such as `grill-me`, `grill-with-docs`, `implement`, or `implement-spec`.
- Keep [skills-lock.json](skills-lock.json) in version control. Update installed skills with `npx skills@latest update --project` when maintaining the skill set.

## Standalone `/push`

When the user's entire message is `/push`, it authorizes this sequence:

1. Review the changes and use the Git commit skill to commit the eligible files locally under the repository's commit policy. If there are no eligible changes, use the existing HEAD.
2. Push the current branch to its corresponding branch on `origin` with a normal, non-forced push.
3. After the push succeeds, run `graphify update .` from the repository root.

Report the commit, push result, and Graphify result. If a step fails, report the blocker and which steps completed.

## Agent skills

### Issue tracker

Issues and specs live in GitHub Issues. See [issue-tracker.md](docs/agents/issue-tracker.md).

### Triage labels

Use the five default labels in [triage-labels.md](docs/agents/triage-labels.md).

### Domain docs

Use the single-context layout in [domain.md](docs/agents/domain.md).

## Codebase knowledge graph

- For architecture and code relationships, use [Graphify](.agents/skills/graphify/SKILL.md) when `graphify-out/graph.json` and the CLI are available. In a dirty worktree, refresh the graph before querying:

  ```powershell
  graphify update .
  ```

- Use the query that matches the task:

  ```powershell
  graphify query "<question>"
  graphify explain "<node>"
  graphify path "<from>" "<to>"
  ```

- After changing code, run `graphify update .` when the CLI is available. If you cannot query the graph, use targeted source reads and text searches.
- If the shell denies access to a Graphify reference required for the task, request a read of that file outside the sandbox through the session's approval mechanism.

## Shell and search

- If PowerShell cannot find an installed tool, check its executable directly. On this Windows setup, Git is commonly at `$env:ProgramFiles\Git\cmd\git.exe`, Graphify at `$env:USERPROFILE\.local\bin\graphify.exe`, and npx at `$env:ProgramFiles\nodejs\npx.cmd`. Use PowerShell's call operator (`&`) with the full path.
- Use ripgrep for direct searches. Use `rg --files -g "<glob>"` to find files and `rg -n "<pattern>" <path>` to search text. Respect ignore rules and use `--hidden` when intentionally searching hidden paths. Use a targeted fallback if `rg` is unavailable.
