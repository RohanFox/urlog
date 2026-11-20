import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState, type SubmitEvent } from 'react';
import { Link } from 'react-router';
import { ApiError, call } from '../api/http';
import type { CaseState, CaseSummaryResponse } from '../api/schema.gen';
import { count, roleName, stateName, utcMinute } from '../app/format';
import { Banner, Button, Dialog, EmptyState, Field, LoadingBlock, SkeletonRows, Tag } from '../ui/controls';
import './pages.css';

type Filter = 'active' | 'closed' | 'archived' | 'all';

const FILTERS: ReadonlyArray<{ key: Filter; label: string }> = [
  { key: 'active', label: 'Active' },
  { key: 'closed', label: 'Closed' },
  { key: 'archived', label: 'Archived' },
  { key: 'all', label: 'All' },
];

const COLUMNS = 5;

function summary(cases: readonly CaseSummaryResponse[]): string {
  if (cases.length === 0) {
    return 'No cases yet';
  }
  const by = (state: CaseState) => cases.filter((c) => c.state === state).length;
  return [`${by('active')} active`, `${by('closed')} closed`, `${by('archived')} archived`].join(' · ');
}

function CreateCase({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const [name, setName] = useState('');
  const queries = useQueryClient();
  const create = useMutation({
    mutationFn: (body: { name: string }) => call('CreateCase', { body }),
    onSuccess: async () => {
      await queries.invalidateQueries({ queryKey: ['cases'] });
      setName('');
      onOpenChange(false);
    },
  });

  const error = create.error;
  // The name is the only thing sent, so a refusal of the request is about the name.
  const refused = error instanceof ApiError && (error.status === 409 || error.status === 400) ? error.message : undefined;
  const failed = error !== null && refused === undefined;

  const submit = (event: SubmitEvent) => {
    event.preventDefault();
    if (name.trim() !== '' && !create.isPending) {
      create.mutate({ name: name.trim() });
    }
  };

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        if (!next) {
          create.reset();
        }
        onOpenChange(next);
      }}
      title="New case"
      description="A case holds the evidence of one incident and the people who may see it. You will be its owner."
    >
      <form className="form" onSubmit={submit} noValidate>
        {failed ? <Banner kind="error" title="The case was not created">The server did not accept the request. Nothing was saved.</Banner> : null}
        {/* eslint-disable-next-line jsx-a11y/no-autofocus -- the dialog has one field and exists to fill it */}
        <Field label="Name" name="name" value={name} maxLength={256} autoComplete="off" autoFocus error={refused} onChange={(e) => { setName(e.target.value); }} />
        <div className="dialog-actions">
          <Button kind="ghost" onClick={() => { onOpenChange(false); }}>
            Cancel
          </Button>
          <Button kind="primary" type="submit" busy={create.isPending} disabled={name.trim() === ''}>
            Create case
          </Button>
        </div>
      </form>
    </Dialog>
  );
}

function CaseTable({ rows }: { rows: readonly CaseSummaryResponse[] }) {
  return (
    <>
      {rows.map((c) => (
        <tr key={c.id}>
          <td className="case-cell">
            <Link className="case-link" to={`/cases/${c.id}`}>
              <span className="mono muted">{c.id}</span>
              <span className="ellipsis" title={c.name}>
                {c.name}
              </span>
            </Link>
          </td>
          <td>
            <Tag accent={c.state === 'active'}>{stateName(c.state)}</Tag>
          </td>
          <td>{roleName(c.role)}</td>
          <td className="mono">{c.owner}</td>
          <td className="mono muted">{utcMinute(c.created_at)}</td>
        </tr>
      ))}
    </>
  );
}

export function Cases() {
  const [filter, setFilter] = useState<Filter>('active');
  const [text, setText] = useState('');
  const [creating, setCreating] = useState(false);
  const cases = useQuery({ queryKey: ['cases'], queryFn: ({ signal }) => call('ListCases', { signal }) });

  const all = cases.data;
  const shown = useMemo(() => {
    const needle = text.trim().toLowerCase();
    return (all ?? []).filter((c) => (filter === 'all' || c.state === filter) && (needle === '' || c.name.toLowerCase().includes(needle) || String(c.id) === needle));
  }, [all, filter, text]);

  let body;
  if (cases.isPending) {
    body = (
      <>
        <LoadingBlock title="Loading your cases" />
        <Table>
          <SkeletonRows rows={4} columns={COLUMNS} />
        </Table>
      </>
    );
  } else if (cases.isError) {
    body = (
      <div className="section-pad">
        <Banner
          kind="error"
          title="Your cases could not be loaded"
          action={
            <Button small icon="retry" onClick={() => void cases.refetch()}>
              Try again
            </Button>
          }
        >
          {cases.error instanceof ApiError && cases.error.offline ? 'The server did not answer.' : 'The server answered with an error.'} Your evidence is not affected.
        </Banner>
      </div>
    );
  } else if (cases.data.length === 0) {
    body = (
      <EmptyState title="Start your first case">
        <p className="muted">A case holds the evidence files of one incident, the people who may see them, and what you find. Nothing is shared between cases.</p>
        <ul>
          <li>Upload the logs of the affected hosts.</li>
          <li>Every file is hashed on arrival and never changed.</li>
          <li>Search across all of a case&apos;s hosts at once.</li>
        </ul>
        <Button kind="primary" icon="plus" onClick={() => { setCreating(true); }}>
          New case
        </Button>
      </EmptyState>
    );
  } else if (shown.length === 0) {
    body = (
      <EmptyState title={text.trim() === '' ? `No ${filter} cases` : 'No case matches the filter'}>
        <p className="muted">{count(cases.data.length, 'case', 'cases')} in all.</p>
        <Button onClick={() => { setFilter('all'); setText(''); }}>Show all cases</Button>
      </EmptyState>
    );
  } else {
    body = (
      <Table>
        <CaseTable rows={shown} />
      </Table>
    );
  }

  return (
    <main className="page">
      <div className="page-pad">
        <div className="page-head">
          <div>
            <h1>Cases</h1>
            <span className="sub">{all === undefined ? ' ' : summary(all)}</span>
          </div>
          <span className="grow" />
          <label className="sr-only" htmlFor="case-filter">
            Filter cases
          </label>
          <input id="case-filter" className="input case-filter" placeholder="Filter by name or id" value={text} onChange={(e) => { setText(e.target.value); }} />
          <div className="seg" role="group" aria-label="State">
            {FILTERS.map((f) => (
              <button key={f.key} type="button" aria-pressed={filter === f.key} onClick={() => { setFilter(f.key); }}>
                {f.label}
              </button>
            ))}
          </div>
          <Button kind="primary" icon="plus" onClick={() => { setCreating(true); }}>
            New case
          </Button>
        </div>
        <section className="section" aria-label="Cases">
          {body}
        </section>
      </div>
      <CreateCase open={creating} onOpenChange={setCreating} />
    </main>
  );
}

function Table({ children }: { children: React.ReactNode }) {
  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>
            <th>Case</th>
            <th>State</th>
            <th>Your role</th>
            <th>Owner</th>
            <th>Created</th>
          </tr>
        </thead>
        <tbody>{children}</tbody>
      </table>
    </div>
  );
}
