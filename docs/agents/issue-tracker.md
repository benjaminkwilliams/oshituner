# Issue tracker

Issues and specs live in GitHub Issues for `benjaminkwilliams/oshituner`. Use the GitHub CLI and pass `--repo benjaminkwilliams/oshituner` to issue and label commands.

- Fetch a ticket with `gh issue view <number> --json number,title,body,labels,comments`.
- List the queue with `gh issue list --state open --json number,title,body,labels`. Use `--label` to filter by a role in [triage-labels.md](triage-labels.md).
- Publish a spec or ticket with `gh issue create --title "<title>" --body-file <path>`.
- Use native GitHub sub-issues and issue dependencies for parent and blocking relationships. If those features are unavailable, record the links in the issue body as `Part of #<parent>` and `Blocked by: #<blocker>`.
- When resolving a ticket, post the resolution with `gh issue comment <number> --body-file <path>` and close it with `gh issue close <number>`.

**PRs as a request surface: no.**
