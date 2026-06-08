# workspace-skill

Plugin pack providing a workspace-bound skill that lets an agent inspect a
sandbox-injected working copy and propose changes through the harness's
`ChangeProposal` pipeline. Mutations never bypass the gate + approval flow.

## What you get

- `skills/workspace/SKILL.md` — the skill manifest.
- Five keyed-DI tools registered by `Infrastructure.AI`:
  - `read_file` — reads from the working copy.
  - `write_file` — submits a `ChangeProposal`; **does not** write to disk.
  - `list_files` — lists files (with optional glob filter).
  - `run_tests` — runs the test suite inside the sandbox.
  - `run_lint` — runs lint inside the sandbox.

## Hard guarantees

- `denied-tools` blocks `shell_exec` and `raw_filesystem` for this skill — even
  if those tools are loaded for other skills, they are unreachable here.
- `sandbox-required: true` ensures the tools only run within a sandbox-managed
  invocation.
- `egress.allowlist: []` is deny-all. The skill has no outbound network surface.
- `write_file` cannot mutate disk directly — by construction it dispatches a
  `SubmitChangeProposalCommand` and returns the resulting proposal id. The
  mutation only happens after gates pass and an approver signs off.

## Wiring it up

Reference the plugin in your `appsettings.json`:

```jsonc
{
  "AppConfig": {
    "AI": {
      "Plugins": {
        "Packages": [
          {
            "Name": "workspace-skill",
            "Path": "plugins/workspace-skill",
            "Enabled": true,
            "AllowedTools": ["read_file", "write_file", "list_files", "run_tests", "run_lint"],
            "DeniedTools": ["shell_exec", "raw_filesystem"]
          }
        ]
      }
    }
  }
}
```

The harness reads `plugin.json`, picks up the `skills/` folder, parses the
SKILL.md, and surfaces the skill at runtime. The `IWorkspaceContextAccessor`
ambient flows the sandbox-injected working-copy path into every tool
invocation.
