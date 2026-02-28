import { useEffect, useState, useCallback } from 'react';
import {
  getPolicies,
  createPolicy,
  updatePolicy,
  deletePolicy,
  togglePolicy,
} from '../api';
import type {
  Policy,
  CreatePolicyRequest,
  RuleGroupRequest,
  RuleRequest,
  ActionRequest,
  FailoverMode,
  PolicyRuleType,
  PolicyActionType,
  MfaMethod,
} from '../types';
import { Filter, Zap, X, Plus, Edit3, Trash2 } from 'lucide-react';
import PageHeader from '../components/PageHeader';
import LoadingSpinner from '../components/LoadingSpinner';
import EmptyState from '../components/EmptyState';
import ConfirmDialog from '../components/ConfirmDialog';
import { useAppToast } from '../App';

const FAILOVER_MODES: FailoverMode[] = ['FailOpen', 'FailClose', 'CachedOnly'];
const RULE_TYPES: PolicyRuleType[] = [
  'SourceUser', 'SourceGroup', 'SourceIp', 'SourceOu',
  'TargetResource', 'AuthProtocol', 'TimeWindow', 'RiskScore',
];
const ACTION_TYPES: PolicyActionType[] = ['RequireMfa', 'Deny', 'Allow', 'AlertOnly'];
const MFA_METHODS: (MfaMethod | '')[] = ['', 'Totp', 'Push', 'Fido2', 'FortiToken', 'Sms', 'Email'];

function blankRule(): RuleRequest {
  return { ruleType: 'SourceGroup', operator: 'Equals', value: '', negate: false };
}

function blankRuleGroup(order: number): RuleGroupRequest {
  return { order, rules: [blankRule()] };
}

function blankAction(): ActionRequest {
  return { actionType: 'RequireMfa', requiredMethod: null };
}

function blankForm(): CreatePolicyRequest {
  return {
    name: '',
    description: null,
    isEnabled: true,
    priority: 100,
    failoverMode: 'FailOpen',
    ruleGroups: [blankRuleGroup(0)],
    actions: [blankAction()],
  };
}

function policyToForm(p: Policy): CreatePolicyRequest {
  return {
    name: p.name,
    description: p.description,
    isEnabled: p.isEnabled,
    priority: p.priority,
    failoverMode: p.failoverMode,
    ruleGroups: p.ruleGroups.map((g) => ({
      order: g.order,
      rules: g.rules.map((r) => ({
        ruleType: r.ruleType,
        operator: r.operator,
        value: r.value,
        negate: r.negate,
      })),
    })),
    actions: p.actions.map((a) => ({
      actionType: a.actionType,
      requiredMethod: a.requiredMethod,
    })),
  };
}

export default function Policies() {
  const { showSuccess, showError } = useAppToast();
  const [policies, setPolicies] = useState<Policy[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  // Modal state
  const [showModal, setShowModal] = useState(false);
  const [editId, setEditId] = useState<string | null>(null);
  const [form, setForm] = useState<CreatePolicyRequest>(blankForm());
  const [saving, setSaving] = useState(false);
  const [formError, setFormError] = useState('');

  // Confirm delete
  const [deleteTarget, setDeleteTarget] = useState<string | null>(null);

  const load = useCallback(() => {
    setLoading(true);
    setError('');
    getPolicies()
      .then(setPolicies)
      .catch((e) => setError(e.message))
      .finally(() => setLoading(false));
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  function openCreate() {
    setEditId(null);
    setForm(blankForm());
    setFormError('');
    setShowModal(true);
  }

  function openEdit(p: Policy) {
    setEditId(p.id);
    setForm(policyToForm(p));
    setFormError('');
    setShowModal(true);
  }

  async function handleSave() {
    if (!form.name.trim()) {
      setFormError('Name is required');
      return;
    }
    setSaving(true);
    setFormError('');
    try {
      if (editId) {
        await updatePolicy(editId, form);
        showSuccess('Policy updated');
      } else {
        await createPolicy(form);
        showSuccess('Policy created');
      }
      setShowModal(false);
      load();
    } catch (e: unknown) {
      setFormError(e instanceof Error ? e.message : 'Save failed');
    } finally {
      setSaving(false);
    }
  }

  async function handleDelete() {
    if (!deleteTarget) return;
    try {
      await deletePolicy(deleteTarget);
      showSuccess('Policy deleted');
      setDeleteTarget(null);
      load();
    } catch (e: unknown) {
      showError(e instanceof Error ? e.message : 'Delete failed');
      setDeleteTarget(null);
    }
  }

  async function handleToggle(id: string) {
    try {
      const result = await togglePolicy(id);
      showSuccess(result.isEnabled ? 'Policy enabled' : 'Policy disabled');
      load();
    } catch (e: unknown) {
      showError(e instanceof Error ? e.message : 'Toggle failed');
    }
  }

  // Form mutation helpers
  function updateField<K extends keyof CreatePolicyRequest>(key: K, value: CreatePolicyRequest[K]) {
    setForm((f) => ({ ...f, [key]: value }));
  }

  function addRuleGroup() {
    const groups = form.ruleGroups ?? [];
    setForm((f) => ({
      ...f,
      ruleGroups: [...groups, blankRuleGroup(groups.length)],
    }));
  }

  function removeRuleGroup(gi: number) {
    setForm((f) => ({
      ...f,
      ruleGroups: (f.ruleGroups ?? []).filter((_, i) => i !== gi),
    }));
  }

  function addRule(gi: number) {
    setForm((f) => {
      const groups = [...(f.ruleGroups ?? [])];
      groups[gi] = { ...groups[gi], rules: [...(groups[gi].rules ?? []), blankRule()] };
      return { ...f, ruleGroups: groups };
    });
  }

  function removeRule(gi: number, ri: number) {
    setForm((f) => {
      const groups = [...(f.ruleGroups ?? [])];
      groups[gi] = {
        ...groups[gi],
        rules: (groups[gi].rules ?? []).filter((_, i) => i !== ri),
      };
      return { ...f, ruleGroups: groups };
    });
  }

  function updateRule(gi: number, ri: number, patch: Partial<RuleRequest>) {
    setForm((f) => {
      const groups = [...(f.ruleGroups ?? [])];
      const rules = [...(groups[gi].rules ?? [])];
      rules[ri] = { ...rules[ri], ...patch };
      groups[gi] = { ...groups[gi], rules };
      return { ...f, ruleGroups: groups };
    });
  }

  function addAction() {
    setForm((f) => ({
      ...f,
      actions: [...(f.actions ?? []), blankAction()],
    }));
  }

  function removeAction(ai: number) {
    setForm((f) => ({
      ...f,
      actions: (f.actions ?? []).filter((_, i) => i !== ai),
    }));
  }

  function updateAction(ai: number, patch: Partial<ActionRequest>) {
    setForm((f) => {
      const actions = [...(f.actions ?? [])];
      actions[ai] = { ...actions[ai], ...patch };
      return { ...f, actions };
    });
  }

  return (
    <div className="page">
      <PageHeader title="Policies" subtitle="Conditional MFA policies evaluated by priority">
        <button className="btn btn-primary" onClick={openCreate}>
          <Plus size={14} /> Create Policy
        </button>
      </PageHeader>

      {error && <div className="error-banner">{error}</div>}

      <div className="card">
        <div className="table-container">
          {loading ? (
            <LoadingSpinner message="Loading policies..." />
          ) : policies.length === 0 ? (
            <EmptyState
              title="No policies configured"
              description="Create a policy to start enforcing MFA rules."
              action={
                <button className="btn btn-primary" onClick={openCreate}>
                  <Plus size={14} /> Create Policy
                </button>
              }
            />
          ) : (
            <table>
              <thead>
                <tr>
                  <th>Priority</th>
                  <th>Name</th>
                  <th>Status</th>
                  <th>Failover</th>
                  <th>Rule Groups</th>
                  <th>Actions</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                {policies.map((p) => (
                  <tr key={p.id}>
                    <td>
                      <span className="badge badge-neutral" style={{ fontWeight: 700, minWidth: 30, textAlign: 'center' }}>
                        {p.priority}
                      </span>
                    </td>
                    <td>
                      <strong>{p.name}</strong>
                      {p.description && (
                        <div className="text-secondary" style={{ fontSize: 11 }}>
                          {p.description}
                        </div>
                      )}
                    </td>
                    <td>
                      <span
                        className={`badge ${p.isEnabled ? 'badge-success' : 'badge-neutral'}`}
                      >
                        {p.isEnabled ? 'Enabled' : 'Disabled'}
                      </span>
                    </td>
                    <td>
                      <span className="badge badge-info">{p.failoverMode}</span>
                    </td>
                    <td>{p.ruleGroups.length}</td>
                    <td>
                      {p.actions.map((a, i) => (
                        <span key={i} className="badge badge-warning" style={{ marginRight: 4 }}>
                          {a.actionType}
                          {a.requiredMethod ? ` (${a.requiredMethod})` : ''}
                        </span>
                      ))}
                    </td>
                    <td>
                      <div className="flex-gap-8">
                        <button
                          className="btn btn-outline btn-sm"
                          onClick={() => handleToggle(p.id)}
                        >
                          {p.isEnabled ? 'Disable' : 'Enable'}
                        </button>
                        <button
                          className="btn btn-outline btn-sm"
                          onClick={() => openEdit(p)}
                          title="Edit"
                        >
                          <Edit3 size={13} />
                        </button>
                        <button
                          className="btn btn-danger btn-sm"
                          onClick={() => setDeleteTarget(p.id)}
                          title="Delete"
                        >
                          <Trash2 size={13} />
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      </div>

      {/* Create / Edit modal */}
      {showModal && (
        <div className="modal-overlay" onClick={() => setShowModal(false)}>
          <div className="modal" onClick={(e) => e.stopPropagation()}>
            <div className="modal-header">
              <h3>{editId ? 'Edit Policy' : 'Create Policy'}</h3>
              <button className="btn btn-outline btn-sm btn-icon" onClick={() => setShowModal(false)}>
                <X size={16} />
              </button>
            </div>
            <div className="modal-body">
              {formError && <div className="error-banner">{formError}</div>}

              <div className="form-row">
                <div className="form-group">
                  <label>Name</label>
                  <input
                    type="text"
                    className="form-control"
                    value={form.name}
                    onChange={(e) => updateField('name', e.target.value)}
                  />
                </div>
                <div className="form-group">
                  <label>Priority</label>
                  <input
                    type="number"
                    className="form-control"
                    value={form.priority}
                    onChange={(e) => updateField('priority', Number(e.target.value))}
                  />
                </div>
              </div>

              <div className="form-group">
                <label>Description</label>
                <textarea
                  className="form-control"
                  value={form.description ?? ''}
                  onChange={(e) => updateField('description', e.target.value || null)}
                />
              </div>

              <div className="form-row">
                <div className="form-group">
                  <label>Failover Mode</label>
                  <select
                    className="form-control"
                    value={form.failoverMode}
                    onChange={(e) => updateField('failoverMode', e.target.value as FailoverMode)}
                  >
                    {FAILOVER_MODES.map((m) => (
                      <option key={m} value={m}>{m}</option>
                    ))}
                  </select>
                </div>
                <div className="form-group" style={{ display: 'flex', alignItems: 'flex-end', paddingBottom: 16 }}>
                  <label className="toggle-label">
                    <input
                      type="checkbox"
                      checked={form.isEnabled}
                      onChange={(e) => updateField('isEnabled', e.target.checked)}
                    />
                    Enabled
                  </label>
                </div>
              </div>

              {/* Rule Groups */}
              <h4 className="mb-8" style={{ fontSize: 14, fontWeight: 600, display: 'flex', alignItems: 'center', gap: 6 }}>
                <Filter size={14} /> Rule Groups
              </h4>
              <p className="text-secondary mb-8" style={{ fontSize: 12 }}>
                Rules within a group are AND-combined. Groups are OR-combined.
              </p>

              {(form.ruleGroups ?? []).map((group, gi) => (
                <div className="rule-group-card" key={gi}>
                  <div className="rule-group-header">
                    <span>Group {gi + 1} (order {group.order})</span>
                    <button
                      className="btn btn-danger btn-sm"
                      onClick={() => removeRuleGroup(gi)}
                      type="button"
                    >
                      <X size={12} /> Remove
                    </button>
                  </div>

                  {(group.rules ?? []).map((rule, ri) => (
                    <div className="rule-row" key={ri}>
                      <select
                        className="form-control"
                        value={rule.ruleType}
                        onChange={(e) => updateRule(gi, ri, { ruleType: e.target.value as PolicyRuleType })}
                      >
                        {RULE_TYPES.map((t) => (
                          <option key={t} value={t}>{t}</option>
                        ))}
                      </select>
                      <select
                        className="form-control"
                        style={{ maxWidth: 120 }}
                        value={rule.operator}
                        onChange={(e) => updateRule(gi, ri, { operator: e.target.value })}
                      >
                        <option value="Equals">Equals</option>
                        <option value="Contains">Contains</option>
                        <option value="StartsWith">StartsWith</option>
                        <option value="Regex">Regex</option>
                      </select>
                      <input
                        type="text"
                        className="form-control"
                        placeholder="Value"
                        value={rule.value}
                        onChange={(e) => updateRule(gi, ri, { value: e.target.value })}
                      />
                      <label className="toggle-label" style={{ minWidth: 70 }}>
                        <input
                          type="checkbox"
                          checked={rule.negate}
                          onChange={(e) => updateRule(gi, ri, { negate: e.target.checked })}
                        />
                        Negate
                      </label>
                      <button
                        className="btn btn-outline btn-sm btn-icon"
                        onClick={() => removeRule(gi, ri)}
                        type="button"
                      >
                        <X size={14} />
                      </button>
                    </div>
                  ))}

                  <button
                    className="btn btn-outline btn-sm mt-8"
                    onClick={() => addRule(gi)}
                    type="button"
                  >
                    <Plus size={12} /> Add Rule
                  </button>
                </div>
              ))}

              <button
                className="btn btn-outline btn-sm mb-16"
                onClick={addRuleGroup}
                type="button"
              >
                <Plus size={12} /> Add Rule Group
              </button>

              {/* Actions */}
              <h4 className="mb-8" style={{ fontSize: 14, fontWeight: 600, display: 'flex', alignItems: 'center', gap: 6 }}>
                <Zap size={14} /> Actions
              </h4>

              {(form.actions ?? []).map((action, ai) => (
                <div className="action-row" key={ai}>
                  <select
                    className="form-control"
                    value={action.actionType}
                    onChange={(e) => updateAction(ai, { actionType: e.target.value as PolicyActionType })}
                  >
                    {ACTION_TYPES.map((t) => (
                      <option key={t} value={t}>{t}</option>
                    ))}
                  </select>
                  <select
                    className="form-control"
                    value={action.requiredMethod ?? ''}
                    onChange={(e) =>
                      updateAction(ai, {
                        requiredMethod: e.target.value ? (e.target.value as MfaMethod) : null,
                      })
                    }
                  >
                    {MFA_METHODS.map((m) => (
                      <option key={m} value={m}>{m || '(any method)'}</option>
                    ))}
                  </select>
                  <button
                    className="btn btn-outline btn-sm btn-icon"
                    onClick={() => removeAction(ai)}
                    type="button"
                  >
                    <X size={14} />
                  </button>
                </div>
              ))}

              <button
                className="btn btn-outline btn-sm mb-16"
                onClick={addAction}
                type="button"
              >
                <Plus size={12} /> Add Action
              </button>
            </div>
            <div className="modal-footer">
              <button className="btn btn-outline" onClick={() => setShowModal(false)}>
                Cancel
              </button>
              <button
                className="btn btn-primary"
                onClick={handleSave}
                disabled={saving}
              >
                {saving ? 'Saving...' : editId ? 'Update Policy' : 'Create Policy'}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Confirm delete */}
      <ConfirmDialog
        open={deleteTarget !== null}
        title="Delete Policy"
        message="Are you sure you want to delete this policy? This action cannot be undone."
        confirmLabel="Delete"
        danger
        onConfirm={handleDelete}
        onCancel={() => setDeleteTarget(null)}
      />
    </div>
  );
}
