/// <reference types="vitest/config" />
import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

// In development the API runs on :5085 and Vite proxies /api to it, so the browser sees a single
// origin: cookies stay first-party and Google's OAuth redirect lands back on this dev server.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': { target: 'http://localhost:5085', changeOrigin: false, xfwd: true },
    },
  },
  build: {
    // The ASP.NET Core API serves the built app from wwwroot in production.
    outDir: '../server/FinSight.Api/wwwroot',
    emptyOutDir: true,
    sourcemap: false,
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
  },
});
