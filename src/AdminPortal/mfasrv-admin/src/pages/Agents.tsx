import { useEffect, useState, useCallback, useRef } from 'react';
import { getAgents } from '../api';
import type { AgentRegistration, AgentStatus } from '../types';
import { Server, Monitor, Wifi, WifiOff, Globe, Tag, Clock, RefreshCw } from 'lucide-react';
import PageHeader from '../components/PageHeader';
import LoadingSpinner from '../components/LoadingSpinner';
import EmptyState from '../components/EmptyState';
import StatusDot from '../components/StatusDot';
import TimeAgo from '../components/TimeAgo';

function getHeartbeatColor(lastHeartbeat: string | null): string {
  if (!lastHeartbeat) return 'var(--text-muted)';
  const diffMs = Date.now() - new Date(lastHeartbeat).getTime();
  const diffMin = diffMs / 60000;
  if (diffMin < 1) return 'var(--success)';
  if (diffMin < 5) return 'var(--warning)';
  return 'var(--danger)';
}

export default function Agents() {
  const [agents, setAgents] = useState<AgentRegistration[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [autoRefresh, setAutoRefresh] = useState(false);
  const intervalRef = useRef<number | null>(null);

  const load = useCallback(() => {
    setLoading(true);
    setError('');
    getAgents()
      .then(setAgents)
      .catch((e) => setError(e.message))
      .finally(() => setLoading(false));
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  useEffect(() => {
    if (autoRefresh) {
      intervalRef.current = window.setInterval(load, 15000);
    }
    return () => {
      if (intervalRef.current !== null) {
        clearInterval(intervalRef.current);
        intervalRef.current = null;
      }
    };
  }, [autoRefresh, load]);

  const online = agents.filter((a) => a.status === 'Online').length;
  const offline = agents.filter((a) => a.status === 'Offline').length;
  const degraded = agents.filter((a) => a.status === 'Degraded').length;

  return (
    <div className="page">
      <PageHeader title="Agents" subtitle="Registered DC Agents and Endpoint Agents">
        <label className="toggle-label">
          <input
            type="checkbox"
            checked={autoRefresh}
            onChange={(e) => setAutoRefresh(e.target.checked)}
          />
          Auto-refresh
        </label>
        <button className="btn btn-outline" onClick={load}>
          <RefreshCw size={14} /> Refresh
        </button>
      </PageHeader>

      {error && <div className="error-banner">{error}</div>}

      {loading ? (
        <LoadingSpinner message="Loading agents..." />
      ) : agents.length === 0 ? (
        <EmptyState
          icon={<Server size={40} strokeWidth={1.5} />}
          title="No agents registered"
          description="Agents will appear here once they connect to the server."
        />
      ) : (
        <>
          {/* Summary bar */}
          <div className="summary-bar">
            <div className="summary-item">
              <Server size={16} />
              <span className="count">{agents.length}</span>
              Total
            </div>
            <div className="summary-item">
              <Wifi size={16} color="var(--success)" />
              <span className="count">{online}</span>
              Online
            </div>
            {offline > 0 && (
              <div className="summary-item">
                <WifiOff size={16} color="var(--danger)" />
                <span className="count">{offline}</span>
                Offline
              </div>
            )}
            {degraded > 0 && (
              <div className="summary-item">
                <Wifi size={16} color="var(--warning)" />
                <span className="count">{degraded}</span>
                Degraded
              </div>
            )}
          </div>

          {/* Agent cards */}
          <div className="agent-grid">
            {agents.map((a) => (
              <div className="agent-card" key={a.id}>
                <div className="agent-card-header">
                  <div>
                    <div className="agent-card-hostname">{a.hostname}</div>
                    <span className={`badge ${a.agentType === 'DcAgent' ? 'badge-info' : 'badge-warning'}`} style={{ marginTop: 4 }}>
                      {a.agentType === 'DcAgent' ? 'DC Agent' : 'Endpoint Agent'}
                    </span>
                  </div>
                  <StatusDot status={a.status as AgentStatus} />
                </div>
                <div className="agent-card-body">
                  <div className="agent-info-row">
                    <Globe size={14} />
                    <span>{a.ipAddress ?? 'No IP'}</span>
                  </div>
                  <div className="agent-info-row">
                    <Tag size={14} />
                    <span>{a.version ?? 'Unknown version'}</span>
                  </div>
                  <div className="agent-info-row">
                    <Clock size={14} style={{ color: getHeartbeatColor(a.lastHeartbeatAt) }} />
                    <span>
                      Heartbeat: <TimeAgo date={a.lastHeartbeatAt} fallback="Never" />
                    </span>
                  </div>
                  <div className="agent-info-row">
                    <Monitor size={14} />
                    <span>
                      Registered: <TimeAgo date={a.registeredAt} />
                    </span>
                  </div>
                </div>
              </div>
            ))}
          </div>
        </>
      )}
    </div>
  );
}
