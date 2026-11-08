using System.IO;
using OpenCvSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Fonts;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace ChineseBuddy;

public static class WorksheetService
{
    private const int Width = 1200, Height = 1697;
    private static double Px(double x) => x * 595.0 / Width;
    private static double Py(double y) => y * 842.0 / Height;

    public static Worksheet Create(IEnumerable<CharacterRecord> records) => new()
    {
        LayoutVersion = 4,
        Questions = records.Select((c, i) => new Question { Number = i + 1, Character = c.Character, Pinyin = c.Pinyin, Words = c.Words.ToList(), ReviewCountAtPrint = c.ReviewCount, ErrorCountAtPrint = c.ErrorCount }).ToList()
    };

    public static void GeneratePdf(Worksheet sheet, string path)
    {
        GlobalFontSettings.FontResolver ??= new ChineseFontResolver();
        using var document = new PdfDocument();
        var page = document.AddPage(); page.Width = XUnit.FromPoint(595); page.Height = XUnit.FromPoint(842);
        using var g = XGraphics.FromPdfPage(page);
        var small = new XFont("FZKai-Z03S", 8); var title = new XFont("FZKai-Z03S", 16, XFontStyleEx.Bold);
        g.DrawString("汉字复习练习纸", title, XBrushes.Black, new XPoint(Px(100), Py(95)));
        g.DrawString("姓名：________________　　日期：________________", small, XBrushes.Black, new XPoint(Px(100), Py(135)));
        foreach (var p in new[] { (60, 60), (1140, 60), (1140, 1637), (60, 1637) }) g.DrawRectangle(XBrushes.Black, Px(p.Item1 - 12), Py(p.Item2 - 12), Px(24), Py(24));
        var qr = new QRCodeWriter().encode(sheet.Id.ToString("N").ToUpperInvariant(), BarcodeFormat.QR_CODE, 150, 150, new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 2 });
        for (int y = 0; y < qr.Height; y++) for (int x = 0; x < qr.Width; x++) if (qr[x, y]) g.DrawRectangle(XBrushes.Black, Px(850 + x), Py(5 + y), Px(1.05), Py(1.05));
        if (sheet.LayoutVersion is not (1 or 2 or 3 or 4)) throw new InvalidDataException("不支持此练习纸版面。");
        foreach (var q in sheet.Questions)
        {
            if (sheet.LayoutVersion == 1) DrawLegacyQuestion(g, q, small);
            else DrawQuestion(g, q, sheet.LayoutVersion);
        }
        document.Save(path);
    }

    private static void DrawLegacyQuestion(XGraphics g, Question q, XFont small)
    {
        int y = 165 + (q.Number - 1) * 145;
        var hints = new List<(string Text, string Font)>();
        foreach (var w in q.Words)
        {
            if (hints.Count > 0) hints.Add(("　", "FZKai-Z03S"));
            var (word, pinyin) = SplitWordForPrint(w.Replace(q.Character, "~"));
            hints.Add((word, "FZKai-Z03S"));
            if (pinyin != null) hints.Add((" " + pinyin, "SimSun"));
        }
        DrawFitted(g, $"{q.Number:D2}   {q.Pinyin}", 11, Px(100), Py(y + 35), Px(820), "SimSun");
        DrawMixedFitted(g, hints, 11, Px(100), Py(y + 78), Px(820));
        g.DrawString($"复习 {q.ReviewCountAtPrint} 次　　错误 {q.ErrorCountAtPrint} 次", small, XBrushes.Gray, new XPoint(Px(100), Py(y + 112)));
        g.DrawRectangle(XPens.Black, Px(960), Py(y), Px(115), Py(115));
        g.DrawLine(XPens.LightGray, Px(1017.5), Py(y), Px(1017.5), Py(y + 115));
        g.DrawLine(XPens.LightGray, Px(960), Py(y + 57.5), Px(1075), Py(y + 57.5));
    }

    private static (int X, int Y, int WordWidth) QuestionPosition(int version, int index) => version switch
    {
        2 => (95 + (index % 2) * 530, 176 + (index / 2) * 286, 270),
        3 => (70 + (index % 3) * 355, 176 + (index / 3) * 286, 120),
        4 => (70 + (index % 3) * 355, 176 + (index / 3) * 286, 180),
        _ => throw new InvalidDataException("不支持此练习纸版面。")
    };

    private static void DrawQuestion(XGraphics g, Question q, int version)
    {
        var (x, y, wordWidth) = QuestionPosition(version, q.Number - 1);
        const int header = 40, bodyHeight = 210;
        int writingSize = version == 4 ? 150 : 210;
        int writingY = y + header + (bodyHeight - writingSize) / 2;
        g.DrawRectangle(XPens.Black, Px(x), Py(y), Px(wordWidth + writingSize), Py(header + bodyHeight));
        g.DrawLine(XPens.Black, Px(x), Py(y + header), Px(x + wordWidth + writingSize), Py(y + header));
        g.DrawLine(XPens.Black, Px(x + wordWidth), Py(y + header), Px(x + wordWidth), Py(y + header + bodyHeight));
        if (version == 4) g.DrawRectangle(XPens.Black, Px(x + wordWidth), Py(writingY), Px(writingSize), Py(writingSize));
        int headerLeft = version is 3 or 4 ? 165 : wordWidth;
        DrawFitted(g, $"{q.Number:D2}  {q.Pinyin}", 10, Px(x + 10), Py(y + 27), Px(headerLeft - 20), "SimSun");
        DrawCentered(g, $"复习 {q.ReviewCountAtPrint}  错误 {q.ErrorCountAtPrint}", 8, x + headerLeft, y, wordWidth + writingSize - headerLeft, header);
        double rowHeight = (double)bodyHeight / q.Words.Count;
        for (int i = 0; i < q.Words.Count; i++)
        {
            if (i > 0) g.DrawLine(XPens.Black, Px(x), Py(y + header + i * rowHeight), Px(x + wordWidth), Py(y + header + i * rowHeight));
            DrawWord(g, q.Words[i].Replace(q.Character, "~"), x + 5, y + header + i * rowHeight, wordWidth - 10, rowHeight);
        }
        var guide = new XPen(XColors.LightGray, 0.5) { DashStyle = XDashStyle.Dash };
        g.DrawLine(guide, Px(x + wordWidth + writingSize / 2.0), Py(writingY), Px(x + wordWidth + writingSize / 2.0), Py(writingY + writingSize));
        g.DrawLine(guide, Px(x + wordWidth), Py(writingY + writingSize / 2.0), Px(x + wordWidth + writingSize), Py(writingY + writingSize / 2.0));
    }

    private static void DrawWord(XGraphics g, string text, double x, double y, double width, double height)
    {
        var (word, pinyin) = SplitWordForPrint(text);
        if (pinyin != null)
        {
            DrawCentered(g, word, 12, x, y, width, height / 2);
            DrawCentered(g, pinyin, 10, x, y + height / 2, width, height / 2, "SimSun");
        }
        else DrawCentered(g, word, 12, x, y, width, height);
    }

    public static (string Word, string? Pinyin) SplitWordForPrint(string text)
    {
        int opening = text.LastIndexOf('(');
        bool hasPinyin = opening > 0 && text.EndsWith(')');
        if (!hasPinyin)
        {
            opening = text.LastIndexOf('（');
            hasPinyin = opening > 0 && text.EndsWith('）');
        }
        return hasPinyin ? (text[..opening].Trim(), text[(opening + 1)..^1].Trim()) : (text, null);
    }

    private static void DrawCentered(XGraphics g, string text, double fontSize, double x, double y, double width, double height, string family = "FZKai-Z03S")
    {
        double size = Math.Min(fontSize, Py(height) * 0.7);
        var font = new XFont(family, size);
        double measured = g.MeasureString(text, font).Width;
        if (measured > Px(width)) { size *= Px(width) / measured; font = new XFont(family, size); }
        if (size < 5) throw new InvalidDataException("词语数量过多或内容过长，无法清晰打印，请编辑后重试。");
        g.DrawString(text, font, XBrushes.Black, new XRect(Px(x), Py(y), Px(width), Py(height)), XStringFormats.Center);
    }

    private static void DrawFitted(XGraphics graphics, string text, double fontSize, double x, double y, double width, string family)
    {
        var font = new XFont(family, fontSize);
        var measured = graphics.MeasureString(text, font).Width;
        if (measured > width)
        {
            var fitted = fontSize * width / measured;
            if (fitted < 7) throw new InvalidDataException("拼音或词语过长，无法清晰打印，请编辑并缩短后重试。");
            font = new XFont(family, fitted);
        }
        graphics.DrawString(text, font, XBrushes.Black, new XPoint(x, y));
    }

    private static void DrawMixedFitted(XGraphics graphics, List<(string Text, string Font)> parts, double size, double x, double y, double width)
    {
        double measured = parts.Sum(part => graphics.MeasureString(part.Text, new XFont(part.Font, size)).Width);
        if (measured > width) size *= width / measured;
        if (size < 7) throw new InvalidDataException("拼音或词语过长，无法清晰打印，请编辑并缩短后重试。");
        foreach (var (text, family) in parts)
        {
            var font = new XFont(family, size);
            graphics.DrawString(text, font, XBrushes.Black, new XPoint(x, y));
            x += graphics.MeasureString(text, font).Width;
        }
    }

    public static int GetPageCount(string file) => Path.GetExtension(file).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? PDFtoImage.Conversion.GetPageCount(File.ReadAllBytes(file)) : 1;

    public static ScanResult Scan(string path, int page, Func<Guid, int?> resolveLayout)
    {
        byte[] bytes;
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = new MemoryStream();
            PDFtoImage.Conversion.SavePng(stream, File.ReadAllBytes(path), page: page, options: new PDFtoImage.RenderOptions(Dpi: 200));
            bytes = stream.ToArray();
        }
        else bytes = File.ReadAllBytes(path);
        using var image = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (image.Empty()) throw new InvalidDataException("无法读取扫描图片。");
        using var gray = new Mat(); using var threshold = new Mat();
        Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.Threshold(gray, threshold, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        Cv2.FindContours(threshold, out Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
        double area = image.Width * image.Height;
        var points = contours.Select(c => Cv2.BoundingRect(c)).Where(r => r.Width > 5 && r.Height > 5 && r.Width * r.Height > area * 0.00002 && r.Width * r.Height < area * 0.004 && (double)r.Width / r.Height is > 0.55 and < 1.8).Select(r => new Point2f(r.X + r.Width / 2f, r.Y + r.Height / 2f)).ToArray();
        if (points.Length < 4) throw new InvalidDataException("无法找到四个定位标记，请重新扫描并保留整张纸的四角。");
        var corners = new[] { new Point2f(0, 0), new Point2f(image.Width, 0), new Point2f(image.Width, image.Height), new Point2f(0, image.Height) };
        var source = corners.Select(c => points.MinBy(p => Math.Pow((p.X - c.X) / image.Width, 2) + Math.Pow((p.Y - c.Y) / image.Height, 2))).ToArray();
        if (source.Distinct().Count() != 4) throw new InvalidDataException("定位标记不完整，请重新扫描。");
        var target = new[] { new Point2f(60, 60), new Point2f(1140, 60), new Point2f(1140, 1637), new Point2f(60, 1637) };
        using var detector = new QRCodeDetector();
        for (int rotation = 0; rotation < 4; rotation++)
        {
            var rotated = Enumerable.Range(0, 4).Select(i => source[(i + rotation) % 4]).ToArray();
            using var matrix = Cv2.GetPerspectiveTransform(rotated, target);
            using var normalized = new Mat(); Cv2.WarpPerspective(image, normalized, matrix, new Size(Width, Height), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.White);
            using var qrRegion = new Mat(normalized, new Rect(800, 0, 260, 165));
            string id = detector.DetectAndDecode(qrRegion, out _);
            if (string.IsNullOrWhiteSpace(id))
            {
                using var qrGray = new Mat(); Cv2.CvtColor(qrRegion, qrGray, ColorConversionCodes.BGR2GRAY);
                var pixels = new byte[qrGray.Rows * qrGray.Cols]; System.Runtime.InteropServices.Marshal.Copy(qrGray.Data, pixels, 0, pixels.Length);
                var reader = new BarcodeReaderGeneric { AutoRotate = true, Options = new DecodingOptions { TryHarder = true, PossibleFormats = [BarcodeFormat.QR_CODE] } };
                id = reader.Decode(new RGBLuminanceSource(pixels, qrGray.Cols, qrGray.Rows, RGBLuminanceSource.BitmapFormat.Gray8))?.Text ?? "";
            }
            if (!Guid.TryParse(id, out var guid)) continue;
            int version = resolveLayout(guid) ?? throw new InvalidDataException("未找到此练习纸的原始记录。");
            if (version is not (1 or 2 or 3 or 4)) throw new InvalidDataException("不支持此练习纸版面。");
            var crops = new List<byte[]>();
            for (int i = 0; i < (version is 3 or 4 ? 15 : 10); i++)
            {
                Rect rect;
                if (version == 1) rect = new Rect(966, 165 + i * 145 + 6, 103, 103);
                else
                {
                    var (x, y, wordWidth) = QuestionPosition(version, i);
                    int writingSize = version == 4 ? 150 : 210;
                    int writingY = y + 40 + (210 - writingSize) / 2;
                    rect = new Rect(x + wordWidth + 8, writingY + 8, writingSize - 16, writingSize - 16);
                }
                using var crop = new Mat(normalized, rect);
                using var enlarged = new Mat(); Cv2.Resize(crop, enlarged, new Size(512, 512), interpolation: InterpolationFlags.Cubic);
                crops.Add(enlarged.ImEncode(".png"));
            }
            return new ScanResult(guid, crops);
        }
        throw new InvalidDataException("无法读取练习纸编号，请确保四角和二维码清晰可见。");
    }
}

public sealed record ScanResult(Guid Id, List<byte[]> Crops);

internal sealed class ChineseFontResolver : IFontResolver
{
    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new(familyName.Equals("SimSun", StringComparison.OrdinalIgnoreCase) ? "simsun" : "fzkai-z03s");
    public byte[] GetFont(string faceName)
    {
        string familyName = faceName == "simsun" ? "SimSun" : "FZKai-Z03S";
        var family = System.Windows.Media.Fonts.SystemFontFamilies
            .FirstOrDefault(f => string.Equals(f.Source, familyName, StringComparison.OrdinalIgnoreCase));
        var typeface = family?.GetTypefaces().FirstOrDefault();
        if (typeface == null || !typeface.TryGetGlyphTypeface(out var glyph))
            throw new FileNotFoundException($"未找到字体 {familyName}，请安装该字体后重试。");

        // Provide PDFsharp with a standalone font, including all available glyphs.
        var glyphs = Enumerable.Range(0, glyph.GlyphCount).Select(i => checked((ushort)i)).ToArray();
        return glyph.ComputeSubset(glyphs);
    }
}
