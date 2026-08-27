import js from '@eslint/js';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  {
    // dashboard-web keeps its own toolchain (see docs/typescript-participants.md).
    ignores: ['dashboard-web/**', '**/dist/**', '**/node_modules/**', '**/coverage/**'],
  },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  {
    rules: {
      '@typescript-eslint/consistent-type-imports': 'error',
      '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_' }],
    },
  },
  {
    // The layering rule that keeps a future @vsaga/core drop-in: transports and the participant
    // runtime see only the wire contract, mirroring how the .NET transport adapters reference
    // VSaga.Abstractions and never VSaga.Core (dotnet/src/VSaga.Abstractions/Transport/IMessageTransport.cs).
    files: ['packages/protocol/**/*.ts'],
    rules: {
      'no-restricted-imports': [
        'error',
        {
          patterns: [
            {
              group: ['@vsaga/*'],
              message:
                '@vsaga/protocol is the leaf package: it must not depend on any other @vsaga package.',
            },
          ],
        },
      ],
    },
  },
);
