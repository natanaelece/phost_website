(function () {
    'use strict';

    const SESSION_KEY = 'premier_analytics_session';
    const allowedProperties = new Set([
        'period', 'computers', 'instances', 'days', 'source', 'logged_in',
        'result', 'error_code', 'cta', 'location', 'renewal'
    ]);

    function sessionId() {
        let id = sessionStorage.getItem(SESSION_KEY);
        if (!id) {
            id = typeof crypto.randomUUID === 'function'
                ? crypto.randomUUID()
                : '10000000-1000-4000-8000-100000000000'.replace(/[018]/g, function (digit) {
                    return (digit ^ crypto.getRandomValues(new Uint8Array(1))[0] & 15 >> digit / 4).toString(16);
                });
            sessionStorage.setItem(SESSION_KEY, id);
        }
        return id;
    }

    function currentUserId() {
        try {
            return JSON.parse(localStorage.getItem('premier_user') || 'null')?.id || null;
        } catch (_) {
            return null;
        }
    }

    function cleanProperties(properties) {
        const clean = {};
        Object.entries(properties || {}).forEach(([key, value]) => {
            if (!allowedProperties.has(key)) return;
            if (['string', 'number', 'boolean'].includes(typeof value)) clean[key] = value;
        });
        return clean;
    }

    function track(eventName, properties) {
        const payload = JSON.stringify({
            eventName,
            sessionId: sessionId(),
            userId: currentUserId(),
            pagePath: location.pathname,
            referrer: document.referrer || null,
            properties: cleanProperties(properties)
        });
        const token = localStorage.getItem('premier_token');

        fetch('/api/analytics/events', {
            method: 'POST',
            keepalive: true,
            headers: Object.assign(
                { 'Content-Type': 'application/json' },
                token ? { 'X-Session-Token': token } : {}
            ),
            body: payload
        }).catch(function () { /* Analytics nunca deve interromper a experiência. */ });
    }

    window.premierAnalytics = { track };

    document.addEventListener('DOMContentLoaded', function () {
        const eventName = location.pathname === '/' ? 'landing_viewed'
            : location.pathname.startsWith('/painel') ? 'simulator_viewed'
            : null;
        if (eventName) track(eventName, { logged_in: Boolean(currentUserId()) });

        document.querySelectorAll('[data-analytics-event]').forEach(function (element) {
            element.addEventListener('click', function () {
                track(element.dataset.analyticsEvent, {
                    cta: element.dataset.analyticsCta || element.textContent.trim().slice(0, 60),
                    location: element.dataset.analyticsLocation || 'page'
                });
            });
        });
    });
})();
