@echo off
setlocal enabledelayedexpansion

echo.
echo  ========================================
echo   Agentic Harness - User Secrets Setup
echo  ========================================
echo.
echo  This script configures .NET User Secrets for the Agentic Harness.
echo  Secrets are stored locally and never committed to source control.
echo.
echo  Press Ctrl+C at any time to cancel. Leave a value blank to skip it.
echo.

set "PROJECT=src\Content\Presentation\Presentation.ConsoleUI"

:: Verify dotnet CLI is available
where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo  [ERROR] dotnet CLI not found. Install the .NET 10 SDK first.
    exit /b 1
)

:: Verify project exists
if not exist "%PROJECT%\Presentation.ConsoleUI.csproj" (
    echo  [ERROR] Project not found at %PROJECT%. Run this script from the repository root.
    exit /b 1
)

echo  ----------------------------------------
echo   REQUIRED: AI Provider
echo  ----------------------------------------
echo.
echo  Choose your AI provider:
echo    1. Azure OpenAI (default)
echo    2. OpenAI
echo.
set /p "PROVIDER_CHOICE=  Selection [1]: "
if "%PROVIDER_CHOICE%"=="" set "PROVIDER_CHOICE=1"

if "%PROVIDER_CHOICE%"=="2" (
    dotnet user-secrets set "AppConfig:AI:AgentFramework:ClientType" "OpenAI" --project %PROJECT% >nul
    echo  [SET] ClientType = OpenAI

    set /p "OPENAI_KEY=  OpenAI API Key (sk-...): "
    if not "!OPENAI_KEY!"=="" (
        dotnet user-secrets set "AppConfig:AI:AgentFramework:ApiKey" "!OPENAI_KEY!" --project %PROJECT% >nul
        echo  [SET] ApiKey
    )

    set /p "OPENAI_MODEL=  Model name [gpt-4o]: "
    if not "!OPENAI_MODEL!"=="" (
        dotnet user-secrets set "AppConfig:AI:AgentFramework:DefaultDeployment" "!OPENAI_MODEL!" --project %PROJECT% >nul
        echo  [SET] DefaultDeployment = !OPENAI_MODEL!
    )
) else (
    set /p "AOAI_ENDPOINT=  Azure OpenAI Endpoint (https://your-resource.openai.azure.com/): "
    if not "!AOAI_ENDPOINT!"=="" (
        dotnet user-secrets set "AppConfig:AI:AgentFramework:Endpoint" "!AOAI_ENDPOINT!" --project %PROJECT% >nul
        echo  [SET] Endpoint
    )

    set /p "AOAI_KEY=  Azure OpenAI API Key: "
    if not "!AOAI_KEY!"=="" (
        dotnet user-secrets set "AppConfig:AI:AgentFramework:ApiKey" "!AOAI_KEY!" --project %PROJECT% >nul
        echo  [SET] ApiKey
    )

    set /p "AOAI_DEPLOYMENT=  Deployment name [gpt-4o]: "
    if not "!AOAI_DEPLOYMENT!"=="" (
        dotnet user-secrets set "AppConfig:AI:AgentFramework:DefaultDeployment" "!AOAI_DEPLOYMENT!" --project %PROJECT% >nul
        echo  [SET] DefaultDeployment = !AOAI_DEPLOYMENT!
    )
)

echo.
echo  ----------------------------------------
echo   OPTIONAL: Azure AI Foundry
echo  ----------------------------------------
echo.
set /p "FOUNDRY_ENDPOINT=  AI Foundry Project Endpoint (or blank to skip): "
if not "%FOUNDRY_ENDPOINT%"=="" (
    dotnet user-secrets set "AppConfig:AI:AIFoundry:ProjectEndpoint" "%FOUNDRY_ENDPOINT%" --project %PROJECT% >nul
    echo  [SET] AIFoundry:ProjectEndpoint
)

echo.
echo  ----------------------------------------
echo   OPTIONAL: Connectors
echo  ----------------------------------------
echo.

echo  -- GitHub --
set /p "GH_TOKEN=  GitHub Access Token (or blank to skip): "
if not "%GH_TOKEN%"=="" (
    dotnet user-secrets set "AppConfig:Connectors:GitHub:AccessToken" "%GH_TOKEN%" --project %PROJECT% >nul
    echo  [SET] GitHub:AccessToken
    set /p "GH_OWNER=  Default GitHub owner/org: "
    if not "!GH_OWNER!"=="" (
        dotnet user-secrets set "AppConfig:Connectors:GitHub:DefaultOwner" "!GH_OWNER!" --project %PROJECT% >nul
        echo  [SET] GitHub:DefaultOwner = !GH_OWNER!
    )
)

echo.
echo  -- Jira --
set /p "JIRA_URL=  Jira Base URL (or blank to skip): "
if not "%JIRA_URL%"=="" (
    dotnet user-secrets set "AppConfig:Connectors:Jira:BaseUrl" "%JIRA_URL%" --project %PROJECT% >nul
    echo  [SET] Jira:BaseUrl
    set /p "JIRA_EMAIL=  Jira Email: "
    if not "!JIRA_EMAIL!"=="" (
        dotnet user-secrets set "AppConfig:Connectors:Jira:Email" "!JIRA_EMAIL!" --project %PROJECT% >nul
        echo  [SET] Jira:Email
    )
    set /p "JIRA_TOKEN=  Jira API Token: "
    if not "!JIRA_TOKEN!"=="" (
        dotnet user-secrets set "AppConfig:Connectors:Jira:ApiToken" "!JIRA_TOKEN!" --project %PROJECT% >nul
        echo  [SET] Jira:ApiToken
    )
    set /p "JIRA_PROJECT=  Jira Default Project Key: "
    if not "!JIRA_PROJECT!"=="" (
        dotnet user-secrets set "AppConfig:Connectors:Jira:DefaultProject" "!JIRA_PROJECT!" --project %PROJECT% >nul
        echo  [SET] Jira:DefaultProject = !JIRA_PROJECT!
    )
)

echo.
echo  -- Azure DevOps --
set /p "ADO_ORG=  Azure DevOps Organization URL (or blank to skip): "
if not "%ADO_ORG%"=="" (
    dotnet user-secrets set "AppConfig:Connectors:AzureDevOps:OrganizationUrl" "%ADO_ORG%" --project %PROJECT% >nul
    echo  [SET] AzureDevOps:OrganizationUrl
    set /p "ADO_PAT=  Personal Access Token: "
    if not "!ADO_PAT!"=="" (
        dotnet user-secrets set "AppConfig:Connectors:AzureDevOps:PersonalAccessToken" "!ADO_PAT!" --project %PROJECT% >nul
        echo  [SET] AzureDevOps:PersonalAccessToken
    )
    set /p "ADO_PROJECT=  Default Project: "
    if not "!ADO_PROJECT!"=="" (
        dotnet user-secrets set "AppConfig:Connectors:AzureDevOps:DefaultProject" "!ADO_PROJECT!" --project %PROJECT% >nul
        echo  [SET] AzureDevOps:DefaultProject = !ADO_PROJECT!
    )
)

echo.
echo  -- Slack --
set /p "SLACK_TOKEN=  Slack Bot Token (or blank to skip): "
if not "%SLACK_TOKEN%"=="" (
    dotnet user-secrets set "AppConfig:Connectors:Slack:BotToken" "%SLACK_TOKEN%" --project %PROJECT% >nul
    echo  [SET] Slack:BotToken
    set /p "SLACK_CHANNEL=  Default Channel: "
    if not "!SLACK_CHANNEL!"=="" (
        dotnet user-secrets set "AppConfig:Connectors:Slack:DefaultChannel" "!SLACK_CHANNEL!" --project %PROJECT% >nul
        echo  [SET] Slack:DefaultChannel = !SLACK_CHANNEL!
    )
    set /p "SLACK_WEBHOOK=  Webhook URL (optional): "
    if not "!SLACK_WEBHOOK!"=="" (
        dotnet user-secrets set "AppConfig:Connectors:Slack:WebhookUrl" "!SLACK_WEBHOOK!" --project %PROJECT% >nul
        echo  [SET] Slack:WebhookUrl
    )
)

echo.
echo  ----------------------------------------
echo   OPTIONAL: Observability
echo  ----------------------------------------
echo.
set /p "OTLP_ENDPOINT=  OTLP Endpoint for Jaeger/Tempo [http://localhost:4317]: "
if not "%OTLP_ENDPOINT%"=="" (
    dotnet user-secrets set "AppConfig:Observability:Exporters:Otlp:Endpoint" "%OTLP_ENDPOINT%" --project %PROJECT% >nul
    echo  [SET] Otlp:Endpoint
)

set /p "AZMON_CONN=  Azure Monitor Connection String (or blank to skip): "
if not "%AZMON_CONN%"=="" (
    dotnet user-secrets set "AppConfig:Observability:Exporters:AzureMonitor:ConnectionString" "%AZMON_CONN%" --project %PROJECT% >nul
    echo  [SET] AzureMonitor:ConnectionString
)

echo.
echo  ----------------------------------------
echo   OPTIONAL: Azure Infrastructure
echo  ----------------------------------------
echo.
set /p "KV_URI=  Azure Key Vault URI (or blank to skip): "
if not "%KV_URI%"=="" (
    dotnet user-secrets set "AppConfig:Azure:KeyVault:VaultUri" "%KV_URI%" --project %PROJECT% >nul
    echo  [SET] KeyVault:VaultUri
)

set /p "AI_CONN=  Application Insights Connection String (or blank to skip): "
if not "%AI_CONN%"=="" (
    dotnet user-secrets set "AppConfig:Azure:ApplicationInsights:ConnectionString" "%AI_CONN%" --project %PROJECT% >nul
    echo  [SET] ApplicationInsights:ConnectionString
)

echo.
echo  ========================================
echo   Setup Complete
echo  ========================================
echo.
echo  Your secrets are stored at:
echo    %%APPDATA%%\Microsoft\UserSecrets\agentic-harness-console-ui\secrets.json
echo.
echo  To view:   dotnet user-secrets list --project %PROJECT%
echo  To clear:  dotnet user-secrets clear --project %PROJECT%
echo.
echo  Run the harness:
echo    dotnet run --project %PROJECT%
echo.
endlocal
