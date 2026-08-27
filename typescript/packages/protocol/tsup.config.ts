import { defineConfig } from 'tsup';

export default defineConfig({
  entry: ['src/index.ts'],
  tsconfig: './tsconfig.build.json',
  // Dual output is not cosmetic: NestJS is CJS-first (its DI relies on decorator metadata emitted
  // by tsc/CJS), while Fastify plugins and the sample are ESM.
  format: ['esm', 'cjs'],
  dts: true,
  sourcemap: true,
  clean: true,
  target: 'node22',
});
