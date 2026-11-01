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
  // Fonts stay files: the CSP (headers.conf) has font-src 'self', which blocks fonts Vite would inline as data: URIs.
  vite: {
    server: { fs: { allow: ['..'] } },
    build: { assetsInlineLimit: (file) => (/\.woff2?$/.test(file) ? false : undefined) },
  },
});
