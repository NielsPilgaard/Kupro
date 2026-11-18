// Agent-facing files, written after `astro build` from the built pages, so they never drift from the HTML:
// - a Markdown twin of every page (pricing.html -> pricing.md), only the <main> content. nginx serves it for
//   Accept: text/markdown and at the .md URL (nginx.conf).
// - /llms.txt, the index of those twins (llmstxt.org).
// - /.well-known/mcp.json, a card pointing agents at the MCP server in the app.
import { readdir, readFile, writeFile, mkdir } from 'node:fs/promises';
import { join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import TurndownService from 'turndown';
import { gfm } from '@joplin/turndown-plugin-gfm';

const decode = (s) =>
  s.replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;/g, "'");

const meta = (html, re) => {
  const m = html.match(re);
  return m ? decode(m[1]).trim() : '';
};

async function htmlFiles(dir) {
  const out = [];
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name);
    if (entry.isDirectory() && entry.name !== '_astro') out.push(...(await htmlFiles(full)));
    else if (entry.isFile() && entry.name.endsWith('.html') && entry.name !== '404.html') out.push(full);
  }
  return out;
}

function converter(site) {
  const td = new TurndownService({ headingStyle: 'atx', codeBlockStyle: 'fenced', bulletListMarker: '-' });
  td.use(gfm);
  td.remove(['svg', 'script', 'style', 'form', 'button']);
  // Absolute links and images: the Markdown is read out of context, often far from the site.
  td.addRule('absoluteLinks', {
    filter: (node) => node.nodeName === 'A' && node.getAttribute('href'),
    replacement: (content, node) => {
      const text = content.trim();
      const gap = node.nextSibling?.nodeName === 'A' ? ' ' : '';
      return text ? `[${text}](${new URL(node.getAttribute('href'), site).href})${gap}` : '';
    },
  });
  td.addRule('absoluteImages', {
    filter: 'img',
    replacement: (_, node) => {
      const src = node.getAttribute('src');
      return src ? `![${node.getAttribute('alt') ?? ''}](${new URL(src, site).href})` : '';
    },
  });
  // Price units sit in <small> right after the amount: "€9 per seat", not "€9per seat".
  td.addRule('small', { filter: 'small', replacement: (content) => ` ${content.trim()}` });
  // FAQ questions are <details><summary>; keep the question visible as bold text above its answer.
  td.addRule('summary', { filter: 'summary', replacement: (content) => `\n\n**${content.trim()}**\n\n` });
  // Decoration is aria-hidden; product mock-ups are role="img" and read as their label, like an image's alt text.
  td.addRule('hidden', { filter: (node) => node.getAttribute?.('aria-hidden') === 'true', replacement: () => '' });
  td.addRule('mockup', {
    filter: (node) => node.getAttribute?.('role') === 'img',
    replacement: (_, node) => `\n\n_${node.getAttribute('aria-label') ?? ''}_\n\n`,
  });
  // Card titles are <b> with the body in a sibling element; keep them apart ("**< 500 ms** for every action").
  td.addRule('bold', {
    filter: ['b', 'strong'],
    replacement: (content, node) => {
      const next = node.nextSibling;
      const gap = next?.nodeType === 1 ? (/^\p{Lu}/u.test(next.textContent.trim()) ? '\n\n' : ' ') : '';
      return content.trim() ? `**${content.trim()}**${gap}` : '';
    },
  });
  return td;
}

/** @param {{ appUrl: string }} options */
export default function agents({ appUrl }) {
  return {
    name: 'kupro-agents',
    hooks: {
      'astro:build:done': async ({ dir, logger }) => {
        const root = fileURLToPath(dir);
        const site = process.env.SITE_URL ?? 'http://localhost:4321';
        const td = converter(site);
        const pages = [];

        for (const file of await htmlFiles(root)) {
          const html = await readFile(file, 'utf8');
          const main = html.match(/<main[^>]*>([\s\S]*)<\/main>/);
          if (!main) continue;
          const title = meta(html, /<title>([^<]*)<\/title>/);
          let md = td.turndown(main[1]).trim();
          // Start at the page heading (eyebrows above it are decoration); pages without one get the <title>.
          const h1 = md.search(/^# /m);
          md = h1 >= 0 ? md.slice(h1) : `# ${title}\n\n${md}`;
          await writeFile(file.replace(/\.html$/, '.md'), `${md}\n`);

          const rel = relative(root, file).split(sep).join('/').replace(/\.html$/, '');
          pages.push({
            lang: meta(html, /<html lang="([^"]+)"/),
            path: rel === 'index' ? '/' : `/${rel}`,
            md: `/${rel}.md`,
            title,
            description: meta(html, /<meta name="description" content="([^"]*)"/),
          });
        }

        pages.sort((a, b) => (a.path === '/' ? -1 : b.path === '/' ? 1 : a.path.localeCompare(b.path)));
        const home = pages.find((p) => p.path === '/');
        const list = (lang) =>
          pages
            .filter((p) => p.lang === lang)
            .map((p) => `- [${p.title}](${new URL(p.md, site).href})${p.description ? `: ${p.description}` : ''}`)
            .join('\n');
        const mcp = new URL('/mcp', appUrl).href;
        const llms = [
          '# Kupro',
          '',
          `> ${home?.description ?? ''}`,
          '',
          'Every page is also served as Markdown: send `Accept: text/markdown`, or add `.md` to the path.',
          '',
          '## Pages',
          '',
          list('en'),
          '',
          '## For agents',
          '',
          `- [MCP server](${mcp}): streamable HTTP, OAuth sign-in. Card: ${new URL('/.well-known/mcp.json', site).href}`,
          `- [CLI install (macOS, Linux)](${new URL('/install.sh', site).href}): \`curl -fsSL ${new URL('/install.sh', site).href} | sh\``,
          `- [CLI install (Windows)](${new URL('/install.ps1', site).href}): \`irm ${new URL('/install.ps1', site).href} | iex\``,
          '',
          '## Optional',
          '',
          list('da'),
          '',
        ].join('\n');
        await writeFile(join(root, 'llms.txt'), llms);

        // MCP server card (draft format the agentready.md checker reads). Tools are listed by the server after sign-in.
        const card = {
          name: 'Kupro',
          version: '1.0.0',
          description: 'Issue tracker and planner. Create, search and update issues, sprints and plans.',
          transport: 'http',
          endpoint: mcp,
          authentication: { type: 'oauth2', protectedResourceMetadata: new URL('/.well-known/oauth-protected-resource', appUrl).href },
          capabilities: { tools: true },
          documentation: new URL('/llms.txt', site).href,
        };
        await mkdir(join(root, '.well-known'), { recursive: true });
        await writeFile(join(root, '.well-known', 'mcp.json'), `${JSON.stringify(card, null, 2)}\n`);

        logger.info(`${pages.length} Markdown pages, llms.txt and .well-known/mcp.json`);
      },
    },
  };
}
