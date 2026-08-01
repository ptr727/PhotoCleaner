# Copilot Instructions

Repository conventions for GitHub Copilot (and any other AI agent reading this file).

The **canonical guide is [AGENTS.md](../AGENTS.md)** at the repo root. Read it first, then the [PR Review Etiquette](../GOVERNANCE.md#pr-review-etiquette) review-loop contract this file's runbook implements. This file is intentionally narrow: commit/PR-title conventions (summarized inline so VS Code's commit-message and PR-title generators have them), guidance for reviewing carried fleet content, plus the GitHub Copilot Review Runbook.

For code-style rules, see [`CODESTYLE.md`](../CODESTYLE.md) at the repo root, one guide with a General section plus a section per language the repo uses.

Do not duplicate language-specific rules here. **Project-specific conventions and API/behavioral contracts also belong in [GOVERNANCE.md](../GOVERNANCE.md), not here.** This file is intentionally limited to the inline commit/PR-title summary, the guidance for reviewing carried fleet content, and the GitHub Copilot Review Runbook. Non-Copilot agents (Claude Code, Codex, Cursor, ...) are not directed to this file and don't read it by default, so any rule a reviewer must honor has to live in `GOVERNANCE.md`, routed to from `AGENTS.md`, to be provider-independent.

## Commit Messages and Pull Request Titles

Summarized for VS Code's generators. The full rules, rationale, and examples are in [GOVERNANCE.md "Pull Request Title and Commit Message Conventions"](../GOVERNANCE.md#pull-request-title-and-commit-message-conventions).

- Imperative subject, <= 72 characters, no trailing period, with an optional blank-line-separated body for the non-obvious *why*.
- US English, title case with lowercase short bind words. No vague titles, no `Co-Authored-By:` unless asked, no release-bump magnitude (NBGV handles versioning). Dependabot's `Bump X from Y to Z` titles are fine.
- develop PRs squash-merge (`gh pr merge --squash`), main PRs merge-commit (`--merge`). A mismatched flag is rejected by branch protection.

## Reviewing Carried Fleet Content

Several of this repository's governance files are carried from a shared template and kept in sync across a fleet of sibling repositories, among them `AGENTS.md`, `GOVERNANCE.md`, `CODESTYLE.md`, `WORKFLOW.md`, this file, and the `repo-config/` rulesets. Most of `GOVERNANCE.md` is universal fleet law: every section that states a rule, as opposed to the two that describe this repository's own directory tree and devcontainer, is byte-locked and verified by an automated byte-for-byte match against the template canonical, not by line-by-line review. The two sections `AGENTS.md` carries are byte-locked on the same terms.

Two constraints follow when reviewing that content.

- **A reference inside byte-locked text to a path or section this repository does not carry is intentional, not a broken link.** Universal rule text names shared infrastructure (a fleet registry, a reusable config snippet, the other workflow model's ruleset payload) that a given repository legitimately may not contain. Editing the text to "fix" such a reference would break the fleet audit that governs it, so the reference is correct as written. Do not report it as a dead link, a missing file, or a broken cross-reference.
- **A genuine substantive defect is still worth raising.** Byte-locked is not unreviewable. A self-contradiction, a factual error, or a real typo in the canonical prose is a valid finding, but note that the fix lands at the template and re-vendors to every repository, rather than proposing a local edit the audit would reject.

## GitHub Copilot Review Runbook

> This runbook implements the [GOVERNANCE.md "PR Review Etiquette"](../GOVERNANCE.md#pr-review-etiquette) review-loop contract for GitHub Copilot. Without it in-repo, an agent has no pointer to the reliable Copilot mechanics and falls back to known-broken paths (the no-op `POST /requested_reviewers`, the wrong bot-login filter). In the API snippets below, fill the `<N>` placeholder (the PR number).

Use this section for provider-specific mechanics. The expected review loop *contract* (request review on every push, verify head-SHA coverage, triage findings, reply + resolve, escalate when stuck) is defined in [GOVERNANCE.md -> PR Review Etiquette](../GOVERNANCE.md#pr-review-etiquette). This section only describes how to make GitHub Copilot reliably execute it.

### Triggering and Polling

Auto-review on push is configured (via the branch ruleset's `copilot_code_review` rule with `review_on_push: true`) but fires inconsistently in practice - treat it as best-effort, not guaranteed. After every push, **re-request a review programmatically** via the GraphQL `requestReviews` mutation, passing the Copilot reviewer's bot node id in `botIds`. This drives the loop end-to-end without a UI hand-off.

**A review with no inline comments is still a completed review - not a failure, and not a reason to ask the maintainer to re-trigger.** Copilot very often posts a single formal review (GraphQL `state: COMMENTED`) whose body ends with "...reviewed N of N changed files ... and generated no comments" and adds **zero** inline threads. That review carries the head `commit.oid` and fully satisfies the loop - it is the clean-pass success case. Never read "no inline comments" as "the review didn't run," and never re-request or escalate to the maintainer because comments are absent.

**Round 1 is normally auto-seeded - poll for it before trying to self-trigger.** Auto-review-on-open supplies the first review with no `botIds` call needed, but it can lag one to three minutes. After opening a PR (or the first push), **poll** for a Copilot review on the head SHA (see [Verify Review Covered Current Head](#verify-review-covered-current-head)) before concluding none ran. The `requestReviews` mutation below is for **re-requesting on later pushes** (a new head SHA); by then a prior review exists, so its bot node id is readable. A missing bot node id on round 1 therefore means "the auto-review has not landed yet - wait and poll," **not** "ask the maintainer to kick it off."

> **The reviewer login differs by API.** In **GraphQL** (`gh api graphql` and `gh pr view --json reviews`, which is GraphQL-backed) the `Bot.login` is `copilot-pull-request-reviewer` - **no `[bot]` suffix**. In the **REST** API (`gh api repos/.../issues|pulls/...`) the same account's `user.login` is `copilot-pull-request-reviewer[bot]` - **with** the suffix. Each query below uses the correct form for its API; match the API, not a single spelling, when adapting them.

```sh
# 1. PR node id + the Copilot reviewer's bot node id (read from any existing
#    Copilot review; the reviewer login is `copilot-pull-request-reviewer`).
PR_NODE=$(gh pr view <N> --json id --jq '.id')
BOT_ID=$(gh api graphql -f query='
{
  repository(owner: "ptr727", name: "PhotoCleaner") {
    pullRequest(number: <N>) {
      reviews(first: 50) { nodes { author { __typename login ... on Bot { id } } } }
    }
  }
}' --jq '[.data.repository.pullRequest.reviews.nodes[]
          | select(.author.login == "copilot-pull-request-reviewer")
          | .author.id] | first')

# 2. Re-request a Copilot review on the current head.
gh api graphql -f query='
mutation($pr: ID!, $bot: ID!) {
  requestReviews(input: { pullRequestId: $pr, botIds: [$bot], union: true }) {
    pullRequest { id }
  }
}' -F pr="$PR_NODE" -F bot="$BOT_ID"
```

The bot node id is read from an existing Copilot **formal** review (`pullRequest.reviews`), so step 1 needs at least one prior formal review on the PR - the auto-review-on-open normally supplies the first one (it may have **no inline comments**; that still counts, and its bot node id is still readable). Poll for it (give auto-review-on-open a few minutes) before deciding it is missing.

**Cold start (round 1 not yet landed): read the id repo-wide, not from this PR.** The Copilot reviewer's bot node id is the reviewer bot *account's* node id and is **stable across every PR in the repo**. So a freshly opened PR that has neither a formal review nor an issue comment yet does **not** need UI seeding to bootstrap the id - read it from any prior Copilot review anywhere in the repo, then feed it into the `requestReviews` mutation to drive round 1. Query the **most recent** PRs (`first: 20` with an explicit newest-first order; plain `last: 20` returns the *oldest* PRs, which may predate Copilot on the repo), and **guard for an empty result** - an empty `$BOT_ID` means none of the sampled PRs carry a Copilot review. Widen the window (raise the count or paginate) before concluding the repo has never had one and falling back to UI seeding; never feed an empty id into the mutation:

```sh
BOT_ID=$(gh api graphql -f query='
{
  repository(owner: "ptr727", name: "PhotoCleaner") {
    pullRequests(first: 20, orderBy: { field: CREATED_AT, direction: DESC }) {
      nodes { reviews(first: 20) { nodes { author { __typename login ... on Bot { id } } } } }
    }
  }
}' --jq '[.data.repository.pullRequests.nodes[].reviews.nodes[]
          | select(.author.login == "copilot-pull-request-reviewer")
          | .author.id] | first // empty')
if [ -z "$BOT_ID" ]; then
  echo "no Copilot review in the 20 most recent PRs - widen the window, else fall back to UI seeding" >&2
  return 1 2>/dev/null || exit 1   # stop; do NOT call requestReviews with an empty id
fi
```

If Copilot posted **only an issue comment** on this PR and no formal review, you can instead read the id from that comment's author (`pullRequest.comments` -> author `... on Bot { id }`). Manual UI seeding is the last resort - needed only for a repo that has **never** had a Copilot review, so no prior id exists anywhere to read; then use the mutation for every subsequent re-request.

**Do NOT post `@Copilot review` as a PR comment.** That comment triggers the Copilot *coding agent* (`copilot-swe-agent[bot]`), which makes code changes rather than posting a review.

Known non-working request paths (don't rely on them - use the `requestReviews` mutation above instead):

- `POST /requested_reviewers` with `reviewers=[Copilot]` can return 200 but no-op.
- `copilot-pull-request-reviewer` as a requested reviewer slug returns 422.
- `requestReviews` with the reviewer's bot node id in **`userIds`** fails with `Could not resolve to User node` - the Copilot reviewer is a **Bot**, so its node id goes in **`botIds`** (as in the mutation above), never `userIds`.
- `suggestedActors(capabilities: [CAN_BE_ASSIGNED])` lists `copilot-swe-agent` (the coding agent), not `copilot-pull-request-reviewer` - do not source the reviewer's bot node id there. Read it from an existing review per step 1 above.
- There is no `removePullRequestFromReviewRequest` mutation, and removing the reviewer to force a fresh pass is unnecessary anyway - `requestReviews` with `union: true` re-fires the review on the current head.

### Verify Review Covered Current Head

Before merging, confirm Copilot reviewed the current PR head SHA. Copilot may respond as either a formal review (carries an exact commit SHA) or an issue comment (no SHA - use the most recent Copilot comment for manual confirmation). Check both.

```sh
PR_HEAD=$(gh pr view <N> --json headRefOid --jq '.headRefOid')

# 1. Formal review - exact SHA match.
gh pr view <N> --json reviews --jq \
  '.reviews[] | select(.author.login=="copilot-pull-request-reviewer") | .commit.oid' \
  | grep -q "$PR_HEAD" && echo "covered via formal review"

# 2. Issue comment - show the most recent Copilot comment for manual
#    confirmation. This is the REST API, so the login carries the `[bot]` suffix.
gh api repos/ptr727/PhotoCleaner/issues/<N>/comments --jq \
  '[.[] | select(.user.login=="copilot-pull-request-reviewer[bot]")] | last | {created_at, body: .body[:200]}'
```

Coverage is confirmed when (1) exits 0 - **a formal review with no inline comments still satisfies path (1)**, because coverage is about the head SHA, not the comment count. For issue comments (path 2), body content is the only reliable signal - `created_at` is not: `git log -1 --format=%cI` is the **commit** timestamp, not the push timestamp, so amended or rebased commits can have an earlier timestamp and an older Copilot comment could satisfy a time check even though Copilot never saw the current head. Treat path (2) as confirmed only when the comment body explicitly refers to the current changes.

### Bounded Retry Workflow

This path is only for a **genuinely missing** review - no Copilot review (formal *or* issue comment) covers the current head SHA after polling. A review that covered the head but produced no comments is a clean pass, not a missing review; do not enter this retry path for it.

If a review did not run on the current head, retry:

1. Wait briefly and check head-SHA coverage (see above).
1. Re-request the review via the `requestReviews` mutation (see "Triggering and Polling"); fall back to the GitHub PR UI only if the mutation no-ops.
1. Retry up to two more times (three total).
1. If still missing, mark review as blocked and escalate to the user/maintainer with what was attempted.

### Reply and Thread Resolution Workflow

Every id below is captured from a live query into a variable and passed from there - never hand-typed, guessed, or pasted as a `PRRT_...` literal. A node id resolves globally, so a fabricated or stale id does not fail, it writes to a real thread on an unrelated repository. This runbook implements [GOVERNANCE.md "Repository Boundaries and Write Safety"](../GOVERNANCE.md#repository-boundaries-and-write-safety): write only to this repo, capture every id from a live query, and never suppress a mutation's output.

List unresolved threads. Use `first: 100` with cursor-based pagination; if `hasNextPage` is true, re-run with `after: "<endCursor>"` to retrieve the next page:

```sh
gh api graphql -f query='
{
  repository(owner: "ptr727", name: "PhotoCleaner") {
    pullRequest(number: <N>) {
      reviewThreads(first: 100) {
        nodes {
          id isResolved path
          comments(first: 1) { nodes { author { login } body } }
        }
        pageInfo { hasNextPage endCursor }
      }
    }
  }
}' | jq '
  .data.repository.pullRequest.reviewThreads |
  (.pageInfo | "hasNextPage=\(.hasNextPage) endCursor=\(.endCursor)"),
  (.nodes[] | select(.isResolved == false))
'
```

Reply on a thread, then resolve it. Capture the target thread's id into `$TID` from the listing query above - filter to the thread being answered by its `path`, and guard for an empty result so a mutation never runs on a guessed id. When a file carries more than one unresolved thread, `path` alone is ambiguous and `head -n 1` would pick the wrong one, so narrow by first-comment body - the query already fetches `comments(first: 1)` for this - by adding `and (.comments.nodes[0].body | contains("<SNIPPET>"))` to the `select`:

```sh
TID=$(gh api graphql -f query='
{
  repository(owner: "ptr727", name: "PhotoCleaner") {
    pullRequest(number: <N>) {
      reviewThreads(first: 100) {
        nodes { id isResolved path comments(first: 1) { nodes { body } } }
      }
    }
  }
}' --jq '.data.repository.pullRequest.reviewThreads.nodes[]
  | select(.isResolved == false and .path == "<PATH>")
  | .id' | head -n 1)
[ -n "$TID" ] || { echo "no matching unresolved thread on <PATH> - do not guess an id" >&2; return 1 2>/dev/null || exit 1; }

# Show the mutation's output. Never append an output-discard or force-success tail
# (>/dev/null, 2>/dev/null, &>/dev/null, || true, || :, || echo) to a write.
gh api graphql -f query='
mutation($threadId: ID!, $body: String!) {
  addPullRequestReviewThreadReply(input: { pullRequestReviewThreadId: $threadId, body: $body }) {
    comment { id url }
  }
}' -F threadId="$TID" -F body="Fixed in <SHA>: <one-line summary>."

# Confirm isResolved: true in this response before treating the thread as closed - a write that
# appears to fail may have taken on the server.
gh api graphql -f query='
mutation($threadId: ID!) {
  resolveReviewThread(input: { threadId: $threadId }) { thread { id isResolved } }
}' -F threadId="$TID"
```

Issue-level Copilot comments (those in `issues/<N>/comments`) have no resolution action - GitHub provides no API or UI to resolve them. Reply if the finding warrants it; no resolution step is needed or possible.

### PR Edits and Merge-State Gotchas

- **`gh pr edit --title/--body` is broken here.** It touches the deprecated Projects-classic `projectCards` GraphQL field and **exits non-zero without applying the change** (a stale PR description then survives review rounds). Edit the title/body via the API and verify it took: GraphQL `updatePullRequest(input: { pullRequestId, title, body })`, or REST `gh api -X PATCH repos/ptr727/PhotoCleaner/pulls/<N> -F body=@body.md` (the `@` reads the body from a file - name it explicitly, not the literal `file`).
- **`main`/`develop` use rulesets, not classic branch protection.** The classic protection REST endpoint (`repos/.../branches/<b>/protection`) 404s - read the ruleset instead. A `mergeStateStatus` of `BLOCKED` on a green PR is usually just **unresolved review threads** (the ruleset requires thread resolution); resolving them moves it to `CLEAN`. (`BLOCKED` is a `mergeStateStatus` value; don't confuse it with the separate `mergeable` field's `MERGEABLE`/`CONFLICTING`, which reports merge conflicts, not review gates.)
- **Push -> head-SHA read race.** A `headRefOid` read taken immediately after a push can return the **old** head; re-read after the push registers, or a coverage poll evaluates the stale SHA.
- **Copilot is sometimes factually wrong** (e.g. it claimed `actionlint -color` "requires a value" - it is a boolean flag). Verify a finding before fixing; decline with evidence when it is wrong - that is distinct from dismissing a still-present finding as stale.

Reply-body conventions:

- Accepted bug/style fix: include fixing commit SHA and a one-line summary.
- Declined style comment: cite the rule (GOVERNANCE.md or the CODESTYLE.md language section) and the existing-tree precedent.
- Declined architecture proposal: one-sentence rationale.

After the final push, sweep-resolve stale older threads for removed code paths.

## When in Doubt

Read [AGENTS.md](../AGENTS.md) to find the section that governs your change, and [GOVERNANCE.md](../GOVERNANCE.md) for the rule text itself. For code-style rules, [`CODESTYLE.md`](../CODESTYLE.md) (its General section plus the relevant language section) is authoritative. Don't restate any of these files' rules in commit bodies or PR descriptions - keep those focused on the change itself.

If you find a gap in the governance itself (this file, AGENTS.md, or GOVERNANCE.md is out of date, a rule is missing, something bit this repo and would bite the next), fix it in the governance docs as part of your change rather than only working around it locally.

## Project Overview

PhotoCleaner is a .NET 10 console application that processes media files in preparation for import into photo management systems (Lightroom, Immich, PhotoPrims). It analyzes and transforms media files through validation, modification, and verification phases.

## Architecture & Data Flow

### Project Structure

- **Docker/**: Docker configuration
  - `Dockerfile`: Two-stage build (SDK Alpine build -> runtime Alpine final); installs `exiftool` and `ffmpeg` in the final stage
- **PhotoCleaner/**: Main console application
  - `Program.cs`: Entry point with logger setup (Main only)
  - `CommandLine.cs`: System.CommandLine implementation for CLI parsing (`process`, `undo`, `import`, `index`, and `trash` subcommands)
  - `MediaUtilities.cs`: Shared static utilities - `SupportedExtensions` (FrozenSet), `GetUniqueFileName`, `GetExifToolJsonAsync`, `SetCreateDateAsync`, video/duration constants
  - `CommandRunner.cs`: Thin wrapper for command start/complete/error logging
  - `DatabaseScope.cs`: Generic async DB lifecycle helper (create, init, dispose)
  - `TrashDatabaseScope.cs`: Same lifecycle helper pattern for `TrashDatabase`
  - `FileEnumerator.cs`: Parallel file enumeration returning `(IReadOnlyList<string>, int)`
  - `DirectoryCleaner.cs`: Static helper that deletes empty subdirectories under a root (deepest-first; root itself is never deleted); used by `import` and `process` when `--deleteempty` is set
  - `ProcessCommand.cs`: Process command orchestration - case conflict resolution, reprocessing loop, result reporting
  - `ImportCommand.cs`: Import command orchestration (formerly `OrganizeCommand`)
  - `IndexCommand.cs`: Index command orchestration
  - `TrashCommand.cs`: Trash command orchestration - fetches trashed asset checksums from Immich API, stores SHA-1 hashes in a `TrashDatabase`
  - `UndoCommand.cs`: Undo command orchestration
  - `ProcessTask.cs`: Core file processing pipeline (validation, conversion, metadata)
  - `UndoTask.cs`: Undo logic - two-pass algorithm that restores `.bak` files
  - `ImportTask.cs`: Import logic - copies (default) or moves supported media files from source into date-based subdirectories under `--outpath`. Inserts a row keyed by SOURCE path into Import.db. Optional SQLite deduplication via `Database`. (Formerly `OrganizeTask`.)
  - `IndexTask.cs`: Common DB upsert logic used by `process` and `index` commands; `IndexFileAsync` (single-file) returns `(IndexStatus, sha256, sha1, wasProcessed)`; `ExecuteAsync` (batch parallel) returns `(inserted, updated, unchanged, ignored, failed)`. When `options.MarkProcessed` is true, newly inserted rows are marked `is_processed=1` (used by `index --processed` to seed Process.db).
  - `Database.cs`: SQLite wrapper with a single `files` table (`path` PRIMARY KEY, `sha256`, `sha1`, `file_size`, `mtime_ticks`, `is_processed`); indexes on both hash columns; size/mtime caching via `ResolveHashesAsync` to skip rehashing unchanged files. Every write computes both sha256 and sha1 in a single read pass; both columns are non-null
  - `TrashDatabase.cs`: Simple SQLite wrapper for Immich trash hashes; single `trash_hashes` table (`sha1` PRIMARY KEY); used by `trash`, `import`, and `process` commands
  - `ImmichApiModels.cs`: AOT-compatible JSON models for Immich API (`ImmichSearchRequest`, `ImmichSearchResponse`, `ImmichAssetDto`) with `ImmichJsonContext` source generation
  - `DateFromPath.cs`: Static utility class for date inference from filenames/paths
  - `ExifToolJson.cs`: JSON model for ExifTool metadata
  - `SkippedExtensionTracker.cs`: Thread-safe tracker for unknown file extensions skipped during processing; used by all commands that filter by `MediaUtilities.SupportedExtensions` (`process`, `import`, `index`)
  - `HttpClientFactory.cs`: Polly resilience pipeline (retry, circuit breaker) and `SocketsHttpHandler` connection pooling
  - `AssemblyInfo.cs`: Assembly metadata (app name, version) used by `HttpClientFactory` for User-Agent header
  - `Extensions.cs`: Extension methods for logging and error handling
- **PhotoCleanerTests/**: Comprehensive test project
  - `DateInferenceTests.cs`: Core date inference functionality tests (33 tests)
  - `DateInferenceEdgeCasesTests.cs`: Edge cases and comprehensive scenarios (19 tests)
  - `CommandLineTests.cs`: Command line parsing and validation tests (15 tests)
  - `ProcessTaskTests.cs`: Process task tests (61 tests)
  - `UndoTaskTests.cs`: Undo task tests (13 tests)
  - `ExifToolJsonTests.cs`: ExifToolJson unit tests (includes GetDate, IsDngVersionNewer) (33 tests)
  - `ImportTaskTests.cs`: Import task tests (24 tests)
  - `DatabaseTests.cs`: Database tests (15 tests)
  - `IndexTaskTests.cs`: IndexTask tests (7 tests)
  - `TrashDatabaseTests.cs`: TrashDatabase tests (8 tests)
  - `TrashCommandTests.cs`: TrashCommand tests with mock HTTP handler (6 tests)
  - `DirectoryCleanerTests.cs`: DirectoryCleaner static helper tests (6 tests)

### Core Processing Pipeline

The application uses a sequential validation pipeline where each method returns `bool` -
`false` stops processing the current file:

```csharp
if (!RenameMismatchedMimeExtensions()
    || !RenameMixedCaseExtensions()
    || !await DeleteLivePhotosAsync()
    || !await ConvertVideoAsync()
    || !WarnDngVersion())
```

### State Management Pattern

- **Primary Constructor Parameters**: Command and task classes use C# 12 primary constructors. All task classes take `CommandLine.Options options` as their first parameter, plus any non-option runtime params (e.g., `Database`, shared collections). Command classes take `(CommandLine.Options options, CancellationToken cancellationToken)` and pass `options` directly to task constructors.
- **Command/Task Separation**: Command classes (e.g., `ProcessCommand`) handle orchestration (file enumeration, DB lifecycle, result logging); task classes (e.g., `ProcessTask`) handle per-file business logic
- **Composable Infrastructure**: `CommandRunner`, `DatabaseScope`, and `FileEnumerator` are static helpers freely composed by command classes - no inheritance hierarchy
- **Shared Collections**: `ConcurrentBag<string>` for file names, `ConcurrentDictionary<string, byte>` for unknown extensions with case-insensitive comparison
- **Parallel Processing**: Files processed using `Parallel.ForEachAsync` with `MaxDegreeOfParallelism`
- **External Tool Integration**: Uses `CliWrap` for all external command execution (exiftool, ffmpeg, ffprobe)
- **FrozenSet Collections**: All static readonly extension collections use `FrozenSet<string>` with `StringComparer.OrdinalIgnoreCase` for O(1) lookups

## Key Patterns & Conventions

### External Tool Execution Pattern

```csharp
BufferedCommandResult result = await Cli.Wrap("exiftool")
    .WithArguments(["-groupNames", "-json", _fileInfo.FullName])
    .ExecuteBufferedAsync();
```

- Always use array syntax for arguments: `["-arg1", "value"]`
- Use `BufferedCommandResult` for output capture, `CommandResult` for fire-and-forget
- JSON trimming pattern: `result.StandardOutput.Trim(' ', '\n', '\r', ' ', '[', ']')`

### Media File Processing Conventions

- **FrozenSet Extensions**: Define supported extensions as `FrozenSet<string>` with `StringComparer.OrdinalIgnoreCase` (e.g., `s_remuxExtensions`, `s_jpegExtensions`)
- **Case-Insensitive Matching**: Use FrozenSet `.Contains()` directly without `.ToLower()` - comparer handles case-insensitivity
- **File Type Categorization**: Group operations by file type requirements (remux vs re-encode vs audio-only)
- **Single-Pass Optimizations**: Prefer single-loop iterations with early exit over multiple LINQ passes
- **Skipped Extension Tracking**: Commands that filter files by `MediaUtilities.SupportedExtensions` pass a shared `SkippedExtensionTracker` instance to their task classes. The tracker collects unknown extensions (thread-safe via `Track()`), and the command calls `LogWarnings()` after processing to log them sorted. Used by `process`, `import`, and `index` commands.

### EXIF/Metadata Handling

- Uses `ExifToolJson` class with `JsonPropertyName` attributes for precise metadata field mapping
- Date validation prioritizes `EXIF:DateTimeOriginal` over `QuickTime:CreateDate`
- Custom `IsDateSet()` and `GetDateString()` methods handle metadata extraction logic
- `ContentIdentifier` property maps both `QuickTime:ContentIdentifier` and `Keys:ContentIdentifier`
  group names (both occur in the wild for ISOBMFF files) returning whichever is set

### Date Inference System (DateFromPath.cs)

- **Static Internal Methods**: All methods are `internal static` for testability with `InternalsVisibleTo`
- **DateFromPath.InferCreatedDate()**: Main entry point - tries filename first, then path fallback
- **DateFromPath.ExtractDateFromFilename()**: Supports multiple filename patterns:
  - `YYYYMMDD_HHMMSS` format (e.g., `20210502_200152957_iOS-1747.jpg`)
  - `YYYYMMDD` format (e.g., `EX_20030219_3378.jpg`)
  - `YYYY-MM-DD-HH-MM-SS` format (e.g., `PHOTO-2024-06-22-07-56-41.jpg`)
  - `YYYY MM DD` format with spaces (e.g., `EV 2014 07 03_0003.tif`)
- **DateFromPath.ExtractDateFromPath()**: Extracts from directory structures and year-only fallback
- **DateFromPath.IsDateValid()**: Validates dates within 1900-current year range

### Command Line Interface (CommandLine.cs)

- **System.CommandLine Integration**: Uses modern .NET command line parsing
- **Five subcommands**: `process`, `undo`, `import`, `index`, `trash` - each with their own option set
- **Required `--path` Parameter**: Single directory path using `Option<DirectoryInfo>`. Validated with `AcceptExistingOnly()`
- **Optional `--dryrun` Flag**: Non-destructive preview mode (process, undo, import - not index)
- **Optional `--threads` Parameter**: Controls parallel processing degree with `DefaultValueFactory = _ => Math.Min(Environment.ProcessorCount, 4)`. Validated to be > 0 and <= Environment.ProcessorCount using `Validators.Add()` (process, import, index)
- **Optional `--skipbackup` Flag** (process only): Skips all `.bak` file creation - originals are deleted/overwritten in-place. Logs a warning at startup. Disables undo.
- **Optional `--deleteempty` Flag** (process, import): After the command completes, deletes empty child subdirectories from the target directory (deepest first; target root is never deleted). For `process` the target is `--path` (operated on in-place); for `import` it is `--outpath`. Implemented by `DirectoryCleaner.DeleteEmptyDirectories(root, dryRun)`.
- **`import` subcommand** (formerly `organize`): Copies (default) or moves supported media files from `--path` sources into `--outpath/date/filename` directory structure. Date comes from EXIF metadata (falls back to `DateTime.MinValue` -> `"0001/01/01"` bucket when absent). `--format` (default `"yyyy/MM/dd"`) controls subdirectory naming and is validated as a date-only format (no time components). Uses `GetUniqueFileName` for collision handling (`foo_1.jpg` etc.). Parallel via `--threads` (same as `process`). `--deleteempty` (default `false`) deletes empty child subdirectories from `--outpath` after all files are imported. `--move` (default `false`) moves files instead of copying. `--tagpath` (default `false`) splits the source sub-directory path into tokens and writes each token as an `XMP:Subject` tag on the destination file using exiftool; filtered by `s_exiftoolWriteExtensions` (`.3gp`, `.arw`, `.cr2`, `.dng`, `.gif`, `.heic`, `.heif`, `.jpeg`, `.jpg`, `.mov`, `.mp4`, `.nef`, `.orf`, `.png`, `.psd`, `.rw2`, `.tif`, `.tiff`) checked via `meta.FileTypeExtension`; uses `-XMP:Subject-= / -XMP:Subject+=` to prevent duplicates while preserving existing tags. `--tags <string>` (optional) applies explicit comma-separated `XMP:Subject` tags to every imported file. `--datepath` (default `false`) infers the EXIF creation date from the source file path when no date is already embedded; applies the date to the destination file before restoring mtime. **`--db <sqlite-file>` (Import.db) is the source-side dedup DB**: rows are keyed by `path = source_path` (NOT dest path) and hold the source file's hash/size/mtime. On each source file, import calls `GetByPathAsync(source_path)` for source-side hash caching, then `Sha256ExistsAsync(source_hash)` to skip already-imported sources. New imports insert a row at the source path. **No command outside `import` writes to source-keyed rows**, so dedup cannot be clobbered by later runs of `process`/`index`. `--trashdb <sqlite-file>` skips files whose **source-file** SHA-1 is in Trash.db (Limitation: when import rewrites the dest via `--tags`/`--tagpath`/`--datepath`, the dest SHA-1 differs from the source SHA-1; Immich stored the dest SHA-1 from a prior upload, so the trash match is missed here and is caught later by `process --trashdb`). `--skipdb <sqlite-file>` skips files whose SHA-256 matches a reference DB (read-only). Cross-collection dedup is typically implemented by pointing `--skipdb` at another collection's Import.db. `--rehash` forces recomputation of all hashes ignoring the size/mtime cache.
- **`index` subcommand**: Iterates all files in `--path`, upserts each into the `files` DB table via `IndexTask.ExecuteAsync` (insert new, update if hash changed, skip unchanged). `--db <sqlite-file>` is **required**. No `--dryrun` (always writes to DB). Supports `--threads` and `--rehash`. `--processed` (optional) marks newly-INSERTED rows with `is_processed = 1`; useful when seeding a Process.db from existing files so `process` treats them as already-done. The flag does not flip the flag on existing rows. Reports `inserted`/`updated`/`unchanged`/`ignored`/`failed` counts.
- **`trash` subcommand**: Syncs trashed asset checksums from an Immich server into a local SQLite trash database. `--url` (Immich server URL, required), `--trashdb <sqlite-file>` (trash database, required), and the API key supplied by exactly one of `--apikey` (inline) or `--apikey-file` (path to a file whose trimmed contents are the key). The two API-key options are mutually exclusive and exactly one must be provided; `--apikey-file` must reference an existing, non-empty, readable file (existence enforced by an option validator, non-empty/readable by a command-level validator; read failures are translated to validation errors, never thrown). The key is resolved at parse time by `CommandLine.ResolveApiKey`/`ReadApiKeyFile` (file contents preferred and `.Trim()`-med) and flows into `Options.ImmichApiKey`. Uses `POST /api/search/metadata` with `trashedAfter` to fetch all trashed assets, converts Base64 SHA-1 checksums to hex, and inserts them via `INSERT OR IGNORE`. Full sync (idempotent, append-only). No `--dryrun`.
- **`--trashdb` Flag** (import, process): SQLite database file with Immich trash hashes (synced by `trash`). In `import`, files matching the trash DB are skipped (this prevents re-importing photos the user trashed in Immich); the check is against the **source-file** SHA-1, so files whose dest SHA-1 was mutated by `import` itself (`--tags`/`--tagpath`/`--datepath`) will not match here even though Immich stored the mutated SHA-1 - `process --trashdb` catches those on the next pass. In `process`, matching files are **deleted from disk and from Process.db** before the per-file processing pipeline runs (cleanup of files trashed in Immich after upload, and the safety net for the import source-vs-dest SHA-1 drift). The Trash.db check is the durable safety net beyond Immich's ~30-day trash retention.
- **Optional `--skipdb` Flag** (import only): SQLite database of files to skip (read-only SHA-256 check). Files whose SHA-256 matches a record in this DB are skipped without being recorded. Use this to skip files already present in another collection.
- **Optional `--rehash` Flag** (process, import, index): Forces SHA-256 recomputation for every file, ignoring the size/mtime cache. SHA-1 is also recomputed when `--trashdb` is in use. Useful after filesystem operations that preserve mtime but change content.
- **Optional `--duration` Flag** (process only): Overrides `ShortVideoDuration` (default `1.0`s). Videos in a live-photo-compatible format whose duration is <= this value are always deleted. Must be `> 0`. Stored in `CommandLine.Options.ShortVideoDuration` and read by `DeleteLivePhotosAsync`.
- **Optional `--reprocess` Flag** (process only): When set, ignores `is_processed` in the DB and forces every file to be processed again. Stored in `CommandLine.Options.Reprocess`; disables the `IndexStatus.Unchanged && wasProcessed` early-return in `ExecuteAsync`.
- **Command Construction**: `CommandLine.SetAction` handlers create the appropriate command class (e.g., `ProcessCommand`, `ImportCommand`) with `CommandLine.Options` and `CancellationToken`
- **Built-in Help System**: Automatic help generation and validation

## Development Workflow

See [`CODESTYLE.md`](../CODESTYLE.md) for build requirements, formatting commands, and tooling.

### Dependencies

- **CliWrap**: External process execution
- **System.CommandLine**: Modern CLI argument parsing and validation
- **System.Text.Json**: High-performance JSON with source generation
- **Microsoft.Data.Sqlite**: SQLite database access for source file deduplication
- **Serilog**: Structured logging with console output
- **Native AOT**: Project configured for `PublishAot=true` with `InvariantGlobalization=true`
- **xUnit**: Testing framework for PhotoCleanerTests project

### Test Architecture

- **PhotoCleanerTests Project**: 300 comprehensive tests covering all functionality
- **InternalsVisibleTo**: Enables direct testing of internal methods without reflection
- **Test Categories**:
  - `DateInferenceTests.cs`: Core date inference functionality (33 tests)
  - `DateInferenceEdgeCasesTests.cs`: Date inference edge cases and integration (19 tests)
  - `CommandLineTests.cs`: Command line parsing and validation (24 tests)
  - `ProcessTaskTests.cs`: Process task tests (61 tests)
- **Coverage Areas**: Date inference (filename patterns, path structures, validation), command line interface (parsing, validation, error handling, multiple paths, thread configuration and boundary validation), integration scenarios, process task execution, live photo detection (ContentIdentifier matching, `_hevc` suffix naming, mismatch/missing tag scenarios), metadata preservation through conversion

## Critical Implementation Details

### Video Conversion Logic

- **Three-tier approach**: Remux (.mts, .m2ts, .mkv) -> Re-encode (.wmv, .avi, .3gp, .gif) -> Audio-only (.mov/.mp4 with PCM)
- **Backup Strategy**: Original files renamed to `.bak` extension after successful conversion; `BackupFile()` returns the backup path. A `{backup}.out` companion file (e.g. `img.gif.bak.out`) is written alongside the backup containing the full output path - this is needed when `GetUniqueFileName` appended a counter suffix (e.g. `img_1.mp4`) because the canonical name was already taken. When `options.SkipBackup` is true, no `.bak` or `.bak.out` files are created - the original is deleted after conversion.
- **Metadata Preservation**: After every ffmpeg conversion, `exiftool -TagsFromFile <source.bak> <output> -all:all -overwrite_original` copies all source metadata to the output file. `ffmpeg -map_metadata` is not used - it is unreliable for Apple QuickTime-specific tags (e.g. `ContentIdentifier` in the `mdta`/`keys` atom). `TagsFromFile` handles cross-format date mapping, so no separate date-setting step is needed after conversion.
- **Re-queue Pattern**: Converted files are added back to processing queue for validation

### Live Photo Detection

- **Short videos** (duration <= `options.ShortVideoDuration`, default `1.0s`; overridable via `--duration`): always deleted regardless of companion file
- **Companion file search** (`FindCompanionImagePath()`): looks for a HEIC/JPG/JPEG file by:
  1. Direct basename match (`IMG_1234.mov` -> `IMG_1234.heic`)
  2. Basename minus `_hevc` suffix (`IMG_1234_HEVC.mov` -> `IMG_1234.heic`) - new iPhone naming
- **ContentIdentifier confirmation**: a candidate pair is only deleted when both files expose a `ContentIdentifier` tag that matches exactly. If either file lacks the tag, or the tags differ, the video is kept. There is no fallback to name-only deletion.
- **Long videos** (>= `LiveVideoDuration` = 4.0s): always kept even with a matching companion; a warning is logged

### Undo Architecture (UndoTask.cs)

- **Backup naming**: `X.bak` (first), `X.bak1`, `X.bak2`, ... (subsequent runs of `process`)
- **`FileEnumerator.Enumerate()`** enumerates all files including `.bak*` files before calling `Execute()`
- **Two-pass algorithm** in `UndoTask.Execute()`:
  - *Pass 1 - Identify derived bases*:
    - **Rule 1**: any numbered backup (`.bak1`, `.bak2`, ...) present -> base is derived
    - **Rule 2**: `.mp4` base with same-stem non-`.mp4` primary backup in same dir -> base is derived
  - *Pass 2 - Act*:
    - Derived base: delete current file + all its backups
    - Non-derived base: delete current file if present, restore `X.bak` -> `X`; then locate the derived conversion output: if `X.bak.out` companion exists read the explicit output path from it and delete that file (handles uniquified names like `img_1.mp4`); otherwise fall back to checking whether `stem.mp4` exists and has no backup (legacy single-run heuristic)
- **Internal static helpers** (testable via `InternalsVisibleTo`):
  - `IsBackupFile(path)` - matches `.bak\d*$`
  - `IsNumberedBackup(path)` - matches `.bak\d+$`
  - `GetBackupBase(path)` - strips the `.bak\d*` suffix
- **Dry run**: logs all intended operations but performs no file I/O
- **Known limitation**: extension renames to a previously non-existent filename create no backup and cannot be undone

### Error Handling Strategy

- Console output uses structured prefixes: `WARNING:`, `INFORMATION:`
- External command failures throw `CommandExecutionException`
- Methods return `false` to skip file processing rather than throwing exceptions

## File Processing Extensions

Supported: `.3gp`, `.arw`, `.avi`, `.cr2`, `.dng`, `.gif`, `.heic`, `.heif`, `.jpeg`, `.jpg`, `.m2ts`, `.mkv`, `.mov`, `.mp4`, `.mts`, `.nef`, `.orf`, `.png`, `.rw2`, `.tif`, `.tiff`, `.wmv`

## Command Line Usage

```bash
# Basic usage
PhotoCleaner process --path /photos

# Dry run mode
PhotoCleaner process --path /photos --dryrun

# Custom thread count
PhotoCleaner process --path /photos --threads 8

# Skip backup files (no .bak created, undo not possible)
PhotoCleaner process --path /photos --skipbackup

# Process and remove empty subdirectories from --path afterward
PhotoCleaner process --path /photos --deleteempty

# Undo last process run
PhotoCleaner undo --path /photos
PhotoCleaner undo --path /photos --dryrun

# Import: copy media files from /Originals into date-based subdirectories under /Processed
PhotoCleaner import --path /photos --outpath /organized
PhotoCleaner import --path /photos --outpath /organized --format "yyyy/MM/yyyy-MM-dd"
PhotoCleaner import --path /photos --outpath /organized --dryrun

# Import with move (removes source files)
PhotoCleaner import --path /photos --outpath /organized --move

# Import with path-based tags (adds sub-directory tokens as XMP:Subject)
PhotoCleaner import --path /photos --outpath /organized --tagpath

# Import with explicit tags applied to every file
PhotoCleaner import --path /photos --outpath /organized --tags "vacation,family"

# Import with date inference from path (sets EXIF date when missing)
PhotoCleaner import --path /photos --outpath /organized --datepath

# Import with deduplication DB (skip sources already imported)
PhotoCleaner import --path /icloud/originals --outpath /intermediate --db /data/Import.db

# Stage-specific DBs: import tracks source identity, process tracks dest state
PhotoCleaner import  --path /icloud/originals --outpath /processed --db /processed/Import.db --trashdb /data/Trash.db
PhotoCleaner process --path /processed --db /processed/Process.db --trashdb /data/Trash.db
# subsequent runs: only new sources are imported; only new dest files are processed

# Index: hash a tree into a DB. Use to seed Import.db (no flag) or Process.db (--processed).
PhotoCleaner index --path /icloud/originals --db /processed/Import.db
PhotoCleaner index --path /processed         --db /processed/Process.db --processed

# Cross-collection dedup: point a secondary import's --skipdb at the primary collection's Import.db
PhotoCleaner import --path /Originals/Pictures --outpath /Processed/Pictures --db /Processed/Pictures/Import.db \
    --skipdb /Processed/iCloud/Import.db

# Trash: sync Immich trash hashes into a local DB
PhotoCleaner trash --url http://immich:2283 --apikey YOUR_API_KEY --trashdb /data/Trash.db

# Trash: supply the API key from a file instead of inline (keeps the secret out of shell history/process args)
PhotoCleaner trash --url http://immich:2283 --apikey-file /secrets/immich_api_key.txt --trashdb /data/Trash.db

# Import with trash skip (prevents re-importing files trashed in Immich, even after Immich purges trash)
PhotoCleaner import --path /photos --outpath /organized --db /data/Import.db --trashdb /data/Trash.db

# Process with trash delete (cleans up files trashed in Immich after upload, before re-uploading)
PhotoCleaner process --path /organized --db /data/Process.db --trashdb /data/Trash.db

# Import with skip DB (skip files already in another collection)
PhotoCleaner import --path /photos --outpath /organized --skipdb /data/existing-collection.db

# Full workflow with trash integration and per-stage DBs
PhotoCleaner trash   --url http://immich:2283 --apikey $IMMICH_KEY --trashdb /data/Trash.db
PhotoCleaner import  --path /icloud --outpath /processed --db /processed/Import.db  --trashdb /data/Trash.db
PhotoCleaner process --path /processed                    --db /processed/Process.db --trashdb /data/Trash.db

# Help
PhotoCleaner --help
PhotoCleaner process --help
PhotoCleaner import --help
PhotoCleaner index --help
PhotoCleaner trash --help
```

## JSON Source Generation

Uses `SourceGenerationContext` for AOT-compatible JSON serialization of `ExifToolJson` metadata.
Uses `ImmichJsonContext` for AOT-compatible JSON serialization of Immich API models.

## Testing Strategy

- **Direct Method Testing**: Uses `InternalsVisibleTo` for compile-time safe method calls
- **Comprehensive Coverage**: Tests all filename patterns, path structures, date validation, and CLI parsing
- **Integration Testing**: Validates end-to-end date inference and command line interface logic
- **No Reflection**: All tests use direct method calls for better performance and maintainability

### Command Line Testing Patterns

- **CreateTestCommand() Helper**: Uses `CommandLine.CreateRootCommand()` directly for single source of truth
- **Type-based Option Extraction**: Identifies options by type (`Option<List<DirectoryInfo>>`, `Option<bool>`, `Option<int>`) using 4-tuple destructuring
- **Real Directory Testing**: Uses `Directory.GetCurrentDirectory()` for path validation tests
- **Parse Result Validation**: Tests both success/error states and extracted argument values, including list counts for multiple paths and thread values
- **Comprehensive Scenarios**: Single path, multiple paths, thread configuration, option properties, argument parsing, validation errors, edge cases, default values
- **Multiple Path Testing**: Validates 2-path and 3-path scenarios, mixed valid/invalid paths, and proper list indexing
- **Thread Option Testing**: Validates thread count parsing, default value calculation, short option, and combined option scenarios
