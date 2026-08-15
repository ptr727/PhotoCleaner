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

Several of this repository's governance files are carried from a shared template and kept in sync across a fleet of sibling repositories, among them `AGENTS.md`, `CODESTYLE.md`, `WORKFLOW.md`, this file, and the `repo-config/` rulesets. Most of `GOVERNANCE.md` is universal fleet law: every section that states a rule, as opposed to the two that describe this repository's own directory tree and devcontainer, is byte-locked and verified by an automated byte-for-byte match against the template canonical, not by line-by-line review. `AGENTS.md` is the thin router and carries three byte-locked sections of its own, with no repository-specific ones.

Three constraints follow when reviewing that content.

- **A reference inside byte-locked text to a path or section this repository does not carry is intentional, not a broken link.** Universal rule text names shared infrastructure (a fleet registry, a reusable config snippet, the other workflow model's ruleset payload) that a given repository legitimately may not contain. Editing the text to "fix" such a reference would break the fleet audit that governs it, so the reference is correct as written. Do not report it as a dead link, a missing file, or a broken cross-reference.
- **A genuine substantive defect is still worth raising.** Byte-locked is not unreviewable. A self-contradiction, a factual error, or a real typo in the canonical prose is a valid finding, but note that the fix lands at the template and re-vendors to every repository, rather than proposing a local edit the audit would reject.
- **A reference to a hub script is a pointer to follow, not a broken local path.** The fleet's gates live in one place and a repository runs them from a checkout of that place rather than holding a copy, so `scripts/prose_lint.py` (prose the CI linters pass on), `scripts/repo_gate.py` (repository settings and action pins), `scripts/pr_review.py` (the review digest, and reply plus resolve without a hand-typed id), and `spec/audit.py` (the conformance audit) resolve there and in none of the repositories they measure. [GOVERNANCE.md "Documentation Style Conventions"](../GOVERNANCE.md#documentation-style-conventions) carries the exception that permits such a pointer inside carried text, and [GOVERNANCE.md "Hub-Hosted Tooling"](../GOVERNANCE.md#hub-hosted-tooling) states how one is reached and what to report when it cannot be. Reach for them before writing a check of your own, since a reconstructed gate encodes its author's reading of a rule rather than the rule, and agrees with no other repository.

## GitHub Copilot Review Runbook

> This runbook implements the [GOVERNANCE.md "PR Review Etiquette"](../GOVERNANCE.md#pr-review-etiquette) review-loop contract for GitHub Copilot. Without it in-repo, an agent has no pointer to the reliable Copilot mechanics and falls back to known-broken paths (the no-op `POST /requested_reviewers`, the wrong bot-login filter). In the API snippets below, fill the `<owner>` / `<repo>` / `<N>` placeholders.

Use this section for provider-specific mechanics. The expected review loop *contract* (request review on every push, verify head-SHA coverage, triage findings, reply + resolve, escalate when stuck) is defined in [GOVERNANCE.md -> PR Review Etiquette](../GOVERNANCE.md#pr-review-etiquette). This section only describes how to make GitHub Copilot reliably execute it.

### Triggering and Polling

Auto-review on push is configured (via the branch ruleset's `copilot_code_review` rule with `review_on_push: true`) but fires inconsistently in practice, so treat it as best-effort, not guaranteed. After every push, **re-request a review programmatically** via the GraphQL `requestReviews` mutation, passing the Copilot reviewer's bot node id in `botIds`. This drives the loop end-to-end without a UI hand-off.

**A review with no inline comments is still a completed review, not a failure, and not a reason to ask the maintainer to re-trigger.** Copilot very often posts a single formal review (GraphQL `state: COMMENTED`) whose body ends with "...reviewed N of N changed files ... and generated no comments" and adds **zero** inline threads. That review carries the head `commit.oid` and fully satisfies the loop, and it is the clean-pass success case. Never read "no inline comments" as "the review didn't run," and never re-request or escalate to the maintainer because comments are absent.

**The one exception is a review that says it did not review, and it is delivered in exactly that shape.** Copilot answers a pull request it will not take on with a formal review, `state: COMMENTED`, carrying the correct `commit.oid` and **zero** inline threads, whose whole body is a refusal: "Copilot wasn't able to review this pull request because it exceeds the maximum number of files (300). Try reducing the number of changed files and requesting a review from Copilot again." Every coverage check passes, the rule above says an empty review is the clean pass, and the two together read a round that never happened as a round that found nothing. Observed on a pull request of 301 changed files, one over the limit, which was one command from merging on it. **The limit is 300 changed files and the remedy is to split the pull request**, since re-requesting the same head repeats the refusal: the file count is what it declined on and re-requesting does not change it. A repository committing binary or generated data alongside code crosses that line easily. Match the refusal on the body's **opening line** rather than anywhere in it, because a review discussing the wording is not one carrying it, and one line rather than two, because a review's first line is its heading and its second is the overview prose where such a description sits. Match an alternation for the same reason the suppressed heading takes one:

```sh
# A review whose opening line declines the round. That line is the unit, since a refusal is
# the whole body and a match further down is a review quoting the wording rather than refusing.
# The dot spans both spellings of the apostrophe, the typographic one Copilot writes and the
# ASCII one, and it also keeps this filter usable inside single quotes, which neither survives.
gh api repos/<owner>/<repo>/pulls/<N>/reviews --jq \
  '.[] | select([(.body // "") | split("\n")[] | select(. != "")][0] // ""
     | test("wasn.t able to review|was not able to review|unable to review")) | {commit_id, body}'
```

**Read the low-confidence findings, which are not inline threads.** A review body can carry a collapsed `<details>` block of findings Copilot withheld from the inline threads, and those findings appear nowhere in `reviewThreads`, so a loop that polls threads alone never sees them and reports a clean pass. **Match the block on more than one phrasing.** Its heading has appeared both as `Suppressed comments (N)` and as "Comments suppressed due to low confidence", so a filter keyed on either one alone silently reports zero suppressed findings on a review that has them, the same false clean this rule exists to prevent, one level up in the detection. **The section moves as well as it is worded, so match the heading wherever it sits.** It has appeared as its own `<details>` wrapper with a matching `<summary>`, as a bare heading in the body, and as a Markdown heading nested inside the `Review details` wrapper, whose `<summary>` names the wrapper and not the section. A filter reading a wrapper's `<summary>` reports zero on the nested shape, and the count it needs is the heading's own `(N)` rather than the wrapper's. They have been right repeatedly, including a rule stated more broadly than its check enforced and a check that skipped fenced blocks in every rule but one. Read the body of every review, investigate each suppressed finding on the same footing as an inline one, and answer it in the PR conversation, since a suppressed finding has no thread to reply on or resolve.

```sh
# `test` with an alternation, not `contains` on one phrasing: the heading wording has changed.
gh api repos/<owner>/<repo>/pulls/<N>/reviews --jq \
  '.[] | select(.body | test("Suppressed comments|low confidence")) | .body'

# Read every round, not only the head. A suppressed finding has no resolved state, so a push
# does not retire it: it simply stops appearing in a head-scoped query while still unanswered.
# Head-scoping this query is how four rounds went unanswered across three pull requests in a day.
gh api repos/<owner>/<repo>/pulls/<N>/reviews --jq \
  '[.[] | select(.body | test("Suppressed comments|low confidence"))] | length'

# Mark which round each came from, since a finding on an older round may since be moot.
PR_HEAD=$(gh pr view <N> --json headRefOid --jq '.headRefOid')
gh api repos/<owner>/<repo>/pulls/<N>/reviews --jq \
  "[.[] | select(.body | test(\"Suppressed comments|low confidence\"))
     | {round: (if .commit_id == \"$PR_HEAD\" then \"head\" else \"earlier\" end), id}]"
```

**Round 1 is normally auto-seeded, so poll for it before trying to self-trigger.** Auto-review-on-open supplies the first review with no `botIds` call needed, but it can lag one to three minutes, and on some pull requests it never fires at all. After opening a PR (or the first push), **poll** for a Copilot review on the head SHA (see [Verify Review Covered Current Head](#verify-review-covered-current-head)) before concluding none ran. Where it never lands, drive round 1 with the same `requestReviews` mutation every later round uses, which needs nothing this PR has to produce first. A round 1 carrying no review therefore means "wait, then request it yourself," **not** "ask the maintainer to kick it off."

> **The reviewer login differs by API, in three forms rather than two.** In **GraphQL** (`gh api graphql` and `gh pr view --json reviews`, which is GraphQL-backed) the `Bot.login` is `copilot-pull-request-reviewer`, with **no `[bot]` suffix**. In the **REST** API (`gh api repos/.../issues|pulls/...`) the same account's `user.login` is `copilot-pull-request-reviewer[bot]`, **with** the suffix. In a REST **timeline** `review_requested` event the `requested_reviewer` is a third spelling again, login `Copilot` with `type` `Bot`, so a filter written against either of the other two selects nothing there and reports a pull request with requests as having none. Match on the type plus a loose login test rather than on any one spelling, and each query below uses the correct form for its API.

```sh
# 1. PR node id, plus the reviewer bot's node id read across the repo's recent PRs.
# The bot id is the reviewer account's own, so every PR in the repo carries the same one.
# The reviewer login is `copilot-pull-request-reviewer` in GraphQL.
PR_NODE=$(gh pr view <N> --json id --jq '.id')
BOT_ID=$(gh api graphql -f query='
{
  repository(owner: "<owner>", name: "<repo>") {
    pullRequests(first: 20, orderBy: { field: CREATED_AT, direction: DESC }) {
      nodes { reviews(first: 20) { nodes { author { __typename login ... on Bot { id } } } } }
    }
  }
}' --jq '[.data.repository.pullRequests.nodes[].reviews.nodes[]
          | select(.author.login == "copilot-pull-request-reviewer")
          | .author.id] | first // empty')
if [ -z "$BOT_ID" ]; then
  echo "no Copilot review in the 20 most recent PRs, so widen the window" >&2
  return 1 2>/dev/null || exit 1   # Stop. Do NOT call requestReviews with an empty id.
fi

# 2. Re-request a Copilot review on the current head.
gh api graphql -f query='
mutation($pr: ID!, $bot: ID!) {
  requestReviews(input: { pullRequestId: $pr, botIds: [$bot], union: true }) {
    pullRequest { id }
  }
}' -F pr="$PR_NODE" -F bot="$BOT_ID"
```

**The bot node id belongs to the reviewer account, not to a pull request**, and it is the same id on **every PR in the repo**, so nothing has to land on this PR before step 1 can read it. A PR opened a minute ago, with no review and no comment of its own, needs no UI seeding to bootstrap the id and no prior review to source it from: any Copilot review anywhere in the repo carries it. Query the **most recent** PRs, since a plain `last: 20` returns the *oldest* ones, which may predate Copilot on the repo. **Guard for an empty result**, because an empty `$BOT_ID` says only that none of the PRs sampled carry a Copilot review, so widen the window (raise the count or paginate) before concluding the repo has never had one. Never pass an empty id to the mutation.

A read scoped to this PR (`pullRequest(number: <N>) { reviews }`) returns the same id once a review has landed here, and it buys nothing over the repo-wide read while failing on exactly the round the repo-wide read handles. Where the repo's only Copilot artifact is an issue comment rather than a formal review, read the id from that comment's author instead (`pullRequest.comments` -> author `... on Bot { id }`). Manual UI seeding is the last resort, needed only for a repo that has **never** had a Copilot review, so no prior id exists anywhere to read.

**Do NOT post `@Copilot review` as a PR comment.** That comment triggers the Copilot *coding agent* (`copilot-swe-agent[bot]`), which makes code changes rather than posting a review.

Known non-working request paths (don't rely on them, and use the `requestReviews` mutation above instead):

- `POST /requested_reviewers` with `reviewers=[Copilot]` can return 200 but no-op.
- `copilot-pull-request-reviewer` as a requested reviewer slug returns 422.
- `requestReviews` with the reviewer's bot node id in **`userIds`** fails with `Could not resolve to User node`, because the Copilot reviewer is a **Bot**, so its node id goes in **`botIds`** (as in the mutation above), never `userIds`.
- `suggestedActors(capabilities: [CAN_BE_ASSIGNED])` lists `copilot-swe-agent` (the coding agent), not `copilot-pull-request-reviewer`, so do not source the reviewer's bot node id there. Read it from an existing review per step 1 above.
- There is no `removePullRequestFromReviewRequest` mutation, but removal is not therefore impossible: `requestReviews` **replaces** the reviewer set when `union` is false (the schema describes `union` as "add users to the set rather than replace"), so an empty `botIds` with `union: false` removes the pending request. Reach for it only in the stuck case below, since `union: true` re-fires a review on the current head without it.
- `gh pr view --json reviewRequests` **omits a Bot reviewer entirely**, reporting an empty set while Copilot sits in it. Read the pending set through GraphQL `reviewRequests`, which returns the `Bot` node, because the REST-backed projection makes a pending request read as no request at all.

### Verify Review Covered Current Head

Before merging, confirm Copilot reviewed the current PR head SHA. Copilot may respond as either a formal review (carries an exact commit SHA) or an issue comment (no SHA, so use the most recent Copilot comment for manual confirmation). Check both.

**Count matches and compare numerically, so an empty result cannot read as success.** A poll that captures a `gh api --jq` result and exits on `[ "$found" != "0" ]` treats an **empty** string as a landed review, and an empty string is exactly what a mis-written filter returns. Pipe the matches through `wc -l` and test `-gt 0`, so a query that finds nothing and a query that ran wrong both read as "not yet". A `gh` call that fails to run reaches the test the same way, because it writes its message to stderr and prints nothing to stdout, so the `$(...)` around it still yields the empty string. A mistyped or unsupported flag is the usual cause, and `gh` reports one as `accepts 1 arg(s), received 4` rather than as anything resembling a review verdict.

**Check head coverage before reading merge-state, never the reverse.** A push makes the required checks go green before Copilot re-reviews the new head, so `mergeStateStatus` can read `CLEAN` in the window before any formal review covers the head. A poll that exits on `CLEAN` merges into that gap. Gate on a formal review whose `commit.oid` equals the current head SHA first, then on zero unresolved threads, and only then read merge-state.

```sh
PR_HEAD=$(gh pr view <N> --json headRefOid --jq '.headRefOid')

# 1. Formal review - exact SHA match.
gh pr view <N> --json reviews --jq \
  '.reviews[] | select(.author.login=="copilot-pull-request-reviewer") | .commit.oid' \
  | grep -q "$PR_HEAD" && echo "covered via formal review"

# 2. Issue comment - show the most recent Copilot comment for manual
#    confirmation. This is the REST API, so the login carries the `[bot]` suffix.
gh api repos/<owner>/<repo>/issues/<N>/comments --jq \
  '[.[] | select(.user.login=="copilot-pull-request-reviewer[bot]")] | last | {created_at, body: .body[:200]}'
```

Coverage is confirmed when (1) exits 0, and **a formal review with no inline comments still satisfies path (1)**, because coverage is about the head SHA, not the comment count. The exception is the refusal above, which is a formal review on the head with no inline comments and covers nothing, so path (1) exits 0 over a round that never ran. Read the body of the review the SHA matched, not only the SHA. For issue comments (path 2), body content is the only reliable signal, and `created_at` is not: `git log -1 --format=%cI` is the **commit** timestamp, not the push timestamp, so amended or rebased commits can have an earlier timestamp and an older Copilot comment could satisfy a time check even though Copilot never saw the current head. Treat path (2) as confirmed only when the comment body explicitly refers to the current changes.

**Coverage of the head is not coverage of the diff, and the second one is stated in a line nothing above reads.** A review body says how many of the pull request's changed files it read, and a round that read fewer than the pull request changed is byte for byte the clean pass in everything else: the same `commit.oid`, the same absent threads, the same "generated no comments". Measured over 332 Copilot review bodies on this repository, five rounds across three pull requests reported reading fewer files than were changed, and all three merged. One of them changed three files, left one unread across **both** its rounds, and reported no comments each time. This is the third instance of the shape the refusal above and the suppressed block below are the first two, so read it the same way: **fail closed on a wording you do not recognize**, since a gate that allows whatever it does not recognize stops gating as the wording drifts, and both of those wordings have drifted once already.

Two spellings carry the count, and both are current rather than one superseding the other. Each opens its own line, which is what separates the round stating its coverage from prose mentioning changed files, that prose being what a review of a change to this rule looks like:

```text
Copilot reviewed 2 out of 3 changed files in this pull request and generated no comments.
- **Files reviewed:** 2/3 changed files
```

The sentence tail after the first spelling reports how many comments the round raised and appears in four wordings. It is not coverage, so it is not part of what has to be recognized, and the counts are. Read them into three verdicts and two exemptions:

- **Counts equal** - the round read the whole diff. This is the clean pass.
- **Counts unequal** - files in the diff have no review at all. Do **not** treat a re-request as the remedy: measured over four pull requests and seven rounds on this repository, every partial round stayed partial at the identical ratio and no round ever recovered, so re-requesting spends a round and changes nothing. Splitting works where it applies and does not apply to a promotion, whose head is `develop`. **The file table in the body does not tell you which file went unread**, and it looks as though it should, which is why it is written down here: measured over 348 review bodies on this repository and 121 on another in this fleet, that table names the whole changed set on partial and fully covered rounds alike, so a table naming every changed file is what a full round carries too and contradicts nothing. One round of the seven is the exception, stating 16 of 17 and naming 16, omitting `GOVERNANCE.md`, and `status` names an omitted file only in that shape, where the table is short by exactly what the counts leave unread and names nothing outside the diff. Treat that as a lead to check rather than a verdict, one round here naming `GOVENANCE.md`, a path no diff carries. Report the state and hand the merge decision to the maintainer.
- **Coverage-shaped and unreadable** - the remedy is to fix the reader, not to read past it. The vetted spellings live in `scripts/pr_review.py` and here, and they stay in step because a case reads them out of this file.
- **Exempt: a body stating no coverage at all.** 28 of those 332 bodies are an overview and a change list and nothing more. That shape is current, interleaves with the counted one throughout, and one pull request carries both across its two rounds, so treating it as a failure cries wolf on about one review in twelve and teaches an agent to work around the gate. It reads as `coverage=unstated`, never as a pass and never as a failure.
- **Exempt: a refusal.** It carries no coverage line by design, and the refusal rule above has already classified it. Read it here as well and every refusal grows a spurious second failure on top of the one that names its remedy.

`scripts/pr_review.py status <N> --repo <owner>/<repo>` reports this as `coverage=full`, `coverage=PARTIAL`, `coverage=UNVETTED` or `coverage=unstated`, and exits `42` on a partial round. An unreadable wording exits `43` instead, as one of the unrecognized shapes below rather than as a case of its own, since both say the reader is what needs fixing. Read it by hand as:

```sh
gh pr view <N> --json reviews --jq \
  '.reviews[] | select(.author.login=="copilot-pull-request-reviewer") | .body
   | split("\n")[] | select(test("^(Copilot|[-*] \\*\\*Files reviewed:).*changed files?"))'
```

### A Shape Nothing Recognizes Blocks the Loop and Earns an Issue

**Every rule above keys on a marker in what Copilot sent, so a marker that changes spelling is a section the reader stops finding and reports as absent.** That is not a hypothetical: all three failures on record here have exactly that shape. The suppressed heading was reworded and the count went to zero. The suppressed section moved inside another wrapper and the count went to zero again. The coverage line was never read at all. Each one reported a clean pass over a review it had misread, and each was caught by the maintainer after it had already landed, rather than by the gate.

**So an unrecognized shape is a blocking outcome, and its remedy is an issue rather than a judgment call.** When any reader here meets a heading, a collapsed section, a metadata line, a coverage wording or a reviewer login it has no vetted spelling for, the review loop **does not close**, whatever else the digest says. Do not read past it, do not infer what the new wording probably means, and do not treat a body that looks clean as a clean review, because "looks clean" is precisely what a misread review looks like. Two things follow, in this order:

1. **File an issue on the hub**, the repository `AGENTS.md` "Fleet Bootstrap" names, which hosts `scripts/pr_review.py` and holds the vetted inventory. Name each unrecognized shape and quote the review body it came from, so the fix is made against the real wording rather than a paraphrase. The issue is filed even when the shape turns out to be cosmetic, since "cosmetic" is a conclusion drawn after reading the body and not before.
2. **The merge decision is the maintainer's**, not the agent's and not the script's. An unrecognized shape does not mean the pull request is bad, it means nothing here can vouch for the review of it. Report the state, hand it over, and stop.

`scripts/pr_review.py status <N> --repo <owner>/<repo>` reports this as `shapes=UNRECOGNIZED`, lists each shape under a marker naming the remedy, and exits `43`. `wait` carries the same code, so a wait cannot end on a clean zero over output nothing read. The vetted inventory lives in that script and is small on purpose: measured over 332 Copilot review bodies on this repository, with fenced blocks dropped and text reduced to ASCII, the whole corpus is seven headings, six `<summary>` texts and three metadata labels, and every body carries at least one of them. A body carrying none is itself the unrecognized shape, which is what catches a rewrite that changes everything at once, the refusal wording drifting among it.

### Bounded Retry Workflow

This path is only for a **genuinely missing** review, meaning no Copilot review (formal *or* issue comment) covers the current head SHA after polling. A review that covered the head but produced no comments is a clean pass, not a missing review, so do not enter this retry path for it.

**A slow review is pending, not missing, so poll with backoff and never escalate on a timeout alone.** Copilot can lag far beyond the usual one-to-three minutes when it has been re-requested many times in quick succession, because it throttles under load, and a re-review landing tens of minutes after the request is normal. A poll that times out is therefore evidence only that the review has not landed *yet*, not that Copilot is done or unresponsive. Report the status as "review still pending" and keep polling on a widening interval (for example 20s steps, then a few minutes) rather than stopping. Enter the escalation step below only when the `requestReviews` mutation itself no-ops or errors, or after a genuinely long wait with the request confirmed accepted, never merely because one fixed poll window elapsed.

**Bound each wait, and read what Copilot actually posted before opening another one.** A poll that widens forever is indistinguishable from a poll that has stopped, and "still pending" is the honest report for exactly as long as evidence supports it. Two readings decide whether waiting again is warranted. Compare the request's timestamp against the newest Copilot activity of **any** kind on the pull request, since a reviewer that has already answered on a later head, or that posted an issue comment instead of a formal review, is not a reviewer running late, and a wait that keeps reporting "pending" against a landed review is a broken wait rather than a slow reviewer. Then read that newest response, because a Copilot answer naming a quota or a rate limit is a **terminal** outcome rather than a pending one: no formal review will land, so path (1) never matches the head and path (2) is correctly never confirmed, both paths behave exactly as specified, and the agent waits for something that is not coming. The fix is account-side and re-requesting does not change it, so report it to the maintainer and stop waiting. Where the newest response is neither a review nor a refusal you recognize, that too goes to the maintainer with its text, rather than being waited through.

**A pending request nothing picked up is a third state, and it is the one that looks most like patience.** Copilot raises a `copilot_work_started` timeline event within about half a minute of accepting a request, and submits its review a few minutes later. A request that never draws one is not a slow review, it is a request nothing is acting on, and it stays that way indefinitely: one sat for thirteen and a half hours while the pull request read as waiting on the reviewer. Elapsed time cannot tell the two apart, since a genuinely slow round also shows no review, so read the event rather than the clock. `copilot_work_started` appears in the REST timeline only, and no GraphQL timeline item carries it:

```sh
# The pending set (GraphQL, since the `gh pr view` projection cannot see a Bot reviewer).
gh api graphql -f query='
{ repository(owner:"<owner>",name:"<repo>"){ pullRequest(number:<N>){
    reviewRequests(first:10){ totalCount
      nodes{ requestedReviewer{ __typename ... on Bot{login} ... on User{login} } } } } } }'

# The request and pickup events, newest last. A `review_requested` with no later
# `copilot_work_started` is the stuck state. Requests are filtered to the reviewer's own,
# since a human requested afterwards is a different request and reading it as this one
# reports a picked-up review as never picked up. `per_page` is the pagination cost.
gh api --paginate 'repos/<owner>/<repo>/issues/<N>/timeline?per_page=100' \
  --jq '.[] | select(.event == "copilot_work_started" or (.event == "review_requested"
        and .requested_reviewer.type == "Bot"
        and ((.requested_reviewer.login // "") | ascii_downcase | test("copilot"))))
        | "\(.event) \(.created_at)"'
```

**Recover it by clearing the request and requesting again**, because the pull request UI offers no re-request control while a request is pending, and `requestReviews` with `union: true` adds a reviewer already in the set, which changes nothing. Read the pending set first, since `union: false` replaces the whole set and would drop a human reviewer requested alongside the bot. Where the clear-and-request does not draw a `copilot_work_started` within a minute or so, push a commit instead, since a new head raises a fresh request rather than poking a stale one.

```sh
PR_NODE=$(gh pr view <N> --json id --jq '.id')
# 1. Clear. `union: false` replaces the set, so an empty botIds removes the pending request.
gh api graphql -f query='
mutation($pr: ID!) {
  requestReviews(input: { pullRequestId: $pr, botIds: [], union: false }) {
    pullRequest { reviewRequests(first: 10) { totalCount } } }
}' -F pr="$PR_NODE"
# 2. Request again, against a now-empty set, with $BOT_ID read as in "Triggering and Polling".
gh api graphql -f query='
mutation($pr: ID!, $bot: ID!) {
  requestReviews(input: { pullRequestId: $pr, botIds: [$bot], union: true }) {
    pullRequest { reviewRequests(first: 10) { totalCount } } }
}' -F pr="$PR_NODE" -F bot="$BOT_ID"
```

If a review did not run on the current head, retry:

1. Wait briefly and check head-SHA coverage (see above).
1. Re-request the review via the `requestReviews` mutation (see "Triggering and Polling"), falling back to the GitHub PR UI only if the mutation no-ops.
1. Retry up to two more times (three total).
1. If still missing, mark review as blocked and escalate to the user/maintainer with what was attempted.

### Reply and Thread Resolution Workflow

Every id below is captured from a live query into a variable and passed from there, never hand-typed, guessed, or pasted as a `PRRT_...` literal. A node id resolves globally, so a fabricated or stale id does not fail, it writes to a real thread on an unrelated repository. This runbook implements [GOVERNANCE.md "Repository Boundaries and Write Safety"](../GOVERNANCE.md#repository-boundaries-and-write-safety): write only to this repo, capture every id from a live query, and never suppress a mutation's output.

**Use the hub's helper, which has nowhere to type an id.** `scripts/pr_review.py reply <N> --repo <owner>/<name> --match "<words from the finding>" --body "<answer>" --resolve` queries the thread id itself and passes it straight to the mutation. That rule is known and read by the agents that break it anyway, three times so far, so the shape is what changes rather than the wording. It selects on the finding's own words rather than a line number, since a fix push moves the line; it refuses on no match and on more than one rather than picking; and it does not resolve a thread whose reply came back without a `url`. Cross-owner targets it refuses outright, which is where the hand-run form below applies, and there the `gh-write-guard` hook is what reads the maintainer's grant. It is hub-hosted per [GOVERNANCE.md "Hub-Hosted Tooling"](../GOVERNANCE.md#hub-hosted-tooling), so it is invoked from a hub checkout and never rebuilt locally.

The hand-run form is below, for a cross-owner target and for the case where the hub cannot be reached and the work cannot wait.

List unresolved threads. Use `first: 100` with cursor-based pagination, and where `hasNextPage` is true, re-run with `after: "<endCursor>"` to retrieve the next page:

```sh
gh api graphql -f query='
{
  repository(owner: "<owner>", name: "<repo>") {
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

Reply on a thread, then resolve it. Capture the target thread's id into `$TID` from the listing query above, filtering to the thread being answered by its `path`, and guard for an empty result so a mutation never runs on a guessed id. When a file carries more than one unresolved thread, `path` alone is ambiguous and `head -n 1` would pick the wrong one, so narrow by first-comment body (the query already fetches `comments(first: 1)` for this) by adding `and (.comments.nodes[0].body | contains("<SNIPPET>"))` to the `select`:

```sh
TID=$(gh api graphql -f query='
{
  repository(owner: "<owner>", name: "<repo>") {
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

Issue-level Copilot comments (those in `issues/<N>/comments`) have no resolution action, since GitHub provides no API or UI to resolve them. Reply if the finding warrants it, but no resolution step is needed or possible.

### PR Edits and Merge-State Gotchas

- **`gh pr edit --title/--body` is broken on `gh` 2.45.x and 2.46.x, and works from 2.47 up.** Those releases touch the deprecated Projects-classic `projectCards` GraphQL field and **exit non-zero without applying the change** (a stale PR description then survives review rounds), which the GitHub CLI maintainers name as broken by deprecated APIs. A distribution package is where that version comes from, so check `gh --version` before concluding the command is unusable, and install from the official repository rather than working around it. Where a host is genuinely stuck on one, edit via the API and verify it took: GraphQL `updatePullRequest(input: { pullRequestId, title, body })`, or REST `gh api -X PATCH repos/<owner>/<repo>/pulls/<N> -F body=@body.md` (the `@` reads the body from a file, so name it explicitly, not the literal `file`). The same version range carries no `--json` flag on `gh pr checks`, so a watcher built on it prints nothing and a quiet result reads as a passing one.
- **`main`/`develop` use rulesets, not classic branch protection.** The classic protection REST endpoint (`repos/.../branches/<b>/protection`) 404s, so read the ruleset instead. A `mergeStateStatus` of `BLOCKED` on a green PR is most often just **unresolved review threads** (the ruleset requires thread resolution), and resolving them moves it to `CLEAN`. (`BLOCKED` is a `mergeStateStatus` value, so don't confuse it with the separate `mergeable` field's `MERGEABLE`/`CONFLICTING`, which reports merge conflicts, not review gates.)
- **`BLOCKED` never says which gate, so never infer one.** The same word covers a red check, a required check nothing is running, an unresolved thread, and a missing approval, and the bullet above says "most often" rather than "always" for that reason. Read the checks instead of guessing: `pr_review.py status` prints `checks=N/M` beside the merge word and names a stuck one, and it exits `44` from `wait` where the merge reads `BLOCKED`, the review loop closed, and a check is starved, expected and never posted, running far past what the job costs, or failed. A **queued check with no runner** is the case that reads exactly like patience: a run here polled `BLOCKED` for twenty-five minutes on a pull request whose only unfinished check was an aggregator job GitHub dispatched and never assigned a runner, and the cause came from the maintainer rather than from any field. Nothing agent-side starts that job, because the pool is GitHub-hosted, so the remedy is a re-run of the workflow or waiting on that capacity, and it is **not** a re-request, a rebase, or an empty commit. A job held behind a `needs:` dependency does not enter the rollup until that dependency finishes, so a queued check is never a dependency waiting its turn.
- **Push -> head-SHA read race.** A `headRefOid` read taken immediately after a push can return the **old** head, so re-read after the push registers, or a coverage poll evaluates the stale SHA.
- **Copilot is sometimes factually wrong** (e.g. it claimed `actionlint -color` "requires a value" when it is a boolean flag). Verify a finding before fixing, and decline with evidence when it is wrong, which is distinct from dismissing a still-present finding as stale. The evidence goes under [Disproved Claims](#disproved-claims) as well as in the thread, because the thread closes with the pull request and the next round starts without it.

Reply-body conventions:

- Accepted bug/style fix: include fixing commit SHA and a one-line summary.
- Declined style comment: cite the rule (GOVERNANCE.md or the CODESTYLE.md language section) and the existing-tree precedent.
- Declined architecture proposal: one-sentence rationale.
- Declined false positive on carried fleet content (a broken-link or dead-cross-reference flag inside byte-locked rule text): cite the "Reviewing Carried Fleet Content" section, since the reference is intentional and the text cannot be edited locally.

After the final push, sweep-resolve stale older threads for removed code paths.

### Disproved Claims

**A disproof is proof about this repository, and the thread it was written in is not where the next round looks.** [GOVERNANCE.md "PR Review Etiquette"](../GOVERNANCE.md#pr-review-etiquette), which routes to the `pr-review-conduct` Skill, closes a false finding by disproving it in the thread, addressed to the reviewer so it does not raise the same thing again, and while the pull request is open that is the right place for it. Afterwards it is the wrong one. The pull request merges, the next round begins with no memory of the last, and the second occurrence reaches a maintainer with no way to tell it from a first. Each entry below is a claim that was tested against this repository and found false, kept so the proof is read rather than built twice.

**An entry names the claim, what was run or read to disprove it, the revision it was proved against, and what ends it.** A disproof is true of one tree at one revision, so an entry whose subject moves is deleted by the change that moves it rather than edited to look current, which is the same sweep the [GOVERNANCE.md "Documentation Style Conventions"](../GOVERNANCE.md#documentation-style-conventions) rule already requires of prose asserting a behavior that has changed underneath it. This is deliberately not a list to append to, since an entry outliving the code it was proved against becomes a reason not to check, and that is strictly worse than proving the claim a second time.

**The record answers a repeated claim and never dismisses a new one.** An entry is cited only where the revision it names is still what the tree carries, and the reply carries the proof re-read rather than a pointer to the entry, since a reviewer that cannot open this file learns nothing from being pointed at it. Judge a finding on its merits first and match it against this record second, because reading it the other way round is how a real finding gets closed by a stale proof.

**The entries are this repository's own.** Each names a file and a revision, so a repository holding a copy of this file carries the shape and the rules above rather than these findings, deletes an entry whose subject it does not carry, and records what it has proved itself.

## When in Doubt

Read [AGENTS.md](../AGENTS.md) to find the section that governs your change, and [GOVERNANCE.md](../GOVERNANCE.md) for the rule text itself. For code-style rules, [`CODESTYLE.md`](../CODESTYLE.md) (its General section plus the relevant language section) is authoritative. Don't restate any of these files' rules in commit bodies or PR descriptions, and keep those focused on the change itself.

If you find a gap in the governance itself (this file, AGENTS.md, or GOVERNANCE.md is out of date, a rule is missing, something bit this repo and would bite the next), fix it in the governance docs as part of your change rather than only working around it locally.

## Project Overview

PhotoCleaner is a .NET 10 console application that processes media files in preparation for import into photo management systems (Lightroom, Immich, PhotoPrims). It analyzes and transforms media files through validation, modification, and verification phases.

## Architecture & Data Flow

### Project Structure

- **Docker/**: Docker configuration
  - `Dockerfile`: Two-stage build (SDK Alpine build -> runtime Alpine final) that installs `exiftool` and `ffmpeg` in the final stage
- **PhotoCleaner/**: Main console application
  - `Program.cs`: Entry point with logger setup (Main only)
  - `CommandLine.cs`: System.CommandLine implementation for CLI parsing (`process`, `undo`, `import`, `index`, `trash`, and `verify` subcommands)
  - `MediaUtilities.cs`: Shared static utilities, `SupportedExtensions` (FrozenSet), `GetUniqueFileName`, `GetExifToolJsonAsync`, `SetCreateDateAsync`, video/duration constants
  - `CommandRunner.cs`: Thin wrapper for command start/complete/error logging, taking `Func<Task<int>>` and returning the command's exit code
  - `ExitCode.cs`: The shared exit-code contract, being `Success` (0), `Error` (1, command could not run), and `Failed` (2, ran to completion with per-file failures)
  - `DatabaseScope.cs`: Generic async DB lifecycle helper (create, init, dispose)
  - `TrashDatabaseScope.cs`: Same lifecycle helper pattern for `TrashDatabase`
  - `FileEnumerator.cs`: Parallel file enumeration returning `(IReadOnlyList<string>, int)`
  - `DirectoryCleaner.cs`: Static helper that deletes empty subdirectories under a root (deepest-first, and the root itself is never deleted), used by `import` and `process` when `--deleteempty` is set
  - `ProcessCommand.cs`: Process command orchestration, case conflict resolution, reprocessing loop, result reporting
  - `ImportCommand.cs`: Import command orchestration (formerly `OrganizeCommand`)
  - `IndexCommand.cs`: Index command orchestration
  - `TrashCommand.cs`: Trash command orchestration, fetches trashed asset checksums from Immich API, stores SHA-1 hashes in a `TrashDatabase`
  - `UndoCommand.cs`: Undo command orchestration
  - `VerifyCommand.cs`: Verify command orchestration, which enumerates, runs `VerifyTask`, and reports counts
  - `ProcessTask.cs`: Core file processing pipeline (validation, conversion, metadata)
  - `UndoTask.cs`: Undo logic, two-pass algorithm that restores `.bak` files
  - `ImportTask.cs`: Import logic, copies (default) or moves supported media files from source into date-based subdirectories under `--outpath`. Inserts a row keyed by SOURCE path into Import.db. Optional SQLite deduplication via `Database`. (Formerly `OrganizeTask`.)
  - `VerifyTask.cs`: Verification logic, a decode pass that runs Immich's own `MediaRepository` inside `ghcr.io/immich-app/immich-server:release` via `docker run`, batching paths over stdin. Preflights the image before judging any file, so an infrastructure failure exits `Error` rather than marking files invalid
  - `ImmichVerifyScript.cs`: The Node script run inside the Immich image, as const strings. Calls Immich's own compiled `MediaRepository`, `defaults`, and `ThumbnailConfig` rather than reimplementing the preview pipeline, so behavior tracks Immich across releases
  - `VerifyResult.cs`: The AOT-compatible `ImmichVerifyLine` JSON model and its `ImmichVerifyJsonContext` source-generated context, together forming the container's output protocol
  - `IndexTask.cs`: Common DB upsert logic used by `process` and `index` commands; `IndexFileAsync` (single-file) returns `(IndexStatus, sha256, sha1, wasProcessed)`; `ExecuteAsync` (batch parallel) returns `(inserted, updated, unchanged, ignored, failed)`. When `options.MarkProcessed` is true, newly inserted rows are marked `is_processed=1` (used by `index --processed` to seed Process.db).
  - `Database.cs`: SQLite wrapper with a single `files` table (`path` PRIMARY KEY, `sha256`, `sha1`, `file_size`, `mtime_ticks`, `is_processed`); indexes on both hash columns; size/mtime caching via `ResolveHashesAsync` to skip rehashing unchanged files. Every write computes both sha256 and sha1 in a single read pass; both columns are non-null
  - `TrashDatabase.cs`: Simple SQLite wrapper for Immich trash hashes; single `trash_hashes` table (`sha1` PRIMARY KEY); used by `trash`, `import`, and `process` commands
  - `ImmichApiModels.cs`: AOT-compatible JSON models for Immich API (`ImmichSearchRequest`, `ImmichSearchResponse`, `ImmichAssetDto`) with `ImmichJsonContext` source generation
  - `DateFromPath.cs`: Static utility class for date inference from filenames/paths
  - `ExifToolJson.cs`: JSON model for ExifTool metadata, including the `ExifTool:Validate` verdict and `ParseValidate` which splits it into error and warning counts
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
  - `VerifyTaskTests.cs`: Verify protocol parsing and script-contract tests (10 tests)

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

Before that chain runs, `CheckExifToolValidation` acts on the `ExifTool:Validate` verdict that
rides along with the metadata read. Only an error count fails the file (`ProcessResult.Invalid`).
Warnings are logged at debug level, because roughly three quarters of healthy files in a real
collection carry at least one.

### State Management Pattern

- **Primary Constructor Parameters**: Command and task classes use C# 12 primary constructors. All task classes take `CommandLine.Options options` as their first parameter, plus any non-option runtime params (e.g., `Database`, shared collections). Command classes take `(CommandLine.Options options, CancellationToken cancellationToken)` and pass `options` directly to task constructors.
- **Command/Task Separation**: Command classes (e.g., `ProcessCommand`) handle orchestration (file enumeration, DB lifecycle, result logging), while task classes (e.g., `ProcessTask`) handle per-file business logic
- **Composable Infrastructure**: `CommandRunner`, `DatabaseScope`, and `FileEnumerator` are static helpers freely composed by command classes, no inheritance hierarchy
- **Shared Collections**: `ConcurrentBag<string>` for file names, `ConcurrentDictionary<string, byte>` for unknown extensions with case-insensitive comparison
- **Parallel Processing**: Files processed using `Parallel.ForEachAsync` with `MaxDegreeOfParallelism`
- **External Tool Integration**: Uses `CliWrap` for all external command execution (exiftool, ffmpeg, ffprobe)
- **FrozenSet Collections**: All static readonly extension collections use `FrozenSet<string>` with `StringComparer.OrdinalIgnoreCase` for O(1) lookups

## Key Patterns & Conventions

### External Tool Execution Pattern

```csharp
BufferedCommandResult result = await Cli.Wrap("exiftool")
    .WithArguments(["-groupNames", "-json", "-validate", "-all", _fileInfo.FullName])
    .ExecuteBufferedAsync();
```

- Always use array syntax for arguments: `["-arg1", "value"]`
- Use `BufferedCommandResult` for output capture, `CommandResult` for fire-and-forget
- JSON trimming pattern: `result.StandardOutput.Trim(' ', '\n', '\r', ' ', '[', ']')`

### Media File Processing Conventions

- **FrozenSet Extensions**: Define supported extensions as `FrozenSet<string>` with `StringComparer.OrdinalIgnoreCase` (e.g., `s_remuxExtensions`, `s_jpegExtensions`)
- **Case-Insensitive Matching**: Use FrozenSet `.Contains()` directly without `.ToLower()`, comparer handles case-insensitivity
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
- **DateFromPath.InferCreatedDate()**: Main entry point, tries filename first, then path fallback
- **DateFromPath.ExtractDateFromFilename()**: Supports multiple filename patterns:
  - `YYYYMMDD_HHMMSS` format (e.g., `20210502_200152957_iOS-1747.jpg`)
  - `YYYYMMDD` format (e.g., `EX_20030219_3378.jpg`)
  - `YYYY-MM-DD-HH-MM-SS` format (e.g., `PHOTO-2024-06-22-07-56-41.jpg`)
  - `YYYY MM DD` format with spaces (e.g., `EV 2014 07 03_0003.tif`)
- **DateFromPath.ExtractDateFromPath()**: Extracts from directory structures and year-only fallback
- **DateFromPath.IsDateValid()**: Validates dates within 1900-current year range

### Command Line Interface (CommandLine.cs)

- **System.CommandLine Integration**: Uses modern .NET command line parsing
- **Six subcommands**: `process`, `undo`, `import`, `index`, `trash`, `verify`, each with their own option set
- **Required `--path` Parameter**: Single directory path using `Option<DirectoryInfo>`. Validated with `AcceptExistingOnly()`
- **Optional `--dryrun` Flag**: Non-destructive preview mode (process, undo, import, not index)
- **Optional `--threads` Parameter**: Controls parallel processing degree with `DefaultValueFactory = _ => Math.Min(Environment.ProcessorCount, 4)`. Validated to be > 0 and <= Environment.ProcessorCount using `Validators.Add()` (process, import, index)
- **Optional `--skipbackup` Flag** (process only): Skips all `.bak` file creation, originals are deleted/overwritten in-place. Logs a warning at startup. Disables undo.
- **Optional `--deleteempty` Flag** (process, import): After the command completes, deletes empty child subdirectories from the target directory (deepest first; target root is never deleted). For `process` the target is `--path` (operated on in-place); for `import` it is `--outpath`. Implemented by `DirectoryCleaner.DeleteEmptyDirectories(root, dryRun)`.
- **`import` subcommand** (formerly `organize`): Copies (default) or moves supported media files from `--path` sources into `--outpath/date/filename` directory structure. Date comes from EXIF metadata (falls back to `DateTime.MinValue` -> `"0001/01/01"` bucket when absent). `--format` (default `"yyyy/MM/dd"`) controls subdirectory naming and is validated as a date-only format (no time components). Uses `GetUniqueFileName` for collision handling (`foo_1.jpg` etc.). Parallel via `--threads` (same as `process`). `--deleteempty` (default `false`) deletes empty child subdirectories from `--outpath` after all files are imported. `--move` (default `false`) moves files instead of copying. `--tagpath` (default `false`) splits the source sub-directory path into tokens and writes each token as an `XMP:Subject` tag on the destination file using exiftool; filtered by `s_exiftoolWriteExtensions` (`.3gp`, `.arw`, `.cr2`, `.dng`, `.gif`, `.heic`, `.heif`, `.jpeg`, `.jpg`, `.mov`, `.mp4`, `.nef`, `.orf`, `.png`, `.psd`, `.rw2`, `.tif`, `.tiff`) checked via `meta.FileTypeExtension`; uses `-XMP:Subject-= / -XMP:Subject+=` to prevent duplicates while preserving existing tags. `--tags <string>` (optional) applies explicit comma-separated `XMP:Subject` tags to every imported file. `--datepath` (default `false`) infers the EXIF creation date from the source file path when no date is already embedded; applies the date to the destination file before restoring mtime. **`--db <sqlite-file>` (Import.db) is the source-side dedup DB**: rows are keyed by `path = source_path` (NOT dest path) and hold the source file's hash/size/mtime. On each source file, import calls `GetByPathAsync(source_path)` for source-side hash caching, then `Sha256ExistsAsync(source_hash)` to skip already-imported sources. New imports insert a row at the source path. **No command outside `import` writes to source-keyed rows**, so dedup cannot be clobbered by later runs of `process`/`index`. `--trashdb <sqlite-file>` skips files whose **source-file** SHA-1 is in Trash.db (Limitation: when import rewrites the dest via `--tags`/`--tagpath`/`--datepath`, the dest SHA-1 differs from the source SHA-1; Immich stored the dest SHA-1 from a prior upload, so the trash match is missed here and is caught later by `process --trashdb`). `--skipdb <sqlite-file>` skips files whose SHA-256 matches a reference DB (read-only). Cross-collection dedup is typically implemented by pointing `--skipdb` at another collection's Import.db. `--rehash` forces recomputation of all hashes ignoring the size/mtime cache.
- **`index` subcommand**: Iterates all files in `--path`, upserts each into the `files` DB table via `IndexTask.ExecuteAsync` (insert new, update if hash changed, skip unchanged). `--db <sqlite-file>` is **required**. No `--dryrun` (always writes to DB). Supports `--threads` and `--rehash`. `--processed` (optional) marks newly-INSERTED rows with `is_processed = 1`, which is useful when seeding a Process.db from existing files so `process` treats them as already-done. The flag does not flip the flag on existing rows. Reports `inserted`/`updated`/`unchanged`/`ignored`/`failed` counts.
- **`trash` subcommand**: Syncs trashed asset checksums from an Immich server into a local SQLite trash database. `--url` (Immich server URL, required), `--trashdb <sqlite-file>` (trash database, required), and the API key supplied by exactly one of `--apikey` (inline) or `--apikey-file` (path to a file whose trimmed contents are the key). The two API-key options are mutually exclusive and exactly one must be provided; `--apikey-file` must reference an existing, non-empty, readable file (existence enforced by an option validator, non-empty/readable by a command-level validator; read failures are translated to validation errors, never thrown). The key is resolved at parse time by `CommandLine.ResolveApiKey`/`ReadApiKeyFile` (file contents preferred and `.Trim()`-med) and flows into `Options.ImmichApiKey`. Uses `POST /api/search/metadata` with `trashedAfter` to fetch all trashed assets, converts Base64 SHA-1 checksums to hex, and inserts them via `INSERT OR IGNORE`. Full sync (idempotent, append-only). No `--dryrun`.
- **`--trashdb` Flag** (import, process): SQLite database file with Immich trash hashes (synced by `trash`). In `import`, files matching the trash DB are skipped (this prevents re-importing photos the user trashed in Immich); the check is against the **source-file** SHA-1, so files whose dest SHA-1 was mutated by `import` itself (`--tags`/`--tagpath`/`--datepath`) will not match here even though Immich stored the mutated SHA-1, `process --trashdb` catches those on the next pass. In `process`, matching files are **deleted from disk and from Process.db** before the per-file processing pipeline runs (cleanup of files trashed in Immich after upload, and the safety net for the import source-vs-dest SHA-1 drift). The Trash.db check is the durable safety net beyond Immich's ~30-day trash retention.
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
- **Backup Strategy**: Original files renamed to `.bak` extension after successful conversion, and `BackupFile()` returns the backup path. A `{backup}.out` companion file (e.g. `img.gif.bak.out`) is written alongside the backup containing the full output path, this is needed when `GetUniqueFileName` appended a counter suffix (e.g. `img_1.mp4`) because the canonical name was already taken. When `options.SkipBackup` is true, no `.bak` or `.bak.out` files are created, and the original is deleted after conversion.
- **Metadata Preservation**: After every ffmpeg conversion, `exiftool -TagsFromFile <source.bak> <output> -all:all -overwrite_original` copies all source metadata to the output file. `ffmpeg -map_metadata` is not used, it is unreliable for Apple QuickTime-specific tags (e.g. `ContentIdentifier` in the `mdta`/`keys` atom). `TagsFromFile` handles cross-format date mapping, so no separate date-setting step is needed after conversion.
- **Re-queue Pattern**: Converted files are added back to processing queue for validation

### Live Photo Detection

- **Short videos** (duration <= `options.ShortVideoDuration`, default `1.0s`, overridable via `--duration`): always deleted regardless of companion file
- **Companion file search** (`FindCompanionImagePath()`): looks for a HEIC/JPG/JPEG file by:
  1. Direct basename match (`IMG_1234.mov` -> `IMG_1234.heic`)
  2. Basename minus `_hevc` suffix (`IMG_1234_HEVC.mov` -> `IMG_1234.heic`), the newer iPhone naming
- **ContentIdentifier confirmation**: a candidate pair is only deleted when both files expose a `ContentIdentifier` tag that matches exactly. If either file lacks the tag, or the tags differ, the video is kept. There is no fallback to name-only deletion.
- **Long videos** (>= `LiveVideoDuration` = 4.0s): always kept even with a matching companion, and a warning is logged

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
  - `IsBackupFile(path)`: matches `.bak\d*$`
  - `IsNumberedBackup(path)`: matches `.bak\d+$`
  - `GetBackupBase(path)`: strips the `.bak\d*` suffix
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
