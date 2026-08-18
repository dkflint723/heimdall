using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Taking files out of a drop that has none on disk — which is what dragging
/// out of 7-Zip or Explorer's zip view actually is.
///
/// The COM conversation itself cannot be tested without a drag: there is no way
/// to conjure a shell data object here. What can be tested is everything around
/// it, and that is where the mistakes live — the record layout, the names, and
/// the guard on where those names may be written.
/// </summary>
[SupportedOSPlatform("windows")]
public class VirtualFileDropTests
{
    /// <summary>
    /// Builds a FILEGROUPDESCRIPTORW by hand: a UINT count, then one 592-byte
    /// record per file with the name at offset 72 in a fixed 260-character
    /// buffer.
    /// </summary>
    private static IntPtr Descriptor(bool wide, params string[] names)
    {
        var size = wide ? 592 : 332;
        var bytes = new byte[4 + (names.Length * size)];

        BitConverter.GetBytes(names.Length).CopyTo(bytes, 0);

        for (var i = 0; i < names.Length; i++)
        {
            var at = 4 + (i * size) + 72;

            var encoded = wide
                ? System.Text.Encoding.Unicode.GetBytes(names[i])
                : System.Text.Encoding.ASCII.GetBytes(names[i]);

            encoded.CopyTo(bytes, at);
        }

        var block = Marshal.AllocHGlobal(bytes.Length);

        Marshal.Copy(bytes, 0, block, bytes.Length);

        return block;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_names_are_read_out_of_the_descriptor(bool wide)
    {
        var block = Descriptor(wide, "one.txt", "two.txt", "three.txt");

        try
        {
            Assert.Equal(["one.txt", "two.txt", "three.txt"], VirtualFileDrop.Parse(block, wide));
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    /// <summary>
    /// **A name is padded with nulls, not ended by its record.** Reading the
    /// whole 260-character buffer would hand back a name with a tail of
    /// nothing attached, which no filesystem will accept.
    /// </summary>
    [Fact]
    public void A_name_stops_at_its_terminator()
    {
        var block = Descriptor(true, "notes.txt");

        try
        {
            var name = Assert.Single(VirtualFileDrop.Parse(block, wide: true));

            Assert.Equal("notes.txt", name);
            Assert.DoesNotContain('\0', name);
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    /// <summary>An archive holding folders describes paths, not bare names.</summary>
    [Fact]
    public void A_path_inside_the_archive_survives()
    {
        var block = Descriptor(true, @"inner\deep\note.txt");

        try
        {
            Assert.Equal(@"inner\deep\note.txt", Assert.Single(VirtualFileDrop.Parse(block, true)));
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    [Fact]
    public void An_empty_descriptor_yields_nothing()
    {
        var block = Descriptor(true);

        try
        {
            Assert.Empty(VirtualFileDrop.Parse(block, wide: true));
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    // ---- where those names may be written ----------------------------------

    /// <summary>
    /// **The name comes from whatever was dragged.** An archive can carry an
    /// entry calling itself ..\..\Windows\System32\something, and writing it
    /// where it asks would be the oldest bug in unpacking — the same rule the
    /// theme unpacker applies, for the same reason.
    /// </summary>
    [Theory]
    [InlineData(@"..\..\escape.txt")]
    [InlineData(@"C:\Windows\System32\evil.dll")]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_name_that_climbs_out_is_refused(string name)
    {
        Assert.Null(VirtualFileDrop.Contained(@"C:\temp\drop", name));
    }

    [Fact]
    public void An_ordinary_name_is_allowed()
    {
        Assert.Equal(
            @"C:\temp\drop\notes.txt",
            VirtualFileDrop.Contained(@"C:\temp\drop", "notes.txt"));

        Assert.Equal(
            @"C:\temp\drop\inner\note.txt",
            VirtualFileDrop.Contained(@"C:\temp\drop", @"inner\note.txt"));
    }

    // ---- what gets handed back ---------------------------------------------

    /// <summary>
    /// **Only the roots.** A folder dragged out of an archive describes every
    /// file inside it; handing all of them back would copy the tree flat into
    /// the destination instead of copying the folder.
    /// </summary>
    [Fact]
    public void A_folder_comes_back_as_one_thing()
    {
        var roots = VirtualFileDrop.Roots(@"C:\temp\drop",
        [
            @"C:\temp\drop\inner\a.txt",
            @"C:\temp\drop\inner\deep\b.txt",
            @"C:\temp\drop\loose.txt",
        ]);

        Assert.Equal([@"C:\temp\drop\inner", @"C:\temp\drop\loose.txt"], roots);
    }

    /// <summary>Anything that is not Avalonia's wrapper yields null rather than
    /// throwing, which is what keeps a drop from failing on a strange source.</summary>
    [Fact]
    public void Something_that_is_not_the_wrapper_yields_nothing()
    {
        Assert.Null(VirtualFileDrop.Native(new object()));
        Assert.Null(VirtualFileDrop.Native("not a data transfer"));
    }
}
