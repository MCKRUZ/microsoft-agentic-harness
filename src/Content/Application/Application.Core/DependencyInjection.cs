using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Application.Core;

/// <summary>
/// Dependency injection configuration for the Application.Core layer.
/// Registers CQRS handlers, validators, and agent-specific services.
/// </summary>
/// <remarks>
/// Called from the Presentation composition root after Application.AI.Common:
/// <code>
/// services.AddApplicationCommonDependencies(appConfig);
/// services.AddApplicationAIDependencies();
/// services.AddApplicationCoreDependencies();
/// </code>
/// </remarks>
public static class DependencyInjection
{
	/// <summary>
	/// Registers all Application.Core dependencies into the service collection.
	/// </summary>
	public static IServiceCollection AddApplicationCoreDependencies(
		this IServiceCollection services)
	{
		var assembly = typeof(DependencyInjection).Assembly;

		// Auto-discover MediatR handlers in this assembly
		services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(assembly));

		// Auto-discover FluentValidation validators in this assembly
		services.AddValidatorsFromAssembly(assembly);

		return services;
	}
}
