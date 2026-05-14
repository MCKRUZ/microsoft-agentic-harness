interface ArcGaugeProps {
  value: number;
  max?: number;
  size?: number;
  color?: string;
  trackColor?: string;
  label: string;
  subtitle?: string;
  thickness?: number;
}

export function ArcGauge({
  value,
  max = 1,
  size = 120,
  color = 'var(--otel-accent)',
  trackColor = 'var(--border)',
  label,
  subtitle,
  thickness = 10,
}: ArcGaugeProps) {
  const r = size / 2 - thickness;
  const c = 2 * Math.PI * r;
  const pct = Math.min(1, value / max);

  return (
    <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`}>
      <circle cx={size / 2} cy={size / 2} r={r} fill="none" stroke={trackColor} strokeWidth={thickness} />
      <circle
        cx={size / 2}
        cy={size / 2}
        r={r}
        fill="none"
        stroke={color}
        strokeWidth={thickness}
        strokeDasharray={`${c * pct} ${c}`}
        strokeLinecap="round"
        transform={`rotate(-90 ${size / 2} ${size / 2})`}
      />
      <text
        x={size / 2}
        y={size / 2 - 2}
        textAnchor="middle"
        fill="currentColor"
        fontSize={size * 0.2}
        fontWeight="700"
        className="fill-foreground"
      >
        {label}
      </text>
      {subtitle && (
        <text
          x={size / 2}
          y={size / 2 + size * 0.16}
          textAnchor="middle"
          fontSize={size * 0.085}
          className="fill-otel-text-mute font-mono"
        >
          {subtitle}
        </text>
      )}
    </svg>
  );
}
