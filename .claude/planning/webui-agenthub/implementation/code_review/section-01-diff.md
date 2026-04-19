diff --git a/src/AgenticHarness.slnx b/src/AgenticHarness.slnx
index 8a8a0d5..40eb04e 100644
--- a/src/AgenticHarness.slnx
+++ b/src/AgenticHarness.slnx
@@ -40,9 +40,11 @@
     <Project Path="Content/Presentation/Presentation.Common/Presentation.Common.csproj" />
     <Project Path="Content/Presentation/Presentation.LoggerUI/Presentation.LoggerUI.csproj" />
     <Project Path="Content/Presentation/Presentation.ConsoleUI/Presentation.ConsoleUI.csproj" />
+    <Project Path="Content/Presentation/Presentation.AgentHub/Presentation.AgentHub.csproj" />
   </Folder>
   <Folder Name="/Tests/">
     <Project Path="Content/Tests/Domain.AI.Tests/Domain.AI.Tests.csproj" />
+    <Project Path="Content/Tests/Presentation.AgentHub.Tests/Presentation.AgentHub.Tests.csproj" />
     <Project Path="Content/Tests/Application.AI.Common.Tests/Application.AI.Common.Tests.csproj" />
     <Project Path="Content/Tests/Application.Common.Tests/Application.Common.Tests.csproj" />
     <Project Path="Content/Tests/Application.Core.Tests/Application.Core.Tests.csproj" />
diff --git a/src/Content/Presentation/Presentation.AgentHub/Presentation.AgentHub.csproj b/src/Content/Presentation/Presentation.AgentHub/Presentation.AgentHub.csproj
new file mode 100644
index 0000000..a8f91d4
--- /dev/null
+++ b/src/Content/Presentation/Presentation.AgentHub/Presentation.AgentHub.csproj
@@ -0,0 +1,20 @@
+<Project Sdk="Microsoft.NET.Sdk.Web">
+
+  <PropertyGroup>
+    <TargetFramework>net10.0</TargetFramework>
+    <ImplicitUsings>enable</ImplicitUsings>
+    <Nullable>enable</Nullable>
+  </PropertyGroup>
+
+  <ItemGroup>
+    <ProjectReference Include="..\Presentation.Common\Presentation.Common.csproj" />
+  </ItemGroup>
+
+  <ItemGroup>
+    <!-- OpenTelemetry — versions already in Directory.Packages.props -->
+    <PackageReference Include="OpenTelemetry" />
+    <!-- Microsoft.Identity.Web — already in Directory.Packages.props -->
+    <PackageReference Include="Microsoft.Identity.Web" />
+  </ItemGroup>
+
+</Project>
diff --git a/src/Content/Presentation/Presentation.AgentHub/Program.cs b/src/Content/Presentation/Presentation.AgentHub/Program.cs
new file mode 100644
index 0000000..0becd19
--- /dev/null
+++ b/src/Content/Presentation/Presentation.AgentHub/Program.cs
@@ -0,0 +1,3 @@
+var builder = WebApplication.CreateBuilder(args);
+var app = builder.Build();
+app.Run();
diff --git a/src/Content/Presentation/Presentation.WebUI/components.json b/src/Content/Presentation/Presentation.WebUI/components.json
new file mode 100644
index 0000000..15addee
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/components.json
@@ -0,0 +1,25 @@
+{
+  "$schema": "https://ui.shadcn.com/schema.json",
+  "style": "base-nova",
+  "rsc": false,
+  "tsx": true,
+  "tailwind": {
+    "config": "",
+    "css": "src/index.css",
+    "baseColor": "neutral",
+    "cssVariables": true,
+    "prefix": ""
+  },
+  "iconLibrary": "lucide",
+  "rtl": false,
+  "aliases": {
+    "components": "@/components",
+    "utils": "@/lib/utils",
+    "ui": "@/components/ui",
+    "lib": "@/lib",
+    "hooks": "@/hooks"
+  },
+  "menuColor": "default",
+  "menuAccent": "subtle",
+  "registries": {}
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/package.json b/src/Content/Presentation/Presentation.WebUI/package.json
new file mode 100644
index 0000000..08b1774
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/package.json
@@ -0,0 +1,62 @@
+{
+  "name": "presentation-webui",
+  "private": true,
+  "version": "0.0.0",
+  "type": "module",
+  "scripts": {
+    "dev": "vite",
+    "build": "tsc -b && vite build",
+    "lint": "eslint .",
+    "test": "vitest run",
+    "test:watch": "vitest",
+    "test:coverage": "vitest run --coverage",
+    "preview": "vite preview"
+  },
+  "dependencies": {
+    "@azure/msal-browser": "^5.6.3",
+    "@azure/msal-react": "^5.2.1",
+    "@base-ui/react": "^1.4.0",
+    "@fontsource-variable/geist": "^5.2.8",
+    "@hookform/resolvers": "^5.2.2",
+    "@microsoft/signalr": "^10.0.0",
+    "@tailwindcss/vite": "^4.2.2",
+    "@tanstack/react-query": "^5.99.0",
+    "axios": "^1.15.0",
+    "class-variance-authority": "^0.7.1",
+    "clsx": "^2.1.1",
+    "concurrently": "^9.2.1",
+    "lucide-react": "^1.8.0",
+    "react": "^19.2.4",
+    "react-dom": "^19.2.4",
+    "react-hook-form": "^7.72.1",
+    "react-window": "^2.2.7",
+    "shadcn": "^4.2.0",
+    "tailwind-merge": "^3.5.0",
+    "tailwindcss": "^4.2.2",
+    "tw-animate-css": "^1.4.0",
+    "zod": "^4.3.6",
+    "zustand": "^5.0.12"
+  },
+  "devDependencies": {
+    "@eslint/js": "^9.39.4",
+    "@testing-library/jest-dom": "^6.9.1",
+    "@testing-library/react": "^16.3.2",
+    "@testing-library/user-event": "^14.6.1",
+    "@types/node": "^24.12.2",
+    "@types/react": "^19.2.14",
+    "@types/react-dom": "^19.2.3",
+    "@types/react-window": "^1.8.8",
+    "@vitejs/plugin-react": "^6.0.1",
+    "@vitest/coverage-v8": "^4.1.4",
+    "eslint": "^9.39.4",
+    "eslint-plugin-react-hooks": "^7.0.1",
+    "eslint-plugin-react-refresh": "^0.5.2",
+    "globals": "^17.4.0",
+    "jsdom": "^29.0.2",
+    "msw": "^2.13.3",
+    "typescript": "~6.0.2",
+    "typescript-eslint": "^8.58.0",
+    "vite": "^8.0.4",
+    "vitest": "^4.1.4"
+  }
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/test/scaffold.test.ts b/src/Content/Presentation/Presentation.WebUI/src/test/scaffold.test.ts
new file mode 100644
index 0000000..2d2d6fc
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/test/scaffold.test.ts
@@ -0,0 +1,5 @@
+describe('scaffold', () => {
+  it('test infrastructure is wired', () => {
+    expect(true).toBe(true)
+  })
+})
diff --git a/src/Content/Presentation/Presentation.WebUI/src/test/setup.ts b/src/Content/Presentation/Presentation.WebUI/src/test/setup.ts
new file mode 100644
index 0000000..4b22e31
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/test/setup.ts
@@ -0,0 +1 @@
+// MSW server and jest-dom setup added in section 12
diff --git a/src/Content/Presentation/Presentation.WebUI/tsconfig.app.json b/src/Content/Presentation/Presentation.WebUI/tsconfig.app.json
new file mode 100644
index 0000000..855405f
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/tsconfig.app.json
@@ -0,0 +1,31 @@
+{
+  "compilerOptions": {
+    "tsBuildInfoFile": "./node_modules/.tmp/tsconfig.app.tsbuildinfo",
+    "target": "es2023",
+    "lib": ["ES2023", "DOM", "DOM.Iterable"],
+    "module": "esnext",
+    "types": ["vite/client", "vitest/globals"],
+    "skipLibCheck": true,
+
+    /* Bundler mode */
+    "moduleResolution": "bundler",
+    "allowImportingTsExtensions": true,
+    "verbatimModuleSyntax": true,
+    "moduleDetection": "force",
+    "noEmit": true,
+    "jsx": "react-jsx",
+
+    /* Linting */
+    "strict": true,
+    "noUncheckedIndexedAccess": true,
+    "noImplicitReturns": true,
+    "noUnusedLocals": true,
+    "noUnusedParameters": true,
+    "erasableSyntaxOnly": true,
+    "noFallthroughCasesInSwitch": true,
+    "paths": {
+      "@/*": ["./src/*"]
+    }
+  },
+  "include": ["src"]
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/tsconfig.json b/src/Content/Presentation/Presentation.WebUI/tsconfig.json
new file mode 100644
index 0000000..fec8c8e
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/tsconfig.json
@@ -0,0 +1,13 @@
+{
+  "files": [],
+  "references": [
+    { "path": "./tsconfig.app.json" },
+    { "path": "./tsconfig.node.json" }
+  ],
+  "compilerOptions": {
+    "baseUrl": ".",
+    "paths": {
+      "@/*": ["./src/*"]
+    }
+  }
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/vite.config.ts b/src/Content/Presentation/Presentation.WebUI/vite.config.ts
new file mode 100644
index 0000000..7e36ac0
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/vite.config.ts
@@ -0,0 +1,11 @@
+import { defineConfig } from 'vite'
+import react from '@vitejs/plugin-react'
+import tailwindcss from '@tailwindcss/vite'
+import path from 'path'
+
+export default defineConfig({
+  plugins: [react(), tailwindcss()],
+  resolve: {
+    alias: { '@': path.resolve(__dirname, './src') },
+  },
+})
diff --git a/src/Content/Presentation/Presentation.WebUI/vitest.config.ts b/src/Content/Presentation/Presentation.WebUI/vitest.config.ts
new file mode 100644
index 0000000..be47d98
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/vitest.config.ts
@@ -0,0 +1,19 @@
+import { defineConfig } from 'vitest/config'
+import react from '@vitejs/plugin-react'
+import path from 'path'
+
+export default defineConfig({
+  plugins: [react()],
+  test: {
+    environment: 'jsdom',
+    globals: true,
+    setupFiles: ['./src/test/setup.ts'],
+    coverage: {
+      provider: 'v8',
+      thresholds: { lines: 80, functions: 80, branches: 80, statements: 80 },
+    },
+  },
+  resolve: {
+    alias: { '@': path.resolve(__dirname, './src') },
+  },
+})
diff --git a/src/Content/Tests/Presentation.AgentHub.Tests/Presentation.AgentHub.Tests.csproj b/src/Content/Tests/Presentation.AgentHub.Tests/Presentation.AgentHub.Tests.csproj
new file mode 100644
index 0000000..cc90f36
--- /dev/null
+++ b/src/Content/Tests/Presentation.AgentHub.Tests/Presentation.AgentHub.Tests.csproj
@@ -0,0 +1,24 @@
+<Project Sdk="Microsoft.NET.Sdk">
+
+  <PropertyGroup>
+    <TargetFramework>net10.0</TargetFramework>
+    <ImplicitUsings>enable</ImplicitUsings>
+    <Nullable>enable</Nullable>
+    <IsPackable>false</IsPackable>
+  </PropertyGroup>
+
+  <ItemGroup>
+    <PackageReference Include="coverlet.collector" />
+    <PackageReference Include="FluentAssertions" />
+    <PackageReference Include="Microsoft.NET.Test.Sdk" />
+    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
+    <PackageReference Include="Moq" />
+    <PackageReference Include="xunit" />
+    <PackageReference Include="xunit.runner.visualstudio" />
+  </ItemGroup>
+
+  <ItemGroup>
+    <ProjectReference Include="..\..\Presentation\Presentation.AgentHub\Presentation.AgentHub.csproj" />
+  </ItemGroup>
+
+</Project>
diff --git a/src/Content/Tests/Presentation.AgentHub.Tests/ScaffoldTests.cs b/src/Content/Tests/Presentation.AgentHub.Tests/ScaffoldTests.cs
new file mode 100644
index 0000000..4895b5e
--- /dev/null
+++ b/src/Content/Tests/Presentation.AgentHub.Tests/ScaffoldTests.cs
@@ -0,0 +1,35 @@
+using Application.Common;
+using MediatR;
+using Microsoft.Extensions.DependencyInjection;
+using Xunit;
+
+namespace Presentation.AgentHub.Tests;
+
+/// <summary>
+/// Smoke tests that validate the project scaffold compiles and basic DI resolves.
+/// These run as part of dotnet test — a failing build means these fail implicitly.
+/// </summary>
+public class ScaffoldTests
+{
+    [Fact]
+    public void AgentHub_ProjectBuilds_WithoutErrors()
+    {
+        // This test passes if the assembly loads. Build failure prevents discovery.
+        Assert.True(true);
+    }
+
+    [Fact]
+    public void Presentation_Common_GetServices_Registers_IMediator()
+    {
+        // Arrange: build a minimal container using Application.Common dependencies
+        var services = new ServiceCollection();
+        services.AddApplicationCommonDependencies();
+        var provider = services.BuildServiceProvider();
+
+        // Act: resolve IMediator
+        var mediator = provider.GetService<IMediator>();
+
+        // Assert: MediatR is registered and resolves
+        Assert.NotNull(mediator);
+    }
+}
diff --git a/src/Directory.Packages.props b/src/Directory.Packages.props
index 9b2c56d..c5eedbd 100644
--- a/src/Directory.Packages.props
+++ b/src/Directory.Packages.props
@@ -88,6 +88,7 @@
     <!-- Testing -->
     <PackageVersion Include="coverlet.collector" Version="6.0.4" />
     <PackageVersion Include="FluentAssertions" Version="8.3.0" />
+    <PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.5" />
     <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.13.0" />
     <PackageVersion Include="Moq" Version="4.20.72" />
     <PackageVersion Include="xunit" Version="2.9.3" />
