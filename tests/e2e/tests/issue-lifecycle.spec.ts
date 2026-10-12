import { expect, test } from '@playwright/test';
import { createIssue, signInToProject } from './helpers';

test('create, move and close an issue, keyboard first', async ({ page }) => {
  await signInToProject(page);
  const title = `E2E lifecycle ${Date.now()}`;

  // Create: C opens the dialog, Ctrl+Enter creates, the panel opens on the new issue.
  const key = await createIssue(page, title);
  const panel = page.getByRole('complementary');

  // Move: drag the card on the board from Todo to In Progress. A column near the board's edge would start dnd-kit's
  // auto-scroll mid-drag and miss the drop.
  await page.keyboard.press('Escape');
  await page.getByRole('tab', { name: 'Board' }).click();
  const card = page.locator(`article[data-key="${key}"]`);
  const target = page.locator('section[data-state="inProgress"]');
  await expect(card).toBeVisible();
  // Earlier runs' issues can push the new card below the fold.
  await card.scrollIntoViewIfNeeded();
  const from = (await card.boundingBox())!;
  const to = (await target.boundingBox())!;
  await page.mouse.move(from.x + from.width / 2, from.y + from.height / 2);
  await page.mouse.down();
  await page.mouse.move(from.x + from.width / 2 + 10, from.y + from.height / 2 + 10, { steps: 4 });
  await page.mouse.move(to.x + to.width / 2, to.y + 60, { steps: 12 });
  // dnd-kit swallows clicks for 50 ms after a drop; waiting for the save also outlasts that.
  const moved = page.waitForResponse((r) => r.request().method() === 'POST' && r.url().endsWith(`/issues/${key}/move`));
  await page.mouse.up();
  await expect(target.locator(`article[data-key="${key}"]`)).toBeVisible();
  expect((await moved).ok()).toBe(true);

  // Close: open the issue, S opens the state picker in the palette, pick Done.
  await card.click();
  await expect(panel.getByLabel('Title')).toHaveValue(title);
  await page.keyboard.press('s');
  await page.getByPlaceholder('Change state…').fill('Done');
  const saved = page.waitForResponse((r) => r.request().method() === 'PATCH' && r.url().endsWith(`/issues/${key}`));
  await page.keyboard.press('Enter');
  // Optimistic: the new state shows before the API answers (ADR-003).
  await expect(panel.getByRole('img', { name: 'Done' }).first()).toBeVisible();
  expect((await saved).ok()).toBe(true);

  // The change survives a reload (it reached the API, not just the cache).
  await page.reload();
  await expect(page.locator(`section[data-state="done"] article[data-key="${key}"]`)).toBeVisible();
});
