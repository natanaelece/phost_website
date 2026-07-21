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
  "script-src 'self' https://challenges.cloudflare.com https://cdn.jsdelivr.net",
  "script-src-attr 'none'",
  "style-src 'self' https://fonts.googleapis.com",
  "style-src-attr 'none'",
  "font-src 'self' https://fonts.gstatic.com",
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
  '/confirmar.html',
  '/painel.html',
  '/privacidade.html',
  '/recuperar-senha.html',
  '/admin.html',
  '/admin/active-directory.html',
  '/admin/crm.html',
  '/admin/dashboard.html',
  '/admin/financeiro.html',
  '/admin/logs.html',
  '/admin/notificacoes.html',
  '/admin/pedidos.html',
  '/admin/testes-gratis.html',
  '/admin/usuarios.html'
];

const contentTypes = new Map([
  ['.css', 'text/css; charset=utf-8'],
  ['.html', 'text/html; charset=utf-8'],
  ['.ico', 'image/x-icon'],
  ['.js', 'text/javascript; charset=utf-8'],
  ['.json', 'application/json; charset=utf-8'],
  ['.png', 'image/png'],
  ['.svg', 'image/svg+xml'],
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
    const relativePath = requestPath === '/' ? 'index.html' : requestPath.replace(/^\/+/, '');
    const filePath = path.resolve(webRoot, relativePath);
    if (filePath !== webRoot && !filePath.startsWith(`${webRoot}${path.sep}`)) {
      response.writeHead(403).end('Forbidden');
      return;
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
    await sleep(750);

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
    console.log(`${cspFailures.length + pageRuntimeFailures.length === 0 ? 'PASS' : 'FAIL'}\t${page}\tCSP=${cspFailures.length}\tJS=${pageRuntimeFailures.length}`);
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
      page: '/painel.html',
      script: `
        document.querySelector('[data-csp-click="h092"]').click();
        const opened = !document.getElementById('ajudaModal').classList.contains('hidden');
        document.querySelector('[data-csp-click="h101"]').click();
        const closed = document.getElementById('ajudaModal').classList.contains('hidden');
        return opened && closed;
      `
    }
  ];

  for (const interaction of interactions) {
    await webdriver(`/session/${sessionId}/url`, {
      method: 'POST',
      body: { url: `http://${host}:${port}${interaction.page}` }
    });
    await sleep(750);
    const passed = await webdriver(`/session/${sessionId}/execute/sync`, {
      method: 'POST',
      body: { script: interaction.script, args: [] }
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
process.exitCode = cspFailuresTotal + runtimeFailures + interactionFailures === 0 ? 0 : 1;
