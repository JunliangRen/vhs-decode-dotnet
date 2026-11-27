using VHSDecode.Preview;

foreach (int threads in new[] { 4, 8 })
{
    long audioFrames = 0;
    var options = new RealtimePreviewOptions(PreviewSourceFormat.Betamax, PreviewVideoStandard.Ntsc, 40_000_000)
    {
        AudioTrack = PreviewAudioTrack.BetamaxHiFi,
        DecodeThreads = threads
    };
    using var session = new RealtimePreviewSession(options, _ => throw new InvalidDataException("Field on silence"),
        block => audioFrames += block.Frames);
    session.Process(new short[400_000], 0);
    if (audioFrames != 480)
        throw new InvalidDataException($"Expected 480 stereo frames, received {audioFrames}.");
}
var palLd = new RealtimePreviewOptions(PreviewSourceFormat.LaserDisc, PreviewVideoStandard.Pal, 40_000_000);
if (!RealtimePreviewCapabilities.IsSupported(palLd)
    || !RealtimePreviewCapabilities.IsSupported(palLd with { AudioTrack = PreviewAudioTrack.LaserDiscAnalog })
    || !RealtimePreviewCapabilities.IsSupported(palLd with { AudioTrack = PreviewAudioTrack.LaserDiscDigitalPcm })
    || RealtimePreviewCapabilities.IsSupported(palLd with { AudioTrack = PreviewAudioTrack.LaserDiscDts }))
    throw new InvalidDataException("Unexpected PAL LD capabilities.");
using (var session = new RealtimePreviewSession(palLd, _ => throw new InvalidDataException("Field on silence")))
{
    session.Process(new short[65_536], 0);
    if (session.LockStatus != new PreviewLockStatus(false, false, false, null))
        throw new InvalidDataException("PAL LD unexpectedly locked on silence.");
}
// Exercise both PAL carriers after a reset, which must preserve the audio standard.
var analog = palLd with { AudioTrack = PreviewAudioTrack.LaserDiscAnalog, InputSampleRateHz = 20_000_000 };
var analogPcm = new List<float>();
using (var session = new RealtimePreviewSession(analog, _ => { },
    block => analogPcm.AddRange(block.InterleavedSamples.ToArray())))
{
    session.Reset();
    var rf = new short[analog.InputSampleRateHz / 20];
    for (int i = 0; i < rf.Length; i++)
    {
        double t = 2 * Math.PI * i / analog.InputSampleRateHz;
        rf[i] = (short)(8000 * (Math.Sin(683593.75 * t + 20 * Math.Sin(1000 * t))
                              + Math.Sin(1066406.25 * t + 10 * Math.Sin(2000 * t))));
    }
    session.Process(rf, 0);
    if (session.LockStatus.AudioLocked != true || analogPcm.Count != 2 * 2400)
        throw new InvalidDataException("Unexpected PAL LD audio lock or duration.");
    // The last 20 ms contain 20 cycles on the left and 40 on the right.
    int leftCycles = 0, rightCycles = 0;
    for (int i = analogPcm.Count - 2 * 960; i < analogPcm.Count; i += 2)
    {
        if (analogPcm[i - 2] <= 0 && analogPcm[i] > 0) leftCycles++;
        if (analogPcm[i - 1] <= 0 && analogPcm[i + 1] > 0) rightCycles++;
    }
    if (Math.Abs(leftCycles - 20) > 1 || Math.Abs(rightCycles - 40) > 1)
        throw new InvalidDataException("PAL LD failed to decode both analog carriers after reset.");
}
var ld = new RealtimePreviewOptions(PreviewSourceFormat.LaserDisc, PreviewVideoStandard.Ntsc, 40_000_000)
{
    AudioTrack = PreviewAudioTrack.LaserDiscDts
};
using var dts = new RealtimePreviewSession(ld, _ => { }, _ => { });
dts.Process(new short[65_536], 0);

foreach (bool pal in new[] { false, true })
{
    short[] samples = CvbsTestSignal.Create(pal);
    double baseline = samples[0];
    double alpha = 1 - Math.Exp(-2 * Math.PI * 6000 / 20_000_000);
    for (int i = 0; i < samples.Length; i++)
    {
        double ac = samples[i] - baseline;
        baseline += alpha * ac;
        samples[i] = (short)Math.Round(ac);
    }
    var options = new RealtimePreviewOptions(PreviewSourceFormat.Cvbs,
        pal ? PreviewVideoStandard.Pal : PreviewVideoStandard.Ntsc, 20_000_000) { Cvbs50Ohms = true };
    int fields = 0;
    bool? previousTop = null;
    using var session = new RealtimePreviewSession(options, field =>
    {
        if (field.IsTopField == previousTop || field.Height != (pal ? 288 : 240) || field.Bgra.IsEmpty)
            throw new InvalidDataException("Invalid CVBS field.");
        previousTop = field.IsTopField;
        fields++;
    });
    session.Process(samples, 0);
    if (fields < 4) throw new InvalidDataException($"Only {fields} CVBS fields on {options.Standard}.");
    Console.WriteLine($"{options.Standard} CVBS package preview produced {fields} fields.");
}
