import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './app/App';

const root = document.getElementById('root');
if (root === null) {
  throw new Error('index.html has no #root');
}

// index.html paints the loading screen itself; the app replaces it and shows the same screen until the
// server has said whether there is a session.
createRoot(root).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
