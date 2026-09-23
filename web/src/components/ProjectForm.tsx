import { useState, type ReactNode } from 'react';
import type { CreateProjectRequest, Project } from '../api/types';
import { TextField } from './ui/TextField';
import { TextArea } from './ui/TextArea';
import { IconAlert, IconArrowLeft, IconArrowRight, IconCheck } from './icons';

/*
 * One definition of the project form, used by:
 *   - Create  (/projects/new)          → ProjectWizard, one step at a time
 *   - Edit    (/projects/:id/edit)     → ProjectEditor, every section visible
 *   - Gathering (/projects/:id/gathering) → ProjectEditor
 * The fields are exactly those of CreateProjectRequest / UpdateProjectRequest.
 */

export type ProjectValues = Required<{ [K in keyof CreateProjectRequest]: string }>;

export type FieldKey = keyof ProjectValues;

interface FieldDef {
  key: FieldKey;
  label: string;
  kind: 'text' | 'area';
  placeholder?: string;
  hint?: string;
  required?: boolean;
  rows?: number;
}

export interface SectionDef {
  key: string;
  title: string;
  description: string;
  fields: FieldDef[];
}

export const SECTIONS: SectionDef[] = [
  {
    key: 'context',
    title: 'Project context',
    description: 'What the project is called and the problem it exists to solve.',
    fields: [
      { key: 'name', label: 'Project name', kind: 'text', placeholder: 'e.g. Medical Clinic Management System', required: true },
      { key: 'description', label: 'Description', kind: 'area', rows: 3, placeholder: 'A short summary anyone on the team can read in ten seconds.' },
      { key: 'businessProblem', label: 'Business problem', kind: 'area', rows: 4, placeholder: 'What pain point or opportunity is being addressed?', hint: 'Describe the problem, not the solution.' },
    ],
  },
  {
    key: 'objectives',
    title: 'Objectives',
    description: 'What success looks like once the project is delivered.',
    fields: [{ key: 'objectives', label: 'Objectives', kind: 'area', rows: 4, placeholder: 'Measurable outcomes the project should deliver.', hint: 'One objective per line works well.' }],
  },
  {
    key: 'scope',
    title: 'Scope',
    description: 'Where the project starts and stops.',
    fields: [{ key: 'scope', label: 'Scope', kind: 'area', rows: 4, placeholder: 'In scope: …\nOut of scope: …', hint: 'Say what is in scope and what is explicitly out.' }],
  },
  {
    key: 'domain',
    title: 'Domain & target users',
    description: 'The business area and the people who will use the result.',
    fields: [
      { key: 'domain', label: 'Domain', kind: 'text', placeholder: 'e.g. Healthcare, Retail' },
      { key: 'targetUsers', label: 'Target users', kind: 'text', placeholder: 'e.g. Clinic administrators, patients' },
    ],
  },
  {
    key: 'technology',
    title: 'Technology & constraints',
    description: 'The environment the solution has to live in.',
    fields: [
      { key: 'technologyPreferences', label: 'Technology preferences', kind: 'text', placeholder: 'e.g. .NET, React, Azure' },
      { key: 'constraints', label: 'Constraints', kind: 'area', rows: 3, placeholder: 'Budget, compliance, legacy systems, fixed dates…' },
    ],
  },
  {
    key: 'timeline',
    title: 'Expected timeline',
    description: 'When the project needs to land.',
    fields: [{ key: 'expectedTimeline', label: 'Expected timeline', kind: 'text', placeholder: 'e.g. 6 months, go-live Q3' }],
  },
];

const ALL_FIELDS: FieldKey[] = SECTIONS.flatMap((s) => s.fields.map((f) => f.key));

export const EMPTY_VALUES: ProjectValues = Object.fromEntries(ALL_FIELDS.map((k) => [k, ''])) as ProjectValues;

export function valuesFromProject(p: Project): ProjectValues {
  return Object.fromEntries(ALL_FIELDS.map((k) => [k, (p[k] as string | null | undefined) ?? ''])) as ProjectValues;
}

/** Empty optional fields are omitted, exactly as the previous create/update calls did. */
export function toRequest(values: ProjectValues): CreateProjectRequest {
  const req: Record<string, string | undefined> = {};
  for (const k of ALL_FIELDS) req[k] = values[k].trim() === '' ? undefined : values[k];
  req.name = values.name;
  return req as unknown as CreateProjectRequest;
}

export function isDirty(a: ProjectValues, b: ProjectValues): boolean {
  return ALL_FIELDS.some((k) => a[k] !== b[k]);
}

const filled = (v: string) => v.trim() !== '';

export function sectionCompletion(section: SectionDef, values: ProjectValues) {
  const done = section.fields.filter((f) => filled(values[f.key])).length;
  return { done, total: section.fields.length, complete: done === section.fields.length };
}

export function overallCompletion(values: ProjectValues) {
  const done = ALL_FIELDS.filter((k) => filled(values[k])).length;
  return { done, total: ALL_FIELDS.length, percent: Math.round((done / ALL_FIELDS.length) * 100) };
}

function nameError(values: ProjectValues, show: boolean) {
  return show && !filled(values.name) ? 'Project name is required.' : undefined;
}

function Fields({ section, values, onChange, showErrors }: { section: SectionDef; values: ProjectValues; onChange: (k: FieldKey, v: string) => void; showErrors: boolean }) {
  return (
    <div className="pf-fields">
      {section.fields.map((f) =>
        f.kind === 'text' ? (
          <TextField
            key={f.key}
            id={`pf-${f.key}`}
            label={f.label + (f.required ? ' *' : '')}
            value={values[f.key]}
            onChange={(v) => onChange(f.key, v)}
            placeholder={f.placeholder}
            hint={f.hint}
            error={f.key === 'name' ? nameError(values, showErrors) : undefined}
            required={f.required}
            autoFocus={f.key === 'name'}
          />
        ) : (
          <TextArea key={f.key} id={`pf-${f.key}`} label={f.label} value={values[f.key]} onChange={(v) => onChange(f.key, v)} rows={f.rows} placeholder={f.placeholder} hint={f.hint} />
        ),
      )}
    </div>
  );
}

function SectionCard({ section, values, children, id }: { section: SectionDef; values: ProjectValues; children: ReactNode; id?: string }) {
  const c = sectionCompletion(section, values);
  return (
    <section className="pf-card" id={id}>
      <header className="pf-card-head">
        <div>
          <h2>{section.title}</h2>
          <p>{section.description}</p>
        </div>
        <span className={`pf-chip${c.complete ? ' done' : ''}`}>
          {c.complete && <IconCheck width={12} height={12} strokeWidth={2.5} />}
          {c.done}/{c.total}
        </span>
      </header>
      {children}
    </section>
  );
}

/* ------------------------------------------------------------------ wizard (create) */

const WIZARD_STEPS: { title: string; sections: string[] }[] = [
  { title: 'Context', sections: ['context'] },
  { title: 'Goals & scope', sections: ['objectives', 'scope'] },
  { title: 'Users & timeline', sections: ['domain', 'timeline'] },
  { title: 'Technology', sections: ['technology'] },
  { title: 'Review', sections: [] },
];

export function ProjectWizard({
  values,
  onChange,
  onSubmit,
  onCancel,
  busy,
  error,
}: {
  values: ProjectValues;
  onChange: (k: FieldKey, v: string) => void;
  onSubmit: () => void;
  onCancel: () => void;
  busy: boolean;
  error: string | null;
}) {
  const [step, setStep] = useState(0);
  const [showErrors, setShowErrors] = useState(false);
  const last = step === WIZARD_STEPS.length - 1;
  const nameOk = filled(values.name);

  const go = (target: number) => {
    // Only the name blocks progress; everything else is optional and can be filled in later.
    if (target > 0 && !nameOk) {
      setShowErrors(true);
      setStep(0);
      return;
    }
    setStep(target);
    window.scrollTo({ top: 0, behavior: 'smooth' });
  };

  const current = WIZARD_STEPS[step];

  return (
    <form
      className="pf-wizard"
      noValidate
      onSubmit={(e) => {
        e.preventDefault();
        if (!last) return go(step + 1);
        if (!nameOk) return go(0);
        onSubmit();
      }}
    >
      <ol className="pf-steps" aria-label="Project creation steps">
        {WIZARD_STEPS.map((s, i) => (
          <li key={s.title} className={i === step ? 'current' : i < step ? 'done' : ''} aria-current={i === step ? 'step' : undefined}>
            <button type="button" onClick={() => go(i)}>
              <span className="pf-step-num">{i < step ? <IconCheck width={13} height={13} strokeWidth={2.5} /> : i + 1}</span>
              <span className="pf-step-title">{s.title}</span>
            </button>
          </li>
        ))}
      </ol>

      <div className="pf-progress" aria-hidden="true">
        <div style={{ width: `${((step + 1) / WIZARD_STEPS.length) * 100}%` }} />
      </div>

      {error && (
        <div className="alert alert-danger" role="alert">
          <IconAlert width={16} height={16} />
          <span>{error}</span>
        </div>
      )}

      {current.sections.map((key) => {
        const section = SECTIONS.find((s) => s.key === key)!;
        return (
          <SectionCard key={key} section={section} values={values}>
            <Fields section={section} values={values} onChange={onChange} showErrors={showErrors} />
          </SectionCard>
        );
      })}

      {last && (
        <section className="pf-card">
          <header className="pf-card-head">
            <div>
              <h2>Review</h2>
              <p>Check the details below. Everything except the name can be completed later during Requirements Gathering.</p>
            </div>
            <span className="pf-chip">{overallCompletion(values).done}/{overallCompletion(values).total}</span>
          </header>
          <ProjectSummary values={values} />
        </section>
      )}

      <div className="pf-actions">
        <button type="button" className="btn btn-ghost" onClick={onCancel} disabled={busy}>
          Cancel
        </button>
        <div className="pf-actions-right">
          {step > 0 && (
            <button type="button" className="btn btn-secondary" onClick={() => go(step - 1)} disabled={busy}>
              <IconArrowLeft width={15} height={15} /> Back
            </button>
          )}
          {last ? (
            <button type="submit" className="btn btn-primary" disabled={busy || !nameOk}>
              {busy ? 'Creating…' : 'Create Project'}
            </button>
          ) : (
            <button type="submit" className="btn btn-primary">
              Next <IconArrowRight width={15} height={15} />
            </button>
          )}
        </div>
      </div>
    </form>
  );
}

/** Read-only key/value rendering of a project's fields. Also used for users who cannot edit. */
export function ProjectSummary({ values }: { values: ProjectValues }) {
  return (
    <dl className="pf-summary">
      {SECTIONS.flatMap((s) => s.fields).map((f) => (
        <div key={f.key} className="pf-summary-row">
          <dt>{f.label}</dt>
          <dd className={filled(values[f.key]) ? undefined : 'empty'}>{filled(values[f.key]) ? values[f.key] : 'Not provided'}</dd>
        </div>
      ))}
    </dl>
  );
}

/* ------------------------------------------------------------------ editor (edit / gathering) */

export function ProjectEditor({
  values,
  saved,
  onChange,
  onSave,
  busy,
  error,
  saveLabel,
  onCancel,
}: {
  values: ProjectValues;
  saved: ProjectValues;
  onChange: (k: FieldKey, v: string) => void;
  onSave: () => void;
  busy: boolean;
  error: string | null;
  saveLabel: string;
  onCancel?: () => void;
}) {
  const [showErrors, setShowErrors] = useState(false);
  const dirty = isDirty(values, saved);
  const overall = overallCompletion(values);
  const nameOk = filled(values.name);

  const jump = (key: string) => {
    const el = document.getElementById(`pf-section-${key}`);
    const reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    el?.scrollIntoView({ behavior: reduce ? 'auto' : 'smooth', block: 'start' });
  };

  return (
    <form
      className="pf-editor"
      noValidate
      onSubmit={(e) => {
        e.preventDefault();
        if (!nameOk) {
          setShowErrors(true);
          return;
        }
        onSave();
      }}
    >
      <div className="pf-editor-main">
        {error && (
          <div className="alert alert-danger" role="alert">
            <IconAlert width={16} height={16} />
            <span>{error}</span>
          </div>
        )}
        {SECTIONS.map((section) => (
          <SectionCard key={section.key} id={`pf-section-${section.key}`} section={section} values={values}>
            <Fields section={section} values={values} onChange={onChange} showErrors={showErrors} />
          </SectionCard>
        ))}

        <div className={`pf-savebar${dirty ? ' dirty' : ''}`}>
          <span className="pf-savebar-state" role="status">
            {dirty ? (
              <>
                <span className="pf-dot" /> Unsaved changes
              </>
            ) : (
              <>
                <IconCheck width={14} height={14} /> All changes saved
              </>
            )}
          </span>
          <div className="pf-actions-right">
            {onCancel && (
              <button type="button" className="btn btn-ghost" onClick={onCancel} disabled={busy}>
                {dirty ? 'Discard' : 'Back'}
              </button>
            )}
            <button type="submit" className="btn btn-primary" disabled={busy || !dirty}>
              {busy ? 'Saving…' : saveLabel}
            </button>
          </div>
        </div>
      </div>

      <aside className="pf-rail" aria-label="Completion">
        <div className="pf-rail-card">
          <div className="pf-rail-head">
            <strong>{overall.percent}% complete</strong>
            <span>
              {overall.done} of {overall.total} details
            </span>
          </div>
          <div className="progress-track">
            <div className="progress-fill" style={{ width: `${Math.max(overall.percent, 2)}%` }} />
          </div>
          <ul className="pf-rail-list">
            {SECTIONS.map((s) => {
              const c = sectionCompletion(s, values);
              return (
                <li key={s.key}>
                  <button type="button" onClick={() => jump(s.key)}>
                    <span className={`pf-rail-mark${c.complete ? ' done' : c.done > 0 ? ' partial' : ''}`} aria-hidden="true">
                      {c.complete && <IconCheck width={11} height={11} strokeWidth={3} />}
                    </span>
                    <span>{s.title}</span>
                    <small>
                      {c.done}/{c.total}
                    </small>
                  </button>
                </li>
              );
            })}
          </ul>
        </div>
      </aside>
    </form>
  );
}
