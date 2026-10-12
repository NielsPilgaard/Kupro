import { defineConfig, devices } from '@playwright/test';

// Runs against a live stack: `dotnet aspire run` (SPA on :5180, Keycloak on :8180). ADR-003 IMP-002.
export default defineConfig({
  testDir: './tests',
  timeout: 60_000,
  fullyParallel: false,
  retries: process.env.CI ? 1 : 0,
  use: {
    baseURL: process.env.FLOWSTATE_WEB_URL ?? 'http://localhost:5180',
    // Keycloak serves the ASP.NET dev certificate locally.
    ignoreHTTPSErrors: true,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'setup', testMatch: /auth\.setup\.ts/, use: { locale: 'en-US' } },
    { name: 'chromium', use: { ...devices['Desktop Chrome'], locale: 'en-US', storageState: '.auth/user.json' }, dependencies: ['setup'] },
  ],
});
