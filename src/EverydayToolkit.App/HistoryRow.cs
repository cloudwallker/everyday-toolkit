using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Imaging;
using EverydayToolkit.Core;

namespace EverydayToolkit.App;

public sealed class HistoryRow : INotifyPropertyChanged
{
    private BitmapSource? thumbnail;
    public HistoryEntry Entry { get; }
    public HistoryRow(HistoryEntry entry) => Entry = entry;
    public string Title => Entry.Kind == EntryKind.Image ? $"图片 · {Entry.Width} × {Entry.Height}" : (Entry.Text ?? "").Replace('\r', ' ').Replace('\n', ' ');
    public string Detail => Entry.LastUsedUtc.ToLocalTime().ToString("MM-dd HH:mm") + (Entry.IsFavorite ? "  ·  已收藏" : "");
    public Visibility ImageVisibility => Entry.Kind == EntryKind.Image ? Visibility.Visible : Visibility.Collapsed;
    public bool IsThumbnailVisible { get; set; }
    public BitmapSource? Thumbnail { get => thumbnail; set { thumbnail = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
