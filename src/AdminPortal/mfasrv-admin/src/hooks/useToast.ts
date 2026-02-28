import { useState, useCallback } from 'react';

export interface ToastItem {
  id: number;
  type: 'success' | 'error';
  message: string;
  exiting?: boolean;
}

let nextId = 0;

export function useToast() {
  const [toasts, setToasts] = useState<ToastItem[]>([]);

  const dismiss = useCallback((id: number) => {
    setToasts((prev) => prev.map((t) => (t.id === id ? { ...t, exiting: true } : t)));
    setTimeout(() => {
      setToasts((prev) => prev.filter((t) => t.id !== id));
    }, 200);
  }, []);

  const show = useCallback(
    (type: 'success' | 'error', message: string) => {
      const id = nextId++;
      setToasts((prev) => [...prev, { id, type, message }]);
      setTimeout(() => dismiss(id), 4000);
    },
    [dismiss]
  );

  const showSuccess = useCallback((msg: string) => show('success', msg), [show]);
  const showError = useCallback((msg: string) => show('error', msg), [show]);

  return { toasts, showSuccess, showError, dismiss };
}
