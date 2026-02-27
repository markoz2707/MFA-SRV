import { useEffect, useState } from 'react';
import { getAgents } from '../api';
import type { AgentRegistration } from '../types';

function formatTime(iso: string | null): string {
  if (!iso) return '-';
  return new Date(iso).toLocaleString();
}

function statusBadgeClass(status: string): string {
  switch (status) {
    case 'Online':
      return 'badge-success';
    case 'Offline':
      return 'badge-danger';
    case 'Degraded':
      return 'badge-warning';
    default:
      return 'badge-neutral';
  }
}

export default function Agents() {
  const [agents, setAgents] = useState<AgentRegistration[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  useEffect(() => {
    getAgents()
      .then(setAgents)
      .catch((e) => setError(e.message))
      .finally(() => setLoading(false));
  }, []);

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <h2>Agents</h2>
          <p>Registered DC Agents and Endpoint Agents</p>
        </div>
        <button className="btn btn-outline" onClick={() => {
          setLoading(true);
          getAgents()
            .then(setAgents)
            .catch((e) => setError(e.message))
            .finally(() => setLoading(false));
        }}>
          Refresh
        </button>
      </div>

      {error && <div className="error-banner">{error}</div>}

      <div className="card">
        <div className="table-container">
          {loading ? (
            <div className="loading">Loading agents...</div>
          ) : (
            <table>
              <thead>
                <tr>
                  <th>Hostname</th>
                  <th>Type</th>
                  <th>Status</th>
                  <th>IP Address</th>
                  <th>Version</th>
                  <th>Last Heartbeat</th>
                  <th>Registered</th>
                </tr>
              </thead>
              <tbody>
                {agents.length === 0 ? (
                  <tr>
                    <td colSpan={7} className="text-muted" style={{ textAlign: 'center' }}>
                      No agents registered
                    </td>
                  </tr>
                ) : (
                  agents.map((a) => (
                    <tr key={a.id}>
                      <td><strong>{a.hostname}</strong></td>
                      <td>
                        <span className="badge badge-info">{a.agentType}</span>
                      </td>
                      <td>
                        <span className={`badge ${statusBadgeClass(a.status)}`}>
                          {a.status}
                        </span>
                      </td>
                      <td className="mono">{a.ipAddress ?? '-'}</td>
                      <td>{a.version ?? '-'}</td>
                      <td className="mono">{formatTime(a.lastHeartbeatAt)}</td>
                      <td className="mono">{formatTime(a.registeredAt)}</td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          )}
        </div>
      </div>
    </div>
  );
}
