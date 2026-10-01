using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using EverydayToolkit.Core;

namespace EverydayToolkit.App;

public partial class MainWindow
{
    private void LoadTextWorkspace(string text)
    {
        textWorkspace.Load(text);
        OriginalText.Text = textWorkspace.Original;
        ResultText.Text = textWorkspace.Result;
        Tabs.SelectedIndex = 2;
        SetStatus("已载入文字；原历史和模板仍保持原样。");
    }
    private void OriginalText_Changed(object sender, TextChangedEventArgs e)
    {
        if (ResultText is null) return;
        textWorkspace.Load(OriginalText.Text);
        ResultText.Text = textWorkspace.Result;
    }
    private void TextRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string ruleName } || !Enum.TryParse<TextRule>(ruleName, out var rule)) return;
        textWorkspace.Result = ResultText.Text;
        textWorkspace.Apply(rule);
        ResultText.Text = textWorkspace.Result;
        SetStatus("处理结果已更新，可继续编辑后复制或保存。");
    }
    private void LoadClipboardText_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = clipboard.ReadCurrent(respectExclusions: false);
            if (content?.Kind != EntryKind.Text) { SetStatus("当前剪贴板没有可载入的纯文字。"); return; }
            LoadTextWorkspace(content.Text ?? "");
        }
        catch (Exception error) { SetStatus(UserError(error)); }
    }
    private async void CopyResult_Click(object sender, RoutedEventArgs e) => await GuardAsync(() => CopyTextAsync(ResultText.Text, false));
    private async void PasteResult_Click(object sender, RoutedEventArgs e) => await GuardAsync(() => CopyTextAsync(ResultText.Text, true));
    private void ResetResult_Click(object sender, RoutedEventArgs e) { textWorkspace.Reset(); ResultText.Text = textWorkspace.Result; }
    private async void ResultSnippet_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        var dialog = new Views.SnippetDialog("保存处理结果", "", "", ResultText.Text) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await Task.Run(() => store.SaveSnippet(dialog.SnippetName, dialog.Category, dialog.SnippetText));
        await RefreshSnippetsAsync();
        SetStatus("处理结果已保存为独立常用语。");
    });
}
