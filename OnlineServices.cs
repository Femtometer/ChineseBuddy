using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace ChineseBuddy;

public static class TokenStore
{
    private static readonly string PathName = Path.Combine(AppContext.BaseDirectory, "ocr-token.dat");
    public static string Load()
    {
        var environmentToken = Environment.GetEnvironmentVariable("PaddleOCR_TOKEN");
        if (!string.IsNullOrWhiteSpace(environmentToken)) return environmentToken.Trim();
        environmentToken = Environment.GetEnvironmentVariable("PaddleOCR_TOKEN", EnvironmentVariableTarget.User);
        if (!string.IsNullOrWhiteSpace(environmentToken)) return environmentToken.Trim();
        environmentToken = Environment.GetEnvironmentVariable("PaddleOCR_TOKEN", EnvironmentVariableTarget.Machine);
        if (!string.IsNullOrWhiteSpace(environmentToken)) return environmentToken.Trim();
        return File.Exists(PathName) ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(PathName), null, DataProtectionScope.CurrentUser)) : "";
    }
    public static void Save(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) { if (File.Exists(PathName)) File.Delete(PathName); return; }
        File.WriteAllBytes(PathName, ProtectedData.Protect(Encoding.UTF8.GetBytes(token.Trim()), null, DataProtectionScope.CurrentUser));
    }
}

public sealed class OcrService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };
    private const string Jobs = "https://paddleocr.aistudio-app.com/api/v2/ocr/jobs";
    private static readonly object Payload = new { markdownIgnoreLabels = new[] { "header", "header_image", "footer", "footer_image", "number", "footnote", "aside_text" }, useDocOrientationClassify = false, useDocUnwarping = false, useLayoutDetection = false, useChartRecognition = false, useSealRecognition = false, useOcrForImageBlock = false, mergeTables = true, relevelTitles = true, layoutShapeMode = "rect", promptLabel = "spotting", repetitionPenalty = 1, temperature = 0, topP = 1, minPixels = 147384, maxPixels = 2822400, layoutNms = true, restructurePages = true };

    public static string FormatRunningStatus(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("extractProgress", out var progress) && progress.ValueKind == JsonValueKind.Object &&
            progress.TryGetProperty("totalPages", out var totalValue) && totalValue.ValueKind == JsonValueKind.Number && totalValue.TryGetInt32(out int total) && total > 0 &&
            progress.TryGetProperty("extractedPages", out var completedValue) && completedValue.ValueKind == JsonValueKind.Number && completedValue.TryGetInt32(out int completed) && completed >= 0)
            return $"OCR 识别中：{completed}/{total} 页";
        return "OCR 识别中…";
    }

    public async Task<string> RecognizeAsync(string path, string token, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("请设置 PaddleOCR_TOKEN 环境变量，或在设置中填写 PaddleOCR 访问令牌。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        var ct = deadline.Token;
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("PaddleOCR-VL-1.6"), "model");
        content.Add(new StringContent(JsonSerializer.Serialize(Payload)), "optionalPayload");
        using var file = File.OpenRead(path);
        content.Add(new StreamContent(file), "file", Path.GetFileName(path));
        using var request = new HttpRequestMessage(HttpMethod.Post, Jobs) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("bearer", token);
        progress?.Report("正在上传文件并创建识别任务…");
        using var response = await Http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var id = created.RootElement.GetProperty("data").GetProperty("jobId").GetString();
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("OCR 服务未返回任务编号。");
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            using var poll = new HttpRequestMessage(HttpMethod.Get, Jobs + "/" + Uri.EscapeDataString(id));
            poll.Headers.Authorization = new AuthenticationHeaderValue("bearer", token);
            using var result = await Http.SendAsync(poll, ct);
            await EnsureSuccess(result, ct);
            using var json = JsonDocument.Parse(await result.Content.ReadAsStringAsync(ct));
            var data = json.RootElement.GetProperty("data");
            var state = data.GetProperty("state").GetString();
            if (state == "failed") throw new InvalidOperationException("OCR 任务失败：" + (data.TryGetProperty("errorMsg", out var error) ? error.GetString() : "未知错误"));
            if (state == "pending") { progress?.Report("OCR 任务排队中…"); continue; }
            if (state == "running") { progress?.Report(FormatRunningStatus(data)); continue; }
            if (state != "done") throw new InvalidDataException("未知 OCR 状态：" + state);
            var url = data.GetProperty("resultUrl").GetProperty("jsonUrl").GetString();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("OCR 结果地址无效。");
            progress?.Report("正在下载识别结果…");
            var lines = (await Http.GetStringAsync(uri, ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var output = new StringBuilder();
            foreach (var line in lines)
            {
                using var document = JsonDocument.Parse(line);
                foreach (var page in document.RootElement.GetProperty("result").GetProperty("layoutParsingResults").EnumerateArray())
                    if (page.TryGetProperty("markdown", out var md) && md.TryGetProperty("text", out var text)) output.AppendLine(text.GetString());
            }
            return output.ToString();
        }
    }
    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"OCR 服务返回 HTTP {(int)response.StatusCode}：{(response.StatusCode == System.Net.HttpStatusCode.Unauthorized ? "访问令牌无效或已过期" : response.ReasonPhrase)}");
        await Task.CompletedTask;
    }
}

public sealed class DictionaryService
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly HttpClient http;
    public DictionaryService(HttpClient? httpClient = null) => http = httpClient ?? SharedHttp;
    public static List<DictionaryLookupPlan> PlanLookups(IEnumerable<CharacterRecord> characters, bool replaceWords)
    {
        return characters.Select(c => new DictionaryLookupPlan(c.Character,
            string.IsNullOrWhiteSpace(c.Pinyin),
            replaceWords || c.Words.Count == 0 || c.Words.Any(w => !HasWordPinyin(w) || !w.Contains(c.Character))))
            .Where(plan => plan.NeedsPinyin || plan.NeedsWords).ToList();
    }

    private static bool HasWordPinyin(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return false;
        word = word.Trim();
        int opening = word.LastIndexOf('(');
        if (opening > 0 && word.EndsWith(')')) return !string.IsNullOrWhiteSpace(word[(opening + 1)..^1]);
        opening = word.LastIndexOf('（');
        return opening > 0 && word.EndsWith('）') && !string.IsNullOrWhiteSpace(word[(opening + 1)..^1]);
    }

    public static string GetPinyinUrl(string character)
    {
        if (!TextRules.IsSingleHan(character)) throw new ArgumentException("请输入单个汉字。", nameof(character));
        return "https://zdic.net/hans/" + Uri.EscapeDataString(character);
    }

    public static string ParsePinyin(string html, string character)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var title = doc.DocumentNode.SelectSingleNode("//div[contains(concat(' ',normalize-space(@class),' '),' char-meta ')]");
        if (title == null || HtmlEntity.DeEntitize(title.GetAttributeValue("content", "")) != character) return "";
        var values = title.SelectNodes(".//span[contains(concat(' ',normalize-space(@class),' '),' meta-pinyin ')]");
        return values == null ? "" : string.Join(" ", values.Select(v => Regex.Replace(HtmlEntity.DeEntitize(v.InnerText).Trim(), @"\s+", " ")).Where(v => v.Length > 0));
    }

    public static bool FillMissing(CharacterRecord item, string pinyin, IEnumerable<string> words)
    {
        bool changed = false;
        if (string.IsNullOrWhiteSpace(item.Pinyin) && !string.IsNullOrWhiteSpace(pinyin))
        {
            item.Pinyin = pinyin.Trim();
            changed = true;
        }
        var results = words.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim()).ToList();
        foreach (var result in results)
        {
            string word = WordName(result);
            int index = item.Words.FindIndex(existing => existing == word);
            if (index >= 0 && result != word && !item.Words.Contains(result, StringComparer.Ordinal)) { item.Words[index] = result; changed = true; }
        }
        foreach (var word in results)
        {
            if (item.Words.Count >= 3) break;
            string candidate = word.Trim();
            string name = WordName(candidate);
            if (string.IsNullOrWhiteSpace(name) || !name.Contains(item.Character) || item.Words.Any(existing => WordName(existing) == name)) continue;
            item.Words.Add(candidate);
            changed = true;
        }
        return changed;
    }

    private static string WordName(string word)
    {
        int opening = word.LastIndexOf('(');
        if (opening > 0 && word.EndsWith(')')) return word[..opening].Trim();
        opening = word.LastIndexOf('（');
        return opening > 0 && word.EndsWith('）') ? word[..opening].Trim() : word.Trim();
    }

    public static bool ApplySingleResult(CharacterRecord item, string pinyin, List<string> words, IReadOnlySet<string> blacklist)
    {
        bool changed = false;
        if (!string.IsNullOrWhiteSpace(pinyin) && item.Pinyin != pinyin)
        {
            item.Pinyin = pinyin;
            changed = true;
        }
        if (words.Count == 0) return changed;
        int removed = item.Words.RemoveAll(word => blacklist.Contains(WordName(word).Normalize(NormalizationForm.FormKC)));
        return FillMissing(item, "", words) || removed > 0 || changed;
    }

    public static bool ApplyBatchResult(CharacterRecord item, string pinyin, List<string> words, bool replaceWords)
    {
        if (!replaceWords || words.Count == 0) return FillMissing(item, pinyin, words);
        bool changed = FillMissing(item, pinyin, []);
        if (!item.Words.SequenceEqual(words)) { item.Words = [.. words]; changed = true; }
        return changed;
    }

    public static List<string> ParseWords(string html, string character, IReadOnlySet<string>? blacklist = null, Func<string, bool>? textbookContains = null)
    {
        if (!TextRules.IsSingleHan(character)) throw new ArgumentException("请输入单个汉字。", nameof(character));
        blacklist ??= WordBlacklist.Load();
        textbookContains ??= TextbookCorpus.Contains;
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var title = doc.DocumentNode.SelectSingleNode("//div[contains(concat(' ',normalize-space(@class),' '),' char-meta ')]");
        if (title == null || HtmlEntity.DeEntitize(title.GetAttributeValue("content", "")) != character) return [];
        var entries = doc.DocumentNode.SelectNodes("//div[contains(concat(' ',normalize-space(@class),' '),' xxjs-cizu ')]//a[contains(concat(' ',normalize-space(@class),' '),' xxjs-cizu__word ')]");
        if (entries == null) return [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<string>();
        var fallback = new List<string>();
        foreach (var entry in entries)
        {
            string word = HtmlEntity.DeEntitize(entry.InnerText).Trim();
            var runes = word.EnumerateRunes().ToArray();
            if (runes.Length is < 2 or > 4 || !word.Contains(character) || !runes.All(TextRules.IsHan) || !seen.Add(word)) continue;
            if ((runes.Length == 2 && runes[0] == runes[1]) || blacklist.Contains(word.Normalize(NormalizationForm.FormKC)) || !CommonCharacters.ContainsAll(runes)) continue;
            if (textbookContains(word)) results.Add(word);
            else if (runes.Length == 2) fallback.Add(word);
        }
        return results.Count > 0 ? results : fallback;
    }

    public Task<(string Pinyin, List<string> Words)> LookupAsync(string character, CancellationToken ct) => LookupAsync(character, ct, true, true);

    public async Task<(string Pinyin, List<string> Words)> LookupAsync(string character, CancellationToken ct, bool needsPinyin, bool needsWords)
    {
        if (!TextRules.IsSingleHan(character)) throw new ArgumentException("请输入单个汉字。", nameof(character));
        ct.ThrowIfCancellationRequested();
        if (!needsPinyin && !needsWords) return ("", []);
        try
        {
            var html = await http.GetStringAsync(GetPinyinUrl(character), ct).ConfigureAwait(false);
            string pinyin = needsPinyin ? ParsePinyin(html, character) : "";
            var words = new List<string>();
            if (needsWords)
            {
                foreach (var word in ParseWords(html, character).Take(3))
                {
                    try
                    {
                        var wordHtml = await http.GetStringAsync("https://zdic.net/hans/" + Uri.EscapeDataString(word), ct).ConfigureAwait(false);
                        string reading = ParseWordPinyin(wordHtml, word);
                        if (!string.IsNullOrWhiteSpace(reading)) words.Add($"{word}({reading})");
                    }
                    catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { if (ct.IsCancellationRequested) throw; }
                    if (words.Count == 3) break;
                }
            }
            return (pinyin, words);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { if (ct.IsCancellationRequested) throw; }
        return ("", []);
    }

    public static string ParseWordPinyin(string html, string word)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var title = doc.DocumentNode.SelectSingleNode("//div[contains(concat(' ',normalize-space(@class),' '),' word-headword-row ')]/span[contains(concat(' ',normalize-space(@class),' '),' dict-section__char ')]");
        if (title == null || HtmlEntity.DeEntitize(title.InnerText).Trim() != word) return "";
        var reading = doc.DocumentNode.SelectSingleNode("//div[contains(concat(' ',normalize-space(@class),' '),' word-pronun-row ')]//span[contains(concat(' ',normalize-space(@class),' '),' meta-pinyin ')]");
        return reading == null ? "" : Regex.Replace(HtmlEntity.DeEntitize(reading.InnerText).Trim(), @"\s+", " ");
    }
}

public sealed record DictionaryLookupPlan(string Character, bool NeedsPinyin, bool NeedsWords);
