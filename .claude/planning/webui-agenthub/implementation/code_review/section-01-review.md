# Code Review: section-01-scaffolding

## CRITICAL
NONE

## HIGH

### H1: `Microsoft.AspNetCore.Mvc.Testing` version `10.0.5` may not exist for .NET 10 pre-GA
File: `src/Directory.Packages.props`
Version `10.0.5` does not exist for pre-GA .NET 10. Should be `10.0.0` or a preview suffix.
**Note:** Build and tests passed locally — NuGet resolved it. May be a false positive if 10.0.5 shipped with the SDK preview installed.

### H2: MediatR DI test is a tautology
File: `src/Content/Tests/Presentation.AgentHub.Tests/ScaffoldTests.cs` line 26
`AddApplicationCommonDependencies()` registers pipeline behaviors that have transitive deps on `IMemoryCache` etc., not all of which are registered. The `GetService<IMediator>()` call returns non-null but doesn't prove the pipeline works. Consistent with section 01 scaffold intent, but technically a weak assertion.

## MEDIUM

### M1: `shadcn` in `dependencies` instead of `devDependencies` (contradicts plan)
File: `src/Content/Presentation/Presentation.WebUI/package.json`
Plan says: "Do not install shadcn as a runtime dependency — it is a CLI tool." shadcn init placed it in dependencies automatically. Should be moved to devDependencies.

### M2: `react-window ^2.2.7` — version accuracy unverified
File: `src/Content/Presentation/Presentation.WebUI/package.json`
`react-window` stable is `1.8.x`. Version `^2.2.7` was resolved by npm install successfully, suggesting it may have shipped a v2. Lock file should confirm.

### M3: `vitest/globals` leaked into app tsconfig
File: `src/Content/Presentation/Presentation.WebUI/tsconfig.app.json`
Makes test globals (`describe`, `it`, `expect`) available in production source without error. Should be isolated to a test-specific tsconfig.

## LOW

### L1: `concurrently` in `dependencies` instead of `devDependencies`
File: `src/Content/Presentation/Presentation.WebUI/package.json`
Process runner, not a browser runtime dep. Should be devDependencies.

### L2: Root `tsconfig.json` has redundant `compilerOptions.baseUrl/paths`
File: `src/Content/Presentation/Presentation.WebUI/tsconfig.json`
`files: []` means this config does no compilation. The paths were added for shadcn detection and are redundant now that `tsconfig.app.json` has them.
