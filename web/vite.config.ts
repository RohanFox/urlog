import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// The backend of the box in development. The browser sees one origin: the page and /api both come from the
// development server, which hands /api on, so the session cookie and the CSRF rule work as they do in production.
const backend = process.env['URLOG_BACKEND'] ?? 'http://127.0.0.1:5080';

export default defineConfig({
  plugins: [react()],
  // Lightning CSS (part of Vite) builds the stylesheets, so the named breakpoints of src/styles/breakpoints.css
  // become plain media queries in the output.
  css: {
    transformer: 'lightningcss',
    lightningcss: { drafts: { customMedia: true } },
  },
  build: {
    // The backend serves its web root; a production build is that folder.
    outDir: '../src/Api/wwwroot',
    emptyOutDir: true,
    sourcemap: false,
  },
  server: {
    host: 'localhost',
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': { target: backend, changeOrigin: false },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['src/test-setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
    css: false,
    restoreMocks: true,
  },
});
