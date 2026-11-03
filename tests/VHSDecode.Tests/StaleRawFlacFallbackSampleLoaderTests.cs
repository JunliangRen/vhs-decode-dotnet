using VHSDecode.Core.Rf;
using Xunit;

namespace VHSDecode.Tests;

public sealed class StaleRawFlacFallbackSampleLoaderTests
{
    [Fact(DisplayName = "LD indexed fallback accepts only a validated wrapped 32-bit sample count")]
    public void EligibilityRequiresWrapped32BitCountAndFixedNativeMetadata()
    {
        var info = new RawFlacStreamInfo(
            SampleRateHz: 40_000,
            Channels: 1,
            BitsPerSample: 16,
            TotalSamples: 190_840_832,
            MinimumBlockSize: 2_048,
            MaximumBlockSize: 2_048,
            HasSeekTable: false,
            MetadataChainComplete: true,
            UsesFixedBlockingStrategy: true);
        const long actual = 107_565_023_232;
        Assert.True(StaleRawFlacFallbackSampleLoader.HasWrappedSampleCount(info, actual));
        foreach (RawFlacStreamInfo unsupported in new[]
        {
            info with { SampleRateHz = 44_100 },
            info with { Channels = 2 },
            info with { BitsPerSample = 24 },
            info with { TotalSamples = null },
            info with { TotalSamples = 0 },
            info with { TotalSamples = actual },
            info with { TotalSamples = actual + 1 },
            info with { TotalSamples = 190_840_831 },
            info with { MaximumBlockSize = 4_096 },
            info with { HasSeekTable = true },
            info with { MetadataChainComplete = false },
            info with { UsesFixedBlockingStrategy = false }
        })
        {
            Assert.False(StaleRawFlacFallbackSampleLoader.HasWrappedSampleCount(unsupported, actual));
        }
        Assert.False(StaleRawFlacFallbackSampleLoader.HasWrappedSampleCount(info, info.TotalSamples!.Value));
        Assert.False(StaleRawFlacFallbackSampleLoader.HasWrappedSampleCount(info, 200_000_000));
        Assert.False(StaleRawFlacFallbackSampleLoader.HasWrappedSampleCount(info, actual - 1));
    }

    [Fact(DisplayName = "LD fallback probes once on first nonempty read and owns its selected decoder")]
    public void LazySelectionHappensOnceAndDisposesSelectedDecoder()
    {
        int creations = 0;
        var selected = new RecordingLoader();
        using (var loader = new StaleRawFlacFallbackSampleLoader(() =>
        {
            creations++;
            return selected;
        }))
        {
            Assert.Empty(loader.Read(Stream.Null, 0, 0)!);
            Assert.Equal(0, creations);
            Assert.Equal([123.0, 2.0], Assert.IsType<double[]>(loader.Read(Stream.Null, 123, 2)));
            Assert.Equal([41.0, 3.0], Assert.IsType<double[]>(loader.Read(Stream.Null, 41, 3)));
            Assert.Equal(1, creations);
            Assert.Equal(2, selected.ReadCount);
            Assert.Equal(0, selected.DisposeCount);
        }
        Assert.Equal(1, selected.DisposeCount);

        var unopened = new StaleRawFlacFallbackSampleLoader(() =>
        {
            creations++;
            return new RecordingLoader();
        });
        unopened.Dispose();
        unopened.Dispose();
        Assert.Equal(1, creations);
        Assert.Throws<ObjectDisposedException>(() => unopened.Read(Stream.Null, 0, 1));
    }

    [Fact(DisplayName = "LD fallback retains selected decoder errors without probing again")]
    public void SelectedDecoderErrorsPropagateWithoutRepeatedSelection()
    {
        int creations = 0;
        var error = new IOException("decoder failure");
        var selected = new RecordingLoader { Error = error };
        using var loader = new StaleRawFlacFallbackSampleLoader(() =>
        {
            creations++;
            return selected;
        });
        Assert.Same(error, Assert.Throws<IOException>(() => loader.Read(Stream.Null, 0, 1)));
        Assert.Same(error, Assert.Throws<IOException>(() => loader.Read(Stream.Null, 100, 1)));
        Assert.Equal(1, creations);
        Assert.Equal(2, selected.ReadCount);
    }

    private sealed class RecordingLoader : IRfSampleLoader, IDisposable
    {
        public int ReadCount { get; private set; }
        public int DisposeCount { get; private set; }
        public IOException? Error { get; init; }

        public double[]? Read(Stream stream, long sample, int readLength)
        {
            ReadCount++;
            if (Error is not null)
            {
                throw Error;
            }
            return [(double)sample, (double)readLength];
        }

        public void Dispose() => DisposeCount++;
    }
}
