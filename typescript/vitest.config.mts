import { defineConfig } from 'vitest/config';

// dashboard-web is deliberately excluded: it is not an npm-workspace member (see
// docs/typescript-participants.md) and runs its own vitest via `ng test`, with an Angular-specific
// jsdom setup that has nothing to do with the SDK packages.
export default defineConfig({
  test: {
    include: ['packages/*/test/**/*.test.ts', 'samples/*/test/**/*.test.ts'],
    exclude: ['**/node_modules/**', '**/dist/**', 'dashboard-web/**'],
    environment: 'node',
  },
});
