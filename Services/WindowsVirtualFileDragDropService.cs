using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Threading;

namespace CxShell.Services;

public sealed class VirtualDragFile(
    string fileName,
    long size,
    DateTime lastModified,
    Func<Stream> openReadStream,
    CancellationToken cancellationToken = default,
    Action? started = null,
    Action<long>? progressChanged = null,
    Action? completed = null,
    Action<string>? failed = null,
    Action? cancelled = null,
    Action? cancellationRequested = null)
{
    private int _started;
    private int _terminalState;
    private long _maximumPosition;

    public string FileName { get; } = fileName;
    public long Size { get; } = size;
    public DateTime LastModified { get; } = lastModified;
    public Func<Stream> OpenReadStream { get; } = openReadStream;
    public CancellationToken CancellationToken { get; } = cancellationToken;

    internal bool IsTerminal => Volatile.Read(ref _terminalState) != 0;

    internal void NotifyStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
            started?.Invoke();
    }

    internal void NotifyProgress(long position)
    {
        if (IsTerminal)
            return;

        position = Math.Clamp(position, 0, Math.Max(0, Size));
        var current = Volatile.Read(ref _maximumPosition);
        while (position > current)
        {
            var observed = Interlocked.CompareExchange(ref _maximumPosition, position, current);
            if (observed == current)
            {
                progressChanged?.Invoke(position);
                break;
            }

            current = observed;
        }
    }

    internal void NotifyCompleted()
    {
        if (Interlocked.CompareExchange(ref _terminalState, 1, 0) != 0)
            return;

        if (Size > 0)
            progressChanged?.Invoke(Size);
        completed?.Invoke();
    }

    internal void NotifyFailed(Exception exception)
    {
        if (CancellationToken.IsCancellationRequested)
        {
            NotifyCancelled();
            return;
        }

        if (Interlocked.CompareExchange(ref _terminalState, 2, 0) == 0)
            failed?.Invoke(exception.Message);
    }

    internal void NotifyCancelled()
    {
        if (Interlocked.CompareExchange(ref _terminalState, 3, 0) == 0)
            cancelled?.Invoke();
    }

    internal void RequestCancellation()
    {
        cancellationRequested?.Invoke();
    }
}

public static class WindowsVirtualFileDragDropService
{
    private static readonly object DebugLogSync = new();

    public static bool IsSupported => OperatingSystem.IsWindows();
    public static string DebugLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CxShell",
        "Logs",
        "sftp-drag.log");

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
        dataObject.SetAsyncMode(true);
        var dropSource = new DropSource();
        DebugLog($"drag start files={files.Count}");
        try
        {
            var hr = OleDoDragDrop(dataObject, dropSource, DropEffectCopy, out var effect);
            DebugLog($"drag loop ended result=0x{hr:X8} effect={effect}");
            dataObject.NotifyDragLoopCompleted(hr, effect);
            if (hr != 0 && hr != DragDropSCancel && hr != DragDropSDrop)
                Marshal.ThrowExceptionForHR(hr);

            return effect;
        }
        catch (Exception ex)
        {
            DebugLog($"drag failed type={ex.GetType().Name} message={ex.Message}");
            dataObject.NotifyDragFailed(ex);
            throw;
        }
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

    [ComImport]
    [Guid("3D8B0590-F691-11D2-8EA9-006097DF5BD4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAsyncOperation
    {
        [PreserveSig]
        int SetAsyncMode([MarshalAs(UnmanagedType.Bool)] bool doOperationAsync);

        [PreserveSig]
        int GetAsyncMode([MarshalAs(UnmanagedType.Bool)] out bool isOperationAsync);

        [PreserveSig]
        int StartOperation(IntPtr reservedBindContext);

        [PreserveSig]
        int InOperation([MarshalAs(UnmanagedType.Bool)] out bool isInAsyncOperation);

        [PreserveSig]
        int EndOperation(int result, IntPtr reservedBindContext, int effects);
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
    private sealed class VirtualFileDataObject : IDataObject, IAsyncOperation
    {
        private static readonly short FileGroupDescriptorW = unchecked((short)RegisterClipboardFormat("FileGroupDescriptorW"));
        private static readonly short FileContents = unchecked((short)RegisterClipboardFormat("FileContents"));
        private readonly IReadOnlyList<VirtualDragFile> _files;
        private readonly FORMATETC[] _formats;
        private readonly object _streamSync = new();
        private readonly List<RemoteComReadStream> _openStreams = [];
        private bool _asyncMode;
        private bool _inAsyncOperation;

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
                RemoteComReadStream stream;
                try
                {
                    file.CancellationToken.ThrowIfCancellationRequested();
                    DebugLog($"stream requested index={index} file={file.FileName} size={file.Size}");
                    var source = file.OpenReadStream();
                    file.NotifyStarted();
                    stream = new RemoteComReadStream(source, file);
                    lock (_streamSync)
                        _openStreams.Add(stream);
                }
                catch (OperationCanceledException)
                {
                    DebugLog($"stream request cancelled index={index} file={file.FileName}");
                    file.NotifyCancelled();
                    throw;
                }
                catch (Exception ex)
                {
                    DebugLog($"stream request failed index={index} file={file.FileName} type={ex.GetType().Name} message={ex.Message}");
                    file.NotifyFailed(ex);
                    throw;
                }

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

        public int SetAsyncMode(bool doOperationAsync)
        {
            _asyncMode = doOperationAsync;
            DebugLog($"async mode set enabled={doOperationAsync}");
            return SOk;
        }

        public int GetAsyncMode(out bool isOperationAsync)
        {
            isOperationAsync = _asyncMode;
            DebugLog($"async mode queried enabled={isOperationAsync}");
            return SOk;
        }

        public int StartOperation(IntPtr reservedBindContext)
        {
            _inAsyncOperation = true;
            DebugLog("async operation started");
            return SOk;
        }

        public int InOperation(out bool isInAsyncOperation)
        {
            isInAsyncOperation = _inAsyncOperation;
            return SOk;
        }

        public int EndOperation(int result, IntPtr reservedBindContext, int effects)
        {
            _inAsyncOperation = false;
            DebugLog($"async operation ended result=0x{result:X8} effects={effects}");
            if (result >= 0 && (effects & DropEffectCopy) != 0)
            {
                NotifyFilesCompleted();
            }
            else if (result == DragDropSCancel || effects == DropEffectNone)
            {
                NotifyFilesCancelled();
            }
            else
            {
                NotifyFilesFailed(Marshal.GetExceptionForHR(result) ?? new IOException("Windows file drop failed."));
            }

            DisposeStreamsInBackground();
            return SOk;
        }

        public void NotifyDragLoopCompleted(int result, int effect)
        {
            if (result == DragDropSCancel || effect == DropEffectNone)
            {
                NotifyFilesCancelled();
                DisposeStreamsInBackground();
                return;
            }

            if (_asyncMode && _inAsyncOperation)
                return;

            if (result >= 0 || result == DragDropSDrop)
                NotifyFilesCompleted();
            else
                NotifyFilesFailed(Marshal.GetExceptionForHR(result) ?? new IOException("Windows file drop failed."));

            DisposeStreamsInBackground();
        }

        public void NotifyDragFailed(Exception exception)
        {
            NotifyFilesFailed(exception);
            DisposeStreamsInBackground();
        }

        private void NotifyFilesCompleted()
        {
            foreach (var file in _files)
                file.NotifyCompleted();
        }

        private void NotifyFilesCancelled()
        {
            foreach (var file in _files)
                file.NotifyCancelled();
        }

        private void NotifyFilesFailed(Exception exception)
        {
            foreach (var file in _files)
                file.NotifyFailed(exception);
        }

        private void DisposeStreamsInBackground()
        {
            List<RemoteComReadStream> streams;
            lock (_streamSync)
            {
                streams = [.. _openStreams];
                _openStreams.Clear();
            }

            foreach (var stream in streams)
                stream.DisposeInBackground();
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
        private Stream? _stream;
        private readonly VirtualDragFile _file;
        private long _bytesRead;
        private int _completionLogged;

        public RemoteComReadStream(Stream stream, VirtualDragFile file)
        {
            _stream = stream;
            _file = file;
        }

        ~RemoteComReadStream()
        {
            DisposeInBackground();
        }

        public void Read(byte[] buffer, int count, IntPtr bytesRead)
        {
            var stream = _stream;
            if (stream == null)
            {
                if (bytesRead != IntPtr.Zero)
                    Marshal.WriteInt32(bytesRead, 0);
                return;
            }

            try
            {
                _file.CancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, count);
                if (bytesRead != IntPtr.Zero)
                    Marshal.WriteInt32(bytesRead, read);

                if (read <= 0)
                {
                    LogCompletion();
                    return;
                }

                var position = stream.CanSeek
                    ? stream.Position
                    : Interlocked.Add(ref _bytesRead, read);
                _file.NotifyProgress(position);
                if (_file.Size >= 0 && position >= _file.Size)
                {
                    LogCompletion();
                }
            }
            catch (OperationCanceledException)
            {
                _file.NotifyCancelled();
                throw;
            }
            catch (Exception ex)
            {
                _file.NotifyFailed(ex);
                throw;
            }
        }

        public void Write(byte[] buffer, int count, IntPtr bytesWritten)
        {
            ThrowHResult(StgEAccessDenied);
        }

        public void Seek(long offset, int origin, IntPtr newPosition)
        {
            try
            {
                _file.CancellationToken.ThrowIfCancellationRequested();
                var stream = _stream ?? throw new ObjectDisposedException(nameof(RemoteComReadStream));
                var position = stream.Seek(offset, (SeekOrigin)origin);
                if (newPosition != IntPtr.Zero)
                    Marshal.WriteInt64(newPosition, position);
            }
            catch (OperationCanceledException)
            {
                _file.NotifyCancelled();
                throw;
            }
            catch (Exception ex)
            {
                _file.NotifyFailed(ex);
                throw;
            }
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

            try
            {
                while (totalRead < count)
                {
                    _file.CancellationToken.ThrowIfCancellationRequested();
                    var toRead = (int)Math.Min(buffer.Length, count - totalRead);
                    var stream = _stream;
                    if (stream == null)
                        break;

                    var read = stream.Read(buffer, 0, toRead);
                    if (read <= 0)
                        break;

                    totalRead += read;
                    destination.Write(buffer, read, IntPtr.Zero);
                    totalWritten += read;
                    var position = stream.CanSeek ? stream.Position : totalRead;
                    _file.NotifyProgress(position);
                }

                LogCompletion();
            }
            catch (OperationCanceledException)
            {
                _file.NotifyCancelled();
                throw;
            }
            catch (Exception ex)
            {
                _file.NotifyFailed(ex);
                throw;
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
                cbSize = _file.Size
            };
        }

        public void Clone(out IStream clone)
        {
            clone = null!;
            ThrowHResult(StgENotImplemented);
        }

        public void DisposeInBackground()
        {
            var stream = Interlocked.Exchange(ref _stream, null);
            if (stream == null)
                return;

            GC.SuppressFinalize(this);
            ThreadPool.QueueUserWorkItem(static state =>
            {
                try
                {
                    ((Stream)state!).Dispose();
                }
                catch
                {
                }
            }, stream);
        }

        private void LogCompletion()
        {
            if (Interlocked.Exchange(ref _completionLogged, 1) == 0)
                DebugLog($"stream completed file={_file.FileName} size={_file.Size}");
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

    private static void DebugLog(string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(DebugLogPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {message}{Environment.NewLine}";
            lock (DebugLogSync)
                File.AppendAllText(DebugLogPath, line);
        }
        catch
        {
        }
    }
}
