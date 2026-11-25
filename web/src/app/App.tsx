import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useState } from 'react';
import { BrowserRouter, Navigate, Outlet, Route, Routes, useLocation, useSearchParams } from 'react-router';
import { ApiError } from '../api/http';
import { BootScreen } from '../brand/Logo';
import { CaseEvidence } from '../pages/CaseEvidence';
import { Cases } from '../pages/Cases';
import { Login } from '../pages/Login';
import { NotFound, NotReady, Unreachable } from '../pages/Standing';
import { safeNext, SessionProvider, useSession } from './session';
import { Shell } from './Shell';
import '../styles/tokens.css';
import '../styles/base.css';

function makeQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 5_000,
        refetchOnWindowFocus: true,
        // A refusal is an answer: asking again changes nothing. Only a missing answer or a server fault is tried again.
        retry: (failures, error) => failures < 2 && !(error instanceof ApiError && error.status >= 400 && error.status < 500),
      },
    },
  });
}

/** Pages behind the session: without one the visitor goes to sign-in and comes back to the same address. */
function SignedIn() {
  const { state } = useSession();
  const location = useLocation();
  if (state.kind !== 'signed-in') {
    // After a sign-out the next person starts at the case list, not at the address the last one stood on.
    const left = state.kind === 'anonymous' && state.left;
    const next = `${location.pathname}${location.search}`;
    return <Navigate to={left || next === '/' || next === '/cases' ? '/login' : `/login?next=${encodeURIComponent(next)}`} replace />;
  }
  return (
    <Shell>
      <Outlet />
    </Shell>
  );
}

function LoginRoute() {
  const { state } = useSession();
  const [search] = useSearchParams();
  if (state.kind === 'signed-in') {
    return <Navigate to={safeNext(search.get('next'))} replace />;
  }
  return <Login ended={state.kind === 'anonymous' && state.ended} />;
}

function Gate() {
  const { state, retry } = useSession();
  if (state.kind === 'loading') {
    return <BootScreen />;
  }
  if (state.kind === 'not-ready') {
    return <NotReady retryAfter={state.retryAfter} onRetry={retry} />;
  }
  if (state.kind === 'unreachable') {
    return <Unreachable onRetry={retry} />;
  }
  return (
    <Routes>
      <Route path="/login" element={<LoginRoute />} />
      <Route element={<SignedIn />}>
        <Route path="/" element={<Navigate to="/cases" replace />} />
        <Route path="/cases" element={<Cases />} />
        <Route path="/cases/:caseId" element={<CaseEvidence />} />
        <Route path="*" element={<NotFound />} />
      </Route>
    </Routes>
  );
}

export function App() {
  const [queries] = useState(makeQueryClient);
  return (
    <QueryClientProvider client={queries}>
      <SessionProvider>
        <BrowserRouter>
          <Gate />
        </BrowserRouter>
      </SessionProvider>
    </QueryClientProvider>
  );
}
