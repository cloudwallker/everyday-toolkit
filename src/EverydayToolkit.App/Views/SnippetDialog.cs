using System.Windows;
using System.Windows.Controls;

namespace EverydayToolkit.App.Views;

public sealed class SnippetDialog : Window
{
    private readonly TextBox nameInput;
    private readonly TextBox categoryInput;
    private readonly TextBox textInput;
    public string SnippetName => nameInput.Text.Trim();
    public string Category => categoryInput.Text.Trim();
    public string SnippetText => textInput.Text;

    public SnippetDialog(string title, string name, string category, string text)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = title;
        Width = 650; Height = 580; MinWidth = 480; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new Grid { Margin = new Thickness(24) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) layout.RowDefinitions.Add(new RowDefinition { Height = height });
        Add(layout, new TextBlock { Text = "名称", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) }, 0);
        nameInput = new TextBox { Text = name, Margin = new Thickness(0, 0, 0, 16) }; Add(layout, nameInput, 1);
        Add(layout, new TextBlock { Text = "分类（可选）", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) }, 2);
        categoryInput = new TextBox { Text = category, Margin = new Thickness(0, 0, 0, 16) }; Add(layout, categoryInput, 3);
        Add(layout, new TextBlock { Text = "文字内容", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) }, 4);
        textInput = new TextBox { Text = text, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top }; Add(layout, textInput, 5);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var save = new Button { Content = "保存", Style = (Style)FindResource("PrimaryButton") };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(SnippetName) || string.IsNullOrWhiteSpace(SnippetText)) { MessageBox.Show(this, "请填写名称和文字内容。", "常用语", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            DialogResult = true;
        };
        actions.Children.Add(cancel); actions.Children.Add(save); Add(layout, actions, 6);
        Content = layout;
        Loaded += (_, _) => nameInput.Focus();
    }
    private static void Add(Grid grid, UIElement element, int row) { Grid.SetRow(element, row); grid.Children.Add(element); }
}
