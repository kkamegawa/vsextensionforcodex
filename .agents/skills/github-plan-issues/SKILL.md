---
name: github-plan-issues
description: 'Break an approved implementation plan into a GitHub issue hierarchy — one parent issue plus sub-issues — using the gh CLI''s native sub-issue support, instead of a pile of unrelated flat issues. Use when a plan has been approved and needs tracking issues, when a single feature needs multiple issues (always parent + sub-issues, never flat), when a task has 3+ subtasks or touches 5+ files, or when the user asks to "create issues for this plan", "break this down into issues", "issueを作って", "サブイシューに分割して". Hands off to github-wiki-plan for feature/refactor/perf work once issues exist.'
---

# GitHub Plan Issues

Turn an approved plan into a GitHub issue hierarchy with the `gh` CLI (2.6x+, tested against 2.97.0). The core rule: **when a piece of work needs more than one issue, it is always a parent issue with sub-issues — never a set of independent flat issues.**

## Phase 0 — Preconditions and work-type classification

1. Confirm the remote is GitHub:
   ```bash
   git remote -v
   ```
   If it isn't (GitLab, Azure DevOps, no remote), stop — this skill only covers GitHub. Azure DevOps work items are a different workflow.

2. Confirm `gh` supports sub-issues:
   ```bash
   gh --version
   gh auth status
   gh issue create --help | grep -E "parent|type|blocked-by"
   ```
   `--parent`, `--type`, `--blocked-by`/`--blocking` should appear. If `--parent` is missing, `gh` predates native sub-issue support — use the REST/GraphQL fallback in [references/gh-subissue-commands.md](references/gh-subissue-commands.md) instead.

3. **Classify the work** — this decision drives Phase 4's wiki handoff:
   - `feature`, `refactor`, `perf` → plan gets published to the wiki after issues are created.
   - bug fix, patch, CI/CD fix, typo, dependency bump → no wiki publishing.
   - If it's genuinely ambiguous, ask the user — don't default to "no wiki" just because it's easier.

## Phase 1 — Decide parent vs. sub-issues

Apply these rules in order:

- **A single feature needing 2+ issues → always one parent issue + sub-issues.** Do not create independent flat issues that reference each other loosely; use real parent/child linkage.
- **Even a single-issue feature gets split into sub-issues if it has 3+ distinct tasks, or the plan touches more than 5 files.**
- Otherwise, one issue is enough — skip straight to `gh issue create` with no `--parent`.

Present the planned breakdown (parent title, each sub-issue title, labels, issue type) to the user and **wait for confirmation before creating anything**. Creating issues is visible to every collaborator and awkward to cleanly undo — don't skip this even if the plan itself was already approved, since the exact titles/numbers here are new.

## Phase 2 — Create the parent issue

Check what labels and issue types actually exist before using them — passing a nonexistent `--type` fails the call:

```bash
gh label list --repo <owner>/<repo>
gh api orgs/<org>/issue-types --jq '.[].name'   # empty/404 on personal repos and some orgs — fall back to labels only
```

```bash
gh issue create --repo <owner>/<repo> \
  --title "<feature title>" \
  --body-file <path-to-body.md> \
  --type Enhancement \
  --label enhancement
```

Body should include: a one-paragraph summary, acceptance criteria, and a checklist of the sub-issues to come (fill in numbers after Phase 3).

## Phase 3 — Create sub-issues

```bash
gh issue create --repo <owner>/<repo> --parent <parent-number> \
  --title "<task title>" \
  --body-file <path-to-body.md> \
  --type Task
```

`--parent` accepts either the issue number or full URL and links the child at creation time — no separate linking step needed.

- To link an **existing** issue as a child instead: `gh issue edit <child> --parent <parent>`, or from the parent side `gh issue edit <parent> --add-sub-issue <a>,<b>`.
- To express ordering/dependencies between sub-issues: `gh issue create ... --blocked-by <n> --blocking <m>`.
- **Verify the hierarchy before reporting done:**
  ```bash
  gh issue view <parent> --repo <owner>/<repo> --json subIssues,subIssuesSummary
  ```
  Confirm `subIssuesSummary.total` matches the number of sub-issues you created.

## Phase 4 — Branch and wiki handoff

- Create the topic branch per the team's branch naming convention (e.g. `feature/<issue-number>-<short-description>`); reuse `git-flow-branch-creator` if available.
- If Phase 0 classified this as `feature`/`refactor`/`perf`, **load and run the `github-wiki-plan` skill** (`../github-wiki-plan/SKILL.md`), passing it: parent issue number + URL, plan body, slug, and approval date.
- If it was classified as work-type (bugfix/patch/CI), skip the wiki step and say so explicitly — don't silently omit it.

## Pitfalls

- Running `gh` from outside the repo directory fails with "could not determine current branch" — always pass `--repo <owner>/<repo>` explicitly, especially in scripts.
- `--parent` requires `gh` 2.6x+. Older versions need the REST fallback, which takes the issue's **database id** (`gh api repos/{owner}/{repo}/issues/{number} --jq .id`), not the issue number — see [references/gh-subissue-commands.md](references/gh-subissue-commands.md).
- Issue types (`Task`/`Bug`/`Enhancement`/...) are defined at the organization level and may not exist on personal repos — check with `gh api orgs/<org>/issue-types` before passing `--type`, or fall back to labels only.
- Never put raw URLs, IP addresses, or email addresses in issue bodies — use placeholders. Exceptions: links to issues within the same repository, and public, unauthenticated URLs actually referenced while designing the plan (e.g. a reference implementation cited in the plan).

## Reference

- [references/gh-subissue-commands.md](references/gh-subissue-commands.md) — REST/GraphQL fallback for `gh` versions without native `--parent`/`--add-sub-issue` support.
