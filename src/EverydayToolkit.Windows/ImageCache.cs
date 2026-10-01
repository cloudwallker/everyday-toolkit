using System.Windows.Media.Imaging;
namespace EverydayToolkit.Windows;

public sealed class ImageCache
{
    private readonly object gate = new();
    private readonly ImageCodec codec = new();
    private readonly Dictionary<Guid, (BitmapSource Image, LinkedListNode<Guid> Node)> thumbnails = [];
    private readonly LinkedList<Guid> recency = new();
    private long thumbnailBytes;
    private Guid? fullId;
    private BitmapSource? full;
    public int ThumbnailCount { get { lock(gate) return thumbnails.Count; } }
    public long ThumbnailBytes { get { lock(gate) return thumbnailBytes; } }
    public BitmapSource GetThumbnail(Guid id, Func<byte[]> loader)
    {
        lock(gate)
        {
            if (thumbnails.TryGetValue(id, out var cached)) { recency.Remove(cached.Node); recency.AddLast(cached.Node); return cached.Image; }
            var image = codec.Decode(loader()); long bytes = Bytes(image);
            // A caller may give full PNG data; the cache still enforces its memory budget.
            if (bytes > 16*1024*1024) return image;
            while (thumbnails.Count >= 100 || thumbnailBytes + bytes > 16*1024*1024) RemoveThumbnail(recency.First!.Value);
            thumbnails.Add(id, (image, recency.AddLast(id))); thumbnailBytes += bytes; return image;
        }
    }
    public BitmapSource GetFullImage(Guid id, Func<byte[]> loader)
    { lock(gate) { if(fullId==id && full!=null) return full; var image=codec.Decode(loader()); fullId=id; full=image; return image; } }
    public void Remove(Guid id) { lock(gate) { RemoveThumbnail(id); if(fullId==id) { full=null; fullId=null; } } }
    public void Clear() { lock(gate) { thumbnails.Clear(); recency.Clear(); thumbnailBytes=0; full=null; fullId=null; } }
    private void RemoveThumbnail(Guid id) { if(thumbnails.Remove(id,out var cached)) { recency.Remove(cached.Node); thumbnailBytes-=Bytes(cached.Image); } }
    private static long Bytes(BitmapSource image) => (long)image.PixelWidth * image.PixelHeight * 4;
}
