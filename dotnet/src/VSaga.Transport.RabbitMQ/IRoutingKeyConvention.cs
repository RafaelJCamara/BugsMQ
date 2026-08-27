using System.Text;

namespace VSaga.Transport.RabbitMQ;

/// <summary>
/// Maps a message type name to the topic-exchange routing key used to publish/bind it. Operates on
/// the short type name (string) rather than a CLR Type so the same convention covers both normal
/// typed publishes and <see cref="RabbitMqTransport.PublishRawAsync"/>'s type-name-only path.
/// </summary>
public interface IRoutingKeyConvention
{
    string GetRoutingKey(string messageTypeName);
}

/// <summary>Lower-kebab-case of the message type's short name, e.g. <c>ReserveInventory</c> -> <c>reserve-inventory</c>.</summary>
public sealed class DefaultRoutingKeyConvention : IRoutingKeyConvention
{
    public string GetRoutingKey(string messageTypeName) => ToKebabCase(messageTypeName);

    private static string ToKebabCase(string value)
    {
        var builder = new StringBuilder(value.Length + 8);

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (char.IsUpper(c))
            {
                if (i > 0)
                    builder.Append('-');

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
