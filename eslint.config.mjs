import js from '@eslint/js';
import reactHooks from 'eslint-plugin-react-hooks';
import globals from 'globals';
import tseslint from 'typescript-eslint';

export default tseslint.config(
    {
        ignores: ['**/node_modules/**', '**/out/**', '**/dist/**', '**/.expo/**', 'server/**', 'database/**'],
    },
    js.configs.recommended,
    ...tseslint.configs.recommended,
    {
        files: ['**/*.{ts,tsx,mjs}'],
        languageOptions: {
            globals: { ...globals.node, ...globals.browser },
        },
    },
    {
        files: ['**/*.tsx'],
        plugins: { 'react-hooks': reactHooks },
        rules: reactHooks.configs.recommended.rules,
    },
    {
        // Evitar eval y similares en todo el código (CSP del panel y seguridad del agente).
        rules: {
            'no-eval': 'error',
            'no-implied-eval': 'error',
            'no-new-func': 'error',
        },
    },
);
