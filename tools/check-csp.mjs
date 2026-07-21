import fs from 'node:fs';
import path from 'node:path';

const root = path.resolve(process.argv[2] || 'wwwroot');

function collectHtmlFiles(directory) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) return collectHtmlFiles(fullPath);
    return entry.isFile() && entry.name.endsWith('.html') ? [fullPath] : [];
  });
}

function collectJavaScriptFiles(directory) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) return collectJavaScriptFiles(fullPath);
    return entry.isFile() && entry.name.endsWith('.js') ? [fullPath] : [];
  });
}

function countMatches(text, expression) {
  return [...text.matchAll(expression)].length;
}

function attributeValues(text, tagName, attributeName) {
  const tags = text.match(new RegExp(`<${tagName}\\b[^>]*>`, 'gi')) || [];
  const attribute = new RegExp(`\\s${attributeName}\\s*=\\s*["']([^"']*)["']`, 'i');
  return tags.map(tag => tag.match(attribute)?.[1]).filter(value => value !== undefined);
}

function isExternalAction(value) {
  if (!value || value.startsWith('/') || value.startsWith('#')) return false;
  try {
    return new URL(value, 'https://phost.pro').origin !== 'https://phost.pro';
  } catch {
    return true;
  }
}

const rows = [];
let safeDirectiveBlockers = 0;
const htmlSources = [];
for (const file of collectHtmlFiles(root).sort()) {
  const html = fs.readFileSync(file, 'utf8');
  htmlSources.push(html);
  const inlineScripts = countMatches(html, /<script(?![^>]*\bsrc=)[^>]*>[\s\S]*?<\/script>/gi);
  const inlineHandlers = countMatches(html, /\son[a-z]+\s*=\s*["']/gi);
  const styleElements = countMatches(html, /<style\b[^>]*>[\s\S]*?<\/style>/gi);
  const styleAttributes = countMatches(html, /\sstyle\s*=\s*["']/gi);
  const objectEmbeds = countMatches(html, /<(?:object|embed)\b/gi);
  const baseElements = countMatches(html, /<base\b/gi);
  const externalForms = attributeValues(html, 'form', 'action').filter(isExternalAction).length;
  const frames = countMatches(html, /<iframe\b/gi);
  safeDirectiveBlockers += objectEmbeds + baseElements + externalForms;
  rows.push({
    file: path.relative(process.cwd(), file),
    inlineScripts,
    inlineHandlers,
    styleElements,
    styleAttributes,
    objectEmbeds,
    baseElements,
    externalForms,
    frames
  });
}

console.log('arquivo\tscript-inline\teventos-inline\tstyle-tag\tstyle-attr\tobject/embed\tbase\tform-externo\tiframe');
for (const row of rows) console.log(Object.values(row).join('\t'));

const strictScriptBlockers = rows.reduce((total, row) => total + row.inlineScripts + row.inlineHandlers, 0);
const strictStyleBlockers = rows.reduce((total, row) => total + row.styleElements + row.styleAttributes, 0);
let dynamicScriptBlockers = 0;
let dynamicStyleBlockers = 0;
const javaScriptSources = collectJavaScriptFiles(root).map(file => fs.readFileSync(file, 'utf8'));
for (const source of javaScriptSources) {
  dynamicScriptBlockers += countMatches(source, /<[a-z][^>]*\son[a-z]+\s*=\s*["']/gi);
  dynamicStyleBlockers += countMatches(source, /<[a-z][^>]*\sstyle\s*=\s*["']/gi);
}

const allSources = [...htmlSources, ...javaScriptSources].join('\n');
const bridgeSource = fs.readFileSync(path.join(root, 'js', 'csp-handlers.js'), 'utf8');
const cspHandlerDefinitions = new Set([...bridgeSource.matchAll(/\b(h\d+):\s*function\b/g)].map(match => match[1]));
const cspHandlerReferences = new Set([
  ...[...allSources.matchAll(/data-csp-[a-z]+\s*=\s*["'](h\d+)["']/gi)].map(match => match[1]),
  ...[...allSources.matchAll(/\.dataset\.csp[A-Z][A-Za-z]*\s*=\s*["'](h\d+)["']/g)].map(match => match[1])
]);
const missingCspHandlers = [...cspHandlerReferences].filter(handler => !cspHandlerDefinitions.has(handler));

const adminSource = fs.readFileSync(path.join(root, 'admin', 'assets', 'admin.js'), 'utf8');
const adminActionBlock = adminSource.match(/const adminDeclarativeActions = Object\.freeze\(\{([\s\S]*?)\n\s*\}\);/)?.[1] || '';
const adminActionDefinitions = new Set([...adminActionBlock.matchAll(/^\s*'([^']+)':/gm)].map(match => match[1]));
const adminActionReferences = new Set([...allSources.matchAll(/data-admin-[a-z]+\s*=\s*["']([^"']+)["']/gi)].map(match => match[1]));
const missingAdminActions = [...adminActionReferences].filter(action => !adminActionDefinitions.has(action));

console.log(`CSP_SAFE_DIRECTIVES=${safeDirectiveBlockers === 0 ? 'PASS' : 'FAIL'}`);
console.log(`CSP_STRICT_SCRIPT_BLOCKERS=${strictScriptBlockers}`);
console.log(`CSP_STRICT_STYLE_BLOCKERS=${strictStyleBlockers}`);
console.log(`CSP_DYNAMIC_SCRIPT_BLOCKERS=${dynamicScriptBlockers}`);
console.log(`CSP_DYNAMIC_STYLE_BLOCKERS=${dynamicStyleBlockers}`);
console.log(`CSP_MISSING_HANDLERS=${missingCspHandlers.length}${missingCspHandlers.length ? ` (${missingCspHandlers.join(', ')})` : ''}`);
console.log(`CSP_MISSING_ADMIN_ACTIONS=${missingAdminActions.length}${missingAdminActions.length ? ` (${missingAdminActions.join(', ')})` : ''}`);
const hasBlockers = safeDirectiveBlockers + strictScriptBlockers + strictStyleBlockers + dynamicScriptBlockers + dynamicStyleBlockers + missingCspHandlers.length + missingAdminActions.length > 0;
process.exitCode = hasBlockers ? 1 : 0;
