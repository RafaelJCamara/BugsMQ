using System.Diagnostics;
using VSaga.Abstractions.Diagnostics;
using VSaga.Abstractions.Transport;

namespace VSaga.Core.Tests;

/// <summary>
/// Hand-rolled W3C `traceparent`/`tracestate` inject/extract (production readiness §8.16). New,
/// previously-untested surface -- nothing else in the suite exercises this parsing, so it is covered
/// directly here rather than only incidentally through <see cref="MessageEnvelope"/>.
/// </summary>
public sealed class VSagaDiagnosticsTraceContextTests
{
    private static readonly ActivityTraceId TraceId = ActivityTraceId.CreateFromString("4bf92f3577b34da6a3ce929d0e0e4736");
    private static readonly ActivitySpanId SpanId = ActivitySpanId.CreateFromString("00f067aa0ba902b7");

    [Fact]
    public void Inject_ThenExtract_RoundTripsTheSameTraceAndSpanIds()
    {
        var original = new ActivityContext(TraceId, SpanId, ActivityTraceFlags.Recorded);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);

        VSagaDiagnostics.Inject(original, headers);

        Assert.True(headers.TryGetValue("traceparent", out var traceparent));
        Assert.Equal("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", traceparent);

        Assert.True(VSagaDiagnostics.TryExtractActivityContext(headers, out var extracted));
        Assert.Equal(original.TraceId, extracted.TraceId);
        Assert.Equal(original.SpanId, extracted.SpanId);
        Assert.Equal(original.TraceFlags, extracted.TraceFlags);
        Assert.True(extracted.IsRemote); // extracted contexts represent a remote parent, not a local one
    }

    [Fact]
    public void Inject_CarriesTraceStateAlongsideTraceParent_AndExtractRoundTripsIt()
    {
        var original = new ActivityContext(TraceId, SpanId, ActivityTraceFlags.None, traceState: "vendor1=value1,vendor2=value2");
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);

        VSagaDiagnostics.Inject(original, headers);

        Assert.Equal("vendor1=value1,vendor2=value2", headers["tracestate"]);

        Assert.True(VSagaDiagnostics.TryExtractActivityContext(headers, out var extracted));
        Assert.Equal("vendor1=value1,vendor2=value2", extracted.TraceState);
    }

    [Fact]
    public void Inject_WithoutTraceState_WritesNoTraceStateHeader()
    {
        var original = new ActivityContext(TraceId, SpanId, ActivityTraceFlags.None);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);

        VSagaDiagnostics.Inject(original, headers);

        Assert.False(headers.ContainsKey("tracestate"));
    }

    [Fact]
    public void Inject_WithDefaultContext_WritesNothing()
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);

        VSagaDiagnostics.Inject(default, headers);

        Assert.Empty(headers);
    }

    [Fact]
    public void TryExtractActivityContext_MissingHeader_ReturnsFalseAndDefaultContext()
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);

        var result = VSagaDiagnostics.TryExtractActivityContext(headers, out var context);

        Assert.False(result);
        Assert.Equal(default, context);
    }

    [Fact]
    public void TryExtractActivityContext_HeadersWithOnlyUnrelatedEntries_ReturnsFalse()
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["x-vsaga-source-service"] = "OrderSaga",
        };

        Assert.False(VSagaDiagnostics.TryExtractActivityContext(headers, out var context));
        Assert.Equal(default, context);
    }

    [Theory]
    [InlineData("")] // empty
    [InlineData("not-a-traceparent")] // nowhere near the right shape
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-1")] // one char short (bad flags length)
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-011")] // one char long
    [InlineData("01-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")] // unsupported version
    [InlineData("00_4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")] // wrong delimiter after version
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736_00f067aa0ba902b7-01")] // wrong delimiter after trace id
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7_01")] // wrong delimiter after span id
    [InlineData("00-4BF92F3577B34DA6A3CE929D0E0E4736-00f067aa0ba902b7-01")] // uppercase hex in trace id
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00F067AA0BA902B7-01")] // uppercase hex in span id
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-zz")] // non-hex flags
    [InlineData("00-gggggggggggggggggggggggggggggggg-00f067aa0ba902b7-01")] // non-hex trace id
    [InlineData("00-00000000000000000000000000000000-00f067aa0ba902b7-01")] // all-zero trace id: the spec's invalid-id sentinel
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-0000000000000000-01")] // all-zero span id: same sentinel
    public void TryExtractActivityContext_MalformedHeader_ReturnsFalseWithoutThrowing(string malformed)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["traceparent"] = malformed };

        var exception = Record.Exception(() => VSagaDiagnostics.TryExtractActivityContext(headers, out _));

        Assert.Null(exception);
        Assert.False(VSagaDiagnostics.TryExtractActivityContext(headers, out var extractedContext));
        Assert.Equal(default, extractedContext);
    }
}
