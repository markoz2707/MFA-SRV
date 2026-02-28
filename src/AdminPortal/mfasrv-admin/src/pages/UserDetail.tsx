import { useEffect, useState, useCallback } from 'react';
import { useParams, Link } from 'react-router-dom';
import { getUser, getUserEnrollments, revokeEnrollment, beginEnrollment } from '../api';
import type { User, EnrollmentListItem, MfaMethod, BeginEnrollmentResponse } from '../types';
import {
  Smartphone, Fingerprint, Key, MessageSquare, Mail, Shield,
  ChevronDown, ArrowLeft,
} from 'lucide-react';
import LoadingSpinner from '../components/LoadingSpinner';
import TimeAgo from '../components/TimeAgo';
import ConfirmDialog from '../components/ConfirmDialog';
import { useAppToast } from '../App';

const MFA_METHODS: { method: MfaMethod; label: string; icon: typeof Smartphone }[] = [
  { method: 'Totp', label: 'TOTP', icon: Smartphone },
  { method: 'Push', label: 'Push', icon: Smartphone },
  { method: 'Fido2', label: 'FIDO2', icon: Fingerprint },
  { method: 'FortiToken', label: 'FortiToken', icon: Key },
  { method: 'Sms', label: 'SMS', icon: MessageSquare },
  { method: 'Email', label: 'Email', icon: Mail },
];

function getMethodIcon(method: MfaMethod) {
  const entry = MFA_METHODS.find((m) => m.method === method);
  if (!entry) return <Key size={18} />;
  const Icon = entry.icon;
  return <Icon size={18} />;
}

function getInitials(name: string): string {
  return name.split(/\s+/).map((w) => w[0]).filter(Boolean).slice(0, 2).join('').toUpperCase();
}

function enrollmentStatusClass(status: string): string {
  switch (status) {
    case 'Active': return 'badge-success';
    case 'Pending': return 'badge-warning';
    case 'Revoked': return 'badge-danger';
    default: return 'badge-neutral';
  }
}

export default function UserDetail() {
  const { id } = useParams<{ id: string }>();
  const { showSuccess, showError } = useAppToast();
  const [user, setUser] = useState<User | null>(null);
  const [enrollments, setEnrollments] = useState<EnrollmentListItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  // Enrollment form
  const [enrollMethod, setEnrollMethod] = useState<MfaMethod>('Totp');
  const [enrollName, setEnrollName] = useState('');
  const [enrolling, setEnrolling] = useState(false);
  const [enrollResult, setEnrollResult] = useState<BeginEnrollmentResponse | null>(null);
  const [enrollError, setEnrollError] = useState('');

  // Confirm dialog
  const [revokeTarget, setRevokeTarget] = useState<string | null>(null);

  // Collapsible groups
  const [groupsOpen, setGroupsOpen] = useState(false);

  const loadUser = useCallback(() => {
    if (!id) return;
    setLoading(true);
    setError('');
    Promise.all([getUser(id), getUserEnrollments(id)])
      .then(([u, e]) => {
        setUser(u);
        setEnrollments(e);
      })
      .catch((e) => setError(e.message))
      .finally(() => setLoading(false));
  }, [id]);

  useEffect(() => {
    loadUser();
  }, [loadUser]);

  async function handleRevoke() {
    if (!revokeTarget) return;
    try {
      await revokeEnrollment(revokeTarget);
      showSuccess('Enrollment revoked');
      setRevokeTarget(null);
      loadUser();
    } catch (e: unknown) {
      showError(e instanceof Error ? e.message : 'Revoke failed');
      setRevokeTarget(null);
    }
  }

  async function handleBeginEnrollment(e: React.FormEvent) {
    e.preventDefault();
    if (!id) return;
    setEnrolling(true);
    setEnrollError('');
    setEnrollResult(null);
    try {
      const result = await beginEnrollment({
        userId: id,
        method: enrollMethod,
        friendlyName: enrollName || null,
      });
      setEnrollResult(result);
      showSuccess('Enrollment started');
    } catch (err: unknown) {
      setEnrollError(err instanceof Error ? err.message : 'Enrollment failed');
    } finally {
      setEnrolling(false);
    }
  }

  if (loading) return <div className="page"><LoadingSpinner message="Loading user..." /></div>;
  if (error && !user) return (
    <div className="page">
      <div className="error-banner">{error}</div>
      <Link to="/users" className="btn btn-outline mt-8"><ArrowLeft size={14} /> Back to Users</Link>
    </div>
  );
  if (!user) return null;

  return (
    <div className="page">
      {/* Breadcrumb */}
      <div style={{ marginBottom: 16 }}>
        <Link to="/users" style={{ display: 'inline-flex', alignItems: 'center', gap: 4, fontSize: 13, color: 'var(--text-secondary)' }}>
          <ArrowLeft size={14} /> Back to Users
        </Link>
      </div>

      {error && <div className="error-banner">{error}</div>}

      {/* Hero header */}
      <div className="user-hero">
        <div className="user-avatar user-avatar--lg">{getInitials(user.displayName)}</div>
        <div className="user-hero-info">
          <h2>{user.displayName}</h2>
          <div className="user-hero-badges">
            {user.isEnabled ? (
              <span className="badge badge-success">Enabled</span>
            ) : (
              <span className="badge badge-danger">Disabled</span>
            )}
            {user.mfaEnabled ? (
              <span className="badge badge-success" style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
                <Shield size={10} /> MFA Active
              </span>
            ) : (
              <span className="badge badge-neutral">MFA Inactive</span>
            )}
            <span className="badge badge-info">{enrollments.length} enrollment{enrollments.length !== 1 ? 's' : ''}</span>
          </div>
        </div>
      </div>

      {/* Two-column info */}
      <div className="info-columns mb-16">
        <div className="card" style={{ marginBottom: 0 }}>
          <div className="card-header"><h3>Account Details</h3></div>
          <div className="card-body">
            <dl>
              <div className="info-row"><dt>SAM Account Name</dt><dd className="mono">{user.samAccountName}</dd></div>
              <div className="info-row"><dt>User Principal Name</dt><dd className="mono">{user.userPrincipalName}</dd></div>
              <div className="info-row"><dt>Email</dt><dd>{user.email ?? '-'}</dd></div>
              <div className="info-row"><dt>Phone</dt><dd>{user.phoneNumber ?? '-'}</dd></div>
              <div className="info-row"><dt>Distinguished Name</dt><dd className="mono" style={{ fontSize: 11 }}>{user.distinguishedName}</dd></div>
            </dl>
          </div>
        </div>
        <div className="card" style={{ marginBottom: 0 }}>
          <div className="card-header"><h3>Authentication Info</h3></div>
          <div className="card-body">
            <dl>
              <div className="info-row"><dt>Last Authentication</dt><dd><TimeAgo date={user.lastAuthAt} /></dd></div>
              <div className="info-row"><dt>Last Synced</dt><dd><TimeAgo date={user.lastSyncAt} /></dd></div>
              <div className="info-row"><dt>Created</dt><dd><TimeAgo date={user.createdAt} /></dd></div>
              <div className="info-row"><dt>Updated</dt><dd><TimeAgo date={user.updatedAt} /></dd></div>
            </dl>
          </div>
        </div>
      </div>

      {/* Enrollment cards */}
      <div className="card">
        <div className="card-header">
          <h3>MFA Enrollments</h3>
        </div>
        <div className="card-body">
          {enrollments.length === 0 ? (
            <div className="empty-state" style={{ padding: '24px 0' }}>
              <p className="text-muted">No enrollments configured</p>
            </div>
          ) : (
            <div className="enrollment-grid">
              {enrollments.map((e) => (
                <div className="enrollment-card" key={e.id}>
                  <div className="enrollment-card-icon">
                    {getMethodIcon(e.method)}
                  </div>
                  <div className="enrollment-card-content">
                    <div className="enrollment-method">
                      {e.method}
                      <span className={`badge ${enrollmentStatusClass(e.status)}`} style={{ marginLeft: 6, verticalAlign: 'middle' }}>
                        {e.status}
                      </span>
                    </div>
                    <div className="enrollment-meta">
                      {e.friendlyName && <span>{e.friendlyName}</span>}
                      {e.lastUsedAt && <span> &middot; Last used <TimeAgo date={e.lastUsedAt} /></span>}
                      {!e.lastUsedAt && e.activatedAt && <span> &middot; Activated <TimeAgo date={e.activatedAt} /></span>}
                      {!e.lastUsedAt && !e.activatedAt && <span> &middot; Created <TimeAgo date={e.createdAt} /></span>}
                    </div>
                  </div>
                  <div className="enrollment-card-actions">
                    {(e.status === 'Active' || e.status === 'Pending') && (
                      <button
                        className="btn btn-danger btn-sm"
                        onClick={() => setRevokeTarget(e.id)}
                      >
                        Revoke
                      </button>
                    )}
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      </div>

      {/* Begin new enrollment */}
      <div className="card">
        <div className="card-header">
          <h3>Begin New Enrollment</h3>
        </div>
        <div className="card-body">
          {enrollError && <div className="error-banner">{enrollError}</div>}
          {enrollResult && (
            <div className="success-banner">
              Enrollment started (ID: {enrollResult.enrollmentId}).
              {enrollResult.qrCodeDataUri && (
                <div className="mt-8">
                  <img src={enrollResult.qrCodeDataUri} alt="QR Code" style={{ maxWidth: 200 }} />
                </div>
              )}
              {enrollResult.provisioningUri && (
                <div className="mt-8 mono" style={{ wordBreak: 'break-all', fontSize: 11 }}>
                  {enrollResult.provisioningUri}
                </div>
              )}
            </div>
          )}

          {/* Method selector grid */}
          <div className="method-grid">
            {MFA_METHODS.map((m) => {
              const Icon = m.icon;
              return (
                <div
                  key={m.method}
                  className={`method-grid-item${enrollMethod === m.method ? ' selected' : ''}`}
                  onClick={() => setEnrollMethod(m.method)}
                >
                  <Icon size={22} />
                  <span>{m.label}</span>
                </div>
              );
            })}
          </div>

          <form onSubmit={handleBeginEnrollment}>
            <div className="form-group">
              <label>Friendly Name (optional)</label>
              <input
                type="text"
                className="form-control"
                placeholder="e.g. Work Phone"
                value={enrollName}
                onChange={(e) => setEnrollName(e.target.value)}
                style={{ maxWidth: 400 }}
              />
            </div>
            <button className="btn btn-primary" type="submit" disabled={enrolling}>
              {enrolling ? 'Starting...' : 'Begin Enrollment'}
            </button>
          </form>
        </div>
      </div>

      {/* Group memberships - collapsible */}
      {user.groupMemberships && user.groupMemberships.length > 0 && (
        <div className="card">
          <div
            className={`collapsible-header${groupsOpen ? ' open' : ''}`}
            onClick={() => setGroupsOpen(!groupsOpen)}
          >
            <h3 style={{ fontSize: 15, fontWeight: 600, margin: 0 }}>
              Group Memberships ({user.groupMemberships.length})
            </h3>
            <ChevronDown size={18} />
          </div>
          {groupsOpen && (
            <div className="table-container">
              <table>
                <thead>
                  <tr>
                    <th>Group Name</th>
                    <th>SID</th>
                    <th>Distinguished Name</th>
                    <th>Synced</th>
                  </tr>
                </thead>
                <tbody>
                  {user.groupMemberships.map((g) => (
                    <tr key={g.id}>
                      <td><strong>{g.groupName}</strong></td>
                      <td className="mono">{g.groupSid}</td>
                      <td className="mono" style={{ fontSize: 11 }}>{g.groupDn}</td>
                      <td><TimeAgo date={g.syncedAt} /></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}

      {/* Confirm dialog */}
      <ConfirmDialog
        open={revokeTarget !== null}
        title="Revoke Enrollment"
        message="Are you sure you want to revoke this enrollment? The user will need to re-enroll."
        confirmLabel="Revoke"
        danger
        onConfirm={handleRevoke}
        onCancel={() => setRevokeTarget(null)}
      />
    </div>
  );
}
