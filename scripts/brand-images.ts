// Renders every raster brand file from the Stream mark (docs/branding.md#logo-mark--stream): favicon.ico, app icons,
// Open Graph images and the GitHub social preview. Chromium (from tests/e2e) draws them, so text uses the real fonts.
//   cd tests/e2e && bun install && bunx playwright install chromium   (once)
//   cd site && bun install                                             (once, for the fonts)
//   bun scripts/brand-images.ts
// Commit the output; nothing renders these at build time.
import { createRequire } from 'node:module';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { en } from '../site/src/i18n/en';
import { da } from '../site/src/i18n/da';

const root = join(import.meta.dir, '..');
const { chromium } = createRequire(join(root, 'tests/e2e/package.json'))('@playwright/test');

// Brand palette (docs/branding.md#brand-palette).
const KALK = '#F2F4F3';
const INK = '#16201D';
const VERDIGRIS = '#2C7A6A';
const MUTE = '#56655F';

const mark = (ink = INK, acc = VERDIGRIS) =>
  `<svg viewBox="0 0 32 32" xmlns="http://www.w3.org/2000/svg">` +
  `<rect x="3" y="6.5" width="17" height="5" rx="2.5" fill="${ink}"/>` +
  `<rect x="9" y="13.5" width="20" height="5" rx="2.5" fill="${acc}"/>` +
  `<rect x="5" y="20.5" width="12" height="5" rx="2.5" fill="${ink}" opacity=".5"/></svg>`;

const font = (family: string, file: string) => {
  const data = readFileSync(join(root, 'site/node_modules', file)).toString('base64');
  return `@font-face{font-family:'${family}';src:url(data:font/woff2;base64,${data}) format('woff2');font-weight:100 900}`;
};
const fonts =
  font('Display', '@fontsource-variable/bricolage-grotesque/files/bricolage-grotesque-latin-wght-normal.woff2') +
  font('Ui', '@fontsource-variable/hanken-grotesk/files/hanken-grotesk-latin-wght-normal.woff2');

const page = (body: string, css = '') =>
  `<!doctype html><html><head><style>${fonts}*{margin:0;box-sizing:border-box}html,body{width:100%;height:100%}` +
  `body{background:transparent}${css}</style></head><body>${body}</body></html>`;

/** Mark centred on a square; `pad` is the share of the side left empty around the 32-unit box. */
const icon = (pad: number, background: string) =>
  page(
    `<div>${mark()}</div>`,
    `div{width:100%;height:100%;display:grid;place-items:center;background:${background}}` +
      `svg{width:${(1 - pad) * 100}%;height:${(1 - pad) * 100}%}`,
  );

/** Social card: lockup, headline, one line of detail, and the plan bars bleeding off the right edge. */
const card = (title: string, detail: string) =>
  page(
    `<main><div class="lockup">${mark()}<span>kupro</span></div>` +
      `<h1>${title}</h1><p>${detail}</p></main>` +
      `<div class="plan"><i style="left:0;width:62%"></i><i class="acc" style="left:22%;width:78%"></i>` +
      `<i class="dim" style="left:8%;width:44%"></i><i style="left:38%;width:62%"></i></div>`,
    `body{background:${KALK};color:${INK};font-family:Ui;position:relative;overflow:hidden}` +
      `main{position:absolute;left:80px;top:72px;bottom:72px;width:62%;display:flex;flex-direction:column}` +
      `.lockup{display:flex;align-items:center;gap:18px;font:700 50px Display;letter-spacing:-.01em}` +
      `.lockup svg{width:64px;height:64px}` +
      `h1{margin-top:auto;font:700 74px/1.04 Display;letter-spacing:-.02em;text-wrap:balance}` +
      `p{margin-top:28px;font:500 30px/1.35 Ui;color:${MUTE}}` +
      `.plan{position:absolute;right:-60px;top:50%;width:34%;height:248px;transform:translateY(-50%)}` +
      `.plan i{position:absolute;height:44px;border-radius:22px;background:${INK}}` +
      `.plan i:nth-child(1){top:0}.plan i:nth-child(2){top:68px}.plan i:nth-child(3){top:136px}.plan i:nth-child(4){top:204px}` +
      `.plan .acc{background:${VERDIGRIS}}.plan .dim{opacity:.5}.plan i:nth-child(4){opacity:.18}`,
  );

/** ICO holding PNG images (supported by every browser that still asks for /favicon.ico). */
const ico = (pngs: { size: number; png: Buffer }[]) => {
  const header = Buffer.alloc(6 + 16 * pngs.length);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(pngs.length, 4);
  let offset = header.length;
  pngs.forEach(({ size, png }, i) => {
    const at = 6 + 16 * i;
    header.writeUInt8(size, at);
    header.writeUInt8(size, at + 1);
    header.writeUInt16LE(1, at + 4);
    header.writeUInt16LE(32, at + 6);
    header.writeUInt32LE(png.length, at + 8);
    header.writeUInt32LE(offset, at + 12);
    offset += png.length;
  });
  return Buffer.concat([header, ...pngs.map((p) => p.png)]);
};

const browser = await chromium.launch();
const render = async (html: string, width: number, height = width) => {
  const tab = await browser.newPage({ viewport: { width, height } });
  await tab.setContent(html);
  await tab.evaluate(() => document.fonts.ready);
  const png = await tab.screenshot({ omitBackground: true });
  await tab.close();
  return png;
};
const write = (path: string, data: Buffer) => {
  const file = join(root, path);
  mkdirSync(dirname(file), { recursive: true });
  writeFileSync(file, data);
  console.log(`wrote ${path} (${data.length} bytes)`);
};

// Tight mark at favicon sizes; app icons sit on Kalk because iOS and Android fill transparency with black.
const favicon = ico(await Promise.all([16, 32, 48].map(async (size) => ({ size, png: await render(icon(0, 'transparent'), size) }))));
const icons = {
  'favicon.ico': favicon,
  'apple-touch-icon.png': await render(icon(0.2, KALK), 180),
  'icon-192.png': await render(icon(0.16, KALK), 192),
  'icon-512.png': await render(icon(0.16, KALK), 512),
  // Maskable: the mark stays inside the 80% safe circle.
  'icon-maskable-512.png': await render(icon(0.36, KALK), 512),
};
for (const dir of ['web/public', 'site/public']) {
  for (const [name, data] of Object.entries(icons)) write(`${dir}/${name}`, data);
}
// Keycloak's login template links img/favicon.ico.
write('infrastructure/keycloak/themes/kupro/login/resources/img/favicon.ico', favicon);

write('site/public/og-image.png', await render(card(en.hero.title, en.meta.ogDetail), 1200, 630));
write('site/public/og-image-da.png', await render(card(da.hero.title, da.meta.ogDetail), 1200, 630));
// GitHub → Settings → Social preview (uploaded by hand); speaks to contributors, so the product tagline.
write('docs/images/social-preview.png', await render(card('Plan the year. Ship the week.', 'Open-source issue tracker and planner your agents can use too.'), 1280, 640));

await browser.close();
