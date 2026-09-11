(function () {
    'use strict';
    if (!('serviceWorker' in navigator)) return;
    var installButton = document.querySelector('[data-pwa-install]');
    var installHint = document.querySelector('[data-pwa-install-hint]');
    var updateButton = document.querySelector('[data-pwa-update]');
    var updateHint = document.querySelector('[data-pwa-update-hint]');
    var deferredPrompt;
    var refreshing = false;
    function showUpdate(registration) {
        if (!registration || !registration.waiting || !navigator.serviceWorker.controller) return;
        if (updateButton) updateButton.hidden = false;
        if (updateHint) updateHint.hidden = false;
    }
    function registerWorker() {
        return navigator.serviceWorker.register('/push-service-worker.js', { scope: '/' }).then(function (registration) {
            showUpdate(registration);
            registration.addEventListener('updatefound', function () {
                var worker = registration.installing;
                if (!worker) return;
                worker.addEventListener('statechange', function () { if (worker.state === 'installed') showUpdate(registration); });
            });
            return registration;
        });
    }
    navigator.serviceWorker.addEventListener('controllerchange', function () { if (!refreshing) { refreshing = true; window.location.reload(); } });
    if (updateButton) updateButton.addEventListener('click', function () {
        navigator.serviceWorker.getRegistration('/').then(function (registration) {
            if (registration && registration.waiting) registration.waiting.postMessage({ type: 'SPLITBILL_SKIP_WAITING' });
        });
    });
    registerWorker().catch(function () { /* PWA remains optional. */ });
    window.addEventListener('beforeinstallprompt', function (event) {
        event.preventDefault(); deferredPrompt = event;
        if (installButton) installButton.hidden = false;
        if (installHint) installHint.hidden = false;
    });
    if (installButton) installButton.addEventListener('click', function () {
        if (!deferredPrompt) return;
        deferredPrompt.prompt();
        deferredPrompt.userChoice.finally(function () { deferredPrompt = null; installButton.hidden = true; if (installHint) installHint.hidden = true; });
    });
    window.addEventListener('appinstalled', function () { deferredPrompt = null; if (installButton) installButton.hidden = true; if (installHint) installHint.hidden = true; });
}());
