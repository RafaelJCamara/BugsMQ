/** Minimal logging surface, so every framework integration can bridge its own logger in. */
export interface Logger {
  debug(message: string, context?: Record<string, unknown>): void;
  info(message: string, context?: Record<string, unknown>): void;
  warn(message: string, context?: Record<string, unknown>): void;
  error(message: string, context?: Record<string, unknown>): void;
}

export const noopLogger: Logger = {
  debug: () => {},
  info: () => {},
  warn: () => {},
  error: () => {},
};

export const consoleLogger: Logger = {
  debug: (m, c) => console.debug(m, c ?? ''),
  info: (m, c) => console.info(m, c ?? ''),
  warn: (m, c) => console.warn(m, c ?? ''),
  error: (m, c) => console.error(m, c ?? ''),
};
