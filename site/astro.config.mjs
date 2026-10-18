// Public website (ADR-017): static HTML, English at /, Danish at /da/.
import { defineConfig } from 'astro/config';

export default defineConfig({
  // Canonical and hreflang URLs; set per environment once the domain is chosen (deploy runbook).
  site: process.env.SITE_URL ?? 'http://localhost:4321',
  output: 'static',
  trailingSlash: 'never',
  build: { format: 'file' },
  i18n: {
    locales: ['en', 'da'],
    defaultLocale: 'en',
    routing: { prefixDefaultLocale: false },
  },
  server: { port: Number(process.env.PORT ?? 4321) },
  // tokens.css lives in web/ (one palette for app and site).
  vite: { server: { fs: { allow: ['..'] } } },
});
