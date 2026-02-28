import { useEffect, useState } from 'react';
import { getDashboardStats } from '../api';
import type { DashboardStats } from '../types';
import {
  Settings as SettingsIcon, Users, ShieldCheck, Key, Server,
  Database, Globe, Info,
} from 'lucide-react';
import PageHeader from '../components/PageHeader';
import LoadingSpinner from '../components/LoadingSpinner';

export default function SettingsPage() {
  const [stats, setStats] = useState<DashboardStats | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  useEffect(() => {
    getDashboardStats()
      .then(setStats)
      .catch((e) => setError(e.message))
      .finally(() => setLoading(false));
  }, []);

  if (loading) return <div className="page"><LoadingSpinner message="Loading settings..." /></div>;
  if (error) return <div className="page"><div className="error-banner">{error}</div></div>;

  const enrollmentMethods = stats ? Object.keys(stats.enrollmentsByMethod ?? {}) : [];

  return (
    <div className="page">
      <PageHeader title="Settings" subtitle="System configuration and information" />

      <div className="settings-grid">
        {/* System Status */}
        <div className="settings-card">
          <div className="settings-card-header">
            <Info size={16} />
            System Status
          </div>
          <div className="settings-card-body">
            <dl>
              <div className="settings-row">
                <dt><Users size={13} style={{ verticalAlign: -2, marginRight: 6 }} />Total Users</dt>
                <dd>{stats?.totalUsers.toLocaleString() ?? '-'}</dd>
              </div>
              <div className="settings-row">
                <dt><ShieldCheck size={13} style={{ verticalAlign: -2, marginRight: 6 }} />MFA Enabled</dt>
                <dd>{stats?.mfaEnabledUsers.toLocaleString() ?? '-'}</dd>
              </div>
              <div className="settings-row">
                <dt><Key size={13} style={{ verticalAlign: -2, marginRight: 6 }} />Active Sessions</dt>
                <dd>{stats?.activeSessions.toLocaleString() ?? '-'}</dd>
              </div>
              <div className="settings-row">
                <dt><Server size={13} style={{ verticalAlign: -2, marginRight: 6 }} />Online Agents</dt>
                <dd>{stats?.onlineAgents ?? '-'}</dd>
              </div>
            </dl>
          </div>
        </div>

        {/* Provider Status */}
        <div className="settings-card">
          <div className="settings-card-header">
            <ShieldCheck size={16} />
            MFA Providers
          </div>
          <div className="settings-card-body">
            <dl>
              {['Totp', 'Push', 'Fido2', 'FortiToken', 'Sms', 'Email'].map((method) => (
                <div className="settings-row" key={method}>
                  <dt>{method}</dt>
                  <dd>
                    {enrollmentMethods.includes(method) ? (
                      <span className="badge badge-success badge-sm">Active</span>
                    ) : (
                      <span className="badge badge-neutral badge-sm">No Enrollments</span>
                    )}
                  </dd>
                </div>
              ))}
            </dl>
          </div>
        </div>

        {/* Server Configuration */}
        <div className="settings-card">
          <div className="settings-card-header">
            <Globe size={16} />
            Server Endpoints
          </div>
          <div className="settings-card-body">
            <dl>
              <div className="settings-row">
                <dt>REST API</dt>
                <dd className="mono">/api/*</dd>
              </div>
              <div className="settings-row">
                <dt>gRPC</dt>
                <dd className="mono">:5001 (mTLS)</dd>
              </div>
              <div className="settings-row">
                <dt>Admin Portal</dt>
                <dd className="mono">/admin</dd>
              </div>
              <div className="settings-row">
                <dt>Gossip</dt>
                <dd className="mono">:5090 (Http2)</dd>
              </div>
            </dl>
          </div>
        </div>

        {/* Database */}
        <div className="settings-card">
          <div className="settings-card-header">
            <Database size={16} />
            Database
          </div>
          <div className="settings-card-body">
            <dl>
              <div className="settings-row">
                <dt>Provider</dt>
                <dd>SQLite (Development)</dd>
              </div>
              <div className="settings-row">
                <dt>ORM</dt>
                <dd>Entity Framework Core</dd>
              </div>
              <div className="settings-row">
                <dt>Production Options</dt>
                <dd>PostgreSQL, SQL Server</dd>
              </div>
            </dl>
          </div>
        </div>

        {/* About */}
        <div className="settings-card">
          <div className="settings-card-header">
            <SettingsIcon size={16} />
            About MfaSrv
          </div>
          <div className="settings-card-body">
            <dl>
              <div className="settings-row">
                <dt>Version</dt>
                <dd>1.0.0</dd>
              </div>
              <div className="settings-row">
                <dt>Framework</dt>
                <dd>ASP.NET Core 8</dd>
              </div>
              <div className="settings-row">
                <dt>Dashboard</dt>
                <dd>React 18 + TypeScript</dd>
              </div>
              <div className="settings-row">
                <dt>Auth Protocol</dt>
                <dd>gRPC/mTLS</dd>
              </div>
            </dl>
          </div>
        </div>
      </div>
    </div>
  );
}
