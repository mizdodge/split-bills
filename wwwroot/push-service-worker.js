/* SplitBill's single root worker owns both Web Push and the safe offline shell.
   Authenticated HTML, API responses, receipts and payment proofs never enter
   the cache. */
var SPLITBILL_STATIC_CACHE = 'splitbill-static-v3';
var SPLITBILL_STATIC_ASSETS = ['/offline.html', '/favicon.ico', '/manifest.webmanifest', '/css/site.css', '/js/site.js', '/js/pwa.js'];

self.addEventListener('install', function (event) {
    event.waitUntil(caches.open(SPLITBILL_STATIC_CACHE).then(function (cache) {
        return cache.addAll(SPLITBILL_STATIC_ASSETS);
    }).then(function () {
        return self.skipWaiting();
    }));
});

self.addEventListener('message', function (event) {
    if (event.data && event.data.type === 'SPLITBILL_SKIP_WAITING') self.skipWaiting();
});

self.addEventListener('activate', function (event) {
    event.waitUntil(caches.keys().then(function (keys) {
        return Promise.all(keys.filter(function (key) { return key !== SPLITBILL_STATIC_CACHE; }).map(function (key) { return caches.delete(key); }));
    }).then(function () { return self.clients.claim(); }));
});

self.addEventListener('fetch', function (event) {
    var request = event.request;
    if (request.method !== 'GET' || new URL(request.url).origin !== self.location.origin) return;
    var url = new URL(request.url);
    var isStatic = url.pathname === '/favicon.ico' || url.pathname === '/manifest.webmanifest' ||
        url.pathname === '/offline.html' || url.pathname === '/css/site.css' ||
        url.pathname === '/js/site.js' || url.pathname === '/js/pwa.js';
    if (isStatic) {
        event.respondWith(
            fetch(request).then(function (networkResponse) {
                if (networkResponse && networkResponse.ok) {
                    var responseClone = networkResponse.clone();
                    caches.open(SPLITBILL_STATIC_CACHE).then(function (cache) {
                        cache.put(url.pathname, responseClone);
                    });
                }
                return networkResponse;
            }).catch(function () {
                return caches.match(url.pathname);
            })
        );
        return;
    }
    if (request.mode === 'navigate') {
        event.respondWith(fetch(request).catch(function () { return caches.match('/offline.html'); }));
    }
});

/* Web Push: display only server-provided notification data and open a
   same-origin relative link when the user taps it. */
self.addEventListener('push', function (event) {
    var fallback = { title: 'SplitBill', body: 'Ada pembaruan baru.', url: '/Notifications', tag: 'splitbill-notification' };
    var data = fallback;
    try {
        if (event.data) data = Object.assign(fallback, event.data.json());
    } catch (_) { /* Keep the safe fallback for malformed payloads. */ }

    var title = typeof data.title === 'string' && data.title.trim() ? data.title : fallback.title;
    var body = typeof data.body === 'string' ? data.body : fallback.body;
    var tag = typeof data.tag === 'string' && data.tag.trim() ? data.tag : fallback.tag;
    var url = typeof data.url === 'string' && data.url.startsWith('/') && !data.url.startsWith('//')
        ? data.url : fallback.url;

    event.waitUntil(self.registration.showNotification(title, {
        body: body,
        tag: tag,
        renotify: true,
        icon: '/favicon.ico',
        badge: '/favicon.ico',
        data: { url: url }
    }).then(function () {
        return self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(function (clients) {
            clients.forEach(function (client) { client.postMessage({ type: 'splitbill-notification' }); });
        });
    }));
});

self.addEventListener('notificationclick', function (event) {
    event.notification.close();
    var target = event.notification.data && event.notification.data.url;
    if (typeof target !== 'string' || !target.startsWith('/') || target.startsWith('//')) target = '/Notifications';
    var absoluteUrl = new URL(target, self.location.origin).href;

    event.waitUntil(self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(function (clients) {
        for (var i = 0; i < clients.length; i++) {
            if ('focus' in clients[i]) {
                clients[i].navigate(absoluteUrl);
                return clients[i].focus();
            }
        }
        return self.clients.openWindow(absoluteUrl);
    }));
});
