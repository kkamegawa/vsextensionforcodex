# Sub-issue commands: native flags vs. REST/GraphQL fallback

`gh` 2.6x+ (verified against 2.97.0 in this environment) has native sub-issue support, which `SKILL.md` uses by default. This reference is for two cases: an older `gh` binary, or needing to inspect the raw API response directly.

## Verifying which path you need

```bash
gh --version
gh issue create --help | grep parent
```

If `--parent` doesn't appear in the help output, use the REST fallback below.

## Native path (preferred)

```bash
# Create parent
gh issue create --repo <owner>/<repo> --title "..." --body-file body.md

# Create sub-issue directly under a parent
gh issue create --repo <owner>/<repo> --parent <parent-number> --title "..." --body-file body.md

# Link an existing issue as a child
gh issue edit <child-number> --repo <owner>/<repo> --parent <parent-number>

# Link from the parent side (comma-separated, multiple at once)
gh issue edit <parent-number> --repo <owner>/<repo> --add-sub-issue <child1>,<child2>

# Remove links
gh issue edit <child-number> --repo <owner>/<repo> --remove-parent
gh issue edit <parent-number> --repo <owner>/<repo> --remove-sub-issue <child-number>

# Read back the hierarchy
gh issue view <parent-number> --repo <owner>/<repo> --json parent,subIssues,subIssuesSummary
```

## REST fallback (`gh` without `--parent`, or direct API inspection)

The sub-issues REST endpoints take the issue's **database id**, not its number — this is the detail most likely to trip you up.

```bash
# Get the database id for an issue number (NOT the same as the issue number itself)
gh api repos/<owner>/<repo>/issues/<number> --jq '.id'

# List sub-issues of a parent (by parent issue *number*, this one does accept the number)
gh api repos/<owner>/<repo>/issues/<parent-number>/sub_issues

# Add an existing issue as a sub-issue (sub_issue_id is the database id from above)
gh api repos/<owner>/<repo>/issues/<parent-number>/sub_issues \
  --method POST \
  -f sub_issue_id=<child-database-id>

# Remove a sub-issue link
gh api repos/<owner>/<repo>/issues/<parent-number>/sub_issue \
  --method DELETE \
  -f sub_issue_id=<child-database-id>

# Reprioritize/reorder sub-issues under a parent
gh api repos/<owner>/<repo>/issues/<parent-number>/sub_issues/priority \
  --method PATCH \
  -f sub_issue_id=<child-database-id> \
  -f after_id=<other-child-database-id>
```

There is no REST endpoint to create-and-link in one call — always `gh issue create` (without `--parent`) to make the child issue first, then POST it onto the parent's `sub_issues` endpoint using its database id.

## GraphQL (only needed for bulk/paginated reads)

For repos with many sub-issues where the REST list needs pagination handled manually, `gh api graphql` with the `Issue.subIssues` connection works the same way `consolidate-dependabot-prs` uses GraphQL for review threads — prefer the REST/native paths above unless you specifically need cursor-based pagination.
