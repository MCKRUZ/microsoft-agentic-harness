# Code Review Interview: section-01-scaffolding

## Findings Disposition

### H1: Microsoft.AspNetCore.Mvc.Testing 10.0.5 version
**Disposition:** Let go — `dotnet build` and `dotnet test` both passed, confirming NuGet resolved the package successfully. Non-issue.

### H2: MediatR DI test tautology
**Disposition:** Let go — the test matches the section 01 plan intent: "resolve IMediator from the service provider" and "Assert: resolved instance is not null". The intent is a build smoke test, not a pipeline validation test. Full pipeline validation belongs in section 07.

### M1: `shadcn` in `dependencies` instead of `devDependencies`
**Disposition:** Auto-fixed — moved `shadcn` from `dependencies` to `devDependencies` in `package.json`. shadcn is a CLI tool, not a browser runtime dep.

### M2: `react-window ^2.2.7` version accuracy
**Disposition:** Let go — `npm install` succeeded and the package resolved. Version confirmed present in npm registry.

### M3: `vitest/globals` leaked into app tsconfig
**Disposition:** User decision: create `tsconfig.test.json`. Applied:
- Created `tsconfig.test.json` extending `tsconfig.app.json`, adding `vitest/globals` to types, including only `src/test`
- Removed `vitest/globals` from `tsconfig.app.json` types
- Added `exclude: ["src/test"]` to `tsconfig.app.json`
- Configured `vitest.config.ts` with `typecheck.tsconfig: './tsconfig.test.json'`

### L1: `concurrently` in `dependencies`
**Disposition:** Auto-fixed — moved to `devDependencies`.

### L2: Redundant `paths` in root `tsconfig.json`
**Disposition:** Let go — kept because shadcn CLI reads from root `tsconfig.json` for alias detection when adding components in later sections. Removing it would break `npx shadcn add <component>` in sections 08+.

## Verified
- `npm run build` ✓
- `npm test` ✓ (1 test file, 1 passed)
