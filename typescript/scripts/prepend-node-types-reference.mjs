#!/usr/bin/env node
// Ensures shipped .d.ts/.d.cts files carry `/// <reference types="node" />` at the top.
//
// Why this exists: tsup's dts bundler (rollup-plugin-dts) strips triple-slash reference
// directives from the source entry file during bundling -- they do not survive into the emitted
// dist/index.d.ts / dist/index.d.cts. Packages whose public API surface touches Node ambient
// types (Buffer, NodeJS.*, etc.) need that directive to survive in the SHIPPED declaration file,
// because TypeScript 7 no longer auto-scans node_modules/@types by default: a consuming project
// with no explicit `"types"` array in its own tsconfig would otherwise hit
// `Cannot find name 'Buffer'` (TS2591) even though @types/node is a real dependency of the
// package it imported.
//
// Usage: node scripts/prepend-node-types-reference.mjs [file ...]
// With no args, defaults to dist/index.d.ts and dist/index.d.cts relative to CWD -- i.e. run as
// each package's own "postbuild" step, invoked from that package's directory by npm.

import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';

const REFERENCE = '/// <reference types="node" />';

const explicitTargets = process.argv.slice(2);
const files = explicitTargets.length > 0 ? explicitTargets : ['dist/index.d.ts', 'dist/index.d.cts'];

for (const file of files) {
  const path = resolve(process.cwd(), file);

  if (!existsSync(path)) {
    // Defaults are best-effort (not every build emits both .d.ts and .d.cts); an explicitly
    // named file that's missing is a real error worth failing the build over.
    if (explicitTargets.length > 0) {
      throw new Error(`prepend-node-types-reference: ${path} does not exist`);
    }
    continue;
  }

  const original = readFileSync(path, 'utf8');
  if (original.startsWith(REFERENCE)) {
    continue; // Already present -- don't duplicate.
  }

  writeFileSync(path, `${REFERENCE}\n${original}`, 'utf8');
  console.log(`prepend-node-types-reference: added Node types reference to ${file}`);
}
