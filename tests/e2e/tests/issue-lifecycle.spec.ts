import { expect, test, type Page } from '@playwright/test';

/** Signs in through Keycloak and lands in a workspace with a project (creating both on a fresh account). */
async function signInToProject(page: Page) {
  await page.goto('/');
  await page.getByRole('textbox', { name: 'Email' }).fill(process.env.FLOWSTATE_USER ?? 'dev@flowstate.local');
  await page.getByRole('textbox', { name: 'Password' }).fill(process.env.FLOWSTATE_PASSWORD ?? 'dev');
  await page.getByRole('button', { name: /sign in/i }).click();

  const onboarding = page.getByRole('heading', { name: 'Create your workspace' });
  const shell = page.getByRole('navigation', { name: 'Main' });
  await expect(onboarding.or(shell)).toBeVisible();
  if (await onboarding.isVisible()) {
    await page.getByLabel('Workspace name').fill(`E2E ${Date.now()}`);
    await page.getByRole('button', { name: 'Create workspace' }).click();
  }

  const newProject = page.getByRole('main').getByRole('heading', { name: 'New project' });
  const list = page.getByRole('tablist', { name: 'View' });
  await expect(newProject.or(list)).toBeVisible();
  if (await newProject.isVisible()) {
    await page.getByLabel('Project name').fill('Flowstate');
    await page.getByRole('button', { name: 'Create project' }).click();
  }

  await expect(list).toBeVisible();
}

test('create, move and close an issue, keyboard first', async ({ page }) => {
  await signInToProject(page);
  const title = `E2E lifecycle ${Date.now()}`;

  // Create: C opens the dialog, Ctrl+Enter creates, the panel opens on the new issue.
  await page.keyboard.press('c');
  await page.getByLabel('Title').fill(title);
  await page.keyboard.press('Control+Enter');
  const panel = page.getByRole('complementary');
  await expect(panel.getByLabel('Title')).toHaveValue(title);
  const key = (await panel.locator('header span.font-mono').textContent())!.trim();

  // Move: drag the card on the board from Todo to In Review.
  await page.keyboard.press('Escape');
  await page.getByRole('tab', { name: 'Board' }).click();
  const card = page.locator(`article[data-key="${key}"]`);
  const review = page.locator('section[data-state="inReview"]');
  await expect(card).toBeVisible();
  const from = (await card.boundingBox())!;
  const to = (await review.boundingBox())!;
  await page.mouse.move(from.x + from.width / 2, from.y + from.height / 2);
  await page.mouse.down();
  await page.mouse.move(from.x + from.width / 2 + 10, from.y + from.height / 2 + 10, { steps: 4 });
  await page.mouse.move(to.x + to.width / 2, to.y + 60, { steps: 12 });
  await page.mouse.up();
  await expect(review.locator(`article[data-key="${key}"]`)).toBeVisible();

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
