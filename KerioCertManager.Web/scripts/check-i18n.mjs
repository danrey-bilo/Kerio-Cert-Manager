// Каждая строка t('…') и формы plural('…') есть в словарях en и uk, вставки {0}… — те же.
// Падает сборку: панель на английском с русской кнопкой — ошибка, а не мелочь.
import { readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const SRC = fileURLToPath(new URL('../src/', import.meta.url));
const dicts = Object.fromEntries(['en', 'uk'].map(l => [l, JSON.parse(readFileSync(join(SRC, 'locales', l + '.json'), 'utf8'))]));

const files = dir => readdirSync(dir, { withFileTypes: true }).flatMap(e =>
  e.isDirectory() ? (e.name === 'locales' ? [] : files(join(dir, e.name))) : /\.tsx?$/.test(e.name) ? [join(dir, e.name)] : []);

const unquote = s => s.replace(/\\(.)/g, (_, c) => ({ n: '\n', r: '\r', t: '\t' })[c] ?? c);
const literal = "'((?:[^'\\\\]|\\\\.)*)'";
const keys = new Set();
for (const file of files(SRC)) {
  // Строки-комментарии (примеры в документации) — не текст панели.
  const text = readFileSync(file, 'utf8').split(/\r?\n/).filter(l => !/^\s*(\/\/|\*|\/\*)/.test(l)).join('\n');
  for (const m of text.matchAll(new RegExp('\\bt\\(' + literal, 'g'))) keys.add(unquote(m[1]));
  for (const m of text.matchAll(new RegExp('\\bplural\\([^,]+,\\s*' + literal + ',\\s*' + literal + ',\\s*' + literal, 'g'))) {
    [m[1], m[2], m[3]].forEach(k => keys.add(unquote(k)));
  }
}

const slots = s => (s.match(/\{\d+\}/g) ?? []).sort().join(',');
const problems = [];
for (const key of keys) {
  for (const [lang, dict] of Object.entries(dicts)) {
    if (!dict[key]) problems.push(`${lang}: нет перевода «${key}»`);
    else if (slots(dict[key]) !== slots(key)) problems.push(`${lang}: вставки не те в «${key}» → «${dict[key]}»`);
  }
}

if (problems.length) {
  console.error(problems.join('\n'));
  console.error(`\ni18n: ${problems.length} ошибок на ${keys.size} строк`);
  process.exit(1);
}

// --prune: убрать из словарей строки, которых в коде больше нет.
if (process.argv.includes('--prune')) {
  for (const [lang, dict] of Object.entries(dicts)) {
    const kept = Object.fromEntries(Object.entries(dict).filter(([k]) => keys.has(k)));
    writeFileSync(join(SRC, 'locales', lang + '.json'), JSON.stringify(kept, null, 1) + '\n');
    console.log(`${lang}: убрано ${Object.keys(dict).length - Object.keys(kept).length}`);
  }
}
console.log(`i18n: ${keys.size} строк — en и uk на месте`);
