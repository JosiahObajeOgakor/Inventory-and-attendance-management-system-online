// Fills the Pay online form and presses Continue. With "intercept" the checkout request is captured and answered locally (nothing is created on the server).
const { chromium } = require('playwright-core');
const out = process.argv[2] || '.';
const mode = process.argv[3] || 'intercept';
const base = process.argv[4] || 'https://chewypetsfeeds.com';
const width = Number(process.argv[5] || 1366);
(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: false, args: ['--window-position=-2000,0', '--autoplay-policy=user-gesture-required', '--mute-audio'] });
  const ctx = await browser.newContext({ viewport: { width, height: 900 }, isMobile: width < 600, hasTouch: width < 600 });
  const page = await ctx.newPage();
  const logs = [];
  page.on('console', m => { if (m.type() !== 'info') logs.push(`console.${m.type()}: ${m.text()}`); });
  page.on('pageerror', e => logs.push(`pageerror: ${e.message}`));
  page.on('response', r => { if (r.url().includes('/api/webchat/checkout')) logs.push(`checkout response ${r.status()}`); });
  if (mode === 'intercept') {
    await page.route('**/api/webchat/checkout**', async route => {
      logs.push('CHECKOUT SENT: ' + route.request().url().replace(base, '') + ' ' + route.request().postData());
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ url: base + '/?intercepted=1', number: 'TEST', total: 1 }) });
    });
  }
  await page.goto(base + '/#shop', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.waitForTimeout(4000);
  const yes = page.getByRole('button', { name: /show me what/i });
  if (await yes.count()) await yes.first().click();
  await page.waitForSelector('.card .add', { timeout: 20000 });
  await page.locator('.card .add').first().click();
  await page.getByRole('button', { name: /pay online/i }).first().click();
  await page.waitForTimeout(800);
  await page.screenshot({ path: `${out}/pay-${width}-open.png` });
  const dlg = page.locator('dialog[open]');
  await dlg.locator('#co-name').fill('Website test - please ignore');
  await dlg.locator('#co-phone').fill('08030000000');
  await dlg.locator('button.co__pay').click();
  await page.waitForTimeout(9000);
  logs.push('url after: ' + page.url());
  const err = await page.locator('dialog[open] [role=alert]').allTextContents().catch(() => []);
  if (err.length) logs.push('shown error: ' + err.join(' | '));
  await page.screenshot({ path: `${out}/pay-${width}-after.png` });
  console.log(logs.join('\n'));
  await browser.close();
})().catch(e => { console.error('FAILED', e.message); process.exit(1); });


