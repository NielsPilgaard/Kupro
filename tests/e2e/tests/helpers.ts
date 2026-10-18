import { expect, type Page } from '@playwright/test';

/** Lands in a workspace with a project (creating both on a fresh account), signed in by auth.setup.ts. */
export async function signInToProject(page: Page) {
  await page.goto('/');

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
    await page.getByLabel('Project name').fill('Kupro');
    await page.getByRole('button', { name: 'Create project' }).click();
  }

  await expect(list).toBeVisible();
}

/** C opens the dialog, Ctrl+Enter creates and opens the panel on the new issue. Returns its key. */
export async function createIssue(page: Page, title: string) {
  await page.keyboard.press('c');
  await page.getByLabel('Title').fill(title);
  await page.keyboard.press('Control+Enter');
  const panel = page.getByRole('complementary');
  await expect(panel.getByLabel('Title')).toHaveValue(title);
  return (await panel.locator('header span.font-mono').textContent())!.trim();
}
