import type { ReactNode } from 'react';
import { Inbox } from 'lucide-react';

interface EmptyStateProps {
  icon?: ReactNode;
  title: string;
  description?: string;
  action?: ReactNode;
}

export default function EmptyState({ icon, title, description, action }: EmptyStateProps) {
  return (
    <div className="empty-state">
      <div style={{ marginBottom: 12, color: 'var(--text-muted)' }}>
        {icon ?? <Inbox size={40} strokeWidth={1.5} />}
      </div>
      <p style={{ fontWeight: 600, color: 'var(--text-secondary)', marginBottom: 4 }}>{title}</p>
      {description && <p style={{ fontSize: 12 }}>{description}</p>}
      {action && <div className="mt-16">{action}</div>}
    </div>
  );
}
