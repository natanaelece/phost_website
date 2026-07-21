import { createHash } from 'node:crypto';
import { mkdir, readFile, readdir, unlink, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { transform } from 'esbuild';

const root = process.cwd();
const adminRoot = path.join(root, 'wwwroot', 'admin');
const sourceRoot = path.join(adminRoot, 'assets');
const outputRoot = path.join(sourceRoot, 'build');

const buildAsset = async (name, loader) => {
  const source = await readFile(path.join(sourceRoot, `admin.${name}`), 'utf8');
  const result = await transform(source, {
    loader,
    minifyWhitespace: true,
    minifySyntax: true,
    minifyIdentifiers: true,
    target: loader === 'js' ? 'es2020' : undefined,
    legalComments: 'none'
  });
  const contents = result.code.endsWith('\n') ? result.code : `${result.code}\n`;
  const hash = createHash('sha256').update(contents).digest('hex').slice(0, 12);
  const fileName = `admin.${hash}.min.${name}`;
  await writeFile(path.join(outputRoot, fileName), contents);
  return { name, fileName, contents };
};

await mkdir(outputRoot, { recursive: true });
const assets = await Promise.all([buildAsset('css', 'css'), buildAsset('js', 'js')]);
const [cssAsset, jsAsset] = assets;

for (const entry of await readdir(adminRoot, { withFileTypes: true })) {
  if (!entry.isFile() || !entry.name.endsWith('.html')) continue;
  const filePath = path.join(adminRoot, entry.name);
  const original = await readFile(filePath, 'utf8');
  const hasCssReference = /\/admin\/assets\/(?:admin\.css(?:\?[^"']*)?|build\/admin\.[a-f0-9]{12}\.min\.css)/.test(original);
  const hasJsReference = /\/admin\/assets\/(?:admin\.js(?:\?[^"']*)?|build\/admin\.[a-f0-9]{12}\.min\.js)/.test(original);
  if (!hasCssReference || !hasJsReference) throw new Error(`Referencias de assets nao encontradas em ${entry.name}`);
  const updated = original
    .replace(/\/admin\/assets\/(?:admin\.css(?:\?[^"']*)?|build\/admin\.[a-f0-9]{12}\.min\.css)/g, `/admin/assets/build/${cssAsset.fileName}`)
    .replace(/\/admin\/assets\/(?:admin\.js(?:\?[^"']*)?|build\/admin\.[a-f0-9]{12}\.min\.js)/g, `/admin/assets/build/${jsAsset.fileName}`);
  if (updated !== original) await writeFile(filePath, updated);
}

const currentFiles = new Set(assets.map(asset => asset.fileName));
for (const fileName of await readdir(outputRoot)) {
  if (/^admin\.[a-f0-9]{12}\.min\.(?:css|js)$/.test(fileName) && !currentFiles.has(fileName)) {
    await unlink(path.join(outputRoot, fileName));
  }
}

for (const asset of assets) {
  console.log(`${asset.fileName}\t${Buffer.byteLength(asset.contents)} bytes`);
}
