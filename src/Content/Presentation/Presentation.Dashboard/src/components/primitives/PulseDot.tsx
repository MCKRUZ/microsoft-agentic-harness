interface PulseDotProps {
  color?: string;
  size?: number;
}

export function PulseDot({ color = 'var(--otel-positive)', size = 8 }: PulseDotProps) {
  return (
    <span className="relative inline-block" style={{ width: size, height: size }}>
      <span
        className="absolute inset-0 rounded-full opacity-40"
        style={{ background: color, animation: 'pulse-ring 1.6s ease-out infinite' }}
      />
      <span className="absolute inset-0 rounded-full" style={{ background: color }} />
    </span>
  );
}
