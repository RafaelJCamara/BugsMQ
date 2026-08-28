import { defineConfig } from 'tsup';

export default defineConfig({
  entry: ['src/index.ts'],
  format: ['esm'],
  target: 'node22',
  clean: true,
  // An application, not a library: no consumer ever imports these types, and skipping the
  // declaration rollup keeps the sample's build a fraction of a package's.
  dts: false,
});
