# Assess Before Skipping — And Design What's Missing

## Rule: Never skip a file based on an agent summary alone. Never port a folder without asking what's missing.

Before classifying any template file as "SKIP", "template-specific", or "wrong layer":

1. **Read the actual code** — not a summary, not just the usings. Read the full implementation.
2. **Ask "what generic pattern does this implement?"** — even SDLC-specific services often contain generic infrastructure patterns (caching, file watching, expression evaluation, composite/decorator architecture).
3. **Ask "does our harness need this capability?"** — not "does it need this exact implementation." Middleware is infrastructure consumed by Presentation, not Presentation itself. State management patterns apply to any stateful system, not just SDLC workflows.
4. **If >50% of the code is generic infrastructure**, port it (adapted). Don't skip the whole file because of template-specific naming.

## Rule: Design what's missing (MANDATORY — applies to every folder ported)

After porting files from a folder, ALWAYS ask these questions before considering the folder done:

1. **"Does this system actually work end-to-end?"** — If you ported an attribute that generates dynamic policy names, does a policy provider exist to resolve them? If you ported a handler, is it registered? Trace the runtime path.
2. **"What would a template consumer need that the reference doesn't provide?"** — The reference may have gaps. A template should be MORE complete than its source, not less.
3. **"What helper/extension/factory would make this system easy to wire up?"** — If the Presentation layer needs 5 manual steps to use this feature, add a helper that does it in 1.

**Present these findings to the user** as part of the port — don't wait to be asked.

## Common mistakes this rule prevents:
- Labeling ASP.NET Core middleware as "Presentation" — middleware implementations live in Infrastructure, `app.UseMiddleware<T>()` lives in Presentation.
- Labeling state management as "wrong abstraction" — the patterns (Composite, Decorator, JSON+Markdown dual persistence) are universal; only the domain types need adapting.
- Labeling file services as "overlapping" — `IContentProvider`, `IArtifactStorageService`, and `IFileSystemService` solve different problems.
- Skipping a file because one method is template-specific — port the generic methods, skip the specific ones.
- **Porting an authorization attribute + handler but not the policy provider that makes them work at runtime.**
- **Porting a feature without DI registration, leaving it dead code.**
