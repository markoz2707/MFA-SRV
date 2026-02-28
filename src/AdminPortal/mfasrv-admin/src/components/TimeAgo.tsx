import { useState, useEffect } from 'react';

interface TimeAgoProps {
  date: string | null;
  fallback?: string;
}

function getTimeAgo(date: Date): string {
  const seconds = Math.floor((Date.now() - date.getTime()) / 1000);
  if (seconds < 5) return 'just now';
  if (seconds < 60) return `${seconds}s ago`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  if (days < 30) return `${days}d ago`;
  return date.toLocaleDateString();
}

export default function TimeAgo({ date, fallback = '-' }: TimeAgoProps) {
  const [, setTick] = useState(0);

  useEffect(() => {
    if (!date) return;
    const timer = setInterval(() => setTick((t) => t + 1), 30000);
    return () => clearInterval(timer);
  }, [date]);

  if (!date) return <span className="text-muted">{fallback}</span>;

  const d = new Date(date);
  return (
    <span title={d.toLocaleString()}>
      {getTimeAgo(d)}
    </span>
  );
}
