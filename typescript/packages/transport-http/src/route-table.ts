import type { ResolvedHttpTransportOptions } from './options.js';

/**
 * Resolves a message type name (publish) or an explicit destination name (send) to the base
 * URL(s) of the remote endpoint(s) to POST to. Deliberately does not know about local
 * subscribers -- that union (docs/design/http-based-sagas.md §3.3a) is HttpMessageTransport's job, so
 * this stays a pure function of config. Mirrors
 * dotnet/src/VSaga.Transport.Http/IHttpRouteTable.cs.
 */
export interface HttpRouteTable {
  /** Remote endpoint base URLs configured for this message type via `routes` -- never includes local subscribers. */
  resolveRemoteEndpoints(messageTypeName: string): readonly string[];
  /** Resolves an explicit send() destination as an endpoint name directly, bypassing `routes` -- the AMQP default-exchange analogue. Undefined if not configured. */
  resolveEndpointByName(destinationName: string): string | undefined;
}

/** Routes key matching any message type with no explicit entry -- see HttpTransportOptions.routes. */
export const WILDCARD_ROUTE = '*';

/** Default HttpRouteTable, reading straight from HttpTransportOptions. */
export function createConfigRouteTable(options: ResolvedHttpTransportOptions): HttpRouteTable {
  return {
    resolveRemoteEndpoints(messageTypeName) {
      const endpointNames = options.routes[messageTypeName] ?? options.routes[WILDCARD_ROUTE];
      if (!endpointNames || endpointNames.length === 0) return [];

      const urls: string[] = [];
      for (const name of endpointNames) {
        const url = options.endpoints[name];
        if (url !== undefined) urls.push(url);
      }
      return urls;
    },

    resolveEndpointByName(destinationName) {
      return options.endpoints[destinationName];
    },
  };
}
