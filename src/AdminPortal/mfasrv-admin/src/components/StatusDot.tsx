interface StatusDotProps {
  status: 'Online' | 'Offline' | 'Degraded';
  label?: string;
}

export default function StatusDot({ status, label }: StatusDotProps) {
  const cls =
    status === 'Online'
      ? 'status-dot--online'
      : status === 'Degraded'
      ? 'status-dot--degraded'
      : 'status-dot--offline';

  return (
    <span className={`status-dot ${cls}`}>
      {label ?? status}
    </span>
  );
}
