import type { NextConfig } from "next";

// /api/* is forwarded to TutorAI.Api by app/api/[...path]/route.ts, which reads TUTORAI_API_URL at
// request time and attaches the signed-in user's JWT.
const nextConfig: NextConfig = {
  cacheComponents: true,
  partialPrefetching: true,
};

export default nextConfig;
