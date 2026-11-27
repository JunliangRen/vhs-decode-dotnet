using VHSDecode.Preview;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

int rate=int.Parse(args[1]);
var samples=new short[new FileInfo(args[0]).Length/2];
using (var source=File.OpenRead(args[0])) source.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan()));
var options=new RealtimePreviewOptions(PreviewSourceFormat.LaserDisc,PreviewVideoStandard.Pal,rate);
if (!RealtimePreviewCapabilities.IsSupported(options) || RealtimePreviewCapabilities.IsSupported(options with {AudioTrack=PreviewAudioTrack.LaserDiscAnalog})) throw new Exception("capabilities");
var results=new List<object>();
var timings=new List<object>();
foreach(int threads in new[]{1,4}) {
    long pos=0;int phase=0;
    var fields=new List<object>();
    var states=new List<object>();
    PreviewLockStatus last=default;
    using var s=new RealtimePreviewSession(options with {DecodeThreads=threads}, f=>{
        var pix=f.Bgra.Span;
        int colored=0;
        for(int i=0;i<pix.Length;i+=64) if(Math.Max(pix[i],Math.Max(pix[i+1],pix[i+2]))-Math.Min(pix[i],Math.Min(pix[i+1],pix[i+2]))>20)colored++;
        if(fields.Count==20 && threads==1)File.WriteAllBytes($"artifacts/field-{rate}.bgra",pix.ToArray());
        fields.Add(new {phase,f.IsTopField,t=f.Timestamp.Ticks,f.Width,f.Height,f.FieldsPerSecond,colored,hash=Convert.ToHexString(SHA256.HashData(pix))});
    });
    void Feed(short[] data,int count) {
        for(int i=0;i<count;) {
            int n=Math.Min(rate/1000,count-i);
            s.Process(data.AsSpan(i,n),pos);pos+=n;i+=n;
            var cur=s.LockStatus;
            if(cur!=last){states.Add(new{phase,pos,cur});last=cur;}
        }
    }
    int dur=Math.Min(samples.Length,rate*2);
    Feed(samples,dur);
    phase=1;Feed(new short[rate/4],rate/4);
    if(s.LockStatus!=new PreviewLockStatus(false,false,false,null))throw new Exception("stale locks");
    phase=2;Feed(samples,dur);
    s.Finish();if(s.LockStatus!=new PreviewLockStatus(false,false,false,null))throw new Exception("finish");
    results.Add(new {threads,dur,fields,states});
}
using var proc=Process.GetCurrentProcess();
foreach(int threads in new[]{1,4,4,1,1,4,4,1}) {
    int fields=0;using var s=new RealtimePreviewSession(options with { DecodeThreads=threads },f=>fields++);
    int warm=rate/2;
    s.Process(samples.AsSpan(0,warm),0);
    if(s.LockStatus.ColorLocked!=true || s.LockStatus.VerticalLocked!=true)throw new Exception("warmup failed");
    fields=0;
    long n=samples.Length-warm;
    var c=proc.TotalProcessorTime;var clock=Stopwatch.StartNew();
    s.Process(samples.AsSpan(warm),warm);
    clock.Stop();double cpu=(proc.TotalProcessorTime-c).TotalSeconds;
    timings.Add(new{threads,fields,seconds=n/(double)rate,wall=clock.Elapsed.TotalSeconds,cpu,processors=Environment.ProcessorCount});
}
File.WriteAllText($"artifacts/measurement-{rate}.json",JsonSerializer.Serialize(new{results,timings}));
