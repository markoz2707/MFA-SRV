import { useEffect, useState, useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
import { getUsers, triggerLdapSync } from '../api';
import type { UserListItem, PagedResult } from '../types';

function formatTime(iso: string | null): string {
  if (!iso) return '-';
  return new Date(iso).toLocaleString();
}

export default function Users() {
  const navigate = useNavigate();
  const [result, setResult] = useState<PagedResult<UserListItem> | null>(null);
  const [page, setPage] = useState(1);
  const [search, setSearch] = useState('');
  const [searchInput, setSearchInput] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [syncing, setSyncing] = useState(false);
  const [syncMessage, setSyncMessage] = useState('');

  const pageSize = 50;

  const load = useCallback(() => {
    setLoading(true);
    setError('');
    getUsers(page, pageSize, search)
      .then(setResult)
      .catch((e) => setError(e.message))
      .finally(() => setLoading(false));
  }, [page, search]);

  useEffect(() => {
    load();
  }, [load]);

  function handleSearch(e: React.FormEvent) {
    e.preventDefault();
    setPage(1);
    setSearch(searchInput);
  }

  async function handleSync() {
    setSyncing(true);
    setSyncMessage('');
    try {
      const res = await triggerLdapSync();
      setSyncMessage(res.message ?? 'Sync completed');
      load();
    } catch (e: unknown) {
      setSyncMessage(e instanceof Error ? e.message : 'Sync failed');
    } finally {
      setSyncing(false);
    }
  }

  const totalPages = result ? Math.ceil(result.total / pageSize) : 0;

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <h2>Users</h2>
          <p>Active Directory synced user accounts</p>
        </div>
        <button className="btn btn-primary" onClick={handleSync} disabled={syncing}>
          {syncing ? 'Syncing...' : 'Sync from AD'}
        </button>
      </div>

      {syncMessage && <div className="success-banner">{syncMessage}</div>}
      {error && <div className="error-banner">{error}</div>}

      <div className="toolbar">
        <form onSubmit={handleSearch} className="form-inline">
          <input
            type="text"
            className="form-control search-input"
            placeholder="Search by name or UPN..."
            value={searchInput}
            onChange={(e) => setSearchInput(e.target.value)}
          />
          <button className="btn btn-outline" type="submit">Search</button>
        </form>
      </div>

      <div className="card">
        <div className="table-container">
          {loading ? (
            <div className="loading">Loading users...</div>
          ) : (
            <table>
              <thead>
                <tr>
                  <th>Name</th>
                  <th>UPN</th>
                  <th>Email</th>
                  <th>MFA Enabled</th>
                  <th>Last Auth</th>
                  <th>Enrollments</th>
                </tr>
              </thead>
              <tbody>
                {(result?.data ?? []).length === 0 ? (
                  <tr>
                    <td colSpan={6} className="text-muted" style={{ textAlign: 'center' }}>
                      No users found
                    </td>
                  </tr>
                ) : (
                  result!.data.map((u) => (
                    <tr
                      key={u.id}
                      className="clickable"
                      onClick={() => navigate(`/users/${u.id}`)}
                    >
                      <td>{u.displayName}</td>
                      <td className="mono">{u.userPrincipalName}</td>
                      <td>{u.email ?? '-'}</td>
                      <td>
                        {u.mfaEnabled ? (
                          <span className="badge badge-success">Yes</span>
                        ) : (
                          <span className="badge badge-neutral">No</span>
                        )}
                      </td>
                      <td className="mono">{formatTime(u.lastAuthAt)}</td>
                      <td>{u.enrollmentCount}</td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          )}
        </div>
      </div>

      {/* Pagination */}
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
