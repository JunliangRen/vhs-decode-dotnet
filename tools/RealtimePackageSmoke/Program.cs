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
var ld = new RealtimePreviewOptions(PreviewSourceFormat.LaserDisc, PreviewVideoStandard.Ntsc, 40_000_000)
{
    AudioTrack = PreviewAudioTrack.LaserDiscDts
};
using var dts = new RealtimePreviewSession(ld, _ => { }, _ => { });
dts.Process(new short[65_536], 0);
