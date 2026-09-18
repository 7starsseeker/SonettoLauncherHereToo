using System.Windows.Forms;

namespace SonettoHere.Launcher;

/// <summary>
/// 对话框基类：全部用 AutoSize 布局，不用固定像素尺寸 ——
/// 固定尺寸在高 DPI（150%/200%）下会把中文按钮文字裁掉。
/// </summary>
internal abstract class AutoSizedDialog : Form
{
    protected AutoSizedDialog()
    {
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Icon = AppIcon.Create();
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Microsoft YaHei UI", 9F);
        Padding = new Padding(22, 18, 22, 16);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
    }

    protected static Label CreateMessage(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(520, 0),
        Margin = new Padding(0),
    };

    protected static Button CreateButton(string text, DialogResult result) => new()
    {
        Text = text,
        DialogResult = result,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(14, 5, 14, 5),
        Margin = new Padding(10, 0, 0, 0),
        MinimumSize = new Size(0, 30),
    };

    /// <summary>按钮行：右对齐（RightToLeft 下第一个加入的显示在最右）。</summary>
    protected static FlowLayoutPanel CreateButtonRow(params Button[] buttons)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 18, 0, 0),
            WrapContents = false,
        };

        foreach (var button in buttons)
        {
            row.Controls.Add(button);
        }

        return row;
    }

    protected static TableLayoutPanel CreateRoot(params Control[] rows)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = rows.Length,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
        };

        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        for (var i = 0; i < rows.Length; i++)
        {
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(rows[i], 0, i);
        }

        return root;
    }
}

/// <summary>关闭窗口前的确认框（带「不再提示」）。</summary>
internal sealed class ConfirmCloseDialog : AutoSizedDialog
{
    private readonly CheckBox _dontAskAgain = new()
    {
        Text = "以后关闭窗口时直接停止服务，不再询问",
        AutoSize = true,
        Margin = new Padding(0, 14, 0, 0),
    };

    public ConfirmCloseDialog(string message)
    {
        Text = "关闭 SonettoHere";

        var confirm = CreateButton("停止服务并退出", DialogResult.OK);
        var cancel = CreateButton("取消", DialogResult.Cancel);

        Controls.Add(CreateRoot(
            CreateMessage(message),
            _dontAskAgain,
            CreateButtonRow(confirm, cancel)));

        AcceptButton = confirm;
        CancelButton = cancel;
    }

    public bool DontAskAgain => _dontAskAgain.Checked;
}

/// <summary>端口被占用时的三选一对话框。</summary>
internal sealed class PortConflictDialog : AutoSizedDialog
{
    public PortConflictDialog(string description, string caption)
    {
        Text = caption;

        var stopAndRestart = CreateButton("停止旧服务并重启", DialogResult.Yes);
        var adopt = CreateButton("沿用现有服务（只管显示）", DialogResult.No);
        var cancel = CreateButton("取消启动", DialogResult.Cancel);

        Controls.Add(CreateRoot(
            CreateMessage(description),
            CreateButtonRow(stopAndRestart, adopt, cancel)));

        AcceptButton = stopAndRestart;
        CancelButton = cancel;
    }

    public PortConflictChoice Choice => DialogResult switch
    {
        DialogResult.Yes => PortConflictChoice.StopAndRestart,
        DialogResult.No => PortConflictChoice.Adopt,
        _ => PortConflictChoice.Cancel,
    };
}
