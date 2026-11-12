using System.Net;
using System.Text;
using Urlog.Sources.Common;

namespace Urlog.Sources.Tests.Common;

public sealed class IpTextTests
{
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("203.0.113.7")]
    [InlineData("255.255.255.255")]
    public void IPv4MatchesTheBcl(string text)
    {
        Span<byte> dest = stackalloc byte[16];
        Assert.Equal(4, IpText.TryParse(Encoding.ASCII.GetBytes(text), dest));
        Assert.Equal(IPAddress.Parse(text).GetAddressBytes(), dest[..4].ToArray());
    }

    [Fact]
    public void LeadingZerosAreDecimalNotOctal()
    {
        // inet_ntoa never emits leading zeros, so a logged "010" is the decimal 10; the BCL's octal reading is a legacy quirk.
        Span<byte> dest = stackalloc byte[16];
        Assert.Equal(4, IpText.TryParse("192.168.001.010"u8, dest));
        Assert.Equal(new byte[] { 192, 168, 1, 10 }, dest[..4].ToArray());
    }

    [Theory]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("1::")]
    [InlineData("2001:db8::1")]
    [InlineData("2001:db8:85a3::8a2e:370:7334")]
    [InlineData("2001:0db8:85a3:0000:0000:8a2e:0370:7334")]
    [InlineData("2a02:1234:5678:9abc:def0:1111:2222:3333")]
    [InlineData("fe80::1ff:fe23:4567:890a")]
    [InlineData("FE80::1FF:FE23:4567:890A")]
    [InlineData("::ffff:192.0.2.44")]
    [InlineData("::ffff:0:1.2.3.4")]
    [InlineData("64:ff9b::203.0.113.7")]
    [InlineData("1:2:3:4:5:6:1.2.3.4")]
    [InlineData("1:2:3:4:5::7:8")]
    [InlineData("1:2:3:4:5:6:7::")]
    public void IPv6MatchesTheBcl(string text)
    {
        Span<byte> dest = stackalloc byte[16];
        Assert.Equal(16, IpText.TryParse(Encoding.ASCII.GetBytes(text), dest));
        Assert.Equal(IPAddress.Parse(text).GetAddressBytes(), dest.ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("1")]
    [InlineData("1.2.3")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.2.3.256")]
    [InlineData("1.2.3.4a")]
    [InlineData("1..3.4")]
    [InlineData(".1.2.3")]
    [InlineData("1.2.3.4 ")]
    [InlineData("+1.2.3.4")]
    [InlineData("1.2.3.1234")]
    [InlineData("host.example.com")]
    [InlineData("unknown")]
    [InlineData(":")]
    [InlineData(":1")]
    [InlineData("1:")]
    [InlineData(":::")]
    [InlineData("1::2::3")]
    [InlineData("1:2:3:4:5:6:7:8:9")]
    [InlineData("1:2:3:4:5:6:7")]
    [InlineData("12345::")]
    [InlineData("g::1")]
    [InlineData("fe80::1%eth0")]
    [InlineData("[::1]")]
    [InlineData("[::1]:443")]
    [InlineData("::ffff:1.2.3")]
    [InlineData("1.2.3.4::")]
    [InlineData("1:2:3:4:5:6:7:1.2.3.4")]
    [InlineData("::1.2.3.4:5")]
    public void RejectsNonAddresses(string text)
    {
        Span<byte> dest = stackalloc byte[16];
        Assert.Equal(0, IpText.TryParse(Encoding.ASCII.GetBytes(text), dest));
    }
}
