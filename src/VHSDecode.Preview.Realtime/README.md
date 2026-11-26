# VHSDecode.Preview.Realtime

Windows x64 streaming RF preview for .NET 11. Reference this package and publish your host for `win-x64`. Install the .NET 11 runtime on the host when using framework-dependent publishing. The package includes the native decoder, with FFmpeg DCA and liba52 statically linked. No FFmpeg executable is needed.

```csharp
using VHSDecode.Preview;

var options = new RealtimePreviewOptions(PreviewSourceFormat.Betamax, PreviewVideoStandard.Ntsc, 40_000_000)
{
    AudioTrack = PreviewAudioTrack.BetamaxHiFi,
    DecodeThreads = 4
};
using var preview = new RealtimePreviewSession(options, OnField, OnAudio);
preview.Process(samples, firstSourceSample);
```

Supply signed 16-bit mono RF and the absolute sample index of each block. Call the session serially on a worker thread. Both callbacks run on that thread and receive owned data. Use the field timestamp and parity for interlaced BGRA, and the audio block presentation timestamp for interleaved stereo PCM. A discontinuous sample index triggers reacquisition.
