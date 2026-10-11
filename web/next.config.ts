import type { NextConfig } from "next";

const apiUrl = process.env.TUTORAI_API_URL ?? "http://localhost:5080";

const nextConfig: NextConfig = {
  cacheComponents: true,
  partialPrefetching: true,
  rewrites() {
    return [{ source: "/api/:path*", destination: `${apiUrl}/api/:path*` }];
  },
};

export default nextConfig;
