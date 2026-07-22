import { createHash } from 'node:crypto';
import { mkdir, readFile, readdir, unlink, writeFile } from 'node:fs/promises';
import path from 'node:path';

const root = process.cwd();
const webRoot = path.join(root, 'wwwroot');
const outputRoot = path.join(webRoot, 'assets', 'build');

const assets = [
  { key: 'tailwind', source: 'css/tailwind.css' },
  { key: 'index-style', source: 'css/index.css' },
  { key: 'panel-style', source: 'css/painel.css' },
  { key: 'privacy-style', source: 'css/privacidade.css' },
  { key: 'guide-style', source: 'css/guia-wyd.css' },
  { key: 'confirm-style', source: 'css/confirmar.css' },
  { key: 'recovery-style', source: 'css/recuperar-senha.css' },
  { key: 'shared-style', source: 'css/csp-static.css' },
  { key: 'analytics', source: 'analytics.js' },
  { key: 'index-script', source: 'js/index.js' },
  { key: 'panel-script', source: 'js/painel.js' },
  { key: 'confirm-script', source: 'js/confirmar.js' },
  { key: 'recovery-script', source: 'js/recuperar-senha.js' },
  { key: 'shared-script', source: 'js/csp-handlers.js' }
];

const publicPages = [
  'index.html',
  'painel.html',
  'privacidade.html',
  'guia-wyd.html',
  'confirmar.html',
  'recuperar-senha.html'
];

const escapeRegex = value => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

await mkdir(outputRoot, { recursive: true });

const generatedAssets = [];
for (const asset of assets) {
  const contents = await readFile(path.join(webRoot, asset.source));
  const extension = path.extname(asset.source).slice(1);
  const hash = createHash('sha256').update(contents).digest('hex').slice(0, 12);
  const fileName = `public.${asset.key}.${hash}.${extension}`;
  const filePath = path.join(outputRoot, fileName);
  const existing = await readFile(filePath).catch(() => null);
  if (!existing || !existing.equals(contents)) await writeFile(filePath, contents);
  generatedAssets.push({ ...asset, extension, fileName, url: `/assets/build/${fileName}` });
}

const currentFiles = new Set(generatedAssets.map(asset => asset.fileName));
for (const pageName of publicPages) {
  const filePath = path.join(webRoot, pageName);
  const original = await readFile(filePath, 'utf8');
  let updated = original;

  for (const asset of generatedAssets) {
    const sourceUrl = `/${asset.source}`;
    const referencePattern = new RegExp(
      `${escapeRegex(sourceUrl)}|/assets/build/public\\.${escapeRegex(asset.key)}\\.[a-f0-9]{12}\\.${asset.extension}`,
      'g'
    );
    updated = updated.replace(referencePattern, asset.url);
  }

  const mutableReference = updated.match(/(?:src|href)=["']\/(?!assets\/build\/)[^"']+\.(?:css|js)(?:\?[^"']*)?["']/i);
  if (mutableReference) {
    throw new Error(`Referencia publica sem hash em ${pageName}: ${mutableReference[0]}`);
  }

  for (const match of updated.matchAll(/\/assets\/build\/(public\.[a-z0-9-]+\.[a-f0-9]{12}\.(?:css|js))/g)) {
    if (!currentFiles.has(match[1])) throw new Error(`Asset publico inexistente em ${pageName}: ${match[1]}`);
  }

  if (updated !== original) await writeFile(filePath, updated);
}

for (const fileName of await readdir(outputRoot)) {
  if (/^public\.[a-z0-9-]+\.[a-f0-9]{12}\.(?:css|js)$/.test(fileName) && !currentFiles.has(fileName)) {
    await unlink(path.join(outputRoot, fileName));
  }
}

for (const asset of generatedAssets) {
  console.log(`${asset.fileName}\t${(await readFile(path.join(outputRoot, asset.fileName))).byteLength} bytes`);
}
