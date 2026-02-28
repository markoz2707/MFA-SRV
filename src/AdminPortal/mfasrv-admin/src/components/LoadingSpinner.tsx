import { Loader2 } from 'lucide-react';

interface LoadingSpinnerProps {
  message?: string;
}

export default function LoadingSpinner({ message = 'Loading...' }: LoadingSpinnerProps) {
  return (
    <div className="loading">
      <Loader2 size={20} style={{ animation: 'spin 1s linear infinite', display: 'inline-block', marginRight: 8, verticalAlign: 'middle' }} />
      {message}
      <style>{`@keyframes spin { from { transform: rotate(0deg); } to { transform: rotate(360deg); } }`}</style>
    </div>
  );
}
