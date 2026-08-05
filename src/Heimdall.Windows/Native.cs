using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Heimdall.Windows;

/// <summary>
/// The Win32 surface this assembly needs, in one place.
///
/// **`LibraryImport`, never `DllImport`.** WINDOWS.md §6 is explicit: the
/// project publishes with `PublishAot=true` and turns the AOT analyser on for
/// every project, with warnings as errors. Source-generated P/Invoke is
/// AOT-clean; the reflection-based marshaller behind `DllImport` is not, and
/// would fail at runtime rather than at build time.
///
/// Everything here is deliberately small. The two things that actually needed
/// native calls are the Recycle Bin, which has no BCL API at all, and the
/// registry, which has one — but only for the `net10.0-windows` TFM this project
/// does not use, and adopting that TFM would cost the free Linux compile-check
/// (§9). Two `RegGetValueW` calls are cheaper than that trade.
/// </summary>
internal static partial class Native
{
    // ---- Registry ----------------------------------------------------------

    internal static readonly nint HKEY_CURRENT_USER = unchecked((nint)(long)0x80000001);

    internal const uint RRF_RT_REG_DWORD = 0x00000010;
    internal const uint KEY_READ = 0x00020019;
    internal const uint REG_NOTIFY_CHANGE_LAST_SET = 0x00000004;
    internal const int ERROR_SUCCESS = 0;

    [LibraryImport("advapi32.dll", EntryPoint = "RegGetValueW",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegGetValue(
        nint hkey, string subKey, string value, uint flags,
        nint type, out uint data, ref uint dataSize);

    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegOpenKeyEx(
        nint hkey, string subKey, uint options, uint desired, out nint result);

    /// <summary>
    /// Called with <c>asynchronous: false</c>, which blocks the calling thread
    /// until the key changes. That is why the theme provider gives it a thread
    /// of its own — and a background one, so a blocked wait cannot hold the
    /// process open at exit.
    /// </summary>
    [LibraryImport("advapi32.dll", EntryPoint = "RegNotifyChangeKeyValue")]
    internal static partial int RegNotifyChangeKeyValue(
        nint hkey,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        uint notifyFilter,
        nint eventHandle,
        [MarshalAs(UnmanagedType.Bool)] bool asynchronous);

    [LibraryImport("advapi32.dll", EntryPoint = "RegCloseKey")]
    internal static partial int RegCloseKey(nint hkey);

    /// <summary>A DWORD from HKCU, or null if it is not there.</summary>
    internal static uint? ReadDword(string subKey, string value)
    {
        uint data = 0;
        var size = (uint)sizeof(uint);

        var status = RegGetValue(
            HKEY_CURRENT_USER, subKey, value, RRF_RT_REG_DWORD, 0, out data, ref size);

        return status == ERROR_SUCCESS ? data : null;
    }

    // ---- Shell file operations ---------------------------------------------

    internal const uint FO_DELETE = 0x0003;

    /// <summary>Recycle rather than destroy. Without it this is a permanent delete.</summary>
    internal const ushort FOF_ALLOWUNDO = 0x0040;
    internal const ushort FOF_SILENT = 0x0004;
    internal const ushort FOF_NOCONFIRMATION = 0x0010;
    internal const ushort FOF_NOERRORUI = 0x0400;

    /// <summary>
    /// Warn before something is destroyed rather than recycled. **Partially
    /// overrides FOF_NOCONFIRMATION, which is the entire point** — see
    /// WindowsFileOperations.Trash.
    /// </summary>
    internal const ushort FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>
    /// Default packing, which is correct on x64 and ARM64. The
    /// <c>#include &lt;pshpack1.h&gt;</c> around this structure in shellapi.h
    /// applies to 32-bit builds only; this application publishes 64-bit.
    /// The string fields are raw pointers so the structure stays blittable and
    /// <c>LibraryImport</c> will accept it — see <see cref="DoubleNullTerminated"/>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SHFILEOPSTRUCTW
    {
        internal nint hwnd;
        internal uint wFunc;
        internal nint pFrom;
        internal nint pTo;
        internal ushort fFlags;
        internal int fAnyOperationsAborted;
        internal nint hNameMappings;
        internal nint lpszProgressTitle;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    internal static partial int SHFileOperation(ref SHFILEOPSTRUCTW operation);

    /// <summary>
    /// The list format SHFileOperation wants: entries separated by NUL and the
    /// whole thing terminated by a second NUL.
    ///
    /// <see cref="Marshal.StringToHGlobalUni"/> appends one NUL of its own, so
    /// the string handed to it ends with a single explicit NUL and the pair
    /// comes out right. Getting this wrong reads past the buffer.
    /// </summary>
    internal static nint DoubleNullTerminated(IReadOnlyList<string> paths)
        => Marshal.StringToHGlobalUni(string.Join('\0', paths) + '\0');

    // ---- Junctions ---------------------------------------------------------

    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint FILE_SHARE_ALL = 0x00000007;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    internal const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    internal const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    internal const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;

    internal static readonly nint INVALID_HANDLE_VALUE = -1;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CreateFile(
        string fileName, uint access, uint share, nint security,
        uint creation, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(
        nint device, uint code, nint inBuffer, uint inSize,
        nint outBuffer, uint outSize, out uint returned, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    /// <summary>
    /// Points an existing, empty directory at <paramref name="target"/>, making
    /// it a junction.
    ///
    /// **This exists because the BCL only offers the kind of link that needs a
    /// privilege.** Directory.CreateSymbolicLink requires
    /// SeCreateSymbolicLinkPrivilege — Developer Mode, or elevation — and
    /// throws "a required privilege is not held by the client" without it. A
    /// junction needs nothing but write access to the directory, which is why
    /// `mklink /J` works for an ordinary user and why Windows uses junctions
    /// for its own compatibility links. Copying a folder that contains one is
    /// not an exotic case (node_modules, package caches, the legacy profile
    /// links), so it cannot be allowed to fail on a machine in its default
    /// configuration.
    ///
    /// The structure is REPARSE_DATA_BUFFER in its mount-point form: an
    /// eight-byte header, four USHORT offsets and lengths, then both names back
    /// to back. ReparseDataLength counts everything after the header, and the
    /// two NUL terminators sit in the buffer without being counted in either
    /// length — the usual way to get this wrong.
    /// </summary>
    internal static void CreateJunction(string path, string target)
    {
        var full = Path.GetFullPath(target);

        // A drive root keeps its separator; anything else loses a trailing one.
        var print = full.Length > 3 ? full.TrimEnd(Path.DirectorySeparatorChar) : full;

        // The object-manager name the reparse point actually stores.
        var substitute = @"\??\" + print;

        var names = (substitute.Length + 1 + print.Length + 1) * 2;
        var buffer = new byte[16 + names];

        BitConverter.TryWriteBytes(buffer.AsSpan(0), IO_REPARSE_TAG_MOUNT_POINT);
        BitConverter.TryWriteBytes(buffer.AsSpan(4), (ushort)(8 + names));
        // Bytes 6..8 are Reserved, and are already zero.
        BitConverter.TryWriteBytes(buffer.AsSpan(8), (ushort)0);
        BitConverter.TryWriteBytes(buffer.AsSpan(10), (ushort)(substitute.Length * 2));
        BitConverter.TryWriteBytes(buffer.AsSpan(12), (ushort)((substitute.Length + 1) * 2));
        BitConverter.TryWriteBytes(buffer.AsSpan(14), (ushort)(print.Length * 2));

        var text = MemoryMarshal.Cast<byte, char>(buffer.AsSpan(16));
        substitute.CopyTo(text);
        print.CopyTo(text[(substitute.Length + 1)..]);

        // BACKUP_SEMANTICS to open a directory at all; OPEN_REPARSE_POINT so the
        // handle is the directory itself rather than whatever it may point at.
        var handle = CreateFile(
            path, GENERIC_WRITE, FILE_SHARE_ALL, 0, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, 0);

        if (handle == INVALID_HANDLE_VALUE)
            throw new IOException(
                $"Could not open '{path}' to make it a junction.",
                Marshal.GetHRForLastWin32Error());

        try
        {
            unsafe
            {
                fixed (byte* data = buffer)
                {
                    if (!DeviceIoControl(
                            handle, FSCTL_SET_REPARSE_POINT,
                            (nint)data, (uint)buffer.Length, 0, 0, out _, 0))
                        throw new IOException(
                            $"Could not point '{path}' at '{print}'.",
                            Marshal.GetHRForLastWin32Error());
                }
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // ---- The desktop's UI font ---------------------------------------------

    internal const uint SPI_GETICONTITLELOGFONT = 0x001F;

    /// <summary>
    /// <c>ushort</c> rather than <c>char</c>, and that is forced rather than
    /// stylistic. <c>char</c> is not blittable — the runtime marshaller has a
    /// conversion for it — so a structure containing one cannot cross a
    /// <c>LibraryImport</c> boundary without disabling runtime marshalling for
    /// the whole assembly (SYSLIB1051). UTF-16 code units are what the field
    /// holds anyway; <see cref="MemoryMarshal.Cast{TFrom,TTo}(ReadOnlySpan{TFrom})"/>
    /// reads them back as text for free.
    /// </summary>
    [InlineArray(32)]
    internal struct FaceName
    {
        private ushort _element0;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LOGFONTW
    {
        internal int lfHeight;
        internal int lfWidth;
        internal int lfEscapement;
        internal int lfOrientation;
        internal int lfWeight;
        internal byte lfItalic;
        internal byte lfUnderline;
        internal byte lfStrikeOut;
        internal byte lfCharSet;
        internal byte lfOutPrecision;
        internal byte lfClipPrecision;
        internal byte lfQuality;
        internal byte lfPitchAndFamily;
        internal FaceName lfFaceName;
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SystemParametersInfo(
        uint action, uint param, ref LOGFONTW data, uint winIni);
}
