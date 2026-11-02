import { expect, test } from '@playwright/test';
import { signInToProject } from './helpers';

const day = (offset: number) => new Date(Date.now() + offset * 86_400_000).toISOString().slice(0, 10);

// Task 028. The sprint and its issues come through the API; the review is what's under test.
test('close a sprint per issue with a split, then read its report', async ({ page }) => {
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

  // Its own project, so a sprint left active by an earlier run doesn't get in the way.
  const project = `S${Date.now().toString(36).toUpperCase().slice(-6)}`;
  await post('/projects', { key: project, name: `Close ${project}` });
  const one = await post('/sprints', { project, name: 'One', startDate: day(0), endDate: day(11) });
  await post('/sprints', { project, name: 'Two', startDate: day(12), endDate: day(23) });
  await post(`/sprints/${one.id}/start`);
  const issues = await post('/issues/bulk', {
    issues: ['Half done', 'For later', 'Finished after all', 'Next time'].map((title) => ({ project, title, state: 'inProgress', effort: 8, sprint: 'active' })),
  });
  const [split, later, finished, carried] = issues.map((i: { key: string }) => i.key) as [string, string, string, string];

  await page.goto(`/w/${workspace}/sprints?project=${project}`);
  await page.getByRole('button', { name: 'Close sprint' }).click();
  await expect(page.getByRole('heading', { name: 'Close One' })).toBeVisible();
  const row = (key: string) => page.locator(`li[data-key="${key}"]`);
  const close = page.getByRole('button', { name: 'Close sprint' });

  // Split starts with an empty, focused hours-left input, and blocks the close until it is valid.
  await row(split).getByRole('radio', { name: 'Split' }).click();
  const hoursLeft = row(split).getByLabel('Hours left');
  await expect(hoursLeft).toBeFocused();
  await expect(hoursLeft).toHaveValue('');
  await expect(close).toBeDisabled();
  await hoursLeft.fill('8');
  await expect(close).toBeDisabled();
  await hoursLeft.fill('3');
  await expect(row(split)).toContainText('new issue with 3h to Two');
  await expect(close).toBeEnabled();

  // Keyboard: J moves to the next row, B sends it to the backlog.
  await page.getByRole('heading', { name: 'Unfinished work' }).click();
  await page.keyboard.press('j');
  await page.keyboard.press('b');
  await expect(row(later).getByRole('radio', { name: 'Backlog' })).toHaveAttribute('aria-checked', 'true');
  await row(finished).getByRole('radio', { name: 'Done' }).click();
  await expect(row(carried).getByRole('radio', { name: 'Carry' })).toHaveAttribute('aria-checked', 'true');
  const summary = page.getByTestId('close-summary');
  await expect(summary).toContainText('1 carry (8h)');
  await expect(summary).toContainText('1 split (5h done here, 1 new issue)');
  await expect(summary).toContainText('1 to backlog');

  await page.getByRole('checkbox', { name: 'Start Two after closing' }).check();
  await close.click();

  // The same page becomes the read-only report.
  await expect(page.getByRole('heading', { name: 'One: sprint report' })).toBeVisible();
  await expect(page.getByRole('radio')).toHaveCount(0);
  await expect(page.getByTestId('remaining-line').first()).toHaveAttribute('d', /^M/);
  const report = page.getByRole('region', { name: 'Completed (2)' });
  await expect(report).toContainText(finished);
  await expect(report.getByText('Continued in')).toBeVisible();

  // The original links on to its continuation.
  await report.getByRole('link', { name: split }).click();
  const panel = page.getByRole('complementary');
  await panel.getByRole('tab', { name: /Relations/ }).click();
  await expect(panel.getByText('Continued in')).toBeVisible();
});
