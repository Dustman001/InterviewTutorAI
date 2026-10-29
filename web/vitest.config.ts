import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    // Unit tests only. The browser smoke test in e2e/ runs with Playwright (npm run test:e2e).
    include: ["lib/**/*.test.ts"],
  },
});
