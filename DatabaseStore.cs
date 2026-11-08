using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;
using System.Text.Encodings.Web;

namespace ChineseBuddy;

public sealed class DatabaseStore
{
    public string PathName { get; }
    public DatabaseStore(string? path = null) => PathName = path ?? Path.Combine(AppContext.BaseDirectory, "ChineseBuddy.json");
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    public Database Load()
    {
        if (!File.Exists(PathName)) { var empty = new Database(); Save(empty); return empty; }
        return Read(PathName);
    }

    public Database RestoreBackup()
    {
        var backup = PathName + ".bak";
        var data = Read(backup);
        var temp = PathName + ".restore.tmp";
        try
        {
            File.Copy(backup, temp, true);
            if (File.Exists(PathName)) File.Replace(temp, PathName, PathName + ".corrupt-" + Guid.NewGuid().ToString("N"), true);
            else File.Move(temp, PathName);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return data;
    }

    private static Database Read(string path)
    {
        var db = JsonSerializer.Deserialize<Database>(File.ReadAllText(path, Encoding.UTF8), Options) ?? throw new InvalidDataException("数据库为空。");
        Validate(db);
        return db;
    }

    public static Database Clone(Database db) => JsonSerializer.Deserialize<Database>(JsonSerializer.Serialize(db, Options), Options)!;

    public void Save(Database db)
    {
        Validate(db);
        var temp = PathName + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, db, Options);
                stream.Flush(true);
            }
            if (File.Exists(PathName)) File.Replace(temp, PathName, PathName + ".bak", true);
            else File.Move(temp, PathName);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void Validate(Database db)
    {
        if (db.SchemaVersion != 1) throw new InvalidDataException($"不支持数据库版本 {db.SchemaVersion}。");
        if (db.Characters == null || db.Worksheets == null) throw new InvalidDataException("数据库缺少生字表或练习纸表。");
        if (db.Characters.Any(x => x == null || !TextRules.IsSingleHan(x.Character) || x.Pinyin == null || x.Words == null || x.Words.Any(w => w == null) || x.ReviewCount < 0 || x.ErrorCount < 0 || x.ErrorCount > x.ReviewCount) || db.Characters.Select(x => x.Character).Distinct().Count() != db.Characters.Count)
            throw new InvalidDataException("生字数据无效或存在重复。");
        if (db.Worksheets.Any(w => w == null || w.Id == Guid.Empty || w.LayoutVersion is not (1 or 2 or 3 or 4) || (w.InvalidatedAt != null && w.SubmittedAt != null) || w.Questions == null || w.Questions.Count < 1 || w.Questions.Count > (w.LayoutVersion is 3 or 4 ? 15 : 10) || w.Questions.Any(q => q == null || !TextRules.IsSingleHan(q.Character) || string.IsNullOrWhiteSpace(q.Pinyin) || q.Words == null || q.Words.Count < 1 || (w.LayoutVersion == 1 && q.Words.Count != 3) || q.Words.Any(x => string.IsNullOrWhiteSpace(x) || !x.Contains(q.Character)) || q.Words.Distinct().Count() != q.Words.Count || q.Number < 1 || q.Number > w.Questions.Count || q.ReviewCountAtPrint < 0 || q.ErrorCountAtPrint < 0 || q.ErrorCountAtPrint > q.ReviewCountAtPrint || (w.SubmittedAt != null && (q.IsCorrect == null || q.IsMasteredAfterReview == null)) || (w.SubmittedAt == null && (q.IsCorrect != null || q.IsMasteredAfterReview != null))) || w.Questions.Select(q => q.Number).Distinct().Count() != w.Questions.Count || w.Questions.Select(q => q.Character).Distinct().Count() != w.Questions.Count) || db.Worksheets.Select(w => w.Id).Distinct().Count() != db.Worksheets.Count)
            throw new InvalidDataException("练习纸数据无效或存在重复。");
        if (db.Worksheets.Where(w => w.SubmittedAt == null && w.InvalidatedAt == null).SelectMany(w => w.Questions).Any(q => !db.Characters.Any(c => c.Character == q.Character))) throw new InvalidDataException("未提交的练习纸引用了不存在的生字。");
    }
}

public static class TextRules
{
    public static bool IsHan(Rune r) => (r.Value >= 0x3400 && r.Value <= 0x4DBF) || (r.Value >= 0x4E00 && r.Value <= 0x9FFF) || (r.Value >= 0x20000 && r.Value <= 0x323AF);
    public static bool IsSingleHan(string? text) => text != null && text.EnumerateRunes().Count() == 1 && IsHan(text.EnumerateRunes().First());
    public static List<string> ExtractHan(string text) => text.EnumerateRunes().Where(IsHan).Select(r => r.ToString()).ToList();
    public static bool Ready(CharacterRecord c) => !string.IsNullOrWhiteSpace(c.Pinyin) && c.Words.Count >= 1 && c.Words.All(w => !string.IsNullOrWhiteSpace(w) && w.Contains(c.Character)) && c.Words.Distinct().Count() == c.Words.Count;
}

public static class SelectionService
{
    public static List<CharacterRecord> Pick(IEnumerable<CharacterRecord> records, int count, IEnumerable<string>? exclude = null)
    {
        var excluded = (exclude ?? []).ToHashSet();
        var sorted = records.Where(c => !c.IsMastered).OrderBy(c => c.LastReviewedAt ?? DateTimeOffset.MinValue).ThenBy(_ => Random.Shared.Next()).ToList();
        int target = Math.Min(count, sorted.Count(c => !excluded.Contains(c.Character)));
        int size = (sorted.Count + 1) / 2;
        while (size < sorted.Count && sorted.Take(size).Count(c => !excluded.Contains(c.Character)) < target) size++;
        return sorted.Take(size).Where(c => !excluded.Contains(c.Character)).OrderBy(_ => Random.Shared.Next()).Take(target).ToList();
    }
}

public static class ReviewService
{
    public static Database Apply(Database original, Guid worksheetId, IReadOnlyList<ReviewRow> rows, DateTimeOffset now)
    {
        var next = DatabaseStore.Clone(original);
        var sheet = next.Worksheets.Single(w => w.Id == worksheetId);
        if (sheet.SubmittedAt != null) throw new InvalidOperationException("这张练习纸已经提交。");
        if (sheet.InvalidatedAt != null) throw new InvalidOperationException("生字已被清空，这张练习纸不能再提交。");
        if (rows.Count != sheet.Questions.Count || rows.Select(r => r.Question.Number).Distinct().Count() != rows.Count || rows.Any(r => r.IsCorrect == null)) throw new InvalidOperationException("请确认所有题目的正确性。");
        foreach (var row in rows)
        {
            var q = sheet.Questions.Single(x => x.Number == row.Question.Number);
            if (q.Character != row.Question.Character) throw new InvalidOperationException("批改题目与练习纸不匹配。");
            var c = next.Characters.Single(x => x.Character == q.Character);
            q.IsCorrect = row.IsCorrect; q.IsMasteredAfterReview = row.IsMastered;
            c.ReviewCount++; if (row.IsCorrect == false) c.ErrorCount++; c.LastReviewedAt = now; c.IsMastered = row.IsMastered;
        }
        sheet.SubmittedAt = now;
        return next;
    }
}

public static class CharacterMaintenance
{
    public static Database Clear(Database original, DateTimeOffset now)
    {
        var next = DatabaseStore.Clone(original);
        foreach (var sheet in next.Worksheets.Where(w => w.SubmittedAt == null && w.InvalidatedAt == null)) sheet.InvalidatedAt = now;
        next.Characters.Clear();
        return next;
    }
}
