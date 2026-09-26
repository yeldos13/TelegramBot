namespace AnikiChatBot.Modules.Media
{
    public enum MediaKind { Photo, Video }

    public class MediaItem
    {
        public MediaKind Kind { get; init; }

        public string? Url { get; init; }
        public string? FilePath { get; set; }

        public int? Width { get; init; }
        public int? Height { get; init; }
        public int? Duration { get; init; }

        public int Index { get; init; }
    }

    public interface IMediaSource
    {
        bool CanHandle(Uri uri);
        Task<List<MediaItem>> ExtractAsync(Uri uri, string workDir, CancellationToken ct);
    }
}
