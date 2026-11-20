import { useEffect, useState, type SubmitEvent } from 'react';
import { ApiError } from '../api/http';
import { useSession } from '../app/session';
import { Lockup } from '../brand/Logo';
import { Banner, Button, Field } from '../ui/controls';
import './pages.css';

type Outcome =
  | { kind: 'none' }
  /** One answer for an unknown name, a wrong password and a locked account: the server does not say which. */
  | { kind: 'refused' }
  | { kind: 'wait'; seconds: number }
  | { kind: 'failed'; message: string };

/** Counts a wait down to zero, one second at a time. */
function useCountdown(outcome: Outcome, done: () => void): void {
  const seconds = outcome.kind === 'wait' ? outcome.seconds : undefined;
  useEffect(() => {
    if (seconds === undefined) {
      return;
    }
    const timer = window.setTimeout(done, 1000);
    return () => {
      window.clearTimeout(timer);
    };
  }, [seconds, done]);
}

export function Login({ ended }: { ended: boolean }) {
  const { signIn } = useSession();
  const [name, setName] = useState('');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [outcome, setOutcome] = useState<Outcome>({ kind: 'none' });

  useCountdown(outcome, () => {
    setOutcome((current) => (current.kind !== 'wait' ? current : current.seconds <= 1 ? { kind: 'none' } : { kind: 'wait', seconds: current.seconds - 1 }));
  });

  const submit = (event: SubmitEvent) => {
    event.preventDefault();
    if (busy || outcome.kind === 'wait') {
      return;
    }
    setBusy(true);
    setOutcome({ kind: 'none' });
    signIn(name, password)
      .catch((error: unknown) => {
        if (error instanceof ApiError && error.status === 401) {
          setOutcome({ kind: 'refused' });
        } else if (error instanceof ApiError && error.status === 429) {
          setOutcome({ kind: 'wait', seconds: error.retryAfter ?? 60 });
        } else if (error instanceof ApiError && error.status === 400) {
          setOutcome({ kind: 'failed', message: error.message });
        } else {
          setOutcome({ kind: 'failed', message: error instanceof ApiError && error.offline ? 'The server did not answer. Nothing was sent twice; try again.' : 'The server could not sign you in. Try again.' });
        }
      })
      .finally(() => {
        setBusy(false);
      });
  };

  return (
    <main className="signin">
      <section className="signin-left">
        <div className="signin-brand">
          <Lockup full />
          <p className="signin-lead muted">Agentless, post-hoc log investigation for CSIRT and incident-response analysts.</p>
        </div>
        <form className="form" onSubmit={submit} noValidate>
          <div>
            <h1>Sign in</h1>
            <p className="sub">Local accounts only. An administrator of this instance creates them.</p>
          </div>
          {ended && outcome.kind === 'none' ? <Banner kind="notice" title="Your session ended">Sign in to go on where you were.</Banner> : null}
          {outcome.kind === 'refused' ? <Banner kind="notice" title="That name and password were not accepted" /> : null}
          {outcome.kind === 'wait' ? (
            <Banner kind="notice" title="Too many attempts from this address">
              Try again in {outcome.seconds} s.
            </Banner>
          ) : null}
          {outcome.kind === 'failed' ? <Banner kind="error" title="Sign-in did not go through">{outcome.message}</Banner> : null}
          <Field label="Name" name="name" autoComplete="username" autoCapitalize="none" spellCheck={false} value={name} required onChange={(e) => { setName(e.target.value); }} />
          <Field label="Password" name="password" type="password" autoComplete="current-password" value={password} required onChange={(e) => { setPassword(e.target.value); }} />
          <Button kind="primary" type="submit" busy={busy} disabled={outcome.kind === 'wait' || name === '' || password === ''}>
            {busy ? 'Signing in' : 'Sign in'}
          </Button>
        </form>
        <p className="sub signin-foot">A session ends by itself when it is left idle. Every sign-in is recorded in the audit log.</p>
      </section>
      <section className="signin-right" aria-label="About this instance">
        <h2 className="signin-about">This instance</h2>
        <dl className="facts">
          <dt>Accounts</dt>
          <dd>Local accounts. No single sign-on.</dd>
          <dt>Evidence</dt>
          <dd>Hashed with SHA-256 on arrival and never changed.</dd>
          <dt>Agents</dt>
          <dd>None. urlog reads only the files you upload.</dd>
        </dl>
      </section>
    </main>
  );
}
