using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ChineseBuddy;

public partial class AppDialog : Window
{
    public AppDialog(string title, Window? owner)
    {
        InitializeComponent();
        Title = title;
        HeadingText.Text = title;
        if (owner?.IsVisible == true)
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        MaxHeight = Math.Min(700, SystemParameters.WorkArea.Height - 20);
    }

    private Button AddButton(string label, bool primary, bool isDefault, bool isCancel, Action action)
    {
        var button = new Button
        {
            Content = label,
            Style = (Style)FindResource(primary ? "DialogPrimaryButton" : "DialogSecondaryButton"),
            IsDefault = isDefault,
            IsCancel = isCancel
        };
        button.Click += (_, _) => action();
        FooterButtons.Children.Add(button);
        return button;
    }

    public static MessageBoxResult ShowMessage(Window? owner, string message, string title, MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult defaultResult)
    {
        var dialog = new AppDialog(title, owner);
        var panel = new Grid();
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bool isError = icon == MessageBoxImage.Error;
        bool isWarning = icon == MessageBoxImage.Warning;
        var badge = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = Brush(isError ? "#FDEEEE" : isWarning ? "#FFF3E3" : "#EBF5FF"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        badge.Child = new TextBlock { Text = isError || isWarning ? "!" : "i", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Brush(isError ? "#CC4B4B" : isWarning ? "#D58922" : "#409EFF"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(badge);
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#333333"), FontSize = 13, LineHeight = 21 };
        var scroller = new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 330 };
        Grid.SetColumn(scroller, 1); panel.Children.Add(scroller);
        dialog.BodyHost.Content = panel;

        MessageBoxResult result = buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.OK;
        if (buttons == MessageBoxButton.YesNo)
        {
            bool noIsDefault = defaultResult == MessageBoxResult.No;
            var no = dialog.AddButton("取消", false, noIsDefault, true, () => { result = MessageBoxResult.No; dialog.DialogResult = false; });
            var yes = dialog.AddButton("确定", true, !noIsDefault, false, () => { result = MessageBoxResult.Yes; dialog.DialogResult = true; });
            dialog.Loaded += (_, _) => (noIsDefault ? no : yes).Focus();
        }
        else if (buttons == MessageBoxButton.OK)
        {
            var ok = dialog.AddButton("确定", true, true, true, () => { result = MessageBoxResult.OK; dialog.DialogResult = true; });
            dialog.Loaded += (_, _) => ok.Focus();
        }
        else throw new NotSupportedException("不支持此对话框按钮组合。");
        dialog.ShowDialog();
        return result;
    }

    public static string? Prompt(Window owner, string title, string label, string initial, bool multiline, bool secret)
    {
        var dialog = new AppDialog(title, owner) { Width = 560 };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#52657A"), FontSize = 13, LineHeight = 21, Margin = new Thickness(0, 0, 0, 12) });
        var box = new TextBox { Text = initial, AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled, Height = multiline ? 180 : 36, VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center };
        var password = new PasswordBox { Height = 36 };
        panel.Children.Add(secret ? password : box);
        dialog.BodyHost.Content = panel;
        dialog.AddButton("取消", false, false, true, () => dialog.DialogResult = false);
        dialog.AddButton("确定", true, true, false, () => dialog.DialogResult = true);
        dialog.Loaded += (_, _) => { if (secret) password.Focus(); else box.Focus(); };
        return dialog.ShowDialog() == true ? (secret ? password.Password : box.Text) : null;
    }

    public static CharacterRecord? EditCharacter(Window owner, CharacterRecord current)
    {
        var dialog = new AppDialog("编辑生字 " + current.Character, owner) { Width = 480 };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "拼音", FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });
        var pinyin = new TextBox { Text = current.Pinyin, Height = 36, Margin = new Thickness(0, 0, 0, 14) }; panel.Children.Add(pinyin);
        panel.Children.Add(new TextBlock { Text = "词语（每行一个，生成练习纸至少需要一个）", FontWeight = FontWeights.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        var words = new TextBox { Text = string.Join("\n", current.Words), AcceptsReturn = true, Height = 110, VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 14) }; panel.Children.Add(words);
        var mastered = new CheckBox { Content = "已掌握", IsChecked = current.IsMastered, FontSize = 13 }; panel.Children.Add(mastered);
        dialog.BodyHost.Content = panel;
        dialog.AddButton("取消", false, false, true, () => dialog.DialogResult = false);
        dialog.AddButton("保存", true, true, false, () => dialog.DialogResult = true);
        dialog.Loaded += (_, _) => pinyin.Focus();
        if (dialog.ShowDialog() != true) return null;
        return new CharacterRecord { Pinyin = pinyin.Text.Trim(), Words = words.Text.Split(['\r', '\n', '、', ',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(), IsMastered = mastered.IsChecked == true };
    }

    private static SolidColorBrush Brush(string value) => (SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
}

public static class ThemedMessageBox
{
    public static MessageBoxResult Show(string message) => Show(message, "提示");
    public static MessageBoxResult Show(string message, string title) => Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    public static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage icon) =>
        AppDialog.ShowMessage(Application.Current?.MainWindow, message, title, buttons, icon, MessageBoxResult.None);
    public static MessageBoxResult Show(Window owner, string message, string title, MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult defaultResult) =>
        AppDialog.ShowMessage(owner, message, title, buttons, icon, defaultResult);
}
