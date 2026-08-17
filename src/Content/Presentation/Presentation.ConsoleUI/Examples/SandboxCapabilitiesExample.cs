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
        table.AddColumn("[bold]Denied Capabilities[/]");
        table.AddColumn("[bold]Effective Capabilities[/]");
        table.AddColumn("[bold]Isolation Level[/]");

        foreach (var toolName in tools)
        {
            try
            {
                var profile = await _enforcer.ResolveProfileAsync(toolName, cancellationToken);

                var capsDisplay = profile.RequiredCapabilities == ToolCapability.None
                    ? "[grey]None[/]"
                    : $"[green]{FormatCapabilities(profile.RequiredCapabilities)}[/]";

                var deniedDisplay = profile.DeniedCapabilities == ToolCapability.None
                    ? "[grey]None[/]"
                    : $"[red]{FormatCapabilities(profile.DeniedCapabilities)}[/]";

                var effectiveDisplay = profile.EffectiveCapabilities == ToolCapability.None
                    ? "[grey]None[/]"
                    : $"[green]{FormatCapabilities(profile.EffectiveCapabilities)}[/]";

                table.AddRow(
                    $"[yellow]{toolName}[/]",
                    capsDisplay,
                    deniedDisplay,
                    effectiveDisplay,
                    $"[magenta]{profile.MinimumIsolation}[/]");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not resolve profile for {ToolName}: {Message}", toolName, ex.Message);
                table.AddRow(
                    $"[yellow]{toolName}[/]",
                    "[red](profile not found)[/]",
                    "-",
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
        AnsiConsole.WriteLine("Checking: does granting only NetworkAccess satisfy 'file_system', which needs FileRead/FileWrite?");
        AnsiConsole.WriteLine();

        var result = await _enforcer.EnforceAsync(
            "file_system",
            ToolCapability.NetworkAccess,
            ct: cancellationToken);

        if (!result.IsSuccess)
        {
            var errorMsg = string.Join("; ", result.Errors);
            ConsoleHelper.DisplayError($"✗ Enforcement check denied: {errorMsg}");
            AnsiConsole.WriteLine("[yellow]This is expected — file_system needs FileRead/FileWrite, which a NetworkAccess-only grant does not cover.[/]");
        }
        else
        {
            ConsoleHelper.DisplaySuccess("✓ Enforcement check passed (unexpected — file_system requires FileRead/FileWrite).");
        }

        AnsiConsole.WriteLine();
    }

    private static void Step5_DisplayResolutionProcess()
    {
        ConsoleHelper.DisplayStep(5, 5, "Capability Resolution");
        AnsiConsole.WriteLine("The capability resolution process:");
        AnsiConsole.WriteLine();

        var steps = new[]
        {
            ("1. Resolve the Tool", "ToolPermissionProfileResolver looks up the tool by name via bounded-key-set keyed DI — the same tool instance the agent would call."),
            ("2. Read Its Declaration", "Read the resolved ITool's own RequiredCapabilities and MinimumIsolation — the tool's single source of truth for what it needs, kept undiminished on the profile."),
            ("3. Read Runtime Configuration", "Check appsettings SandboxConfig for a per-tool DeniedCapabilities/MinimumIsolation override — kept as a separate field, never folded into the declaration."),
            ("4. Enforce the Grant", "CapabilityEnforcer checks RequiredCapabilities against (grantedCapabilities & ~DeniedCapabilities) — a requirement the deny list touches is refused outright, not silently let through."),
            ("5. Provision the Effective Set", "Sandbox launch and attestation read EffectiveCapabilities (RequiredCapabilities & ~DeniedCapabilities) — what should actually be provisioned, distinct from the undiminished requirement.")
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

        AnsiConsole.MarkupLine("[yellow]Key Principle:[/] A per-tool deny genuinely restricts. " +
                               "It narrows what gets granted and what gets provisioned, kept apart from the tool's own declaration " +
                               "of what it requires — the two answer different questions and must never be collapsed into one field.");
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
