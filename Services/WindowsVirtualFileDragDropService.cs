using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace ChiXueSsh.Services;

public sealed record VirtualDragFile(
    string FileName,
    long Size,
    DateTime LastModified,
    Func<Stream> OpenReadStream);

public static class WindowsVirtualFileDragDropService
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    [SupportedOSPlatform("windows")]
    public static int DoDragDrop(VirtualDragFile file)
    {
        return DoDragDrop([file]);
    }

    [SupportedOSPlatform("windows")]
    public static int DoDragDrop(IReadOnlyList<VirtualDragFile> files)
    {
        if (!OperatingSystem.IsWindows())
            return 0;

        if (files.Count == 0)
            return DropEffectNone;

        var dataObject = new VirtualFileDataObject(files);
        var dropSource = new DropSource();
        var hr = OleDoDragDrop(dataObject, dropSource, DropEffectCopy, out var effect);
        if (hr != 0 && hr != DragDropSCancel && hr != DragDropSDrop)
            Marshal.ThrowExceptionForHR(hr);

        return effect;
    }

    private const int DropEffectNone = 0;
    private const int DropEffectCopy = 1;
    private const int MkLButton = 0x0001;
    private const int SOk = 0;
    private const int DragDropSDrop = 0x00040100;
    private const int DragDropSCancel = 0x00040101;
    private const int DragDropSUseDefaultCursors = 0x00040102;

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int DoDragDrop(
        IDataObject dataObject,
        IDropSource dropSource,
        int okEffects,
        out int effect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClipboardFormat(string format);

    private static int OleDoDragDrop(IDataObject dataObject, IDropSource dropSource, int okEffects, out int effect)
    {
        return DoDragDrop(dataObject, dropSource, okEffects, out effect);
    }

    [ComImport]
    [Guid("00000121-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropSource
    {
        [PreserveSig]
        int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escapePressed, int keyState);

        [PreserveSig]
        int GiveFeedback(int effect);
    }

    private sealed class DropSource : IDropSource
    {
        public int QueryContinueDrag(bool escapePressed, int keyState)
        {
            if (escapePressed)
                return DragDropSCancel;

            return (keyState & MkLButton) == 0 ? DragDropSDrop : SOk;
        }

        public int GiveFeedback(int effect)
        {
            return DragDropSUseDefaultCursors;
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class VirtualFileDataObject : IDataObject
    {
        private static readonly short FileGroupDescriptorW = unchecked((short)RegisterClipboardFormat("FileGroupDescriptorW"));
        private static readonly short FileContents = unchecked((short)RegisterClipboardFormat("FileContents"));
        private readonly IReadOnlyList<VirtualDragFile> _files;
        private readonly FORMATETC[] _formats;

        public VirtualFileDataObject(IReadOnlyList<VirtualDragFile> files)
        {
            _files = files;
            _formats =
            [
                new FORMATETC
                {
                    cfFormat = FileGroupDescriptorW,
                    dwAspect = DVASPECT.DVASPECT_CONTENT,
                    lindex = -1,
                    tymed = TYMED.TYMED_HGLOBAL
                },
                new FORMATETC
                {
                    cfFormat = FileContents,
                    dwAspect = DVASPECT.DVASPECT_CONTENT,
                    lindex = -1,
                    tymed = TYMED.TYMED_ISTREAM
                }
            ];
        }

        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            medium = default;

            if (format.cfFormat == FileGroupDescriptorW && Allows(format.tymed, TYMED.TYMED_HGLOBAL))
            {
                medium.tymed = TYMED.TYMED_HGLOBAL;
                medium.unionmember = CreateFileGroupDescriptor(_files);
                medium.pUnkForRelease = null;
                return;
            }

            if (format.cfFormat == FileContents && Allows(format.tymed, TYMED.TYMED_ISTREAM))
            {
                var index = format.lindex < 0 ? 0 : format.lindex;
                if (index >= _files.Count)
                    ThrowHResult(DvEFormatEtc);

                var file = _files[index];
                var stream = new RemoteComReadStream(file.OpenReadStream(), file.Size);
                medium.tymed = TYMED.TYMED_ISTREAM;
                medium.unionmember = Marshal.GetComInterfaceForObject(stream, typeof(IStream));
                medium.pUnkForRelease = null;
                return;
            }

            ThrowHResult(DvEFormatEtc);
        }

        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium)
        {
            ThrowHResult(DvEFormatEtc);
        }

        public int QueryGetData(ref FORMATETC format)
        {
            if (format.cfFormat == FileGroupDescriptorW && Allows(format.tymed, TYMED.TYMED_HGLOBAL))
                return SOk;

            if (format.cfFormat == FileContents && Allows(format.tymed, TYMED.TYMED_ISTREAM))
                return SOk;

            return DvEFormatEtc;
        }

        public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
        {
            formatOut = formatIn;
            formatOut.ptd = IntPtr.Zero;
            return DataSUnableToRender;
        }

        public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
        {
            ThrowHResult(OleENotSupported);
        }

        public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
        {
            if (direction == DATADIR.DATADIR_GET)
                return new FormatEtcEnumerator(_formats);

            ThrowHResult(OleENotSupported);
            return null!;
        }

        public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection)
        {
            connection = 0;
            return OleEAdviseNotSupported;
        }

        public void DUnadvise(int connection)
        {
            ThrowHResult(OleEAdviseNotSupported);
        }

        public int EnumDAdvise(out IEnumSTATDATA enumAdvise)
        {
            enumAdvise = null!;
            return OleEAdviseNotSupported;
        }

        private static bool Allows(TYMED actual, TYMED expected)
        {
            return (actual & expected) == expected;
        }

        private static IntPtr CreateFileGroupDescriptor(IReadOnlyList<VirtualDragFile> files)
        {
            var descriptorSize = Marshal.SizeOf<FileDescriptor>();
            var totalSize = sizeof(uint) + descriptorSize * files.Count;
            var handle = Marshal.AllocHGlobal(totalSize);
            Marshal.WriteInt32(handle, files.Count);

            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                var descriptor = new FileDescriptor
                {
                    dwFlags = FileDescriptorFlags.FdAttributes | FileDescriptorFlags.FdFileSize | FileDescriptorFlags.FdWriteTime,
                    dwFileAttributes = FileAttributeNormal,
                    nFileSizeHigh = (uint)((ulong)file.Size >> 32),
                    nFileSizeLow = (uint)((ulong)file.Size & 0xffffffff),
                    cFileName = SanitizeShellFileName(file.FileName)
                };

                var fileTime = file.LastModified.ToFileTimeUtc();
                descriptor.ftLastWriteTime.dwLowDateTime = unchecked((int)(fileTime & 0xffffffff));
                descriptor.ftLastWriteTime.dwHighDateTime = unchecked((int)(fileTime >> 32));
                Marshal.StructureToPtr(descriptor, IntPtr.Add(handle, sizeof(uint) + descriptorSize * index), false);
            }

            return handle;
        }

        private static string SanitizeShellFileName(string name)
        {
            foreach (var invalidChar in Path.GetInvalidFileNameChars())
            {
                if (invalidChar is '\\' or '/')
                    continue;

                name = name.Replace(invalidChar, '_');
            }

            return string.IsNullOrWhiteSpace(name) ? "download" : name;
        }
    }

    private sealed class RemoteComReadStream : IStream
    {
        private readonly Stream _stream;
        private readonly long _size;

        public RemoteComReadStream(Stream stream, long size)
        {
            _stream = stream;
            _size = size;
        }

        public void Read(byte[] buffer, int count, IntPtr bytesRead)
        {
            var read = _stream.Read(buffer, 0, count);
            if (bytesRead != IntPtr.Zero)
                Marshal.WriteInt32(bytesRead, read);
        }

        public void Write(byte[] buffer, int count, IntPtr bytesWritten)
        {
            ThrowHResult(StgEAccessDenied);
        }

        public void Seek(long offset, int origin, IntPtr newPosition)
        {
            var position = _stream.Seek(offset, (SeekOrigin)origin);
            if (newPosition != IntPtr.Zero)
                Marshal.WriteInt64(newPosition, position);
        }

        public void SetSize(long value)
        {
            ThrowHResult(StgEAccessDenied);
        }

        public void CopyTo(IStream destination, long count, IntPtr bytesRead, IntPtr bytesWritten)
        {
            var buffer = new byte[64 * 1024];
            long totalRead = 0;
            long totalWritten = 0;

            while (totalRead < count)
            {
                var toRead = (int)Math.Min(buffer.Length, count - totalRead);
                var read = _stream.Read(buffer, 0, toRead);
                if (read <= 0)
                    break;

                totalRead += read;
                destination.Write(buffer, read, IntPtr.Zero);
                totalWritten += read;
            }

            if (bytesRead != IntPtr.Zero)
                Marshal.WriteInt64(bytesRead, totalRead);
            if (bytesWritten != IntPtr.Zero)
                Marshal.WriteInt64(bytesWritten, totalWritten);
        }

        public void Commit(int flags)
        {
        }

        public void Revert()
        {
            ThrowHResult(StgEReverted);
        }

        public void LockRegion(long offset, long count, int lockType)
        {
            ThrowHResult(StgEInvalidFunction);
        }

        public void UnlockRegion(long offset, long count, int lockType)
        {
            ThrowHResult(StgEInvalidFunction);
        }

        public void Stat(out STATSTG stat, int flags)
        {
            stat = new STATSTG
            {
                type = StgTypeStream,
                cbSize = _size
            };
        }

        public void Clone(out IStream clone)
        {
            clone = null!;
            ThrowHResult(StgENotImplemented);
        }
    }

    private sealed class FormatEtcEnumerator : IEnumFORMATETC
    {
        private readonly FORMATETC[] _formats;
        private int _index;

        public FormatEtcEnumerator(FORMATETC[] formats)
        {
            _formats = formats;
        }

        public int Next(int count, FORMATETC[] formats, int[]? fetched)
        {
            var copied = 0;
            while (copied < count && _index < _formats.Length)
            {
                formats[copied++] = _formats[_index++];
            }

            if (fetched is { Length: > 0 })
                fetched[0] = copied;

            return copied == count ? SOk : SFalse;
        }

        public int Skip(int count)
        {
            _index = Math.Min(_index + count, _formats.Length);
            return _index < _formats.Length ? SOk : SFalse;
        }

        public int Reset()
        {
            _index = 0;
            return SOk;
        }

        public void Clone(out IEnumFORMATETC newEnum)
        {
            newEnum = new FormatEtcEnumerator(_formats) { _index = _index };
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileDescriptor
    {
        public FileDescriptorFlags dwFlags;
        public Guid clsid;
        public SizeL sizel;
        public PointL pointl;
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizeL
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointL
    {
        public int x;
        public int y;
    }

    [Flags]
    private enum FileDescriptorFlags : uint
    {
        FdAttributes = 0x00000004,
        FdWriteTime = 0x00000020,
        FdFileSize = 0x00000040
    }

    private const uint FileAttributeNormal = 0x00000080;
    private const int StgTypeStream = 2;
    private const int SFalse = 1;
    private const int DataSUnableToRender = 0x000401A0;
    private const int OleENotSupported = unchecked((int)0x80040000);
    private const int OleEAdviseNotSupported = unchecked((int)0x80040003);
    private const int DvEFormatEtc = unchecked((int)0x80040064);
    private const int StgEInvalidFunction = unchecked((int)0x80030001);
    private const int StgEAccessDenied = unchecked((int)0x80030005);
    private const int StgEReverted = unchecked((int)0x80030102);
    private const int StgENotImplemented = unchecked((int)0x80030201);

    private static void ThrowHResult(int hresult)
    {
        Marshal.ThrowExceptionForHR(hresult);
    }
}
