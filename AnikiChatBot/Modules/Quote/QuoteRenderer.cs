using System.Globalization;
using System.Text;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace AnikiChatBot.Modules.Quote
{
    public record QuoteCard(
        string Name,
        long ColorSeed,
        string? Text,
        byte[]? Avatar = null,
        byte[]? Media = null,
        string? ReplyName = null,
        string? ReplyText = null,
        SKColor? Background = null,
        float Scale = 1);

    public static class QuoteRenderer
    {
        public const int StickerSide = 512;
        public const int PngSide = 1024;
        public const int MaxTextLines = 30;
        private const int MaxStickerBytes = 500_000;
        private const float BubbleWidth = 420;

        private static readonly SKColor DefaultBackground = new(0x23, 0x2E, 0x3C);

        private static readonly SKColor[] NameColors =
        [
            new(0xE1, 0x70, 0x76), new(0xE0, 0xA0, 0x50), new(0x9B, 0x7F, 0xE0), new(0x5F, 0xB8, 0x5F),
            new(0x4F, 0xB5, 0xC9), new(0x4F, 0x9B, 0xE0), new(0xD9, 0x6F, 0xB0),
        ];

        public static byte[] RenderSticker(QuoteCard card)
        {
            using var image = Draw(card, StickerSide);
            byte[] data = Encode(image, SKEncodedImageFormat.Webp, 90);
            return data.Length <= MaxStickerBytes ? data : Encode(image, SKEncodedImageFormat.Webp, 60);
        }

        public static byte[] RenderPng(QuoteCard card)
        {
            using var image = Draw(card, PngSide);
            return Encode(image, SKEncodedImageFormat.Png, 100);
        }

        private static byte[] Encode(SKImage image, SKEncodedImageFormat format, int quality)
        {
            using var data = image.Encode(format, quality);
            return data.ToArray();
        }

        private static SKImage Draw(QuoteCard card, int side)
        {
            float s = card.Scale;
            float pad = 14 * s, gap = 8 * s, avatarSize = 44 * s, nameSize = 17 * s, textSize = 21 * s, replySize = 15 * s;
            float inner = BubbleWidth - 2 * pad;

            SKColor background = card.Background ?? DefaultBackground;
            bool light = background.Red * 0.299 + background.Green * 0.587 + background.Blue * 0.114 > 150;
            SKColor textColor = light ? new SKColor(0x1B, 0x1B, 0x1B) : SKColors.White;
            SKColor nameColor = NameColors[(int)(Math.Abs(card.ColorSeed) % NameColors.Length)];

            using var kit = new TextKit();
            var nameLines = kit.Layout(card.Name, kit.Bold, nameSize, inner, 1);
            var textLines = string.IsNullOrWhiteSpace(card.Text) ? [] : kit.Layout(card.Text, kit.Regular, textSize, inner, MaxTextLines);

            float replyInset = 10 * s;
            var replyNameLines = card.ReplyName != null ? kit.Layout(card.ReplyName, kit.Bold, replySize, inner - replyInset, 1) : [];
            var replyTextLines = card.ReplyText != null ? kit.Layout(card.ReplyText, kit.Regular, replySize, inner - replyInset, 1) : [];
            bool hasReply = card.ReplyName != null;

            using var media = Decode(card.Media);
            float mediaW = 0, mediaH = 0;
            if (media != null)
            {
                float fit = Math.Min(inner / media.Width, 320 * s / media.Height);
                mediaW = media.Width * fit;
                mediaH = media.Height * fit;
            }

            float contentWidth = new[]
            {
                Width(nameLines),
                Width(textLines),
                hasReply ? Width(replyNameLines) + replyInset : 0,
                hasReply ? Width(replyTextLines) + replyInset : 0,
                mediaW,
                60 * s,
            }.Max();

            float nameHeight = nameSize * 1.35f;
            float textLine = textSize * 1.3f;
            float replyLine = replySize * 1.35f;
            float replyHeight = hasReply ? replyLine * 2 + 6 * s : 0;
            float mediaHeight = media != null ? mediaH + 6 * s : 0;

            float bubbleW = contentWidth + 2 * pad;
            float bubbleH = pad * 2 + nameHeight + replyHeight + mediaHeight + textLines.Count * textLine;
            float width = avatarSize + gap + bubbleW;
            float height = Math.Max(avatarSize, bubbleH);

            float factor = Math.Min(side / Math.Max(width, height), side / 256f);
            int pixelW = Math.Min(side, (int)Math.Ceiling(width * factor));
            int pixelH = Math.Min(side, (int)Math.Ceiling(height * factor));

            using var surface = SKSurface.Create(new SKImageInfo(pixelW, pixelH, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(factor);

            DrawAvatar(canvas, kit, card, avatarSize, nameColor);

            float left = avatarSize + gap;
            using (var paint = new SKPaint { Color = background, IsAntialias = true })
                canvas.DrawRoundRect(new SKRect(left, 0, left + bubbleW, bubbleH), 18 * s, 18 * s, paint);

            float x = left + pad, y = pad;
            kit.DrawLine(canvas, nameLines[0], x, y, nameSize, nameColor);
            y += nameHeight;

            if (hasReply)
            {
                using (var bar = new SKPaint { Color = nameColor, IsAntialias = true })
                    canvas.DrawRoundRect(new SKRect(x, y + 2 * s, x + 3 * s, y + replyLine * 2), 1.5f * s, 1.5f * s, bar);

                kit.DrawLine(canvas, replyNameLines[0], x + replyInset, y, replySize, nameColor);
                if (replyTextLines.Count > 0)
                    kit.DrawLine(canvas, replyTextLines[0], x + replyInset, y + replyLine, replySize, textColor);
                y += replyHeight;
            }

            if (media != null)
            {
                using var mediaImage = SKImage.FromBitmap(media);
                var rect = new SKRect(x, y, x + mediaW, y + mediaH);
                canvas.Save();
                canvas.ClipRoundRect(new SKRoundRect(rect, 10 * s), antialias: true);
                canvas.DrawImage(mediaImage, rect, new SKSamplingOptions(SKCubicResampler.Mitchell));
                canvas.Restore();
                y += mediaHeight;
            }

            foreach (var line in textLines)
            {
                kit.DrawLine(canvas, line, x, y, textSize, textColor);
                y += textLine;
            }

            return surface.Snapshot();
        }

        private static void DrawAvatar(SKCanvas canvas, TextKit kit, QuoteCard card, float size, SKColor color)
        {
            float radius = size / 2;
            using var bitmap = Decode(card.Avatar);

            if (bitmap != null)
            {
                using var image = SKImage.FromBitmap(bitmap);
                var rect = new SKRect(0, 0, size, size);
                using var clip = new SKPath();
                clip.AddCircle(radius, radius, radius);
                canvas.Save();
                canvas.ClipPath(clip, antialias: true);
                canvas.DrawImage(image, rect, new SKSamplingOptions(SKCubicResampler.Mitchell));
                canvas.Restore();
                return;
            }

            using (var paint = new SKPaint { Color = color, IsAntialias = true })
                canvas.DrawCircle(radius, radius, radius, paint);

            var enumerator = StringInfo.GetTextElementEnumerator(card.Name);
            string initial = enumerator.MoveNext() ? enumerator.GetTextElement().ToUpperInvariant() : "?";
            float letterSize = size * 0.45f;
            var line = kit.Layout(initial, kit.Bold, letterSize, size, 1)[0];
            kit.DrawLine(canvas, line, radius - line.Sum(c => c.Width) / 2, radius - letterSize * 0.65f, letterSize, SKColors.White);
        }

        private static SKBitmap? Decode(byte[]? data)
        {
            if (data == null)
                return null;

            try
            {
                return SKBitmap.Decode(data);
            }
            catch (ArgumentNullException)
            {
                return null;
            }
        }

        private static float Width(List<List<Cell>> lines) =>
            lines.Count == 0 ? 0 : lines.Max(line => line.Sum(c => c.Width));

        internal static bool IsEmoji(string grapheme) =>
            grapheme.EnumerateRunes().Any(r => r.Value is 0xFE0F or (>= 0x2600 and <= 0x27BF) or (>= 0x1F000 and <= 0x1FAFF));

        private record Cell(string Text, SKTypeface Typeface, float Width);

        private sealed class TextKit : IDisposable
        {
            public SKTypeface Regular { get; } = SKTypeface.FromFamilyName("Segoe UI");
            public SKTypeface Bold { get; } = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold);
            private readonly SKTypeface _emoji = SKTypeface.FromFamilyName("Segoe UI Emoji");
            private readonly Dictionary<string, SKTypeface> _fallbacks = [];
            private readonly Dictionary<SKTypeface, SKShaper> _shapers = [];

            public List<List<Cell>> Layout(string text, SKTypeface main, float size, float maxWidth, int maxLines)
            {
                var lines = new List<List<Cell>>();
                bool truncated = false;

                foreach (string paragraph in text.Trim().Replace("\r", "").Split('\n'))
                {
                    var line = new List<Cell>();
                    float width = 0;
                    int lastSpace = -1;

                    var enumerator = StringInfo.GetTextElementEnumerator(paragraph);
                    while (enumerator.MoveNext())
                    {
                        string grapheme = enumerator.GetTextElement();
                        var cell = Measure(grapheme, main, size);

                        if (width + cell.Width > maxWidth && line.Count > 0)
                        {
                            bool atSpace = lastSpace >= 0;
                            int end = atSpace ? lastSpace : line.Count;
                            lines.Add(line[..end]);
                            line = atSpace ? line[(lastSpace + 1)..] : [];
                            width = line.Sum(c => c.Width);
                            lastSpace = -1;
                        }

                        if (grapheme == " ")
                            lastSpace = line.Count;

                        line.Add(cell);
                        width += cell.Width;
                    }

                    lines.Add(line);
                    if (lines.Count > maxLines)
                    {
                        truncated = true;
                        break;
                    }
                }

                if (lines.Count > maxLines)
                {
                    truncated = true;
                    lines.RemoveRange(maxLines, lines.Count - maxLines);
                }

                if (truncated)
                {
                    var last = lines[^1];
                    var dots = Measure("…", main, size);
                    while (last.Count > 0 && last.Sum(c => c.Width) + dots.Width > maxWidth)
                        last.RemoveAt(last.Count - 1);
                    last.Add(dots);
                }

                return lines;
            }

            public void DrawLine(SKCanvas canvas, List<Cell> line, float x, float top, float size, SKColor color)
            {
                using var paint = new SKPaint { Color = color, IsAntialias = true };
                float baseline = top + size;

                for (int i = 0; i < line.Count;)
                {
                    int start = i;
                    var run = new StringBuilder();
                    float runWidth = 0;
                    while (i < line.Count && line[i].Typeface == line[start].Typeface)
                    {
                        run.Append(line[i].Text);
                        runWidth += line[i].Width;
                        i++;
                    }

                    using var font = NewFont(line[start].Typeface, size);
                    canvas.DrawShapedText(Shaper(line[start].Typeface), run.ToString(), x, baseline, font, paint);
                    x += runWidth;
                }
            }

            private Cell Measure(string grapheme, SKTypeface main, float size)
            {
                var typeface = Pick(grapheme, main);
                using var font = NewFont(typeface, size);
                return new Cell(grapheme, typeface, Shaper(typeface).Shape(grapheme, font).Width);
            }

            private SKTypeface Pick(string grapheme, SKTypeface main)
            {
                if (IsEmoji(grapheme))
                    return _emoji;

                int codepoint = grapheme.EnumerateRunes().First().Value;
                if (main.ContainsGlyph(codepoint))
                    return main;

                var match = SKFontManager.Default.MatchCharacter(codepoint);
                if (match == null)
                    return main;

                if (_fallbacks.TryGetValue(match.FamilyName, out var known))
                {
                    match.Dispose();
                    return known;
                }

                _fallbacks[match.FamilyName] = match;
                return match;
            }

            private SKShaper Shaper(SKTypeface typeface)
            {
                if (!_shapers.TryGetValue(typeface, out var shaper))
                    _shapers[typeface] = shaper = new SKShaper(typeface);
                return shaper;
            }

            private static SKFont NewFont(SKTypeface typeface, float size) =>
                new(typeface, size) { Subpixel = true, Edging = SKFontEdging.Antialias };

            public void Dispose()
            {
                foreach (var shaper in _shapers.Values)
                    shaper.Dispose();
                foreach (var fallback in _fallbacks.Values)
                    fallback.Dispose();
                Regular.Dispose();
                Bold.Dispose();
                _emoji.Dispose();
            }
        }
    }
}
