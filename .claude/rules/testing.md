---
paths: src/Content/Testing/**/*.cs
---
# Testing Rules

## Framework
xUnit + Moq + coverlet. 80% minimum coverage on new code.

## Test Project Structure
Mirror the layer structure:
- `Testing.Domain.UnitTests` — Domain logic
- `Testing.Application.*.UnitTests` — Command/query handlers, validators
- `Testing.Infrastructure.*.UnitTests` — Service implementations
- `Testing.Presentation.*.UnitTests` — API endpoints, middleware

## Patterns
- AAA (Arrange-Act-Assert) in every test
- One assertion per test method (prefer multiple focused tests over one mega-test)
- Use `WebApplicationFactory<Program>` for integration tests
- Mock external dependencies (AI services, MCP servers) with Moq
- Never mock domain objects — use real instances

## Bug Fix Workflow (MANDATORY)
1. Write a failing test that reproduces the bug
2. Fix the bug
3. Verify the test passes

## Naming
`MethodName_StateUnderTest_ExpectedBehavior`
Example: `LoadSkill_Tier2_ReturnsInstructionsOnDemand`

## AI/Agent Testing
- Mock `IChatClientFactory` to return deterministic responses
- Test skill progressive disclosure: verify Tier 1 loads without Tier 2/3
- Test tool resolution: verify keyed DI resolves correct implementation
- Test MCP server endpoints with `WebApplicationFactory` + test JWT tokens
- Test content safety middleware with known-bad inputs
