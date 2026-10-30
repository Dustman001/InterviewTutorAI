import { expect, test } from "@playwright/test";

const { TUTORAI_E2E_WEB, TUTORAI_E2E_USER, TUTORAI_E2E_PASSWORD } = process.env;

// About 60 words each, so three turns pass the 150-word minimum and the session is really graded.
const TURNS = [
  "Last summer I travelled to Portugal with my family for two weeks. We stayed in a small house near the beach, and every morning we was going to the market to buy fresh fish and fruits. It was very relaxing for all of us, and the weather was warm and sunny almost every day of the trip.",
  "I think the most interesting place was Porto, because the city have a lot of history and the people is very friendly. We walked along the river, we tried the local food, and we visited an old bookshop. If I would have more time, I would stay there one more week to see the countryside too.",
  "Next year I want to visit Japan, because I am interested in the culture since many years. I am learning some basic words, but the language is very difficult for me. I hope I can to travel there in the spring, when the trees are with flowers, and maybe I will visit Kyoto and Osaka.",
];

test.skip(
  !TUTORAI_E2E_WEB || !TUTORAI_E2E_USER || !TUTORAI_E2E_PASSWORD,
  "Set TUTORAI_E2E_WEB, TUTORAI_E2E_USER and TUTORAI_E2E_PASSWORD to run the browser smoke test.",
);

test("sign in, hold a typed conversation, end it and see it graded", async ({ page }) => {
  await page.goto("/login");
  await page.getByLabel("Username").fill(TUTORAI_E2E_USER!);
  await page.getByLabel("Password").fill(TUTORAI_E2E_PASSWORD!);
  await page.getByRole("button", { name: "Sign in" }).click();
  // The first request can wait up to a minute while a paused serverless database resumes.
  await expect(page).toHaveURL(/\/$/, { timeout: 90_000 });
  // The user bar appears only once the page's script runs. A click before that submits the form natively and
  // just reloads the page.
  await expect(page.getByText("Signed in as")).toBeVisible({ timeout: 30_000 });

  await page.getByRole("button", { name: "Start session" }).click();
  await expect(page).toHaveURL(/\/session\/[0-9a-f-]+$/, { timeout: 30_000 });

  await page.getByRole("button", { name: "Start", exact: true }).click();
  const status = page.locator(".status");
  const turnInput = page.getByLabel("Type your turn");

  // The tutor's opening line is streamed and spoken, then it's the learner's turn.
  await expect(page.locator(".line.tutor")).toHaveCount(1, { timeout: 60_000 });
  await expect(status).toHaveClass(/listening/, { timeout: 90_000 });

  for (const [i, text] of TURNS.entries()) {
    await turnInput.fill(text);
    await page.getByRole("button", { name: "Send" }).click();
    await expect(page.locator(".line.tutor")).toHaveCount(i + 2, { timeout: 60_000 });
    await expect(status).toHaveClass(/listening/, { timeout: 90_000 });
  }

  await page.getByRole("button", { name: "End & score" }).click();
  await expect(page).toHaveURL(/\/summary$/);

  // Grading runs in the background: the page shows that it's scoring, then the levels.
  await expect(page.getByRole("status")).toContainText("Scoring your session", { timeout: 15_000 });
  await expect(page.locator(".levels .level")).toHaveCount(5, { timeout: 4 * 60_000 });
  await expect(page.getByText(/Overall/)).toBeVisible();
  await expect(page.getByRole("status")).toHaveCount(0);
});
