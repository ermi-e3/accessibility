import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';

test.beforeEach(async ({ page }) => {
  await page.route('**/*', async (route) => {
    const url = new URL(route.request().url());
    if (url.origin === 'http://127.0.0.1:4173') await route.continue();
    else await route.abort();
  });
});

test('judge showcase exposes a clear three-module route', async ({ page }) => {
  await page.setViewportSize({ width: 640, height: 720 });
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'One accessible path from learning to work.' })).toBeVisible();
  await expect(page.locator('.judge-route > li')).toHaveCount(3);
  await expect(page.getByRole('link', { name: 'Start the 3-minute demo' })).toHaveAttribute(
    'href',
    'pages/concept-with-navigation.html#accessible-lesson-title',
  );
  const hasHorizontalOverflow = await page.evaluate(
    () => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1,
  );
  expect(hasHorizontalOverflow).toBe(false);
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze();
  expect(results.violations).toEqual([]);
});

test('career cards reflow at 200% and speech controls remain operable', async ({ page }) => {
  await page.addInitScript(() => {
    class FakeUtterance extends EventTarget {
      text: string;
      lang = '';
      rate = 1;
      constructor(text: string) { super(); this.text = text; }
    }
    Object.defineProperty(window, 'SpeechSynthesisUtterance', { configurable: true, value: FakeUtterance });
    Object.defineProperty(window, 'speechSynthesis', {
      configurable: true,
      value: {
        cancel() {},
        speak(utterance: EventTarget) { utterance.dispatchEvent(new Event('start')); },
      },
    });
  });
  await page.setViewportSize({ width: 640, height: 720 });
  await page.goto('/pages/jobs.html');

  await expect(page.locator('[data-job-card]')).toHaveCount(3);
  const firstListen = page.locator('[data-listen-summary]').first();
  await firstListen.click();
  await expect(firstListen).toHaveAttribute('aria-pressed', 'true');
  await expect(firstListen).toContainText('Stop summary');
  await firstListen.click();
  await expect(firstListen).toHaveAttribute('aria-pressed', 'false');
  await expect(page.locator('[data-speech-status]').first()).toContainText('stopped');

  const hasHorizontalOverflow = await page.evaluate(
    () => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1,
  );
  expect(hasHorizontalOverflow).toBe(false);
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze();
  expect(results.violations).toEqual([]);
});

test('certificate URL returns a public verified registry record', async ({ page }) => {
  await page.goto('/pages/certificate.html?id=ALX-VA1-AMARA-2026');
  await expect(page.locator('[data-verification-status]')).toContainText('Verified:');
  await expect(page.locator('[data-certificate-record]')).toBeVisible();
  await expect(page.locator('[data-verification-link]')).toHaveAttribute('href', /ALX-VA1-AMARA-2026/);
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze();
  expect(results.violations).toEqual([]);
});

test('Savanna concept loads the captioned sample and emits completion', async ({ page }) => {
  await page.goto('/pages/concept-with-navigation.html');
  const orderIsCorrect = await page.locator('#curriculum_navigation_content .savanna-styles').evaluate((scope) => {
    const banner = scope.querySelector(':scope > p');
    const player = scope.querySelector('accesslearn-player');
    const figure = scope.querySelector(':scope > figure');
    if (!banner || !player || !figure) return false;
    return Boolean(banner.compareDocumentPosition(player) & Node.DOCUMENT_POSITION_FOLLOWING)
      && Boolean(player.compareDocumentPosition(figure) & Node.DOCUMENT_POSITION_FOLLOWING);
  });
  expect(orderIsCorrect).toBe(true);
  const player = page.locator('accesslearn-player');
  await expect(player.locator('[data-status]')).toContainText('Lesson ready', { timeout: 10_000 });
  await expect(player.locator('[data-caption-select]')).toHaveValue('en');
  await player.locator('[data-transcript-button]').click();
  await expect(player.locator('[data-segments] > li')).toHaveCount(4);
  await player.locator('[data-caption-select]').selectOption('am');
  await expect(player.locator('[data-transcript-language]')).toHaveValue('am');
  await expect(player.locator('[data-segments] > li').first()).toContainText(/[\u1200-\u137f]/u);
  await player.locator('[data-transcript-search]').fill('ትምህርት');
  await expect(player.locator('[data-caption-select]')).toHaveValue('am');
  await player.locator('[data-transcript-search]').fill('');

  await player.locator('[data-sign-button]').click();
  const signWindow = player.locator('[data-sign-window]');
  await expect(signWindow).toBeVisible();
  await expect(player.locator('[data-sign-label]')).toContainText('educator recording required');
  const initialBox = await signWindow.boundingBox();
  await signWindow.focus();
  await signWindow.press('ArrowLeft');
  const movedBox = await signWindow.boundingBox();
  expect(movedBox!.x).toBeLessThan(initialBox!.x);
  await signWindow.press('Control+ArrowRight');
  const resizedBox = await signWindow.boundingBox();
  expect(resizedBox!.width).toBeGreaterThanOrEqual(movedBox!.width);
  await player.locator('[data-sign-minimize]').click();
  await expect(signWindow).toHaveAttribute('data-minimized', 'true');
  await player.locator('[data-sign-minimize]').click();
  await player.locator('[data-sign-maximize]').click();
  await expect(signWindow).toHaveAttribute('data-maximized', 'true');
  await player.locator('[data-sign-maximize]').click();
  await player.locator('[data-sign-close]').press('Enter');
  await expect(signWindow).toBeHidden();

  const downloadPromise = page.waitForEvent('download');
  await player.locator('[data-offline-download]').click();
  const download = await downloadPromise;
  expect(download.suggestedFilename()).toContain('complete-demo.zip');
  const video = player.locator('video[data-media]');
  await expect.poll(() => video.evaluate((element: HTMLVideoElement) => element.duration)).toBeGreaterThan(20);
  await video.evaluate(async (element: HTMLVideoElement) => {
    element.muted = true;
    element.currentTime = 0;
    element.playbackRate = 8;
    await element.play();
  });
  await expect(page.locator('[data-accesslearn-integration]')).toHaveAttribute('data-lesson-complete', 'true', { timeout: 10_000 });
  await expect(page.locator('[data-lesson-progress-status]')).toContainText('Lesson complete');
  await expect(page.getByRole('link', { name: 'Explore career opportunities' })).toHaveAttribute('href', 'jobs.html');
});

test('sample manifest and video support static same-origin delivery', async ({ request }) => {
  const manifest = await request.get('/api/v1/public/lessons/demo-accessible-careers/manifest');
  expect(manifest.ok()).toBe(true);
  expect(manifest.headers()['content-type']).toContain('application/json');
  expect((await manifest.json()).captions[0]).toMatchObject({ language: 'en', isHumanReviewed: true });

  const video = await request.get('/assets/media/accesslearn-demo/sample-lesson.webm', {
    headers: { Range: 'bytes=0-1023' },
  });
  expect(video.status()).toBe(206);
  expect(video.headers()['content-type']).toBe('video/webm');
  expect(video.headers()['content-range']).toMatch(/^bytes 0-1023\//);
  expect((await video.body()).byteLength).toBe(1024);

  const offline = await request.get('/assets/offline/accesslearn-demo/accesslearn-demo-complete.zip');
  expect(offline.ok()).toBe(true);
  expect(offline.headers()['content-type']).toBe('application/zip');
  expect((await offline.body()).byteLength).toBeGreaterThan(300_000);
});
