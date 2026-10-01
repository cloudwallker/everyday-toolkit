using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using EverydayToolkit.Core;
using EverydayToolkit.Windows;
using Microsoft.Win32;

namespace EverydayToolkit.App;

public partial class MainWindow
{
    private async void HistoryFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        await GuardAsync(RefreshHistoryAsync);
    }
    private async Task RefreshHistoryAsync()
    {
        var revision = ++historyRevision;
        var kind = KindFilter.SelectedIndex == 1 ? EntryKind.Text : KindFilter.SelectedIndex == 2 ? EntryKind.Image : (EntryKind?)null;
        HistorySearch.IsEnabled = kind != EntryKind.Image;
        var search = kind == EntryKind.Image ? "" : HistorySearch.Text;
        DateTimeOffset? since = TimeFilter.SelectedIndex == 1 ? DateTimeOffset.UtcNow.AddDays(-1) : TimeFilter.SelectedIndex == 2 ? DateTimeOffset.UtcNow.AddDays(-7) : null;
        var query = new HistoryQuery(search, kind, FavoritesFilter.IsChecked == true, since);
        var selectedId = (HistoryList.SelectedItem as HistoryRow)?.Entry.Id;
        var entries = await controller.GetHistoryAsync(query);
        if (revision != historyRevision || exiting) return;
        PreviewImage.Source = null;
        imageCache.Clear();
        foreach (var old in historyRows) { old.IsThumbnailVisible = false; old.Thumbnail = null; }
        historyRows.Clear();
        foreach (var entry in entries) historyRows.Add(new HistoryRow(entry));
        HistoryEmpty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmpty.Text = search.Length > 0 || kind is not null || FavoritesFilter.IsChecked == true ? "没有符合筛选条件的内容。" : settings.RecordingEnabled ? "之后复制的文字和图片会出现在这里。" : "开启记录后，你之后复制的内容会保存到这里。";
        HistoryList.SelectedItem = historyRows.FirstOrDefault(row => row.Entry.Id == selectedId) ?? historyRows.FirstOrDefault();
        if (HistoryList.SelectedItem is null) { PreviewText.Text = "选择一条文字或图片查看预览。"; PreviewImage.Visibility = Visibility.Collapsed; PreviewText.Visibility = Visibility.Visible; UpdateHistoryActions(null); }
        var statistics = await Task.Run(store.GetStatistics);
        if (revision != historyRevision || exiting) return;
        StatisticsText.Text = $"{statistics.HistoryCount} 条历史 · {statistics.FavoriteCount} 条收藏 · 内容 {statistics.HistoryBytes / 1048576d:F1} / {settings.HistoryMaxBytes / 1048576d:F0} MiB · 磁盘 {statistics.DiskBytes / 1048576d:F1} MiB";
    }

    private async void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = HistoryList.SelectedItem as HistoryRow;
        UpdateHistoryActions(row?.Entry);
        PreviewImage.Source = null;
        if (row is null) return;
        PreviewTitle.Text = row.Entry.Kind == EntryKind.Image ? $"图片 · {row.Entry.Width} × {row.Entry.Height}" : "文字预览";
        PreviewText.Visibility = row.Entry.Kind == EntryKind.Text ? Visibility.Visible : Visibility.Collapsed;
        PreviewImage.Visibility = row.Entry.Kind == EntryKind.Image ? Visibility.Visible : Visibility.Collapsed;
        PreviewText.Text = row.Entry.Text ?? "";
        if (row.Entry.Kind == EntryKind.Image)
        {
            await GuardAsync(async () =>
            {
                var image = await Task.Run(() => imageCache.GetFullImage(row.Entry.Id, () => store.GetImage(row.Entry.Id).Png));
                if (HistoryList.SelectedItem == row && !exiting) PreviewImage.Source = image;
                else imageCache.Remove(row.Entry.Id);
            });
        }
    }
    private void UpdateHistoryActions(HistoryEntry? entry)
    {
        var hasEntry = entry is not null;
        foreach (var button in new[] { HistoryCopyButton, HistoryPasteButton, FavoriteButton, HistoryDeleteButton }) button.IsEnabled = hasEntry;
        var image = entry?.Kind == EntryKind.Image;
        ImageOpenButton.Visibility = ImageSaveButton.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
        ImageOpenButton.IsEnabled = ImageSaveButton.IsEnabled = image;
        HistorySnippetButton.Visibility = HistoryCleanButton.Visibility = image ? Visibility.Collapsed : Visibility.Visible;
        HistorySnippetButton.IsEnabled = HistoryCleanButton.IsEnabled = hasEntry && !image;
        FavoriteButton.Content = entry?.IsFavorite == true ? "取消收藏" : "收藏";
        HistoryCopyButton.Content = image ? "复制图片" : "复制";
    }
    private async void HistoryThumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image element || element.DataContext is not HistoryRow row || row.Entry.Kind != EntryKind.Image) return;
        row.IsThumbnailVisible = true;
        await GuardAsync(async () =>
        {
            var image = await Task.Run(() => imageCache.GetThumbnail(row.Entry.Id, () => store.GetThumbnail(row.Entry.Id)));
            if (row.IsThumbnailVisible && !exiting) row.Thumbnail = image;
        });
    }
    private void HistoryThumbnail_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image { DataContext: HistoryRow row }) { row.IsThumbnailVisible = false; row.Thumbnail = null; }
    }

    private async Task CopyHistoryAsync(bool shouldPaste)
    {
        if (HistoryList.SelectedItem is not HistoryRow row) return;
        if (!TryBeginClipboardOperation()) return;
        var target = shouldPaste ? paste.GetTargetSnapshot() : default;
        try
        {
            var entry = row.Entry;
            var success = entry.Kind == EntryKind.Text ? clipboard.WriteText(entry.Text ?? "") : await clipboard.WriteImageAsync((await Task.Run(() => store.GetImage(entry.Id))).Png);
            if (!success) { SetStatus("系统剪贴板暂时不可用，请稍后重试。"); return; }
            await Task.Run(() => store.Touch(entry.Id));
            if (shouldPaste) await PasteCopiedContentAsync(target, entry.Kind == EntryKind.Image);
            else SetStatus(entry.Kind == EntryKind.Image ? "图片已恢复到系统剪贴板，可手动粘贴。" : "文字已复制到系统剪贴板。");
        }
        finally { clipboardOperationBusy = false; }
    }
    private async Task PasteCopiedContentAsync(PasteTarget target, bool isImage = false)
    {
        var reason = await paste.PasteAsync(target);
        SetStatus(reason ?? (isImage ? "已尝试粘贴图片；目标应用需要支持图片。" : "已尝试粘贴到原窗口。"));
        if (reason is null) Hide();
    }
    private async void HistoryCopy_Click(object sender, RoutedEventArgs e) => await GuardAsync(() => CopyHistoryAsync(false));
    private async void HistoryPaste_Click(object sender, RoutedEventArgs e) => await GuardAsync(() => CopyHistoryAsync(true));
    private async void Favorite_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (HistoryList.SelectedItem is not HistoryRow row) return;
        await Task.Run(() => store.SetFavorite(row.Entry.Id, !row.Entry.IsFavorite));
        await RefreshHistoryAsync();
    });
    private async void HistoryDelete_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (HistoryList.SelectedItem is not HistoryRow row) return;
        if (row.Entry.IsFavorite && MessageBox.Show(this, "删除这条已收藏的历史内容？", "删除收藏", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Task.Run(() => store.DeleteHistory(row.Entry.Id));
        imageCache.Remove(row.Entry.Id);
        await RefreshHistoryAsync();
        SetStatus("已删除该历史条目。");
    });
    private async void ClearHistory_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (MessageBox.Show(this, "清空所有未收藏历史？收藏和常用语会保留。", "清空历史", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Task.Run(store.ClearUnpinnedHistory);
        imageCache.Clear();
        await RefreshHistoryAsync();
        SetStatus("已清空未收藏历史，收藏和常用语已保留。");
    });
    private async void ImageSave_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (HistoryList.SelectedItem is not HistoryRow { Entry.Kind: EntryKind.Image } row) return;
        var dialog = new SaveFileDialog { Title = "另存图片为 PNG（文件未加密）", Filter = "PNG 图片|*.png", DefaultExt = ".png", AddExtension = true, OverwritePrompt = true, FileName = "图片-" + row.Entry.CreatedUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss") + ".png" };
        if (dialog.ShowDialog(this) != true) return;
        var payload = await Task.Run(() => store.GetImage(row.Entry.Id));
        await File.WriteAllBytesAsync(dialog.FileName, payload.Png);
        SetStatus("图片已另存为未加密 PNG，历史内容未改变。");
    });
    private async void ImageOpen_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (HistoryList.SelectedItem is not HistoryRow { Entry.Kind: EntryKind.Image } row) return;
        var image = await Task.Run(() => imageCache.GetFullImage(row.Entry.Id, () => store.GetImage(row.Entry.Id).Png));
        var viewer = new Views.ImagePreviewWindow(image) { Owner = this };
        viewer.ShowDialog();
    });
    private async void HistorySnippet_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (HistoryList.SelectedItem is not HistoryRow { Entry.Kind: EntryKind.Text } row) return;
        var dialog = new Views.SnippetDialog("保存为常用语", "", "", row.Entry.Text ?? "") { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await controller.SaveFromHistoryAsync(row.Entry, dialog.SnippetName, dialog.Category, dialog.SnippetText);
        await RefreshSnippetsAsync();
        SetStatus("已建立独立常用语，删除历史不会删除模板。");
    });
    private void HistoryClean_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryRow { Entry.Kind: EntryKind.Text } row) LoadTextWorkspace(row.Entry.Text ?? "");
    }
}
