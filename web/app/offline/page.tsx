export default function OfflinePage() {
  return <main style={{ minHeight: "100vh", display: "grid", placeItems: "center", background: "#101513", color: "#e9ece5", padding: 24, fontFamily: "system-ui, sans-serif" }}>
    <section style={{ maxWidth: 480 }}><p style={{ color: "#b7dca5", letterSpacing: ".1em" }}>STILLWATCH</p><h1>You’re offline.</h1><p style={{ lineHeight: 1.7, color: "#bac0b6" }}>Reconnect to see current markets, your watchlist and alert history. We don’t show cached prices as live data.</p><p style={{ lineHeight: 1.7, color: "#bac0b6" }}>Server monitoring may continue, but this device needs a connection to receive updates. Push delivery can be delayed or unavailable.</p><a href="/app/" style={{ display: "inline-block", background: "#b7dca5", color: "#101513", padding: "14px 20px", borderRadius: 8, fontWeight: 600 }}>Try reconnecting</a></section>
  </main>;
}
