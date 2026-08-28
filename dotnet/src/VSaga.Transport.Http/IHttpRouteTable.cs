namespace VSaga.Transport.Http;

/// <summary>
/// Resolves a message type name (PublishAsync/PublishRawAsync) or an explicit destination name
/// (SendAsync) to the base URL(s) of the remote endpoint(s) to POST to. Deliberately does not know
/// about local subscribers -- that union (§3.3a of docs/design/http-based-sagas.md) is
/// <see cref="HttpMessageTransport"/>'s job, so this interface stays a pure function of config and can
/// be swapped for e.g. a service-discovery-backed implementation without touching dispatch logic.
/// </summary>
public interface IHttpRouteTable
{
    /// <summary>Remote endpoint base URLs configured for this message type via Routes -- never includes local subscribers.</summary>
    IReadOnlyList<string> ResolveRemoteEndpoints(string messageTypeName);

    /// <summary>Resolves an explicit SendAsync destination as an endpoint name directly, bypassing Routes -- the AMQP default-exchange analogue. Null if not configured.</summary>
    string? ResolveEndpointByName(string destinationName);
}

/// <summary>Default <see cref="IHttpRouteTable"/> reading straight from <see cref="HttpTransportOptions"/>.</summary>
public sealed class ConfigHttpRouteTable(HttpTransportOptions options) : IHttpRouteTable
{
    /// <summary>Routes key matching any message type with no explicit entry -- see HttpTransportOptions.Routes.</summary>
    public const string WildcardRoute = "*";

    public IReadOnlyList<string> ResolveRemoteEndpoints(string messageTypeName)
    {
        if (!options.Routes.TryGetValue(messageTypeName, out var endpointNames) &&
            !options.Routes.TryGetValue(WildcardRoute, out endpointNames))
        {
            return [];
        }

        if (endpointNames.Count == 0)
            return [];

        var urls = new List<string>(endpointNames.Count);
        foreach (var name in endpointNames)
        {
            if (options.Endpoints.TryGetValue(name, out var url))
                urls.Add(url);
        }

        return urls;
    }

    public string? ResolveEndpointByName(string destinationName) =>
        options.Endpoints.TryGetValue(destinationName, out var url) ? url : null;
}
