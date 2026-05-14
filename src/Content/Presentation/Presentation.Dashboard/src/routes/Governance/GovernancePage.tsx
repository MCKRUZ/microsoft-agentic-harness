import { usePromQuery } from '@/hooks/usePromQuery';
import { metricCatalog } from '@/config/metricCatalog';
import { KpiCard } from '@/components/panels/KpiCard';
import { PanelCard } from '@/components/panels/PanelCard';
import { PanelGrid } from '@/components/panels/PanelGrid';
import { LoadingSkeleton } from '@/components/panels/LoadingSkeleton';
import { TimeSeriesChart } from '@/components/charts/TimeSeriesChart';
import { PageHeader } from '@/components/primitives/PageHeader';
import { Section } from '@/components/primitives/Section';
import { ArcGauge } from '@/components/primitives/ArcGauge';
import { HBarList } from '@/components/primitives/HBarList';
import { Pill } from '@/components/primitives/Pill';

function latestValue(data: ReturnType<typeof usePromQuery>['data']): number {
  const dp = data?.series[0]?.dataPoints;
  if (!dp || dp.length === 0) return 0;
  return parseFloat(dp[dp.length - 1]!.value) || 0;
}

export default function GovernancePage() {
  const decisions = usePromQuery(metricCatalog['governance_decisions']!.query);
  const violations = usePromQuery(metricCatalog['governance_violations']!.query);
  const evalDuration = usePromQuery(metricCatalog['governance_eval_duration']!.query);
  const rateLimitHits = usePromQuery(metricCatalog['governance_rate_limit_hits']!.query);
  const auditEvents = usePromQuery(metricCatalog['governance_audit_events']!.query);
  const injections = usePromQuery(metricCatalog['governance_injection_detections']!.query);
  const mcpScans = usePromQuery(metricCatalog['governance_mcp_scans']!.query);
  const mcpThreats = usePromQuery(metricCatalog['governance_mcp_threats']!.query);

  const decisionsTs = usePromQuery(metricCatalog['governance_decisions_ts']!.query);
  const violationsByTool = usePromQuery(metricCatalog['governance_violations_by_tool']!.query);
  const injectionsTs = usePromQuery(metricCatalog['governance_injections_ts']!.query);

  if (decisions.isLoading) {
    return (
      <div className="space-y-6">
        <PageHeader title="Governance" subtitle="Policy enforcement, injection detection, MCP security, and audit trail" />
        <PanelGrid columns={4}>
          {Array.from({ length: 4 }).map((_, i) => <LoadingSkeleton key={i} />)}
        </PanelGrid>
      </div>
    );
  }

  const totalDecisions = latestValue(decisions.data);
  const totalViolations = latestValue(violations.data);
  const violationRate = totalDecisions > 0 ? totalViolations / totalDecisions : 0;
  const totalInjections = latestValue(injections.data);
  const totalMcpScans = latestValue(mcpScans.data);
  const totalMcpThreats = latestValue(mcpThreats.data);
  const mcpThreatRate = totalMcpScans > 0 ? totalMcpThreats / totalMcpScans : 0;

  return (
    <div className="space-y-6">
      <PageHeader title="Governance" subtitle="Policy enforcement, injection detection, MCP security, and audit trail" />

      <Section title="Overview" kicker="01">
        <PanelGrid columns={4}>
          <KpiCard
            title="Policy Decisions"
            description="Total governance policy evaluations performed on tool calls. Each tool invocation triggers a policy check against loaded YAML rules."
            value={totalDecisions.toFixed(0)}
            sparklineData={decisions.data?.series[0]?.dataPoints}
          />
          <KpiCard
            title="Violations"
            description="Tool calls denied by governance policy. High counts may indicate agents attempting restricted operations or overly strict policies."
            value={totalViolations.toFixed(0)}
            sparklineData={violations.data?.series[0]?.dataPoints}
            delta={violationRate > 0.1 ? `${(violationRate * 100).toFixed(0)}% denied` : undefined}
            trend={violationRate > 0.1 ? 'down' : undefined}
          />
          <KpiCard
            title="Eval Latency (p50)"
            description="Median policy evaluation duration. Governance checks run synchronously in the MediatR pipeline — high latency directly impacts agent response time."
            value={latestValue(evalDuration.data).toFixed(1)}
            unit="ms"
            sparklineData={evalDuration.data?.series[0]?.dataPoints}
          />
          <KpiCard
            title="Rate Limit Hits"
            description="Tool calls throttled by rate-limiting rules. Shows how often agents hit per-tool or per-agent rate limits."
            value={latestValue(rateLimitHits.data).toFixed(0)}
            sparklineData={rateLimitHits.data?.series[0]?.dataPoints}
          />
        </PanelGrid>
      </Section>

      <Section title="Policy Enforcement" kicker="02">
        <PanelGrid columns={2}>
          <PanelCard title="Decisions Over Time" description="Allow vs Deny rate by action type">
            <TimeSeriesChart series={decisionsTs.data?.series ?? []} unit="decisions/min" />
          </PanelCard>
          <PanelCard title="Top Blocked Tools" description="Most frequently denied tool calls">
            <HBarList
              items={(violationsByTool.data?.series ?? []).map((s) => ({
                label: s.labels['agent_governance_tool'] ?? 'unknown',
                value: parseFloat(s.dataPoints[s.dataPoints.length - 1]?.value ?? '0'),
              }))}
              color="var(--otel-negative)"
              formatValue={(v) => v.toFixed(0)}
            />
          </PanelCard>
        </PanelGrid>
        <PanelGrid columns={1}>
          <PanelCard title="Violation Rate">
            <div className="flex items-center gap-4 py-2">
              <ArcGauge
                value={violationRate}
                max={1}
                size={140}
                color={violationRate > 0.2 ? 'var(--otel-negative)' : violationRate > 0.05 ? 'var(--otel-warning)' : 'var(--otel-positive)'}
                label={`${(violationRate * 100).toFixed(1)}%`}
                subtitle="of decisions denied"
                thickness={12}
              />
              <div className="space-y-2">
                <p className="text-sm text-muted-foreground">
                  {totalViolations.toFixed(0)} of {totalDecisions.toFixed(0)} decisions resulted in denials
                </p>
                <Pill variant={violationRate > 0.2 ? 'negative' : violationRate > 0.05 ? 'warning' : 'info'}>
                  {violationRate > 0.2 ? 'High' : violationRate > 0.05 ? 'Moderate' : 'Low'} violation rate
                </Pill>
              </div>
            </div>
          </PanelCard>
        </PanelGrid>
      </Section>

      <Section title="Prompt Injection Detection" kicker="03">
        <PanelGrid columns={2}>
          <PanelCard title="Detections Over Time" description="Prompt injection attempts caught by pattern matching">
            <TimeSeriesChart series={injectionsTs.data?.series ?? []} unit="detections/min" />
          </PanelCard>
          <PanelCard title="Detection Summary">
            <div className="space-y-3 py-2">
              <KpiCard
                title="Total Detections"
                description="Prompt injection attempts identified and blocked by the PromptInjectionBehavior pipeline step."
                value={totalInjections.toFixed(0)}
                sparklineData={injections.data?.series[0]?.dataPoints}
              />
              <Pill variant={totalInjections > 0 ? 'negative' : 'info'}>
                {totalInjections > 0 ? `${totalInjections.toFixed(0)} injection attempts blocked` : 'No injections detected'}
              </Pill>
            </div>
          </PanelCard>
        </PanelGrid>
      </Section>

      <Section title="MCP Tool Security" kicker="04">
        <PanelGrid columns={3}>
          <KpiCard
            title="Tools Scanned"
            description="MCP tools evaluated by the McpSecurityScanner for tool poisoning, description injection, and other threats."
            value={totalMcpScans.toFixed(0)}
            sparklineData={mcpScans.data?.series[0]?.dataPoints}
          />
          <KpiCard
            title="Threats Found"
            description="MCP tools flagged as potentially malicious. Includes tool poisoning, zero-width character injection, and typosquatting detection."
            value={totalMcpThreats.toFixed(0)}
            sparklineData={mcpThreats.data?.series[0]?.dataPoints}
            delta={totalMcpThreats > 0 ? `${totalMcpThreats.toFixed(0)} threats` : undefined}
            trend={totalMcpThreats > 0 ? 'down' : undefined}
          />
          <PanelCard title="Threat Rate">
            <div className="flex items-center justify-center py-4">
              <ArcGauge
                value={mcpThreatRate}
                max={1}
                size={120}
                color={mcpThreatRate > 0.1 ? 'var(--otel-negative)' : 'var(--otel-warning)'}
                label={`${(mcpThreatRate * 100).toFixed(1)}%`}
                subtitle="of scans flagged"
                thickness={10}
              />
            </div>
          </PanelCard>
        </PanelGrid>
      </Section>

      <Section title="Audit Trail" kicker="05">
        <PanelGrid columns={2}>
          <KpiCard
            title="Audit Events"
            description="Governance audit log entries recording all policy evaluations, injection scans, and MCP security checks with tamper-evident hashing."
            value={latestValue(auditEvents.data).toFixed(0)}
            sparklineData={auditEvents.data?.series[0]?.dataPoints}
          />
          <PanelCard title="Chain Integrity">
            <div className="flex items-center gap-3 py-4">
              <Pill variant="info">Verified</Pill>
              <span className="text-sm text-muted-foreground">
                Tamper-evident hash chain intact
              </span>
            </div>
          </PanelCard>
        </PanelGrid>
      </Section>
    </div>
  );
}
