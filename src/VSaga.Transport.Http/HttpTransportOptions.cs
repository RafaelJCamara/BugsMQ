namespace VSaga.Transport.Http;

/// <summary>
/// Config surface for <see cref="HttpMessageTransport"/>. Bind from a config section, e.g.
/// <c>builder.Configuration.GetSection("Http").Bind(o)</c>.
/// </summary>
public sealed class HttpTransportOptions
{
    /// <summary>This process's own identity, for logging only -- never stamped onto envelopes (that's
    /// the saga/participant layer's job via MessageEnvelope.From).</summary>
    public string ServiceName { get; set; } = "vsaga-http";

    /// <summary>Endpoint name -> base URL, e.g. <c>{"payments": "http://payments:8080"}</c>.</summary>
    public IDictionary<string, string> Endpoints { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Message type name -> endpoint names to POST to on PublishAsync/PublishRawAsync. A <c>"*"</c> key
    /// is a wildcard fallback used for any type with no explicit entry -- e.g. a dashboard/ops process
    /// that only ever redrives messages toward a single saga host and has no reason to enumerate every
    /// message type that host understands.
    /// </summary>
    public IDictionary<string, IList<string>> Routes { get; set; } = new Dictionary<string, IList<string>>(StringComparer.Ordinal);

    /// <summary>Per-request timeout for the outbound HTTP call, including the participant's own processing time.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Path this service's own receive endpoint is mapped to by <c>MapVSagaHttp()</c>.</summary>
    public string InboundPath { get; set; } = "/vsaga/messages";
}
