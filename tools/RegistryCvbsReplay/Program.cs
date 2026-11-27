using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VHSDecode.Preview;

CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
bool restore=bool.Parse(args[0]); string prefix=args[1];
var info=new ProcessStartInfo("ffmpeg"){UseShellExecute=false,RedirectStandardOutput=true};
if (args.Length == 4) foreach (var option in new[] { "-f", "s16le", "-ar", "48000", "-ac", "1" }) info.ArgumentList.Add(option);
foreach(var a in new[]{"-nostdin","-hide_banner","-loglevel","error","-xerror","-i",args[2],"-map","0:a:0","-f","s16le","pipe:1"}) info.ArgumentList.Add(a);
using var ff=Process.Start(info)!; using var input=ff.StandardOutput.BaseStream;
using var md5=IncrementalHash.CreateHash(HashAlgorithmName.MD5);
using var fields=File.CreateText(prefix+".csv"); using var locks=File.CreateText(prefix+".locks");
long position=args.Length == 4 ? long.Parse(args[3]) : 0, end=position, count=0, next=position; PreviewLockStatus? previous=null;
using var decoder=new RealtimePreviewSession(new(PreviewSourceFormat.Cvbs,PreviewVideoStandard.Ntsc,40_000_000){Cvbs50Ohms=restore,DecodeThreads=4},f=>{
    var pixels=f.Bgra.Span;
    long chroma=0;int sum=0,ns=0;
    for(int y=0;y<f.Height;y+=3) for(int x=0;x<f.Width;x+=4) {
        int i=(y*f.Width+x)*4,b=pixels[i],g=pixels[i+1],r=pixels[i+2];
        chroma+=Math.Max(r,Math.Max(g,b))-Math.Min(r,Math.Min(g,b));sum+=g;ns++;
    }
    bool colored=chroma!=0;
    if(!colored) for(int i=0;i<pixels.Length;i+=4) if(pixels[i]!=pixels[i+1]||pixels[i]!=pixels[i+2]){colored=true;break;}
    fields.WriteLine($"{f.Timestamp.Ticks},{end},{(f.IsTopField?1:0)},{(colored?1:0)},{(double)chroma/ns:F4},{(double)sum/ns:F3}");
    count++;
});
var samples=new short[65536];int n;var watch=Stopwatch.StartNew();
while((n=input.ReadAtLeast(MemoryMarshal.AsBytes(samples.AsSpan()),samples.Length*2,false))!=0){
    if(n%2!=0)throw new InvalidDataException();
    end=position+n/2; md5.AppendData(MemoryMarshal.AsBytes(samples.AsSpan())[..n]);decoder.Process(samples.AsSpan(0,n/2),position);position=end;
    var state=decoder.LockStatus;
    if(state!=previous){locks.WriteLine($"{end},{state.HorizontalLocked},{state.VerticalLocked},{state.ColorLocked}");previous=state;}
    if(position>=next){fields.Flush();locks.Flush();File.WriteAllText(prefix+".progress",$"{position} {count} {watch.Elapsed.TotalSeconds:F1}");next=position+200000000;}
}
decoder.Finish();ff.WaitForExit();if(ff.ExitCode!=0) throw new InvalidDataException($"ffmpeg {ff.ExitCode}");
File.WriteAllText(prefix+".done",$"{position} {count} {Convert.ToHexString(md5.GetHashAndReset())}");
