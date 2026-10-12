import { expect, test as setup } from '@playwright/test';

export const authFile = '.auth/user.json';

// One Keycloak sign-in per run; the tests reuse its SSO cookie. Signing in once per test trips Keycloak's
// brute-force detection (user_temporarily_disabled) when tests start back to back.
setup('sign in', async ({ page }) => {
  // The app redirects to Keycloak straight away, which can abort a goto that waits for `load`.
  await page.goto('/', { waitUntil: 'commit' });
  await page.getByRole('textbox', { name: 'Email' }).fill(process.env.FLOWSTATE_USER ?? 'dev@flowstate.local');
  await page.getByRole('textbox', { name: 'Password' }).fill(process.env.FLOWSTATE_PASSWORD ?? 'dev');
  await page.getByRole('button', { name: /sign in/i }).click();
  await expect(page.getByRole('heading', { name: 'Create your workspace' }).or(page.getByRole('navigation', { name: 'Main' }))).toBeVisible();
  await page.context().storageState({ path: authFile });
});
