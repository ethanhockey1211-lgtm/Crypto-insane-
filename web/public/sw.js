/* Cache the offline explanation only. Never store customer requests, responses or live analyses. */
const CACHE = "stillwatch-static-v1";
self.addEventListener("install", event => {
  event.waitUntil(caches.open(CACHE).then(cache => cache.addAll(["/offline/", "/icon-192.png"])).then(() => self.skipWaiting()));
});
self.addEventListener("activate", event => {
  event.waitUntil(caches.keys().then(keys => Promise.all(keys.filter(key => key.startsWith("stillwatch-static-") && key !== CACHE).map(key => caches.delete(key)))).then(() => self.clients.claim()));
});
self.addEventListener("fetch", event => {
  const request = event.request, url = new URL(request.url);
  if (url.origin !== self.location.origin || request.method !== "GET" || url.pathname.startsWith("/api/") || url.pathname.startsWith("/hubs/")) return;
  if (request.mode === "navigate") {
    const offline = () => caches.match("/offline/").then(response => response || new Response("You are offline. Reconnect to check current market data.", { headers: { "Content-Type": "text/plain" } }));
    // An explicit offline state should not return an HTTP-cached app shell without its scripts.
    // A reported online state is only a hint, so network failures still use the same fallback.
    event.respondWith(self.navigator?.onLine === false ? offline() : fetch(request, { cache: "no-store" }).catch(offline));
  }
});
self.addEventListener("push", event => {
  event.waitUntil((async () => {
    let payload;
    try { payload = event.data?.json(); } catch { return; }
    if (!payload || typeof payload.title !== "string" || typeof payload.body !== "string") return;
    const expiry = Date.parse(payload.expiresAt);
    if (!Number.isFinite(expiry)) return;
    const expired = expiry <= Date.now();
    let url;
    try { url = new URL(payload.url, self.location.origin); } catch { return; }
    if (url.origin !== self.location.origin || !["/app", "/app/"].includes(url.pathname)) return;
    // User-visible push is required on supported browsers. A delayed message displays no stale
    // setup/price/action: just a neutral expiry notice linked to immutable history.
    if (expired) url = new URL("/app/?tab=alerts", self.location.origin);
    await self.registration.showNotification(expired ? "Stillwatch · update expired" : payload.title.slice(0, 120), {
      body: expired ? "A monitoring update expired before delivery. Open history to review its timestamp." : payload.body.slice(0, 500), icon: "/icon-192.png", badge: "/icon-192.png",
      tag: typeof payload.tag === "string" ? payload.tag.slice(0, 64) : undefined,
      renotify: false, data: { url: url.href, expiresAt: payload.expiresAt },
    });
  })());
});
self.addEventListener("notificationclick", event => {
  event.notification.close();
  event.waitUntil((async () => {
    let target;
    try { target = new URL(event.notification.data?.url || "/app/?tab=alerts", self.location.origin); } catch { return; }
    if (target.origin !== self.location.origin || !["/app", "/app/"].includes(target.pathname)) return;
    // Opens the immutable issuance record; the app separately labels current/expired data.
    const windows = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
    for (const client of windows) {
      if (new URL(client.url).origin === target.origin) {
        await client.navigate(target.href); await client.focus(); return;
      }
    }
    await self.clients.openWindow(target.href);
  })());
});
