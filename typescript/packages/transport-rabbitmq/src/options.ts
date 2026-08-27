/** Mirrors RabbitMqOptions (dotnet/src/VSaga.Transport.RabbitMQ/RabbitMqOptions.cs). */
export interface RabbitMqTransportOptions {
  readonly connectionString?: string;
  readonly exchangeName?: string;
  readonly deadLetterExchangeName?: string;
  readonly clientProvidedName?: string;
  readonly prefetchCount?: number;
}

export interface ResolvedRabbitMqOptions {
  readonly connectionString: string;
  readonly exchangeName: string;
  readonly deadLetterExchangeName: string;
  readonly clientProvidedName: string;
  readonly prefetchCount: number;
}

export function resolveOptions(options: RabbitMqTransportOptions = {}): ResolvedRabbitMqOptions {
  return {
    connectionString: options.connectionString ?? 'amqp://guest:guest@localhost:5672/',
    exchangeName: options.exchangeName ?? 'vsaga.saga.events',
    deadLetterExchangeName: options.deadLetterExchangeName ?? 'vsaga.dlx',
    clientProvidedName: options.clientProvidedName ?? 'VSaga',
    // Matches RabbitMqTransport.SubscribeAsync's BasicQosAsync(prefetchCount: 32, global: false).
    prefetchCount: options.prefetchCount ?? 32,
  };
}
