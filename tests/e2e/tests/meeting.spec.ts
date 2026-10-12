import { expect, test } from '@playwright/test';
import { createIssue, signInToProject } from './helpers';

// Typed transcript, so it runs without an AI key: the API's keyword extractor makes the proposals (task 015).
test('meeting agent proposes, applies and undoes changes', async ({ page }) => {
  await signInToProject(page);
  const key = await createIssue(page, `E2E meeting ${Date.now()}`);
  await page.keyboard.press('Escape');
  const other = await createIssue(page, `E2E meeting other ${Date.now()}`);
  await page.keyboard.press('Escape');
  await page.getByRole('tab', { name: 'Board' }).click();
  const cardIn = (state: string, issue = key) => page.locator(`section[data-state="${state}"] article[data-key="${issue}"]`);

  await page.getByRole('button', { name: 'Start meeting' }).click();
  const meeting = page.getByRole('complementary', { name: 'Meeting' });
  await expect(meeting.getByText('Listening')).toBeVisible();
  const say = async (text: string) => {
    await meeting.getByLabel('What was said').fill(text);
    await meeting.getByRole('button', { name: 'Send' }).click();
  };

  // Suggest: one proposal under the issue with diff and snippet; nothing changes until it is approved.
  await say(`Let's move ${key} to in progress`);
  const proposal = meeting.getByRole('article', { name: new RegExp(key) });
  await expect(proposal).toContainText('→ In Progress');
  await expect(proposal).toContainText(`move ${key} to in progress`);
  await expect(cardIn('inProgress')).toHaveCount(0);
  await proposal.getByRole('button', { name: 'Approve', exact: true }).click();
  await expect(cardIn('inProgress')).toBeVisible();

  await meeting.getByRole('button', { name: `Undo change to ${key}` }).click();
  await expect(cardIn('inProgress')).toHaveCount(0);

  // Full auto: the account owns the workspace, so it may allow it; clear changes apply at once.
  // The checkbox shows the saved setting, so it flips once the save returns; a rerun finds it already on.
  const allow = meeting.getByRole('checkbox', { name: /Allow full auto/ });
  if (!(await allow.isChecked())) await allow.click();
  await expect(allow).toBeChecked();
  const fullAuto = meeting.getByRole('radio', { name: 'Full auto' });
  await fullAuto.click();
  await expect(fullAuto).toHaveAttribute('aria-checked', 'true');
  await say(`${key} is done. ${other} is in progress.`);
  await expect(cardIn('done')).toBeVisible();
  await expect(cardIn('inProgress', other)).toBeVisible();

  page.once('dialog', (dialog) => void dialog.accept());
  await meeting.getByRole('button', { name: 'Undo meeting' }).click();
  await expect(cardIn('done')).toHaveCount(0);
  await expect(cardIn('inProgress', other)).toHaveCount(0);

  await meeting.getByRole('button', { name: 'End meeting' }).click();
  await expect(meeting.getByText('Meeting ended')).toBeVisible();
});
