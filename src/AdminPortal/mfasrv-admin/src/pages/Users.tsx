import { useEffect, useState, useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
import { getUsers, triggerLdapSync } from '../api';
import type { UserListItem, PagedResult } from '../types';
import { Search, ShieldCheck, RefreshCw } from 'lucide-react';
import PageHeader from '../components/PageHeader';
import Pagination from '../components/Pagination';
import LoadingSpinner from '../components/LoadingSpinner';
import TimeAgo from '../components/TimeAgo';
import { useAppToast } from '../App';

function getInitials(name: string): string {
  return name
    .split(/\s+/)
    .map((w) => w[0])
    .filter(Boolean)
    .slice(0, 2)
    .join('')
    .toUpperCase();
}

export default function Users() {
  const navigate = useNavigate();
  const { showSuccess, showError } = useAppToast();
  const [result, setResult] = useState<PagedResult<UserListItem> | null>(null);
  const [page, setPage] = useState(1);
  const [search, setSearch] = useState('');
  const [searchInput, setSearchInput] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [syncing, setSyncing] = useState(false);

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
    try {
      const res = await triggerLdapSync();
      showSuccess(res.message ?? 'LDAP sync completed');
      load();
    } catch (e: unknown) {
      showError(e instanceof Error ? e.message : 'Sync failed');
    } finally {
      setSyncing(false);
    }
  }

  return (
    <div className="page">
      <PageHeader title="Users" subtitle="Active Directory synced user accounts">
        <button className="btn btn-primary" onClick={handleSync} disabled={syncing}>
          <RefreshCw size={14} className={syncing ? 'spinning' : ''} />
          {syncing ? 'Syncing...' : 'Sync from AD'}
        </button>
      </PageHeader>

      {error && <div className="error-banner">{error}</div>}

      <div className="toolbar">
        <form onSubmit={handleSearch} className="form-inline">
          <div className="search-wrapper">
            <Search size={14} />
            <input
              type="text"
              className="form-control search-input"
              placeholder="Search by name or UPN..."
              value={searchInput}
              onChange={(e) => setSearchInput(e.target.value)}
            />
          </div>
          <button className="btn btn-outline" type="submit">Search</button>
        </form>
      </div>

      <div className="card">
        <div className="table-container">
          {loading ? (
            <LoadingSpinner message="Loading users..." />
          ) : (
            <table>
              <thead>
                <tr>
                  <th>User</th>
                  <th>Email</th>
                  <th>MFA</th>
                  <th>Last Auth</th>
                  <th>Enrollments</th>
                </tr>
              </thead>
              <tbody>
                {(result?.data ?? []).length === 0 ? (
                  <tr>
                    <td colSpan={5} className="text-muted" style={{ textAlign: 'center', padding: 32 }}>
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
                      <td>
                        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                          <div className="user-avatar">{getInitials(u.displayName)}</div>
                          <div>
                            <div style={{ fontWeight: 600 }}>{u.displayName}</div>
                            <div className="mono text-muted" style={{ fontSize: 11 }}>
                              {u.userPrincipalName}
                            </div>
                          </div>
                        </div>
                      </td>
                      <td>{u.email ?? '-'}</td>
                      <td>
                        {u.mfaEnabled ? (
                          <span className="badge badge-success" style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
                            <ShieldCheck size={12} /> Enabled
                          </span>
                        ) : (
                          <span className="badge badge-neutral">Disabled</span>
                        )}
                      </td>
                      <td><TimeAgo date={u.lastAuthAt} /></td>
                      <td>
                        {u.enrollmentCount > 0 ? (
                          <span className="badge badge-info">{u.enrollmentCount}</span>
                        ) : (
                          <span className="text-muted">0</span>
                        )}
                      </td>
                    </tr>
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
