using System.Windows;
using System.Windows.Controls;
using ShuShuShai.Core;

namespace ShuShuShai.App.Views;

internal enum HistoryRemoval { Cancel, Archive, Delete }

internal sealed class HistoryRemovalWindow : Window
{
    internal HistoryRemoval Choice { get; private set; }

    internal HistoryRemovalWindow(Window owner, PhotoTask task, bool canArchive)
    {
        Owner = owner;
        DialogUi.Setup(this, "删除历史记录", 570, 265);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(DialogUi.Text(task.SourceTitle, 18));
        panel.Children.Add(DialogUi.Text(
            (canArchive ? "移到设置会保留选片进度，可以随时恢复。\n" : "")
            + "永久删除会移除任务、评分、取舍和配对记录。原片与导出文件会保留。"));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (canArchive) row.Children.Add(ChoiceButton("移到设置", HistoryRemoval.Archive, "从首页移除，可在设置恢复。"));
        row.Children.Add(ChoiceButton("永久删除", HistoryRemoval.Delete, "永久删除选片记录，保留照片文件。"));
        var cancel = DialogUi.Button("取消", (_, _) => Close(), "关闭窗口并保留历史记录。");
        cancel.IsCancel = true;
        cancel.IsDefault = true;
        row.Children.Add(cancel);
        panel.Children.Add(row);
        Content = panel;
    }

    private Button ChoiceButton(string label, HistoryRemoval choice, string hint) =>
        DialogUi.Button(label, (_, _) => { Choice = choice; DialogResult = true; }, hint);
}
