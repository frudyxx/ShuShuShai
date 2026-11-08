using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace ShuShuShai.App.Views;

internal enum CloseChoice { Cancel, Minimize, Exit }

internal sealed class ClosePrompt : Window
{
    public CloseChoice Choice { get; private set; }

    public ClosePrompt(Window owner)
    {
        Owner = owner;
        Title = "关闭舒舒筛";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 382;
        ShowInTaskbar = false;
        SetResourceReference(StyleProperty, typeof(Window));

        var exit = ChoiceButton("关闭软件", "ExitApplication", CloseChoice.Exit);
        var minimize = ChoiceButton("最小化", "MinimizeApplication", CloseChoice.Minimize);
        var cancel = ChoiceButton("取消", "CancelClose", CloseChoice.Cancel);
        cancel.IsCancel = true;
        cancel.IsDefault = true;
        ContentRendered += (_, _) => Keyboard.Focus(cancel);

        var buttons = DialogUi.Row(exit, minimize, cancel);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        var body = new StackPanel { Margin = new Thickness(16, 12, 16, 14) };
        body.Children.Add(DialogUi.Text("要关闭软件，还是最小化到任务栏？", 15));
        body.Children.Add(buttons);
        Content = body;
    }

    private Button ChoiceButton(string label, string automationId, CloseChoice choice)
    {
        var button = DialogUi.Button(label, (_, _) => { Choice = choice; DialogResult = choice != CloseChoice.Cancel; });
        AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }
}
