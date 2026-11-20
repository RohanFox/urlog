import js from '@eslint/js';
import jsxA11y from 'eslint-plugin-jsx-a11y';
import reactHooks from 'eslint-plugin-react-hooks';
import globals from 'globals';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  { ignores: ['node_modules', 'src/api/schema.gen.ts', 'test-results', 'playwright-report'] },
  js.configs.recommended,
  {
    files: ['**/*.{ts,tsx}'],
    extends: [...tseslint.configs.strictTypeChecked, jsxA11y.flatConfigs.strict],
    languageOptions: {
      globals: globals.browser,
      parserOptions: { projectService: true, tsconfigRootDir: import.meta.dirname },
    },
    plugins: { 'react-hooks': reactHooks },
    rules: {
      'react-hooks/rules-of-hooks': 'error',
      'react-hooks/exhaustive-deps': 'error',
      '@typescript-eslint/consistent-type-imports': 'error',
      '@typescript-eslint/restrict-template-expressions': ['error', { allowNumber: true }],
      // One module talks to the backend, so the CSRF header and the 401 rule have one home.
      'no-restricted-globals': ['error', { name: 'fetch', message: 'Use src/api/http.ts.' }],
      // Colours, fonts and sizes live in styles/tokens.css. A style attribute would be a second home.
      'no-restricted-syntax': ['error', { selector: "JSXAttribute[name.name='style']", message: 'Use a class and the tokens.' }],
    },
  },
  { files: ['src/api/http.ts'], rules: { 'no-restricted-globals': 'off' } },
  { files: ['**/*.{js,mjs}'], languageOptions: { globals: globals.node } },
  { files: ['public/**/*.js'], languageOptions: { globals: globals.browser, sourceType: 'script' } },
);
