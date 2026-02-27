import { useEffect, useState } from 'react';
import { getDashboardStats } from '../api';
import type { DashboardStats } from '../types';

function formatTime(iso: string): string {
  const d = new Date(iso);
  return d.toLocaleString();
}

export default function Dashboard() {
  const [stats, setStats] = useState<DashboardStats | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    getDashboardStats()
      .then(setStats)
      .catch((e) => setError(e.message))
      .finally(() => setLoading(false));
  }, []);

  if (loading) return <div className="page"><div className="loading">Loading dashboard...</div></div>;
  if (error) return <div className="page"><div className="error-banner">{error}</div></div>;
  if (!stats) return null;

  const enrollmentEntries = Object.entries(stats.enrollmentsByMethod ?? {});
  const maxEnrollment = Math.max(1, ...enrollmentEntries.map(([, v]) => v));

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <h2>Dashboard</h2>
          <p>MfaSrv system overview</p>
        </div>
      </div>

      {/* ── Top stat cards ── */}
      <div className="stats-grid">
        <div className="stat-card">
          <div className="stat-label">Total Users</div>
          <div className="stat-value">{stats.totalUsers.toLocaleString()}</div>
        </div>
        <div className="stat-card">
          <div className="stat-label">MFA Enabled</div>
          <div className="stat-value">{stats.mfaEnabledUsers.toLocaleString()}</div>
          <div className="stat-sub">
            {stats.totalUsers > 0
              ? `${((stats.mfaEnabledUsers / stats.totalUsers) * 100).toFixed(1)}% of users`
              : 'No users'}
          </div>
        </div>
        <div className="stat-card">
          <div className="stat-label">Active Sessions</div>
          <div className="stat-value">{stats.activeSessions.toLocaleString()}</div>
        </div>
        <div className="stat-card">
          <div className="stat-label">Online Agents</div>
          <div className="stat-value">{stats.onlineAgents}</div>
        </div>
      </div>

      {/* ── Last 24h stats ── */}
      <div className="stats-grid">
        <div className="stat-card">
          <div className="stat-label">Authentications (24h)</div>
          <div className="stat-value">{stats.last24h.authentications.toLocaleString()}</div>
        </div>
        <div className="stat-card">
          <div className="stat-label">MFA Challenges (24h)</div>
          <div className="stat-value">{stats.last24h.mfaChallenges.toLocaleString()}</div>
        </div>
        <div className="stat-card">
          <div className="stat-label">Successes (24h)</div>
          <div className="stat-value text-success">{stats.last24h.successes.toLocaleString()}</div>
        </div>
        <div className="stat-card">
          <div className="stat-label">Failures (24h)</div>
          <div className="stat-value text-danger">{stats.last24h.failures.toLocaleString()}</div>
        </div>
      </div>

      {/* ── Enrollments by method ── */}
      {enrollmentEntries.length > 0 && (
        <div className="card">
          <div className="card-header">
            <h3>Enrollments by Method</h3>
          </div>
          <div className="card-body">
            <div className="bar-chart">
              {enrollmentEntries.map(([method, count]) => (
                <div className="bar-row" key={method}>
                  <div className="bar-label">{method}</div>
                  <div className="bar-track">
                    <div
                      className="bar-fill"
                      style={{ width: `${(count / maxEnrollment) * 100}%` }}
                    />
                  </div>
                  <div className="bar-value">{count}</div>
                </div>
              ))}
            </div>
          </div>
        </div>
      )}

      {/* ── Recent events ── */}
      <div className="card">
        <div className="card-header">
          <h3>Recent Events</h3>
        </div>
        <div className="table-container">
          <table>
            <thead>
              <tr>
                <th>Timestamp</th>
                <th>Event</th>
                <th>User</th>
                <th>Source IP</th>
                <th>Result</th>
                <th>Details</th>
              </tr>
            </thead>
            <tbody>
              {(stats.recentEvents ?? []).length === 0 ? (
                <tr>
                  <td colSpan={6} className="text-muted" style={{ textAlign: 'center' }}>
                    No recent events
                  </td>
                </tr>
              ) : (
                stats.recentEvents.map((e) => (
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
                    <td className="text-secondary">{e.details ? (e.details.length > 60 ? e.details.substring(0, 60) + '...' : e.details) : '-'}</td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
