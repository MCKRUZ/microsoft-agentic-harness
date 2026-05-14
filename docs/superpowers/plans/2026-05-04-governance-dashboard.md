# Governance Dashboard Tab Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a "Governance" tab to the observability dashboard showing policy decisions, prompt injection detections, MCP tool scans, and audit trail health.

**Architecture:** New route group under `/governance` with a hub page linking to sub-pages. Uses existing Prometheus query pipeline (`usePromQuery` + `metricCatalog`) and reusable chart/panel components.

**Tech Stack:** React 19, TypeScript, Recharts (via existing chart wrappers), lucide-react icons, Prometheus PromQL

---

### Task 1: Governance Metric Catalog Entries

**Files:**
- Modify: `src/Content/Presentation/Presentation.Dashboard/src/config/metricCatalog.ts`

- [ ] **Step 1: Add governance metric entries to the catalog**

Add a new `governance` category with entries for all 8 governance metrics. Insert after the existing `budget` category block:

```typescript
  // ── Governance ──────────────────────────────
  governance_decisions: {
    id: 'governance_decisions',
    title: 'Policy Decisions',
    description: 'Total governance policy evaluations',
    query: 'sum(increase(agent_governance_decisions_total[5m]))',
    chartType: 'stat' as const,
    unit: '',
    category: 'governance',
    refreshIntervalSeconds: 15,
  },
  governance_violations: {
    id: 'governance_violations',
    title: 'Violations',
    description: 'Denied actions by governance policy',
    query: 'sum(increase(agent_governance_violations_total[5m]))',
    chartType: 'stat' as const,
    unit: '',
    category: 'governance',
    refreshIntervalSeconds: 15,
  },
  governance_eval_duration: {
    id: 'governance_eval_duration',
    title: 'Eval Latency',
    description: 'Policy evaluation duration (p50)',
    query: 'histogram_quantile(0.5, sum(rate(agent_governance_evaluation_duration_bucket[5m])) by (le))',
    chartType: 'stat' as const,
    unit: 'ms',
    category: 'governance',
    refreshIntervalSeconds: 15,
  },
  governance_rate_limit_hits: {
    id: 'governance_rate_limit_hits',
    title: 'Rate Limit Hits',
    description: 'Rate-limited tool calls',
    query: 'sum(increase(agent_governance_rate_limit_hits_total[5m]))',
    chartType: 'stat' as const,
    unit: '',
    category: 'governance',
    refreshIntervalSeconds: 15,
  },
  governance_audit_events: {
    id: 'governance_audit_events',
    title: 'Audit Events',
    description: 'Governance audit log entries',
    query: 'sum(increase(agent_governance_audit_events_total[5m]))',
    chartType: 'stat' as const,
    unit: '',
    category: 'governance',
    refreshIntervalSeconds: 15,
  },
  governance_injection_detections: {
    id: 'governance_injection_detections',
    title: 'Injection Detections',
    description: 'Prompt injection attempts detected',
    query: 'sum(increase(agent_governance_injection_detections_total[5m]))',
    chartType: 'stat' as const,
    unit: '',
    category: 'governance',
    refreshIntervalSeconds: 15,
  },
  governance_mcp_scans: {
    id: 'governance_mcp_scans',
    title: 'MCP Scans',
    description: 'MCP tool security scans performed',
    query: 'sum(increase(agent_governance_mcp_scans_total[5m]))',
    chartType: 'stat' as const,
    unit: '',
    category: 'governance',
    refreshIntervalSeconds: 15,
  },
  governance_mcp_threats: {
    id: 'governance_mcp_threats',
    title: 'MCP Threats',
    description: 'MCP tool threats detected',
    query: 'sum(increase(agent_governance_mcp_threats_total[5m]))',
    chartType: 'stat' as const,
    unit: '',
    category: 'governance',
    refreshIntervalSeconds: 15,
  },
  governance_decisions_ts: {
    id: 'governance_decisions_ts',
    title: 'Decisions Over Time',
    description: 'Policy decisions rate by action',
    query: 'sum(rate(agent_governance_decisions_total[1m])) by (agent_governance_action)',
    chartType: 'timeseries' as const,
    unit: 'decisions/min',
    category: 'governance',
    refreshIntervalSeconds: 15,
  },
  governance_violations_by_tool: {
    id: 'governance_violations_by_tool',
    title: 'Violations by Tool',
    description: 'Blocked tool calls by tool name',
    query: 'topk(10, sum(increase(agent_governance_violations_total[1h])) by (agent_governance_tool))',
    chartType: 'bar' as const,
    unit: '',
    category: 'governance',
    refreshIntervalSeconds: 30,
  },
  governance_injections_ts: {
    id: 'governance_injections_ts',
    title: 'Injections Over Time',
    description: 'Prompt injection detections rate',
    query: 'sum(rate(agent_governance_injection_detections_total[1m]))',
    chartType: 'timeseries' as const,
    unit: 'detections/min',
    category: 'governance',
    refreshIntervalSeconds: 15,
  },
```

- [ ] **Step 2: Verify no TypeScript errors**

Run: `cd src/Content/Presentation/Presentation.Dashboard && npx tsc --noEmit`
Expected: no errors

- [ ] **Step 3: Commit**

```bash
git add src/Content/Presentation/Presentation.Dashboard/src/config/metricCatalog.ts
git commit -m "feat(dashboard): add governance metric catalog entries"
```

---

### Task 2: Governance Page Component

**Files:**
- Create: `src/Content/Presentation/Presentation.Dashboard/src/routes/Governance/GovernancePage.tsx`

- [ ] **Step 1: Create GovernancePage component**

Follow the existing page pattern (SafetyPage/ToolsPage). Use `usePromQuery` hook with `metricCatalog` lookups. Structure:

```tsx
import { usePromQuery } from '../../api/usePromQuery';
import { metricCatalog } from '../../config/metricCatalog';
import { PageHeader } from '../../components/primitives/PageHeader';
import { Section } from '../../components/primitives/Section';
import { PanelCard } from '../../components/panels/PanelCard';
import { PanelGrid } from '../../components/panels/PanelGrid';
import { KpiCard } from '../../components/panels/KpiCard';
import { TimeSeriesChart } from '../../components/charts/TimeSeriesChart';
import { HBarList } from '../../components/charts/HBarList';
import { ArcGauge } from '../../components/charts/ArcGauge';
import { Pill } from '../../components/primitives/Pill';

export default function GovernancePage() {
  // Fetch KPI metrics
  const decisions = usePromQuery(metricCatalog['governance_decisions']!.query);
  const violations = usePromQuery(metricCatalog['governance_violations']!.query);
  const evalDuration = usePromQuery(metricCatalog['governance_eval_duration']!.query);
  const rateLimitHits = usePromQuery(metricCatalog['governance_rate_limit_hits']!.query);
  const auditEvents = usePromQuery(metricCatalog['governance_audit_events']!.query);
  const injections = usePromQuery(metricCatalog['governance_injection_detections']!.query);
  const mcpScans = usePromQuery(metricCatalog['governance_mcp_scans']!.query);
  const mcpThreats = usePromQuery(metricCatalog['governance_mcp_threats']!.query);

  // Fetch time series
  const decisionsTs = usePromQuery(metricCatalog['governance_decisions_ts']!.query);
  const violationsByTool = usePromQuery(metricCatalog['governance_violations_by_tool']!.query);
  const injectionsTs = usePromQuery(metricCatalog['governance_injections_ts']!.query);

  // Compute derived values
  const totalDecisions = Number(decisions.data?.value ?? 0);
  const totalViolations = Number(violations.data?.value ?? 0);
  const violationRate = totalDecisions > 0 ? totalViolations / totalDecisions : 0;
  const totalInjections = Number(injections.data?.value ?? 0);
  const totalMcpScans = Number(mcpScans.data?.value ?? 0);
  const totalMcpThreats = Number(mcpThreats.data?.value ?? 0);
  const mcpThreatRate = totalMcpScans > 0 ? totalMcpThreats / totalMcpScans : 0;

  return (
    <>
      <PageHeader title="Governance" />

      {/* ── Section 01: Overview ── */}
      <Section title="Overview" kicker="01">
        <PanelGrid columns={4}>
          <PanelCard title="Policy Decisions">
            <KpiCard title="Total" value={totalDecisions.toLocaleString()} unit="decisions" />
          </PanelCard>
          <PanelCard title="Violations">
            <KpiCard title="Blocked" value={totalViolations.toLocaleString()} unit="denied"
              delta={violationRate > 0.1 ? 'high' : undefined} />
          </PanelCard>
          <PanelCard title="Eval Latency">
            <KpiCard title="p50" value={Number(evalDuration.data?.value ?? 0).toFixed(1)} unit="ms" />
          </PanelCard>
          <PanelCard title="Rate Limits">
            <KpiCard title="Hits" value={Number(rateLimitHits.data?.value ?? 0).toLocaleString()} unit="hits" />
          </PanelCard>
        </PanelGrid>
      </Section>

      {/* ── Section 02: Policy Enforcement ── */}
      <Section title="Policy Enforcement" kicker="02">
        <PanelGrid columns={2}>
          <PanelCard title="Decisions Over Time" description="Allow vs Deny rate by action type">
            <TimeSeriesChart series={decisionsTs.data?.series ?? []} unit="decisions/min" />
          </PanelCard>
          <PanelCard title="Top Blocked Tools" description="Most frequently denied tool calls">
            <HBarList
              items={(violationsByTool.data?.series ?? []).map(s => ({
                label: s.metric?.agent_governance_tool ?? 'unknown',
                value: Number(s.values?.[s.values.length - 1]?.[1] ?? 0),
              }))}
              color="var(--otel-error)"
            />
          </PanelCard>
        </PanelGrid>
        <PanelGrid columns={1}>
          <PanelCard title="Violation Rate">
            <div style={{ display: 'flex', alignItems: 'center', gap: '1rem' }}>
              <ArcGauge value={violationRate} max={1} size={140}
                label={`${(violationRate * 100).toFixed(1)}%`} />
              <div>
                <p style={{ color: 'var(--text-secondary)', fontSize: '0.875rem' }}>
                  {totalViolations} of {totalDecisions} decisions resulted in denials
                </p>
                <Pill variant={violationRate > 0.2 ? 'negative' : violationRate > 0.05 ? 'warning' : 'info'}>
                  {violationRate > 0.2 ? 'High' : violationRate > 0.05 ? 'Moderate' : 'Low'} violation rate
                </Pill>
              </div>
            </div>
          </PanelCard>
        </PanelGrid>
      </Section>

      {/* ── Section 03: Prompt Injection Detection ── */}
      <Section title="Prompt Injection Detection" kicker="03">
        <PanelGrid columns={2}>
          <PanelCard title="Detections Over Time" description="Prompt injection attempts caught by pattern matching">
            <TimeSeriesChart series={injectionsTs.data?.series ?? []} unit="detections/min" />
          </PanelCard>
          <PanelCard title="Detection Summary">
            <KpiCard title="Total Detections" value={totalInjections.toLocaleString()} unit="blocked" />
            <div style={{ marginTop: '0.5rem' }}>
              <Pill variant={totalInjections > 0 ? 'negative' : 'info'}>
                {totalInjections > 0 ? `${totalInjections} injection attempts blocked` : 'No injections detected'}
              </Pill>
            </div>
          </PanelCard>
        </PanelGrid>
      </Section>

      {/* ── Section 04: MCP Tool Security ── */}
      <Section title="MCP Tool Security" kicker="04">
        <PanelGrid columns={3}>
          <PanelCard title="Tools Scanned">
            <KpiCard title="Scans" value={totalMcpScans.toLocaleString()} unit="tools" />
          </PanelCard>
          <PanelCard title="Threats Found">
            <KpiCard title="Threats" value={totalMcpThreats.toLocaleString()} unit="threats" />
          </PanelCard>
          <PanelCard title="Threat Rate">
            <ArcGauge value={mcpThreatRate} max={1} size={120}
              label={`${(mcpThreatRate * 100).toFixed(1)}%`} />
          </PanelCard>
        </PanelGrid>
      </Section>

      {/* ── Section 05: Audit Trail ── */}
      <Section title="Audit Trail" kicker="05">
        <PanelGrid columns={2}>
          <PanelCard title="Audit Events" description="Governance audit log entries over time">
            <KpiCard title="Total" value={Number(auditEvents.data?.value ?? 0).toLocaleString()} unit="events" />
          </PanelCard>
          <PanelCard title="Chain Integrity">
            <div style={{ display: 'flex', alignItems: 'center', gap: '0.75rem' }}>
              <Pill variant="info">Verified</Pill>
              <span style={{ color: 'var(--text-secondary)', fontSize: '0.875rem' }}>
                Tamper-evident hash chain intact
              </span>
            </div>
          </PanelCard>
        </PanelGrid>
      </Section>
    </>
  );
}
```

Note: This is the target structure. Adapt import paths, hook signatures, and component props to match what actually exists in the codebase. Read the actual `usePromQuery` hook, `KpiCard`, `PanelGrid`, `ArcGauge`, `HBarList`, `Pill`, `Section`, and `PageHeader` components to confirm their actual prop interfaces before coding.

- [ ] **Step 2: Verify no TypeScript errors**

Run: `cd src/Content/Presentation/Presentation.Dashboard && npx tsc --noEmit`

- [ ] **Step 3: Commit**

```bash
git add src/Content/Presentation/Presentation.Dashboard/src/routes/Governance/GovernancePage.tsx
git commit -m "feat(dashboard): add Governance page with policy, injection, MCP, and audit panels"
```

---

### Task 3: Route and Sidebar Navigation

**Files:**
- Modify: `src/Content/Presentation/Presentation.Dashboard/src/app/router.tsx`
- Modify: `src/Content/Presentation/Presentation.Dashboard/src/components/layout/Sidebar.tsx`

- [ ] **Step 1: Add lazy import and route**

In `router.tsx`, add a lazy import at the top with the other lazy pages:

```tsx
const GovernancePage = lazy(() => import('../routes/Governance/GovernancePage'));
```

Add a route inside the `DashboardShell` children array, after the Registry group:

```tsx
{ path: '/governance', element: <Suspense fallback={<LoadingSkeleton />}><GovernancePage /></Suspense> },
```

- [ ] **Step 2: Add sidebar navigation entry**

In `Sidebar.tsx`, add a new nav group. Import the `Shield` icon from lucide-react. Add after the Registry group:

```tsx
{/* Governance */}
<NavLink to="/governance" className={/* same active-state pattern as other single-page links */}>
  <Shield size={16} />
  <span>Governance</span>
</NavLink>
```

Follow the exact same styling pattern as the other nav items (active border, hover states, icon + label layout).

- [ ] **Step 3: Verify routing works**

Run: `cd src/Content/Presentation/Presentation.Dashboard && npm run dev`
Navigate to `/governance` in the browser. Verify:
- Page renders without errors
- Sidebar shows Governance link
- Active state highlights correctly

- [ ] **Step 4: Commit**

```bash
git add src/Content/Presentation/Presentation.Dashboard/src/app/router.tsx src/Content/Presentation/Presentation.Dashboard/src/components/layout/Sidebar.tsx
git commit -m "feat(dashboard): add Governance route and sidebar navigation"
```

---

### Task 4: Visual Verification and Polish

- [ ] **Step 1: Start dev server and verify all sections render**

Run: `cd src/Content/Presentation/Presentation.Dashboard && npm run dev`

Check:
- Overview section: 4 KPI cards render (may show 0 without live data)
- Policy Enforcement section: Time series chart + bar chart render
- Prompt Injection section: Detection chart + summary card render
- MCP Security section: 3 cards with gauge render
- Audit Trail section: Events + chain integrity render
- No console errors

- [ ] **Step 2: Build production bundle**

Run: `cd src/Content/Presentation/Presentation.Dashboard && npm run build`
Expected: Build succeeds with no errors

- [ ] **Step 3: Commit any polish fixes**

```bash
git add -A src/Content/Presentation/Presentation.Dashboard/
git commit -m "feat(dashboard): polish Governance tab layout and build"
```
