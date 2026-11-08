using System.IO;
using System.Text;

namespace ChineseBuddy;

public static class WordBlacklist
{
    private static readonly string[] Defaults =
    [
        "性欲", "色情", "性交", "淫乱", "强奸", "卖淫", "嫖娼",
        "暴力", "打死", "杀死", "杀人", "谋杀", "抢劫", "虐待", "拐卖", "自杀",
        "毒品", "吸毒", "贩毒", "赌博", "抽烟", "香烟", "烟袋", "烟壶", "炸弹", "炸药"
    ];

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "WordBlacklist.txt");

    public static HashSet<string> Load(string? path = null)
    {
        path ??= DefaultPath;
        return File.Exists(path) ? Parse(File.ReadAllText(path, Encoding.UTF8)) : new HashSet<string>(Defaults, StringComparer.Ordinal);
    }

    public static HashSet<string> Parse(string text)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var word = line.Trim().Normalize(NormalizationForm.FormKC);
            if (word.Length == 0) continue;
            var runes = word.EnumerateRunes().ToArray();
            if (runes.Length is < 2 or > 12 || !runes.All(TextRules.IsHan))
                throw new InvalidDataException($"黑名单每行须填写 2 至 12 个汉字：{word}");
            words.Add(word);
        }
        return words;
    }

    public static void Save(string text, string? path = null)
    {
        var words = Parse(text);
        path ??= DefaultPath;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temporary, words.OrderBy(w => w, StringComparer.Ordinal), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
