using System.Net;
using OpenRestoApi.Core.Application.Utilities;

namespace OpenRestoApi.Tests.Utilities;

/// <summary>
/// The line between "an address the internet can reach" and "an address only this deployment
/// can": everything the server fetches on a caller's instruction is held to it, so both sides
/// of every range are pinned.
/// </summary>
public class PublicAddressTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.8.8.8")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.255")]
    [InlineData("64:ff9b::a00:1")]        // NAT64 around 10.0.0.1
    [InlineData("64:ff9b::7f00:1")]       // NAT64 around 127.0.0.1
    [InlineData("64:ff9b::c0a8:101")]     // NAT64 around 192.168.1.1
    [InlineData("64:ff9b::a9fe:a9fe")]    // NAT64 around the cloud metadata address
    [InlineData("64:ff9b::6440:1")]       // NAT64 around 100.64.0.1
    [InlineData("64:ff9b:1::1")]          // local-use NAT64, never the public internet
    [InlineData("2002:a00:1::")]          // 6to4 around 10.0.0.1
    [InlineData("2002:7f00:1::1")]        // 6to4 around 127.0.0.1
    [InlineData("2002:c0a8:101::")]       // 6to4 around 192.168.1.1
    [InlineData("2002:a9fe:a9fe::")]      // 6to4 around the cloud metadata address
    [InlineData("2002::")]                // 6to4 around 0.0.0.0
    public void IsPublic_RejectsLoopbackPrivateAndLinkLocalRanges(string address)
        => Assert.False(PublicAddress.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.15.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("100.63.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("203.0.113.9")]
    [InlineData("2606:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("198.17.255.255")]
    [InlineData("198.20.0.0")]
    [InlineData("64:ff9b::808:808")]      // NAT64 around the public 8.8.8.8
    [InlineData("2002:808:808::")]        // 6to4 around the public 8.8.8.8
    [InlineData("2002:cb00:7109::1")]     // 6to4 around the public 203.0.113.9
    public void IsPublic_AcceptsRoutableAddresses(string address)
        => Assert.True(PublicAddress.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("https://localhost/x")]
    [InlineData("https://LOCALHOST:8081/")]
    [InlineData("https://api.localhost/")]
    [InlineData("https://printer.local/")]
    [InlineData("https://metadata.internal/")]
    [InlineData("https://localhost./")]
    [InlineData("https://printer.local./")]
    [InlineData("https://metadata.internal./")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://[::1]/")]
    [InlineData("https://10.1.2.3:8080/")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://198.18.0.1/")]
    [InlineData("https://[64:ff9b::a00:1]/")]
    [InlineData("https://[2002:a00:1::]/")]
    public void IsPublicHost_RejectsLocalhostAndPrivateLiterals(string url)
        => Assert.False(PublicAddress.IsPublicHost(new Uri(url)));

    [Theory]
    [InlineData("https://bookings.example.com/")]
    [InlineData("https://bookings.example.com./")]
    [InlineData("https://8.8.8.8/")]
    [InlineData("https://[2606:4700::1111]/")]
    [InlineData("https://[64:ff9b::808:808]/")]
    public void IsPublicHost_AcceptsPublicNamesAndLiterals(string url)
        => Assert.True(PublicAddress.IsPublicHost(new Uri(url)));
}
