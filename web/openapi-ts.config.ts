import { defineConfig } from '@hey-api/openapi-ts';

// Generates the typed API client from the committed spec (ADR-003 IMP-003). Output is gitignored.
export default defineConfig({
  input: '../openapi/flowstate.json',
  output: 'src/api/gen',
  plugins: ['@hey-api/client-fetch', '@tanstack/react-query'],
});
