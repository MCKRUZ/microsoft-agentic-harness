using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Services.Sandbox;
using Domain.AI.Sandbox;
using Microsoft.Extensions.Logging;
using Presentation.ConsoleUI.Common.Helpers;
using Spectre.Console;

namespace Presentation.ConsoleUI.Examples;

/// <summary>
/// Demonstrates capability-based tool permission enforcement and permission profile resolution
/// with deny-overrides-allow semantics. Covers capability taxonomy, profile resolution,
/// valid enforcement, invalid enforcement, and the resolution process.
/// </summary>
public class SandboxCapabilitiesExample
{
    private readonly ICapabilityEnforcer _enforcer;
    private readonly ToolPermissionProfileResolver _resolver;
    private readonly ILogger<SandboxCapabilitiesExample> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SandboxCapabilitiesExample"/> class.
    /// </summary>
    /// <param name="enforcer">Capability enforcer for permission checks.</param>
    /// <param name="resolver">Permission profile resolver for tool capability discovery.</param>
    /// <param name="logger">Logger instance.</param>
    public SandboxCapabilitiesExample(
        ICapabilityEnforcer enforcer,
        ToolPermissionProfileResolver resolver,
        ILogger<SandboxCapabilitiesExample> logger)
    {
        _enforcer = enforcer;
        _resolver = resolver;
        _logger = logger;
    }

    /// <summary>
    /// Runs the interactive sandbox capabilities demonstration.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ConsoleHelper.DisplayHeader("Sandbox Capabilities & Permission Enforcement", Color.Blue);
            ConsoleHelper.DisplayModeInfo(isLive: false, "Pure logic — no external dependencies");

            await Step1_DisplayCapabilityTaxonomyAsync();
            await Step2_ResolveProfilesAsync(cancellationToken);
            await Step3_ValidEnforcementAsync(cancellationToken);
            await Step4_InvalidEnforcementAsync(cancellationToken);
            Step5_DisplayResolutionProcess();

            AnsiConsole.WriteLine();
            ConsoleHelper.DisplaySuccess("Sandbox capabilities demonstration complete.");
        }
        catch (Exception ex)
        {
            ConsoleHelper.DisplayError($"Demo failed: {ex.Message}");
            _logger.LogError(ex, "SandboxCapabilitiesExample failed");
        }
    }

    private static Task Step1_DisplayCapabilityTaxonomyAsync()
    {
        ConsoleHelper.DisplayStep(1, 5, "Capability Taxonomy");
        AnsiConsole.WriteLine("All available ToolCapability flags with their bit values:");
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]Capability[/]");
        table.AddColumn("[bold]Bit Value[/]", cfg => cfg.Alignment(Justify.Right));
        table.AddColumn("[bold]Description[/]");

        var capabilities = new[]
        {
            ("None", 0, "No capabilities required."),
            ("FileRead", 1, "Read access to the filesystem."),
            ("FileWrite", 2, "Write access to the filesystem."),
            ("NetworkAccess", 4, "Outbound network access (HTTP, TCP, etc.)."),
            ("Subprocess", 8, "Ability to spawn child processes."),
            ("EnvRead", 16, "Read access to environment variables."),
            ("DatabaseRead", 32, "Read access to databases."),
            ("DatabaseWrite", 64, "Write access to databases."),
            ("LlmInvocation", 128, "Ability to invoke LLM inference endpoints.")
        };

        foreach (var (name, value, description) in capabilities)
        {
            table.AddRow(
                $"[cyan]{name}[/]",
                $"[yellow]{value}[/]",
                description);
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        return Task.CompletedTask;
    }

    private async Task Step2_ResolveProfilesAsync(CancellationToken cancellationToken)
    {
        ConsoleHelper.DisplayStep(2, 5, "Profile Resolution");
        AnsiConsole.WriteLine("Resolving permission profiles for sample tools:");
        AnsiConsole.WriteLine();

        var tools = new[] { "file_system", "web_search", "calculation_engine" };

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]Tool[/]");
        table.AddColumn("[bold]Required Capabilities[/]");
        table.AddColumn("[bold]Isolation Level[/]");
        table.AddColumn("[bold]Allowed Paths[/]");

        foreach (var toolName in tools)
        {
            try
            {
                var profile = await _enforcer.ResolveProfileAsync(toolName, cancellationToken);

                var capsDisplay = profile.RequiredCapabilities == ToolCapability.None
                    ? "[grey]None[/]"
                    : $"[green]{FormatCapabilities(profile.RequiredCapabilities)}[/]";

                var pathsDisplay = profile.AllowedPaths.Count > 0
                    ? $"[cyan]{string.Join(", ", profile.AllowedPaths.Take(2))}[/]"
                    : "[grey]None[/]";

                table.AddRow(
                    $"[yellow]{toolName}[/]",
                    capsDisplay,
                    $"[magenta]{profile.MinimumIsolation}[/]",
                    pathsDisplay);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not resolve profile for {ToolName}: {Message}", toolName, ex.Message);
                table.AddRow(
                    $"[yellow]{toolName}[/]",
                    "[red](profile not found)[/]",
                    "-",
                    "-");
            }
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private async Task Step3_ValidEnforcementAsync(CancellationToken cancellationToken)
    {
        ConsoleHelper.DisplayStep(3, 5, "Valid Enforcement");
        AnsiConsole.WriteLine("Checking: Can 'file_system' tool read a file?");
        AnsiConsole.WriteLine();

        var result = await _enforcer.EnforceAsync(
            "file_system",
            ToolCapability.FileRead,
            requestedPaths: new[] { "/app/data/config.json" },
            ct: cancellationToken);

        if (result.IsSuccess)
        {
            ConsoleHelper.DisplaySuccess("✓ Enforcement check passed — file_system is allowed to read files.");
        }
        else
        {
            var errorMsg = string.Join("; ", result.Errors);
            ConsoleHelper.DisplayError($"✗ Enforcement check failed: {errorMsg}");
        }

        AnsiConsole.WriteLine();
    }

    private async Task Step4_InvalidEnforcementAsync(CancellationToken cancellationToken)
    {
        ConsoleHelper.DisplayStep(4, 5, "Invalid Enforcement");
        AnsiConsole.WriteLine("Checking: Can 'file_system' tool make network requests? (read-only tool)");
        AnsiConsole.WriteLine();

        var result = await _enforcer.EnforceAsync(
            "file_system",
            ToolCapability.NetworkAccess,
            requestedHosts: new[] { "api.example.com" },
            ct: cancellationToken);

        if (!result.IsSuccess)
        {
            var errorMsg = string.Join("; ", result.Errors);
            ConsoleHelper.DisplayError($"✗ Enforcement check denied: {errorMsg}");
            AnsiConsole.WriteLine("[yellow]This is expected — a read-only file tool should not have network access.[/]");
        }
        else
        {
            ConsoleHelper.DisplaySuccess("✓ Enforcement check passed (unexpected — file_system should not have NetworkAccess).");
        }

        AnsiConsole.WriteLine();
    }

    private static void Step5_DisplayResolutionProcess()
    {
        ConsoleHelper.DisplayStep(5, 5, "Deny-Overrides-Allow Resolution");
        AnsiConsole.WriteLine("The 5-step capability resolution process:");
        AnsiConsole.WriteLine();

        var steps = new[]
        {
            ("1. Register Tool Type", "ToolPermissionProfileResolver caches the tool's ToolCapabilityAttribute via RegisterToolType()."),
            ("2. Read Compile-Time Attribute", "Extract RequiredCapabilities and MinimumIsolation from the tool class [ToolCapability] attribute."),
            ("3. Read Runtime Configuration", "Check appsettings SandboxConfig for per-tool overrides (DeniedCapabilities, AllowedPaths, etc.)."),
            ("4. Merge with Deny Override", "Apply deny-overrides-allow: if a capability is in both allowed and denied, deny wins. Formula: BaseCapabilities & ~DeniedCapabilities."),
            ("5. Return Merged Profile", "Return ToolPermissionProfile with effective capabilities, allowed/denied paths/hosts, and isolation level.")
        };

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]Step[/]");
        table.AddColumn("[bold]Behavior[/]");

        foreach (var (step, behavior) in steps)
        {
            table.AddRow(
                $"[cyan]{step}[/]",
                behavior);
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[yellow]Key Principle:[/] Deny-overrides-allow means [bold]security is pessimistic[/]. " +
                               "A tool must be explicitly allowed at every level (compile-time + runtime) to gain a capability. " +
                               "A single deny at any level blocks the capability regardless of other allows.");
        AnsiConsole.WriteLine();
    }

    private static string FormatCapabilities(ToolCapability caps)
    {
        if (caps == ToolCapability.None) return "None";

        var names = new List<string>();
        foreach (ToolCapability cap in Enum.GetValues(typeof(ToolCapability)))
        {
            if (cap != ToolCapability.None && (caps & cap) == cap)
                names.Add(cap.ToString());
        }

        return string.Join(" | ", names);
    }
}
