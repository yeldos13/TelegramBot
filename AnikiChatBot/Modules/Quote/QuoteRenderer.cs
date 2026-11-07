using System.Globalization;
using System.Text;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace AnikiChatBot.Modules.Quote
{
    [Flags]
    public enum QuoteStyle
    {
        None = 0,
        Bold = 1,
        Italic = 2,
        Underline = 4,
        Strike = 8,
        Spoiler = 16,
        Mono = 32,
        Link = 64,
    }

    public record QuoteSpan(int Offset, int Length, QuoteStyle Style, byte[]? Image = null);

    public record QuoteCard(
        string Name,
        long ColorSeed,
        string? Text,
        byte[]? Avatar = null,
        byte[]? Media = null,
        string? ReplyName = null,
        string? ReplyText = null,
        SKColor? Background = null,
        float Scale = 1,
        IReadOnlyList<QuoteSpan>? Spans = null,
        bool CropMedia = false);

    public static class QuoteRenderer
    {
        public const int StickerSide = 512;
        public const int PngSide = 1024;
        public const int MaxTextLines = 30;
        private const int MaxStickerBytes = 500_000;
        private const float BubbleWidth = 420;
        private const float CropAspect = 9f / 16;

        private static readonly SKColor DefaultBackground = new(0x23, 0x2E, 0x3C);

        private static readonly SKColor[] NameColors =
        [
            new(0xE1, 0x70, 0x76), new(0xE0, 0xA0, 0x50), new(0x9B, 0x7F, 0xE0), new(0x5F, 0xB8, 0x5F),
            new(0x4F, 0xB5, 0xC9), new(0x4F, 0x9B, 0xE0), new(0xD9, 0x6F, 0xB0),
        ];

        public static byte[] RenderSticker(params QuoteCard[] cards)
        {
            using var image = Draw(cards, StickerSide);
            byte[] data = Encode(image, SKEncodedImageFormat.Webp, 90);
            return data.Length <= MaxStickerBytes ? data : Encode(image, SKEncodedImageFormat.Webp, 60);
        }

        public static byte[] RenderPng(params QuoteCard[] cards)
        {
            using var image = Draw(cards, PngSide);
            return Encode(image, SKEncodedImageFormat.Png, 100);
        }

        public static byte[] RenderPhoto(params QuoteCard[] cards)
        {
            using var image = Draw(cards, PngSide, SKColors.White);
            return Encode(image, SKEncodedImageFormat.Png, 100);
        }

        private static byte[] Encode(SKImage image, SKEncodedImageFormat format, int quality)
        {
            using var data = image.Encode(format, quality);
            return data.ToArray();
        }

        private sealed record Bubble(
            QuoteCard Card,
            bool Header,
            List<List<Cell>> NameLines,
            List<List<Cell>> TextLines,
            List<List<Cell>> ReplyNameLines,
            List<List<Cell>> ReplyTextLines,
            SKBitmap? Media,
            float MediaW,
            float MediaH,
            float Width,
            float Height);

        private static Bubble Measure(TextKit kit, QuoteCard card, bool header, int maxLines, float s)
        {
            float pad = 14 * s, inner = BubbleWidth - 2 * pad, nameSize = 17 * s, textSize = 21 * s, replySize = 15 * s, replyInset = 10 * s;

            var nameLines = header ? kit.Layout(card.Name, kit.Bold, nameSize, inner, 1) : [];
            var textLines = string.IsNullOrWhiteSpace(card.Text) ? [] : kit.Layout(card.Text, kit.Regular, textSize, inner, maxLines, card.Spans);
            var replyNameLines = card.ReplyName != null ? kit.Layout(card.ReplyName, kit.Bold, replySize, inner - replyInset, 1) : [];
            var replyTextLines = card.ReplyText != null ? kit.Layout(card.ReplyText, kit.Regular, replySize, inner - replyInset, 1) : [];
            bool hasReply = card.ReplyName != null;

            var media = Decode(card.Media);
            float mediaW = 0, mediaH = 0;
            if (media != null)
            {
                float fit = Math.Min(inner / media.Width, 320 * s / media.Height);
                mediaW = card.CropMedia ? inner : media.Width * fit;
                mediaH = card.CropMedia ? inner * CropAspect : media.Height * fit;
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

            float height = pad * 2
                + (header ? nameSize * 1.35f : 0)
                + (hasReply ? replySize * 1.35f * 2 + 6 * s : 0)
                + (media != null ? mediaH + 6 * s : 0)
                + textLines.Count * textSize * 1.3f;

            return new Bubble(card, header, nameLines, textLines, replyNameLines, replyTextLines, media, mediaW, mediaH, contentWidth + 2 * pad, height);
        }

        internal static SKRect CropSource(int width, int height, float aspect)
        {
            float cropHeight = Math.Min(height, width * aspect);
            float cropWidth = cropHeight / aspect;
            float x = (width - cropWidth) / 2, y = (height - cropHeight) / 2;
            return new SKRect(x, y, x + cropWidth, y + cropHeight);
        }

        private static SKImage Draw(QuoteCard[] cards, int side, SKColor? canvasColor = null)
        {
            float s = cards[0].Scale;
            float gap = 8 * s, avatarSize = 44 * s, bubbleGap = 5 * s;
            int maxLines = Math.Max(4, MaxTextLines / cards.Length);

            SKColor background = cards[0].Background ?? DefaultBackground;
            bool light = background.Red * 0.299 + background.Green * 0.587 + background.Blue * 0.114 > 150;
            SKColor textColor = light ? new SKColor(0x1B, 0x1B, 0x1B) : SKColors.White;

            using var kit = new TextKit { LinkColor = light ? new SKColor(0x1A, 0x6F, 0xC4) : new SKColor(0x6A, 0xB3, 0xF3) };
            var bubbles = cards
                .Select((card, i) => Measure(kit, card, i == 0 || card.Name != cards[i - 1].Name || card.ColorSeed != cards[i - 1].ColorSeed, maxLines, s))
                .ToList();

            try
            {
                float width = avatarSize + gap + bubbles.Max(b => b.Width);
                float height = bubbles.Sum(b => b.Height) + bubbleGap * (bubbles.Count - 1);

                float factor = Math.Min(side / Math.Max(width, height), side / 256f);
                int pixelW = Math.Min(side, (int)Math.Ceiling(width * factor));
                int pixelH = Math.Min(side, (int)Math.Ceiling(height * factor));

                using var surface = SKSurface.Create(new SKImageInfo(pixelW, pixelH, SKColorType.Rgba8888, SKAlphaType.Premul));
                var canvas = surface.Canvas;
                canvas.Clear(canvasColor ?? SKColors.Transparent);
                canvas.Scale(factor);

                float top = 0;
                foreach (var bubble in bubbles)
                {
                    DrawBubble(canvas, kit, bubble, avatarSize + gap, top, avatarSize, background, textColor, s);
                    top += bubble.Height + bubbleGap;
                }

                return surface.Snapshot();
            }
            finally
            {
                foreach (var bubble in bubbles)
                    bubble.Media?.Dispose();
            }
        }

        private static void DrawBubble(SKCanvas canvas, TextKit kit, Bubble bubble, float left, float top, float avatarSize, SKColor background, SKColor textColor, float s)
        {
            float pad = 14 * s, nameSize = 17 * s, textSize = 21 * s, replySize = 15 * s, replyInset = 10 * s;
            float replyLine = replySize * 1.35f;
            SKColor nameColor = NameColors[(int)(Math.Abs(bubble.Card.ColorSeed) % NameColors.Length)];

            if (bubble.Header)
            {
                canvas.Save();
                canvas.Translate(0, top);
                DrawAvatar(canvas, kit, bubble.Card, avatarSize, nameColor);
                canvas.Restore();
            }

            using (var paint = new SKPaint { Color = background, IsAntialias = true })
                canvas.DrawRoundRect(new SKRect(left, top, left + bubble.Width, top + bubble.Height), 18 * s, 18 * s, paint);

            float x = left + pad, y = top + pad;

            if (bubble.Header)
            {
                kit.DrawLine(canvas, bubble.NameLines[0], x, y, nameSize, nameColor);
                y += nameSize * 1.35f;
            }

            if (bubble.Card.ReplyName != null)
            {
                using (var bar = new SKPaint { Color = nameColor, IsAntialias = true })
                    canvas.DrawRoundRect(new SKRect(x, y + 2 * s, x + 3 * s, y + replyLine * 2), 1.5f * s, 1.5f * s, bar);

                kit.DrawLine(canvas, bubble.ReplyNameLines[0], x + replyInset, y, replySize, nameColor);
                if (bubble.ReplyTextLines.Count > 0)
                    kit.DrawLine(canvas, bubble.ReplyTextLines[0], x + replyInset, y + replyLine, replySize, textColor);
                y += replyLine * 2 + 6 * s;
            }

            if (bubble.Media != null)
            {
                using var mediaImage = SKImage.FromBitmap(bubble.Media);
                var rect = new SKRect(x, y, x + bubble.MediaW, y + bubble.MediaH);
                canvas.Save();
                canvas.ClipRoundRect(new SKRoundRect(rect, 10 * s), antialias: true);
                var source = bubble.Card.CropMedia
                    ? CropSource(bubble.Media.Width, bubble.Media.Height, CropAspect)
                    : new SKRect(0, 0, bubble.Media.Width, bubble.Media.Height);
                canvas.DrawImage(mediaImage, source, rect, new SKSamplingOptions(SKCubicResampler.Mitchell));
                canvas.Restore();
                y += bubble.MediaH + 6 * s;
            }

            foreach (var line in bubble.TextLines)
            {
                kit.DrawLine(canvas, line, x, y, textSize, textColor);
                y += textSize * 1.3f;
            }
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

        private record Cell(string Text, SKTypeface Typeface, float Width, QuoteStyle Style = QuoteStyle.None, SKBitmap? Image = null);

        private sealed class TextKit : IDisposable
        {
            public SKColor LinkColor { get; init; }
            public SKTypeface Regular { get; } = SKTypeface.FromFamilyName("Segoe UI");
            public SKTypeface Bold { get; } = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold);
            private readonly SKTypeface _italic = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Italic);
            private readonly SKTypeface _boldItalic = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.BoldItalic);
            private readonly SKTypeface _mono = SKTypeface.FromFamilyName("Consolas");
            private readonly SKTypeface _emoji = SKTypeface.FromFamilyName("Segoe UI Emoji");
            private readonly Dictionary<string, SKTypeface> _fallbacks = [];
            private readonly Dictionary<SKTypeface, SKShaper> _shapers = [];
            private readonly Dictionary<byte[], SKBitmap?> _images = [];

            public List<List<Cell>> Layout(string text, SKTypeface main, float size, float maxWidth, int maxLines, IReadOnlyList<QuoteSpan>? spans = null)
            {
                var lines = new List<List<Cell>>();
                bool truncated = false;

                string trimmedEnd = text.TrimEnd();
                int cursor = trimmedEnd.Length - trimmedEnd.TrimStart().Length;

                foreach (string paragraph in trimmedEnd[cursor..].Replace('\r', ' ').Split('\n'))
                {
                    var line = new List<Cell>();
                    float width = 0;
                    int lastSpace = -1;
                    int offset = cursor;
                    cursor += paragraph.Length + 1;

                    var enumerator = StringInfo.GetTextElementEnumerator(paragraph);
                    while (enumerator.MoveNext())
                    {
                        string grapheme = enumerator.GetTextElement();
                        int at = offset;
                        offset += grapheme.Length;

                        var covering = spans?.Where(sp => at >= sp.Offset && at < sp.Offset + sp.Length).ToList();
                        var style = covering?.Aggregate(QuoteStyle.None, (all, sp) => all | sp.Style) ?? QuoteStyle.None;
                        var imageSpan = covering?.FirstOrDefault(sp => sp.Image != null);
                        var bitmap = imageSpan != null ? Image(imageSpan.Image!) : null;

                        if (bitmap != null && at != imageSpan!.Offset)
                            continue;

                        var cell = bitmap != null
                            ? new Cell(grapheme, _emoji, size * 1.15f, style, bitmap)
                            : Measure(grapheme, Styled(main, style), size, style);

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
                float baseline = top + size;

                for (int i = 0; i < line.Count;)
                {
                    var first = line[i];
                    var run = new StringBuilder();
                    float runWidth = 0;
                    do
                    {
                        run.Append(line[i].Text);
                        runWidth += line[i].Width;
                        i++;
                    }
                    while (first.Image == null && i < line.Count && line[i].Image == null && line[i].Typeface == first.Typeface && line[i].Style == first.Style);

                    SKColor runColor = first.Style.HasFlag(QuoteStyle.Link) ? LinkColor : color;
                    using var paint = new SKPaint { Color = runColor, IsAntialias = true };

                    if (first.Style.HasFlag(QuoteStyle.Spoiler))
                    {
                        using var veil = new SKPaint { Color = color.WithAlpha(170), IsAntialias = true };
                        canvas.DrawRoundRect(new SKRect(x, baseline - size, x + runWidth, baseline + size * 0.28f), size * 0.15f, size * 0.15f, veil);
                    }
                    else if (first.Image != null)
                    {
                        using var image = SKImage.FromBitmap(first.Image);
                        canvas.DrawImage(image, new SKRect(x, baseline - size, x + size * 1.15f, baseline + size * 0.15f), new SKSamplingOptions(SKCubicResampler.Mitchell));
                    }
                    else
                    {
                        using var font = NewFont(first.Typeface, size);
                        canvas.DrawShapedText(Shaper(first.Typeface), run.ToString(), x, baseline, font, paint);

                        float thickness = Math.Max(1, size * 0.06f);
                        if (first.Style.HasFlag(QuoteStyle.Underline))
                            canvas.DrawRect(x, baseline + size * 0.12f, runWidth, thickness, paint);
                        if (first.Style.HasFlag(QuoteStyle.Strike))
                            canvas.DrawRect(x, baseline - size * 0.32f, runWidth, thickness, paint);
                    }

                    x += runWidth;
                }
            }

            private SKTypeface Styled(SKTypeface main, QuoteStyle style)
            {
                if (style.HasFlag(QuoteStyle.Mono))
                    return _mono;

                bool bold = main == Bold || style.HasFlag(QuoteStyle.Bold);
                bool italic = style.HasFlag(QuoteStyle.Italic);
                return bold && italic ? _boldItalic : bold ? Bold : italic ? _italic : Regular;
            }

            private SKBitmap? Image(byte[] data)
            {
                if (!_images.TryGetValue(data, out var bitmap))
                    _images[data] = bitmap = Decode(data);
                return bitmap;
            }

            private Cell Measure(string grapheme, SKTypeface main, float size, QuoteStyle style = QuoteStyle.None)
            {
                var typeface = Pick(grapheme, main);
                using var font = NewFont(typeface, size);
                return new Cell(grapheme, typeface, Shaper(typeface).Shape(grapheme, font).Width, style);
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
                foreach (var image in _images.Values)
                    image?.Dispose();
                Regular.Dispose();
                Bold.Dispose();
                _italic.Dispose();
                _boldItalic.Dispose();
                _mono.Dispose();
                _emoji.Dispose();
            }
        }
    }
}
