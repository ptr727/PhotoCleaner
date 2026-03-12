# Code Style and Formatting Rules

## Build Requirements

### Zero Warnings Policy

**CRITICAL**: All builds must complete without warnings. The project enforces this through:

1. **Code change workflow** — run steps in this order:
   1. Format code: `dotnet csharpier format --log-level=debug .`
   2. Verify style: `dotnet format style --verify-no-changes --severity=info --verbosity=detailed`
   3. Build: `dotnet build --verbosity=diagnostic`
   4. Test: `dotnet test`
   - All errors and all warnings must be addressed

2. **Analyzer configuration**
   - `.csproj` file: `<AnalysisLevel>latest-all</AnalysisLevel>`
   - `.csproj` file: `<EnableNETAnalyzers>true</EnableNETAnalyzers>`
   - `.editorconfig` files

3. **Git pre-commit hooks**
   - `dotnet husky run`

### Build Tasks

Available VS Code tasks (use via `run_task` tool):

- `CSharpier Format`: Auto-format code with CSharpier
- `.Net Format`: Verify formatting and style (must pass)
- `.Net Build`: Build with diagnostic verbosity
- `.Net Tool Update`: Update dotnet tools
- `Husky.Net Run`: Run pre-commit hooks manually

## Tooling and Editor

### Code Formatting and Tooling

1. **CSharpier**: Primary code formatter — run before committing

2. **dotnet format**: Style verification — run after CSharpier to confirm no remaining issues

3. **Husky.Net**: Git hooks for automated checks
   - Installed as a local dotnet tool (via `dotnet tool restore`)
   - Install Git hooks locally with `dotnet husky install`
   - Pre-commit hooks run formatting and style checks

4. **Other tools**
   - `dotnet-outdated-tool`: Dependency update checks
   - Nerdbank.GitVersioning: Version management

### Editor Baseline

1. **Required VS Code extensions**: CSharpier, markdownlint, CSpell
2. **VS Code settings**: Use the workspace settings without overrides

### Markdown Files

1. **Linting**: All `.md` files must be linted with the VS Code `markdownlint` extension (local only; no CI)
2. **Zero warnings**: Markdown linting must be error and warning free

### Spelling

1. **CSpell**: All spelling checks must be error free using the CSpell VS Code integration
2. **Accepted spellings**: Words must be correctly spelled in US or UK English
3. **Allowed exceptions**: Project-specific terms must be added to the workspace CSpell config

## Coding Standards and Conventions

Note: Code snippets are illustrative examples only. Replace namespaces/types to match your project.

### C# Language Features

1. **File-scoped namespaces**

   ```csharp
   namespace Example.Project.Library;
   ```

2. **Nullable reference types**: Enabled (`<Nullable>enable</Nullable>`)
   - Use nullable annotations appropriately
   - Use `required` for mandatory properties

3. **Modern C# features**: Prefer modern language constructs
   - Primary constructors when appropriate
   - Top-level statements for console apps
   - Pattern matching over traditional checks
   - Collection expressions (`[…]`) when the target type supports them
   - Extension members using C# 14 `extension()` syntax — do **not** use static class + `this` parameter:

     ```csharp
     // Correct (C# 14)
     extension(ILogger logger)
     {
         public void LogInfo(string message) => logger.Information(message);
     }

     // Incorrect (old style)
     public static class LoggerExtensions
     {
         public static void LogInfo(this ILogger logger, string message) =>
             logger.Information(message);
     }
     ```

   - Target-typed `new` expressions (`new()`) when the type is apparent from context
   - Range and index operators

4. **Expression-bodied members**: Use for applicable members
   - Methods, properties, accessors, operators, lambdas, local functions

5. **`var` keyword**: Do NOT use `var` — always use explicit types, including for LINQ results
   and generic collections

   ```csharp
   // Correct
   int count = 42;
   string name = "test";
   List<string> items = new();
   IEnumerable<FileInfo> files = directory.EnumerateFiles("*.jpg");

   // Incorrect
   var count = 42;
   var name = "test";
   var items = new List<string>();
   var files = directory.EnumerateFiles("*.jpg");
   ```

### Naming Conventions

1. **Private fields**: underscore prefix with camelCase

   ```csharp
   private readonly HttpClient _httpClient;
   private int _counter;
   ```

2. **Static fields**: `s_` prefix with camelCase

   ```csharp
   private static int s_instanceCount;
   ```

3. **Constants**: PascalCase

   ```csharp
   private const int MaxRetries = 3;
   ```

### Code Structure

1. **Global usings**: Use `GlobalUsings.cs` for common namespaces

   ```csharp
   global using System;
   global using System.Net.Http;
   global using System.Threading.Tasks;
   global using Serilog;
   ```

2. **Usings placement**: Outside namespace, sorted with `System` directives first — CSharpier
   handles sorting automatically

   ```csharp
   using System.CommandLine;
   using System.Runtime.CompilerServices;
   using Example.Project.Library;

   namespace Example.Project.Console;
   ```

3. **Braces**: Allman style

   ```csharp
   public void Method()
   {
       if (condition)
       {
           // code
       }
   }
   ```

4. **Indentation**
   - C# files: 4 spaces
   - XML/csproj files: 2 spaces
   - YAML files: 2 spaces
   - JSON files: 4 spaces

5. **Line endings**
   - C#, XML, YAML, JSON, Windows scripts: CRLF
   - Linux scripts (`.sh`): LF

6. **`#region`**: Do not use regions. Prefer logical file/folder/namespace organization.

7. **Member ordering (StyleCop SA1201)**:
   1. `const` fields
   2. `static readonly` fields
   3. Other `static` fields
   4. `readonly` instance fields
   5. Other instance fields
   6. Constructors
   7. Public members: events → properties → indexers → methods → operators
   8. Non-public members: same order as public
   9. Nested types

### Comments and Documentation

1. **XML documentation** (required for library projects; optional for console apps)
   - `<GenerateDocumentationFile>true</GenerateDocumentationFile>`
   - Missing XML comments for public APIs are suppressed (`.editorconfig`)
   - Must document all public surfaces.
   - Single-line summaries, additional details in remarks, document input parameters, returns values, exceptions, and add crefs

   ```csharp
   /// <summary>
   /// Example of a single line summary.
   /// </summary>
   /// <remarks>
   /// Additional important details about usage.
   /// Multiple lines if needed.
   /// </remarks>
   /// <param name="category">
   /// The quote category to request
   /// </param>
   /// <param name="cancellationToken">
   /// A <see cref="System.Threading.CancellationToken"/> that can be used to cancel the request.
   /// </param>
   /// <returns>
   /// A <see cref="string"/> containing the quote text.
   /// </returns>
   /// <exception cref="System.ArgumentException">
   /// Thrown when <paramref name="category"/> is not a supported value.
   /// </exception>
   public async Task<string> GetQuoteOfTheDayAsync(string category, CancellationToken cancellationToken) {}
   ```

2. **Code analysis suppressions**: When a warning cannot be fixed, choose the appropriate approach:
   - **Fix the code** — always the preferred option
   - **`[SuppressMessage]`** — one-off false positives; must include a `Justification`
   - **`.editorconfig`** — project-wide rules that apply uniformly across all files

   Do not use `#pragma` sections to disable analyzers.

   ```csharp
   [System.Diagnostics.CodeAnalysis.SuppressMessage(
       "Design",
       "CA1034:Nested types should not be visible",
       Justification = "https://github.com/dotnet/sdk/issues/51681"
   )]
   ```

### Error Handling and Logging

1. **Serilog logging**: Use structured logging

   ```csharp
   logger.Error(exception, "{Function}", function);
   ```

2. **Library log configuration**: Libraries must expose logging configuration
   - Provide options or settings to supply an `ILoggerFactory` and/or `ILogger`
   - Offer a global fallback logger for static usage when needed

3. **CallerMemberName**: Use for automatic function name tracking

   ```csharp
   public bool LogAndPropagate(
       Exception exception,
       [CallerMemberName] string function = "unknown"
   )
   ```

4. **Logger extensions**: Use `Extensions.cs` for logger and other extension methods

   ```csharp
   extension(ILogger logger)
   {
       public bool LogAndPropagate(Exception exception, ...) { }
   }
   ```

5. **Exceptions**: Do not swallow exceptions; log and rethrow or translate to a domain-specific exception

### Code Patterns

1. **Guard clauses**: Prefer early returns for validation and error handling
2. **Async all the way**: Avoid blocking calls (`.Result`, `.Wait()`); use `async`/`await`
3. **Cancellation tokens**: Accept `CancellationToken` as the last parameter and pass it through
4. **ConfigureAwait**: In library code, use `ConfigureAwait(false)` unless context is required
   - Do not use `ConfigureAwait` in xUnit tests — xUnit provides its own synchronization context,
     and using `ConfigureAwait(false)` triggers warning xUnit1030
5. **Disposables**: Use `await using` for async disposables; prefer `using` declarations
6. **LINQ vs loops**: Use LINQ for clarity, loops for hot paths or allocations
7. **HTTP**: Reuse `HttpClient` via factory; avoid per-request instantiation
8. **Collections**: Prefer `IReadOnlyList<T>`/`IReadOnlyCollection<T>` for public APIs
9. **Immutability**: Prefer immutable records; use init-only setters when records are not suitable;
   prefer immutable or frozen collections for read-only data
10. **Exceptions as control flow**: Avoid using exceptions for expected flow
11. **Sealing classes**: Seal classes that are not designed for inheritance
12. **Lazy initialization**: Use `Lazy<T>` for static, thread-safe instantiation (e.g., logger factory, HTTP factory)

### Testing Conventions

1. **Framework**: xUnit with AwesomeAssertions

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

2. **Organization**: Arrange-Act-Assert pattern
3. **Naming**: Descriptive names with underscores
4. **Theory tests**: Use `[Theory]` with `[InlineData]`
5. **Null assertions**: After `.Should().NotBeNull()`, use the null-forgiving operator `!` to access
   members — required to satisfy CS8629

   ```csharp
   // After NotBeNull(), use ! to satisfy nullable analysis (CS8629)
   Result? result = GetResult();
   result.Should().NotBeNull();
   int value = result!.Value;
   ```

## Project Configuration

1. **Target framework**: .NET 10.0 (`<TargetFramework>net10.0</TargetFramework>`)

2. **AOT compatibility**
   - `<IsAotCompatible>true</IsAotCompatible>`
   - `<VerifyReferenceAotCompatibility>true</VerifyReferenceAotCompatibility>`

3. **Assembly information**
   - Use semantic versioning
   - For packable libraries: Include SourceLink: `<PublishRepositoryUrl>true</PublishRepositoryUrl>`
   - For packable libraries: Embed untracked sources: `<EmbedUntrackedSources>true</EmbedUntrackedSources>`

4. **Internal visibility**: Use `InternalsVisibleTo` for test and benchmark access

   ```xml
   <ItemGroup>
     <InternalsVisibleTo Include="Benchmarks" />
     <InternalsVisibleTo Include="Tests" />
   </ItemGroup>
   ```
