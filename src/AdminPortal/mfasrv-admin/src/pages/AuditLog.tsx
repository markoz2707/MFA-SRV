import { useEffect, useState, useCallback } from 'react';
import { getAuditLog } from '../api';
import type { AuditLogEntry, PagedResult, AuditEventType } from '../types';

const EVENT_TYPES: AuditEventType[] = [
  'AuthenticationAttempt',
  'MfaChallengeIssued',
  'MfaChallengeVerified',
  'MfaChallengeFailed',
  'MfaChallengeExpired',
  'PolicyEvaluated',
  'SessionCreated',
  'SessionExpired',
  'SessionRevoked',
  'UserEnrolled',
  'UserDisenrolled',
  'PolicyCreated',
  'PolicyUpdated',
  'PolicyDeleted',
  'AgentRegistered',
  'AgentHeartbeat',
  'AgentDisconnected',
  'FailoverActivated',
  'FailoverDeactivated',
  'ConfigurationChanged',
];

function formatTime(iso: string): string {
  return new Date(iso).toLocaleString();
}

export default function AuditLog() {
  const [result, setResult] = useState<PagedResult<AuditLogEntry> | null>(null);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  // Filters
  const [userId, setUserId] = useState('');
  const [eventType, setEventType] = useState<AuditEventType | ''>('');
  const [fromDate, setFromDate] = useState('');
  const [toDate, setToDate] = useState('');

  const pageSize = 50;

  const load = useCallback(() => {
    setLoading(true);
    setError('');
    getAuditLog({
      page,
      pageSize,
      userId: userId || undefined,
      eventType,
      from: fromDate || undefined,
      to: toDate || undefined,
    })
      .then(setResult)
      .catch((e) => setError(e.message))
      .finally(() => setLoading(false));
  }, [page, userId, eventType, fromDate, toDate]);

  useEffect(() => {
    load();
  }, [load]);

  function applyFilters(e: React.FormEvent) {
    e.preventDefault();
    setPage(1);
    load();
  }

  function clearFilters() {
    setUserId('');
    setEventType('');
    setFromDate('');
    setToDate('');
    setPage(1);
  }

  const totalPages = result ? Math.ceil(result.total / pageSize) : 0;

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <h2>Audit Log</h2>
          <p>System-wide audit trail of all events</p>
        </div>
      </div>

      {error && <div className="error-banner">{error}</div>}

      {/* ── Filters ── */}
      <div className="card mb-16">
        <div className="card-body">
          <form onSubmit={applyFilters}>
            <div className="form-row">
              <div className="form-group">
                <label>User ID</label>
                <input
                  type="text"
                  className="form-control"
                  placeholder="Filter by user ID..."
                  value={userId}
                  onChange={(e) => setUserId(e.target.value)}
                />
              </div>
              <div className="form-group">
                <label>Event Type</label>
                <select
                  className="form-control"
                  value={eventType}
                  onChange={(e) => setEventType(e.target.value as AuditEventType | '')}
                >
                  <option value="">All Events</option>
                  {EVENT_TYPES.map((t) => (
                    <option key={t} value={t}>{t}</option>
                  ))}
                </select>
              </div>
              <div className="form-group">
                <label>From</label>
                <input
                  type="datetime-local"
                  className="form-control"
                  value={fromDate}
                  onChange={(e) => setFromDate(e.target.value)}
                />
              </div>
              <div className="form-group">
                <label>To</label>
                <input
                  type="datetime-local"
                  className="form-control"
                  value={toDate}
                  onChange={(e) => setToDate(e.target.value)}
                />
              </div>
            </div>
            <div className="flex-gap-8 mt-8">
              <button className="btn btn-primary btn-sm" type="submit">Apply Filters</button>
              <button className="btn btn-outline btn-sm" type="button" onClick={clearFilters}>Clear</button>
            </div>
          </form>
        </div>
      </div>

      {/* ── Table ── */}
      <div className="card">
        <div className="table-container">
          {loading && !result ? (
            <div className="loading">Loading audit log...</div>
          ) : (
            <table>
              <thead>
                <tr>
                  <th>Timestamp</th>
                  <th>Event Type</th>
                  <th>User</th>
                  <th>Source IP</th>
                  <th>Result</th>
                  <th>Target</th>
                  <th>Details</th>
                </tr>
              </thead>
              <tbody>
                {(result?.data ?? []).length === 0 ? (
                  <tr>
                    <td colSpan={7} className="text-muted" style={{ textAlign: 'center' }}>
                      No audit entries found
                    </td>
                  </tr>
                ) : (
                  result!.data.map((e) => (
                    <tr key={e.id}>
                      <td className="mono">{formatTime(e.timestamp)}</td>
                      <td>
                        <span className="badge badge-info">{e.eventType}</span>
                      </td>
                      <td>{e.userName ?? e.userId ?? '-'}</td>
                      <td className="mono">{e.sourceIp ?? '-'}</td>
                      <td>
                        {e.success ? (
                          <span className="badge badge-success">Success</span>
                        ) : (
                          <span className="badge badge-danger">Failed</span>
                        )}
                      </td>
                      <td>{e.targetResource ?? '-'}</td>
                      <td className="text-secondary" style={{ maxWidth: 250, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                        {e.details ?? '-'}
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
