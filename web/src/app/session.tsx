import { useQueryClient } from '@tanstack/react-query';
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { ApiError, call, setCsrfToken, setUnauthorizedHandler } from '../api/http';
import type { UserResponse } from '../api/schema.gen';

/** What the app knows about who is using it. */
export type SessionState =
  | { kind: 'loading' }
  /** `ended`: the server ended a session that was in use. `left`: the user signed out. */
  | { kind: 'anonymous'; ended: boolean; left: boolean }
  | { kind: 'signed-in'; user: UserResponse }
  /** The backend answers only its health route until its two stores are confirmed to belong together. */
  | { kind: 'not-ready'; retryAfter: number }
  | { kind: 'unreachable'; message: string };

interface Session {
  state: SessionState;
  signIn: (name: string, password: string) => Promise<void>;
  signOut: () => Promise<void>;
  /** Reads the session again, after "not ready" or "unreachable". */
  retry: () => void;
}

const SessionContext = createContext<Session | undefined>(undefined);

export function SessionProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<SessionState>({ kind: 'loading' });
  const [attempt, setAttempt] = useState(0);
  const queries = useQueryClient();
  const signedIn = useRef(false);

  const end = useCallback(
    (ended: boolean, left = false) => {
      // Nothing read under the old session stays in memory, and its token is gone.
      setCsrfToken(undefined);
      queries.clear();
      signedIn.current = false;
      setState({ kind: 'anonymous', ended, left });
    },
    [queries],
  );

  useEffect(() => {
    // Any request answered 401 means the server ended the session: idle, expired, locked or reset.
    setUnauthorizedHandler(() => {
      end(signedIn.current);
    });
    return () => {
      setUnauthorizedHandler(undefined);
    };
  }, [end]);

  useEffect(() => {
    const controller = new AbortController();
    call('Me', { signal: controller.signal, quiet401: true })
      .then((session) => {
        setCsrfToken(session.csrf_token);
        signedIn.current = true;
        setState({ kind: 'signed-in', user: session.user });
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) {
          return;
        }
        if (error instanceof ApiError && error.status === 401) {
          setState({ kind: 'anonymous', ended: false, left: false });
        } else if (error instanceof ApiError && error.slug === 'not-ready') {
          setState({ kind: 'not-ready', retryAfter: error.retryAfter ?? 5 });
        } else {
          setState({ kind: 'unreachable', message: error instanceof Error ? error.message : 'no answer from the server' });
        }
      });
    return () => {
      controller.abort();
    };
  }, [attempt]);

  const value = useMemo<Session>(
    () => ({
      state,
      async signIn(name, password) {
        const session = await call('Login', { body: { name, password }, quiet401: true });
        setCsrfToken(session.csrf_token);
        signedIn.current = true;
        setState({ kind: 'signed-in', user: session.user });
      },
      async signOut() {
        try {
          await call('Logout', { quiet401: true });
        } catch (error) {
          // A session the server already ended is signed out; anything else the user must see.
          if (!(error instanceof ApiError && error.status === 401)) {
            throw error;
          }
        }
        end(false, true);
      },
      retry() {
        setState({ kind: 'loading' });
        setAttempt((n) => n + 1);
      },
    }),
    [state, end],
  );

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

export function useSession(): Session {
  const session = useContext(SessionContext);
  if (session === undefined) {
    throw new Error('useSession outside SessionProvider');
  }
  return session;
}

/** The signed-in user. Only pages behind the session gate call this. */
export function useUser(): UserResponse {
  const { state } = useSession();
  if (state.kind !== 'signed-in') {
    throw new Error('useUser without a session');
  }
  return state.user;
}

/** Where to go after sign-in: a path of this app and nothing else. */
export function safeNext(next: string | null): string {
  if (next === null || !next.startsWith('/') || next.startsWith('//') || next.startsWith('/\\') || next.startsWith('/login')) {
    return '/cases';
  }
  return next;
}
