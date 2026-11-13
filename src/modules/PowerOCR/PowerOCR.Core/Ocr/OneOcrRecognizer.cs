/*
 * Copyright (c) Microsoft Corporation
 * The Microsoft Corporation licenses this file to you under the MIT license.
 * See the LICENSE file in the project root for more information.
 */

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PowerOCR.Core.Models;

namespace PowerOCR.Core.Ocr;

public sealed partial class OneOcrRecognizer : IOcrRecognizer, IDisposable
{
    private const string ModelKey = "kj)TGtrK>f]b[Piow.gU+nC@s\"\"\"\"\"\"4";
    private const int MaximumLineCount = 1000;

    private const uint LoadLibrarySearchDllLoadDir = 0x00000100;
    private const uint LoadLibrarySearchDefaultDirs = 0x00001000;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private IntPtr _library;
    private long _context;
    private long _pipeline;
    private long _processOptions;

    private bool _initializationAttempted;
    private bool _initialized;
    private Exception? _initializationFailure;
    private bool _disposed;

    private CreateOcrInitOptionsDelegate? _createInitOptions;
    private OcrInitOptionsSetUseModelDelayLoadDelegate? _setModelDelayLoad;
    private CreateOcrPipelineDelegate? _createPipeline;
    private CreateOcrProcessOptionsDelegate? _createProcessOptions;
    private OcrProcessOptionsSetMaxRecognitionLineCountDelegate? _setMaxLineCount;
    private RunOcrPipelineDelegate? _runPipeline;
    private GetOcrLineCountDelegate? _getLineCount;
    private GetOcrLineDelegate? _getLine;
    private GetOcrLineContentDelegate? _getLineContent;
    private GetOcrLineBoundingBoxDelegate? _getLineBoundingBox;
    private GetOcrLineWordCountDelegate? _getLineWordCount;
    private GetOcrWordDelegate? _getWord;
    private GetOcrWordContentDelegate? _getWordContent;
    private GetOcrWordBoundingBoxDelegate? _getWordBoundingBox;

    private ReleaseOcrInitOptionsDelegate? _releaseInitOptions;
    private ReleaseOcrPipelineDelegate? _releasePipeline;
    private ReleaseOcrProcessOptionsDelegate? _releaseProcessOptions;
    private ReleaseOcrResultDelegate? _releaseResult;

    public async Task<OcrDocument> RecognizeAsync(
        Bitmap bitmap,
        Windows.Globalization.Language language,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(language);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await Task.Run(
                () => RecognizeCore(bitmap, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private OcrDocument RecognizeCore(Bitmap bitmap, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();

        Bitmap source = bitmap;
        Bitmap? convertedBitmap = null;

        if (bitmap.PixelFormat != PixelFormat.Format32bppArgb)
        {
            convertedBitmap = new Bitmap(
                bitmap.Width,
                bitmap.Height,
                PixelFormat.Format32bppArgb);

            using (Graphics graphics = Graphics.FromImage(convertedBitmap))
            {
                graphics.DrawImageUnscaled(bitmap, 0, 0);
            }

            source = convertedBitmap;
        }

        try
        {
            Rectangle rectangle = new(0, 0, source.Width, source.Height);
            BitmapData bitmapData = source.LockBits(
                rectangle,
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            try
            {
                if (bitmapData.Stride <= 0)
                {
                    throw new InvalidOperationException(
                        "OneOCR requires a bitmap with a positive row stride.");
                }

                NativeImage image = new()
                {
                    Type = 3,
                    Width = source.Width,
                    Height = source.Height,
                    Reserved = 0,
                    Stride = bitmapData.Stride,
                    Data = bitmapData.Scan0,
                };

                cancellationToken.ThrowIfCancellationRequested();

                long result = 0;
                long status = _runPipeline!(
                    _pipeline,
                    ref image,
                    _processOptions,
                    out result);

                EnsureSuccess("RunOcrPipeline", status);

                if (result == 0)
                {
                    throw new InvalidOperationException(
                        "OneOCR returned an empty result handle.");
                }

                System.Diagnostics.Trace.TraceInformation(
                    "OneOCR pipeline succeeded.");

                try
                {
                    OcrDocument document = ReadResult(result, cancellationToken);

                    System.Diagnostics.Trace.TraceInformation(
                        "OneOCR recognition completed successfully.");

                    return document;
                }
                finally
                {
                    _releaseResult!(result);
                }
            }
            finally
            {
                source.UnlockBits(bitmapData);
            }
        }
        finally
        {
            convertedBitmap?.Dispose();
        }
    }

    private OcrDocument ReadResult(long result, CancellationToken cancellationToken)
    {
        EnsureSuccess(
            "GetOcrLineCount",
            _getLineCount!(result, out long lineCount));

        if (lineCount < 0 || lineCount > 10000)
        {
            throw new InvalidOperationException(
                $"OneOCR returned an invalid line count: {lineCount}.");
        }

        List<OcrLineData> lines = new((int)lineCount);

        for (long lineIndex = 0; lineIndex < lineCount; lineIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            EnsureSuccess(
                "GetOcrLine",
                _getLine!(result, lineIndex, out long lineHandle));

            if (lineHandle == 0)
            {
                continue;
            }

            EnsureSuccess(
                "GetOcrLineContent",
                _getLineContent!(lineHandle, out IntPtr lineTextPointer));

            string lineText = Marshal.PtrToStringUTF8(lineTextPointer)
                ?? string.Empty;

            EnsureSuccess(
                "GetOcrLineWordCount",
                _getLineWordCount!(lineHandle, out long wordCount));

            if (wordCount < 0 || wordCount > 10000)
            {
                throw new InvalidOperationException(
                    $"OneOCR returned an invalid word count: {wordCount}.");
            }

            List<OcrWordData> words = new((int)wordCount);

            for (long wordIndex = 0; wordIndex < wordCount; wordIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                EnsureSuccess(
                    "GetOcrWord",
                    _getWord!(lineHandle, wordIndex, out long wordHandle));

                if (wordHandle == 0)
                {
                    continue;
                }

                EnsureSuccess(
                    "GetOcrWordContent",
                    _getWordContent!(wordHandle, out IntPtr wordTextPointer));

                string wordText = Marshal.PtrToStringUTF8(wordTextPointer)
                    ?? string.Empty;

                EnsureSuccess(
                    "GetOcrWordBoundingBox",
                    _getWordBoundingBox!(wordHandle, out IntPtr wordBoundsPointer));

                if (wordBoundsPointer == IntPtr.Zero)
                {
                    continue;
                }

                NativeBoundingBox wordBounds =
                    Marshal.PtrToStructure<NativeBoundingBox>(wordBoundsPointer);

                words.Add(new OcrWordData(
                    wordText,
                    ToOcrRect(wordBounds)));
            }

            OcrRect lineBounds;

            long boundsStatus = _getLineBoundingBox!(
                lineHandle,
                out IntPtr lineBoundsPointer);

            if (boundsStatus == 0 && lineBoundsPointer != IntPtr.Zero)
            {
                NativeBoundingBox nativeBounds =
                    Marshal.PtrToStructure<NativeBoundingBox>(lineBoundsPointer);

                lineBounds = ToOcrRect(nativeBounds);
            }
            else if (words.Count > 0)
            {
                lineBounds = words
                    .Select(word => word.Bounds)
                    .Aggregate((left, right) => left.Union(right));
            }
            else
            {
                lineBounds = new OcrRect(0, 0, 0, 0);
            }

            lines.Add(new OcrLineData(lineText, lineBounds, words));
        }

        return new OcrDocument(lines);
    }

    private static OcrRect ToOcrRect(NativeBoundingBox bounds)
    {
        double left = Math.Min(
            Math.Min(bounds.X1, bounds.X2),
            Math.Min(bounds.X3, bounds.X4));

        double top = Math.Min(
            Math.Min(bounds.Y1, bounds.Y2),
            Math.Min(bounds.Y3, bounds.Y4));

        double right = Math.Max(
            Math.Max(bounds.X1, bounds.X2),
            Math.Max(bounds.X3, bounds.X4));

        double bottom = Math.Max(
            Math.Max(bounds.Y1, bounds.Y2),
            Math.Max(bounds.Y3, bounds.Y4));

        return new OcrRect(left, top, right - left, bottom - top);
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        if (_initializationAttempted)
        {
            throw new InvalidOperationException(
                "OneOCR initialization previously failed.",
                _initializationFailure);
        }

        _initializationAttempted = true;

        try
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            {
                throw new PlatformNotSupportedException(
                    "The installed OneOCR binaries require an x64 process.");
            }

            string engineDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "PowerToys",
                "PowerOCR",
                "OneOCR");

            string dllPath = Path.Combine(engineDirectory, "oneocr.dll");
            string modelPath = Path.Combine(engineDirectory, "oneocr.onemodel");

            if (!File.Exists(dllPath))
            {
                throw new FileNotFoundException("OneOCR DLL was not found.", dllPath);
            }

            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException("OneOCR model was not found.", modelPath);
            }

            _library = LoadLibraryExW(
                dllPath,
                IntPtr.Zero,
                LoadLibrarySearchDllLoadDir | LoadLibrarySearchDefaultDirs);

            if (_library == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"Could not load oneocr.dll. Win32 error: {Marshal.GetLastWin32Error()}.");
            }

            LoadFunctions();

            EnsureSuccess(
                "CreateOcrInitOptions",
                _createInitOptions!(out _context));

            EnsureSuccess(
                "OcrInitOptionsSetUseModelDelayLoad",
                _setModelDelayLoad!(_context, 0));

            IntPtr modelPathPointer = Marshal.StringToHGlobalAnsi(modelPath);
            IntPtr modelKeyPointer = Marshal.StringToHGlobalAnsi(ModelKey);

            try
            {
                EnsureSuccess(
                    "CreateOcrPipeline",
                    _createPipeline!(
                        modelPathPointer,
                        modelKeyPointer,
                        _context,
                        out _pipeline));
            }
            finally
            {
                Marshal.FreeHGlobal(modelPathPointer);
                Marshal.FreeHGlobal(modelKeyPointer);
            }

            EnsureSuccess(
                "CreateOcrProcessOptions",
                _createProcessOptions!(out _processOptions));

            EnsureSuccess(
                "OcrProcessOptionsSetMaxRecognitionLineCount",
                _setMaxLineCount!(_processOptions, MaximumLineCount));

            _initialized = true;

            System.Diagnostics.Trace.TraceInformation(
                "OneOCR initialization succeeded.");
        }
        catch (Exception exception)
        {
            _initializationFailure = exception;
            ReleaseNativeResources();
            throw;
        }
    }

    private void LoadFunctions()
    {
        _createInitOptions = GetFunction<CreateOcrInitOptionsDelegate>("CreateOcrInitOptions");
        _setModelDelayLoad = GetFunction<OcrInitOptionsSetUseModelDelayLoadDelegate>("OcrInitOptionsSetUseModelDelayLoad");
        _createPipeline = GetFunction<CreateOcrPipelineDelegate>("CreateOcrPipeline");
        _createProcessOptions = GetFunction<CreateOcrProcessOptionsDelegate>("CreateOcrProcessOptions");
        _setMaxLineCount = GetFunction<OcrProcessOptionsSetMaxRecognitionLineCountDelegate>("OcrProcessOptionsSetMaxRecognitionLineCount");
        _runPipeline = GetFunction<RunOcrPipelineDelegate>("RunOcrPipeline");
        _getLineCount = GetFunction<GetOcrLineCountDelegate>("GetOcrLineCount");
        _getLine = GetFunction<GetOcrLineDelegate>("GetOcrLine");
        _getLineContent = GetFunction<GetOcrLineContentDelegate>("GetOcrLineContent");
        _getLineBoundingBox = GetFunction<GetOcrLineBoundingBoxDelegate>("GetOcrLineBoundingBox");
        _getLineWordCount = GetFunction<GetOcrLineWordCountDelegate>("GetOcrLineWordCount");
        _getWord = GetFunction<GetOcrWordDelegate>("GetOcrWord");
        _getWordContent = GetFunction<GetOcrWordContentDelegate>("GetOcrWordContent");
        _getWordBoundingBox = GetFunction<GetOcrWordBoundingBoxDelegate>("GetOcrWordBoundingBox");

        _releaseInitOptions = GetFunction<ReleaseOcrInitOptionsDelegate>("ReleaseOcrInitOptions");
        _releasePipeline = GetFunction<ReleaseOcrPipelineDelegate>("ReleaseOcrPipeline");
        _releaseProcessOptions = GetFunction<ReleaseOcrProcessOptionsDelegate>("ReleaseOcrProcessOptions");
        _releaseResult = GetFunction<ReleaseOcrResultDelegate>("ReleaseOcrResult");
    }

    private T GetFunction<T>(string name)
        where T : Delegate
    {
        IntPtr address = NativeLibrary.GetExport(_library, name);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static void EnsureSuccess(string functionName, long status)
    {
        if (status != 0)
        {
            throw new InvalidOperationException(
                $"OneOCR function {functionName} failed with status {status}.");
        }
    }

    private void ReleaseNativeResources()
    {
        try
        {
            if (_processOptions != 0)
            {
                _releaseProcessOptions?.Invoke(_processOptions);
                _processOptions = 0;
            }

            if (_pipeline != 0)
            {
                _releasePipeline?.Invoke(_pipeline);
                _pipeline = 0;
            }

            if (_context != 0)
            {
                _releaseInitOptions?.Invoke(_context);
                _context = 0;
            }
        }
        finally
        {
            if (_library != IntPtr.Zero)
            {
                NativeLibrary.Free(_library);
                _library = IntPtr.Zero;
            }

            _initialized = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gate.Wait();

        try
        {
            if (_disposed)
            {
                return;
            }

            ReleaseNativeResources();
            _disposed = true;
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(
        string fileName,
        IntPtr file,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeImage
    {
        public int Type;
        public int Width;
        public int Height;
        public int Reserved;
        public long Stride;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBoundingBox
    {
        public float X1;
        public float Y1;
        public float X2;
        public float Y2;
        public float X3;
        public float Y3;
        public float X4;
        public float Y4;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long CreateOcrInitOptionsDelegate(out long context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long OcrInitOptionsSetUseModelDelayLoadDelegate(long context, byte flag);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long CreateOcrPipelineDelegate(
        IntPtr modelPath,
        IntPtr key,
        long context,
        out long pipeline);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long CreateOcrProcessOptionsDelegate(out long options);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long OcrProcessOptionsSetMaxRecognitionLineCountDelegate(
        long options,
        long count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long RunOcrPipelineDelegate(
        long pipeline,
        ref NativeImage image,
        long options,
        out long result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long GetOcrLineCountDelegate(long result, out long count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long GetOcrLineDelegate(long result, long index, out long line);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long GetOcrLineContentDelegate(long line, out IntPtr content);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long GetOcrLineBoundingBoxDelegate(long line, out IntPtr bounds);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long GetOcrLineWordCountDelegate(long line, out long count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long GetOcrWordDelegate(long line, long index, out long word);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long GetOcrWordContentDelegate(long word, out IntPtr content);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long GetOcrWordBoundingBoxDelegate(long word, out IntPtr bounds);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long ReleaseOcrInitOptionsDelegate(long context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long ReleaseOcrPipelineDelegate(long pipeline);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long ReleaseOcrProcessOptionsDelegate(long options);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long ReleaseOcrResultDelegate(long result);
}
