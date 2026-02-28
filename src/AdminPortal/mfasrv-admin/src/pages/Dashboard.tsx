import { useEffect, useState } from 'react';
import { getDashboardStats, getHourlyStats } from '../api';
import type { DashboardStats, HourlyStat } from '../types';
import {
  AreaChart, Area, BarChart, Bar,
  XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer, Legend,
} from 'recharts';
import {
  Users, ShieldCheck, Key, Server,
  Activity, CheckCircle, XCircle, TrendingUp,
} from 'lucide-react';
import PageHeader from '../components/PageHeader';
import LoadingSpinner from '../components/LoadingSpinner';
import TimeAgo from '../components/TimeAgo';

type HourRange = 12 | 24 | 48 | 168;

function eventBadgeClass(eventType: string): string {
  if (eventType.includes('Failed') || eventType.includes('Denied'))
    return 'badge badge-danger';
  if (eventType.includes('Success') || eventType.includes('Verified') || eventType.includes('Created'))
    return 'badge badge-success';
  if (eventType.includes('Expired') || eventType.includes('Revoked'))
    return 'badge badge-warning';
  return 'badge badge-info';
}

export default function Dashboard() {
  const [stats, setStats] = useState<DashboardStats | null>(null);
  const [hourlyData, setHourlyData] = useState<HourlyStat[]>([]);
  const [hourRange, setHourRange] = useState<HourRange>(24);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    setLoading(true);
    Promise.all([getDashboardStats(), getHourlyStats(hourRange)])
      .then(([s, h]) => {
        setStats(s);
        setHourlyData(h);
      })
      .catch((e) => setError(e.message))
      .finally(() => setLoading(false));
  }, [hourRange]);

  if (loading) return <div className="page"><LoadingSpinner message="Loading dashboard..." /></div>;
  if (error) return <div className="page"><div className="error-banner">{error}</div></div>;
  if (!stats) return null;

  const enrollmentEntries = Object.entries(stats.enrollmentsByMethod ?? {});
  const enrollmentChartData = enrollmentEntries.map(([method, count]) => ({ method, count }));

  const hourRanges: { label: string; value: HourRange }[] = [
    { label: '12h', value: 12 },
    { label: '24h', value: 24 },
    { label: '48h', value: 48 },
    { label: '7d', value: 168 },
  ];

  return (
    <div className="page">
      <PageHeader title="Dashboard" subtitle="MfaSrv system overview" />

      {/* Top stat cards */}
      <div className="stats-grid">
        <div className="stat-card stat-card--primary">
          <div className="stat-header">
            <Users size={16} />
            <span className="stat-label">Total Users</span>
          </div>
          <div className="stat-value">{stats.totalUsers.toLocaleString()}</div>
        </div>
        <div className="stat-card stat-card--success">
          <div className="stat-header">
            <ShieldCheck size={16} />
            <span className="stat-label">MFA Enabled</span>
          </div>
          <div className="stat-value">{stats.mfaEnabledUsers.toLocaleString()}</div>
          <div className="stat-sub">
            {stats.totalUsers > 0
              ? `${((stats.mfaEnabledUsers / stats.totalUsers) * 100).toFixed(1)}% of users`
              : 'No users'}
          </div>
        </div>
        <div className="stat-card stat-card--info">
          <div className="stat-header">
            <Key size={16} />
            <span className="stat-label">Active Sessions</span>
          </div>
          <div className="stat-value">{stats.activeSessions.toLocaleString()}</div>
        </div>
        <div className="stat-card stat-card--warning">
          <div className="stat-header">
            <Server size={16} />
            <span className="stat-label">Online Agents</span>
          </div>
          <div className="stat-value">{stats.onlineAgents}</div>
        </div>
      </div>

      {/* 24h stats */}
      <div className="stats-grid">
        <div className="stat-card">
          <div className="stat-header">
            <Activity size={16} />
            <span className="stat-label">Authentications (24h)</span>
          </div>
          <div className="stat-value">{stats.last24h.authentications.toLocaleString()}</div>
        </div>
        <div className="stat-card">
          <div className="stat-header">
            <TrendingUp size={16} />
            <span className="stat-label">MFA Challenges (24h)</span>
          </div>
          <div className="stat-value">{stats.last24h.mfaChallenges.toLocaleString()}</div>
        </div>
        <div className="stat-card stat-card--success">
          <div className="stat-header">
            <CheckCircle size={16} />
            <span className="stat-label">Successes (24h)</span>
          </div>
          <div className="stat-value text-success">{stats.last24h.successes.toLocaleString()}</div>
        </div>
        <div className="stat-card stat-card--danger">
          <div className="stat-header">
            <XCircle size={16} />
            <span className="stat-label">Failures (24h)</span>
          </div>
          <div className="stat-value text-danger">{stats.last24h.failures.toLocaleString()}</div>
        </div>
      </div>

      {/* Hourly stats chart */}
      <div className="card">
        <div className="card-header">
          <h3>Authentication Trends</h3>
          <div className="flex-gap-8">
            {hourRanges.map((r) => (
              <button
                key={r.value}
                className={`btn btn-sm ${hourRange === r.value ? 'btn-primary' : 'btn-outline'}`}
                onClick={() => setHourRange(r.value)}
              >
                {r.label}
              </button>
            ))}
          </div>
        </div>
        <div className="card-body">
          <div className="chart-container">
            <ResponsiveContainer width="100%" height={280}>
              <AreaChart data={hourlyData}>
                <defs>
                  <linearGradient id="colorAuth" x1="0" y1="0" x2="0" y2="1">
                    <stop offset="5%" stopColor="#0d9488" stopOpacity={0.3} />
                    <stop offset="95%" stopColor="#0d9488" stopOpacity={0} />
                  </linearGradient>
                  <linearGradient id="colorSuccess" x1="0" y1="0" x2="0" y2="1">
                    <stop offset="5%" stopColor="#16a34a" stopOpacity={0.3} />
                    <stop offset="95%" stopColor="#16a34a" stopOpacity={0} />
                  </linearGradient>
                  <linearGradient id="colorFail" x1="0" y1="0" x2="0" y2="1">
                    <stop offset="5%" stopColor="#dc2626" stopOpacity={0.3} />
                    <stop offset="95%" stopColor="#dc2626" stopOpacity={0} />
                  </linearGradient>
                </defs>
                <CartesianGrid strokeDasharray="3 3" stroke="#e2e8f0" />
                <XAxis
                  dataKey="hour"
                  tick={{ fontSize: 11, fill: '#94a3b8' }}
                  tickFormatter={(v: string) => {
                    const d = new Date(v);
                    return `${d.getHours().toString().padStart(2, '0')}:00`;
                  }}
                />
                <YAxis tick={{ fontSize: 11, fill: '#94a3b8' }} />
                <Tooltip
                  contentStyle={{
                    background: '#fff',
                    border: '1px solid #e2e8f0',
                    borderRadius: 8,
                    fontSize: 12,
                  }}
                  labelFormatter={(v) => new Date(String(v)).toLocaleString()}
                />
                <Legend wrapperStyle={{ fontSize: 12 }} />
                <Area
                  type="monotone"
                  dataKey="authentications"
                  stroke="#0d9488"
                  fill="url(#colorAuth)"
                  strokeWidth={2}
                  name="Authentications"
                />
                <Area
                  type="monotone"
                  dataKey="successes"
                  stroke="#16a34a"
                  fill="url(#colorSuccess)"
                  strokeWidth={2}
                  name="Successes"
                />
                <Area
                  type="monotone"
                  dataKey="failures"
                  stroke="#dc2626"
                  fill="url(#colorFail)"
                  strokeWidth={2}
                  name="Failures"
                />
              </AreaChart>
            </ResponsiveContainer>
          </div>
        </div>
      </div>

      {/* Enrollments by method - Recharts bar chart */}
      {enrollmentChartData.length > 0 && (
        <div className="card">
          <div className="card-header">
            <h3>Enrollments by Method</h3>
          </div>
          <div className="card-body">
            <div className="chart-container">
              <ResponsiveContainer width="100%" height={Math.max(200, enrollmentChartData.length * 50)}>
                <BarChart
                  data={enrollmentChartData}
                  layout="vertical"
                  margin={{ left: 20 }}
                >
                  <CartesianGrid strokeDasharray="3 3" stroke="#e2e8f0" horizontal={false} />
                  <XAxis type="number" tick={{ fontSize: 11, fill: '#94a3b8' }} />
                  <YAxis
                    type="category"
                    dataKey="method"
                    tick={{ fontSize: 12, fill: '#64748b', fontWeight: 600 }}
                    width={90}
                  />
                  <Tooltip
                    contentStyle={{
                      background: '#fff',
                      border: '1px solid #e2e8f0',
                      borderRadius: 8,
                      fontSize: 12,
                    }}
                  />
                  <Bar
                    dataKey="count"
                    fill="#0d9488"
                    radius={[0, 4, 4, 0]}
                    name="Enrollments"
                  />
                </BarChart>
              </ResponsiveContainer>
            </div>
          </div>
        </div>
      )}

      {/* Recent events */}
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
                  <td colSpan={6} className="text-muted" style={{ textAlign: 'center', padding: 32 }}>
                    No recent events
                  </td>
                </tr>
              ) : (
                stats.recentEvents.map((e) => (
                  <tr key={e.id}>
                    <td><TimeAgo date={e.timestamp} /></td>
                    <td>
                      <span className={eventBadgeClass(e.eventType)}>{e.eventType}</span>
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
                    <td className="text-secondary" style={{ maxWidth: 250, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                      {e.details ? (e.details.length > 60 ? e.details.substring(0, 60) + '...' : e.details) : '-'}
                    </td>
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
