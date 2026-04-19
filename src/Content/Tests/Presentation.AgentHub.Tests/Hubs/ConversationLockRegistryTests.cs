using FluentAssertions;
using Presentation.AgentHub.Hubs;
using Xunit;

namespace Presentation.AgentHub.Tests.Hubs;

public sealed class ConversationLockRegistryTests
{
    [Fact]
    public void GetOrCreate_SameId_ReturnsSameSemaphore()
    {
        var registry = new ConversationLockRegistry();

        var sem1 = registry.GetOrCreate("conv-1");
        var sem2 = registry.GetOrCreate("conv-1");

        sem1.Should().BeSameAs(sem2);
    }

    [Fact]
    public void GetOrCreate_DifferentIds_ReturnDifferentSemaphores()
    {
        var registry = new ConversationLockRegistry();

        var sem1 = registry.GetOrCreate("conv-1");
        var sem2 = registry.GetOrCreate("conv-2");

        sem1.Should().NotBeSameAs(sem2);
    }

    [Fact]
    public void GetOrCreate_ReturnsSemaphoreWithInitialCountOfOne()
    {
        var registry = new ConversationLockRegistry();

        var sem = registry.GetOrCreate("conv-test");

        sem.CurrentCount.Should().Be(1);
    }

    [Fact]
    public async Task GetOrCreate_SemaphoreEnforcesSerialAccess()
    {
        var registry = new ConversationLockRegistry();
        var sem = registry.GetOrCreate("conv-serial");

        await sem.WaitAsync();

        // Second wait should not complete immediately
        var completed = sem.Wait(TimeSpan.FromMilliseconds(50));
        completed.Should().BeFalse("semaphore should be held");

        sem.Release();

        // Now it should be acquirable
        await sem.WaitAsync();
        sem.CurrentCount.Should().Be(0);
        sem.Release();
    }

    [Fact]
    public void GetOrCreate_ConcurrentAccess_NoDataCorruption()
    {
        var registry = new ConversationLockRegistry();
        var semaphores = new SemaphoreSlim[100];

        Parallel.For(0, 100, i =>
        {
            semaphores[i] = registry.GetOrCreate("shared-conv");
        });

        // All should be the same instance
        semaphores.Distinct().Should().HaveCount(1);
    }
}
