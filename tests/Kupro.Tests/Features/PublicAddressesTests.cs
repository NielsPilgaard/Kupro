using System.Net;
using Kupro.Api.Features.Imports;

namespace Kupro.Tests.Features;

/// <summary>Server-side connectors stay on the public internet (task 025, SSRF).</summary>
public sealed class PublicAddressesTests
{
    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.5", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("13.107.42.18", true)]
    [InlineData("2620:1ec:21::18", true)]
    public void Only_public_addresses_pass(string address, bool expected) =>
        PublicAddresses.IsPublic(IPAddress.Parse(address)).ShouldBe(expected);

    [Fact]
    public async Task A_connection_to_a_private_address_is_refused_before_it_opens()
    {
        using var http = new HttpClient(new SocketsHttpHandler { ConnectCallback = PublicAddresses.ConnectAsync });

        var refused = await Should.ThrowAsync<HttpRequestException>(() => http.GetAsync("http://127.0.0.1:9/", TestContext.Current.CancellationToken));

        refused.ToString().ShouldContain("not on the public internet");
    }
}
