# PhotoCleaner — Claude Code Instructions

@.github/copilot-instructions.md

## Workflow Notes

## Critical Rule

After every code change, run `dotnet build` and ensure 0 errors and 0 warnings before finishing.

## Key Commands

```shell
dotnet build PhotoCleaner --verbosity=diagnostic
dotnet csharpier format --log-level=debug .
dotnet format style --verify-no-changes --severity=info --verbosity=detailed
dotnet test
dotnet husky run
```

## Coding Style

See [`CODESTYLE.md`](./CODESTYLE.md) for authoritative style rules.
