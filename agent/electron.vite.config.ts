import { resolve } from 'node:path';
import { defineConfig } from 'electron-vite';

export default defineConfig({
  main: {
    build: {
      // El paquete shared se publica como TypeScript: se incluye en el bundle, no se carga en tiempo de ejecución.
      externalizeDeps: { exclude: ['@monitoreoti/shared'] },
      rollupOptions: {
        // index: modo UI (Electron). service: modo servicio (Node puro, ELECTRON_RUN_AS_NODE=1).
        input: {
          index: resolve(__dirname, 'src/main/index.ts'),
          service: resolve(__dirname, 'src/service/index.ts'),
        },
      },
    },
  },
  preload: {},
  renderer: {},
});
