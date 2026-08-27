/**
 * Port of DefaultRoutingKeyConvention.ToKebabCase
 * (dotnet/src/VSaga.Transport.RabbitMQ/IRoutingKeyConvention.cs).
 *
 * This is deliberately NOT lodash's `kebabCase`, and the difference is not academic -- a routing key
 * that disagrees with the .NET binding means the message is published to a key nothing is bound to:
 *
 *   input              vSaga            lodash.kebabCase
 *   ReserveInventory   reserve-inventory  reserve-inventory   (agree)
 *   HTTPOrder          h-t-t-p-order      http-order          (differ)
 *   OrderID            order-i-d          order-id            (differ)
 *   Order2Ship         order2-ship        order-2-ship        (differ)
 *
 * The rule is literally "insert `-` before every uppercase char except at index 0, and lowercase
 * only uppercase chars". Nothing else is touched.
 *
 * The .NET original uses `char.IsUpper`, which is Unicode-aware; this narrows to ASCII A-Z, which is
 * a deliberate simplification -- every vSaga contract type name is an ASCII C# identifier. If that
 * ever stops being true, this is the line to revisit.
 */
export function toRoutingKey(messageTypeName: string): string {
  let out = '';

  for (let i = 0; i < messageTypeName.length; i++) {
    const c = messageTypeName[i]!;

    if (c >= 'A' && c <= 'Z') {
      if (i > 0) out += '-';
      out += c.toLowerCase();
    } else {
      out += c;
    }
  }

  return out;
}
