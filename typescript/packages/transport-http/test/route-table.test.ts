import { describe, expect, it } from 'vitest';

import { type HttpTransportOptions, resolveOptions } from '../src/options.js';
import { WILDCARD_ROUTE, createConfigRouteTable } from '../src/route-table.js';

/**
 * Mirrors dotnet/tests/VSaga.Transport.Http.Tests/ConfigHttpRouteTableTests.cs case for case.
 *
 * The route table is a pure function of config, so it is worth pinning directly rather than only
 * through the transport: the wildcard fallback and the drop-unknown-endpoint-name rule are both
 * silent behaviours -- they turn a config mistake into "the message went somewhere else" or "the
 * message went nowhere" rather than into an error -- and the two runtimes have to agree on them
 * exactly, or the same appsettings.json/`HttpTransportOptions` routes differently per runtime.
 */
describe('createConfigRouteTable', () => {
  function routeTable(options: HttpTransportOptions) {
    return createConfigRouteTable(resolveOptions(options));
  }

  describe('resolveRemoteEndpoints', () => {
    it('resolves each endpoint name in the route to its base URL, in order', () => {
      const table = routeTable({
        endpoints: { payments: 'http://payments:8080', shipping: 'http://shipping:8080' },
        routes: { OrderPlaced: ['payments', 'shipping'] },
      });

      expect(table.resolveRemoteEndpoints('OrderPlaced')).toEqual([
        'http://payments:8080',
        'http://shipping:8080',
      ]);
    });

    it('yields nothing for a message type with no route and no wildcard', () => {
      const table = routeTable({
        endpoints: { payments: 'http://payments:8080' },
        routes: { OrderPlaced: ['payments'] },
      });

      expect(table.resolveRemoteEndpoints('SomethingElse')).toEqual([]);
    });

    it('falls back to the "*" wildcard for a message type with no explicit entry', () => {
      const table = routeTable({
        endpoints: { hub: 'http://hub:8080' },
        routes: { [WILDCARD_ROUTE]: ['hub'] },
      });

      expect(table.resolveRemoteEndpoints('AnyMessageAtAll')).toEqual(['http://hub:8080']);
    });

    it('prefers an explicit entry over the wildcard', () => {
      const table = routeTable({
        endpoints: { payments: 'http://payments:8080', hub: 'http://hub:8080' },
        routes: { OrderPlaced: ['payments'], [WILDCARD_ROUTE]: ['hub'] },
      });

      expect(table.resolveRemoteEndpoints('OrderPlaced')).toEqual(['http://payments:8080']);
      expect(table.resolveRemoteEndpoints('OrderCancelled')).toEqual(['http://hub:8080']);
    });

    // Subtle, and the two runtimes agree only by coincidence of how each looks the key up: TS
    // short-circuits on `??` (an empty array is not nullish, so the wildcard is never consulted),
    // .NET on TryGetValue succeeding before its Count == 0 check. An explicit empty list is how you
    // say "this one type goes nowhere" while a wildcard covers everything else, so it has to stay
    // an opt-out rather than a fall-through.
    it('treats an explicit empty route list as "no endpoints", not as a miss that falls through to the wildcard', () => {
      const table = routeTable({
        endpoints: { hub: 'http://hub:8080' },
        routes: { OrderPlaced: [], [WILDCARD_ROUTE]: ['hub'] },
      });

      expect(table.resolveRemoteEndpoints('OrderPlaced')).toEqual([]);
    });

    it('drops a route entry naming an endpoint that was never configured', () => {
      const table = routeTable({
        endpoints: { payments: 'http://payments:8080' },
        routes: { OrderPlaced: ['payments', 'typo-in-this-name'] },
      });

      expect(table.resolveRemoteEndpoints('OrderPlaced')).toEqual(['http://payments:8080']);
    });

    it('yields nothing when every endpoint name in the route is unknown', () => {
      const table = routeTable({
        endpoints: { payments: 'http://payments:8080' },
        routes: { OrderPlaced: ['nope', 'also-nope'] },
      });

      expect(table.resolveRemoteEndpoints('OrderPlaced')).toEqual([]);
    });

    it('yields nothing when nothing is configured at all', () => {
      expect(routeTable({}).resolveRemoteEndpoints('OrderPlaced')).toEqual([]);
    });
  });

  describe('resolveEndpointByName', () => {
    it('resolves a configured endpoint name to its base URL', () => {
      const table = routeTable({ endpoints: { payments: 'http://payments:8080' } });

      expect(table.resolveEndpointByName('payments')).toBe('http://payments:8080');
    });

    it('is undefined for an unconfigured name', () => {
      const table = routeTable({ endpoints: { payments: 'http://payments:8080' } });

      expect(table.resolveEndpointByName('shipping')).toBeUndefined();
    });

    // send()'s destination is an endpoint name, never a routes key -- §4.3's AMQP-default-exchange
    // analogue bypasses routing entirely, so a name that only exists in `routes` is not addressable.
    it('does not consult routes, so a routes key is not a valid destination', () => {
      const table = routeTable({
        endpoints: { payments: 'http://payments:8080' },
        routes: { OrderPlaced: ['payments'] },
      });

      expect(table.resolveEndpointByName('OrderPlaced')).toBeUndefined();
    });

    it('does not treat the wildcard as an addressable destination', () => {
      const table = routeTable({
        endpoints: { hub: 'http://hub:8080' },
        routes: { [WILDCARD_ROUTE]: ['hub'] },
      });

      expect(table.resolveEndpointByName(WILDCARD_ROUTE)).toBeUndefined();
    });
  });
});
