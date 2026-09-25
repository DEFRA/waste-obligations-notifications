using Defra.WasteObligations.Consumer.Utils.Logging;
using Microsoft.AspNetCore.HeaderPropagation;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Defra.WasteObligations.Consumer.Tests.Utils.Logging;

public class TraceIdReaderTests
{
    [Fact]
    public void Read_WhenNoHeadersHaveBeenPropagated_ShouldReturnNull()
    {
        var subject = CreateSubject(new HeaderPropagationValues());

        var actual = subject.Read();

        Assert.Null(actual);
    }

    [Fact]
    public void Read_WhenTheTraceHeaderIsNotPresent_ShouldReturnNull()
    {
        var subject = CreateSubject(new HeaderPropagationValues { Headers = new Dictionary<string, StringValues>() });

        var actual = subject.Read();

        Assert.Null(actual);
    }

    [Fact]
    public void Read_WhenTheTraceHeaderIsWhitespace_ShouldReturnNull()
    {
        var subject = CreateSubject(WithTraceHeader("  "));

        var actual = subject.Read();

        Assert.Null(actual);
    }

    [Fact]
    public void Read_WhenTheTraceHeaderIsPresent_ShouldReturnItsValue()
    {
        var subject = CreateSubject(WithTraceHeader("request-123"));

        var actual = subject.Read();

        Assert.Equal("request-123", actual);
    }

    private static TraceIdReader CreateSubject(HeaderPropagationValues values) =>
        new(values, Options.Create(new TraceHeader { Name = "x-cdp-request-id" }));

    private static HeaderPropagationValues WithTraceHeader(string traceId) =>
        new() { Headers = new Dictionary<string, StringValues> { ["x-cdp-request-id"] = traceId } };
}
