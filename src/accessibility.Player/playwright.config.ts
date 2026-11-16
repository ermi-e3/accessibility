import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./tests",
  timeout: 120_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  use: {
    baseURL: process.env.ACCESSLEARN_TEST_BASE_URL ?? "https://localhost:7121",
    ignoreHTTPSErrors: true,
    serviceWorkers: "allow",
    trace: "retain-on-failure"
  },
  reporter: [["list"]]
});
