using System.Text.Json.Serialization;

namespace ChineseBuddy;

public sealed class Database
{
    [JsonRequired] public int SchemaVersion { get; set; } = 1;
    [JsonRequired] public List<CharacterRecord> Characters { get; set; } = [];
    [JsonRequired] public List<Worksheet> Worksheets { get; set; } = [];
}

public sealed class CharacterRecord
{
    [JsonRequired] public string Character { get; set; } = "";
    [JsonRequired] public string Pinyin { get; set; } = "";
    [JsonRequired] public List<string> Words { get; set; } = [];
    [JsonRequired] public DateTimeOffset? LastReviewedAt { get; set; }
    [JsonRequired] public int ReviewCount { get; set; }
    [JsonRequired] public int ErrorCount { get; set; }
    [JsonRequired] public bool IsMastered { get; set; }
    [JsonIgnore] public string WordsText => string.Join("、", Words);
}

public sealed class Worksheet
{
    [JsonRequired] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonRequired] public int LayoutVersion { get; set; } = 1;
    [JsonRequired] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    [JsonRequired] public DateTimeOffset? SubmittedAt { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? InvalidatedAt { get; set; }
    [JsonRequired] public List<Question> Questions { get; set; } = [];
}

public sealed class Question
{
    [JsonRequired] public int Number { get; set; }
    [JsonRequired] public string Character { get; set; } = "";
    [JsonRequired] public string Pinyin { get; set; } = "";
    [JsonRequired] public List<string> Words { get; set; } = [];
    [JsonRequired] public int ReviewCountAtPrint { get; set; }
    [JsonRequired] public int ErrorCountAtPrint { get; set; }
    [JsonRequired] public bool? IsCorrect { get; set; }
    [JsonRequired] public bool? IsMasteredAfterReview { get; set; }
}

public sealed class SelectionRow
{
    public bool Replace { get; set; }
    public CharacterRecord Item { get; set; } = new();
}

public sealed class ReviewRow
{
    public Question Question { get; set; } = new();
    public string Recognized { get; set; } = "";
    public System.Windows.Media.ImageSource? Image { get; set; }
    public bool? IsCorrect { get; set; }
    public bool IsMastered { get; set; }
    public int ReviewCount { get; set; }
    public int ErrorCount { get; set; }
}
