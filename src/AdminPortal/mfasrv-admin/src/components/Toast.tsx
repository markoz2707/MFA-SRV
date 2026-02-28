import { CheckCircle, XCircle, X } from 'lucide-react';
import type { ToastItem } from '../hooks/useToast';

interface ToastContainerProps {
  toasts: ToastItem[];
  dismiss: (id: number) => void;
}

export default function ToastContainer({ toasts, dismiss }: ToastContainerProps) {
  if (toasts.length === 0) return null;

  return (
    <div className="toast-container">
      {toasts.map((t) => (
        <div
          key={t.id}
          className={`toast toast--${t.type}${t.exiting ? ' toast--exit' : ''}`}
        >
          {t.type === 'success' ? (
            <CheckCircle size={16} />
          ) : (
            <XCircle size={16} />
          )}
          <span style={{ flex: 1 }}>{t.message}</span>
          <button
            onClick={() => dismiss(t.id)}
            style={{
              background: 'none',
              border: 'none',
              color: 'inherit',
              cursor: 'pointer',
              padding: 2,
              display: 'flex',
            }}
          >
            <X size={14} />
          </button>
        </div>
      ))}
    </div>
  );
}
