using System.Collections;
using System.Reflection;
using Application.Core.Validation;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Presentation.FoundryHost;
using Xunit;

namespace Presentation.FoundryHost.Tests;

/// <summary>
/// The paths FoundryHost roots under <c>$HOME/agent-state</c> (#996): the audit pseudonym key, the verification checkpoints,
/// the erasure journal and the other personal-data stores must outlive a container restart, and rooting them must not
/// trip any rule the configuration is validated by at startup.
/// </summary>
public sealed class FoundryHostHomeRootedPathTests
{
    private const string Home = "/home/agent";

    private static AppConfig Bound(string? home)
    {
        var overrides = FoundryHostBootstrap.BuildConfigOverrides(name => name == "HOME" ? home : null);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(overrides.Select(o =>
                new KeyValuePair<string, string?>(o.Key.Replace("__", ":", StringComparison.Ordinal), o.Value)))
            .Build();
        return configuration.GetSection("AppConfig").Get<AppConfig>() ?? new AppConfig();
    }

    [Fact]
    public void BuildConfigOverrides_WithHome_RootsThePseudonymKeyUnderTheStateRoot()
    {
        var config = Bound(Home);

        config.AI.Audit.PseudonymKeyPath.Should().StartWith($"{Home}/agent-state/",
            "only $HOME survives Foundry's idle/resume, and a key made anew at every start breaks every reference made before it");
    }

    [Fact]
    public void BuildConfigOverrides_WithHome_ThePseudonymKeyPathPassesTheKeyValidator()
    {
        var config = Bound(Home);

        var result = new AuditPseudonymKeyValidator().Validate(config.AI);

        result.IsValid.Should().BeTrue(
            "the rooted key must sit outside every rooted audit folder: " + string.Join("; ", result.Errors));
    }

    [Fact]
    public void BuildConfigOverrides_WithHome_PutsTheComplianceTrailInItsOwnFolderOnce()
    {
        var config = Bound(Home);

        Path.Combine(config.AI.Erasure.AuditStoragePath, ErasureConfig.TrailFolderName).Replace('\\', '/')
            .Should().Be($"{Home}/agent-state/compliance", "not compliance/compliance");
    }

    [Fact]
    public void BuildConfigOverrides_WithHome_RootingAddsNoErrorToAnyConfigValidator()
    {
        var rooted = ValidationErrors(Bound(Home), out var validated);
        var unrooted = ValidationErrors(Bound(home: null), out _);

        validated.Should().Contain(
            [typeof(AuditPseudonymKeyValidator), typeof(AuditArchivePathValidator), typeof(ErasureConfigValidator)],
            "the rules about where audit files may go are the ones rooting could break");
        rooted.Except(unrooted).Should().BeEmpty(
            "every rule the configuration meets without $HOME it must still meet with every store rooted under it");
    }

    /// <summary>
    /// Runs every validator in Application.Core that needs no services against each part of <paramref name="config"/> it
    /// validates, and returns the errors as "validator: property: message".
    /// </summary>
    private static HashSet<string> ValidationErrors(AppConfig config, out List<Type> validated)
    {
        var parts = ConfigParts(config);
        var errors = new HashSet<string>(StringComparer.Ordinal);
        validated = [];
        foreach (var validatorType in typeof(AuditPseudonymKeyValidator).Assembly.GetTypes())
        {
            if (ValidatedType(validatorType) is not { } target || !parts.TryGetValue(target, out var instances))
                continue;

            var validator = (IValidator)Activator.CreateInstance(validatorType)!;
            validated.Add(validatorType);
            foreach (var instance in instances)
            {
                foreach (var error in validator.Validate(new ValidationContext<object>(instance)).Errors)
                    errors.Add($"{validatorType.Name}: {error.PropertyName}: {error.ErrorMessage}");
            }
        }

        return errors;
    }

    /// <summary>The type a validator that needs no services validates, or null for any other type.</summary>
    private static Type? ValidatedType(Type type)
    {
        if (type is not { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
            || type.GetConstructor(Type.EmptyTypes) is null)
            return null;

        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(AbstractValidator<>))
                return current.GetGenericArguments()[0];
        }

        return null;
    }

    /// <summary>Every configuration object reachable from <paramref name="root"/>, by type.</summary>
    private static Dictionary<Type, List<object>> ConfigParts(AppConfig root)
    {
        var parts = new Dictionary<Type, List<object>>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>([root]);
        while (queue.TryDequeue(out var part))
        {
            if (!seen.Add(part))
                continue;
            if (!parts.TryGetValue(part.GetType(), out var list))
                parts[part.GetType()] = list = [];
            list.Add(part);

            foreach (var child in Children(part))
                queue.Enqueue(child);
        }

        return parts;
    }

    private static IEnumerable<object> Children(object part)
    {
        foreach (var property in part.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || property.GetValue(part) is not { } value)
                continue;
            if (value is IEnumerable items and not string)
            {
                foreach (var item in items)
                {
                    if (item is not null && IsConfigObject(item.GetType()))
                        yield return item;
                }
            }
            else if (IsConfigObject(value.GetType()))
                yield return value;
        }
    }

    private static bool IsConfigObject(Type type) =>
        type.IsClass && type.Namespace?.StartsWith("Domain.Common.Config", StringComparison.Ordinal) == true;
}
