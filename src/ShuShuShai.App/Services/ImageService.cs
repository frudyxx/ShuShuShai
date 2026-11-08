using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShuShuShai.Core;

namespace ShuShuShai.App.Services;
public record PreviewResult(BitmapSource Image,string? ColorWarning=null,string? MetadataWarning=null,int OriginalWidth=0,int OriginalHeight=0)
{
    public string Warning=>string.Join("；",new[]{ColorWarning,MetadataWarning}.Where(s=>!string.IsNullOrEmpty(s)));
}
public sealed class ImageService : IDisposable
{
    public static ImageService Shared { get; }=new();
    private record CacheEntry(PreviewResult Result,long Bytes,long Tick);
    private record CacheMetadata(string? ColorWarning,string? MetadataWarning,int OriginalWidth,int OriginalHeight);
    private sealed class Work(string path,int width,string key)
    {
        public readonly string Path=path,Key=key;public readonly int Width=width;
        public int Consumers;
        public readonly TaskCompletionSource<PreviewResult> Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly object gate=new();
    private readonly Dictionary<string,CacheEntry> cache=[];
    private readonly Dictionary<string,Work> inflight=[];
    private readonly PriorityQueue<Work,int> queue=new();
    private readonly SemaphoreSlim signal=new(0),histogramWorker=new(1);
    private readonly CancellationTokenSource lifetime=new();
    private readonly Dictionary<string,HistogramData> histograms=[];
    private long used,tick,decodeCount;
    private const long Budget=192L*1024*1024;
    public string CacheDirectory {get;}
    public long DecodeCount=>Interlocked.Read(ref decodeCount);
    public event Action<string,string>? ReadFailed;
    public event Action<string,PreviewResult>? PreviewReady;
    public ImageService(string? cacheDirectory=null)
    {
        CacheDirectory=cacheDirectory??Path.Combine(UpdateInstallation.DataDirectory,"cache");
        for(int i=0;i<2;i++)_=Task.Run(Worker);
    }
    private static string Key(string path,int width){var f=new FileInfo(path);return $"v4|{f.FullName.ToUpperInvariant()}|{f.Length}|{f.LastWriteTimeUtc.Ticks}|{width}";}
    public string CacheFilePath(string path,int width)=>Path.Combine(CacheDirectory,Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Key(path,width))))+".png");
    public async Task<BitmapSource> LoadAsync(string path,int width,CancellationToken ct)=>(await LoadResultAsync(path,width,ct)).Image;
    public async Task<PreviewResult> LoadResultAsync(string path,int width,CancellationToken ct,int priority=0)
    {
        Work? work=null;
        try
        {
            ct.ThrowIfCancellationRequested();
            if(!PathRules.IsJpg(path))throw new IOException("RAW 只配对和搬运；预览仅支持 JPG。");
            string key=Key(path,width);PreviewResult? ready=null;
            lock(gate)
            {
                if(cache.TryGetValue(key,out var hit)){cache[key]=hit with{Tick=++tick};ready=hit.Result;}
                else
                {
                    if(!inflight.TryGetValue(key,out work))
                    {work=new(path,width,key);inflight[key]=work;queue.Enqueue(work,priority);signal.Release();}
                    work.Consumers++;
                }
            }
            var result=ready??await work!.Completion.Task.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();PreviewReady?.Invoke(path,result);return result;
        }
        catch(Exception ex) when(ex is not OperationCanceledException){ReadFailed?.Invoke(path,ex.Message);throw;}
        finally{if(work!=null)lock(gate)work.Consumers--;}
    }
    private async Task Worker()
    {
        while(!lifetime.IsCancellationRequested)
        {
            try{await signal.WaitAsync(lifetime.Token);}catch(OperationCanceledException){return;}
            Work job;
            lock(gate)
            {
                if(!queue.TryDequeue(out job!,out _))continue;
                if(job.Consumers==0){inflight.Remove(job.Key);job.Completion.TrySetCanceled();continue;}
            }
            try
            {
                var result=Decode(job.Path,job.Width);long bytes=(long)result.Image.PixelWidth*result.Image.PixelHeight*4;
                lock(gate)
                {
                    while(used+bytes>Budget&&cache.Count>0){var oldest=cache.MinBy(p=>p.Value.Tick);used-=oldest.Value.Bytes;cache.Remove(oldest.Key);}
                    if(bytes<=Budget&&!lifetime.IsCancellationRequested){cache[job.Key]=new(result,bytes,++tick);used+=bytes;}
                    inflight.Remove(job.Key);
                }
                job.Completion.TrySetResult(result);
            }
            catch(Exception ex){lock(gate)inflight.Remove(job.Key);job.Completion.TrySetException(ex);}
        }
    }
    private static bool Recoverable(Exception ex)=>ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException or FormatException or COMException;
    private PreviewResult Decode(string path,int width)
    {
        string? disk=width>0&&width<=640?CacheFilePath(path,width):null;
        if(disk!=null&&File.Exists(disk)&&File.Exists(disk+".json"))
        {
            try
            {
                var metadata=JsonSerializer.Deserialize<CacheMetadata>(File.ReadAllText(disk+".json"))??throw new InvalidDataException("预览缓存信息缺失");
                using var stream=File.OpenRead(disk);var cached=new BitmapImage();cached.BeginInit();cached.CacheOption=BitmapCacheOption.OnLoad;cached.StreamSource=stream;cached.EndInit();cached.Freeze();
                return new(cached,metadata.ColorWarning,metadata.MetadataWarning,metadata.OriginalWidth,metadata.OriginalHeight);
            }
            catch(Exception ex) when(Recoverable(ex)||ex is JsonException){System.Diagnostics.Trace.WriteLine("重建预览缓存："+ex.Message);}
        }
        Interlocked.Increment(ref decodeCount);
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        var header=BitmapDecoder.Create(input,BitmapCreateOptions.DelayCreation|BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.None).Frames[0];
        int originalW=header.PixelWidth,originalH=header.PixelHeight,orientation=1;string? colorWarning=null,metadataWarning=null;
        try{if(header.Metadata is BitmapMetadata md&&md.GetQuery("/app1/ifd/{ushort=274}") is ushort value)orientation=value;}
        catch(Exception ex) when(Recoverable(ex)){metadataWarning="部分元数据无法读取";}
        System.Collections.ObjectModel.ReadOnlyCollection<ColorContext>? contexts=null;
        try{contexts=header.ColorContexts;}catch(Exception ex) when(Recoverable(ex)){colorWarning="色彩配置不兼容，预览颜色可能有偏差";}
        if((contexts==null||contexts.Count==0)&&HasEmbeddedProfile(input))colorWarning="色彩配置不兼容，预览颜色可能有偏差";
        input.Position=0;var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;
        image.CreateOptions=BitmapCreateOptions.IgnoreColorProfile|BitmapCreateOptions.PreservePixelFormat;image.StreamSource=input;
        if(width>0)image.DecodePixelWidth=Math.Min(originalW,Math.Max(1,(int)Math.Ceiling(originalW*Math.Min(1,width/(double)Math.Max(originalW,originalH)))));
        image.EndInit();image.Freeze();BitmapSource result=image;
        if(contexts is {Count:>0})
        {
            try{var converted=new ColorConvertedBitmap(result,contexts[0],new ColorContext(PixelFormats.Bgra32),PixelFormats.Bgra32);converted.Freeze();result=converted;}
            catch(Exception ex) when(Recoverable(ex)){colorWarning="色彩配置不兼容，预览颜色可能有偏差";System.Diagnostics.Trace.WriteLine(ex.Message);}
        }
        result=Orient(result,orientation);var preview=new PreviewResult(result,colorWarning,metadataWarning,orientation is >=5 and <=8?originalH:originalW,orientation is >=5 and <=8?originalW:originalH);
        if(disk!=null)WriteCache(disk,preview);
        return preview;
    }
    private static bool HasEmbeddedProfile(Stream stream)
    {
        long saved=stream.Position;
        try
        {
            stream.Position=0;if(stream.ReadByte()!=255||stream.ReadByte()!=216)return false;
            while(stream.Position<stream.Length)
            {
                if(stream.ReadByte()!=255)return false;int marker;do{marker=stream.ReadByte();}while(marker==255);
                if(marker is 218 or 217 or -1)return false;if(marker is >=208 and <=215)continue;
                int a=stream.ReadByte(),b=stream.ReadByte();if(a<0||b<0)return false;int length=(a<<8|b)-2;if(length<0||stream.Position+length>stream.Length)return false;
                if(marker==226&&length>=12){byte[] name=new byte[12];stream.ReadExactly(name);if(System.Text.Encoding.ASCII.GetString(name)=="ICC_PROFILE\0")return true;length-=12;}
                stream.Position+=length;
            }
            return false;
        }
        finally{stream.Position=saved;}
    }
    private void WriteCache(string disk,PreviewResult result)
    {
        string temporary=disk+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            Directory.CreateDirectory(CacheDirectory);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(result.Image));
            using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))encoder.Save(output);
            File.WriteAllText(temporary+".json",JsonSerializer.Serialize(new CacheMetadata(result.ColorWarning,result.MetadataWarning,result.OriginalWidth,result.OriginalHeight)));
            File.Move(temporary,disk,true);File.Move(temporary+".json",disk+".json",true);
        }
        catch(Exception ex) when(Recoverable(ex)){System.Diagnostics.Trace.WriteLine("预览缓存未写入："+ex.Message);}
        finally{foreach(var file in new[]{temporary,temporary+".json"})try{if(File.Exists(file))File.Delete(file);}catch(IOException){}catch(UnauthorizedAccessException){}}
    }
    public static BitmapSource Orient(BitmapSource source,int orientation)
    {
        Matrix matrix=orientation switch{2=>new(-1,0,0,1,0,0),3=>new(-1,0,0,-1,0,0),4=>new(1,0,0,-1,0,0),5=>new(0,1,1,0,0,0),6=>new(0,1,-1,0,0,0),7=>new(0,-1,-1,0,0,0),8=>new(0,-1,1,0,0,0),_=>Matrix.Identity};
        if(matrix.IsIdentity)return source;var transformed=new TransformedBitmap(source,new MatrixTransform(matrix));transformed.Freeze();return transformed;
    }
    public async Task<HistogramData> HistogramAsync(string path,CancellationToken ct)
    {
        if(!PathRules.IsJpg(path))throw new IOException("直方图仅允许读取 JPG。");string key=Key(path,-1);
        lock(gate)if(histograms.TryGetValue(key,out var hit))return hit;
        await histogramWorker.WaitAsync(ct);
        try{return await Task.Run(()=>
        {
            using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            var decoder=BitmapDecoder.Create(input,BitmapCreateOptions.IgnoreColorProfile|BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
            var rgb=new FormatConvertedBitmap(decoder.Frames[0],PixelFormats.Bgra32,null,0);var result=new HistogramData();byte[] row=new byte[rgb.PixelWidth*4];
            for(int y=0;y<rgb.PixelHeight;y++){ct.ThrowIfCancellationRequested();rgb.CopyPixels(new Int32Rect(0,y,rgb.PixelWidth,1),row,row.Length,0);for(int x=0;x<row.Length;x+=4){result.Blue[row[x]]++;result.Green[row[x+1]]++;result.Red[row[x+2]]++;}}
            result.Pixels=(long)rgb.PixelWidth*rgb.PixelHeight;lock(gate){if(histograms.Count>=32)histograms.Remove(histograms.Keys.First());histograms[key]=result;}return result;
        },ct);}finally{histogramWorker.Release();}
    }
    public void ClearCache()
    {
        lock(gate){cache.Clear();histograms.Clear();used=0;}
        if(!Directory.Exists(CacheDirectory))return;
        foreach(var path in Directory.EnumerateFiles(CacheDirectory))if(System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path),@"^[0-9A-F]{64}\.png(\.json)?$"))File.Delete(path);
    }
    public (int Count,long Bytes) MemoryUsage{get{lock(gate)return(cache.Count,used);}}
    public void Dispose(){lifetime.Cancel();lock(gate){foreach(var work in inflight.Values)work.Completion.TrySetCanceled();cache.Clear();used=0;}}
}
public class HistogramData
{
    public long[] Red {get;}=new long[256];public long[] Green {get;}=new long[256];public long[] Blue {get;}=new long[256];public long Pixels {get;set;}
}
