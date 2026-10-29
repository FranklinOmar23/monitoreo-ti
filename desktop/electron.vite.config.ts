import { resolve } from 'node:path';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'electron-vite';

// El paquete shared se publica como TypeScript: se incluye en el bundle, no se carga en tiempo de ejecución.
const bundleShared = { externalizeDeps: { exclude: ['@monitoreoti/shared'] } };

export default defineConfig({
  main: { build: bundleShared },
  preload: { build: bundleShared },
  renderer: {
    resolve: {
      alias: {
        '@components': resolve(__dirname, 'src/components'),
      },
    },
    plugins: [react()],
  },
});
