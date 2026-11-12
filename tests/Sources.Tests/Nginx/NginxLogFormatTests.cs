using Urlog.Core.Sources;
using System.Text;
using Urlog.Sources.Nginx;

namespace Urlog.Sources.Tests.Nginx;

/// <summary>The <c>log_format</c> compiler: field tables of the built-ins, the variable mapping, and what it refuses.</summary>
public sealed class NginxLogFormatTests
{
    [Fact]
    public void CombinedCompilesToTenFieldsWithTheRightTerminators()
    {
        var layout = NginxLayout.Combined;
        Assert.Equal("combined", layout.Name);
        Assert.Empty(layout.Prefix);
        Assert.Equal(["remote_addr", "ident", "remote_user", "time_local", "request", "status", "body_bytes_sent", "http_referer", "http_user_agent"], layout.Fields.Select(f => f.Variable));
        Assert.Equal([" ", " ", " [", "] \"", "\" ", " ", " \"", "\" \"", "\""], layout.Fields.Select(f => Encoding.UTF8.GetString(f.Terminator)));
        Assert.Equal([false, false, false, false, true, false, false, true, true], layout.Fields.Select(f => f.Quoted));
        Assert.Equal(ClfTimestamp.Length, layout.Fields[3].FixedWidth);
        Assert.Equal(NginxVariableKind.SourceIp, layout.Fields[0].Kind);
        Assert.Equal(NginxVariableKind.Ident, layout.Fields[1].Kind);
        Assert.Equal(NginxVariableKind.User, layout.Fields[2].Kind);
        Assert.Equal(NginxVariableKind.TimeLocal, layout.Fields[3].Kind);
        Assert.Equal(NginxVariableKind.Request, layout.Fields[4].Kind);
        Assert.Equal(NginxVariableKind.Status, layout.Fields[5].Kind);
        Assert.Equal("http.response.body.bytes", layout.Fields[6].Key);
        Assert.Equal("http_referrer", layout.Fields[7].Key); // promoted columns
        Assert.Equal(EventColumn.HttpReferrer, layout.Fields[7].Column);
        Assert.Equal("user_agent", layout.Fields[8].Key);
        Assert.Equal(EventColumn.UserAgent, layout.Fields[8].Column);
        Assert.Equal(4, layout.MessageFirst); // "request" status bytes
        Assert.Equal(6, layout.MessageLast);
    }

    [Fact]
    public void CommonIsCombinedWithoutTheQuotedTail()
    {
        var layout = NginxLayout.Common;
        Assert.Equal(7, layout.Fields.Length);
        Assert.Empty(layout.Fields[^1].Terminator);
        Assert.Equal(4, layout.MessageFirst);
        Assert.Equal(6, layout.MessageLast);
    }

    [Fact]
    public void IngressNginxMapsUpstreamAndDurationVariables()
    {
        var layout = NginxLayout.IngressNginx;
        Assert.Equal("ingress-nginx", layout.Name);
        Assert.Equal(17, layout.Fields.Length);
        var byName = layout.Fields.ToDictionary(f => f.Variable, StringComparer.Ordinal);
        Assert.Equal(NginxVariableKind.DurationSeconds, byName["request_time"].Kind);
        Assert.Equal("event.duration", byName["request_time"].Key);
        Assert.Equal("http.request.bytes", byName["request_length"].Key);
        Assert.Equal("upstream.addr", byName["upstream_addr"].Key);
        Assert.Equal("upstream.status", byName["upstream_status"].Key);
        Assert.Equal("http.request.id", byName["req_id"].Key);
        Assert.Equal("proxy_upstream_name", byName["proxy_upstream_name"].Key); // unknown: its own name
        Assert.Equal("]", Encoding.UTF8.GetString(byName["proxy_upstream_name"].Terminator)[..1]);
        Assert.Equal(" - ", Encoding.UTF8.GetString(layout.Fields[0].Terminator)); // the literal "-" ident
    }

    [Fact]
    public void BuiltInsResolveByNameCaseInsensitively()
    {
        Assert.Same(NginxLayout.Combined, NginxLayout.BuiltIn("Combined"));
        Assert.Same(NginxLayout.IngressNginx, NginxLayout.Resolve(" INGRESS-NGINX "));
        Assert.Null(NginxLayout.BuiltIn("extended"));
        Assert.Equal([NginxLayout.IngressNginx, NginxLayout.Combined, NginxLayout.Common], NginxLayout.BuiltIns);
        Assert.Equal("custom", NginxLayout.Resolve("$remote_addr $status").Name);
    }

    [Theory]
    [InlineData("remote_addr", NginxVariableKind.SourceIp, "")]
    [InlineData("remote_user", NginxVariableKind.User, "")]
    [InlineData("time_iso8601", NginxVariableKind.TimeIso8601, "")]
    [InlineData("msec", NginxVariableKind.Msec, "")]
    [InlineData("status", NginxVariableKind.Status, "")]
    [InlineData("request_time", NginxVariableKind.DurationSeconds, "event.duration")]
    [InlineData("body_bytes_sent", NginxVariableKind.BodyBytes, "http.response.body.bytes")]
    [InlineData("bytes_sent", NginxVariableKind.Extra, "http.response.bytes")]
    [InlineData("http_x_forwarded_for", NginxVariableKind.Extra, "network.forwarded_ip")]
    [InlineData("host", NginxVariableKind.Extra, "url.domain")]
    [InlineData("server_name", NginxVariableKind.Extra, "server.domain")]
    [InlineData("server_protocol", NginxVariableKind.TextColumn, "http_version")]
    [InlineData("request_method", NginxVariableKind.TextColumn, "http_method")]
    [InlineData("request_uri", NginxVariableKind.Extra, "url.original")]
    [InlineData("args", NginxVariableKind.TextColumn, "url_query")]
    [InlineData("scheme", NginxVariableKind.Extra, "url.scheme")]
    [InlineData("request_id", NginxVariableKind.Extra, "http.request.id")]
    [InlineData("geoip_country_code", NginxVariableKind.Extra, "source.geo.country_iso_code")]
    [InlineData("upstream_cache_status", NginxVariableKind.Extra, "upstream.cache_status")]
    [InlineData("upstream_connect_time", NginxVariableKind.Extra, "upstream.connect_time")]
    [InlineData("http_x_real_ip", NginxVariableKind.Extra, "http_x_real_ip")]
    [InlineData("ssl_protocol", NginxVariableKind.Extra, "ssl_protocol")]
    public void VariablesMapToEcsKeysOrTheirOwnName(string variable, NginxVariableKind kind, string key)
    {
        var field = Assert.Single(NginxLayout.Compile("$" + variable).Fields);
        Assert.Equal(kind, field.Kind);
        Assert.Equal(key, field.Key);
    }

    [Fact]
    public void LiteralPrefixBracesAndTimestampsAreHandled()
    {
        var layout = NginxLayout.Compile("ts=${time_iso8601} ip=$remote_addr \"${request}\"");
        Assert.Equal("ts=", Encoding.UTF8.GetString(layout.Prefix));
        Assert.Equal(["time_iso8601", "remote_addr", "request"], layout.Fields.Select(f => f.Variable));
        Assert.Equal(25, layout.Fields[0].FixedWidth);
        Assert.True(layout.Fields[2].Quoted);
        Assert.Equal("\"", Encoding.UTF8.GetString(layout.Fields[2].Terminator));
        Assert.Equal(2, layout.MessageFirst);
        Assert.Equal(2, layout.MessageLast);

        var quotedTime = NginxLayout.Compile("\"$time_local\" $status");
        Assert.True(quotedTime.Fields[0].Quoted);
        Assert.Equal(0, quotedTime.Fields[0].FixedWidth); // scanned as a quoted field instead
        Assert.Equal(1, quotedTime.MessageFirst);
    }

    [Fact]
    public void MessageRunStopsAtTheFirstFieldThatIsNotRequestStatusOrBytes()
    {
        var layout = NginxLayout.Compile("$remote_addr \"$request\" $status $request_time $body_bytes_sent");
        Assert.Equal(1, layout.MessageFirst);
        Assert.Equal(2, layout.MessageLast);

        var noRequest = NginxLayout.Compile("$remote_addr $status $bytes_sent $http_user_agent");
        Assert.Equal(1, noRequest.MessageFirst);
        Assert.Equal(2, noRequest.MessageLast);

        var neither = NginxLayout.Compile("$remote_addr $http_user_agent");
        Assert.Equal(-1, neither.MessageFirst);
    }

    [Theory]
    [InlineData("", "no variables")]
    [InlineData("no variables here", "no variables")]
    [InlineData("$remote_addr$status", "no literal between")]
    [InlineData("$remote_addr $", "not followed by a variable name")]
    [InlineData("$remote_addr ${status", "not closed")]
    [InlineData("$-", "not followed by a variable name")]
    public void InvalidFormatsAreRefusedWithAReason(string format, string reason)
    {
        Assert.False(NginxLayout.TryCompile(format, out var layout, out var error));
        Assert.Null(layout);
        Assert.Contains(reason, error, StringComparison.Ordinal);
        var ex = Assert.Throws<FormatException>(() => NginxLayout.Compile(format));
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TooManyVariablesAreRefused()
    {
        var format = string.Join(' ', Enumerable.Range(0, NginxLayout.MaxFields + 1).Select(i => "$v" + i));
        Assert.False(NginxLayout.TryCompile(format, out _, out var error));
        Assert.Contains("at most", error, StringComparison.Ordinal);
        Assert.True(NginxLayout.TryCompile(string.Join(' ', Enumerable.Range(0, NginxLayout.MaxFields).Select(i => "$v" + i)), out _, out _));
    }

    [Fact]
    public void CustomFixtureFormatsCompile()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(Fixtures.Root, "fixtures", "nginx", "custom"), "*.format"))
        {
            var format = File.ReadAllText(file).Trim();
            Assert.True(NginxLayout.TryCompile(format, out var layout, out var error), $"{file}: {error}");
            Assert.True(layout.Fields.Length >= 7, file);
        }
    }
}
