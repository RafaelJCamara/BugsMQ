using System.Text;

namespace VSaga.Transport.Brighter;

/// <summary>
/// Lower-kebab-case of a message type's short name, e.g. <c>ReserveInventory</c> -> <c>reserve-inventory</c>.
/// Deliberately duplicated from <c>VSaga.Transport.RabbitMQ.DefaultRoutingKeyConvention</c> rather than
/// shared: this adapter must not take a project dependency on a sibling adapter's directory (see the
/// file-touch rules this track was built under), and the logic is small enough that duplicating it here
/// is cheaper than introducing a new shared package for one 20-line helper.
/// </summary>
internal static class RoutingKeyConvention
{
    public static string GetRoutingKey(string messageTypeName) => ToKebabCase(messageTypeName);

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
