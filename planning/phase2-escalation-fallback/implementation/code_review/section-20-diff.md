diff --git a/src/Content/Presentation/Presentation.AgentHub/appsettings.json b/src/Content/Presentation/Presentation.AgentHub/appsettings.json
index f329b87..ba8b42e 100644
--- a/src/Content/Presentation/Presentation.AgentHub/appsettings.json
+++ b/src/Content/Presentation/Presentation.AgentHub/appsettings.json
@@ -25,7 +25,74 @@
         "EnableMcpSecurity": true,
         "EnableAudit": true,
         "EnableMetrics": true,
-        "InjectionBlockThreshold": "High"
+        "InjectionBlockThreshold": "High",
+        "Escalation": {
+          "Enabled": true,
+          "DefaultTimeoutSeconds": 300,
+          "DefaultTimeoutAction": "DenyAndEscalate",
+          "DefaultApprovalStrategy": "AnyOf",
+          "AuditStoragePath": ".agent-sessions/escalations",
+          "PriorityLevels": {
+            "Informational": {
+              "TimeoutSeconds": 0,
+              "Async": true,
+              "EscalateToAll": false
+            },
+            "Blocking": {
+              "TimeoutSeconds": 300,
+              "Async": false,
+              "EscalateToAll": false
+            },
+            "Critical": {
+              "TimeoutSeconds": 600,
+              "Async": false,
+              "EscalateToAll": true
+            }
+          }
+        }
+      },
+      "Resilience": {
+        "Enabled": false,
+        "FallbackChain": [
+          {
+            "ClientType": "AzureOpenAI",
+            "DeploymentId": "gpt-4o",
+            "Capabilities": {
+              "SupportsToolCalling": true,
+              "SupportsStreaming": true,
+              "SupportsVision": true,
+              "MaxTokens": 128000
+            }
+          },
+          {
+            "ClientType": "AzureAIInference",
+            "DeploymentId": "claude-sonnet",
+            "Capabilities": {
+              "SupportsToolCalling": true,
+              "SupportsStreaming": true,
+              "SupportsVision": false,
+              "MaxTokens": 200000
+            }
+          }
+        ],
+        "CircuitBreaker": {
+          "FailureRatio": 0.5,
+          "SamplingDurationSeconds": 30,
+          "MinimumThroughput": 5,
+          "BreakDurationSeconds": 60
+        },
+        "Retry": {
+          "MaxAttempts": 2,
+          "BaseDelaySeconds": 1.0,
+          "BackoffType": "Exponential"
+        },
+        "Timeout": {
+          "PerAttemptSeconds": 30
+        },
+        "DegradedMode": {
+          "RetryQueueTtlSeconds": 300,
+          "MaxQueueSize": 100
+        }
       },
       "Permissions": {
         "DefaultBehavior": "Ask",
diff --git a/src/Content/Presentation/Presentation.ConsoleUI/appsettings.json b/src/Content/Presentation/Presentation.ConsoleUI/appsettings.json
index b2bfdb0..87d230b 100644
--- a/src/Content/Presentation/Presentation.ConsoleUI/appsettings.json
+++ b/src/Content/Presentation/Presentation.ConsoleUI/appsettings.json
@@ -38,7 +38,74 @@
         "EnableMcpSecurity": true,
         "EnableAudit": true,
         "EnableMetrics": true,
-        "InjectionBlockThreshold": "High"
+        "InjectionBlockThreshold": "High",
+        "Escalation": {
+          "Enabled": true,
+          "DefaultTimeoutSeconds": 300,
+          "DefaultTimeoutAction": "DenyAndEscalate",
+          "DefaultApprovalStrategy": "AnyOf",
+          "AuditStoragePath": ".agent-sessions/escalations",
+          "PriorityLevels": {
+            "Informational": {
+              "TimeoutSeconds": 0,
+              "Async": true,
+              "EscalateToAll": false
+            },
+            "Blocking": {
+              "TimeoutSeconds": 300,
+              "Async": false,
+              "EscalateToAll": false
+            },
+            "Critical": {
+              "TimeoutSeconds": 600,
+              "Async": false,
+              "EscalateToAll": true
+            }
+          }
+        }
+      },
+      "Resilience": {
+        "Enabled": false,
+        "FallbackChain": [
+          {
+            "ClientType": "AzureOpenAI",
+            "DeploymentId": "gpt-4o",
+            "Capabilities": {
+              "SupportsToolCalling": true,
+              "SupportsStreaming": true,
+              "SupportsVision": true,
+              "MaxTokens": 128000
+            }
+          },
+          {
+            "ClientType": "AzureAIInference",
+            "DeploymentId": "claude-sonnet",
+            "Capabilities": {
+              "SupportsToolCalling": true,
+              "SupportsStreaming": true,
+              "SupportsVision": false,
+              "MaxTokens": 200000
+            }
+          }
+        ],
+        "CircuitBreaker": {
+          "FailureRatio": 0.5,
+          "SamplingDurationSeconds": 30,
+          "MinimumThroughput": 5,
+          "BreakDurationSeconds": 60
+        },
+        "Retry": {
+          "MaxAttempts": 2,
+          "BaseDelaySeconds": 1.0,
+          "BackoffType": "Exponential"
+        },
+        "Timeout": {
+          "PerAttemptSeconds": 30
+        },
+        "DegradedMode": {
+          "RetryQueueTtlSeconds": 300,
+          "MaxQueueSize": 100
+        }
       },
       "Permissions": {
         "DefaultBehavior": "Ask",
diff --git a/src/Content/Tests/Presentation.Common.Tests/Extensions/IServiceCollectionExtensionsTests.cs b/src/Content/Tests/Presentation.Common.Tests/Extensions/IServiceCollectionExtensionsTests.cs
index aacafa6..1e72b78 100644
--- a/src/Content/Tests/Presentation.Common.Tests/Extensions/IServiceCollectionExtensionsTests.cs
+++ b/src/Content/Tests/Presentation.Common.Tests/Extensions/IServiceCollectionExtensionsTests.cs
@@ -1,4 +1,5 @@
 using Domain.Common.Config;
+using Domain.Common.Config.AI;
 using Domain.Common.Config.AI.Governance;
 using Domain.Common.Config.AI.Resilience;
 using Domain.Common.Config.Cache;
@@ -190,4 +191,91 @@ public sealed class IServiceCollectionExtensionsTests
         var resConfig = provider.GetRequiredService<IOptionsMonitor<ResilienceConfig>>().CurrentValue;
         resConfig.Enabled.Should().BeTrue();
     }
+
+    [Fact]
+    public void EscalationConfig_BindsFullStructure_FromAppsettings()
+    {
+        var config = new ConfigurationBuilder()
+            .AddInMemoryCollection(new Dictionary<string, string?>
+            {
+                ["AppConfig:AI:Governance:Escalation:Enabled"] = "true",
+                ["AppConfig:AI:Governance:Escalation:DefaultTimeoutSeconds"] = "300",
+                ["AppConfig:AI:Governance:Escalation:DefaultTimeoutAction"] = "DenyAndEscalate",
+                ["AppConfig:AI:Governance:Escalation:DefaultApprovalStrategy"] = "AnyOf",
+                ["AppConfig:AI:Governance:Escalation:PriorityLevels:Informational:TimeoutSeconds"] = "0",
+                ["AppConfig:AI:Governance:Escalation:PriorityLevels:Informational:Async"] = "true",
+                ["AppConfig:AI:Governance:Escalation:PriorityLevels:Critical:TimeoutSeconds"] = "600",
+                ["AppConfig:AI:Governance:Escalation:PriorityLevels:Critical:EscalateToAll"] = "true",
+            })
+            .Build();
+
+        var escConfig = config.GetSection("AppConfig:AI:Governance:Escalation").Get<EscalationConfig>();
+
+        escConfig.Should().NotBeNull();
+        escConfig!.Enabled.Should().BeTrue();
+        escConfig.DefaultTimeoutSeconds.Should().Be(300);
+        escConfig.DefaultTimeoutAction.Should().Be("DenyAndEscalate");
+        escConfig.DefaultApprovalStrategy.Should().Be("AnyOf");
+        escConfig.PriorityLevels.Should().ContainKeys("Informational", "Critical");
+        escConfig.PriorityLevels["Informational"].Async.Should().BeTrue();
+        escConfig.PriorityLevels["Critical"].EscalateToAll.Should().BeTrue();
+        escConfig.PriorityLevels["Critical"].TimeoutSeconds.Should().Be(600);
+    }
+
+    [Fact]
+    public void ResilienceConfig_BindsFullStructure_FromAppsettings()
+    {
+        var config = new ConfigurationBuilder()
+            .AddInMemoryCollection(new Dictionary<string, string?>
+            {
+                ["AppConfig:AI:Resilience:Enabled"] = "false",
+                ["AppConfig:AI:Resilience:FallbackChain:0:ClientType"] = "AzureOpenAI",
+                ["AppConfig:AI:Resilience:FallbackChain:0:DeploymentId"] = "gpt-4o",
+                ["AppConfig:AI:Resilience:FallbackChain:0:Capabilities:SupportsToolCalling"] = "true",
+                ["AppConfig:AI:Resilience:FallbackChain:1:ClientType"] = "AzureAIInference",
+                ["AppConfig:AI:Resilience:FallbackChain:1:DeploymentId"] = "claude-sonnet",
+                ["AppConfig:AI:Resilience:CircuitBreaker:FailureRatio"] = "0.5",
+                ["AppConfig:AI:Resilience:Retry:MaxAttempts"] = "2",
+                ["AppConfig:AI:Resilience:Timeout:PerAttemptSeconds"] = "30",
+            })
+            .Build();
+
+        var resConfig = config.GetSection("AppConfig:AI:Resilience").Get<ResilienceConfig>();
+
+        resConfig.Should().NotBeNull();
+        resConfig!.Enabled.Should().BeFalse();
+        resConfig.FallbackChain.Should().HaveCount(2);
+        resConfig.FallbackChain[0].ClientType.Should().Be(AIAgentFrameworkClientType.AzureOpenAI);
+        resConfig.FallbackChain[0].DeploymentId.Should().Be("gpt-4o");
+        resConfig.FallbackChain[1].ClientType.Should().Be(AIAgentFrameworkClientType.AzureAIInference);
+        resConfig.CircuitBreaker.FailureRatio.Should().Be(0.5);
+        resConfig.Retry.MaxAttempts.Should().Be(2);
+        resConfig.Timeout.PerAttemptSeconds.Should().Be(30);
+    }
+
+    [Fact]
+    public void FallbackProviderConfig_BindsCapabilities()
+    {
+        var config = new ConfigurationBuilder()
+            .AddInMemoryCollection(new Dictionary<string, string?>
+            {
+                ["FallbackChain:0:ClientType"] = "AzureOpenAI",
+                ["FallbackChain:0:DeploymentId"] = "gpt-4o",
+                ["FallbackChain:0:Capabilities:SupportsToolCalling"] = "true",
+                ["FallbackChain:0:Capabilities:SupportsStreaming"] = "true",
+                ["FallbackChain:0:Capabilities:SupportsVision"] = "true",
+                ["FallbackChain:0:Capabilities:MaxTokens"] = "128000",
+            })
+            .Build();
+
+        var entries = config.GetSection("FallbackChain").Get<FallbackProviderConfig[]>();
+
+        entries.Should().NotBeNull().And.HaveCount(1);
+        var entry = entries![0];
+        entry.ClientType.Should().Be(AIAgentFrameworkClientType.AzureOpenAI);
+        entry.DeploymentId.Should().Be("gpt-4o");
+        entry.Capabilities.SupportsToolCalling.Should().BeTrue();
+        entry.Capabilities.SupportsVision.Should().BeTrue();
+        entry.Capabilities.MaxTokens.Should().Be(128000);
+    }
 }
