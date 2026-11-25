using System.Text;

namespace Urlog.Tests.Shared;

/// <summary>
/// The memory ceiling of every ClickHouse server a test fixture starts. The deployment's configuration gives a
/// server a share of the machine's memory (<c>max_server_memory_usage_to_ram_ratio</c> 0.6), and the server
/// works that share out again and again from what is free at the moment, so a test server's ceiling fell when
/// anything else on the box held memory: 3.45 GiB once, under a server that needs 3.1 GiB at its peak (the
/// worker crash tests, measured), and the tests failed on "memory limit exceeded". A fixture's server gets a
/// fixed ceiling instead: the measured peak with a margin of about 30 %. The ratio beside it is the server's
/// own default, since the smaller of the two applies and 0.6 of a busy box could still fall under the ceiling.
/// A file of its own, read after the deployment's (the name sorts last), so nothing of the deployment's file
/// is repeated here and nothing in deploy/ changes.
/// </summary>
internal static class TestServerMemory
{
    /// <summary>4 GiB.</summary>
    public const long CeilingBytes = 4L << 30;

    /// <summary>Where the file goes in the server's configuration directory.</summary>
    public const string ContainerPath = "/etc/clickhouse-server/config.d/zz-test-memory.xml";

    public static string ConfigXml =>
        $"""
        <clickhouse>
            <max_server_memory_usage>{CeilingBytes}</max_server_memory_usage>
            <max_server_memory_usage_to_ram_ratio>0.9</max_server_memory_usage_to_ram_ratio>
        </clickhouse>

        """;

    public static byte[] ConfigBytes => Encoding.UTF8.GetBytes(ConfigXml);
}
