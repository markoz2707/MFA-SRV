import { useEffect, useState, useCallback } from 'react';
import { getBackups, createBackup, requestRestore, confirmRestore, getBackupDownloadUrl } from '../api';
import type { BackupFileInfo } from '../types';
import { Database, Download, RotateCcw, Plus, HardDrive } from 'lucide-react';
import PageHeader from '../components/PageHeader';
import LoadingSpinner from '../components/LoadingSpinner';
import EmptyState from '../components/EmptyState';
import ConfirmDialog from '../components/ConfirmDialog';
import TimeAgo from '../components/TimeAgo';
import { useAppToast } from '../App';

function formatBytes(bytes: number): string {
  if (bytes === 0) return '0 B';
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + ' ' + sizes[i];
}

export default function Backups() {
  const { showSuccess, showError } = useAppToast();
  const [backups, setBackups] = useState<BackupFileInfo[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [creating, setCreating] = useState(false);

  // Restore state
  const [restoreTarget, setRestoreTarget] = useState<string | null>(null);
  const [restoreToken, setRestoreToken] = useState<string | null>(null);
  const [restoring, setRestoring] = useState(false);

  const load = useCallback(() => {
    setLoading(true);
    setError('');
    getBackups()
      .then((res) => setBackups(res.data))
      .catch((e) => setError(e.message))
      .finally(() => setLoading(false));
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  async function handleCreate() {
    setCreating(true);
    try {
      const res = await createBackup();
      showSuccess(`Backup created: ${res.fileName}`);
      load();
    } catch (e: unknown) {
      showError(e instanceof Error ? e.message : 'Backup creation failed');
    } finally {
      setCreating(false);
    }
  }

  async function handleRestoreRequest(fileName: string) {
    setRestoreTarget(fileName);
    setRestoring(true);
    try {
      const res = await requestRestore(fileName);
      setRestoreToken(res.token);
    } catch (e: unknown) {
      showError(e instanceof Error ? e.message : 'Restore request failed');
      setRestoreTarget(null);
    } finally {
      setRestoring(false);
    }
  }

  async function handleRestoreConfirm() {
    if (!restoreTarget || !restoreToken) return;
    setRestoring(true);
    try {
      const res = await confirmRestore(restoreTarget, restoreToken);
      showSuccess(res.message ?? 'Restore completed');
      setRestoreTarget(null);
      setRestoreToken(null);
      load();
    } catch (e: unknown) {
      showError(e instanceof Error ? e.message : 'Restore failed');
    } finally {
      setRestoring(false);
    }
  }

  return (
    <div className="page">
      <PageHeader title="Backups" subtitle="Database backup and restore management">
        <button className="btn btn-primary" onClick={handleCreate} disabled={creating}>
          <Plus size={14} /> {creating ? 'Creating...' : 'Create Backup'}
        </button>
      </PageHeader>

      {error && <div className="error-banner">{error}</div>}

      {loading ? (
        <LoadingSpinner message="Loading backups..." />
      ) : backups.length === 0 ? (
        <div className="card">
          <EmptyState
            icon={<Database size={40} strokeWidth={1.5} />}
            title="No backups found"
            description="Create a backup to protect your MfaSrv configuration and data."
            action={
              <button className="btn btn-primary" onClick={handleCreate} disabled={creating}>
                <Plus size={14} /> Create Backup
              </button>
            }
          />
        </div>
      ) : (
        <div className="card">
          <div className="backup-list">
            {backups.map((b) => (
              <div className="backup-item" key={b.fileName}>
                <div style={{ color: 'var(--primary)', flexShrink: 0 }}>
                  <HardDrive size={20} />
                </div>
                <div className="backup-item-info">
                  <div className="backup-item-name">{b.fileName}</div>
                  <div className="backup-item-meta">
                    <span>{formatBytes(b.sizeBytes)}</span>
                    <span>Created <TimeAgo date={b.createdUtc} /></span>
                  </div>
                </div>
                <div className="backup-item-actions">
                  <a
                    href={getBackupDownloadUrl(b.fileName)}
                    className="btn btn-outline btn-sm"
                    download
                  >
                    <Download size={13} /> Download
                  </a>
                  <button
                    className="btn btn-outline btn-sm"
                    onClick={() => handleRestoreRequest(b.fileName)}
                  >
                    <RotateCcw size={13} /> Restore
                  </button>
                </div>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Two-step restore confirmation */}
      <ConfirmDialog
        open={restoreTarget !== null && restoreToken !== null}
        title="Confirm Restore"
        message={`You are about to restore the database from "${restoreTarget}". This will overwrite the current database. This action cannot be undone. Are you absolutely sure?`}
        confirmLabel={restoring ? 'Restoring...' : 'Restore Now'}
        danger
        onConfirm={handleRestoreConfirm}
        onCancel={() => {
          setRestoreTarget(null);
          setRestoreToken(null);
        }}
      />
    </div>
  );
}
