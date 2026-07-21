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
for (const file of collectHtmlFiles(root).sort()) {
  const html = fs.readFileSync(file, 'utf8');
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
console.log(`CSP_SAFE_DIRECTIVES=${safeDirectiveBlockers === 0 ? 'PASS' : 'FAIL'}`);
console.log(`CSP_STRICT_SCRIPT_BLOCKERS=${strictScriptBlockers}`);
console.log(`CSP_STRICT_STYLE_BLOCKERS=${strictStyleBlockers}`);
process.exitCode = safeDirectiveBlockers === 0 ? 0 : 1;
