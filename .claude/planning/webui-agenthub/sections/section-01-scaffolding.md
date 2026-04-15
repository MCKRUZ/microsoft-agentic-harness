# Section 01: Solution Structure and Project Scaffolding

## Overview

This section creates the two new project directories, registers them in the solution, and establishes a green build/test baseline before any application logic is written. Completing this section is a prerequisite for all other sections.

**Verification command:** `dotnet build src/AgenticHarness.slnx && dotnet test src/AgenticHarness.slnx`

**WebUI verification:** `cd src/Content/Presentation/Presentation.WebUI && npm run build`

---

## Tests First

Write these tests before implementing anything else. They confirm the scaffold compiles and the DI wiring is discoverable.

### AgentHub Build Smoke Tests

File: `src/Content/Tests/Presentation.AgentHub.Tests/ScaffoldTests.cs`

```csharp
/// <summary>
/// Smoke tests that validate the project scaffold compiles and basic DI resolves.
/// These run as part of dotnet test — a failing build means these fail implicitly.
/// </summary>
public class ScaffoldTests
{
    [Fact]
    public void AgentHub_ProjectBuilds_WithoutErrors()
    {
        // This test passes if the assembly loads. Build failure prevents discovery.
        Assert.True(true);
    }

    [Fact]
    public void Presentation_Common_GetServices_Registers_IMediator()
    {
        // Arrange: build a minimal host using GetServices()
        // Act: resolve IMediator from the service provider
        // Assert: resolved instance is not null
        // Note: Call services.GetServices(includeHealthChecksUI: false) from Presentation.Common
    }
}
```

### WebUI Build Smoke Test

There is no xUnit test for WebUI at this stage. The verification is:
- `npm run build` exits 0 (TypeScript compiles, Vite bundles without error)
- `npx vitest run` finds at least one test file (the placeholder test added below)

---

## What to Build

### 1. AgentHub .csproj

**File:** `src/Content/Presentation/Presentation.AgentHub/Presentation.AgentHub.csproj`

This is a standard ASP.NET Core Web API project targeting `net10.0`. Package versions are managed centrally via `src/Directory.Packages.props` — do not specify version attributes on `PackageReference` elements. Two new package versions must be added to `Directory.Packages.props`:

- `Microsoft.AspNetCore.SignalR.Client` — not needed at runtime (SignalR server is in the ASP.NET Core framework for .NET 10), but the test project needs `Microsoft.AspNetCore.Mvc.Testing`
- `Microsoft.AspNetCore.Mvc.Testing` — add to `Directory.Packages.props` under the Testing section

The `.csproj` structure:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Presentation.Common\Presentation.Common.csproj" />
  </ItemGroup>

  <ItemGroup>
    <!-- OpenTelemetry — versions already in Directory.Packages.props -->
    <PackageReference Include="OpenTelemetry" />
    <!-- Microsoft.Identity.Web — already in Directory.Packages.props -->
    <PackageReference Include="Microsoft.Identity.Web" />
  </ItemGroup>

</Project>
```

SignalR server is part of `Microsoft.AspNetCore.App` (the Web SDK implicit framework reference) on .NET 10 — no separate NuGet package is required. `IHostedService` is also in the framework. `OpenTelemetry` is already versioned in `Directory.Packages.props`. `Microsoft.Identity.Web` is already versioned there too.

### 2. AgentHub Test .csproj

**File:** `src/Content/Tests/Presentation.AgentHub.Tests/Presentation.AgentHub.Tests.csproj`

Follow the exact same pattern as `Infrastructure.AI.MCPServer.Tests.csproj`. Add `Microsoft.AspNetCore.Mvc.Testing` to `Directory.Packages.props` (Testing section, latest stable version compatible with .NET 10).

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
    <PackageReference Include="Moq" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\Presentation\Presentation.AgentHub\Presentation.AgentHub.csproj" />
  </ItemGroup>

</Project>
```

The test project uses `Microsoft.NET.Sdk` (not `.Web`) because `WebApplicationFactory<T>` works from non-web test projects.

### 3. Solution Registration

**File:** `src/AgenticHarness.slnx`

Add two new `<Project>` entries to the existing `<Folder Name="/Presentation/">` folder and one new entry to `<Folder Name="/Tests/">`:

```xml
<!-- Inside <Folder Name="/Presentation/"> -->
<Project Path="Content/Presentation/Presentation.AgentHub/Presentation.AgentHub.csproj" />

<!-- Inside <Folder Name="/Tests/"> -->
<Project Path="Content/Tests/Presentation.AgentHub.Tests/Presentation.AgentHub.Tests.csproj" />
```

WebUI is a Node project and must NOT be added to the `.slnx`.

### 4. Minimal AgentHub Entry Point

The project needs a compilable `Program.cs` before full wiring (done in section 02). Create a minimal stub that allows `dotnet build` to succeed:

**File:** `src/Content/Presentation/Presentation.AgentHub/Program.cs`

```csharp
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.Run();
```

This will be replaced entirely in section 02. Its only purpose here is to satisfy the SDK requirement that a `Program.cs` exists.

### 5. Minimal Test Stub

The test project needs at least one test class for `dotnet test` to find the assembly:

**File:** `src/Content/Tests/Presentation.AgentHub.Tests/ScaffoldTests.cs`

As shown in the Tests First section above. Keep it minimal — the real test infrastructure (TestWebApplicationFactory, TestAuthHandler) is built in section 07.

### 6. WebUI Project

**Directory:** `src/Content/Presentation/Presentation.WebUI/`

Initialize the Vite + React TypeScript project:

```bash
cd src/Content/Presentation/Presentation.WebUI
npm create vite@latest . -- --template react-ts
```

Then install all runtime and dev dependencies in a single pass:

```bash
npm install tailwindcss @tailwindcss/vite
npm install @azure/msal-browser @azure/msal-react
npm install @microsoft/signalr
npm install zustand @tanstack/react-query
npm install react-hook-form zod @hookform/resolvers
npm install react-window
npm install axios concurrently

npm install --save-dev \
  @types/react-window \
  vitest @vitest/coverage-v8 \
  @testing-library/react @testing-library/user-event @testing-library/jest-dom \
  msw jsdom
```

Do not install `shadcn` as a runtime dependency — it is a CLI tool used to copy component source files. Run it after installation:

```bash
npx shadcn@latest init
```

Accept defaults during `shadcn init` — it will configure `tailwind.config.js`, `components.json`, and `src/lib/utils.ts`. All shadcn component source files copied into `src/components/ui/` in section 08.

### 7. WebUI tsconfig.json

The generated `tsconfig.json` from `vite` must be updated to enable strict mode and path aliases. Set the following compiler options:

```json
{
  "compilerOptions": {
    "strict": true,
    "noUncheckedIndexedAccess": true,
    "noImplicitReturns": true,
    "paths": {
      "@/*": ["src/*"]
    }
  }
}
```

Also configure Vite to resolve the `@` alias in `vite.config.ts`:

```ts
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import path from 'path'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { '@': path.resolve(__dirname, './src') },
  },
})
```

### 8. WebUI vitest.config.ts

Vitest needs its own config (separate from `vite.config.ts`) so it can target `jsdom` without affecting the browser build:

**File:** `src/Content/Presentation/Presentation.WebUI/vitest.config.ts`

```ts
import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import path from 'path'

export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    coverage: {
      provider: 'v8',
      thresholds: { lines: 80, functions: 80, branches: 80, statements: 80 },
    },
  },
  resolve: {
    alias: { '@': path.resolve(__dirname, './src') },
  },
})
```

### 9. WebUI package.json scripts

Ensure `package.json` has these scripts after scaffolding (merge with whatever `vite` generated):

```json
{
  "scripts": {
    "dev": "vite",
    "build": "tsc -b && vite build",
    "test": "vitest run",
    "test:watch": "vitest",
    "test:coverage": "vitest run --coverage",
    "preview": "vite preview"
  }
}
```

### 10. WebUI placeholder test

Create `src/Content/Presentation/Presentation.WebUI/src/test/setup.ts` as an empty file (MSW and jest-dom setup added in section 12). Create a minimal placeholder test so `vitest run` succeeds:

**File:** `src/Content/Presentation/Presentation.WebUI/src/test/scaffold.test.ts`

```ts
describe('scaffold', () => {
  it('test infrastructure is wired', () => {
    expect(true).toBe(true)
  })
})
```

---

## Implementation Notes (Actual)

### Deviations from Plan
- **TypeScript 6.0 / `baseUrl` deprecation**: TS 6.0 deprecated `baseUrl`. Removed `baseUrl` from `tsconfig.app.json`; `paths` works without it in TS 6.
- **`vitest/globals` isolation**: Added `tsconfig.test.json` (extends `tsconfig.app.json`, adds `vitest/globals`, includes `src/test` only) to keep test globals out of the app compilation context. `tsconfig.app.json` excludes `src/test`. `vitest.config.ts` references `typecheck.tsconfig: './tsconfig.test.json'`.
- **`shadcn` and `concurrently` moved to devDependencies**: `shadcn init` placed them in `dependencies`; corrected post-init.
- **Nested `.git` removed**: Vite scaffold creates its own `.git`; removed before staging.
- **Tailwind v4 CSS config**: No `tailwind.config.js` (v4 uses `@import "tailwindcss"` in CSS). shadcn init configured `src/index.css` with v4 theme variables.
- **Additional shadcn-installed packages**: `@base-ui/react`, `tw-animate-css`, `@fontsource-variable/geist`, `class-variance-authority`, `clsx`, `lucide-react`, `tailwind-merge` added by `shadcn init`.
- **`tsconfig.json` retains `paths`**: Root `tsconfig.json` keeps `paths` so `npx shadcn add <component>` (used in section 08) can detect the alias.

### Actual Files Created
- `src/Content/Presentation/Presentation.AgentHub/Presentation.AgentHub.csproj`
- `src/Content/Presentation/Presentation.AgentHub/Program.cs` (minimal stub)
- `src/Content/Tests/Presentation.AgentHub.Tests/Presentation.AgentHub.Tests.csproj`
- `src/Content/Tests/Presentation.AgentHub.Tests/ScaffoldTests.cs` (2 tests: build smoke + MediatR DI)
- `src/AgenticHarness.slnx` (updated — 2 new project entries)
- `src/Directory.Packages.props` (updated — added `Microsoft.AspNetCore.Mvc.Testing 10.0.5`)
- `src/Content/Presentation/Presentation.WebUI/` (full Vite scaffold + all deps)
- `src/Content/Presentation/Presentation.WebUI/tsconfig.test.json` (new — vitest type isolation)

### Test Results
- `dotnet test`: 2 new tests passed (487 total)
- `npm test`: 1 test file, 1 test passed

---

## Directory Layout After This Section

```
src/
  AgenticHarness.slnx                         ← updated (two new entries)
  Directory.Packages.props                    ← updated (Microsoft.AspNetCore.Mvc.Testing)
  Content/
    Presentation/
      Presentation.AgentHub/
        Presentation.AgentHub.csproj
        Program.cs                            ← minimal stub
      Presentation.WebUI/
        package.json
        vite.config.ts
        vitest.config.ts
        tsconfig.json                         ← strict mode + path alias
        components.json                       ← from shadcn init
        src/
          test/
            setup.ts                          ← empty placeholder
            scaffold.test.ts
    Tests/
      Presentation.AgentHub.Tests/
        Presentation.AgentHub.Tests.csproj
        ScaffoldTests.cs
```

---

## Dependencies

This section has no dependencies on other sections. All subsequent sections depend on this one completing successfully.

**Sections that can run in parallel after this completes:**
- section-02-agenthub-core
- section-08-webui-shell

---

## Verification Checklist

Before marking this section complete:

1. `dotnet build src/AgenticHarness.slnx` — exits 0, no warnings
2. `dotnet test src/AgenticHarness.slnx` — discovers and passes the `ScaffoldTests` in `Presentation.AgentHub.Tests`
3. `cd src/Content/Presentation/Presentation.WebUI && npm run build` — exits 0, TypeScript compiles, Vite bundles
4. `cd src/Content/Presentation/Presentation.WebUI && npm test` — vitest discovers and passes `scaffold.test.ts`
5. `Presentation.AgentHub` and `Presentation.AgentHub.Tests` appear in `dotnet sln src/AgenticHarness.slnx list` output
6. `Presentation.WebUI` does NOT appear in solution list (it is Node-only)
