import type { NextConfig } from "next";

// NEXT_OUTPUT=export produces a static site (web/out) that the API serves from its own origin: one deployable.
// The default standalone build is for running the dashboard as its own Node service.
const nextConfig: NextConfig = {
  reactStrictMode: true,
  poweredByHeader: false,
  output: process.env.NEXT_OUTPUT === "export" ? "export" : "standalone",
  images: { unoptimized: true },
};

export default nextConfig;
