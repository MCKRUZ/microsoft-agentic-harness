# Assess Before Skipping

## Rule: Never skip a file based on an agent summary alone.

Before classifying any template file as "SKIP", "template-specific", or "wrong layer":

1. **Read the actual code** — not a summary, not just the usings. Read the full implementation.
2. **Ask "what generic pattern does this implement?"** — even SDLC-specific services often contain generic infrastructure patterns (caching, file watching, expression evaluation, composite/decorator architecture).
3. **Ask "does our harness need this capability?"** — not "does it need this exact implementation." Middleware is infrastructure consumed by Presentation, not Presentation itself. State management patterns apply to any stateful system, not just SDLC workflows.
4. **If >50% of the code is generic infrastructure**, port it (adapted). Don't skip the whole file because of template-specific naming.

## Common mistakes this rule prevents:
- Labeling ASP.NET Core middleware as "Presentation" — middleware implementations live in Infrastructure, `app.UseMiddleware<T>()` lives in Presentation.
- Labeling state management as "wrong abstraction" — the patterns (Composite, Decorator, JSON+Markdown dual persistence) are universal; only the domain types need adapting.
- Labeling file services as "overlapping" — `IContentProvider`, `IArtifactStorageService`, and `IFileSystemService` solve different problems.
- Skipping a file because one method is template-specific — port the generic methods, skip the specific ones.
