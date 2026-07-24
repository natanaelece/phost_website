(function () {
    'use strict';

    const CONSENT_COOKIE = 'phost_marketing_consent';
    const VERSION_COOKIE = 'phost_marketing_consent_version';
    const ATTRIBUTION_COOKIE = 'phost_meta_attribution';
    const FBCLID_SESSION_KEY = 'phost_meta_fbclid';
    const COOKIE_MAX_AGE = 60 * 60 * 24 * 180;
    const META_SCRIPT_URL = 'https://connect.facebook.net/en_US/fbevents.js';

    let configuration = null;
    let activationStarted = false;
    let viewContentTracked = false;
    let pixelInitialized = false;
    let modalReturnFocus = null;

    function readCookie(name) {
        const prefix = `${encodeURIComponent(name)}=`;
        const item = document.cookie.split('; ').find(value => value.startsWith(prefix));
        return item ? decodeURIComponent(item.slice(prefix.length)) : null;
    }

    function writeCookie(name, value) {
        document.cookie = `${encodeURIComponent(name)}=${encodeURIComponent(value)}; Max-Age=${COOKIE_MAX_AGE}; Path=/; Secure; SameSite=Lax`;
    }

    function deleteCookie(name) {
        document.cookie = `${encodeURIComponent(name)}=; Max-Age=0; Path=/; Secure; SameSite=Lax`;
    }

    function randomId() {
        if (typeof crypto.randomUUID === 'function') return crypto.randomUUID();
        return '10000000-1000-4000-8000-100000000000'.replace(/[018]/g, function (digit) {
            return (digit ^ crypto.getRandomValues(new Uint8Array(1))[0] & 15 >> digit / 4).toString(16);
        });
    }

    function attributionId(create) {
        let value = readCookie(ATTRIBUTION_COOKIE);
        if (!value && create) {
            value = randomId();
            writeCookie(ATTRIBUTION_COOKIE, value);
        }
        return value;
    }

    function marketingAccepted() {
        return Boolean(configuration)
            && readCookie(CONSENT_COOKIE) === 'accepted'
            && readCookie(VERSION_COOKIE) === configuration.consentVersion;
    }

    function metaCookie(name) {
        return marketingAccepted() ? readCookie(name) : null;
    }

    function capturedFbclid() {
        if (!marketingAccepted()) return null;
        const current = new URLSearchParams(location.search).get('fbclid');
        if (current) sessionStorage.setItem(FBCLID_SESSION_KEY, current.slice(0, 500));
        return current || sessionStorage.getItem(FBCLID_SESSION_KEY);
    }

    function sessionToken() {
        return localStorage.getItem('premier_token');
    }

    function attributionHeaders(headers) {
        const result = Object.assign({}, headers || {});
        const id = marketingAccepted() ? attributionId(false) : null;
        if (id) result['X-Meta-Attribution-Id'] = id;
        return result;
    }

    function captureConsent(status) {
        const id = attributionId(true);
        const accepted = status === 'accepted';
        return fetch('/api/meta/consent', {
            method: 'POST',
            keepalive: true,
            headers: attributionHeaders(Object.assign(
                { 'Content-Type': 'application/json' },
                sessionToken() ? { 'X-Session-Token': sessionToken() } : {}
            )),
            body: JSON.stringify({
                attributionId: id,
                status,
                fbp: accepted ? readCookie('_fbp') : null,
                fbc: accepted ? readCookie('_fbc') : null,
                fbclid: accepted ? capturedFbclid() : null,
                sourceUrl: accepted ? location.href : null
            })
        }).catch(function () {
            return null;
        });
    }

    function createPixelQueue() {
        if (window.fbq) return window.fbq;
        const fbq = function () {
            if (fbq.callMethod) fbq.callMethod.apply(fbq, arguments);
            else fbq.queue.push(arguments);
        };
        fbq.push = fbq;
        fbq.loaded = true;
        fbq.version = '2.0';
        fbq.queue = [];
        window.fbq = fbq;
        window._fbq = fbq;
        return fbq;
    }

    function initializePixel() {
        if (pixelInitialized || !marketingAccepted() || !configuration.enabled || !configuration.pixelId) return;
        pixelInitialized = true;
        const fbq = createPixelQueue();
        const script = document.createElement('script');
        script.async = true;
        script.src = META_SCRIPT_URL;
        document.head.appendChild(script);
        fbq('init', configuration.pixelId);
        fbq('track', 'PageView');
    }

    function sendBrowserEvent(eventName, customData, eventId, contentName) {
        if (!marketingAccepted()) return;
        const id = attributionId(true);
        fetch('/api/meta/events/browser', {
            method: 'POST',
            keepalive: true,
            headers: attributionHeaders(Object.assign(
                { 'Content-Type': 'application/json' },
                sessionToken() ? { 'X-Session-Token': sessionToken() } : {}
            )),
            body: JSON.stringify({
                attributionId: id,
                eventName,
                eventId,
                contentName: contentName || customData?.content_name || null,
                fbp: metaCookie('_fbp'),
                fbc: metaCookie('_fbc'),
                fbclid: capturedFbclid(),
                sourceUrl: location.href
            })
        }).catch(function () {
            return null;
        });
    }

    function trackBrowserEvent(eventName, customData, contentName) {
        if (!marketingAccepted()) return null;
        const eventId = randomId();
        initializePixel();
        window.fbq?.('track', eventName, customData || {}, { eventID: eventId });
        sendBrowserEvent(eventName, customData || {}, eventId, contentName);
        return eventId;
    }

    function trackServerEvent(eventName, customData, eventId) {
        if (!marketingAccepted() || !eventId) return;
        initializePixel();
        window.fbq?.('track', eventName, customData || {}, { eventID: eventId });
    }

    function trackGuideView() {
        if (viewContentTracked || !marketingAccepted()) return;
        if (location.pathname !== '/guia-wyd' && location.pathname !== '/guia-wyd.html') return;
        viewContentTracked = true;
        trackBrowserEvent('ViewContent', {
            content_name: 'Guia WYD',
            content_category: 'Conteúdo'
        });
    }

    function activateMarketing() {
        if (activationStarted || !marketingAccepted()) return;
        activationStarted = true;
        captureConsent('accepted');
        initializePixel();
        trackGuideView();
        setTimeout(function () {
            if (marketingAccepted()) captureConsent('accepted');
        }, 1000);
    }

    function setBackgroundInert(active) {
        const modal = document.getElementById('marketingConsentModal');
        Array.from(document.body.children).forEach(function (element) {
            if (element === modal || element.tagName === 'SCRIPT') return;
            if (active && !element.inert) {
                element.inert = true;
                element.dataset.cookieConsentInert = 'true';
            } else if (!active && element.dataset.cookieConsentInert === 'true') {
                element.inert = false;
                delete element.dataset.cookieConsentInert;
            }
        });
    }

    function showConsentView(showDetails) {
        const modal = document.getElementById('marketingConsentModal');
        if (!modal) return;
        modal.querySelector('[data-cookie-summary]')?.classList.toggle('hidden', showDetails);
        modal.querySelector('[data-cookie-details]')?.classList.toggle('hidden', !showDetails);
        requestAnimationFrame(function () {
            if (showDetails) modal.querySelector('[data-cookie-meta-control]')?.focus();
            else modal.querySelector('[data-cookie-accept]')?.focus();
        });
    }

    function closeConsentModal() {
        document.getElementById('marketingConsentModal')?.classList.add('hidden');
        setBackgroundInert(false);
        document.body.classList.remove('cookie-modal-open');
        if (modalReturnFocus?.isConnected) modalReturnFocus.focus();
        modalReturnFocus = null;
    }

    function openConsentModal(showDetails, synchronizeControl) {
        const modal = document.getElementById('marketingConsentModal');
        if (!modal) return;
        if (modal.classList.contains('hidden') && document.activeElement instanceof HTMLElement)
            modalReturnFocus = document.activeElement;
        if (synchronizeControl) {
            const control = modal.querySelector('[data-cookie-meta-control]');
            if (control) control.checked = marketingAccepted();
        }
        showConsentView(Boolean(showDetails));
        modal.classList.remove('hidden');
        setBackgroundInert(true);
        document.body.classList.add('cookie-modal-open');
    }

    function trapModalKeyboard(event) {
        const modal = document.getElementById('marketingConsentModal');
        if (!modal || modal.classList.contains('hidden')) return;
        if (event.key === 'Escape') {
            event.preventDefault();
            event.stopPropagation();
            return;
        }
        if (event.key !== 'Tab') return;

        const focusable = Array.from(modal.querySelectorAll(
            'button:not([disabled]), input:not([disabled]), [href], [tabindex]:not([tabindex="-1"])'
        )).filter(function (element) {
            return !element.closest('.hidden');
        });
        if (focusable.length === 0) {
            event.preventDefault();
            modal.querySelector('#cookieConsentTitle')?.focus();
            return;
        }

        const first = focusable[0];
        const last = focusable[focusable.length - 1];
        if (!focusable.includes(document.activeElement)) {
            event.preventDefault();
            (event.shiftKey ? last : first).focus();
        } else if (event.shiftKey && document.activeElement === first) {
            event.preventDefault();
            last.focus();
        } else if (!event.shiftKey && document.activeElement === last) {
            event.preventDefault();
            first.focus();
        }
    }

    function restoreModalFocusFromBackdrop(event) {
        const modal = document.getElementById('marketingConsentModal');
        if (!modal || event.target !== modal) return;

        event.preventDefault();
        const details = modal.querySelector('[data-cookie-details]');
        if (details && !details.classList.contains('hidden'))
            modal.querySelector('[data-cookie-meta-control]')?.focus();
        else
            modal.querySelector('[data-cookie-accept]')?.focus();
    }

    function refreshConsentUi() {
        const choice = readCookie(CONSENT_COOKIE);
        const validVersion = readCookie(VERSION_COOKIE) === configuration.consentVersion;
        const decided = validVersion && (choice === 'accepted' || choice === 'rejected');
        if (!decided) openConsentModal(false, false);
    }

    async function setConsent(status) {
        const hadLoadedPixel = pixelInitialized;
        writeCookie(CONSENT_COOKIE, status);
        writeCookie(VERSION_COOKIE, configuration.consentVersion);
        attributionId(true);
        closeConsentModal();

        if (status === 'accepted') {
            activationStarted = false;
            activateMarketing();
            return;
        }

        activationStarted = false;
        sessionStorage.removeItem(FBCLID_SESSION_KEY);
        deleteCookie('_fbp');
        deleteCookie('_fbc');
        await captureConsent('rejected');
        if (hadLoadedPixel) location.reload();
    }

    function bindConsentControls() {
        document.querySelectorAll('[data-cookie-accept]').forEach(function (button) {
            button.addEventListener('click', function () { setConsent('accepted'); });
        });
        document.querySelectorAll('[data-cookie-reject]').forEach(function (button) {
            button.addEventListener('click', function () { setConsent('rejected'); });
        });
        document.querySelectorAll('[data-cookie-customize]').forEach(function (button) {
            button.addEventListener('click', function () { showConsentView(true); });
        });
        document.querySelectorAll('[data-cookie-back]').forEach(function (button) {
            button.addEventListener('click', function () { showConsentView(false); });
        });
        document.querySelectorAll('[data-cookie-save]').forEach(function (button) {
            button.addEventListener('click', function () {
                const enabled = document.querySelector('[data-cookie-meta-control]')?.checked;
                setConsent(enabled ? 'accepted' : 'rejected');
            });
        });
        document.querySelectorAll('[data-manage-cookies]').forEach(function (button) {
            button.addEventListener('click', function () { openConsentModal(true, true); });
        });
        const modal = document.getElementById('marketingConsentModal');
        modal?.addEventListener('keydown', trapModalKeyboard);
        modal?.addEventListener('pointerdown', restoreModalFocusFromBackdrop);
    }

    function bindWhatsAppContacts() {
        document.querySelectorAll('a[href*="wa.me"], a[href*="whatsapp.com/channel"]').forEach(function (link) {
            link.addEventListener('click', function () {
                const isChannel = link.href.includes('whatsapp.com/channel');
                const contentName = isChannel ? 'Canal de novidades' : 'Atendimento Premier Host';
                trackBrowserEvent('Contact', { content_name: contentName }, contentName);
            });
        });
    }

    async function loadConfiguration() {
        try {
            const response = await fetch('/api/meta/config', { headers: { Accept: 'application/json' } });
            if (!response.ok) throw new Error('configuration unavailable');
            configuration = await response.json();
        } catch (_) {
            configuration = { enabled: false, pixelId: null, consentVersion: '2' };
        }
    }

    document.addEventListener('DOMContentLoaded', async function () {
        await loadConfiguration();
        bindConsentControls();
        bindWhatsAppContacts();
        refreshConsentUi();
        if (marketingAccepted()) activateMarketing();
        else if (
            readCookie(CONSENT_COOKIE) === 'rejected'
            && readCookie(VERSION_COOKIE) === configuration.consentVersion
        ) {
            captureConsent('rejected');
        }
    });

    window.premierMeta = {
        getAttributionId: function () {
            return marketingAccepted() ? attributionId(false) : null;
        },
        withAttributionHeaders: attributionHeaders,
        trackServerEvent
    };
})();
