import { defineConfig } from "@playwright/test";

/**
 * Browser smoke test against a running stack (web app + TutorAI.Api + database). Opt-in: it runs only when
 * TUTORAI_E2E_WEB, TUTORAI_E2E_USER and TUTORAI_E2E_PASSWORD are set, and it calls Claude for real.
 * Uses the installed Chrome, so no Playwright browser download is needed.
 */
export default defineConfig({
  testDir: "e2e",
  timeout: 6 * 60 * 1000,
  retries: 0,
  reporter: "list",
  use: {
    baseURL: process.env.TUTORAI_E2E_WEB,
    channel: "chrome",
    trace: "retain-on-failure",
    launchOptions: {
      // A fake microphone and no autoplay prompt, so the session runs unattended.
      args: [
        "--use-fake-ui-for-media-stream",
        "--use-fake-device-for-media-stream",
        "--autoplay-policy=no-user-gesture-required",
      ],
    },
  },
});
