import type { Metadata, Viewport } from "next";
import "./globals.css";
import "./product.css";
export const metadata: Metadata = {
  title: { default: "Stillwatch — Keep perspective. Stay informed.", template: "%s · Stillwatch" },
  description: "Monitor crypto markets without staring at charts all day. Understand market conditions, build your watchlist, and receive considered alerts.",
  manifest: "/manifest.webmanifest",
  appleWebApp: { capable: true, statusBarStyle: "black-translucent", title: "Stillwatch" },
  icons: { icon: "/icon.svg", apple: "/icon-192.png" },
};
export const viewport: Viewport = { width: "device-width", initialScale: 1, viewportFit: "cover", themeColor: "#101513" };
export default function RootLayout({ children }: { children: React.ReactNode }) {
  return <html lang="en"><body>{children}</body></html>;
}
