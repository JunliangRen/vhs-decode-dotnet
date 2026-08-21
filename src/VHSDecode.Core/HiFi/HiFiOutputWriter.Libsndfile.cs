using System.Runtime.InteropServices;

namespace VHSDecode.Core.HiFi;

internal sealed partial class HiFiOutputWriter
{
    private sealed unsafe partial class LibsndfileFlacWriter : IHiFiFloatWriter
    {
        private const int FlacPcm24Format = 0x170003;
        private const int SetClippingCommand = 0x10c0;
        private const int SetCompressionLevelCommand = 0x1301;
        private const int True = 1;
        private const int WriteMode = 0x20;

        private readonly int _channels;
        private nint _file;
        private bool _completed;
        private bool _disposed;

        public LibsndfileFlacWriter(
            string path,
            int channels,
            int sampleRate)
        {
            _channels = channels;
            var info = new SoundFileInfo
            {
                SampleRate = sampleRate,
                Channels = channels,
                Format = FlacPcm24Format
            };

            try
            {
                _file = NativeMethods.Open(path, WriteMode, ref info);
            }
            catch (DllNotFoundException ex)
            {
                throw new NotSupportedException(
                    "The libsndfile native library is required to write HiFi FLAC output.",
                    ex);
            }

            if (_file == 0)
            {
                throw new InvalidDataException(
                    $"libsndfile failed to open HiFi FLAC output: {ErrorText(0)}");
            }

            int clippingResult = NativeMethods.Command(
                _file,
                SetClippingCommand,
                null,
                True);
            if (clippingResult != 1)
            {
                string error = ErrorText(_file);
                NativeMethods.Close(_file);
                _file = 0;
                throw new InvalidDataException(
                    $"libsndfile failed to enable HiFi FLAC clipping: {error}");
            }

            double compressionLevel = 1.0;
            int result = NativeMethods.Command(
                _file,
                SetCompressionLevelCommand,
                &compressionLevel,
                sizeof(double));
            if (result != 1)
            {
                string error = ErrorText(_file);
                NativeMethods.Close(_file);
                _file = 0;
                throw new InvalidDataException(
                    $"libsndfile failed to set HiFi FLAC compression: {error}");
            }
        }

        public void Write(ReadOnlySpan<float> samples)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed)
            {
                throw new InvalidOperationException(
                    "Audio writer is already complete.");
            }

            if (samples.Length % _channels != 0)
            {
                throw new ArgumentException(
                    "Interleaved HiFi audio must contain complete frames.",
                    nameof(samples));
            }

            if (samples.IsEmpty)
            {
                return;
            }

            long frames = samples.Length / _channels;
            fixed (float* samplePointer = samples)
            {
                long written = NativeMethods.WriteFramesFloat(
                    _file,
                    samplePointer,
                    frames);
                if (written != frames)
                {
                    throw new InvalidDataException(
                        $"libsndfile failed while writing HiFi FLAC output: {ErrorText(_file)}");
                }
            }
        }

        public void Complete()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed)
            {
                return;
            }

            _completed = true;
            nint file = _file;
            _file = 0;
            int result = NativeMethods.Close(file);
            if (result != 0)
            {
                throw new InvalidDataException(
                    $"libsndfile failed while finalizing HiFi FLAC output: {ErrorText(0)}");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (!_completed)
                {
                    Complete();
                }
            }
            finally
            {
                _disposed = true;
            }
        }

        private static string ErrorText(nint file)
            => Marshal.PtrToStringUTF8(NativeMethods.StrError(file))
                ?? "unknown libsndfile error";

        [StructLayout(LayoutKind.Sequential)]
        private struct SoundFileInfo
        {
            public long Frames;
            public int SampleRate;
            public int Channels;
            public int Format;
            public int Sections;
            public int Seekable;
        }

        private static partial class NativeMethods
        {
            private const string LibraryName = "sndfile";

            internal static nint Open(
                string path,
                int mode,
                ref SoundFileInfo info)
                => OperatingSystem.IsWindows()
                    ? OpenWindows(path, mode, ref info)
                    : OpenUnix(path, mode, ref info);

            [LibraryImport(
                LibraryName,
                EntryPoint = "sf_wchar_open",
                StringMarshalling = StringMarshalling.Utf16)]
            private static partial nint OpenWindows(
                string path,
                int mode,
                ref SoundFileInfo info);

            [LibraryImport(
                LibraryName,
                EntryPoint = "sf_open",
                StringMarshalling = StringMarshalling.Utf8)]
            private static partial nint OpenUnix(
                string path,
                int mode,
                ref SoundFileInfo info);

            [LibraryImport(
                LibraryName,
                EntryPoint = "sf_command")]
            internal static partial int Command(
                nint file,
                int command,
                void* data,
                int dataSize);

            [LibraryImport(
                LibraryName,
                EntryPoint = "sf_writef_float")]
            internal static partial long WriteFramesFloat(
                nint file,
                float* samples,
                long frames);

            [LibraryImport(
                LibraryName,
                EntryPoint = "sf_close")]
            internal static partial int Close(nint file);

            [LibraryImport(
                LibraryName,
                EntryPoint = "sf_strerror")]
            internal static partial nint StrError(nint file);
        }
    }
}
