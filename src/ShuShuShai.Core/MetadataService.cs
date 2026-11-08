using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Jpeg;

namespace ShuShuShai.Core;
public record PhotoMetadata(string Text, DateTime? ShotTime, int Orientation, int Width, int Height);
public static class MetadataService
{
    public static PhotoMetadata Read(string path)
    {
        if (!PathRules.IsJpg(path)) throw new InvalidOperationException("仅允许读取 JPG 预览与元数据。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var data = ImageMetadataReader.ReadMetadata(stream);
        var ifd = data.OfType<ExifIfd0Directory>().FirstOrDefault();
        var sub = data.OfType<ExifSubIfdDirectory>().FirstOrDefault(); var jpeg = data.OfType<JpegDirectory>().FirstOrDefault();
        string Tag(MetadataExtractor.Directory? d, int t) => d?.GetDescription(t) ?? "—";
        DateTime? shot = sub != null && sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var date) ? date : null;
        int orientation = ifd != null && ifd.TryGetInt32(ExifDirectoryBase.TagOrientation, out var o) ? o : 1;
        int w = jpeg != null && jpeg.TryGetInt32(JpegDirectory.TagImageWidth, out var jw) ? jw : 0;
        int h = jpeg != null && jpeg.TryGetInt32(JpegDirectory.TagImageHeight, out var jh) ? jh : 0;
        return new($"相机   {Tag(ifd, ExifDirectoryBase.TagMake)} {Tag(ifd, ExifDirectoryBase.TagModel)}\n镜头   {Tag(sub, ExifDirectoryBase.TagLensModel)}\n拍摄   {(shot.HasValue ? shot.Value.ToString("yyyy-MM-dd HH:mm:ss") : "—")}\nISO   {Tag(sub, ExifDirectoryBase.TagIsoEquivalent)}\n光圈   {Tag(sub, ExifDirectoryBase.TagFNumber)}\n快门   {Tag(sub, ExifDirectoryBase.TagExposureTime)}\n焦距   {Tag(sub, ExifDirectoryBase.TagFocalLength)}\n尺寸   {w} × {h}\n大小   {stream.Length / 1048576.0:F2} MB", shot, orientation, w, h);
    }
}
