using System.Windows;
using MagiDesk.Config;
using MagiDesk.Features.BrowserBadges;

namespace MagiDesk.Features.ProfileDock;

internal static class DockApplicationIconEditor
{
    private static bool CanEdit(DockApplication app, bool respectLayoutLock) => (!respectLayoutLock || !DockLayoutLock.IsLocked) && AppConfig.Current.DockApplications.Contains(app);

    internal static void EditText(DockApplication app, Window? owner, bool respectLayoutLock = true)
    {
        if (!CanEdit(app, respectLayoutLock)) return;
        var draft = app.IconStyle?.Copy() ?? new AvatarStyle();
        var editor = new AvatarTextEditorWindow(app.Name, app.Name, draft, maxTextLength: 0) { Owner = owner };
        if (editor.ShowDialog() != true || !CanEdit(app, respectLayoutLock)) return;
        app.IconStyle = editor.WasReset ? null : draft;
        app.IconPath = null;
        AppConfig.Current.Save();
    }

    internal static async Task<string?> ChooseImage(DockApplication app, Window? owner, bool respectLayoutLock = true)
    {
        if (!CanEdit(app, respectLayoutLock)) return null;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择应用图标", Filter = "图标或图片|*.ico;*.png;*.jpg;*.jpeg;*.bmp" };
        if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) != true) return null;
        DockApplicationIcons.Invalidate(dialog.FileName);
        var image = await DockApplicationIcons.LoadAsync(dialog.FileName);
        if (!CanEdit(app, respectLayoutLock)) return null;
        if (image is null) return "无法读取图片，请选择有效的 ICO、PNG、JPG 或 BMP 文件。";
        app.IconPath = dialog.FileName;
        app.IconStyle = null;
        app.IconRevision++;
        AppConfig.Current.Save();
        return "图标已更新，请保留原图片文件；可点击“恢复图标”使用应用原图标。";
    }

    internal static void Reset(DockApplication app, bool respectLayoutLock = true)
    {
        if (!CanEdit(app, respectLayoutLock)) return;
        app.IconPath = null;
        app.IconStyle = null;
        AppConfig.Current.Save();
    }
}
