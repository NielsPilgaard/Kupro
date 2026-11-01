import { expect, test } from '@playwright/test';
import { scrollUntilLoaded, signInToProject } from './helpers';

test('board columns and list groups load past the first page as they scroll', async ({ page }) => {
  await signInToProject(page);
  const [, workspace, project] = /\/w\/([^/]+)\/p\/([^/?]+)/.exec(page.url())!;

  // Borrow the SPA's token for a bulk create: more issues in one state than a page holds (50).
  const authorized = page.waitForRequest((r) => r.url().includes('/api/v1/') && !!r.headers()['authorization']);
  await page.reload();
  const authorization = (await authorized).headers()['authorization']!;
  const run = Date.now();
  const created = await page.request.post(`/api/v1/workspaces/${workspace}/issues/bulk`, {
    headers: { authorization },
    data: { issues: Array.from({ length: 60 }, (_, i) => ({ project, title: `Paging ${run} ${i}`, state: 'inReview' })) },
  });
  expect(created.status()).toBe(201);
  // The board puts a bulk create on top in request order, the list shows newest first: each view's off-page issue is
  // at the other end.
  const keys = ((await created.json()) as { key: string }[]).map((i) => i.key);
  const last = keys.at(-1)!;
  const first = keys[0]!;

  await page.getByRole('tab', { name: 'Board' }).click();
  const column = page.locator('section[data-state="inReview"]');
  await expect(column.getByRole('heading')).toContainText('+');
  await scrollUntilLoaded(column, column.locator(`article[data-key="${last}"]`));

  await page.getByRole('tab', { name: 'List' }).click();
  const group = page.getByRole('region', { name: 'In Review' });
  await scrollUntilLoaded(group, group.locator(`[data-key="${first}"]`));
});
