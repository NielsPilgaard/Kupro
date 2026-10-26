import { expect, type Page, test } from '@playwright/test';

// README screenshots (task 022), light and dark. Opt-in: needs the workspace from scripts/demo-seed.ps1, signed in as
// the user who ran it. other@kupro.local has no other workspaces, so the sidebar stays clean:
//   KUPRO_SCREENSHOTS=1 KUPRO_USER=other@kupro.local KUPRO_PASSWORD=other bunx playwright test readme-screenshots
const workspace = process.env.KUPRO_DEMO_WORKSPACE ?? 'lindholm';
const images = new URL('../../../docs/images/', import.meta.url);

test.skip(!process.env.KUPRO_SCREENSHOTS, 'Set KUPRO_SCREENSHOTS=1 to retake the README screenshots.');
// --lang: the native month inputs on the plan follow the OS language otherwise.
test.use({ viewport: { width: 1760, height: 680 }, launchOptions: { args: ['--lang=en-US'] } });

function month(offset: number) {
  const d = new Date();
  d.setMonth(d.getMonth() + offset, 1);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`;
}

async function shoot(page: Page, name: string) {
  // No caret or hover state in the picture.
  await page.mouse.move(0, 0);
  await page.screenshot({ path: new URL(`${name}.png`, images).pathname.replace(/^\/([A-Za-z]:)/, '$1') });
}

for (const scheme of ['light', 'dark'] as const) {
  test.describe(scheme, () => {
    test.use({ colorScheme: scheme });

    test('sprint board', async ({ page }) => {
      await page.goto(`/w/${workspace}/sprints?project=APP`);
      await expect(page.getByText('Retry uploads with backoff')).toBeVisible();
      await shoot(page, `board-${scheme}`);
    });

    test('delivery plan', async ({ page }) => {
      await page.goto(`/w/${workspace}/plan?from=${month(0)}&to=${month(7)}`);
      await page.getByRole('tab', { name: 'Months' }).click();
      await expect(page.getByText('Routing engine v2')).toBeVisible();
      await shoot(page, `plan-${scheme}`);
    });
  });
}
