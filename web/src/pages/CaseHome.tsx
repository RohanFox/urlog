import { useQuery } from '@tanstack/react-query';
import { Link, useParams } from 'react-router';
import { ApiError, call } from '../api/http';
import { count, roleName, stateName, utcMinute } from '../app/format';
import { Banner, Button, EmptyState, LoadingBlock, Tag } from '../ui/controls';
import './pages.css';

/** A case as its record describes it: who owns it, its state, and the span of its events. */
export function CaseHome() {
  const { caseId = '' } = useParams();
  const valid = /^[0-9]+$/.test(caseId);
  const one = useQuery({
    queryKey: ['case', caseId],
    queryFn: ({ signal }) => call('GetCase', { params: { caseId }, signal }),
    enabled: valid,
    retry: false,
  });

  // The server answers a case that does not exist and a case that is not shared with the caller alike.
  if (!valid || (one.error instanceof ApiError && one.error.status === 404)) {
    return <NoSuchCase />;
  }

  if (one.isPending) {
    return (
      <main className="page">
        <div className="page-pad">
          <section className="section">
            <LoadingBlock title="Loading the case" />
          </section>
        </div>
      </main>
    );
  }

  if (one.isError) {
    return (
      <main className="page">
        <div className="page-pad">
          <Banner
            kind="error"
            title="The case could not be loaded"
            action={
              <Button small icon="retry" onClick={() => void one.refetch()}>
                Try again
              </Button>
            }
          >
            {one.error instanceof ApiError && one.error.offline ? 'The server did not answer.' : 'The server answered with an error.'}
          </Banner>
        </div>
      </main>
    );
  }

  const c = one.data;
  return (
    <main className="page">
      <div className="page-pad">
        <div className="page-head">
          <div>
            <h1>{c.name}</h1>
            <span className="sub mono">case {c.id}</span>
          </div>
          <Tag accent={c.state === 'active'}>{stateName(c.state)}</Tag>
        </div>
        <section className="section section-pad" aria-label="Case record">
          <dl className="facts">
            <dt>Your role</dt>
            <dd>{roleName(c.my_role)}</dd>
            <dt>Owner</dt>
            <dd className="mono">{c.owner}</dd>
            <dt>Members</dt>
            <dd>{count(c.members, 'person', 'people')}</dd>
            <dt>Created</dt>
            <dd className="mono">{utcMinute(c.created_at)}</dd>
            <dt>Events</dt>
            <dd className="mono">{c.span.from !== null && c.span.to !== null ? `${utcMinute(c.span.from)} to ${utcMinute(c.span.to)}` : 'No events with a usable time yet'}</dd>
          </dl>
        </section>
      </div>
    </main>
  );
}

export function NoSuchCase() {
  return (
    <main className="page">
      <div className="page-pad">
        <section className="section">
          <EmptyState title="No such case">
            <p className="muted">There is no case at this address, or it is not shared with you. The owner of a case adds people to it.</p>
            <Link className="btn" to="/cases">
              All cases
            </Link>
          </EmptyState>
        </section>
      </div>
    </main>
  );
}
