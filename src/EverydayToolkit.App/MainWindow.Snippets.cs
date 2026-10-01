using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using EverydayToolkit.Core;
using Microsoft.Win32;

namespace EverydayToolkit.App;

public partial class MainWindow
{
    private async Task RefreshSnippetsAsync()
    {
        var revision = ++snippetRevision;
        var query = SnippetSearch.Text;
        var entries = await controller.GetSnippetsAsync(query);
        if (revision != snippetRevision || exiting) return;
        snippets.Clear();
        foreach (var entry in entries) snippets.Add(entry);
        SnippetList.SelectedIndex = snippets.Count > 0 ? 0 : -1;
        if (snippets.Count == 0) { SnippetTitle.Text = "长期保存的回复与模板"; SnippetPreview.Text = "新建常用语，或把一条历史文字保存为模板。"; }
    }
    private async void SnippetSearch_Changed(object sender, TextChangedEventArgs e) { if (ready) await GuardAsync(RefreshSnippetsAsync); }
    private void SnippetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SnippetList.SelectedItem is not SnippetEntry entry) return;
        SnippetTitle.Text = entry.Name;
        SnippetPreview.Text = entry.Text;
    }
    private async Task EditSnippetAsync(bool isNew)
    {
        var old = isNew ? null : SnippetList.SelectedItem as SnippetEntry;
        if (!isNew && old is null) return;
        var dialog = new Views.SnippetDialog(isNew ? "新建常用语" : "编辑常用语", old?.Name ?? "", old?.Category ?? "", old?.Text ?? "") { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await Task.Run(() => store.SaveSnippet(dialog.SnippetName, dialog.Category, dialog.SnippetText, old?.Id));
        await RefreshSnippetsAsync();
        SetStatus("常用语已保存。");
    }
    private async void NewSnippet_Click(object sender, RoutedEventArgs e) => await GuardAsync(() => EditSnippetAsync(true));
    private async void EditSnippet_Click(object sender, RoutedEventArgs e) => await GuardAsync(() => EditSnippetAsync(false));
    private async void DeleteSnippet_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (SnippetList.SelectedItem is not SnippetEntry entry) return;
        if (MessageBox.Show(this, "删除这个常用语？", "删除常用语", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Task.Run(() => store.DeleteSnippet(entry.Id));
        await RefreshSnippetsAsync();
        SetStatus("常用语已删除。");
    });
    private async Task CopyTextAsync(string text, bool shouldPaste)
    {
        if (string.IsNullOrEmpty(text)) { SetStatus("没有可复制的文字。"); return; }
        if (!TryBeginClipboardOperation()) return;
        var target = shouldPaste ? paste.GetTargetSnapshot() : default;
        try
        {
            if (!clipboard.WriteText(text)) { SetStatus("剪贴板暂时不可用，请重试。"); return; }
            if (shouldPaste) await PasteCopiedContentAsync(target);
            else SetStatus("文字已复制，可手动粘贴。");
        }
        finally { clipboardOperationBusy = false; }
    }
    private async void SnippetCopy_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () => { if (SnippetList.SelectedItem is SnippetEntry entry) await CopyTextAsync(entry.Text, false); });
    private async void SnippetPaste_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () => { if (SnippetList.SelectedItem is SnippetEntry entry) await CopyTextAsync(entry.Text, true); });
    private void SnippetClean_Click(object sender, RoutedEventArgs e) { if (SnippetList.SelectedItem is SnippetEntry entry) LoadTextWorkspace(entry.Text); }
    private async void ImportSnippets_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        var dialog = new OpenFileDialog { Title = "导入常用语 JSON", Filter = "JSON 文件|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        var result = await Task.Run(() => { using var source = File.OpenRead(dialog.FileName); return store.ImportSnippets(source); });
        await RefreshSnippetsAsync();
        SetStatus($"已导入 {result.ImportedCount} 条常用语，{result.RenamedCount} 条重名项自动改名。");
    });
    private async void ExportSnippets_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        var dialog = new SaveFileDialog { Title = "导出常用语 JSON（包含未加密正文）", Filter = "JSON 文件|*.json", DefaultExt = ".json", AddExtension = true, OverwritePrompt = true, FileName = "常用语.json" };
        if (dialog.ShowDialog(this) != true) return;
        await Task.Run(() => { using var destination = File.Create(dialog.FileName); store.ExportSnippets(destination); });
        SetStatus("常用语已导出为未加密 JSON，可用于主动迁移。");
    });
}
