# AUDIT.md

How this repository audits itself against its committed baseline and reports drift. This is the repo-scoped adaptation of the fleet-wide AUDIT.md kept at the fleet hub (carried per the [repo-config downstream carry][repo-config-readme]), and the hub's fleet-wide audit remains authoritative. The ground truth here is the committed [`repo-config/`][repo-config] payloads and [`spec/secrets.json`][secrets], and the prose authorities are [`GOVERNANCE.md`][governance], [`CODESTYLE.md`][codestyle], and [`WORKFLOW.md`][workflow].

The audit is read-only: it diffs live state against the committed baseline and reports findings, and it never applies changes. The verdict vocabulary is [`WORKFLOW.md`][workflow]'s: **operational / not operational**, **N/A**, **defect**, and the applicable/absent rule.

## Scope

This is a release-model repo: the self-audit covers the `main` and `develop` rulesets, general repository settings, and secret names. Code-project conformance (analyzers, tests, coverage, publish workflows) is CI's job and the fleet hub's fleet-wide audit's, not this self-audit's. See [GOVERNANCE.md "Branching Model"][governance-branching-model] for the model this baseline encodes.

## General Settings

Diff the live repository settings against [`repo-config/settings.json`][repo-config-settings]. The two state-dependent settings are not in the file: `has_discussions` follows visibility (public on / private off) and `default_branch` is `main`.

```sh
repo="$(gh repo view --json nameWithOwner --jq '.nameWithOwner')"
live=$(gh api "repos/$repo" --jq '{has_wiki,has_projects,allow_merge_commit,allow_squash_merge,allow_rebase_merge,allow_auto_merge,allow_update_branch,delete_branch_on_merge}')
diff <(jq -S . repo-config/settings.json) <(jq -S . <<<"$live") \
  && echo "settings: in sync" || echo "settings: DRIFT"
```

## Rulesets

Diff each live ruleset against the committed expected payload with a normalized comparison (sort the order-insensitive `rules[]` on each rule's whole content before diffing, so a reordered but equivalent ruleset does not read as drift). The compared subset is `name`, `target`, `enforcement`, `conditions` and `rules`, which is the same subset and the same sort key the hub's `spec/audit.py` uses. `bypass_actors` sits deliberately outside it: who may bypass a ruleset is a human decision taken in the UI, no payload declares one, and comparing it here would report a finding against every ruleset that has any bypass actor at all, which is the field's normal state rather than drift. This release carry keeps its `develop` payload at [`repo-config/develop.json`][repo-config-develop].

```sh
repo="$(gh repo view --json nameWithOwner --jq '.nameWithOwner')"
# bypass_actors stays outside the projection, since no payload declares one and jq cannot sort the null that leaves.
# Rules sort on each rule's whole content, matching the key the hub's audit.py sorts by.
# Sorting on .type alone leaves two rules of one type in input order, so a reordered pair would read as drift.
# canon sorts keys at every depth before serializing, because the committed payload is written key-sorted and the API returns its own order, so a bare tojson gives the same rule two different sort keys.
canon='def canon: . as $in | if type == "object" then reduce (keys_unsorted|sort)[] as $k ({}; . + { ($k): ($in[$k]|canon) }) elif type == "array" then map(canon) else . end;'
norm="$canon"'{name,target,enforcement,conditions,rules} | .rules|=sort_by(canon|tojson)'
# Paginate so later-page rulesets count: --paginate with --jq '.[]' emits one JSON object per ruleset
# across all pages, and jq -s re-assembles them into the single array the selections below expect.
rulesets=$(gh api --paginate "repos/$repo/rulesets" --jq '.[]' | jq -s '.')
for b in develop main; do
  file="repo-config/$b.json"
  # Exactly one ruleset per name: zero or duplicates is itself a finding, so report it and never diff a guess.
  count=$(jq --arg n "$b" '[.[] | select(.name==$n)] | length' <<<"$rulesets")
  [ "$count" -eq 1 ] || { echo "$b: expected exactly 1 ruleset, found $count (defect/drift)"; continue; }
  id=$(jq --arg n "$b" '.[] | select(.name==$n) | .id' <<<"$rulesets")
  diff <(jq -S "$norm" "$file") \
       <(gh api "repos/$repo/rulesets/$id" --jq '{name,target,enforcement,conditions,rules}' | jq -S "$norm") \
    && echo "$b: in sync" || echo "$b: DRIFT"
done
```

The result must be exactly two rulesets named `develop` and `main`. A missing ruleset or a divergent payload is a **defect**, and a duplicate or stray ruleset is a **drift finding**.

## Secrets

Confirm each name [`spec/secrets.json`][secrets] requires exists in the stores its mechanism claims, and no forbidden name is present (names only, since values are not readable). The baseline App pair, the Docker Hub pair, and `CODECOV_TOKEN` all live in both the Actions and Dependabot stores, since a workflow run triggered by a Dependabot pull request reads the Dependabot store and would otherwise silently skip the coverage upload.

```sh
repo="$(gh repo view --json nameWithOwner --jq '.nameWithOwner')"
for store in actions dependabot; do
  names=$(gh api "repos/$repo/$store/secrets" --jq '.secrets[].name')
  want="CODEGEN_APP_CLIENT_ID CODEGEN_APP_PRIVATE_KEY DOCKER_HUB_USERNAME DOCKER_HUB_ACCESS_TOKEN CODECOV_TOKEN"
  for s in $want; do
    grep -qx "$s" <<<"$names" && echo "$store/$s: present" || echo "$store/$s: MISSING (defect)"
  done
  for s in CODEGEN_APP_ID; do
    grep -qx "$s" <<<"$names" && echo "$store/$s: forbidden name present (defect)" || true
  done
done
```

## Verdict and Follow-Up

A missing required item or a divergent payload is a **defect** (not operational), and an equivalent outcome in a non-standard form is a **drift finding**. N/A items are excluded, never counted as failures. Surface findings as repository issues, and land fixes as a pull request to `develop` per [GOVERNANCE.md "Branching Model"][governance-branching-model]. To re-apply the whole baseline, run `repo-config/configure.sh` from a hub checkout against this repo (see [repo-config/README.md][repo-config-readme]).

<!-- Repo -->

[codestyle]: ./CODESTYLE.md
[governance]: ./GOVERNANCE.md
[governance-branching-model]: ./GOVERNANCE.md#branching-model
[repo-config]: ./repo-config/
[repo-config-develop]: ./repo-config/develop.json
[repo-config-readme]: ./repo-config/README.md
[repo-config-settings]: ./repo-config/settings.json
[secrets]: ./spec/secrets.json
[workflow]: ./WORKFLOW.md
