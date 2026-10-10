/// <reference types="vitest/config" />
import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// Under `aspire run`, Aspire injects the API address as API_HTTPS / API_HTTP (WithReference(api)).
const apiTarget = process.env.API_HTTPS ?? process.env.API_HTTP ?? 'http://localhost:5239';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    proxy: {
      '/api': { target: apiTarget, changeOrigin: true, secure: false },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    css: false,
  },
});
