import { expect, test } from '@playwright/test';
import { signInToProject } from './helpers';

// Task 018: a guest with billing access is free, so the seat count doesn't move.
test('owner invites a guest with billing access and the seat count stays the same', async ({ page }) => {
  await signInToProject(page);
  await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Members' }).click();
  const seats = page.getByTestId('member-counts').locator('span').first();
  await expect(seats).toContainText('seat');
  const before = await seats.textContent();

  const email = `guest-${Date.now()}@e2e.kupro`;
  const form = page.locator('form').filter({ hasText: 'Invite someone' });
  await form.getByLabel('Email').fill(email);
  await form.getByRole('radio', { name: /Guest/ }).check();
  await form.getByRole('checkbox', { name: /Billing access/ }).check();
  await form.getByRole('button', { name: 'Send invite' }).click();

  const invite = page.getByTestId('member-row').filter({ hasText: email });
  await expect(invite.getByLabel(/Role of/)).toHaveValue('guest');
  await expect(invite.getByRole('checkbox', { name: 'Billing access' })).toBeChecked();
  await expect(seats).toHaveText(before!);

  // Deactivate the invite again, so reruns stay under the guest cap.
  await invite.getByRole('button', { name: 'Deactivate' }).click();
  await invite.getByRole('alertdialog').getByRole('button', { name: 'Deactivate' }).click();
  await expect(page.getByTestId('member-row').filter({ hasText: email }).getByLabel(/Role of/)).toHaveCount(0);
});
