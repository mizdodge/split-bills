(function () {
    'use strict';

    var config = window.splitBillPush;
    if (!config) return;

    var status = document.querySelector('[data-push-status]');
    var detail = document.querySelector('[data-push-detail]');
    var enableButton = document.querySelector('[data-push-enable]');
    var disableButton = document.querySelector('[data-push-disable]');
    var testButton = document.querySelector('[data-push-test]');
    var installationKey = 'splitbill.push.installation-id';

    function text(key, fallback) { return config.strings && config.strings[key] ? config.strings[key] : fallback; }
    function setState(state, message) {
        if (status) { status.dataset.state = state; status.textContent = message; }
        if (detail && message) detail.textContent = message;
        if (enableButton) enableButton.hidden = state === 'granted' || state === 'unsupported' || state === 'insecure' || state === 'denied';
        if (disableButton) disableButton.hidden = state !== 'granted';
        if (testButton) testButton.hidden = state !== 'granted';
    }
    function installationId() {
        try {
            var value = window.localStorage.getItem(installationKey);
            if (!value) { value = crypto.randomUUID(); window.localStorage.setItem(installationKey, value); }
            return value;
        } catch (_) { return crypto.randomUUID(); }
    }
    function decodeBase64Url(value) {
        var base64 = value.replace(/-/g, '+').replace(/_/g, '/');
        while (base64.length % 4) base64 += '=';
        var raw = window.atob(base64), bytes = new Uint8Array(raw.length);
        for (var i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
        return bytes;
    }
    function antiForgeryToken() {
        var input = document.querySelector('input[name="__RequestVerificationToken"]');
        return input ? input.value : '';
    }
    function post(path, body) {
        return fetch(path, {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': antiForgeryToken() },
            body: JSON.stringify(body || {})
        }).then(function (response) {
            return response.json().catch(function () { return {}; }).then(function (payload) {
                if (!response.ok) throw new Error(payload.error || text('error', 'Push notification gagal.'));
                return payload;
            });
        });
    }
    function browserLabel() {
        try { return navigator.userAgentData && navigator.userAgentData.brands ? navigator.userAgentData.brands.map(function (x) { return x.brand; }).join(', ') : navigator.userAgent; }
        catch (_) { return ''; }
    }
    function sendSubscription(subscription) {
        var json = subscription.toJSON();
        return post('/Push/Subscribe', {
            endpoint: json.endpoint,
            p256dh: json.keys && json.keys.p256dh,
            auth: json.keys && json.keys.auth,
            installationId: installationId(),
            culture: document.documentElement.lang === 'en' ? 'en-US' : 'id-ID',
            browserLabel: browserLabel(),
            expiresAt: subscription.expirationTime ? new Date(subscription.expirationTime).toISOString() : null
        });
    }
    function loadExisting(registration) {
        return registration.pushManager.getSubscription().then(function (subscription) {
            if (!subscription) { setState('default', text('enablePrompt', 'Aktifkan notifikasi browser.')); return null; }
            return sendSubscription(subscription).then(function () {
                setState('granted', text('enabled', 'Notifikasi browser aktif.')); return subscription;
            });
        });
    }
    function initialize() {
        if (!window.isSecureContext || !('serviceWorker' in navigator) || !('PushManager' in window) || !('Notification' in window)) {
            setState(window.isSecureContext ? 'unsupported' : 'insecure', window.isSecureContext ? text('unsupported', 'Browser ini belum mendukung push notification.') : text('insecure', 'Push notification membutuhkan HTTPS atau localhost.'));
            return Promise.resolve(null);
        }
        return fetch('/Push/PublicKey', { credentials: 'same-origin' }).then(function (response) {
            if (!response.ok) throw new Error(text('error', 'Push notification tidak tersedia.'));
            return response.json();
        }).then(function (publicConfig) {
            if (!publicConfig.enabled || !publicConfig.publicKey) throw new Error(text('error', 'Push notification belum diaktifkan admin.'));
            return navigator.serviceWorker.register('/push-service-worker.js', { scope: '/' });
        }).then(loadExisting).catch(function (error) { setState('error', error.message || text('error', 'Push notification gagal.')); return null; });
    }
    function enable() {
        if (!enableButton) return;
        enableButton.disabled = true;
        setState('loading', text('requesting', 'Meminta izin browser...'));
        fetch('/Push/PublicKey', { credentials: 'same-origin' }).then(function (response) { return response.json(); }).then(function (publicConfig) {
            if (!publicConfig.publicKey) throw new Error(text('error', 'Kunci push belum tersedia.'));
            return Notification.requestPermission().then(function (permission) {
                if (permission !== 'granted') { setState('denied', text('denied', 'Izin notifikasi ditolak di browser.')); throw new Error('permission-denied'); }
                return navigator.serviceWorker.register('/push-service-worker.js', { scope: '/' }).then(function (registration) {
                    return registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: decodeBase64Url(publicConfig.publicKey) });
                });
            });
        }).then(sendSubscription).then(function () { setState('granted', text('enabled', 'Notifikasi browser aktif.')); })
            .catch(function (error) { if (error.message !== 'permission-denied') setState('error', error.message || text('error', 'Push notification gagal.')); })
            .finally(function () { enableButton.disabled = false; });
    }
    function disable() {
        if (!disableButton) return;
        disableButton.disabled = true;
        navigator.serviceWorker.getRegistration('/').then(function (registration) { return registration && registration.pushManager.getSubscription(); }).then(function (subscription) {
            var endpoint = subscription && subscription.endpoint;
            return post('/Push/Unsubscribe', { endpoint: endpoint, installationId: installationId() }).then(function () { return subscription && subscription.unsubscribe(); });
        }).then(function () { setState('default', text('disabled', 'Notifikasi browser dimatikan.')); }).catch(function (error) { setState('error', error.message || text('error', 'Push notification gagal.')); })
            .finally(function () { disableButton.disabled = false; });
    }
    function testPush() {
        if (!testButton) return;
        testButton.disabled = true;
        post('/Push/Test', { installationId: installationId() })
            .then(function () { setState('granted', text('testSent', 'Test notification dikirim.')); })
            .catch(function (error) { setState('error', error.message || text('error', 'Push notification gagal.')); })
            .finally(function () { testButton.disabled = false; });
    }
    if (enableButton) enableButton.addEventListener('click', enable);
    if (disableButton) disableButton.addEventListener('click', disable);
    if (testButton) testButton.addEventListener('click', testPush);
    if ('serviceWorker' in navigator) navigator.serviceWorker.addEventListener('message', function (event) {
        if (event.data && event.data.type === 'splitbill-notification' && window.location.pathname.toLowerCase() === '/notifications') window.location.reload();
    });
    initialize();
}());
