using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace ShuShuShai.Core;
public record ExportProgress(int Done, int Total, string File, long Bytes, long FileLength, string Phase);
// Checkpoints are a fault-injection seam. Production leaves this null.
public sealed class ExportService(TaskStore store, Action<string, ExportFile>? checkpoint = null)
{
    public Task RunAsync(ExportPlan plan, IProgress<ExportProgress>? progress, CancellationToken ct) => Task.Run(async () =>
    {
        plan.Complete = false; store.SaveExport(plan);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!plan.RootCreated)
            {
                if (Directory.Exists(plan.Root) || File.Exists(plan.Root)) throw new IOException("目标结果目录已存在，请重新生成导出计划。");
                EnsureNoReparseParents(Path.GetDirectoryName(plan.Root)!);
                Directory.CreateDirectory(plan.Root);
                using (var marker = new FileStream(Path.Combine(plan.Root, ".shushushai-export"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(marker)) writer.Write(plan.Id);
                plan.RootCreated = true; store.SaveExport(plan);
            }
            else
            {
                var marker = Path.Combine(plan.Root, ".shushushai-export");
                if (!File.Exists(marker) || File.ReadAllText(marker) != plan.Id) throw new IOException("目标目录归属无法核验，已停止恢复。");
            }
            EnsureNoReparseParents(plan.Root);
            foreach (var group in plan.Files.GroupBy(f => f.GroupId))
            {
                ct.ThrowIfCancellationRequested();
                bool valid = true;
                foreach (var f in group)
                {
                    ct.ThrowIfCancellationRequested(); f.Error = null;
                    try { await CopyOne(f); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or CryptographicException)
                    { f.Error = ex.Message; plan.Messages.Add($"{f.Source}：{ex}"); valid = false; store.SaveExport(plan); }
                }
                if (!valid || !plan.Move) continue;
                // Keep verified destination handles open while removing the group sources.
                // Other processes cannot write/delete these copies between verification and removal.
                var targetLocks=new List<FileStream>();
                try
                {
                    foreach(var f in group)
                    {
                        var locked=new FileStream(f.Target,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,FileOptions.Asynchronous);targetLocks.Add(locked);
                        if(locked.Length!=f.Length||Convert.ToHexString(await SHA256.HashDataAsync(locked,ct))!=f.Sha256)throw new IOException($"目标文件在移除源文件前发生变化：{f.Target}");
                    }
                    foreach (var f in group)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (f.State == ExportFileState.SourceRemoved) continue;
                        try
                        {
                            checkpoint?.Invoke("before-remove", f); ct.ThrowIfCancellationRequested();
                            if (!File.Exists(f.Source))
                            {
                                // A crash can happen between deletion and journal update. Target was verified above.
                                plan.Messages.Add($"源文件已不在原位，保留已核验目标：{f.Source}");
                            }
                            else await RemoveVerifiedSource(f, ct);
                            f.State = ExportFileState.SourceRemoved; store.SaveExport(plan);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
                        { f.Error = "目标已校验，源文件保留：" + ex.Message; plan.Messages.Add($"{f.Source}：{ex}"); store.SaveExport(plan); break; }
                    }
                }
                finally{foreach(var locked in targetLocks)locked.Dispose();}
            }
            plan.Complete = plan.Files.All(f => f.Error == null && f.State >= (plan.Move ? ExportFileState.SourceRemoved : ExportFileState.Committed));
        }
        catch (OperationCanceledException) { plan.Messages.Add("用户取消：不再开始新的源文件移除操作，可稍后核验并恢复。"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        { plan.Messages.Add("导出中断：" + ex); }
        finally { store.SaveExport(plan); }

        async Task CopyOne(ExportFile f)
        {
            EnsureNoReparseParents(Path.GetDirectoryName(f.Target)!);
            Directory.CreateDirectory(Path.GetDirectoryName(f.Target)!);
            // Only a prepared journal entry with a recorded hash may recognize an existing target.
            if (File.Exists(f.Target))
            {
                if (f.State < ExportFileState.Prepared || f.Sha256 == null || !await Matches(f.Target, f, ct))
                    throw new IOException($"目标已存在且不能归属于本任务，绝不覆盖：{f.Target}");
                if (f.State < ExportFileState.Committed) { f.State = ExportFileState.Committed; store.SaveExport(plan); }
                progress?.Report(new(plan.Files.Count(x => x.State >= ExportFileState.Committed), plan.Files.Count, f.Target, f.Length, f.Length, "已核验结果")); return;
            }
            if (f.State == ExportFileState.SourceRemoved) throw new IOException($"已移动的目标文件缺失：{f.Target}。请恢复目标，不会尝试移除其他源文件。");
            if (f.State >= ExportFileState.Prepared && File.Exists(f.Temp) && await Matches(f.Temp, f, ct))
            {
                File.Move(f.Temp, f.Target, false); f.State = ExportFileState.Committed; store.SaveExport(plan); return;
            }
            // This exact temp path was allocated in the persisted plan. No directory cleanup occurs.
            if (File.Exists(f.Temp)) File.Delete(f.Temp);
            using var source = new FileStream(f.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var info = new FileInfo(f.Source);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length != f.Length || info.LastWriteTimeUtc != f.ModifiedUtc) throw new IOException("源文件自生成预览后发生变化，请重新生成计划。");
            f.Identity = NativeFiles.Identity(source.SafeFileHandle);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var target = new FileStream(f.Temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
            {
                byte[] buffer = new byte[1024 * 1024]; int read; long done = 0;
                while ((read = await source.ReadAsync(buffer, ct)) != 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct); hasher.AppendData(buffer, 0, read); done += read;
                    progress?.Report(new(plan.Files.Count(x => x.State >= ExportFileState.Committed), plan.Files.Count, f.Source, done, f.Length, "复制"));
                }
                await target.FlushAsync(ct); target.Flush(true);
            }
            f.Sha256 = Convert.ToHexString(hasher.GetHashAndReset());
            checkpoint?.Invoke("after-copy", f); ct.ThrowIfCancellationRequested();
            if (!await Matches(f.Temp, f, ct)) throw new IOException("复制长度或 SHA-256 不一致，源文件保留。");
            try { File.SetLastWriteTimeUtc(f.Temp, f.ModifiedUtc); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { plan.Messages.Add($"未能保留修改时间 {f.Target}：{ex.Message}"); }
            f.State = ExportFileState.Prepared; store.SaveExport(plan);
            checkpoint?.Invoke("before-commit", f); ct.ThrowIfCancellationRequested();
            File.Move(f.Temp, f.Target, false);
            checkpoint?.Invoke("after-commit", f);
            f.State = ExportFileState.Committed; store.SaveExport(plan);
            progress?.Report(new(plan.Files.Count(x => x.State >= ExportFileState.Committed), plan.Files.Count, f.Target, f.Length, f.Length, "已校验并提交"));
        }
    }, ct);

    public static async Task<bool> Matches(string path, ExportFile f, CancellationToken ct)
    {
        if (!File.Exists(path) || f.Sha256 == null) return false;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return stream.Length == f.Length && Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)) == f.Sha256;
    }
    private static async Task RemoveVerifiedSource(ExportFile f, CancellationToken ct)
    {
        // Open read + DELETE, deny writes/deletion by other handles, hash this handle, then delete THIS file by handle.
        // This avoids a path replacement race between verification and File.Delete.
        using var handle = NativeFiles.OpenForRemoval(f.Source);
        using var stream = new FileStream(handle, FileAccess.Read, 1024 * 1024, false);
        if (NativeFiles.Identity(handle) != f.Identity || stream.Length != f.Length || File.GetLastWriteTimeUtc(f.Source) != f.ModifiedUtc ||
            Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)) != f.Sha256) throw new IOException("源文件身份或内容已变化，未移除源文件。");
        ct.ThrowIfCancellationRequested(); NativeFiles.MarkDelete(handle);
    }
    public static void EnsureNoReparseParents(string path)
    {
        for (var d = new DirectoryInfo(path); d != null; d = d.Parent)
            if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException($"目标路径经过重解析目录，无法安全核验：{d.FullName}");
    }
    public static void ApplyMovedLocations(PhotoTask task, ExportPlan plan)
    {
        if (!plan.Move) return;
        foreach (var f in plan.Files.Where(f => f.State == ExportFileState.SourceRemoved))
        {
            var p = task.Photos.Find(p => p.Id == f.GroupId); if (p == null) continue;
            if (f.IsRaw) { p.OriginalRawPath ??= f.Source; p.RawPath = f.Target; } else { p.OriginalJpgPath ??= f.Source; p.JpgPath = f.Target; }
            if (p.RawPath != null) p.PairState = PairState.ConfirmedPair;
            else p.PairState = PairState.ConfirmedJpgOnly;
            var index = task.Files.FindIndex(x => PathRules.Same(x.Path, f.Source));
            if (index >= 0) task.Files[index] = new(f.Target, f.Length, f.ModifiedUtc, f.IsRaw);
        }
    }
}
internal static class NativeFiles
{
    [StructLayout(LayoutKind.Sequential)] private struct FileInfo
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle h, out FileInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFileInformationByHandle(SafeFileHandle h, int fileClass, ref int value, uint size);
    public static string Identity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return $"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}";
    }
    public static SafeFileHandle OpenForRemoval(string path)
    {
        var h = CreateFileW(path, 0x80010000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero); // OPEN_REPARSE_POINT: never follow a replaced link
        if (h.IsInvalid) { h.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); } return h;
    }
    public static void MarkDelete(SafeFileHandle handle)
    { int value = 1; if (!SetFileInformationByHandle(handle, 4, ref value, sizeof(int))) throw new Win32Exception(Marshal.GetLastWin32Error()); }
}
