using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using EverydayToolkit.Core;
using Forms = System.Windows.Forms;

namespace EverydayToolkit.App.Views;

public interface ISettingsDialogMessages
{
    bool ConfirmDeletion(Window owner);
    void ShowDeletionResult(Window owner, bool completed);
}

public sealed class SettingsDialog : Window
{
    public ToolSettings? UpdatedSettings { get; private set; }
    public Func<Task>? DeleteContents { get; set; }

    public SettingsDialog(ToolSettings settings, string dataRoot, bool allowLocationChange = true, ISettingsDialogMessages? messages = null)
    {
        messages ??= new NativeMessages();
        Style = (Style)FindResource(typeof(Window));
        Title = "设置"; Width = 670; Height = Math.Min(760, SystemParameters.WorkArea.Height - 40); MinWidth = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new DockPanel { Margin = new Thickness(24) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var save = new Button { Content = "保存设置", Style = (Style)FindResource("PrimaryButton") };
        actions.Children.Add(cancel); actions.Children.Add(save); DockPanel.SetDock(actions, Dock.Bottom); layout.Children.Add(actions);
        var fields = new StackPanel(); layout.Children.Add(new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        TextBox Field(string caption, string value)
        {
            fields.Children.Add(new TextBlock { Text = caption, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
            var input = new TextBox { Text = value, Margin = new Thickness(0, 0, 0, 16) }; fields.Children.Add(input); return input;
        }
        var recording = new CheckBox { Content = "记录新复制的内容", IsChecked = settings.RecordingEnabled, Margin = new Thickness(0, 0, 0, 16) };
        fields.Children.Add(recording);
        var hotkey = Field("打开面板的快捷键", settings.Hotkey);
        var count = Field("历史最多条数（1–1000，含收藏）", settings.HistoryMaxCount.ToString());
        var capacity = Field("历史载荷上限（1–200 MiB，含图片和缩略图）", (settings.HistoryMaxBytes / 1048576L).ToString());
        var days = Field("未收藏历史保留天数（1–365）", settings.HistoryDays.ToString());
        fields.Children.Add(new TextBlock { Text = "调低上限或期限后会清理最早未收藏历史；收藏及常用语不会自动删除。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        var startup = new CheckBox { Content = "登录 Windows 时启动", IsChecked = settings.StartWithWindows, Margin = new Thickness(0, 0, 0, 18) }; fields.Children.Add(startup);
        var excluded = Field("不记录的应用（每行一个进程名，如 KeePass.exe）", string.Join(Environment.NewLine, settings.ExcludedApps));
        excluded.AcceptsReturn = true; excluded.Height = 80; excluded.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var location = Field(allowLocationChange ? "本地数据位置（重启后生效）" : "当前本地数据位置", dataRoot);
        location.IsReadOnly = !allowLocationChange;
        if (allowLocationChange)
        {
            var choose = new Button { Content = "选择数据目录", HorizontalAlignment = HorizontalAlignment.Left };
            choose.Click += (_, _) =>
            {
                using var picker = new Forms.FolderBrowserDialog { Description = "选择空目录或已有的日用工具箱数据目录", SelectedPath = dataRoot, ShowNewFolderButton = true };
                if (picker.ShowDialog() == Forms.DialogResult.OK) location.Text = picker.SelectedPath;
            };
            fields.Children.Add(choose);
        }
        var folder = new Button { Content = "打开数据目录", HorizontalAlignment = HorizontalAlignment.Left };
        folder.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("explorer.exe", dataRoot) { UseShellExecute = true }); } catch (Exception) { MessageBox.Show(this, "无法打开目录，请检查该位置是否存在。", "数据目录"); } }; fields.Children.Add(folder);
        fields.Children.Add(new TextBlock { Text = allowLocationChange
            ? "可选新的空目录或带有效标识的既有工具箱目录。切换后应用会退出，重新打开才使用所选目录；现有文字、图片和设置不会自动搬迁，原目录保留。同一 Windows 用户下的加密数据可继续读取。"
            : "本次使用 --data-dir 显式指定了目录。请退出应用并修改启动命令来更换位置；设置中的位置不能覆盖该参数。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 16) });
        var delete = new Button { Content = "删除全部已保存内容", HorizontalAlignment = HorizontalAlignment.Left };
        delete.Click += async (_, _) =>
        {
            if (!messages.ConfirmDeletion(this)) return;
            delete.IsEnabled = false;
            save.IsEnabled = false;
            try
            {
                if (DeleteContents is not null) await DeleteContents();
                recording.IsChecked = false;
                messages.ShowDeletionResult(this, true);
            }
            catch (Exception)
            {
                recording.IsChecked = settings.RecordingEnabled;
                messages.ShowDeletionResult(this, false);
            }
            finally { delete.IsEnabled = true; save.IsEnabled = true; }
        }; fields.Children.Add(delete);
        save.Click += (_, _) =>
        {
            if (!save.IsEnabled) return;
            if (!int.TryParse(count.Text, out var parsedCount) || parsedCount < 1 || parsedCount > 1000 || !long.TryParse(capacity.Text, out var parsedCapacity) || parsedCapacity < 1 || parsedCapacity > 200 || !int.TryParse(days.Text, out var parsedDays) || parsedDays < 1 || parsedDays > 365)
            { MessageBox.Show(this, "请按标明的范围填写条数、容量和天数。", "设置"); return; }
            var copy = JsonSerializer.Deserialize<ToolSettings>(JsonSerializer.Serialize(settings))!;
            copy.RecordingEnabled = recording.IsChecked == true;
            copy.FirstRunComplete = true;
            copy.Hotkey = hotkey.Text.Trim(); copy.HistoryMaxCount = parsedCount; copy.HistoryMaxBytes = parsedCapacity * 1048576L; copy.HistoryDays = parsedDays; copy.StartWithWindows = startup.IsChecked == true;
            // The draft expresses the shown directory; persisted default redirection is retained by the commit coordinator.
            copy.DataDirectory = allowLocationChange ? location.Text.Trim() : dataRoot;
            copy.ExcludedApps = excluded.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            UpdatedSettings = copy; DialogResult = true;
        };
        Content = layout;
    }
    private sealed class NativeMessages : ISettingsDialogMessages
    {
        public bool ConfirmDeletion(Window owner) => MessageBox.Show(owner, "删除所有文字与图片历史、收藏、常用语，并暂停记录？\n设置会保留，这个操作无法撤销。", "删除全部内容", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        public void ShowDeletionResult(Window owner, bool completed) => MessageBox.Show(owner,
            completed ? "已删除保存内容并暂停记录。" : "删除未能完成，原数据仍应保留。请检查数据库及权限后重试。", completed ? "完成" : "操作失败");
    }
}
