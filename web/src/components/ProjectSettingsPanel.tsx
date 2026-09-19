import { useEffect, useState } from 'react';
import { useAuth } from '../auth/AuthContext';
import { ApiError } from '../api/client';
import { projectsApi } from '../api/projects';
import { PROJECT_STATUS_LABELS, type Project, type Stakeholder, type UpdateProjectRequest } from '../api/types';

const STATUS_COUNT = Object.keys(PROJECT_STATUS_LABELS).length;

function toFormState(project: Project): UpdateProjectRequest {
  return {
    name: project.name,
    description: project.description ?? undefined,
    businessProblem: project.businessProblem ?? undefined,
    objectives: project.objectives ?? undefined,
    scope: project.scope ?? undefined,
    domain: project.domain ?? undefined,
    targetUsers: project.targetUsers ?? undefined,
    technologyPreferences: project.technologyPreferences ?? undefined,
    constraints: project.constraints ?? undefined,
    expectedTimeline: project.expectedTimeline ?? undefined,
  };
}

const emptyStakeholderForm = { name: '', roleInProject: '', contactInfo: '' };

export function ProjectSettingsPanel({ project, onProjectUpdated }: { project: Project; onProjectUpdated: (p: Project) => void }) {
  const { token, hasRole } = useAuth();
  const canManage = hasRole('Administrator') || hasRole('BusinessAnalyst');
  const canAdvanceStatus = canManage || hasRole('Reviewer');

  const [form, setForm] = useState<UpdateProjectRequest>(toFormState(project));
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const [stakeholders, setStakeholders] = useState<Stakeholder[] | null>(null);
  const [stakeholderForm, setStakeholderForm] = useState(emptyStakeholderForm);
  const [editingStakeholderId, setEditingStakeholderId] = useState<string | null>(null);

  useEffect(() => {
    setForm(toFormState(project));
  }, [project]);

  const loadStakeholders = async () => {
    setBusy('load-stakeholders');
    setError(null);
    try {
      setStakeholders(await projectsApi.getStakeholders(project.id, token));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to load stakeholders.');
    } finally {
      setBusy(null);
    }
  };

  const onSaveProject = async () => {
    setBusy('save-project');
    setError(null);
    try {
      onProjectUpdated(await projectsApi.update(project.id, form, token));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to update project.');
    } finally {
      setBusy(null);
    }
  };

  const onAdvanceStatus = async () => {
    setBusy('advance-status');
    setError(null);
    try {
      onProjectUpdated(await projectsApi.updateStatus(project.id, project.status + 1, token));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to advance status.');
    } finally {
      setBusy(null);
    }
  };

  const onSaveStakeholder = async () => {
    if (!stakeholderForm.name.trim()) return;
    setBusy('save-stakeholder');
    setError(null);
    try {
      const request = {
        name: stakeholderForm.name,
        roleInProject: stakeholderForm.roleInProject || undefined,
        contactInfo: stakeholderForm.contactInfo || undefined,
      };
      if (editingStakeholderId) {
        await projectsApi.updateStakeholder(project.id, editingStakeholderId, request, token);
      } else {
        await projectsApi.addStakeholder(project.id, request, token);
      }
      setStakeholderForm(emptyStakeholderForm);
      setEditingStakeholderId(null);
      await loadStakeholders();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to save stakeholder.');
    } finally {
      setBusy(null);
    }
  };

  const onEditStakeholder = (s: Stakeholder) => {
    setEditingStakeholderId(s.id);
    setStakeholderForm({ name: s.name, roleInProject: s.roleInProject ?? '', contactInfo: s.contactInfo ?? '' });
  };

  const onRemoveStakeholder = async (id: string) => {
    setBusy(`remove-${id}`);
    setError(null);
    try {
      await projectsApi.removeStakeholder(project.id, id, token);
      await loadStakeholders();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to remove stakeholder.');
    } finally {
      setBusy(null);
    }
  };

  return (
    <div className="card">
      <h2>Project Settings</h2>
      {error && <p className="error">{error}</p>}

      <div className="status-row">
        <span className="hint">Status:</span>
        <span className="status-pill">{PROJECT_STATUS_LABELS[project.status]}</span>
        {canAdvanceStatus && project.status < STATUS_COUNT - 1 && (
          <button type="button" disabled={busy === 'advance-status'} onClick={onAdvanceStatus}>
            {busy === 'advance-status' ? 'Advancing…' : `Advance to ${PROJECT_STATUS_LABELS[project.status + 1]}`}
          </button>
        )}
      </div>

      {canManage && (
        <div className="project-edit-form">
          <label>
            Name
            <input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} required />
          </label>
          <label>
            Description
            <textarea value={form.description ?? ''} onChange={(e) => setForm({ ...form, description: e.target.value })} rows={2} />
          </label>
          <label>
            Business problem
            <textarea value={form.businessProblem ?? ''} onChange={(e) => setForm({ ...form, businessProblem: e.target.value })} rows={2} />
          </label>
          <label>
            Objectives
            <textarea value={form.objectives ?? ''} onChange={(e) => setForm({ ...form, objectives: e.target.value })} rows={2} />
          </label>
          <label>
            Scope
            <textarea value={form.scope ?? ''} onChange={(e) => setForm({ ...form, scope: e.target.value })} rows={2} />
          </label>
          <label>
            Domain
            <input value={form.domain ?? ''} onChange={(e) => setForm({ ...form, domain: e.target.value })} />
          </label>
          <label>
            Target users
            <input value={form.targetUsers ?? ''} onChange={(e) => setForm({ ...form, targetUsers: e.target.value })} />
          </label>
          <label>
            Technology preferences
            <input value={form.technologyPreferences ?? ''} onChange={(e) => setForm({ ...form, technologyPreferences: e.target.value })} />
          </label>
          <label>
            Constraints
            <textarea value={form.constraints ?? ''} onChange={(e) => setForm({ ...form, constraints: e.target.value })} rows={2} />
          </label>
          <label>
            Expected timeline
            <input value={form.expectedTimeline ?? ''} onChange={(e) => setForm({ ...form, expectedTimeline: e.target.value })} />
          </label>
          <button type="button" disabled={!form.name.trim() || busy === 'save-project'} onClick={onSaveProject}>
            {busy === 'save-project' ? 'Saving…' : 'Save project details'}
          </button>
        </div>
      )}

      <h3>Stakeholders</h3>
      <button type="button" disabled={busy === 'load-stakeholders'} onClick={loadStakeholders}>
        {busy === 'load-stakeholders' ? 'Loading…' : stakeholders ? 'Refresh' : 'Load stakeholders'}
      </button>

      {stakeholders && (
        stakeholders.length === 0 ? (
          <p>No stakeholders yet.</p>
        ) : (
          <ul className="document-list">
            {stakeholders.map((s) => (
              <li key={s.id}>
                <span>{s.name}</span>
                <span className="hint">{s.roleInProject ?? '—'}</span>
                <span className="hint">{s.contactInfo ?? '—'}</span>
                {canManage && (
                  <>
                    <button type="button" onClick={() => onEditStakeholder(s)}>Edit</button>
                    <button type="button" disabled={busy === `remove-${s.id}`} onClick={() => onRemoveStakeholder(s.id)}>
                      {busy === `remove-${s.id}` ? 'Removing…' : 'Remove'}
                    </button>
                  </>
                )}
              </li>
            ))}
          </ul>
        )
      )}

      {canManage && (
        <div className="upload-row">
          <input
            type="text"
            placeholder="Name"
            value={stakeholderForm.name}
            onChange={(e) => setStakeholderForm({ ...stakeholderForm, name: e.target.value })}
          />
          <input
            type="text"
            placeholder="Role (e.g. Sponsor)"
            value={stakeholderForm.roleInProject}
            onChange={(e) => setStakeholderForm({ ...stakeholderForm, roleInProject: e.target.value })}
          />
          <input
            type="text"
            placeholder="Contact info"
            value={stakeholderForm.contactInfo}
            onChange={(e) => setStakeholderForm({ ...stakeholderForm, contactInfo: e.target.value })}
          />
          <button type="button" disabled={!stakeholderForm.name.trim() || busy === 'save-stakeholder'} onClick={onSaveStakeholder}>
            {busy === 'save-stakeholder' ? 'Saving…' : editingStakeholderId ? 'Update' : 'Add stakeholder'}
          </button>
          {editingStakeholderId && (
            <button type="button" onClick={() => { setEditingStakeholderId(null); setStakeholderForm(emptyStakeholderForm); }}>
              Cancel
            </button>
          )}
        </div>
      )}
    </div>
  );
}
