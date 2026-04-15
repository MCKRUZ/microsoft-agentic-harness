# Code Review: Section-08 Skill Content Provider

## Summary

Clean, minimal abstraction that enables evaluation runs to inject in-memory candidate skill content instead of reading from disk. The interface placement (Application layer), implementation placement (Infrastructure layer), and DI wiring all follow Clean Architecture conventions correctly. The AdditionalPropertiesKey constant follows the established ITraceWriter precedent. No security issues. The primary concerns are a missing null-guard on CandidateSkillContentProvider constructor, a TOCTOU race in FileSystemSkillContentProvider, and missing path traversal validation on the filesystem provider.

## Verdict: WARNING -- merge with fixes for M-1 through M-3

## Findings

### CRITICAL

None.

### HIGH

None.

### MEDIUM

**[M-1] FileSystemSkillContentProvider has a TOCTOU race condition**
File: `FileSystemSkillContentProvider.cs:14-17`

File.Exists followed by File.ReadAllTextAsync is a classic time-of-check-to-time-of-use race. A file can be deleted between the check and the read, causing an unhandled FileNotFoundException. Since skill files can be modified by the proposer while evaluation is in progress (the entire reason CandidateSkillContentProvider exists), this race is plausible in production.

Fix -- catch the exception instead of pre-checking:

```csharp
public async Task<string?> GetSkillContentAsync(string skillPath, CancellationToken cancellationToken = default)
{
    try
    {
        return await File.ReadAllTextAsync(skillPath, cancellationToken);
    }
    catch (FileNotFoundException)
    {
        return null;
    }
    catch (DirectoryNotFoundException)
    {
        return null;
    }
}
```

This also handles the case where a parent directory does not exist, which File.Exists returns false for but is a distinct failure mode.

**[M-2] CandidateSkillContentProvider does not guard against null constructor argument**
File: `CandidateSkillContentProvider.cs:20`

If someone passes null for skillFileSnapshots, the NullReferenceException will surface at GetSkillContentAsync call time, far from the construction site. Per project conventions (validate at system boundaries), the constructor should guard:

```csharp
public CandidateSkillContentProvider(IReadOnlyDictionary<string, string> skillFileSnapshots)
{
    ArgumentNullException.ThrowIfNull(skillFileSnapshots);
    _skillFileSnapshots = skillFileSnapshots;
}
```

**[M-3] FileSystemSkillContentProvider performs no path validation**
File: `FileSystemSkillContentProvider.cs:14`

The skillPath parameter is passed directly to File.ReadAllTextAsync with no validation. Unlike the FileSystemService (which is sandboxed to AllowedBasePaths), this provider will read any file the process can access. If a caller passes an unsanitized path (e.g., from a skill manifest that references ../../etc/passwd), this becomes a path traversal vulnerability.

Currently the only callers are internal infrastructure code, and skill paths come from config/SKILL.md parsing, not user input. However, as a template project, downstream consumers may wire this differently.

Fix -- either:
1. Accept an IFileSystemService dependency and delegate reads through the sandboxed service, or
2. Add a simple path validation (reject paths containing .., require paths to be rooted under a configured skills directory), or
3. Document the trust boundary explicitly in the XML doc: callers are responsible for validating that skillPath is within an allowed directory.

Option 3 is acceptable for the current POC scope; option 1 is the right long-term answer for a template.

### LOW

**[L-1] DI registration is Transient -- consider Singleton**
File: `DependencyInjection.cs:129`

FileSystemSkillContentProvider is stateless -- it holds no fields and its only dependency is the static File class. Registering it as Transient creates a new instance per resolution. Singleton would be sufficient and avoids unnecessary allocations. The CandidateSkillContentProvider is correctly not registered in DI (constructed manually per evaluation), so this only affects the default provider.

**[L-2] No test for CandidateSkillContentProvider with empty dictionary**
File: `SkillContentProviderTests.cs`

The tests cover path-in-snapshot and path-not-in-snapshot, but not the edge case of an empty dictionary. While functionally equivalent to path-not-in-snapshot, an explicit empty-snapshot test documents the expected behavior when a candidate has no skill file overrides.

**[L-3] AgentExecutionContextFactory constructor parameter count is growing**
File: `AgentExecutionContextFactory.cs:37-46`

The constructor now has 9 parameters (4 required, 5 optional). This is approaching the threshold where an options/builder pattern would improve readability. Not blocking for section-08 (which adds one parameter to an existing pattern), but worth noting as the factory accumulates more optional dependencies.

**[L-4] CandidateSkillContentProvider.GetSkillContentAsync does case-sensitive path matching**
File: `CandidateSkillContentProvider.cs:26`

IReadOnlyDictionary.TryGetValue uses the dictionary comparer, which defaults to StringComparer.Ordinal. On Windows, file paths are case-insensitive (skills/Foo/SKILL.md vs skills/foo/SKILL.md). If snapshot keys come from one path normalization and lookups come from another, matches will silently fail.

Fix -- the caller constructing the dictionary should use StringComparer.OrdinalIgnoreCase, or the provider should normalize paths. Since this is an Infrastructure concern and the provider receives the dictionary from outside, document the expectation in the param doc or enforce it in the constructor by wrapping with a case-insensitive comparer.

### INFO

**[I-1] AdditionalPropertiesKey constant follows established pattern**
The ISkillContentProvider.AdditionalPropertiesKey = "__skillContentProvider" mirrors the ITraceWriter.AdditionalPropertiesKey = "__traceWriter" pattern. Consistent and discoverable.

**[I-2] No consumers of AdditionalPropertiesKey yet**
The provider is stored in AdditionalProperties but nothing retrieves it in this diff. This is expected -- the evaluator (section-12) and outer loop (section-14) will consume it. Noted for completeness.

**[I-3] Clean Architecture compliance is correct**
- Interface in Application.AI.Common/Interfaces/Skills/ -- correct layer
- Implementations in Infrastructure.AI/Skills/ -- correct layer
- AgentExecutionContextFactory (Application layer) references only the interface, never the concrete types -- correct dependency direction
- DI registration in Infrastructure.AI/DependencyInjection.cs -- correct composition location

## Files Reviewed

| File | Lines | Status |
|------|-------|--------|
| ISkillContentProvider.cs | 18 | OK |
| FileSystemSkillContentProvider.cs | 19 | OK |
| CandidateSkillContentProvider.cs | 29 | OK |
| AgentExecutionContextFactory.cs | 370 | OK (under 400 limit) |
| DependencyInjection.cs | 222 | OK |
| SkillContentProviderTests.cs | 69 | OK |

## Test Coverage Assessment

- **CandidateSkillContentProvider hit/miss**: Covered (2 tests)
- **FileSystemSkillContentProvider existing/missing file**: Covered (2 tests)
- **Gaps**: No empty-dictionary test, no TOCTOU/concurrent-deletion test, no path-with-different-casing test (relevant to L-4), no test for AgentExecutionContextFactory wiring the provider into AdditionalProperties
