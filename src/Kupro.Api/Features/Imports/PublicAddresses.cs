using System.Net;
using System.Net.Sockets;

namespace Kupro.Api.Features.Imports;

/// <summary>
/// Keeps server-side connectors on the public internet (task 025, SSRF): an import's base URL is the owner's to choose,
/// so without this it could point the API at Postgres, Keycloak or the cloud's metadata service. Checked when each
/// connection opens, after DNS, so a name that later resolves somewhere private is refused too. A self-hosted Kupro
/// that imports from an internal tracker turns it off with <c>Imports:AllowPrivateAddresses</c>; the CLI's
/// <c>--local</c> mode never goes through it.
/// </summary>
public static class PublicAddresses
{
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        var allowed = addresses.Where(IsPublic).ToList();
        if (allowed.Count == 0 || allowed.Count != addresses.Length)
        {
            throw new HttpRequestException($"{host} is not on the public internet, so Kupro won't connect to it from the server. Use the CLI's --local mode.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync([.. allowed], context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>False for loopback, private, link-local, carrier-grade NAT, unique-local, multicast and unspecified addresses.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal);
        }

        var b = address.GetAddressBytes();
        return b[0] switch
        {
            0 or 10 or 127 => false,
            100 when b[1] is >= 64 and <= 127 => false, // carrier-grade NAT
            169 when b[1] == 254 => false,              // link-local, cloud metadata
            172 when b[1] is >= 16 and <= 31 => false,
            192 when b[1] == 168 => false,
            192 when b[1] == 0 && b[2] == 0 => false,
            198 when b[1] is 18 or 19 => false,         // benchmarking
            >= 224 => false,                            // multicast and reserved
            _ => true,
        };
    }
}
