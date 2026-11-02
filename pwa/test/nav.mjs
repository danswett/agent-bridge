// Getting about the app as a person does: the navigation (a rail on a desktop, the bottom bar on
// a phone) and the sessions page's New, which is the way to Launch on a phone.
import { expect } from '@playwright/test';

export async function go(page, route) {
  const item = page.locator(`#nav [data-nav="${route}"]`);
  if (route === 'launch' && !(await item.isVisible())) {
    await go(page, 'sessions');
    await page.locator('.new-session').click();
  } else {
    await item.click();
  }
  await expect(page.locator(`[data-route="${route}"]`)).toBeVisible();
}
