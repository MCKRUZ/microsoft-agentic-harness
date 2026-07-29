using Xunit;

// Test classes in this assembly boot real hosts, and several of them override configuration through
// ENVIRONMENT VARIABLES — the only configuration source that both outranks appsettings.json and is
// visible to the eager builder.Configuration read inside AddExecutionApiServices.
//
// Environment variables are process-global. A lock around "set, boot, clear" only serializes the
// classes that take that lock; any other class booting a host in parallel can observe the override
// and behave as though the operator had set it. That is not hypothetical — adding a second
// host-booting class made WorkflowOwnershipIsolationTests intermittently receive 403 (workflow
// submission disabled) from the window opened by WorkflowsControllerIntegrationTests.
//
// Disabling parallelization assembly-wide is deliberately blunt. A shared collection would be
// narrower but would depend on every future host-booting class remembering to join it, and the
// failure mode when one forgets is an intermittent test that looks like a product defect. This suite
// runs in roughly four seconds; the determinism is worth more than the concurrency.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
