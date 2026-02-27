import { useEffect, useState, useCallback, useRef } from 'react';
import { getSessions, revokeSession } from '../api';
import type { SessionListItem, PagedResult } from '../types';

function formatTime(iso: string): string {
  return new Date(iso).toLocaleString();
}

export default function Sessions() {
  const [result, setResult] = useState<PagedResult<SessionListItem> | null>(null);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [autoRefresh, setAutoRefresh] = useState(false);
  const intervalRef = useRef<number | null>(null);

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
      intervalRef.current = window.setInterval(load, 10000);
    }
    return () => {
      if (intervalRef.current !== null) {
        clearInterval(intervalRef.current);
        intervalRef.current = null;
      }
    };
  }, [autoRefresh, load]);

  async function handleRevoke(id: string) {
    if (!confirm('Revoke this session?')) return;
    try {
      await revokeSession(id);
      load();
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Revoke failed');
    }
  }

  const totalPages = result ? Math.ceil(result.total / pageSize) : 0;

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <h2>Active Sessions</h2>
          <p>Currently active MFA sessions</p>
        </div>
        <label className="toggle-label">
          <input
            type="checkbox"
            checked={autoRefresh}
            onChange={(e) => setAutoRefresh(e.target.checked)}
          />
          Auto-refresh (10s)
        </label>
      </div>

      {error && <div className="error-banner">{error}</div>}

      <div className="card">
        <div className="table-container">
          {loading && !result ? (
            <div className="loading">Loading sessions...</div>
          ) : (
            <table>
              <thead>
                <tr>
                  <th>User ID</th>
                  <th>Source IP</th>
                  <th>Method</th>
                  <th>Target</th>
                  <th>Created</th>
                  <th>Expires</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                {(result?.data ?? []).length === 0 ? (
                  <tr>
                    <td colSpan={7} className="text-muted" style={{ textAlign: 'center' }}>
                      No active sessions
                    </td>
                  </tr>
                ) : (
                  result!.data.map((s) => (
                    <tr key={s.id}>
                      <td className="mono">{s.userId}</td>
                      <td className="mono">{s.sourceIp}</td>
                      <td>
                        <span className="badge badge-info">{s.verifiedMethod}</span>
                      </td>
                      <td>{s.targetResource ?? '-'}</td>
                      <td className="mono">{formatTime(s.createdAt)}</td>
                      <td className="mono">{formatTime(s.expiresAt)}</td>
                      <td>
                        <button
                          className="btn btn-danger btn-sm"
                          onClick={() => handleRevoke(s.id)}
                        >
                          Revoke
                        </button>
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          )}
        </div>
      </div>

      {result && totalPages > 1 && (
        <div className="pagination">
          <span>
            Showing {(page - 1) * pageSize + 1}-
            {Math.min(page * pageSize, result.total)} of {result.total}
          </span>
          <div className="pagination-buttons">
            <button
              className="btn btn-outline btn-sm"
              disabled={page <= 1}
              onClick={() => setPage((p) => p - 1)}
            >
              Previous
            </button>
            <button
              className="btn btn-outline btn-sm"
              disabled={page >= totalPages}
              onClick={() => setPage((p) => p + 1)}
            >
              Next
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
