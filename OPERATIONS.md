# Operations

How this repository is run. It ships a .NET console application and a multi-architecture Docker image, so its operations are the local gates that mirror CI, the release pipeline, and the external tools the application drives at runtime.

## Local Verification

What verifying a change in this repository requires, and specifically the part of its contract CI structurally cannot exercise: CI builds, formats-verifies, and unit-tests the code, and runs the lint set, but it never runs the application against a real media library, a real `exiftool`/`ffmpeg` install, or a live Immich server. The `verify` command's Immich decode path and any end-to-end run of `process`/`undo` against real files are exercised only by a local, manual run.

Local and CI runs read the same committed configuration, but they invoke it differently: locally the formatter writes, and in CI it only verifies. The [`.NET Format`](./.vscode/tasks.json) task is the local clean-compile chain, meaning `dotnet csharpier format`, then `dotnet build`, then the style verify. Run the chain and the suite before committing, since the chain never runs the tests and a change that compiles and formats cleanly can still be broken. CSharpier and Husky.Net are local tools declared in [`.config/dotnet-tools.json`](./.config/dotnet-tools.json), and neither the `.NET Format` task nor the two tasks it depends on restores them, so run `dotnet tool restore` on a fresh clone before that task or the chain below. A clone whose package cache does not already hold the tools otherwise fails on the format step:

```sh
dotnet tool restore
dotnet csharpier format --log-level=debug .
dotnet build
dotnet format style --verify-no-changes --severity=info --verbosity=detailed
dotnet test
dotnet husky run
```

CI differs in two places. It substitutes `dotnet csharpier check .` for the format step, and it runs the suite as `dotnet test --coverage --coverage-output-format cobertura --results-directory ./coverage` so `Microsoft.Testing.Extensions.CodeCoverage` emits the report the Codecov upload consumes, prefixing each `<guid>.cobertura.xml` to `coverage-<guid>.cobertura.xml` because that default name is one `codecov-cli`'s file finder does not match. The style verify is identical. So a local run that formats a file leaves CI clean, while an unformatted commit fails there rather than being fixed.

The lint set runs in containers, matching the `Lint:` tasks in [`.vscode/tasks.json`](./.vscode/tasks.json):

```sh
docker run --rm --pull=always -v "$PWD":/workdir -w /workdir davidanson/markdownlint-cli2:latest "**/*.md"
docker run --rm --pull=always -v "$PWD":/workdir -w /workdir ghcr.io/streetsidesoftware/cspell:latest --no-progress README.md HISTORY.md
docker run --rm --pull=always -v "$PWD":/repo -w /repo rhysd/actionlint:latest -color
docker run --rm --pull=always -v "$PWD":/check -w /check mstruebing/editorconfig-checker:latest
```

Those four are the whole local lint surface, matching the four `Lint:` tasks. CI checks the same four rules from the same committed configuration, but not through the same tools: it reaches markdownlint, cspell and actionlint through SHA-pinned action wrappers, and only editorconfig-checker runs as a container there. The local commands pull `:latest` deliberately, so a local result can legitimately differ from CI once an upstream release lands ahead of the pinned wrapper. Treat CI as authoritative when the two disagree, and read the difference as a version gap rather than a rule change.

The prose gate lives in the hub rather than here, so it is consumed from a hub checkout and reads only the lines a change touches:

```sh
python3 [path-to-hub]/scripts/prose_lint.py . --diff origin/develop
```

## Runbooks

### Cut a release

Publishing never happens as a side effect of a merge. A release is a `workflow_dispatch` of [`publish-release.yml`](./.github/workflows/publish-release.yml), and the same workflow runs on a weekly schedule so the image picks up base-image and tool updates. Merging to `main` publishes nothing on its own.

### Fix a red Dependabot PR

A grouped update (`nuget-deps`, `actions-deps`) can go red for a reason the bump itself cannot fix, because Dependabot only edits version numbers, never source or project files. Two known failure classes: a formatter tool bump (`csharpier` in [`.config/dotnet-tools.json`](./.config/dotnet-tools.json)) changes a formatting rule and flags an untouched file elsewhere in the tree, or a test-stack bump moves `Microsoft.Testing.Extensions.CodeCoverage`, `xunit.v3`, or `Microsoft.NET.Test.Sdk` onto a Microsoft.Testing.Platform major the others do not carry. All three currently resolve to platform 2.3.3, and a skew between them throws `TypeLoadException`, runs zero tests, and still writes a well-formed Cobertura file reporting full coverage, so only the non-zero exit says the run reported nothing. They move together in the `nuget-deps` group, so read [`WORKFLOW.md`](./WORKFLOW.md) D1.6 and check the resolved platform version before accepting a bump that touches any of them.

Diagnose from the failing job's own log rather than the checks summary, since a check only names the job that failed:

```sh
gh pr checks [N] --repo ptr727/PhotoCleaner
gh api repos/ptr727/PhotoCleaner/actions/jobs/[job-id]/logs
```

Push the fix as a commit directly onto the Dependabot branch rather than waiting on a rebase. The merge bot's "Disable auto-merge on maintainer push" job exists for exactly this, so a maintainer push onto a Dependabot branch is expected and safe.

## Backup and Recovery

The repository is the record, and GitHub holds it. Nothing here keeps state outside git.

The application writes state the user owns rather than the repository: the SQLite databases named by `--db` and `--trashdb`, and the `.bak` files that `process` creates. `undo` restores those `.bak` files, and it is the recovery path for a processing run that went wrong. Running `process --skipbackup` makes that recovery impossible, which is the trade the flag names.

A deleted branch is recoverable from any full clone that still has the commit:

```sh
git push origin [sha]:refs/heads/[branch]
```

Never use `--depth 1` on a clone that will amend or force-push, because a shallow clone severs the merge base and orphans the branch.

## Logs and Debugging

Workflow runs are the CI log. `gh run list --branch [branch]` and `gh run view [id] --log-failed` reach them, and a local gate above reproduces a CI failure exactly, so reproduce locally before reading workflow logs.

The application logs to the console and to the file named by `--logfile`. Raise the level with `--loglevel debug` when a file is rejected and the reason is not obvious.

A calling script branches on the exit code rather than on output. Every command uses the same three codes, and [Exit Codes](./README.md#exit-codes) is the contract for what each one means.

The operational point is that `0` and `2` both mean the command ran to completion, and they differ only in whether every file succeeded. A pipeline that reads any non-zero code as "nothing happened" is therefore wrong: `1` is the only code that says the command reported nothing about the files, and a command-line parse error short-circuits before any work starts, so nothing was touched.

## Tool Usage

The application shells out to external tools rather than reimplementing them, so their versions decide its behavior:

- **exiftool** reads and writes metadata, invoked through `MediaUtilities.GetExifToolJsonAsync`. It is installed in the Docker image, and a native run needs it on `PATH`.
- **ffmpeg** handles video, and is installed in the image alongside exiftool.
- **Docker** is a runtime dependency of `verify` alone, which runs the Immich decoder inside the `immich-server` image. The preflight exits `1` when the `docker` command is missing or the image cannot be prepared, so an unreachable daemon fails the run rather than condemning files.

Every other command runs natively, so outside `verify` Docker is a packaging and tooling concern only, meaning the shipped image and the containerized linters above.

The Immich API key can be given inline with `--apikey` or read from a file with `--apikey-file`, and the two are mutually exclusive. Prefer the file: an inline key lands in shell history and is visible in the process list for as long as the command runs.

## Configuration Layout

- [PhotoCleaner/](./PhotoCleaner/) is the console application.
- [PhotoCleanerTests/](./PhotoCleanerTests/) is the xUnit suite, and [PhotoCleanerBenchmarks/](./PhotoCleanerBenchmarks/) is the benchmark project.
- [Docker/](./Docker/) holds the multi-architecture `Dockerfile` and the Docker Hub README.
- [.github/workflows/](./.github/workflows/) holds the CI and release pipelines. The pull request check reaches the hub-hosted `validate-task.yml` and `build-release-task.yml` in ptr727/ProjectTemplate, and the release chain reaches those two plus `publish-plan-task.yml`, rather than carrying their own copies.
- Analyzer and package configuration is central: `Directory.Build.props` carries the analyzer set, and `Directory.Packages.props` pins every package version.
- [global.json](./global.json) selects Microsoft.Testing.Platform as the test runner for the whole repo, which is what makes `dotnet test` reach the xUnit v3 suite directly instead of the VSTest host the .NET 10 SDK no longer runs it under. It declares no `sdk` key, so it pins no SDK version and affects nothing but test execution.
