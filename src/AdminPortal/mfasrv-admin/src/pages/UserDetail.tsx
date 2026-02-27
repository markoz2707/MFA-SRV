import { useEffect, useState, useCallback } from 'react';
import { useParams, Link } from 'react-router-dom';
import { getUser, getUserEnrollments, revokeEnrollment, beginEnrollment } from '../api';
import type { User, EnrollmentListItem, MfaMethod, BeginEnrollmentResponse } from '../types';

const MFA_METHODS: MfaMethod[] = ['Totp', 'Push', 'Fido2', 'FortiToken', 'Sms', 'Email'];

function formatTime(iso: string | null | undefined): string {
  if (!iso) return '-';
  return new Date(iso).toLocaleString();
}

export default function UserDetail() {
  const { id } = useParams<{ id: string }>();
  const [user, setUser] = useState<User | null>(null);
  const [enrollments, setEnrollments] = useState<EnrollmentListItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  // Enrollment form state
  const [enrollMethod, setEnrollMethod] = useState<MfaMethod>('Totp');
  const [enrollName, setEnrollName] = useState('');
  const [enrolling, setEnrolling] = useState(false);
  const [enrollResult, setEnrollResult] = useState<BeginEnrollmentResponse | null>(null);
  const [enrollError, setEnrollError] = useState('');

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

  async function handleRevoke(enrollmentId: string) {
    if (!confirm('Revoke this enrollment?')) return;
    try {
      await revokeEnrollment(enrollmentId);
      loadUser();
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Revoke failed');
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
    } catch (err: unknown) {
      setEnrollError(err instanceof Error ? err.message : 'Enrollment failed');
    } finally {
      setEnrolling(false);
    }
  }

  if (loading) return <div className="page"><div className="loading">Loading user...</div></div>;
  if (error && !user) return <div className="page"><div className="error-banner">{error}</div><Link to="/users">Back to Users</Link></div>;
  if (!user) return null;

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <h2>{user.displayName}</h2>
          <p>
            <Link to="/users">Users</Link> / {user.displayName}
          </p>
        </div>
      </div>

      {error && <div className="error-banner">{error}</div>}

      {/* ── User info ── */}
      <div className="card">
        <div className="card-header">
          <h3>User Information</h3>
        </div>
        <div className="card-body">
          <dl>
            <div className="info-row">
              <dt>SAM Account Name</dt>
              <dd className="mono">{user.samAccountName}</dd>
            </div>
            <div className="info-row">
              <dt>User Principal Name</dt>
              <dd className="mono">{user.userPrincipalName}</dd>
            </div>
            <div className="info-row">
              <dt>Email</dt>
              <dd>{user.email ?? '-'}</dd>
            </div>
            <div className="info-row">
              <dt>Phone</dt>
              <dd>{user.phoneNumber ?? '-'}</dd>
            </div>
            <div className="info-row">
              <dt>Distinguished Name</dt>
              <dd className="mono" style={{ fontSize: 11 }}>{user.distinguishedName}</dd>
            </div>
            <div className="info-row">
              <dt>Enabled</dt>
              <dd>
                {user.isEnabled ? (
                  <span className="badge badge-success">Yes</span>
                ) : (
                  <span className="badge badge-danger">No</span>
                )}
              </dd>
            </div>
            <div className="info-row">
              <dt>MFA Enabled</dt>
              <dd>
                {user.mfaEnabled ? (
                  <span className="badge badge-success">Yes</span>
                ) : (
                  <span className="badge badge-neutral">No</span>
                )}
              </dd>
            </div>
            <div className="info-row">
              <dt>Last Authentication</dt>
              <dd>{formatTime(user.lastAuthAt)}</dd>
            </div>
            <div className="info-row">
              <dt>Last Synced</dt>
              <dd>{formatTime(user.lastSyncAt)}</dd>
            </div>
            <div className="info-row">
              <dt>Created</dt>
              <dd>{formatTime(user.createdAt)}</dd>
            </div>
          </dl>
        </div>
      </div>

      {/* ── Enrollments ── */}
      <div className="card">
        <div className="card-header">
          <h3>MFA Enrollments</h3>
        </div>
        <div className="table-container">
          <table>
            <thead>
              <tr>
                <th>Method</th>
                <th>Status</th>
                <th>Name</th>
                <th>Device</th>
                <th>Created</th>
                <th>Activated</th>
                <th>Last Used</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {enrollments.length === 0 ? (
                <tr>
                  <td colSpan={8} className="text-muted" style={{ textAlign: 'center' }}>
                    No enrollments
                  </td>
                </tr>
              ) : (
                enrollments.map((e) => (
                  <tr key={e.id}>
                    <td>
                      <span className="badge badge-info">{e.method}</span>
                    </td>
                    <td>
                      <span
                        className={`badge ${
                          e.status === 'Active'
                            ? 'badge-success'
                            : e.status === 'Pending'
                            ? 'badge-warning'
                            : e.status === 'Revoked'
                            ? 'badge-danger'
                            : 'badge-neutral'
                        }`}
                      >
                        {e.status}
                      </span>
                    </td>
                    <td>{e.friendlyName ?? '-'}</td>
                    <td className="mono">{e.deviceIdentifier ?? '-'}</td>
                    <td className="mono">{formatTime(e.createdAt)}</td>
                    <td className="mono">{formatTime(e.activatedAt)}</td>
                    <td className="mono">{formatTime(e.lastUsedAt)}</td>
                    <td>
                      {(e.status === 'Active' || e.status === 'Pending') && (
                        <button
                          className="btn btn-danger btn-sm"
                          onClick={() => handleRevoke(e.id)}
                        >
                          Revoke
                        </button>
                      )}
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </div>

      {/* ── Begin new enrollment ── */}
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
          <form onSubmit={handleBeginEnrollment}>
            <div className="form-row">
              <div className="form-group">
                <label>MFA Method</label>
                <select
                  className="form-control"
                  value={enrollMethod}
                  onChange={(e) => setEnrollMethod(e.target.value as MfaMethod)}
                >
                  {MFA_METHODS.map((m) => (
                    <option key={m} value={m}>{m}</option>
                  ))}
                </select>
              </div>
              <div className="form-group">
                <label>Friendly Name (optional)</label>
                <input
                  type="text"
                  className="form-control"
                  placeholder="e.g. Work Phone"
                  value={enrollName}
                  onChange={(e) => setEnrollName(e.target.value)}
                />
              </div>
            </div>
            <button className="btn btn-primary" type="submit" disabled={enrolling}>
              {enrolling ? 'Starting...' : 'Begin Enrollment'}
            </button>
          </form>
        </div>
      </div>

      {/* ── Group memberships ── */}
      {user.groupMemberships && user.groupMemberships.length > 0 && (
        <div className="card">
          <div className="card-header">
            <h3>Group Memberships</h3>
          </div>
          <div className="table-container">
            <table>
              <thead>
                <tr>
                  <th>Group Name</th>
                  <th>SID</th>
                  <th>Distinguished Name</th>
                  <th>Synced At</th>
                </tr>
              </thead>
              <tbody>
                {user.groupMemberships.map((g) => (
                  <tr key={g.id}>
                    <td>{g.groupName}</td>
                    <td className="mono">{g.groupSid}</td>
                    <td className="mono" style={{ fontSize: 11 }}>{g.groupDn}</td>
                    <td className="mono">{formatTime(g.syncedAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </div>
  );
}
