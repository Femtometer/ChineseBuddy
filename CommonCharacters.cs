using System.IO;
using System.Text;

namespace ChineseBuddy;

public static class CommonCharacters
{
    private static readonly Lazy<HashSet<Rune>> Characters = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool ContainsAll(IEnumerable<Rune> characters) => characters.All(Characters.Value.Contains);

    private static HashSet<Rune> Load()
    {
        using var stream = typeof(CommonCharacters).Assembly.GetManifestResourceStream("ChineseBuddy.CommonCharacters.txt")
            ?? throw new InvalidDataException("程序中未找到现代汉语常用字表。");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var result = new HashSet<Rune>();
        while (reader.ReadLine() is { } line)
        {
            string character = line.Trim();
            if (character.Length == 0) continue;
            if (!TextRules.IsSingleHan(character)) throw new InvalidDataException("现代汉语常用字表格式无效。");
            result.Add(character.EnumerateRunes().Single());
        }
        if (result.Count == 0) throw new InvalidDataException("现代汉语常用字表为空。");
        return result;
    }
}
