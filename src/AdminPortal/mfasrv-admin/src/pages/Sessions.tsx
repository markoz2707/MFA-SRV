import { useEffect, useState, useCallback, useRef } from 'react';
import { getSessions, revokeSession } from '../api';
import type { SessionListItem, PagedResult, MfaMethod } from '../types';
import { Smartphone, Fingerprint, Key, MessageSquare, Mail, RefreshCw } from 'lucide-react';
import PageHeader from '../components/PageHeader';
import Pagination from '../components/Pagination';
import LoadingSpinner from '../components/LoadingSpinner';
import TimeAgo from '../components/TimeAgo';
import ConfirmDialog from '../components/ConfirmDialog';
import { useAppToast } from '../App';

function getMethodIcon(method: MfaMethod) {
  switch (method) {
    case 'Totp': case 'Push': return <Smartphone size={14} />;
    case 'Fido2': return <Fingerprint size={14} />;
    case 'FortiToken': return <Key size={14} />;
    case 'Sms': return <MessageSquare size={14} />;
    case 'Email': return <Mail size={14} />;
    default: return <Key size={14} />;
  }
}

function getTimeRemainingPct(createdAt: string, expiresAt: string): number {
  const now = Date.now();
  const start = new Date(createdAt).getTime();
  const end = new Date(expiresAt).getTime();
  const total = end - start;
  if (total <= 0) return 0;
  const remaining = end - now;
  return Math.max(0, Math.min(100, (remaining / total) * 100));
}

function getBarClass(pct: number): string {
  if (pct > 50) return 'bar-fill--healthy';
  if (pct > 25) return 'bar-fill--warning';
  return 'bar-fill--critical';
}

export default function Sessions() {
  const { showSuccess, showError } = useAppToast();
  const [result, setResult] = useState<PagedResult<SessionListItem> | null>(null);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [autoRefresh, setAutoRefresh] = useState(false);
  const [countdown, setCountdown] = useState(10);
  const intervalRef = useRef<number | null>(null);
  const countdownRef = useRef<number | null>(null);

  const [revokeTarget, setRevokeTarget] = useState<string | null>(null);

  const pageSize = 50;

  const load = useCallback(() => {
    setLoading(true);
    setError('');
    getSessions(page, pageSize)
      .then(setResult)
      .catch((e) => setError(e.message))
      .finally(() => setLoading(false));
  }, [page]);

  useEffect(() => {
    load();
  }, [load]);

  // Auto-refresh
  useEffect(() => {
    if (autoRefresh) {
      setCountdown(10);
      intervalRef.current = window.setInterval(() => {
        load();
        setCountdown(10);
      }, 10000);
      countdownRef.current = window.setInterval(() => {
        setCountdown((c) => Math.max(0, c - 1));
      }, 1000);
    }
    return () => {
      if (intervalRef.current !== null) {
        clearInterval(intervalRef.current);
        intervalRef.current = null;
      }
      if (countdownRef.current !== null) {
        clearInterval(countdownRef.current);
        countdownRef.current = null;
      }
    };
  }, [autoRefresh, load]);

  async function handleRevoke() {
    if (!revokeTarget) return;
    try {
      await revokeSession(revokeTarget);
      showSuccess('Session revoked');
      setRevokeTarget(null);
      load();
    } catch (e: unknown) {
      showError(e instanceof Error ? e.message : 'Revoke failed');
      setRevokeTarget(null);
    }
  }

  return (
    <div className="page">
      <PageHeader title="Active Sessions" subtitle="Currently active MFA sessions">
        <label className="toggle-label">
          <input
            type="checkbox"
            checked={autoRefresh}
            onChange={(e) => setAutoRefresh(e.target.checked)}
          />
          Auto-refresh
          {autoRefresh && (
            <span className="badge badge-neutral badge-sm" style={{ marginLeft: 4 }}>
              {countdown}s
            </span>
          )}
        </label>
        {autoRefresh && (
          <RefreshCw size={14} style={{ animation: 'spin 2s linear infinite', color: 'var(--primary)' }} />
        )}
      </PageHeader>

      {error && <div className="error-banner">{error}</div>}

      <div className="card">
        <div className="table-container">
          {loading && !result ? (
            <LoadingSpinner message="Loading sessions..." />
          ) : (
            <table>
              <thead>
                <tr>
                  <th>User ID</th>
                  <th>Source IP</th>
                  <th>Method</th>
                  <th>Target</th>
                  <th>Created</th>
                  <th>Time Remaining</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                {(result?.data ?? []).length === 0 ? (
                  <tr>
                    <td colSpan={7} className="text-muted" style={{ textAlign: 'center', padding: 32 }}>
                      No active sessions
                    </td>
                  </tr>
                ) : (
                  result!.data.map((s) => {
                    const pct = getTimeRemainingPct(s.createdAt, s.expiresAt);
                    return (
                      <tr key={s.id}>
                        <td className="mono">{s.userId}</td>
                        <td className="mono">{s.sourceIp}</td>
                        <td>
                          <span className="badge badge-info" style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
                            {getMethodIcon(s.verifiedMethod)}
                            {s.verifiedMethod}
                          </span>
                        </td>
                        <td>{s.targetResource ?? '-'}</td>
                        <td><TimeAgo date={s.createdAt} /></td>
                        <td style={{ minWidth: 120 }}>
                          <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                            <div className="time-remaining-bar" style={{ flex: 1 }}>
                              <div
                                className={`bar-fill ${getBarClass(pct)}`}
                                style={{ width: `${pct}%` }}
                              />
                            </div>
                            <span className="text-muted" style={{ fontSize: 11, whiteSpace: 'nowrap' }}>
                              <TimeAgo date={s.expiresAt} fallback="Expired" />
                            </span>
                          </div>
                        </td>
                        <td>
                          <button
                            className="btn btn-danger btn-sm"
                            onClick={() => setRevokeTarget(s.id)}
                          >
                            Revoke
                          </button>
                        </td>
                      </tr>
                    );
                  })
                )}
              </tbody>
            </table>
          )}
        </div>
      </div>

      {result && (
        <Pagination
          page={page}
          pageSize={pageSize}
          total={result.total}
          onPageChange={setPage}
        />
      )}

      <ConfirmDialog
        open={revokeTarget !== null}
        title="Revoke Session"
        message="Are you sure you want to revoke this session? The user will need to re-authenticate."
        confirmLabel="Revoke"
        danger
        onConfirm={handleRevoke}
        onCancel={() => setRevokeTarget(null)}
      />

      <style>{`@keyframes spin { from { transform: rotate(0deg); } to { transform: rotate(360deg); } }`}</style>
    </div>
  );
}
