using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VHSDecode.Preview;

public enum PreviewSourceFormat { Vhs, LaserDisc, Betamax, Cvbs, SVhs, SecamVhs, MesecamVhs }
public enum PreviewVideoStandard { Ntsc, Pal, Secam }
public enum PreviewAudioTrack { None, VhsHiFi, BetamaxHiFi, LaserDiscAnalog, LaserDiscDigitalPcm, LaserDiscDts, LaserDiscAc3Rf }
public enum BetamaxVideoProfile { Standard, HiFi, SuperBeta, SuperBetaHiFi, EdBeta }

/// <summary>Receiver state at the end of a processed block. Null means the stream has no such track.</summary>
public readonly record struct PreviewLockStatus(bool? HorizontalLocked, bool? VerticalLocked,
    bool? ColorLocked, bool? AudioLocked);

public sealed record RealtimePreviewOptions(
    PreviewSourceFormat SourceFormat,
    PreviewVideoStandard Standard,
    int InputSampleRateHz)
{
    /// <summary>Enable baseline restoration for AC-coupled DdD composite input.</summary>
    public bool Cvbs50Ohms { get; init; }
    /// <summary>Include signed signal-domain Y/U/V samples for each video field.</summary>
    public bool IncludeSignalScopes { get; init; }
    public PreviewAudioTrack AudioTrack { get; init; }
    /// <summary>Playback delay relative to recovered AC-3 RF media time. The default was
    /// measured against the companion digital PCM tracks on two NTSC discs.</summary>
    public TimeSpan LaserDiscAc3PresentationDelay { get; init; } = TimeSpan.FromMilliseconds(128);
    public BetamaxVideoProfile BetamaxProfile { get; init; }
    public PreviewRegion Region { get; init; } = PreviewRegion.ActiveVideo;
    public int DecodeThreads { get; init; } = 1;
}

/// <summary>Owned interleaved stereo preview audio.</summary>
public sealed class PreviewAudioBlock
{
    internal PreviewAudioBlock(float[] samples, long endSourceSample, int sourceRate, int sampleRate, bool locked, TimeSpan presentationDelay)
    {
        InterleavedSamples = samples;
        EndSourceSample = endSourceSample;
        EndTimestamp = TimeSpan.FromSeconds(endSourceSample / (double)sourceRate);
        PresentationEndTimestamp = EndTimestamp + presentationDelay;
        SampleRateHz = sampleRate;
        IsLocked = locked;
    }

    public ReadOnlyMemory<float> InterleavedSamples { get; }
    public int SampleRateHz { get; }
    public int Channels => 2;
    public int Frames => InterleavedSamples.Length / Channels;
    /// <summary>Exclusive end position on the input RF sample clock.</summary>
    public long EndSourceSample { get; }
    public TimeSpan EndTimestamp { get; }
    /// <summary>Schedule the end of this audio block against the video field timestamps.</summary>
    public TimeSpan PresentationEndTimestamp { get; }
    public bool IsLocked { get; }
}

/// <summary>A synchronous, stateful RF preview session with callbacks on the caller's thread.</summary>
/// <remarks>Use a worker thread and call Process, Reset and Dispose serially. Returned video and audio can be retained.</remarks>
public sealed class RealtimePreviewSession : IDisposable
{
    internal const int BlockSamples = 65_536;
    private const int HalfBandDelay = 15;
    private const string LibraryName = "vhsdecode_realtime_preview";
    private static readonly NativeFieldCallback FieldCallback = ReceiveField;
    private static readonly NativeAudioCallback AudioCallback = ReceiveAudio;

    private readonly NativeDecoder _decoder;
    private readonly CallbackState _state;
    private int _busy;
    private int _lockFlags;

    /// <summary>Thread-safe snapshot after the latest completed RF block. Reset and Finish clear lock.</summary>
    public PreviewLockStatus LockStatus
    {
        get
        {
            int flags = Volatile.Read(ref _lockFlags);
            bool video = _state.Options.AudioTrack != PreviewAudioTrack.VhsHiFi;
            bool audio = _state.Options.AudioTrack != PreviewAudioTrack.None;
            return new(video ? (flags & 1) != 0 : null, video ? (flags & 2) != 0 : null,
                video ? (flags & 4) != 0 : null, audio ? (flags & 8) != 0 : null);
        }
    }

    public RealtimePreviewSession(RealtimePreviewOptions options, Action<PreviewField>? fieldSink = null,
        Action<PreviewAudioBlock>? audioSink = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        bool audioOnly = options.AudioTrack == PreviewAudioTrack.VhsHiFi;
        if (audioOnly && (audioSink is null || fieldSink is not null))
            throw new ArgumentException("VHS HiFi preview requires only an audio sink.");
        if (options.AudioTrack is (PreviewAudioTrack.BetamaxHiFi or PreviewAudioTrack.LaserDiscAnalog or PreviewAudioTrack.LaserDiscDigitalPcm or PreviewAudioTrack.LaserDiscDts or PreviewAudioTrack.LaserDiscAc3Rf) && (audioSink is null || fieldSink is null))
            throw new ArgumentException("Audiovisual preview requires both a field sink and an audio sink.");
        if (options.AudioTrack == PreviewAudioTrack.None && (fieldSink is null || audioSink is not null))
            throw new ArgumentException("Video preview requires only a field sink.");
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The Realtime preview native library currently targets Windows x64.");

        _state = new CallbackState(options, fieldSink, audioSink);
        _decoder = Open(options);
        _state.Decoder = _decoder;
    }

    /// <summary>Consumes signed 16-bit mono RF. A discontinuous source position resets decoder history.</summary>
    public void Process(ReadOnlySpan<short> samples, long firstSourceSample, CancellationToken cancellationToken = default)
        => ProcessCore(samples, firstSourceSample, cancellationToken, 0, int.MaxValue);

    internal unsafe void ProcessCore(ReadOnlySpan<short> samples, long firstSourceSample,
        CancellationToken cancellationToken, long minimumFieldSample, int fieldLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstSourceSample);
        if (firstSourceSample > long.MaxValue - samples.Length)
            throw new ArgumentOutOfRangeException(nameof(firstSourceSample));
        Enter();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (samples.IsEmpty)
                return;
            _state.ResetForCall(cancellationToken, minimumFieldSample, fieldLimit);
            GCHandle context = GCHandle.Alloc(_state);
            try
            {
                nint callbackContext = GCHandle.ToIntPtr(context);
                while (!samples.IsEmpty && _state.FieldsRemaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = Math.Min(BlockSamples, samples.Length);
                    fixed (short* data = samples)
                    {
                        if (NativeProcess(_decoder, data, (nuint)count, (ulong)firstSourceSample,
                                FieldCallback, AudioCallback, callbackContext) != 1)
                            throw new InvalidOperationException("The Realtime preview decoder rejected the RF block.");
                    }
                    Volatile.Write(ref _lockFlags, NativeLockFlags(_decoder));
                    _state.Error?.Throw();
                    samples = samples[count..];
                    firstSourceSample += count;
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                context.Free();
                _state.ResetForCall(default, 0, int.MaxValue);
            }
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Drains validated digital PCM at natural EOF and resets the session for the next stream.</summary>
    public void Finish()
    {
        Enter();
        try
        {
            GCHandle context = GCHandle.Alloc(_state);
            try
            {
                NativeFinish(_decoder, AudioCallback, GCHandle.ToIntPtr(context));
                Volatile.Write(ref _lockFlags, 0);
                _state.Error?.Throw();
            }
            finally
            {
                context.Free();
                _state.ResetForCall(default, 0, int.MaxValue);
            }
        }
        finally { Exit(); }
    }

    /// <summary>Discards decoder and resampler history, including any incomplete field or audio block.</summary>
    public void Reset()
    {
        Enter();
        try
        {
            NativeReset(_decoder);
            Volatile.Write(ref _lockFlags, 0);
        }
        finally { Exit(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0)
            throw new InvalidOperationException("Realtime preview calls must be serialized.");
        try { _decoder.Dispose(); }
        finally { Exit(); }
    }

    private void Enter()
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0)
            throw new InvalidOperationException("Realtime preview calls must be serialized.");
        if (_decoder.IsClosed)
        {
            Exit();
            throw new ObjectDisposedException(nameof(RealtimePreviewSession));
        }
    }

    private void Exit() => Volatile.Write(ref _busy, 0);

    internal static void Validate(RealtimePreviewOptions options)
    {
        if (!Enum.IsDefined(options.Standard) || !Enum.IsDefined(options.SourceFormat)
            || !Enum.IsDefined(options.Region) || !Enum.IsDefined(options.AudioTrack)
            || !Enum.IsDefined(options.BetamaxProfile))
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.DecodeThreads is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(options.DecodeThreads),
                "Realtime preview supports 1 to 64 decode threads.");
        if (options.Cvbs50Ohms && options.SourceFormat != PreviewSourceFormat.Cvbs)
            throw new NotSupportedException("The 50 ohm preset requires CVBS input.");
        if (!RealtimePreviewCapabilities.IsSupported(options))
            throw new NotSupportedException("Unsupported Realtime source, standard, sample rate, Betamax profile or audio track.");
    }

    private static NativeDecoder Open(RealtimePreviewOptions options)
    {
        NativeDecoder decoder;
        try
        {
            if (options.AudioTrack == PreviewAudioTrack.LaserDiscDts && NativeHasDts() == 0)
                throw new NotSupportedException("LaserDisc DTS requires a native preview built with FFmpeg DCA support (-FfmpegPrefix).");
            int source = options.SourceFormat switch
            {
                PreviewSourceFormat.LaserDisc when options.Standard == PreviewVideoStandard.Pal => 10,
                PreviewSourceFormat.LaserDisc => 3,
                PreviewSourceFormat.SVhs => 4,
                PreviewSourceFormat.SecamVhs => 7,
                PreviewSourceFormat.MesecamVhs => 8,
                PreviewSourceFormat.Betamax when options.BetamaxProfile == BetamaxVideoProfile.EdBeta => 9,
                PreviewSourceFormat.Betamax when options.BetamaxProfile == BetamaxVideoProfile.SuperBeta => 5,
                PreviewSourceFormat.Betamax when options.BetamaxProfile == BetamaxVideoProfile.SuperBetaHiFi => 6,
                PreviewSourceFormat.Betamax when options.BetamaxProfile == BetamaxVideoProfile.HiFi
                    || options.AudioTrack == PreviewAudioTrack.BetamaxHiFi => 2,
                PreviewSourceFormat.Betamax => 1,
                _ => 0
            };
            decoder = options.SourceFormat == PreviewSourceFormat.Cvbs
                ? NativeOpenCvbs(options.Standard == PreviewVideoStandard.Pal ? 1 : 0,
                    options.Region == PreviewRegion.FullRaster ? 1 : 0, (uint)options.InputSampleRateHz,
                    options.Cvbs50Ohms ? 1 : 0)
                : NativeOpen(source,
                options.Standard == PreviewVideoStandard.Pal ? 1 : 0,
                options.Region == PreviewRegion.FullRaster ? 1 : 0, (uint)options.InputSampleRateHz, (int)options.AudioTrack);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new NotSupportedException(
                "Realtime preview requires an up-to-date vhsdecode_realtime_preview.dll. Build it with tools/build-realtime-preview-native.ps1.", ex);
        }

        try
        {
            if (decoder.IsInvalid || (options.DecodeThreads > 1 && NativeSetThreads(decoder, options.DecodeThreads) != 1))
                throw new InvalidOperationException("Could not initialize the Realtime preview decoder.");
            if (options.IncludeSignalScopes && NativeEnableScope(decoder) != 1)
                throw new InvalidOperationException("Could not allocate the signal scope buffer.");
            // Check the lock API before the first block can invoke a host callback.
            try { _ = NativeLockFlags(decoder); }
            catch (EntryPointNotFoundException ex)
            {
                throw new NotSupportedException("The native Realtime preview library does not support receiver lock status.", ex);
            }
            return decoder;
        }
        catch
        {
            decoder.Dispose();
            throw;
        }
    }

    private static unsafe void ReceiveField(nint context, nint rgba, int width, int rows, int parity,
        ulong start, int activeX, int activeY, int activeWidth, int activeHeight)
    {
        var state = (CallbackState)GCHandle.FromIntPtr(context).Target!;
        if (state.Error is not null || state.FieldsRemaining == 0)
            return;

        try
        {
            long sourceStart = state.Options.InputSampleRateHz == 40_000_000
                ? Math.Max(0, checked((long)start * 2) - HalfBandDelay)
                : checked((long)start);
            if (sourceStart < state.MinimumFieldSample)
                return;

            state.Cancellation.ThrowIfCancellationRequested();
            var raster = new PreviewRectangle(0, 0, width, rows);
            var active = new PreviewRectangle(activeX, activeY, activeWidth, activeHeight);
            if (width <= 0 || rows <= 0 || parity is < 0 or > 1 || activeX < 0 || activeY < 0
                || activeWidth <= 0 || activeHeight <= 0 || activeWidth > width - activeX
                || activeHeight > rows - activeY)
                throw new InvalidDataException("The Realtime preview decoder returned invalid field geometry.");

            byte[] bgra = GC.AllocateUninitializedArray<byte>(checked(width * rows * sizeof(uint)));
            Span<uint> output = MemoryMarshal.Cast<byte, uint>(bgra);
            var source = new ReadOnlySpan<uint>((void*)rgba, checked(((rows - 1) * 2 + parity + 1) * width));
            for (int y = 0; y < rows; y++)
            {
                var inputLine = source.Slice((2 * y + parity) * width, width);
                var outputLine = output.Slice(y * width, width);
                for (int x = 0; x < width; x++)
                {
                    uint pixel = inputLine[x];
                    outputLine[x] = (pixel & 0xff00ff00u) | ((pixel & 0xffu) << 16) | ((pixel >> 16) & 0xffu);
                }
            }

            state.FieldSink!(new PreviewField(bgra, raster, active, parity == 0,
                TimeSpan.FromSeconds(sourceStart / (double)state.Options.InputSampleRateHz),
                state.Options.Standard != PreviewVideoStandard.Ntsc ? 50 : 60_000.0 / 1001,
                default, default, 0, 0, 0, default(PreviewRectangle),
                state.Options.IncludeSignalScopes
                    ? new ReadOnlySpan<short>((void*)NativeScope(state.Decoder!), checked(width * rows * 3)).ToArray()
                    : default));
            state.FieldsRemaining--;
        }
        catch (Exception ex)
        {
            state.Error = ExceptionDispatchInfo.Capture(ex);
        }
    }

    private static unsafe void ReceiveAudio(nint context, nint samples, nuint frames, ulong end, int locked)
    {
        var state = (CallbackState)GCHandle.FromIntPtr(context).Target!;
        if (state.Error is not null)
            return;

        try
        {
            state.Cancellation.ThrowIfCancellationRequested();
            var pcm = new ReadOnlySpan<float>((void*)samples, checked((int)frames * 2));
            // The last 20 MHz observation was at index end - 1. Convert that
            // sample center to the source clock, then restore the exclusive end.
            long sourceEnd = state.Options.InputSampleRateHz == 40_000_000
                ? Math.Max(0, checked(((long)end - 1) * 2 + 1) - HalfBandDelay)
                : checked((long)end);
            state.AudioSink!(new PreviewAudioBlock(pcm.ToArray(), sourceEnd,
                state.Options.InputSampleRateHz, state.Options.AudioTrack is PreviewAudioTrack.LaserDiscDigitalPcm or PreviewAudioTrack.LaserDiscDts ? 44_100 : 48_000, locked != 0,
                state.Options.AudioTrack == PreviewAudioTrack.LaserDiscAc3Rf
                    ? state.Options.LaserDiscAc3PresentationDelay : TimeSpan.Zero));
        }
        catch (Exception ex)
        {
            state.Error = ExceptionDispatchInfo.Capture(ex);
        }
    }

    private sealed class CallbackState(RealtimePreviewOptions options, Action<PreviewField>? fieldSink,
        Action<PreviewAudioBlock>? audioSink)
    {
        internal NativeDecoder? Decoder;
        internal readonly RealtimePreviewOptions Options = options;
        internal readonly Action<PreviewField>? FieldSink = fieldSink;
        internal readonly Action<PreviewAudioBlock>? AudioSink = audioSink;
        internal CancellationToken Cancellation;
        internal long MinimumFieldSample;
        internal int FieldsRemaining;
        internal ExceptionDispatchInfo? Error;

        internal void ResetForCall(CancellationToken cancellation, long minimumFieldSample, int fieldLimit)
        {
            Cancellation = cancellation;
            MinimumFieldSample = minimumFieldSample;
            FieldsRemaining = fieldLimit;
            Error = null;
        }
    }

    private sealed class NativeDecoder : SafeHandleZeroOrMinusOneIsInvalid
    {
        public NativeDecoder() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle()
        {
            NativeClose(handle);
            return true;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeFieldCallback(nint context, nint rgba, int width, int rows, int parity,
        ulong start, int activeX, int activeY, int activeWidth, int activeHeight);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeAudioCallback(nint context, nint samples, nuint frames, ulong end, int locked);

    [DllImport(LibraryName, EntryPoint = "realtime_lock_flags", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeLockFlags(NativeDecoder decoder);

    [DllImport(LibraryName, EntryPoint = "realtime_has_dts", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeHasDts();

    [DllImport(LibraryName, EntryPoint = "realtime_open_cvbs", CallingConvention = CallingConvention.Cdecl)]
    private static extern NativeDecoder NativeOpenCvbs(int pal, int full, uint rate, int restore);

    [DllImport(LibraryName, EntryPoint = "realtime_enable_signal", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeEnableScope(NativeDecoder decoder);

    [DllImport(LibraryName, EntryPoint = "realtime_signal", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint NativeScope(NativeDecoder decoder);

    [DllImport(LibraryName, EntryPoint = "realtime_open_v9", CallingConvention = CallingConvention.Cdecl)]
    private static extern NativeDecoder NativeOpen(int source, int pal, int full, uint inputRate, int audioTrack);

    [DllImport(LibraryName, EntryPoint = "realtime_set_threads", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeSetThreads(NativeDecoder handle, int threads);

    [DllImport(LibraryName, EntryPoint = "realtime_close", CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeClose(nint handle);

    [DllImport(LibraryName, EntryPoint = "realtime_finish", CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeFinish(NativeDecoder handle, NativeAudioCallback callback, nint context);

    [DllImport(LibraryName, EntryPoint = "realtime_reset", CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeReset(NativeDecoder handle);

    [DllImport(LibraryName, EntryPoint = "realtime_process", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int NativeProcess(NativeDecoder handle, short* samples, nuint count,
        ulong position, NativeFieldCallback fieldCallback, NativeAudioCallback audioCallback, nint context);
}
