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

For 20 or 40 MSPS signed 16-bit PAL or NTSC composite video, select
`PreviewSourceFormat.Cvbs` and pass only a field callback. Both regions and
thread budgets apply as for RF preview. Realtime file decoding exposes the
same preset on `PreviewFieldOptions`.

```csharp
var cvbs = new RealtimePreviewOptions(PreviewSourceFormat.Cvbs, PreviewVideoStandard.Ntsc, 40_000_000)
{
    Cvbs50Ohms = true
};
using var composite = new RealtimePreviewSession(cvbs, OnField);
```

`Cvbs50Ohms` defaults to false. Expose the option only for the CVBS source.
It enables low-frequency restoration and back-porch clamping for AC-coupled
DdD captures. The name follows the offline decoder preset.

Use `PreviewSourceFormat.SVhs` for PAL or NTSC S-VHS video at 20 or 40 MSPS.
For NTSC SuperBeta, select Betamax and `BetamaxVideoProfile.SuperBeta` or
`SuperBetaHiFi`. The HiFi profile supports video alone or simultaneous stereo
audio via `AudioTrack = PreviewAudioTrack.BetamaxHiFi`. For separate VHS
Hi-Fi RF, use the VHS source and VHS HiFi track with only an audio sink.
Both standards accept 8, 8.192, 10, 20 and 40 MSPS and output 48 kHz stereo.

Enumerate `RealtimePreviewCapabilities.SupportedCombinations` to populate a
source selector, or call `RealtimePreviewCapabilities.IsSupported(options)`
for a single configuration. These describe format support in the packaged
win-x64 decoder; querying them does not open a decoder or probe the host.

For 625/50 native SECAM VHS or MESECAM VHS, select `SecamVhs` or `MesecamVhs`
with `PreviewVideoStandard.Secam` at 20 or 40 MSPS. The explicit tape sources
also accept `Pal` for compatibility with hosts using 625/50 timing. NTSC ED
Beta uses the Betamax source and `BetamaxVideoProfile.EdBeta`, with optional
simultaneous `BetamaxHiFi` audio.

Set `IncludeSignalScopes` on the stream or Realtime file field options to
receive an owned `PreviewField.SignalYuv` buffer, three signed 16-bit
values per output pixel in Y, U, V order. Y is measured before the monitor
transfer function at 64 counts per IRE. U and V use 4096 counts per normalized
unit before RGB clipping. The field dimensions and crop match BGRA. The
default skips scope buffer allocation and returns an empty plane.

PAL LaserDisc video accepts 20 or 40 MSPS RF. Select `LaserDisc`, `Pal` and
`PreviewAudioTrack.LaserDiscAnalog` and supply field and audio sinks for concurrent
interlaced video and 48 kHz stereo PCM from the analog carriers. For discs with digital sound,
select `PreviewAudioTrack.LaserDiscDigitalPcm` to receive 44.1 kHz stereo from EFM/CIRC. Use `PreviewAudioTrack.None` for video alone. A session exposes
`LockStatus` for polling on a display thread. Its horizontal sync, vertical
sync, color and selected audio indicators describe the last completed RF
block, so a callback during processing can still see the previous snapshot.
Absent tracks report null. A reset or finish clears active locks. Audio lock
follows the selected decoder and can include single-channel analog reception.
