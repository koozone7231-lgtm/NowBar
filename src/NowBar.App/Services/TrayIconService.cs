using NowBar.App.Helpers;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace NowBar.App.Services;

/// <summary>시스템 트레이 아이콘과 메뉴 (작업 표시줄에 창이 없으므로 종료/설정 진입점 역할).</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _visibilityItem;

    public TrayIconService(Action togglePinnedExpand, Action toggleVisible, Action openSettings, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("패널 고정해서 열기/닫기", null, (_, _) => togglePinnedExpand());
        _visibilityItem = new Forms.ToolStripMenuItem("NowBar 숨기기", null, (_, _) => toggleVisible());
        menu.Items.Add(_visibilityItem);
        menu.Items.Add("설정...", null, (_, _) => openSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => exit());

        _icon = new Forms.NotifyIcon
        {
            Text = "NowBar",
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) togglePinnedExpand();
        };
        _icon.DoubleClick += (_, _) => openSettings();
    }

    public void SetVisibleState(bool nowbarVisible) =>
        _visibilityItem.Text = nowbarVisible ? "NowBar 숨기기" : "NowBar 보이기";

    private static Drawing.Icon LoadIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Resources/Icons/nowbar.ico"));
            if (info is not null)
            {
                using var stream = info.Stream;
                return new Drawing.Icon(stream, new Drawing.Size(16, 16));
            }
        }
        catch (Exception ex)
        {
            Logger.Error("트레이 아이콘 로드 실패", ex);
        }
        return Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
