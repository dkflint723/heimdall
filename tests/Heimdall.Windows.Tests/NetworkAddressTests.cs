using System.Runtime.Versioning;
using Heimdall.Core.Tests;
using Xunit;

namespace Heimdall.Windows.Tests;

/// <summary>
/// The address translation between what a person types, what DNS-SD advertises
/// and what the Windows redirector will accept.
///
/// **Three vocabularies meet here and none of them is Windows'.** The connect
/// prompt takes whatever the user types, which on Windows is usually
/// `\\server\share` but may well be the `smb://` form they know from Linux or
/// saw in the README. Discovery produces neither: DNS-SD advertises a host and
/// a port, so a found share arrives as a URI. WNetAddConnection2 accepts only a
/// UNC path, or an http:// URL when the WebClient service is running. Getting
/// this wrong is silent — a share that simply refuses to connect, with an error
/// from the redirector about a name it was never given in a form it understood.
/// </summary>
[SupportedOSPlatform("windows")]
public class NetworkAddressTests
{
    [WindowsTheory]
    // Already a UNC path: unchanged.
    [InlineData(@"\\nas\media", @"\\nas\media")]
    [InlineData(@"\\nas", @"\\nas")]
    // The Linux spelling, which the README documents and discovery may produce.
    [InlineData("smb://nas/media", @"\\nas\media")]
    [InlineData("cifs://nas/media", @"\\nas\media")]
    // Bare and slash-prefixed forms, both of which people type.
    [InlineData("//nas/media", @"\\nas\media")]
    [InlineData("nas/media", @"\\nas\media")]
    [InlineData(@"nas\media", @"\\nas\media")]
    // A trailing separator names the same share.
    [InlineData("smb://nas/media/", @"\\nas\media")]
    [InlineData(@"\\nas\media\", @"\\nas\media")]
    // Deeper than a share root.
    [InlineData("smb://nas/media/photos", @"\\nas\media\photos")]
    public void An_address_becomes_a_UNC_path(string typed, string expected)
        => Assert.Equal(expected, WindowsRemoteMounts.ToUnc(typed));

    /// <summary>
    /// Left alone rather than converted: the redirector takes an http:// URL
    /// directly and hands it to WebClient, which is how WebDAV is mounted on
    /// Windows. Turning it into a UNC path would break the one protocol here
    /// that is not SMB.
    /// </summary>
    [WindowsTheory]
    [InlineData("http://box:3923/")]
    [InlineData("https://box/dav")]
    [InlineData("dav://box/share")]
    public void A_web_address_is_passed_through(string typed)
        => Assert.Equal(typed, WindowsRemoteMounts.ToUnc(typed));

    /// <summary>
    /// Named rather than swallowed. "Could not connect" would send someone
    /// looking for a network fault that is not there — Windows has no SFTP
    /// redirector, and no amount of retrying will produce one.
    /// </summary>
    [WindowsFact]
    public void A_protocol_Windows_cannot_mount_says_so()
    {
        var error = Assert.Throws<NotSupportedException>(
            () => WindowsRemoteMounts.ToUnc("sftp://box/home"));

        Assert.Contains("sftp", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [WindowsTheory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_address_is_refused(string typed)
        => Assert.Throws<ArgumentException>(() => WindowsRemoteMounts.ToUnc(typed));

    /// <summary>Matching the phrasing the gvfs reader produces on Linux.</summary>
    [WindowsTheory]
    [InlineData(@"\\nas\media", "media on nas")]
    [InlineData(@"\\nas\media\photos", "photos on nas")]
    [InlineData(@"\\nas", "nas")]
    public void A_share_is_labelled_by_name_and_server(string unc, string expected)
        => Assert.Equal(expected, WindowsRemoteMounts.LabelFor(unc));

    /// <summary>
    /// The WebDAV forms, all three taken from real connections. `host@8080` is
    /// how the redirector carries a port and reads as an email address;
    /// `DavWWWRoot` is its own name for "the root of this server" and means
    /// nothing to a person.
    /// </summary>
    [WindowsTheory]
    // A phone serving http://192.168.4.39:8080/files.
    [InlineData(@"\\192.168.4.39@8080\files", "files on 192.168.4.39:8080")]
    // The root of a WebDAV server: the share name is the redirector's, not the user's.
    [InlineData(@"\\127.0.0.1@3939\DavWWWRoot", "127.0.0.1:3939")]
    [InlineData(@"\\box@SSL\DavWWWRoot", "box")]
    // https on a non-default port keeps the port and loses the @SSL noise.
    [InlineData(@"\\box@SSL\dav", "dav on box")]
    public void A_WebDAV_share_is_labelled_in_words_a_person_uses(string unc, string expected)
        => Assert.Equal(expected, WindowsRemoteMounts.LabelFor(unc));

    /// <summary>
    /// `\\host@SSL\path` and `\\host@8080\path` are how the redirector spells an
    /// HTTP endpoint, and are the only thing here that is not SMB.
    /// </summary>
    [WindowsTheory]
    [InlineData(@"\\nas\media", "smb")]
    [InlineData(@"\\box@SSL\dav", "dav")]
    [InlineData(@"\\box@8080\dav", "dav")]
    public void The_protocol_is_read_from_the_host(string unc, string expected)
        => Assert.Equal(expected, WindowsRemoteMounts.Protocol(unc));

    /// <summary>
    /// What a discovered service becomes when it is double-clicked. SMB has to
    /// come back as a UNC path — the redirector will not take smb:// — and
    /// everything else stays a URL.
    /// </summary>
    [WindowsTheory]
    [InlineData("_smb._tcp", @"\\192.168.4.10")]
    [InlineData("_webdav._tcp", "http://192.168.4.10:3923")]
    [InlineData("_webdavs._tcp", "https://192.168.4.10:3923")]
    [InlineData("_http._tcp", "http://192.168.4.10:3923")]
    [InlineData("_ftp._tcp", "ftp://192.168.4.10:3923")]
    public void A_discovered_service_becomes_something_mountable(string type, string expected)
        => Assert.Equal(expected, WindowsNetworkDiscovery.MountUri(type, "192.168.4.10", 3923));

    /// <summary>
    /// The round trip that matters: what discovery produces for an SMB share
    /// has to be something the mounter accepts. These two are written in
    /// different files and only meet at runtime, in the one place where a
    /// mismatch would be a share that is found and cannot be opened.
    /// </summary>
    [WindowsFact]
    public void A_discovered_SMB_share_can_be_handed_straight_to_the_mounter()
    {
        var advertised = WindowsNetworkDiscovery.MountUri("_smb._tcp", "nas.local", 445);

        Assert.Equal(@"\\nas.local", WindowsRemoteMounts.ToUnc(advertised));
    }

    [WindowsFact]
    public void A_discovered_WebDAV_share_survives_the_mounter_unchanged()
    {
        var advertised = WindowsNetworkDiscovery.MountUri("_webdav._tcp", "box.local", 3923);

        Assert.Equal(advertised, WindowsRemoteMounts.ToUnc(advertised));
    }
}
