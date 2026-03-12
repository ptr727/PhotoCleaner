# PhotoCleaner — Claude Code Instructions

## Project Overview

PhotoCleaner is a .NET 10 console application that processes and prepares media files for import into photo management systems (Lightroom, Immich, PhotoPrism). It validates, transforms, and repairs media files through a parallel processing pipeline.

## Solution Structure

- `PhotoCleaner/` — Console application (entry point + all processing logic)
- `PhotoCleanerTests/` — xUnit v3 tests

## Build & Quality

### Zero Warnings Policy

All builds must complete without errors or warnings.

### Key Commands

```shell
# Build
dotnet build --verbosity=diagnostic

# Format (run before committing)
dotnet csharpier format --log-level=debug .
dotnet format style --verify-no-changes --severity=info --verbosity=detailed

# Test
dotnet test

# Pre-commit hooks
dotnet husky run
```

### VS Code Tasks

- `.Net Build` — Build with diagnostic verbosity
- `.Net Format` — Verify formatting (must pass before commit)
- `CSharpier Format` — Auto-format code
- `Husky.Net Run` — Run pre-commit hooks manually

## External Dependencies

- **ffmpeg** — Video conversion (MTS/M2TS remux, WMV/AVI re-encode)
- **exiftool** — EXIF metadata reading/writing
- **CliWrap** — CLI process wrapper
- **System.CommandLine** — Argument parsing
- **Serilog** — Structured logging

## C# Coding Conventions

### Language

- Target: .NET 10, C# 14
- Do **NOT** use `var` — always use explicit types
- File-scoped namespaces
- Nullable reference types enabled
- Allman-style braces
- No `#region` blocks

### Naming

- Private fields: `_camelCase`
- Static fields: `s_camelCase`
- Constants: `PascalCase`

### Member Ordering (SA1201)

`const` → `static readonly` → `static fields` → `readonly fields` → `instance fields` → constructors → public members → non-public members → nested types

### Code Patterns

- Guard clauses / early returns for validation
- `async`/`await` all the way — no `.Result` or `.Wait()`
- Always pass `CancellationToken` as last parameter
- Use `ConfigureAwait(false)` in library code (not in xUnit tests)
- Seal classes not designed for inheritance
- Prefer immutable records and frozen collections for read-only data

### Logging

Use Serilog structured logging:

```csharp
logger.Error(exception, "{Function}", function);
```

Use `[CallerMemberName]` for automatic function name tracking. Logger extension methods live in `Extensions.cs`.

### Suppressions

- No `#pragma` to disable analyzers
- Use `[SuppressMessage(..., Justification = "...")]` for one-off cases
- Project-wide suppressions go in `.editorconfig`

## Testing

- Framework: xUnit v3 + AwesomeAssertions
- Pattern: Arrange-Act-Assert
- Naming: `MethodName_Scenario_ExpectedBehavior()`
- No `ConfigureAwait(false)` in test code (xUnit1030)

```csharp
[Fact]
public void MethodName_Scenario_ExpectedBehavior()
{
    // Arrange
    int expected = 42;

    // Act
    int actual = GetValue();

    // Assert
    actual.Should().Be(expected);
}
```

## File Formatting

- C# files: 4-space indent, CRLF line endings
- XML/csproj: 2-space indent, CRLF
- JSON: 4-space indent, CRLF
- Linux scripts (`.sh`): LF

## Markdown & Spelling

- All `.md` files must be lint-clean (markdownlint)
- All spelling must pass CSpell checks (US/UK English)
- Project-specific terms go in the workspace CSpell config
