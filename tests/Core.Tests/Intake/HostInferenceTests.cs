using Urlog.Core.Intake;

namespace Urlog.Core.Tests.Intake;

/// <summary>
/// The default pattern list over the path shapes uploads have. A proposal is never required: a path without a
/// host-shaped folder stays unassigned.
/// </summary>
public sealed class HostInferenceTests
{
    [Theory]
    [InlineData("web-01/nginx/access.log.3", "web-01")]
    [InlineData("bundle.zip!web-01/nginx/access.log.3", "web-01")]
    [InlineData("bundle.zip!logs/inner.tar.gz!web-01/nginx/access.log.3", "web-01")]
    [InlineData("collect.tar.gz!db-02/var/log/syslog.1", "db-02")]
    [InlineData("apache-host/apache2/error.log", "apache-host")]
    [InlineData("mail01/httpd/access_log", "mail01")]
    [InlineData("IIS-FE1/inetpub/logs/LogFiles/W3SVC1/u_ex261101.log", "iis-fe1")]
    [InlineData("export.zip!FE2/W3SVC2/u_ex261102.log", "fe2")]
    [InlineData("dc01/winevt/Logs/Security.evtx", "dc01")]
    [InlineData("triage.zip!WS-114/C/Windows/System32/winevt/Logs/System.evtx", "ws-114")]
    [InlineData("fileserver.example.org/Windows/System32/winevt/Logs/Application.evtx", "fileserver.example.org")]
    [InlineData("web-03.zip!access.log", "web-03")]
    [InlineData("db-01-logs.tar.gz!postgresql.log", "db-01")]
    [InlineData("web-04_access.log.2.gz", "web-04")]
    [InlineData("backup\\srv-9\\var\\log\\auth.log", "srv-9")]
    [InlineData("k8s-node-3/var/lib/docker/containers/abc/abc-json.log", "k8s-node-3")]
    [InlineData("10.0.0.5/nginx/access.log", "10.0.0.5")]
    [InlineData("access.log", null)]
    [InlineData("logs/2026-11-01/nginx/access.log", null)]
    [InlineData("var/log/nginx/access.log", null)]
    [InlineData("bundle.zip!nginx/access.log", null)]
    [InlineData("collect.tar.gz!var/log/syslog", null)]
    [InlineData("error_2026.log", null)]
    public void TheDefaultPatternsProposeTheHostFolder(string path, string? expected)
    {
        var proposal = HostInference.Default.Infer(path);

        Assert.Equal(expected, proposal?.Host);
        if (expected is not null)
        {
            Assert.False(string.IsNullOrEmpty(proposal!.Rule));
        }
    }

    [Fact]
    public void AnArchiveNameIsNeverTakenForAHostFolder()
    {
        // "bundle.zip" has the shape of a host name with a domain; a folder pattern must not capture it.
        Assert.Null(HostInference.Default.Infer("bundle.zip!var/log/auth.log"));
        var named = HostInference.Default.Infer("srv.example.zip!inner.tar!var/log/auth.log");
        Assert.Equal("srv.example", named!.Host);
        Assert.DoesNotContain("var/log", named.Rule, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFirstPatternThatYieldsAHostWins()
    {
        var inference = new HostInference(["**/{host}/b/**", "{host}/**"]);

        Assert.Equal("inner", inference.Infer("outer/inner/b/x.log")!.Host);
        Assert.Equal("**/{host}/b/**", inference.Infer("outer/inner/b/x.log")!.Rule);
        Assert.Equal("outer", inference.Infer("outer/inner/c/x.log")!.Host);
    }

    [Fact]
    public void ACandidateOnTheStopListDoesNotEndTheSearch()
    {
        var inference = new HostInference(["**/{host}/nginx/**", "{host}.zip!**"]);

        Assert.Equal("web-07", inference.Infer("web-07.zip!log/nginx/access.log")!.Host);
    }

    [Theory]
    [InlineData("web-01", true)]
    [InlineData("WEB_01", true)]
    [InlineData("a", false)]
    [InlineData("2026-11-01", false)]
    [InlineData("12345", false)]
    [InlineData("-web", false)]
    [InlineData("web-", false)]
    [InlineData("we b", false)]
    [InlineData("logs", false)]
    [InlineData("192.168.1.20", true)]
    [InlineData("300.1.1.1", false)]
    public void HostShape(string candidate, bool expected) =>
        Assert.Equal(expected, HostInference.Default.IsHostShaped(candidate));

    [Theory]
    [InlineData("{host}")]
    [InlineData("**/nginx/**")]
    [InlineData("{host}/{host}/**")]
    [InlineData("a/{host/**")]
    [InlineData("")]
    public void APatternWithoutExactlyOneHostPlaceholderIsRefused(string pattern)
    {
        if (pattern == "{host}")
        {
            // One placeholder alone is a valid pattern: a file named after its host.
            Assert.NotNull(new HostInference([pattern]));
            return;
        }

        Assert.Throws<ArgumentException>(() => new HostInference([pattern]));
    }
}
