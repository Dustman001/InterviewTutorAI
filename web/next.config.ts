import type { NextConfig } from "next";

// /api/* is forwarded to TutorAI.Api by app/api/[...path]/route.ts, which reads TUTORAI_API_URL at
// request time and attaches the signed-in user's JWT.
const nextConfig: NextConfig = {
  // A self-contained server.js with only the runtime files it needs, so the app is built once (locally or
  // in CI) and App Service just runs it. Building on a small App Service plan is too slow.
  output: "standalone",
  cacheComponents: true,
  partialPrefetching: true,
};

export default nextConfig;
