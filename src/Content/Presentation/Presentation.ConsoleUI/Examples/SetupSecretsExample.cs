using Domain.Common.Config;
using Domain.Common.Config.AI;
using Microsoft.Extensions.Options;
using Presentation.ConsoleUI.Common.Helpers;
using Spectre.Console;
using System.Diagnostics;

namespace Presentation.ConsoleUI.Examples;

/// <summary>
/// Interactive wizard for configuring .NET User Secrets.
/// Prompts for each secret category and writes values via <c>dotnet user-secrets</c>.
/// </summary>
public class SetupSecretsExample
{
    private const string ProjectPath = "src/Content/Presentation/Presentation.ConsoleUI";
    private readonly IOptionsMonitor<AppConfig> _appConfig;

    public SetupSecretsExample(IOptionsMonitor<AppConfig> appConfig)
    {
        _appConfig = appConfig;
    }

    public async Task RunAsync()
    {
        ConsoleHelper.DisplayHeader("Setup Secrets", Color.Yellow);

        AnsiConsole.MarkupLine("[grey]This wizard configures .NET User Secrets for the Agentic Harness.[/]");
        AnsiConsole.MarkupLine("[grey]Secrets are stored locally and never committed to source control.[/]");
        AnsiConsole.MarkupLine("[grey]Leave any value blank to skip it.[/]");
        AnsiConsole.WriteLine();

        var categories = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title("[bold cornflowerblue]Which categories do you want to configure?[/]")
                .HighlightStyle(Style.Parse("cornflowerblue"))
                .InstructionsText("[grey](Space to toggle, Enter to confirm)[/]")
                .AddChoices(
                    "AI Provider (Required)",
                    "Azure AI Foundry",
                    "GitHub Connector",
                    "Jira Connector",
                    "Azure DevOps Connector",
                    "Slack Connector",
                    "Observability (Jaeger/Azure Monitor)",
                    "Azure Infrastructure (Key Vault/App Insights)"));

        var secrets = new List<(string Key, string Value)>();

        if (categories.Contains("AI Provider (Required)"))
            ConfigureAIProvider(secrets);

        if (categories.Contains("Azure AI Foundry"))
            ConfigureAIFoundry(secrets);

        if (categories.Contains("GitHub Connector"))
            ConfigureGitHub(secrets);

        if (categories.Contains("Jira Connector"))
            ConfigureJira(secrets);

        if (categories.Contains("Azure DevOps Connector"))
            ConfigureAzureDevOps(secrets);

        if (categories.Contains("Slack Connector"))
            ConfigureSlack(secrets);

        if (categories.Contains("Observability (Jaeger/Azure Monitor)"))
            ConfigureObservability(secrets);

        if (categories.Contains("Azure Infrastructure (Key Vault/App Insights)"))
            ConfigureAzureInfra(secrets);

        if (secrets.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No secrets to set.[/]");
            return;
        }

        // Confirm
        AnsiConsole.WriteLine();
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]Secret[/]");
        table.AddColumn("[bold]Value[/]");

        foreach (var (key, value) in secrets)
        {
            var masked = key.Contains("Key", StringComparison.OrdinalIgnoreCase)
                || key.Contains("Token", StringComparison.OrdinalIgnoreCase)
                || key.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                || key.Contains("Password", StringComparison.OrdinalIgnoreCase)
                || key.Contains("ConnectionString", StringComparison.OrdinalIgnoreCase)
                ? MaskValue(value)
                : value;
            table.AddRow(Markup.Escape(key), Markup.Escape(masked));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        if (!AnsiConsole.Confirm("[bold]Apply these secrets?[/]"))
        {
            AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
            return;
        }

        // Write secrets
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("[cornflowerblue]Writing secrets...[/]", async _ =>
            {
                foreach (var (key, value) in secrets)
                {
                    await SetSecretAsync(key, value);
                }
            });

        AnsiConsole.WriteLine();
        ConsoleHelper.DisplaySuccess($"{secrets.Count} secret(s) configured successfully.");
        AnsiConsole.MarkupLine("[grey]View with: dotnet user-secrets list --project " + ProjectPath + "[/]");
    }

    private static void ConfigureAIProvider(List<(string Key, string Value)> secrets)
    {
        AnsiConsole.Write(new Rule("[bold cornflowerblue]AI Provider[/]").RuleStyle("grey"));

        var provider = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Which AI provider?")
                .AddChoices("Azure OpenAI", "OpenAI"));

        if (provider == "OpenAI")
        {
            secrets.Add(("AppConfig:AI:AgentFramework:ClientType", "OpenAI"));
            PromptSecret(secrets, "AppConfig:AI:AgentFramework:ApiKey", "API Key (sk-...)");
            PromptOptional(secrets, "AppConfig:AI:AgentFramework:DefaultDeployment", "Model name", "gpt-4o");
        }
        else
        {
            PromptOptional(secrets, "AppConfig:AI:AgentFramework:Endpoint", "Azure OpenAI Endpoint (https://your-resource.openai.azure.com/)");
            PromptSecret(secrets, "AppConfig:AI:AgentFramework:ApiKey", "API Key");
            PromptOptional(secrets, "AppConfig:AI:AgentFramework:DefaultDeployment", "Deployment name", "gpt-4o");
        }
    }

    private static void ConfigureAIFoundry(List<(string Key, string Value)> secrets)
    {
        AnsiConsole.Write(new Rule("[bold cornflowerblue]Azure AI Foundry[/]").RuleStyle("grey"));
        PromptOptional(secrets, "AppConfig:AI:AIFoundry:ProjectEndpoint", "Project Endpoint URL");
    }

    private static void ConfigureGitHub(List<(string Key, string Value)> secrets)
    {
        AnsiConsole.Write(new Rule("[bold cornflowerblue]GitHub[/]").RuleStyle("grey"));
        PromptSecret(secrets, "AppConfig:Connectors:GitHub:AccessToken", "Personal Access Token");
        PromptOptional(secrets, "AppConfig:Connectors:GitHub:DefaultOwner", "Default owner/org");
    }

    private static void ConfigureJira(List<(string Key, string Value)> secrets)
    {
        AnsiConsole.Write(new Rule("[bold cornflowerblue]Jira[/]").RuleStyle("grey"));
        PromptOptional(secrets, "AppConfig:Connectors:Jira:BaseUrl", "Instance URL (https://yoursite.atlassian.net)");
        PromptOptional(secrets, "AppConfig:Connectors:Jira:Email", "Email");
        PromptSecret(secrets, "AppConfig:Connectors:Jira:ApiToken", "API Token");
        PromptOptional(secrets, "AppConfig:Connectors:Jira:DefaultProject", "Default project key");
        PromptOptional(secrets, "AppConfig:Connectors:Jira:DefaultProject", "Default project key");
    }

    private static void ConfigureAzureDevOps(List<(string Key, string Value)> secrets)
    {
        AnsiConsole.Write(new Rule("[bold cornflowerblue]Azure DevOps[/]").RuleStyle("grey"));
        PromptOptional(secrets, "AppConfig:Connectors:AzureDevOps:OrganizationUrl", "Organization URL");
        PromptSecret(secrets, "AppConfig:Connectors:AzureDevOps:PersonalAccessToken", "Personal Access Token");
        PromptOptional(secrets, "AppConfig:Connectors:AzureDevOps:DefaultProject", "Default project");
    }

    private static void ConfigureSlack(List<(string Key, string Value)> secrets)
    {
        AnsiConsole.Write(new Rule("[bold cornflowerblue]Slack[/]").RuleStyle("grey"));
        PromptSecret(secrets, "AppConfig:Connectors:Slack:BotToken", "Bot Token (xoxb-...)");
        PromptOptional(secrets, "AppConfig:Connectors:Slack:DefaultChannel", "Default channel");
        PromptSecret(secrets, "AppConfig:Connectors:Slack:WebhookUrl", "Webhook URL (optional)");
    }

    private static void ConfigureObservability(List<(string Key, string Value)> secrets)
    {
        AnsiConsole.Write(new Rule("[bold cornflowerblue]Observability[/]").RuleStyle("grey"));
        PromptOptional(secrets, "AppConfig:Observability:Exporters:Otlp:Endpoint", "OTLP Endpoint", "http://localhost:4317");
        PromptSecret(secrets, "AppConfig:Observability:Exporters:AzureMonitor:ConnectionString", "Azure Monitor Connection String (InstrumentationKey=...)");
    }

    private static void ConfigureAzureInfra(List<(string Key, string Value)> secrets)
    {
        AnsiConsole.Write(new Rule("[bold cornflowerblue]Azure Infrastructure[/]").RuleStyle("grey"));
        PromptOptional(secrets, "AppConfig:Azure:KeyVault:VaultUri", "Key Vault URI (https://your-vault.vault.azure.net/)");
        PromptSecret(secrets, "AppConfig:Azure:ApplicationInsights:ConnectionString", "App Insights Connection String (InstrumentationKey=...)");
    }

    private static void PromptSecret(List<(string Key, string Value)> secrets, string key, string label)
    {
        var value = AnsiConsole.Prompt(
            new TextPrompt<string>($"  [grey]{label}:[/]")
                .AllowEmpty()
                .Secret());

        if (!string.IsNullOrWhiteSpace(value))
            secrets.Add((key, value));
    }

    private static void PromptOptional(List<(string Key, string Value)> secrets, string key, string label, string? defaultHint = null)
    {
        var prompt = defaultHint != null
            ? $"  [grey]{label} [{defaultHint}]:[/]"
            : $"  [grey]{label}:[/]";

        var value = AnsiConsole.Prompt(
            new TextPrompt<string>(prompt)
                .AllowEmpty());

        if (!string.IsNullOrWhiteSpace(value))
            secrets.Add((key, value));
    }

    private static string MaskValue(string value)
    {
        if (value.Length <= 8) return new string('*', value.Length);
        return value[..4] + new string('*', value.Length - 8) + value[^4..];
    }

    private static async Task SetSecretAsync(string key, string value)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("user-secrets");
        psi.ArgumentList.Add("set");
        psi.ArgumentList.Add(key);
        psi.ArgumentList.Add(value);
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(ProjectPath);

        using var process = Process.Start(psi);
        if (process != null)
            await process.WaitForExitAsync();
    }
}
