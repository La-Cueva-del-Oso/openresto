using System.Net;
using System.Net.Sockets;

namespace OpenRestoApi.Core.Application.Utilities;

/// <summary>
/// Whether an address is one the outside world could reach. Everything the server fetches on an
/// admin's or a guest's say-so — the deployment's own <c>/.well-known/</c> documents, a browser's
/// Web Push endpoint — has to clear this, or a URL that resolves to the Docker network, a cloud
/// metadata service or the host's own loopback becomes a request the server makes on the caller's
/// behalf. A pure classifier: DNS is the caller's business.
/// </summary>
/// <seealso>PublicAddressTests.IsPublic_RejectsLoopbackPrivateAndLinkLocalRanges</seealso>
/// <seealso>PublicAddressTests.IsPublic_AcceptsRoutableAddresses</seealso>
/// <seealso>PublicAddressTests.IsPublicHost_RejectsLocalhostAndPrivateLiterals</seealso>
public static class PublicAddress
{
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IsLocalUseNat64(address))
            {
                return false;
            }

            address = EmbeddedIPv4(address) ?? address;
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !address.IsIPv6LinkLocal
                && !address.IsIPv6SiteLocal
                && !address.IsIPv6UniqueLocal
                && !address.IsIPv6Multicast;
        }

        byte[] bytes = address.GetAddressBytes();
        return bytes[0] switch
        {
            10 => false,                                   // 10.0.0.0/8
            127 => false,                                  // 127.0.0.0/8
            169 when bytes[1] == 254 => false,             // 169.254.0.0/16 (link-local, cloud metadata)
            172 when bytes[1] is >= 16 and <= 31 => false, // 172.16.0.0/12 (Docker's default bridges)
            192 when bytes[1] == 168 => false,             // 192.168.0.0/16
            100 when bytes[1] is >= 64 and <= 127 => false, // 100.64.0.0/10 (carrier NAT)
            198 when bytes[1] is 18 or 19 => false,         // 198.18.0.0/15 (benchmarking)
            0 => false,
            >= 224 => false,                               // multicast and reserved
            _ => true,
        };
    }

    // 64:ff9b::/96 (NAT64) and 2002::/16 (6to4) carry an IPv4 address that a gateway or relay
    // delivers to, so the IPv4 rules decide, exactly as for an IPv4-mapped address.
    private static IPAddress? EmbeddedIPv4(IPAddress v6)
    {
        byte[] b = v6.GetAddressBytes();
        bool nat64 = b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4..12].All(x => x == 0);
        bool sixToFour = b[0] == 0x20 && b[1] == 0x02;
        if (nat64)
        {
            return new IPAddress(b[12..16]);
        }

        return sixToFour ? new IPAddress(b[2..6]) : null;
    }

    // 64:ff9b:1::/48 (RFC 8215) is NAT64 space reserved for local use, so never the public internet.
    private static bool IsLocalUseNat64(IPAddress v6)
    {
        byte[] b = v6.GetAddressBytes();
        return b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4] == 0x00 && b[5] == 0x01;
    }

    /// <summary>
    /// The literal-only half: <c>localhost</c> and an IP written into the URL. A host name that
    /// merely resolves to a private address is only knowable after a lookup, which is the
    /// fetching side's job.
    /// </summary>
    public static bool IsPublicHost(Uri url)
    {
        if (url.HostNameType == UriHostNameType.IPv4 || url.HostNameType == UriHostNameType.IPv6)
        {
            return IPAddress.TryParse(url.Host.Trim('[', ']'), out IPAddress? literal) && IsPublic(literal);
        }

        string host = url.Host.TrimEnd('.'); // "localhost." is the same name with the root label spelled out
        return !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);
    }
}
