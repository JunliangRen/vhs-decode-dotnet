using System.Buffers.Binary;
using VHSDecode.Core.Dsp;

namespace VHSDecode.Core.Decode;

public interface ILaserDiscEfmOutputWriter
{
    IReadOnlyList<TbcDecodedField> Write(DecodeSession session, IReadOnlyList<TbcDecodedField> fields);

    ILaserDiscFieldOutputSession Open(DecodeSession session);
}

public interface ILaserDiscFieldOutputSession : IDisposable
{
    TbcDecodedField Write(TbcDecodedField field);

    TbcDecodedField WriteBeforeMetadata(TbcDecodedField field) => Write(field);

    void WriteAfterVideo(TbcDecodedField field)
    {
    }
}

public sealed class LaserDiscEfmOutputWriter : ILaserDiscEfmOutputWriter
{
    private readonly Func<string, Stream> _createOutput;

    public LaserDiscEfmOutputWriter(Func<string, Stream>? createOutput = null)
    {
        _createOutput = createOutput ?? OpenDefaultOutput;
    }

    public IReadOnlyList<TbcDecodedField> Write(DecodeSession session, IReadOnlyList<TbcDecodedField> fields)
    {
        var written = new TbcDecodedField[fields.Count];
        using ILaserDiscFieldOutputSession output = Open(session);
        for (int i = 0; i < written.Length; i++)
        {
            written[i] = output.Write(fields[i]);
        }

        return written;
    }

    public ILaserDiscFieldOutputSession Open(DecodeSession session)
    {
        LaserDiscAudioOptions? audioOptions = session.LaserDiscAudioOptions;
        return session.Spec.Name == "ld" && audioOptions is not null
            ? new OutputSession(session, audioOptions, _createOutput)
            : PassthroughOutputSession.Instance;
    }

    private static Stream OpenDefaultOutput(string path)
    {
        return path.EndsWith(".tbc.ldf", StringComparison.OrdinalIgnoreCase)
            ? FfmpegLdTestLdfWriter.OpenFfmpegInputPipe(
                path,
                terminateBeforeInputClose: true)
            : path.EndsWith(".ac3", StringComparison.OrdinalIgnoreCase)
                ? LaserDiscAc3Pipe.Open(path)
            : File.Create(path);
    }

    private static void WriteInt16LittleEndian(Stream destination, ReadOnlySpan<short> samples)
    {
        byte[] buffer = new byte[checked(samples.Length * sizeof(short))];
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * sizeof(short), sizeof(short)), samples[i]);
        }

        destination.Write(buffer, 0, buffer.Length);
    }

    private sealed class PassthroughOutputSession : ILaserDiscFieldOutputSession
    {
        public static PassthroughOutputSession Instance { get; } = new();

        public TbcDecodedField Write(TbcDecodedField field) => field;

        public void Dispose()
        {
        }
    }

    private sealed class OutputSession : ILaserDiscFieldOutputSession
    {
        private readonly Stream? _efmOutput;
        private readonly Stream? _preEfmOutput;
        private readonly Stream? _pcmOutput;
        private readonly Stream? _rfTbcOutput;
        private readonly Stream? _ac3Output;
        private readonly LaserDiscAc3Filter? _ac3Filter;
        private readonly LaserDiscEfmPll _pll = new();
        private bool _disposed;

        public OutputSession(
            DecodeSession session,
            LaserDiscAudioOptions audioOptions,
            Func<string, Stream> createOutput)
        {
            try
            {
                _pcmOutput = audioOptions.DecodeAnalogAudio
                    ? createOutput(session.OutputBase + ".pcm")
                    : null;
                _efmOutput = audioOptions.DecodeDigitalAudio
                    ? createOutput(session.OutputBase + ".efm")
                    : null;
                _preEfmOutput = audioOptions.DecodeDigitalAudio && audioOptions.WritePreEfm
                    ? createOutput(session.OutputBase + ".prefm")
                    : null;
                _rfTbcOutput = audioOptions.WriteRfTbc
                    ? createOutput(session.OutputBase + ".tbc.ldf")
                    : null;
                _ac3Output = audioOptions.Ac3
                    ? createOutput(session.OutputBase + ".ac3")
                    : null;
                _ac3Filter = _ac3Output is not null
                    ? new LaserDiscAc3Filter(session.Filters.LdAc3
                        ?? throw new InvalidOperationException("LD AC3 output was enabled but no AC3 filter was configured."))
                    : null;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public TbcDecodedField Write(TbcDecodedField field)
        {
            TbcDecodedField prepared = WriteBeforeMetadata(field);
            WriteAfterVideo(prepared);
            return prepared;
        }

        public TbcDecodedField WriteBeforeMetadata(TbcDecodedField field)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int efmTValueCount = 0;
            if (_efmOutput is not null)
            {
                if (field.Efm is null)
                {
                    throw new InvalidOperationException("LD digital audio output was enabled but the decoded field did not contain EFM samples.");
                }

                if (_preEfmOutput is not null)
                {
                    WriteInt16LittleEndian(_preEfmOutput, field.Efm);
                }

                byte[] efmTValues = _pll.Process(field.Efm);
                _efmOutput.Write(efmTValues, 0, efmTValues.Length);
                efmTValueCount = efmTValues.Length;
            }

            int audioSampleCount = _pcmOutput is not null && field.AudioPcm is not null
                ? field.AudioPcm.Length / 2
                : 0;
            return field with
            {
                EfmTValueCount = efmTValueCount,
                AudioSampleCount = audioSampleCount
            };
        }

        public void WriteAfterVideo(TbcDecodedField field)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_rfTbcOutput is not null)
            {
                if (field.RfTbc is null)
                {
                    throw new InvalidOperationException("LD RF_TBC output was enabled but the decoded field did not contain RF_TBC samples.");
                }

                WriteInt16LittleEndian(_rfTbcOutput, field.RfTbc);
            }

            if (_ac3Output is not null)
            {
                if (field.RfTbc is null)
                {
                    throw new InvalidOperationException("LD AC3 output was enabled but the decoded field did not contain RF_TBC samples.");
                }

                byte[] ac3Bytes = _ac3Filter!.Process(field.RfTbc);
                _ac3Output.Write(ac3Bytes, 0, ac3Bytes.Length);
            }

            if (_pcmOutput is not null && field.AudioPcm is not null)
            {
                WriteInt16LittleEndian(_pcmOutput, field.AudioPcm);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pcmOutput?.Dispose();
            _efmOutput?.Dispose();
            _rfTbcOutput?.Dispose();
            _ac3Output?.Dispose();
            _preEfmOutput?.Dispose();
        }
    }
}
