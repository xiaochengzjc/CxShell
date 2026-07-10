using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CxShell.Services;

public static class MacOSFilePromiseDragDropService
{
    private const string NativeLibrary = "CxMacDragBridge";
    private static readonly ConcurrentDictionary<nint, PromiseContext> Contexts = new();
    private static readonly WritePromiseCallback WriteCallback = WritePromisedFile;
    private static readonly PromiseCallback CancelCallback = CancelPromisedFile;
    private static readonly PromiseCallback ReleaseCallback = ReleasePromisedFile;
    private static long _nextContextId;

    public static bool IsAvailable
    {
        get
        {
            if (!OperatingSystem.IsMacOS())
                return false;

            try
            {
                return NativeMethods.GetVersion() >= 1;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool TryStart(
        nint nativeWindowOrView,
        IReadOnlyList<VirtualDragFile> files,
        out string? error)
    {
        error = null;
        if (!OperatingSystem.IsMacOS() || nativeWindowOrView == 0 || files.Count == 0)
            return false;

        var descriptors = new NativePromiseDescriptor[files.Count];
        var contextIds = new nint[files.Count];
        var fileNamePointers = new nint[files.Count];

        try
        {
            for (var index = 0; index < files.Count; index++)
            {
                var contextId = (nint)Interlocked.Increment(ref _nextContextId);
                var fileNamePointer = Marshal.StringToCoTaskMemUTF8(files[index].FileName);
                Contexts[contextId] = new PromiseContext(files[index]);
                contextIds[index] = contextId;
                fileNamePointers[index] = fileNamePointer;
                descriptors[index] = new NativePromiseDescriptor
                {
                    FileNameUtf8 = fileNamePointer,
                    Context = contextId
                };
            }

            var errorBuffer = new StringBuilder(1024);
            var started = NativeMethods.BeginFilePromiseDrag(
                nativeWindowOrView,
                descriptors,
                descriptors.Length,
                WriteCallback,
                CancelCallback,
                ReleaseCallback,
                errorBuffer,
                errorBuffer.Capacity);
            if (started != 0)
                return true;

            error = errorBuffer.Length > 0
                ? errorBuffer.ToString()
                : "macOS could not start the Finder file promise.";
            var startException = new InvalidOperationException(error);
            foreach (var file in files)
                file.NotifyFailed(startException);
            CleanupUnclaimedContexts(contextIds);
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            foreach (var file in files)
                file.NotifyFailed(ex);
            CleanupUnclaimedContexts(contextIds);
            return false;
        }
        finally
        {
            foreach (var pointer in fileNamePointers)
            {
                if (pointer != 0)
                    Marshal.FreeCoTaskMem(pointer);
            }
        }
    }

    private static int WritePromisedFile(nint contextId, nint destinationPathUtf8)
    {
        if (!Contexts.TryGetValue(contextId, out var context))
            return 2;

        var destinationPath = Marshal.PtrToStringUTF8(destinationPathUtf8);
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            context.File.NotifyFailed(new IOException("Finder did not provide a destination path."));
            return 3;
        }

        var partPath = destinationPath + ".cxshell.part";
        try
        {
            context.File.CancellationToken.ThrowIfCancellationRequested();
            var parentDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(parentDirectory))
                Directory.CreateDirectory(parentDirectory);

            var offset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            if (offset > context.File.Size)
            {
                File.Delete(partPath);
                offset = 0;
            }

            using (var source = context.File.OpenReadStream())
            using (var destination = File.Open(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
            {
                context.File.NotifyStarted();
                if (offset > 0)
                {
                    source.Seek(offset, SeekOrigin.Begin);
                    destination.Seek(offset, SeekOrigin.Begin);
                    context.File.NotifyProgress(offset);
                }

                var buffer = new byte[128 * 1024];
                var transferred = offset;
                while (true)
                {
                    context.File.CancellationToken.ThrowIfCancellationRequested();
                    var read = source.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        break;

                    destination.Write(buffer, 0, read);
                    transferred += read;
                    context.File.NotifyProgress(transferred);
                }

                destination.Flush(true);
            }

            context.File.CancellationToken.ThrowIfCancellationRequested();
            File.Move(partPath, destinationPath, true);
            context.File.NotifyCompleted();
            return 0;
        }
        catch (OperationCanceledException)
        {
            context.File.NotifyCancelled();
            return 4;
        }
        catch (Exception ex)
        {
            context.File.NotifyFailed(ex);
            return 5;
        }
    }

    private static void CancelPromisedFile(nint contextId)
    {
        if (!Contexts.TryGetValue(contextId, out var context))
            return;

        context.File.RequestCancellation();
        context.File.NotifyCancelled();
    }

    private static void ReleasePromisedFile(nint contextId)
    {
        Contexts.TryRemove(contextId, out _);
    }

    private static void CleanupUnclaimedContexts(IEnumerable<nint> contextIds)
    {
        foreach (var contextId in contextIds)
        {
            if (contextId != 0)
                Contexts.TryRemove(contextId, out _);
        }
    }

    private sealed class PromiseContext(VirtualDragFile file)
    {
        public VirtualDragFile File { get; } = file;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePromiseDescriptor
    {
        public nint FileNameUtf8;
        public nint Context;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WritePromiseCallback(nint context, nint destinationPathUtf8);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PromiseCallback(nint context);

    private static class NativeMethods
    {
        [DllImport(NativeLibrary, EntryPoint = "cxmac_drag_bridge_version", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetVersion();

        [DllImport(NativeLibrary, EntryPoint = "cxmac_begin_file_promise_drag", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int BeginFilePromiseDrag(
            nint nativeWindowOrView,
            [In] NativePromiseDescriptor[] descriptors,
            int descriptorCount,
            WritePromiseCallback writeCallback,
            PromiseCallback cancelCallback,
            PromiseCallback releaseCallback,
            [Out] StringBuilder errorBuffer,
            int errorBufferSize);
    }
}
