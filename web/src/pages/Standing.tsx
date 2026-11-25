import { useEffect, useState } from 'react';
import { Link } from 'react-router';
import { healthReason } from '../api/http';
import { Mark } from '../brand/Logo';
import { Banner, Button, EmptyState } from '../ui/controls';
import './pages.css';

/**
 * The backend runs and serves nothing but its health route: its metadata file and its event store are not yet
 * confirmed to belong together. That is a state of the instance, not a failure of this page, and it is asked again.
 */
export function NotReady({ retryAfter, onRetry }: { retryAfter: number; onRetry: () => void }) {
  const [reason, setReason] = useState<string>();

  useEffect(() => {
    const controller = new AbortController();
    void healthReason(controller.signal).then(setReason);
    const timer = window.setTimeout(onRetry, Math.max(1, retryAfter) * 1000);
    return () => {
      controller.abort();
      window.clearTimeout(timer);
    };
  }, [retryAfter, onRetry]);

  return (
    <main className="standing">
      <Mark size={48} label="urlog" />
      <Banner kind="notice" title="This instance is not serving yet">
        {reason ?? 'It is waiting for its event store.'} This page asks again every {retryAfter} s.
      </Banner>
    </main>
  );
}

export function Unreachable({ onRetry }: { onRetry: () => void }) {
  return (
    <main className="standing">
      <Mark size={48} label="urlog" />
      <Banner
        kind="error"
        title="The server did not answer"
        action={
          <Button small icon="retry" onClick={onRetry}>
            Try again
          </Button>
        }
      >
        Nothing could be read. Your evidence is not affected.
      </Banner>
    </main>
  );
}

export function NotFound() {
  return (
    <main className="page">
      <div className="page-pad">
        <section className="section">
          <EmptyState title="Nothing at this address">
            <p className="muted">The address does not name a page of urlog.</p>
            <Link className="btn" to="/cases">
              All cases
            </Link>
          </EmptyState>
        </section>
      </div>
    </main>
  );
}

/** One page for a case that does not exist and a case that is not shared with the caller: the server answers both alike. */
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
