using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using ShuShuShai.Core;

namespace ShuShuShai.App.Services;

public enum ShortcutAction
{
    Large, Overview, Compare, FullScreen, Previous, Next, Fit, ActualPixels, ZoomIn, ZoomOut,
    SelectAll, Undo, Unrate, Rate1, Rate2, Rate3, Rate4, Rate5, Keep, Reject, Undecide
}

public readonly record struct ShortcutGesture(Key Key, ModifierKeys Modifiers = ModifierKeys.None)
{
    public static ShortcutGesture From(KeyEventArgs e) =>
        new(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers);

    public bool IsAssignable =>
        Enum.IsDefined(Key) && (Modifiers & ~(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift)) == 0
        && Key is not (Key.None or Key.Escape or Key.Tab or Key.Return or Key.Space or Key.LWin or Key.RWin or Key.Apps
            or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.System or Key.ImeProcessed or Key.DeadCharProcessed)
        && !(Modifiers.HasFlag(ModifierKeys.Alt) && Key == Key.F4)
        && !(Modifiers.HasFlag(ModifierKeys.Alt) && Modifiers.HasFlag(ModifierKeys.Control) && Key == Key.Delete);

    public string Label
    {
        get
        {
            var key = Key switch
            {
                >= Key.D0 and <= Key.D9 => ((int)(Key - Key.D0)).ToString(),
                >= Key.NumPad0 and <= Key.NumPad9 => $"小键盘 {Key - Key.NumPad0}",
                Key.Left => "←", Key.Right => "→", Key.Up => "↑", Key.Down => "↓",
                Key.Add => "小键盘 +", Key.Subtract => "小键盘 −",
                Key.OemPlus => Modifiers.HasFlag(ModifierKeys.Shift) ? "+" : "=",
                Key.OemMinus => "−",
                _ => Key.ToString()
            };
            return (Modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "")
                + (Modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "")
                + (Modifiers.HasFlag(ModifierKeys.Shift) && Key != Key.OemPlus ? "Shift+" : "") + key;
        }
    }
}

public sealed record ShortcutDefinition(ShortcutAction Action, string Name, string Hint, IReadOnlyList<ShortcutGesture> Defaults);

public sealed class Shortcuts : INotifyPropertyChanged
{
    private const string SettingKey = "keyboard-shortcuts-v1";
    private Dictionary<ShortcutAction, ShortcutGesture?> overrides = [];
    private Dictionary<ShortcutGesture, ShortcutAction> actions = BuildLookup(new Dictionary<ShortcutAction, ShortcutGesture?>());

    private static ShortcutDefinition Define(ShortcutAction action, string name, string hint, params ShortcutGesture[] defaults) =>
        new(action, name, hint, defaults);

    public static IReadOnlyList<ShortcutDefinition> Definitions { get; } =
    [
        Define(ShortcutAction.Large, "大图", "切换到当前照片的大图。", new ShortcutGesture(Key.D)),
        Define(ShortcutAction.Overview, "总览", "浏览当前筛选范围内的缩略图。", new ShortcutGesture(Key.G)),
        Define(ShortcutAction.Compare, "对比选中", "并排查看已选的 2–9 张照片。", new ShortcutGesture(Key.C)),
        Define(ShortcutAction.FullScreen, "切换全屏", "切换全屏预览。Esc 始终可以退出。", new ShortcutGesture(Key.F11)),
        Define(ShortcutAction.Previous, "上一张", "查看上一张照片。双图对比时轮换候选。", new ShortcutGesture(Key.Left)),
        Define(ShortcutAction.Next, "下一张", "查看下一张照片。双图对比时轮换候选。", new ShortcutGesture(Key.Right)),
        Define(ShortcutAction.Fit, "适合窗口", "完整显示照片，保持原比例。", new ShortcutGesture(Key.F)),
        Define(ShortcutAction.ActualPixels, "100% 原始像素", "按显示器物理像素查看 JPG 原始细节。", new ShortcutGesture(Key.Z)),
        Define(ShortcutAction.ZoomIn, "放大", "放大当前照片。", new ShortcutGesture(Key.OemPlus, ModifierKeys.Shift), new ShortcutGesture(Key.Add), new ShortcutGesture(Key.OemPlus)),
        Define(ShortcutAction.ZoomOut, "缩小", "缩小当前照片。", new ShortcutGesture(Key.OemMinus), new ShortcutGesture(Key.Subtract)),
        Define(ShortcutAction.SelectAll, "全选", "选择当前筛选范围内的所有照片。", new ShortcutGesture(Key.A, ModifierKeys.Control)),
        Define(ShortcutAction.Undo, "撤回上一步", "撤回最近一次评分、标记或配对。", new ShortcutGesture(Key.Z, ModifierKeys.Control)),
        Define(ShortcutAction.Unrate, "清除星级", "将操作对象设为未评分。", new ShortcutGesture(Key.D0), new ShortcutGesture(Key.NumPad0)),
        Define(ShortcutAction.Rate1, "评为 1 星", "给操作对象评 1 星。", new ShortcutGesture(Key.D1), new ShortcutGesture(Key.NumPad1)),
        Define(ShortcutAction.Rate2, "评为 2 星", "给操作对象评 2 星。", new ShortcutGesture(Key.D2), new ShortcutGesture(Key.NumPad2)),
        Define(ShortcutAction.Rate3, "评为 3 星", "给操作对象评 3 星。", new ShortcutGesture(Key.D3), new ShortcutGesture(Key.NumPad3)),
        Define(ShortcutAction.Rate4, "评为 4 星", "给操作对象评 4 星。", new ShortcutGesture(Key.D4), new ShortcutGesture(Key.NumPad4)),
        Define(ShortcutAction.Rate5, "评为 5 星", "给操作对象评 5 星。", new ShortcutGesture(Key.D5), new ShortcutGesture(Key.NumPad5)),
        Define(ShortcutAction.Keep, "标记保留", "把操作对象标记为保留。", new ShortcutGesture(Key.P)),
        Define(ShortcutAction.Reject, "标记淘汰", "把操作对象标记为淘汰，文件继续保存在磁盘。", new ShortcutGesture(Key.X)),
        Define(ShortcutAction.Undecide, "标记未定", "把操作对象的取舍状态恢复为未定。", new ShortcutGesture(Key.U))
    ];

    public event PropertyChangedEventHandler? PropertyChanged;
    public string this[string id] => Enum.TryParse<ShortcutAction>(id, out var action) ? Hint(action) : "";
    public Dictionary<ShortcutAction, ShortcutGesture?> CreateDraft() => new(overrides);
    public bool TryResolve(ShortcutGesture gesture, out ShortcutAction action) => actions.TryGetValue(gesture, out action);
    public string BindingText(ShortcutAction action) => BindingText(Definitions.First(d => d.Action == action), overrides);

    public string Hint(ShortcutAction action, string? description = null)
    {
        var definition = Definitions.First(d => d.Action == action);
        var text = description ?? definition.Hint;
        var keys = Effective(definition, overrides);
        return keys.Count == 0 ? text : $"{text}\n快捷键  {BindingText(definition, overrides)}";
    }

    public static string BindingText(ShortcutDefinition definition, IReadOnlyDictionary<ShortcutAction, ShortcutGesture?> draft)
    {
        var keys = Effective(definition, draft);
        return keys.Count == 0 ? "未绑定" : string.Join(" / ", keys.Select(g => g.Label));
    }

    private static IReadOnlyList<ShortcutGesture> Effective(ShortcutDefinition definition, IReadOnlyDictionary<ShortcutAction, ShortcutGesture?> draft) =>
        draft.TryGetValue(definition.Action, out var gesture) ? gesture is { } binding ? [binding] : [] : definition.Defaults;

    private static Dictionary<ShortcutGesture, ShortcutAction> BuildLookup(IReadOnlyDictionary<ShortcutAction, ShortcutGesture?> draft)
    {
        var lookup = new Dictionary<ShortcutGesture, ShortcutAction>();
        foreach (var definition in Definitions)
        {
            foreach (var key in Effective(definition, draft))
            {
                if (!key.IsAssignable) throw new InvalidOperationException($"{definition.Name} 使用了系统或控件保留的键。");
                if (!lookup.TryAdd(key, definition.Action))
                {
                    var other = Definitions.First(d => d.Action == lookup[key]);
                    throw new InvalidOperationException($"{key.Label} 同时被「{other.Name}」和「{definition.Name}」占用。");
                }
            }
        }
        return lookup;
    }

    public static string? ValidationError(IReadOnlyDictionary<ShortcutAction, ShortcutGesture?> draft)
    {
        try { BuildLookup(draft); return null; }
        catch (InvalidOperationException ex) { return ex.Message; }
    }

    public void Load(TaskStore store)
    {
        var json = store.GetSetting(SettingKey);
        if (json == null) return;
        var saved = JsonSerializer.Deserialize<Dictionary<ShortcutAction, ShortcutGesture?>>(json)
            ?? throw new InvalidDataException("快捷键设置为空。");
        Replace(saved, BuildLookup(saved));
    }

    public async Task SaveAsync(TaskStore store, Dictionary<ShortcutAction, ShortcutGesture?> draft)
    {
        var snapshot = new Dictionary<ShortcutAction, ShortcutGesture?>(draft);
        var lookup = BuildLookup(snapshot);
        var json = JsonSerializer.Serialize(snapshot);
        await Task.Run(() => store.SetSetting(SettingKey, json));
        Replace(snapshot, lookup);
    }

    private void Replace(Dictionary<ShortcutAction, ShortcutGesture?> snapshot, Dictionary<ShortcutGesture, ShortcutAction> lookup)
    {
        overrides = snapshot;
        actions = lookup;
        PropertyChanged?.Invoke(this, new("Item[]"));
    }

    public static bool IsEditing(DependencyObject? element, Key key = Key.None)
    {
        bool navigating = key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown;
        for (var node = element; node != null; node = node is Visual or Visual3D
                 ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is TextBoxBase or PasswordBox or ComboBox or ComboBoxItem or MenuItem or Slider or ScrollBar
                || navigating && node is TreeViewItem or RadioButton) return true;
        return false;
    }
}
