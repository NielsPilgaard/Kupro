// Every page in both languages, each with its hreflang alternates (same set as the <link rel="alternate"> in Base).
import type { APIRoute } from 'astro';
import { pages, path, type Locale } from '../i18n';

const locales: Locale[] = ['en', 'da'];

export const GET: APIRoute = ({ site }) => {
  const href = (l: Locale, p: (typeof pages)[number]) => new URL(path(l, p), site).href;
  const urls = pages.flatMap((p) =>
    locales.map(
      (l) =>
        `  <url>\n    <loc>${href(l, p)}</loc>\n` +
        locales.map((a) => `    <xhtml:link rel="alternate" hreflang="${a}" href="${href(a, p)}"/>\n`).join('') +
        `    <xhtml:link rel="alternate" hreflang="x-default" href="${href('en', p)}"/>\n  </url>\n`,
    ),
  );
  const xml =
    '<?xml version="1.0" encoding="UTF-8"?>\n' +
    '<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:xhtml="http://www.w3.org/1999/xhtml">\n' +
    urls.join('') +
    '</urlset>\n';
  return new Response(xml, { headers: { 'Content-Type': 'application/xml; charset=utf-8' } });
};
