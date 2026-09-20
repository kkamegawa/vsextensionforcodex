---
name: github-wiki-plan
description: 'Publish an approved feature/refactor/perf plan to a repository''s GitHub wiki, bilingually (English + a `_ja` Japanese page), and keep the Home page''s grouped index table up to date. Use when a plan has been approved and needs to be recorded in the wiki, when the user asks to "publish this plan to the wiki", "wikiに登録して", "add this to the wiki", or "update the Home index" — and as the handoff target from github-plan-issues once tracking issues exist. Not for bug fixes, patches, or CI/CD-only changes, which skip wiki publishing entirely.'
---

# GitHub Wiki Plan

Publish an approved plan to the repository's GitHub wiki as a bilingual page pair, and keep the `Home` index current. Modeled on the reference wiki at https://github.com/kkamegawa/vsextensionforcodex/wiki.

Only run this for `feature`/`refactor`/`perf` work — bug fixes, patches, and CI/CD-only changes don't get wiki pages.

## Phase 0 — Confirm the wiki exists and is initialized

```bash
gh repo view <owner>/<repo> --json hasWikiEnabled
git ls-remote https://github.com/<owner>/<repo>.wiki.git
```

- `hasWikiEnabled: false` → stop. Ask the user to enable the wiki in repository settings.
- `hasWikiEnabled: true` but `ls-remote` returns **`Repository not found`** → the wiki feature is on but **uninitialized**. GitHub does not materialize a wiki's git repo (nor expose any API to create one) until a first page is saved through the web UI — there is no way to `git init`/push your way around this.
  **Ask the user for explicit permission to create the Home page**, and if they agree, have them open `https://github.com/<owner>/<repo>/wiki/_new` and save an initial `Home` page. Do not proceed until that exists — a subsequent `ls-remote` should now succeed.
- `ls-remote` succeeds → proceed to Phase 1.

## Phase 1 — Clone and lay out paths

```bash
git clone https://github.com/<owner>/<repo>.wiki.git <scratch-dir>/wiki
```

| Content | Path |
|---|---|
| Plan (English) | `plan/<yyyy-MM-dd>/<slug>.md` |
| Plan (Japanese) | `plan/<yyyy-MM-dd>/<slug>_ja.md` |
| Index (English) | `Home.md` |
| Index (Japanese) | `Home_ja.md` |

- `<yyyy-MM-dd>` is the plan's approval date (ISO, hyphenated — matches the reference wiki).
- `<slug>` is lowercase, hyphen-separated, derived from the plan title.
- **Wiki-internal links have no file extension** — link to a page's path without `.md` (a page saved as `plan/2026-07-19/approval-mode-picker.md` is linked as `plan/2026-07-19/approval-mode-picker`). A trailing `.md` produces a 404.
- The Japanese page is a translation of the same document, not a separate one — keep headings and tables in 1:1 correspondence with the English page.

## Phase 2 — Write the plan page

Start from [templates/plan-page.md](templates/plan-page.md). Include:

- Title, with a language-switch link at the top: link text "日本語" targeting `<slug>_ja` on the English page, link text "English" targeting `<slug>` on the Japanese page
- Links to the tracking issue(s) — parent issue and its sub-issues (issue links are allowed; this is the one exception to the placeholder rule below)
- Background, design, implementation phases, verification approach — drawn from the approved plan
- **Placeholder any URL, IP address, email address, or GUID**, per the team's documentation rules — with two exceptions: same-repo issue links, and public, unauthenticated website URLs that were actually referenced while designing the plan (e.g. a reference implementation or upstream doc the plan is modeled on). Anything else — internal/authenticated endpoints, private hosts, credentials-adjacent URLs — gets a placeholder.

## Phase 3 — Update the Home index

Match the reference wiki's structure exactly (verified against the live page):

- One introductory paragraph, plus a language-switch link to the other Home page.
- Content grouped under **H2 headings by feature area**, each with a 4-column table:

```markdown
## Slash Commands & Composer

| Plan | Date | Tracking | 日本語 |
|---|---|---|---|
| <plan title, linked to its wiki path> | 2026-07-19 | <issue title, linked to the issue URL> | <"日本語", linked to the _ja page path> |
```

**Read the existing `Home.md` first** — do not copy group names from [templates/home.md](templates/home.md) mechanically. Steps:

1. Find the group that best fits the new plan's subject area and append a row to its table.
2. If no existing group fits, create a new `## ` section — and tell the user you did, since it changes the page's structure.
3. **Update `Home_ja.md` with the same row**, translated (column headers in Japanese; the "日本語" column becomes an "English" column pointing back at the non-`_ja` page). Never update only one language.

Use [templates/home.md](templates/home.md) / [templates/home_ja.md](templates/home_ja.md) only as a starting skeleton for a wiki that has no `Home` page yet (Phase 0's uninitialized case) — an existing Home always wins over the template.

## Phase 4 — Commit and push

Pushing to the wiki repo publishes immediately with no review step — **present the file list and the Home diff to the user and get confirmation before pushing.**

```bash
git -C <scratch-dir>/wiki add plan/<date>/<slug>.md plan/<date>/<slug>_ja.md Home.md Home_ja.md
git -C <scratch-dir>/wiki commit -m "add: plan <slug>"
git -C <scratch-dir>/wiki push origin master
```

After pushing, verify both the new page and the Home link resolve. There is no REST API for wiki page content, so this is a browser check, not a `gh`/`curl` command:

Open `https://github.com/<owner>/<repo>/wiki/plan/<date>/<slug>` and confirm it's not a 404, and that the link from Home lands there.

## Pitfalls

- An uninitialized wiki cannot be cloned or pushed to, no matter what — `hasWikiEnabled: true` only means the feature is turned on, not that content exists. There is no API-based way to create the first page.
- Wiki-internal links with a `.md` suffix 404. Omit the extension.
- The wiki's own page list/sidebar is flat regardless of your `plan/<date>/` subdirectories — `Home.md` is the only real navigation. Skipping the Home update leaves the new page effectively unreachable.
- The wiki is a separate git repository from the main one — no branch protection, no PR review; a push is instantly live.
- The `_ja` suffix is a naming convention this team uses, not a GitHub feature — cross-links between language pairs must be added by hand in both directions.

## Reference

- [templates/plan-page.md](templates/plan-page.md) — starter structure for a new plan page.
- [templates/home.md](templates/home.md), [templates/home_ja.md](templates/home_ja.md) — starter skeleton, English/Japanese, for a wiki with no existing Home page.
