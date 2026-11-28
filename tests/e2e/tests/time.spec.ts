import { expect, test } from '@playwright/test';
import { signInToProject } from './helpers';

// Task 034. The setting, the Log time dialog, the week grid, closing with time, and the Owner's export.
test('turn on time registration, log time, close an issue with time, export', async ({ page }) => {
  let bearer = '';
  page.on('request', (request) => {
    bearer = request.headers().authorization ?? bearer;
  });
  await signInToProject(page);
  await expect.poll(() => bearer).not.toBe('');
  const workspace = new URL(page.url()).pathname.split('/')[2];
  const post = async (path: string, data?: unknown) => {
    const response = await page.request.post(`/api/v1/workspaces/${workspace}${path}`, { headers: { authorization: bearer }, data });
    expect(response.ok(), await response.text()).toBe(true);
    return response.json();
  };

  // Settings: an Owner turns it on.
  await page.goto(`/w/${workspace}/settings`);
  const toggle = page.getByRole('switch', { name: 'Time registration' });
  if ((await toggle.getAttribute('aria-checked')) !== 'true') await toggle.click();
  await expect(toggle).toHaveAttribute('aria-checked', 'true');
  await expect(page.getByRole('link', { name: 'My time' })).toBeVisible();

  // The stack may keep data from earlier runs: Team time is checked against what today held before this one.
  const day = new Intl.DateTimeFormat('en-CA', { timeZone: 'Europe/Copenhagen' }).format(new Date());
  const mine = await page.request.get(`/api/v1/workspaces/${workspace}/time/mine?from=${day}&to=${day}`, { headers: { authorization: bearer } });
  expect(mine.ok(), await mine.text()).toBe(true);
  const before = ((await mine.json()) as { totalMinutes: number }).totalMinutes;
  const hm = (m: number) => (m % 60 ? (m >= 60 ? `${Math.floor(m / 60)}h ${m % 60}m` : `${m}m`) : `${m / 60}h`);

  const project = `T${Date.now().toString(36).toUpperCase().slice(-6)}`;
  await post('/projects', { key: project, name: `Time ${project}` });
  const [worked, closing] = ((await post('/issues/bulk', {
    issues: ['Worked on', 'Closed with time'].map((title) => ({ project, title, state: 'inProgress' })),
  })) as { key: string }[]).map((i) => i.key) as [string, string];

  // T on the open issue logs time on it.
  await page.goto(`/w/${workspace}/p/${project}?view=list&issue=${worked}`);
  const panel = page.getByRole('complementary');
  await expect(panel.getByLabel('Title')).toHaveValue('Worked on');
  await panel.getByRole('button', { name: /Log time/ }).first().waitFor();
  await page.keyboard.press('t');
  const dialog = page.getByRole('dialog', { name: 'Log time' });
  await dialog.getByLabel('Time spent').fill('1h30');
  await page.keyboard.press('Enter');
  await expect(dialog).toBeHidden();
  await expect(page.getByText(`Logged 1h 30m on ${worked}`)).toBeVisible();
  await expect(panel.getByRole('group', { name: 'Time on this issue' })).toContainText('1h 30m');

  // The week grid: the cell holds the time, and changing it edits the entry.
  await page.getByRole('link', { name: 'My time' }).click();
  const cell = page.locator(`input[data-key="${worked}"][aria-label^="${worked}"]:not(:disabled)`).last();
  await expect(cell).toHaveValue('1h 30m');
  await cell.fill('2h');
  await page.keyboard.press('Enter');
  await expect(page.getByRole('table', { name: 'Time per issue and day' }).locator('tfoot')).toContainText(hm(before + 120));

  // Marking Done with no time logged asks for it, and saves both together.
  await page.goto(`/w/${workspace}/p/${project}?view=list&issue=${closing}`);
  await expect(panel.getByLabel('Title')).toHaveValue('Closed with time');
  await panel.getByRole('button', { name: 'State' }).first().click();
  await page.getByRole('option', { name: 'Done' }).click();
  const close = page.getByRole('dialog', { name: 'Mark Done' });
  await close.getByLabel('Time spent').fill('30m');
  await close.getByRole('button', { name: 'Log time and mark Done' }).click();
  await expect(page.getByText(`Saved together: 30m logged and ${closing} marked Done`)).toBeVisible();
  await expect(panel.getByRole('group', { name: 'Time on this issue' })).toContainText('Locked');

  // Team time: the Owner sees day totals and downloads the export.
  await page.getByRole('link', { name: 'Team time' }).click();
  await expect(page.getByRole('table', { name: 'Team time' })).toContainText(hm(before + 150));
  await page.getByRole('button', { name: 'Export…' }).click();
  const download = page.waitForEvent('download');
  await page.getByRole('dialog', { name: 'Export time' }).getByRole('button', { name: 'Download' }).click();
  expect((await download).suggestedFilename()).toMatch(/^kupro-time-.*\.csv$/);
});
