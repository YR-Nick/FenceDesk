using System.Drawing;
using WinForms = System.Windows.Forms;

namespace FenceDesk.Services;

public sealed class TrayService : IDisposable
{
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly Icon _icon;
    private readonly WinForms.ToolStripMenuItem _toggleItem;
    private bool _disposed;

    public TrayService(
        Action toggleFences,
        Action openSearch,
        Action organizeDesktop,
        Action newFence,
        Action openSettings,
        Action exit)
    {
        _icon = LoadIcon();

        _toggleItem = new WinForms.ToolStripMenuItem("隐藏桌面围栏")
        {
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
        };
        _toggleItem.Click += (_, _) => toggleFences();

        var searchItem = new WinForms.ToolStripMenuItem("搜索围栏    Ctrl+Alt+F");
        searchItem.Click += (_, _) => openSearch();

        var organizeItem = new WinForms.ToolStripMenuItem("整理桌面");
        organizeItem.Click += (_, _) => organizeDesktop();

        var newFenceItem = new WinForms.ToolStripMenuItem("新建围栏");
        newFenceItem.Click += (_, _) => newFence();

        var settingsItem = new WinForms.ToolStripMenuItem("设置");
        settingsItem.Click += (_, _) => openSettings();

        var exitItem = new WinForms.ToolStripMenuItem("退出 FenceDesk");
        exitItem.Click += (_, _) => exit();

        _menu = new WinForms.ContextMenuStrip
        {
            BackColor = Color.FromArgb(21, 24, 32),
            ForeColor = Color.FromArgb(238, 242, 248),
            ShowImageMargin = false,
            Font = new Font("Microsoft YaHei UI", 9f),
            Padding = new WinForms.Padding(5),
            Renderer = new DarkMenuRenderer(),
        };
        _menu.Items.Add(new WinForms.ToolStripLabel("FenceDesk · 桌面围栏")
        {
            ForeColor = Color.FromArgb(51, 214, 192),
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
            Margin = new WinForms.Padding(5, 4, 5, 4),
        });
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(_toggleItem);
        _menu.Items.Add(searchItem);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(newFenceItem);
        _menu.Items.Add(organizeItem);
        _menu.Items.Add(settingsItem);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _icon,
            Text = "FenceDesk 桌面围栏",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _notifyIcon.DoubleClick += (_, _) => openSearch();
    }

    public void UpdateHidden(bool hidden)
    {
        _toggleItem.Text = hidden ? "显示桌面围栏" : "隐藏桌面围栏";
    }

    public void ShowBalloon(string title, string text)
    {
        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = text;
            _notifyIcon.BalloonTipIcon = WinForms.ToolTipIcon.Info;
            _notifyIcon.ShowBalloonTip(2500);
        }
        catch
        {
            // ignore
        }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/FenceDesk.ico"));
            if (info is not null)
            {
                using var stream = info.Stream;
                using var loaded = new Icon(stream);
                return (Icon)loaded.Clone();
            }
        }
        catch (Exception ex)
        {
            LogService.Error("加载托盘图标失败", ex);
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }

    private sealed class DarkMenuRenderer : WinForms.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColorTable())
        {
            RoundedEdges = true;
        }

        protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? Color.White : Color.FromArgb(226, 232, 242);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderToolStripBorder(WinForms.ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Color.FromArgb(45, 255, 255, 255));
            var rect = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            e.Graphics.DrawRectangle(pen, rect);
        }
    }

    private sealed class DarkColorTable : WinForms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.FromArgb(21, 24, 32);
        public override Color ImageMarginGradientBegin => Color.FromArgb(21, 24, 32);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(21, 24, 32);
        public override Color ImageMarginGradientEnd => Color.FromArgb(21, 24, 32);
        public override Color MenuBorder => Color.FromArgb(42, 48, 60);
        public override Color MenuItemBorder => Color.FromArgb(51, 214, 192);
        public override Color MenuItemSelected => Color.FromArgb(42, 48, 61);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(42, 48, 61);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(42, 48, 61);
        public override Color SeparatorDark => Color.FromArgb(38, 44, 56);
        public override Color SeparatorLight => Color.FromArgb(38, 44, 56);
    }
}
