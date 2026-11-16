import { expect, test } from "@playwright/test";

const lessonId = process.env.ACCESSLEARN_TEST_LESSON_ID;
const applicationOrigin = new URL(process.env.ACCESSLEARN_TEST_BASE_URL ?? "https://localhost:7121").origin;

test.skip(!lessonId, "Set ACCESSLEARN_TEST_LESSON_ID to a published lesson with a Ready offline package.");

async function download(page: import("@playwright/test").Page): Promise<void> {
  await page.goto(`/offline/?lesson=${lessonId}`);
  await page.getByRole("button", { name: "Download", exact: true }).click();
  await expect(page.getByRole("status")).toContainText("Download complete");
  await expect(page.getByText("Ready offline")).toBeVisible();
}

test("downloads, activates, and reloads without a network", async ({ page, context }) => {
  await download(page);
  await context.setOffline(true);
  await page.reload();
  await expect(page.getByText("Ready offline")).toBeVisible();
  await page.getByRole("button", { name: "Open" }).click();
  await expect(page.locator("audio")).toBeVisible();
  await expect(page.locator(".lesson-view section").first()).toBeVisible();
  await context.setOffline(false);
});

test("interrupted asset download remains resumable and removable", async ({ page }) => {
  let aborted = false;
  await page.route("**/*", async (route) => {
    const url = new URL(route.request().url());
    if (!aborted && url.origin !== applicationOrigin && url.pathname.includes("/offline/")) {
      aborted = true;
      await route.abort();
      return;
    }
    await route.continue();
  });
  await page.goto(`/offline/?lesson=${lessonId}`);
  await page.getByRole("button", { name: "Download", exact: true }).click();
  await expect(page.getByRole("status")).toContainText("could not be downloaded");
  await expect(page.getByRole("button", { name: "Download", exact: true }).last()).toBeVisible();
  await page.unroute("**/*");
  await page.getByRole("button", { name: "Download", exact: true }).last().click();
  await expect(page.getByRole("status")).toContainText("Download complete");
  await page.getByRole("button", { name: "Remove" }).click();
  await expect(page.getByText("No offline lessons have been downloaded.")).toBeVisible();
});

test("offline library remains usable on a narrow mobile viewport", async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 740 });
  await page.goto(`/offline/?lesson=${lessonId}`);
  await expect(page.getByRole("heading", { name: "Offline lesson library" })).toBeVisible();
  await expect(page.locator("main")).not.toHaveCSS("overflow-x", "scroll");
  await expect(page.getByRole("button", { name: "Download", exact: true })).toBeVisible();
});
