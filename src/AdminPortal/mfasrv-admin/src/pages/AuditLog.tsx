import { useEffect, useState, useCallback } from 'react';
import { getAuditLog } from '../api';
import type { AuditLogEntry, PagedResult, AuditEventType } from '../types';
import {
  ChevronDown, ChevronRight, Filter, Download,
  LogIn, ShieldCheck, ShieldAlert, ShieldOff, Clock,
  FileText, UserPlus, UserMinus, Settings, Server, AlertTriangle,
} from 'lucide-react';
import PageHeader from '../components/PageHeader';
import Pagination from '../components/Pagination';
import LoadingSpinner from '../components/LoadingSpinner';
import TimeAgo from '../components/TimeAgo';

const EVENT_TYPES: AuditEventType[] = [
  'AuthenticationAttempt', 'MfaChallengeIssued', 'MfaChallengeVerified',
  'MfaChallengeFailed', 'MfaChallengeExpired', 'PolicyEvaluated',
  'SessionCreated', 'SessionExpired', 'SessionRevoked',
  'UserEnrolled', 'UserDisenrolled', 'PolicyCreated', 'PolicyUpdated',
  'PolicyDeleted', 'AgentRegistered', 'AgentHeartbeat', 'AgentDisconnected',
  'FailoverActivated', 'FailoverDeactivated', 'ConfigurationChanged',
];

function getEventIcon(eventType: string) {
  if (eventType.startsWith('Authentication')) return <LogIn size={14} />;
  if (eventType.includes('Verified')) return <ShieldCheck size={14} />;
  if (eventType.includes('Failed')) return <ShieldAlert size={14} />;
  if (eventType.includes('Expired') || eventType.includes('Revoked')) return <ShieldOff size={14} />;
  if (eventType.startsWith('Session')) return <Clock size={14} />;
  if (eventType.startsWith('Policy')) return <FileText size={14} />;
  if (eventType === 'UserEnrolled') return <UserPlus size={14} />;
  if (eventType === 'UserDisenrolled') return <UserMinus size={14} />;
  if (eventType.startsWith('Agent')) return <Server size={14} />;
  if (eventType.startsWith('Failover')) return <AlertTriangle size={14} />;
  if (eventType === 'ConfigurationChanged') return <Settings size={14} />;
  return <FileText size={14} />;
}

function getEventBadgeClass(eventType: string): string {
  if (eventType.includes('Failed') || eventType.includes('Denied')) return 'badge badge-danger';
  if (eventType.includes('Verified') || eventType.includes('Created') || eventType === 'UserEnrolled') return 'badge badge-success';
  if (eventType.includes('Expired') || eventType.includes('Revoked') || eventType.startsWith('Failover')) return 'badge badge-warning';
  return 'badge badge-info';
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
  const [filtersOpen, setFiltersOpen] = useState(false);

  // Expandable rows
  const [expandedRows, setExpandedRows] = useState<Set<number>>(new Set());

  const pageSize = 50;

  const activeFilterCount = [userId, eventType, fromDate, toDate].filter(Boolean).length;

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

  function toggleRow(id: number) {
    setExpandedRows((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  function exportCsv() {
    if (!result?.data.length) return;
    const headers = ['Timestamp', 'Event Type', 'User', 'Source IP', 'Success', 'Target', 'Details'];
    const rows = result.data.map((e) => [
      e.timestamp,
      e.eventType,
      e.userName ?? e.userId ?? '',
      e.sourceIp ?? '',
      e.success ? 'Yes' : 'No',
      e.targetResource ?? '',
      (e.details ?? '').replace(/"/g, '""'),
    ]);
    const csv = [headers.join(','), ...rows.map((r) => r.map((c) => `"${c}"`).join(','))].join('\n');
    const blob = new Blob([csv], { type: 'text/csv' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `audit-log-${new Date().toISOString().slice(0, 10)}.csv`;
    a.click();
    URL.revokeObjectURL(url);
  }

  return (
    <div className="page">
      <PageHeader title="Audit Log" subtitle="System-wide audit trail of all events">
        <button
          className="btn btn-outline"
          onClick={exportCsv}
          disabled={!result?.data.length}
        >
          <Download size={14} /> Export CSV
        </button>
      </PageHeader>

      {error && <div className="error-banner">{error}</div>}

      {/* Collapsible filter panel */}
      <div className="card mb-16">
        <div
          className="collapsible-header"
          onClick={() => setFiltersOpen(!filtersOpen)}
          style={{ borderBottom: filtersOpen ? undefined : 'none' }}
        >
          <div className="filter-toggle">
            <Filter size={14} />
            <span style={{ fontWeight: 600, fontSize: 13 }}>
              {filtersOpen ? 'Hide Filters' : 'Show Filters'}
            </span>
            {activeFilterCount > 0 && (
              <span className="filter-count-badge">{activeFilterCount}</span>
            )}
          </div>
          <ChevronDown
            size={16}
            style={{
              transition: 'transform 150ms ease',
              transform: filtersOpen ? 'rotate(180deg)' : 'rotate(0deg)',
            }}
          />
        </div>
        {filtersOpen && (
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
        )}
      </div>

      {/* Table */}
      <div className="card">
        <div className="table-container">
          {loading && !result ? (
            <LoadingSpinner message="Loading audit log..." />
          ) : (
            <table>
              <thead>
                <tr>
                  <th style={{ width: 30 }}></th>
                  <th>Timestamp</th>
                  <th>Event Type</th>
                  <th>User</th>
                  <th>Source IP</th>
                  <th>Result</th>
                  <th>Target</th>
                </tr>
              </thead>
              <tbody>
                {(result?.data ?? []).length === 0 ? (
                  <tr>
                    <td colSpan={7} className="text-muted" style={{ textAlign: 'center', padding: 32 }}>
                      No audit entries found
                    </td>
                  </tr>
                ) : (
                  result!.data.map((e) => (
                    <>
                      <tr
                        key={e.id}
                        className="expandable-row"
                        onClick={() => toggleRow(e.id)}
                      >
                        <td style={{ width: 30, paddingRight: 0 }}>
                          {expandedRows.has(e.id) ? (
                            <ChevronDown size={14} color="var(--text-muted)" />
                          ) : (
                            <ChevronRight size={14} color="var(--text-muted)" />
                          )}
                        </td>
                        <td>
                          <TimeAgo date={e.timestamp} />
                        </td>
                        <td>
                          <span className={getEventBadgeClass(e.eventType)} style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
                            {getEventIcon(e.eventType)}
                            {e.eventType}
                          </span>
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
                      </tr>
                      {expandedRows.has(e.id) && (
                        <tr key={`${e.id}-detail`} className="expand-detail">
                          <td colSpan={7}>
                            <div className="expand-detail-inner">
                              <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '8px 24px' }}>
                                <div>
                                  <strong>Event ID:</strong> {e.id}
                                </div>
                                <div>
                                  <strong>Timestamp:</strong> {new Date(e.timestamp).toLocaleString()}
                                </div>
                                <div>
                                  <strong>Agent ID:</strong> {e.agentId ?? '-'}
                                </div>
                                <div>
                                  <strong>User ID:</strong> {e.userId ?? '-'}
                                </div>
                              </div>
                              {e.details && (
                                <div style={{ marginTop: 8 }}>
                                  <strong>Details:</strong>
                                  <pre style={{
                                    margin: '4px 0 0',
                                    padding: 10,
                                    background: 'var(--surface)',
                                    border: '1px solid var(--border)',
                                    borderRadius: 4,
                                    fontSize: 12,
                                    whiteSpace: 'pre-wrap',
                                    wordBreak: 'break-word',
                                    fontFamily: 'inherit',
                                  }}>
                                    {e.details}
                                  </pre>
                                </div>
                              )}
                            </div>
                          </td>
                        </tr>
                      )}
                    </>
                  ))
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
    </div>
  );
}
