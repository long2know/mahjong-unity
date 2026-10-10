#!/usr/bin/env node
/*
 * Phase K Wave 8 — Inverse of `three-deep-imports.js`: collapses any
 * per-class deep three.js imports back into a single `from 'three'`
 * import per source file.  Used when the deep-import experiment must
 * be rolled back (e.g. when the bundled `build/three.module.js` ends
 * up being more tree-shake-friendly than the per-file source tree).
 *
 * Walks `src/**.ts`, finds consecutive `import ... from 'three/src/...'`
 * lines, and replaces them with one `import { Foo, Bar, Baz } from
 * 'three';` line.  Quote style is preserved.
 */
import { readFileSync, writeFileSync, readdirSync, statSync } from 'node:fs';
import { resolve, join } from 'node:path';

const ROOT = resolve(new URL('..', import.meta.url).pathname, 'src');
const DRY = process.argv.includes('--dry-run');

const DEEP_RE = /^(import(?:\s+type)?\s+\{\s*[^}]+?\s*\}\s+from\s+['"]three\/src\/[^'"]+?['"];?\s*\n)+/gm;

function rewriteFileContent(text) {
  let changed = false;
  const newText = text.replace(DEEP_RE, block => {
    const lines = block.trim().split('\n');
    const valueSymbols = new Set();
    const typeSymbols = new Set();
    let quote = "'";
    for (const ln of lines) {
      const m = /^import(\s+type)?\s+\{\s*([^}]+?)\s*\}\s+from\s+(['"])three\/src\/[^'"]+\3;?\s*$/.exec(ln);
      if (m === null) {
        return block;
      }
      const isType = m[1] !== undefined;
      const body = m[2];
      quote = m[3];
      for (const raw of body.split(',').map(s => s.trim()).filter(Boolean)) {
        if (isType) typeSymbols.add(raw);
        else valueSymbols.add(raw);
      }
    }
    changed = true;
    const out = [];
    if (valueSymbols.size > 0) {
      const sorted = Array.from(valueSymbols).sort();
      out.push(`import { ${sorted.join(', ')} } from ${quote}three${quote};`);
    }
    if (typeSymbols.size > 0) {
      const sorted = Array.from(typeSymbols).sort();
      out.push(`import type { ${sorted.join(', ')} } from ${quote}three${quote};`);
    }
    return out.join('\n') + '\n';
  });
  return { text: newText, changed };
}

function walk(dir) {
  const out = [];
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    const st = statSync(full);
    if (st.isDirectory()) {
      out.push(...walk(full));
    } else if (entry.endsWith('.ts')) {
      out.push(full);
    }
  }
  return out;
}

const files = walk(ROOT);
let totalChanged = 0;
for (const f of files) {
  const text = readFileSync(f, 'utf8');
  if (!text.includes("from 'three/src/") && !text.includes('from "three/src/')) continue;
  const { text: newText, changed } = rewriteFileContent(text);
  if (changed && newText !== text) {
    if (DRY) {
      console.log(`would rewrite: ${f}`);
    } else {
      writeFileSync(f, newText);
      console.log(`rewrote: ${f}`);
    }
    totalChanged += 1;
  }
}
console.log(`${DRY ? 'would change' : 'changed'}: ${totalChanged} file(s)`);
