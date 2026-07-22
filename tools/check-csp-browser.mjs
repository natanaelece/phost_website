import http from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import path from 'node:path';

const host = '127.0.0.1';
const port = Number(process.env.CSP_TEST_PORT || 8083);
const driverUrl = process.env.CHROMEDRIVER_URL || 'http://127.0.0.1:9515';
const webRoot = path.resolve('wwwroot');
const csp = [
  "default-src 'self'",
  "object-src 'none'",
  "base-uri 'none'",
  "form-action 'self'",
  "frame-ancestors 'none'",
  "script-src 'self' https://challenges.cloudflare.com",
  "script-src-attr 'none'",
  "style-src 'self'",
  "style-src-attr 'none'",
  "font-src 'self'",
  "img-src 'self' data: https://phost.pro https://www.phost.pro https://challenges.cloudflare.com",
  "media-src 'self' https://phost.pro https://www.phost.pro",
  'frame-src https://challenges.cloudflare.com https://*.cloudflare.com',
  "connect-src 'self' https://challenges.cloudflare.com https://*.cloudflare.com"
].join('; ');

const pricingFixture = {
  minComputers: 1,
  maxComputers: 20,
  minSlots: 1,
  maxSlots: 8,
  minDailyComputers: 3,
  minDailyDays: 3,
  maxDailyDays: 6,
  weeklyDays: 7,
  monthlyDays: 30,
  weeklyBasePrice: 35,
  dailyWeeklyBasePrice: 40,
  additionalSlotPrice: 10,
  additionalComputerDiscount: 5,
  monthlyWeeks: 4,
  monthlyDiscountRate: 0.25,
  referralDiscountRate: 0.05,
  commercialRoundingThreshold: 50,
  minimumPrices: { diaria: 51, semanal: 35, mensal: 105 }
};

const pages = [
  '/',
  '/confirmar',
  '/guia-wyd',
  '/painel',
  '/privacidade',
  '/recuperar-senha',
  '/admin',
  '/admin/active-directory',
  '/admin/crm',
  '/admin/dashboard',
  '/admin/financeiro',
  '/admin/logs',
  '/admin/notificacoes',
  '/admin/pedidos',
  '/admin/testes-gratis',
  '/admin/usuarios'
];

const contentTypes = new Map([
  ['.css', 'text/css; charset=utf-8'],
  ['.html', 'text/html; charset=utf-8'],
  ['.ico', 'image/x-icon'],
  ['.js', 'text/javascript; charset=utf-8'],
  ['.json', 'application/json; charset=utf-8'],
  ['.png', 'image/png'],
  ['.svg', 'image/svg+xml'],
  ['.woff2', 'font/woff2'],
  ['.webp', 'image/webp']
]);

const server = http.createServer(async (request, response) => {
  try {
    const requestPath = decodeURIComponent(new URL(request.url, `http://${host}`).pathname);
    if (requestPath === '/api/checkout/pricing-rules') {
      response.writeHead(200, {
        'Content-Type': 'application/json; charset=utf-8',
        'Content-Security-Policy': csp
      });
      response.end(JSON.stringify(pricingFixture));
      return;
    }
    if (requestPath === '/api/admin/session') {
      const authenticated = new URL(request.headers.referer || `http://${host}`).searchParams.has('authenticated-fixture');
      setTimeout(() => {
        response.writeHead(authenticated ? 200 : 401, {
          'Content-Type': 'application/json; charset=utf-8',
          'Content-Security-Policy': csp,
          'Cache-Control': 'no-store'
        });
        response.end(authenticated
          ? '{"csrfToken":"fixture","user":{"name":"Admin Fixture"}}'
          : '{"erro":"session fixture"}');
      }, 1500);
      return;
    }
    const relativePath = requestPath === '/' ? 'index.html' : requestPath.replace(/^\/+/, '');
    let filePath = path.resolve(webRoot, relativePath);
    if (filePath !== webRoot && !filePath.startsWith(`${webRoot}${path.sep}`)) {
      response.writeHead(403).end('Forbidden');
      return;
    }

    if (!path.extname(filePath)) {
      const htmlPath = `${filePath}.html`;
      if ((await stat(htmlPath).catch(() => null))?.isFile()) filePath = htmlPath;
    }
    if (!(await stat(filePath)).isFile()) throw new Error('not-found');
    const body = await readFile(filePath);
    response.writeHead(200, {
      'Content-Type': contentTypes.get(path.extname(filePath)) || 'application/octet-stream',
      'Content-Security-Policy': csp,
      'Referrer-Policy': 'strict-origin-when-cross-origin',
      'X-Content-Type-Options': 'nosniff',
      'X-Frame-Options': 'DENY'
    });
    response.end(body);
  } catch {
    response.writeHead(404, { 'Content-Type': 'application/json; charset=utf-8' });
    response.end('{"erro":"not found"}');
  }
});

const webdriver = async (endpoint, options = {}) => {
  const response = await fetch(`${driverUrl}${endpoint}`, {
    method: options.method || 'GET',
    headers: { 'Content-Type': 'application/json' },
    body: options.body === undefined ? undefined : JSON.stringify(options.body)
  });
  const payload = await response.json();
  if (!response.ok || payload.value?.error) {
    throw new Error(payload.value?.message || `WebDriver HTTP ${response.status}`);
  }
  return payload.value;
};

const sleep = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds));

let sessionId;
let cspFailuresTotal = 0;
let runtimeFailures = 0;
let interactionFailures = 0;
let adminSessionGateFailures = 0;
let adminShellFailures = 0;

try {
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(port, host, resolve);
  });

  const session = await webdriver('/session', {
    method: 'POST',
    body: {
      capabilities: {
        alwaysMatch: {
          browserName: 'chrome',
          pageLoadStrategy: 'eager',
          'goog:chromeOptions': {
            args: ['--headless=new', '--no-sandbox', '--disable-dev-shm-usage', '--window-size=1440,1200']
          },
          'goog:loggingPrefs': { browser: 'ALL' }
        }
      }
    }
  });
  sessionId = session.sessionId;

  for (const page of pages) {
    await webdriver(`/session/${sessionId}/url`, {
      method: 'POST',
      body: { url: `http://${host}:${port}${page}` }
    });
    const isAdminPage = page.startsWith('/admin/');
    let gatePassed = true;
    if (isAdminPage) {
      gatePassed = await webdriver(`/session/${sessionId}/execute/sync`, {
        method: 'POST',
        body: {
          script: `
            const loading = document.getElementById('admin-session-loading');
            const login = document.getElementById('login-screen');
            const app = document.getElementById('app');
            return document.body.classList.contains('admin-session-pending')
              && getComputedStyle(loading).display === 'flex'
              && getComputedStyle(login).display === 'none'
              && getComputedStyle(app).display === 'none';
          `,
          args: []
        }
      });
      if (!gatePassed) adminSessionGateFailures += 1;
    }
    await sleep(isAdminPage ? 1750 : 750);

    let shellPassed = true;
    if (isAdminPage) {
      shellPassed = await webdriver(`/session/${sessionId}/execute/sync`, {
        method: 'POST',
        body: {
          script: `
            return !document.body.classList.contains('admin-session-pending')
              && document.querySelectorAll('#nav-trials').length === 1
              && Boolean(document.querySelector('.slogo svg'))
              && document.querySelector('[title="Sair"]')?.classList.contains('csp-s006');
          `,
          args: []
        }
      });
      if (!shellPassed) adminShellFailures += 1;
    }

    const logs = await webdriver(`/session/${sessionId}/log`, {
      method: 'POST',
      body: { type: 'browser' }
    });
    const cspFailures = logs.filter(({ message }) =>
      /content security policy|violates the following|refused to (execute|apply|load|frame)/i.test(message)
    );
    const pageRuntimeFailures = logs.filter(({ message }) => /uncaught(?: \(in promise\))? (?:type|reference|syntax)?error/i.test(message));
    cspFailuresTotal += cspFailures.length;
    runtimeFailures += pageRuntimeFailures.length;
    const pageFailed = cspFailures.length + pageRuntimeFailures.length > 0 || !gatePassed || !shellPassed;
    console.log(`${pageFailed ? 'FAIL' : 'PASS'}\t${page}\tCSP=${cspFailures.length}\tJS=${pageRuntimeFailures.length}${isAdminPage ? `\tGATE=${gatePassed ? 0 : 1}\tSHELL=${shellPassed ? 0 : 1}` : ''}`);
    for (const failure of cspFailures) console.log(`  ${failure.message}`);
    for (const failure of pageRuntimeFailures) console.log(`  ${failure.message}`);
  }

  const interactions = [
    {
      name: 'home-auth-modal',
      page: '/',
      script: `
        document.querySelector('[data-csp-click="h069"]').click();
        const opened = !document.getElementById('authModal').classList.contains('hidden');
        document.querySelector('[data-csp-click="h077"]').click();
        const registered = !document.getElementById('registerForm').classList.contains('hidden');
        document.querySelector('[data-csp-click="h080"]').click();
        const recovered = !document.getElementById('recoverForm').classList.contains('hidden');
        document.querySelector('[data-csp-click="h075"]').click();
        const closed = document.getElementById('authModal').classList.contains('hidden');
        return opened && registered && recovered && closed;
      `
    },
    {
      name: 'panel-help-modal',
      page: '/painel',
      script: `
        document.querySelector('[data-csp-click="h092"]').click();
        const opened = !document.getElementById('ajudaModal').classList.contains('hidden');
        document.querySelector('[data-csp-click="h101"]').click();
        const closed = document.getElementById('ajudaModal').classList.contains('hidden');
        return opened && closed;
      `
    },
    {
      name: 'guide-faq-and-contract-links',
      page: '/guia-wyd',
      script: `
        const details = document.querySelector('.guide-faq details');
        details.querySelector('summary').click();
        const simulator = document.querySelector('[data-analytics-location="guide_hero"][href="/painel#simular-planos"]');
        const trial = document.querySelector('[data-analytics-location="guide_trial"]');
        return details.open
          && document.querySelectorAll('h1').length === 1
          && document.querySelector('link[rel="canonical"]')?.href === 'https://phost.pro/guia-wyd'
          && simulator?.dataset.analyticsSource === 'guia_wyd'
          && trial?.getAttribute('href') === '/?action=login&intent=free-trial'
          && trial?.dataset.analyticsEvent === 'free_trial_cta_clicked';
      `
    },
    {
      name: 'admin-authenticated-shell',
      page: '/admin/testes-gratis?authenticated-fixture=1',
      wait: 1750,
      script: `
        return getComputedStyle(document.getElementById('app')).display === 'flex'
          && getComputedStyle(document.getElementById('login-screen')).display === 'none'
          && document.getElementById('nav-trials').classList.contains('active')
          && document.getElementById('sname').textContent === 'Admin Fixture'
          && Boolean(document.querySelector('.slogo svg'))
          && document.querySelector('[title="Sair"]')?.classList.contains('csp-s006');
      `
    },
    {
      name: 'admin-local-chart-and-font',
      page: '/admin/dashboard?authenticated-fixture=1',
      wait: 1750,
      script: `
        const resources = performance.getEntriesByType('resource').map(entry => entry.name);
        return Boolean(window.Chart)
          && resources.some(url => url.includes('/admin/assets/vendor/chart.umd.min.js'))
          && resources.some(url => url.includes('/admin/assets/fonts/inter-latin-wght-normal.woff2'))
          && !resources.some(url => url.includes('cdn.jsdelivr.net') || url.includes('fonts.googleapis.com') || url.includes('fonts.gstatic.com'));
      `
    },
    {
      name: 'admin-navigation-without-shell-reload',
      page: '/admin/testes-gratis?authenticated-fixture=1',
      wait: 1750,
      asyncScript: `
        const done = arguments[arguments.length - 1];
        document.getElementById('nav-dashboard').click();
        const deadline = Date.now() + 4000;
        const check = () => {
          const resources = performance.getEntriesByType('resource').map(entry => new URL(entry.name).pathname);
          const passed = location.pathname === '/admin/dashboard'
            && document.body.dataset.view === 'dashboard'
            && Boolean(document.getElementById('view-dashboard'))
            && resources.filter(path => path.startsWith('/admin/assets/build/admin.') && path.endsWith('.min.js')).length === 1
            && resources.filter(path => path === '/api/admin/session').length === 1;
          if (passed || Date.now() >= deadline) done(passed);
          else setTimeout(check, 50);
        };
        check();
      `
    },
    {
      name: 'admin-dashboard-period-change',
      page: '/admin/dashboard?authenticated-fixture=1',
      wait: 1750,
      asyncScript: `
        const done = arguments[arguments.length - 1];
        const select = document.getElementById('dash-period');
        select.value = '7d';
        select.dispatchEvent(new Event('change', { bubbles: true }));
        document.querySelector('[data-csp-click="h015"]').click();
        const deadline = Date.now() + 3000;
        const check = () => {
          const requested = performance.getEntriesByType('resource').some(entry => {
            const url = new URL(entry.name);
            return url.pathname === '/api/admin/dashboard' && url.searchParams.get('period') === '7d';
          });
          const passed = select.value === '7d' && requested;
          if (passed || Date.now() >= deadline) done(passed);
          else setTimeout(check, 50);
        };
        check();
      `
    },
    {
      name: 'admin-typography-without-heavy-glow',
      page: '/admin/dashboard?authenticated-fixture=1',
      wait: 1750,
      script: `
        const elements = ['.slogo-text', '.hdr-title', '.card-title', '.stat-val', '.ni', '.suser-name']
          .map(selector => document.querySelector(selector));
        return elements.every(element => {
          const style = getComputedStyle(element);
          return Number(style.fontWeight) <= 500
            && style.textShadow === 'none'
            && style.filter === 'none';
        });
      `
    }
  ];

  for (const interaction of interactions) {
    await webdriver(`/session/${sessionId}/url`, {
      method: 'POST',
      body: { url: `http://${host}:${port}${interaction.page}` }
    });
    await sleep(interaction.wait || 750);
    const passed = await webdriver(`/session/${sessionId}/execute/${interaction.asyncScript ? 'async' : 'sync'}`, {
      method: 'POST',
      body: { script: interaction.asyncScript || interaction.script, args: [] }
    });
    if (!passed) interactionFailures += 1;
    console.log(`${passed ? 'PASS' : 'FAIL'}\tinteraction\t${interaction.name}`);
  }
} finally {
  if (sessionId) {
    await webdriver(`/session/${sessionId}`, { method: 'DELETE', body: {} }).catch(() => {});
  }
  await new Promise((resolve) => server.close(resolve));
}

console.log(`CSP_BROWSER_PAGES=${pages.length}`);
console.log(`CSP_BROWSER_VIOLATIONS=${cspFailuresTotal}`);
console.log(`CSP_BROWSER_RUNTIME_ERRORS=${runtimeFailures}`);
console.log(`CSP_BROWSER_INTERACTION_FAILURES=${interactionFailures}`);
console.log(`ADMIN_SESSION_GATE_FAILURES=${adminSessionGateFailures}`);
console.log(`ADMIN_SHELL_FAILURES=${adminShellFailures}`);
process.exitCode = cspFailuresTotal + runtimeFailures + interactionFailures + adminSessionGateFailures + adminShellFailures === 0 ? 0 : 1;
