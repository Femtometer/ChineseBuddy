using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MessageBox = ChineseBuddy.ThemedMessageBox;

namespace ChineseBuddy;

public partial class MainWindow : Window
{
    private readonly DatabaseStore store = new();
    private readonly OcrService ocr = new();
    private Database db = new();
    private List<SelectionRow> selected = [];
    private List<ReviewRow> reviews = [];
    private Guid? currentSheet;
    private CancellationTokenSource? task;

    public MainWindow()
    {
        InitializeComponent();
        try { BlacklistBox.Text = string.Join(Environment.NewLine, WordBlacklist.Load().OrderBy(w => w, StringComparer.Ordinal)); }
        catch (Exception e) { MessageBox.Show("无法读取词语黑名单：" + e.Message, "黑名单错误", MessageBoxButton.OK, MessageBoxImage.Error); }
        try { db = store.Load(); Refresh(); }
        catch (Exception e)
        {
            if (MessageBox.Show("数据库无法读取：" + e.Message + "\n是否尝试恢复上一份备份？", "数据库错误", MessageBoxButton.YesNo, MessageBoxImage.Error) == MessageBoxResult.Yes)
            {
                try { db = store.RestoreBackup(); Refresh(); return; }
                catch (Exception restore) { MessageBox.Show("恢复失败：" + restore.Message); }
            }
            Loaded += (_, _) => Close();
        }
    }

    private void Refresh()
    {
        if (CharactersGrid == null) return;
        string? characterSelection = (CharactersGrid.SelectedItem as CharacterRecord)?.Character;
        string? worksheetSelection = (SelectionGrid?.SelectedItem as SelectionRow)?.Item.Character;
        var query = SearchBox?.Text?.Trim() ?? "";
        CharactersGrid.ItemsSource = db.Characters.Where(c => (UnmasteredOnly?.IsChecked != true || !c.IsMastered) && (WithoutWordsOnly?.IsChecked != true || !c.Words.Any(w => !string.IsNullOrWhiteSpace(w))) && MatchesSearch(c, query)).OrderBy(c => c.Character).ToList();
        if (characterSelection != null) CharactersGrid.SelectedItem = CharactersGrid.Items.Cast<CharacterRecord>().FirstOrDefault(c => c.Character == characterSelection);
        foreach (var row in selected) row.Item = db.Characters.First(c => c.Character == row.Item.Character);
        if (SelectionGrid != null)
        {
            SelectionGrid.ItemsSource = selected.ToList();
            if (worksheetSelection != null) SelectionGrid.SelectedItem = selected.FirstOrDefault(row => row.Item.Character == worksheetSelection);
        }
    }
    private static bool MatchesSearch(CharacterRecord character, string query) =>
        query.Length == 0 || character.Character.Contains(query) || character.Pinyin.Contains(query, StringComparison.OrdinalIgnoreCase) || character.Words.Any(w => w.Contains(query));
    private void Search_Changed(object sender, RoutedEventArgs e) => Refresh();
    private void Commit(Database next) { store.Save(next); db = next; Refresh(); }
    private void Error(Exception e) { StatusText.Text = "操作未完成"; MessageBox.Show(e.Message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => task?.Cancel();
    private bool BeginTask()
    {
        if (task != null) { MessageBox.Show("已有任务正在进行，请等待或取消。"); return false; }
        task = new CancellationTokenSource(); return true;
    }
    private void EndTask() { task?.Dispose(); task = null; }
    private IProgress<string> Progress() => new Progress<string>(s => StatusText.Text = s);

    private void SaveToken_Click(object sender, RoutedEventArgs e)
    {
        try { TokenStore.Save(TokenBox.Password); TokenBox.Clear(); StatusText.Text = "备用令牌已保存；环境变量 PaddleOCR_TOKEN 优先使用。"; }
        catch (Exception ex) { Error(ex); }
    }
    private void ClearToken_Click(object sender, RoutedEventArgs e) { try { TokenStore.Save(""); TokenBox.Clear(); StatusText.Text = "已清除本地保存的令牌；环境变量 PaddleOCR_TOKEN 不受影响。"; } catch (Exception ex) { Error(ex); } }
    private void SaveBlacklist_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            WordBlacklist.Save(BlacklistBox.Text);
            BlacklistBox.Text = string.Join(Environment.NewLine, WordBlacklist.Load().OrderBy(w => w, StringComparer.Ordinal));
            StatusText.Text = "词语黑名单已保存，后续字典查询将使用新规则。";
            MessageBox.Show(StatusText.Text, "保存成功");
        }
        catch (Exception ex) { Error(ex); }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "PDF 课本|*.pdf" }; if (dlg.ShowDialog() != true || !BeginTask()) return;
        try
        {
            var output = await ocr.RecognizeAsync(dlg.FileName, TokenStore.Load(), Progress(), task!.Token);
            var all = TextRules.ExtractHan(output); var unique = all.Distinct().ToList();
            var answer = Input("核对导入生字", $"识别到 {all.Count} 个汉字，去重后 {unique.Count} 个。可删除不需要的字后确认导入：", string.Join("", unique), true);
            if (answer == null) return;
            var chosen = TextRules.ExtractHan(answer).Distinct().ToList();
            var next = DatabaseStore.Clone(db); var known = next.Characters.Select(c => c.Character).ToHashSet(); int added = 0;
            foreach (var c in chosen) if (known.Add(c)) { next.Characters.Add(new CharacterRecord { Character = c }); added++; }
            Commit(next); StatusText.Text = $"识别 {all.Count} 个，去重后 {unique.Count} 个，新增 {added} 个，已存在 {chosen.Count - added} 个。";
            MessageBox.Show(StatusText.Text, "导入完成");
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消等待或识别超时；远端任务可能仍在运行。"; }
        catch (Exception ex) { Error(ex); }
        finally { EndTask(); }
    }

    private void AddCharacter_Click(object sender, RoutedEventArgs e)
    {
        var text = Input("手动添加生字", "输入要添加的汉字：", "", true); if (text == null) return;
        try { var next = DatabaseStore.Clone(db); var known = next.Characters.Select(c => c.Character).ToHashSet(); var chars = TextRules.ExtractHan(text).Distinct().Where(known.Add).ToList(); foreach (var c in chars) next.Characters.Add(new CharacterRecord { Character = c }); Commit(next); StatusText.Text = $"新增 {chars.Count} 个生字。"; }
        catch (Exception ex) { Error(ex); }
    }
    private void ClearCharacters_Click(object sender, RoutedEventArgs e)
    {
        if (task != null) { MessageBox.Show("有任务正在进行，请等待完成或先取消任务。", "清空生字"); return; }
        int count = db.Characters.Count;
        if (count == 0) { MessageBox.Show("生字表已经为空。", "清空生字"); return; }
        var answer = MessageBox.Show(this,
            $"将清空全部 {count} 个生字及其复习记录，未提交的练习纸将不能再批改。已生成的练习纸记录会保留。\n\n确定继续吗？",
            "清空生字", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            var next = CharacterMaintenance.Clear(db, DateTimeOffset.Now);
            store.Save(next);
            db = next;
            selected.Clear(); SelectionGrid.ItemsSource = null;
            reviews.Clear(); ReviewGrid.ItemsSource = null; currentSheet = null; WorksheetText.Text = "";
            Refresh(); StatusText.Text = $"已清空 {count} 个生字。";
        }
        catch (Exception ex) { Error(ex); }
    }
    private void EditCharacter_Click(object sender, RoutedEventArgs e) { if (CharactersGrid.SelectedItem is CharacterRecord c) Edit(c.Character); }
    private void EditSelected_Click(object sender, RoutedEventArgs e) { if (SelectionGrid.SelectedItem is SelectionRow row) Edit(row.Item.Character); }
    private void Edit(string character)
    {
        var old = db.Characters.First(c => c.Character == character);
        var edited = EditDialog(old);
        if (edited == null) return;
        try
        {
            var next = DatabaseStore.Clone(db); var c = next.Characters.First(x => x.Character == character);
            c.Pinyin = edited.Pinyin; c.Words = edited.Words; c.IsMastered = edited.IsMastered;
            Commit(next); StatusText.Text = $"已保存“{character}”的资料。";
        }
        catch (Exception ex) { Error(ex); }
    }
    private async void Dictionary_Click(object sender, RoutedEventArgs e)
    {
        if (SelectionGrid.SelectedItem is not SelectionRow row) { MessageBox.Show("请先抽取生字，再在练习纸列表中选择一个字。"); return; }
        await QueryDictionaryAsync(row.Item.Character);
    }
    private async void CharacterDictionary_Click(object sender, RoutedEventArgs e)
    {
        if (CharactersGrid.SelectedItem is not CharacterRecord item) { MessageBox.Show("请先在生字列表中选择一个字。"); return; }
        await QueryDictionaryAsync(item.Character);
    }
    private async Task QueryDictionaryAsync(string character)
    {
        if (!BeginTask()) return;
        try
        {
            StatusText.Text = "正在查询拼音和词语…";
            bool selectedInCharacters = (CharactersGrid.SelectedItem as CharacterRecord)?.Character == character;
            var (pinyin, words) = await new DictionaryService().LookupAsync(character, task!.Token);
            var next = DatabaseStore.Clone(db); var item = next.Characters.First(x => x.Character == character);
            if (DictionaryService.ApplySingleResult(item, pinyin, words, WordBlacklist.Load())) Commit(next);
            bool clearedSearch = selectedInCharacters && SearchBox.Text.Trim().Length > 0 && !MatchesSearch(item, SearchBox.Text.Trim());
            if (clearedSearch) SearchBox.Clear();
            if (selectedInCharacters && CharactersGrid.Items.Cast<CharacterRecord>().FirstOrDefault(c => c.Character == character) is { } visible)
            {
                CharactersGrid.SelectedItem = visible;
                CharactersGrid.ScrollIntoView(visible);
            }
            StatusText.Text = $"“{character}”查询完成，请核对拼音和词语，不足部分可手动补全。" + (clearedSearch ? "原搜索条件不再匹配，已清空搜索并定位该字。" : "");
            if (words.Count == 0) MessageBox.Show("未找到符合筛选条件的带拼音词语，或查询未成功；已保留原有词语，可重试或手动编辑。", "查询提示");
            if (!TextRules.Ready(item)) MessageBox.Show("请编辑生字，补全拼音及至少一个包含该字的词语。");
        }
        catch (OperationCanceledException) { StatusText.Text = "查询已取消。"; }
        catch (Exception ex) { Error(ex); }
        finally { EndTask(); }
    }
    private async void BatchDictionary_Click(object sender, RoutedEventArgs e)
    {
        if (selected.Count == 0) { MessageBox.Show("请先抽取生字，再批量查询字典。"); return; }
        await QueryBatchDictionaryAsync(selected.Select(row => row.Item).ToList(), ReplaceWordsOnBatch.IsChecked == true, "本次抽取的生字");
    }
    private async void CharacterBatchDictionary_Click(object sender, RoutedEventArgs e)
    {
        if (db.Characters.Count == 0) { MessageBox.Show("生字表为空，请先导入或添加生字。"); return; }
        await QueryBatchDictionaryAsync(db.Characters.ToList(), ReplaceCharacterWordsOnBatch.IsChecked == true, "全部生字");
    }
    private async Task QueryBatchDictionaryAsync(List<CharacterRecord> targets, bool replaceWords, string scope)
    {
        var plans = DictionaryService.PlanLookups(targets, replaceWords);
        var planByCharacter = plans.ToDictionary(plan => plan.Character);
        var characters = plans.Select(plan => plan.Character).ToArray();
        int skipped = targets.Count - characters.Length;
        if (characters.Length == 0)
        {
            StatusText.Text = $"{scope}共 {targets.Count} 个，均已有拼音和带拼音的词语，无需查询。";
            MessageBox.Show(StatusText.Text, "批量查询字典");
            return;
        }
        if (!BeginTask()) return;
        int processed = 0, updated = 0;
        var incomplete = new List<string>();
        var failed = new List<string>();
        try
        {
            var dictionary = new DictionaryService();
            var cancellation = task!.Token;
            StatusText.Text = $"正在批量查询拼音和词语：0/{characters.Length}…";
            await foreach (var result in BatchDictionaryService.RunAsync(characters,
                (character, ct) => dictionary.LookupAsync(character, ct, planByCharacter[character].NeedsPinyin, planByCharacter[character].NeedsWords),
                maxConcurrency: BatchDictionaryService.DefaultMaxConcurrency, cancellation,
                (character, pinyin, words) => (planByCharacter[character].NeedsPinyin && string.IsNullOrWhiteSpace(pinyin)) || (planByCharacter[character].NeedsWords && words.Count == 0)))
            {
                string character = result.Character;
                if (result.Error != null)
                {
                    failed.Add(character); processed++;
                    StatusText.Text = $"正在批量查询拼音和词语：{processed}/{characters.Length}（{character}）…";
                    continue;
                }
                string pinyin = result.Pinyin;
                var words = result.Words;
                var plan = planByCharacter[character];
                bool pinyinFailed = plan.NeedsPinyin && string.IsNullOrWhiteSpace(pinyin);
                bool wordsFailed = plan.NeedsWords && words.Count == 0;
                if ((plan.NeedsPinyin || plan.NeedsWords) && (!plan.NeedsPinyin || pinyinFailed) && (!plan.NeedsWords || wordsFailed)) failed.Add(character);
                else if (pinyinFailed || wordsFailed) incomplete.Add(character);
                var next = DatabaseStore.Clone(db);
                var item = next.Characters.First(c => c.Character == character);
                if (DictionaryService.ApplyBatchResult(item, pinyin, words, replaceWords)) { Commit(next); updated++; }
                processed++;
                StatusText.Text = $"正在批量查询拼音和词语：{processed}/{characters.Length}（{character}）…";
            }
            StatusText.Text = $"{scope}批量查询完成：已查询 {processed} 个，跳过已有资料 {skipped} 个，更新 {updated} 个，不完整 {incomplete.Count} 个，失败 {failed.Count} 个。";
            string details = StatusText.Text + (replaceWords ? "\n已用成功取得的汉典词语替换原词语；已有生字拼音保留。" : "\n已有拼音和词语已保留；匹配的词语已补充拼音。") + "已取得的信息已保存。";
            if (incomplete.Count > 0) details += "\n查询结果不完整：" + FormatCharacters(incomplete);
            if (failed.Count > 0) details += "\n未获取结果：" + FormatCharacters(failed);
            MessageBox.Show(details, "批量查询拼音和词语");
        }
        catch (OperationCanceledException) { StatusText.Text = $"批量查询已取消：已查询 {processed}/{characters.Length} 个，更新 {updated} 个，结果已保存。"; }
        catch (Exception ex) { Error(new InvalidOperationException($"批量查询已停止，已查询 {processed}/{characters.Length} 个，已保存的结果保留。\n{ex.Message}", ex)); }
        finally { EndTask(); }
    }
    private static string FormatCharacters(List<string> characters) => string.Join("、", characters.Take(20)) + (characters.Count > 20 ? "等" : "");
    private void Select_Click(object sender, RoutedEventArgs e)
    {
        selected = SelectionService.Pick(db.Characters, 15).Select(c => new SelectionRow { Item = c }).ToList();
        Refresh(); StatusText.Text = selected.Count == 0 ? "没有未掌握的生字。" : $"已抽取 {selected.Count} 个生字。";
    }
    private void Replace_Click(object sender, RoutedEventArgs e)
    {
        SelectionGrid.CommitEdit(DataGridEditingUnit.Cell, true); SelectionGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var replace = selected.Where(x => x.Replace).ToList();
        if (replace.Count == 0) { MessageBox.Show("请先勾选要重新抽取的字。"); return; }
        var alternatives = SelectionService.Pick(db.Characters, replace.Count, selected.Select(x => x.Item.Character));
        for (int i = 0; i < alternatives.Count; i++) { replace[i].Item = alternatives[i]; replace[i].Replace = false; }
        Refresh(); StatusText.Text = $"已替换 {alternatives.Count} 个；未替换项保留勾选。";
    }
    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (selected.Count == 0) { MessageBox.Show("请先抽取生字。"); return; }
            var invalid = selected.Where(x => !TextRules.Ready(x.Item)).Select(x => x.Item.Character).ToList();
            if (invalid.Count > 0) { MessageBox.Show("请先为以下生字填写拼音及至少一个包含该字的词语，词语不能重复：" + string.Join("、", invalid)); return; }
            var dialog = new SaveFileDialog { Filter = "PDF 文件|*.pdf", FileName = WorksheetExport.DefaultFileName(DateTime.Now), OverwritePrompt = false }; if (dialog.ShowDialog() != true) return;
            var sheet = WorksheetService.Create(selected.Select(s => s.Item));
            var temp = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, "." + Guid.NewGuid().ToString("N") + ".pdf");
            string exportedPath;
            try
            {
                WorksheetService.GeneratePdf(sheet, temp);
                var next = DatabaseStore.Clone(db); next.Worksheets.Add(sheet); Commit(next);
                exportedPath = WorksheetExport.MoveToAvailablePath(temp, dialog.FileName);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            StatusText.Text = "练习纸已生成：" + exportedPath; MessageBox.Show("练习纸已生成，请按实际大小打印。\n" + exportedPath, "完成");
        }
        catch (Exception ex) { Error(ex); }
    }
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "扫描文件|*.pdf;*.png;*.jpg;*.jpeg" }; if (dialog.ShowDialog() != true) return;
        if (!BeginTask()) return;
        currentSheet = null; reviews = []; ReviewGrid.ItemsSource = null; WorksheetText.Text = "";
        try
        {
            int pages = await Task.Run(() => WorksheetService.GetPageCount(dialog.FileName)); int page = 1;
            if (pages > 1)
            {
                var value = Input("选择页面", $"PDF 共 {pages} 页，请输入练习纸所在页码：", "1", false);
                if (value == null) return;
                if (!int.TryParse(value, out page) || page < 1 || page > pages) throw new InvalidDataException("页码无效。");
            }
            StatusText.Text = "正在定位练习纸…";
            var layouts = db.Worksheets.ToDictionary(w => w.Id, w => w.LayoutVersion);
            var scan = await Task.Run(() => WorksheetService.Scan(dialog.FileName, page - 1, id => layouts.TryGetValue(id, out var version) ? version : null), task!.Token);
            var sheet = db.Worksheets.FirstOrDefault(w => w.Id == scan.Id) ?? throw new InvalidDataException("未找到此练习纸的原始记录。");
            if (sheet.SubmittedAt != null) throw new InvalidOperationException("这张练习纸已经提交，不能重复累计。");
            if (sheet.InvalidatedAt != null) throw new InvalidOperationException("生字已被清空，这张练习纸不能再批改。");
            string token = TokenStore.Load(); if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("请设置 PaddleOCR_TOKEN 环境变量，或在设置中填写 PaddleOCR 访问令牌。");
            var result = new List<ReviewRow>();
            foreach (var q in sheet.Questions.OrderBy(q => q.Number))
            {
                task!.Token.ThrowIfCancellationRequested();
                var crop = scan.Crops[q.Number - 1];
                string temp = Path.Combine(Path.GetTempPath(), "ChineseBuddy-" + Guid.NewGuid().ToString("N") + ".png");
                string text = "";
                try
                {
                    await File.WriteAllBytesAsync(temp, crop, task.Token);
                    var progress = new Progress<string>(s => StatusText.Text = $"第 {q.Number}/{sheet.Questions.Count} 题：{s}");
                    text = await ocr.RecognizeAsync(temp, token, progress, task.Token);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                var chars = TextRules.ExtractHan(text);
                var character = db.Characters.First(c => c.Character == q.Character);
                var suggestion = chars.Count == 1 ? (chars[0] == q.Character ? "建议：正确" : "建议：错误") : "无法确定";
                result.Add(new ReviewRow { Question = q, Recognized = text.Trim() + "  （" + suggestion + "）", Image = LoadImage(crop), IsCorrect = null, IsMastered = character.IsMastered, ReviewCount = character.ReviewCount, ErrorCount = character.ErrorCount });
            }
            currentSheet = sheet.Id; reviews = result; ReviewGrid.ItemsSource = reviews; WorksheetText.Text = $"练习纸 {sheet.Id:D}";
            StatusText.Text = "识别完成，请逐题核对后保存。";
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消等待或识别超时；远端任务可能仍在运行。"; }
        catch (Exception ex) { Error(ex); }
        finally { EndTask(); }
    }
    private void SaveReview_Click(object sender, RoutedEventArgs e)
    {
        ReviewGrid.CommitEdit(DataGridEditingUnit.Cell, true); ReviewGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (currentSheet == null || reviews.Count == 0) { MessageBox.Show("请先导入练习纸。"); return; }
        if (reviews.Any(r => r.IsCorrect == null)) { MessageBox.Show("请先确认所有题目的正确性。"); return; }
        var password = Input("保存确认", "请输入当前年月日时分（yyyyMMddHHmm）：", "", false, true);
        if (password == null) return;
        if (password != DateTime.Now.ToString("yyyyMMddHHmm")) { MessageBox.Show("密码不正确或已过期，请重新输入。"); return; }
        try
        {
            var next = ReviewService.Apply(db, currentSheet.Value, reviews, DateTimeOffset.Now);
            Commit(next); currentSheet = null; reviews = []; ReviewGrid.ItemsSource = null; WorksheetText.Text = ""; StatusText.Text = "批改结果已保存。";
            MessageBox.Show("批改结果已保存。");
        }
        catch (Exception ex) { Error(ex); }
    }
    private static BitmapImage LoadImage(byte[] bytes)
    {
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = new MemoryStream(bytes); image.EndInit(); image.Freeze(); return image;
    }
    private string? Input(string title, string label, string initial, bool multiline, bool secret = false)
        => AppDialog.Prompt(this, title, label, initial, multiline, secret);
    private CharacterRecord? EditDialog(CharacterRecord current)
        => AppDialog.EditCharacter(this, current);
}
