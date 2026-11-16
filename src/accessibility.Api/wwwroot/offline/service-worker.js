const SHELL_CACHE = "accesslearn-offline-shell-v1";
const SHELL = [
  "/offline/",
  "/offline/index.html",
  "/offline/app.js",
  "/offline/manifest.webmanifest",
  "/offline/icon.svg"
];

self.addEventListener("install", (event) => {
  event.waitUntil(caches.open(SHELL_CACHE).then((cache) => cache.addAll(SHELL)));
  self.skipWaiting();
});

self.addEventListener("activate", (event) => {
  event.waitUntil(caches.keys().then((keys) => Promise.all(
    keys.filter((key) => key.startsWith("accesslearn-offline-shell-") && key !== SHELL_CACHE)
      .map((key) => caches.delete(key))
  )));
  self.clients.claim();
});

self.addEventListener("fetch", (event) => {
  const url = new URL(event.request.url);
  if (url.origin !== self.location.origin) return;
  if (url.pathname.startsWith("/offline/content/")) {
    event.respondWith((async () => {
      const keys = await caches.keys();
      for (const key of keys.filter((value) =>
        value.startsWith("accesslearn-package-") && !value.startsWith("accesslearn-package-temp-"))) {
        const response = await (await caches.open(key)).match(event.request);
        if (response) return response;
      }
      return new Response("Offline asset unavailable", { status: 404 });
    })());
    return;
  }
  if (event.request.mode === "navigate" && url.pathname.startsWith("/offline")) {
    event.respondWith(fetch(event.request).catch(() => caches.match("/offline/index.html")));
    return;
  }
  if (url.pathname.startsWith("/offline/")) {
    event.respondWith(caches.match(event.request).then((cached) => cached || fetch(event.request)));
  }
});
