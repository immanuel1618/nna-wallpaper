using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;
using NNA.Wallpaper.Engine;
using NNA.Wallpaper.Host;

namespace NNA.Wallpaper;

/// <summary>
/// System tray icon and context menu. Menu text follows <c>app.json</c>'s <c>language</c> (ru/en),
/// read once at construction time. Every action here runs on the UI thread already (tray clicks are
/// UI events), so it talks to <see cref="WallpaperEngine"/> and the windows directly.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly HostContext _host;
    private readonly WallpaperEngine? _engine;
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _pauseItem;
    private readonly bool _ru;
    private bool _disposed;

    public TrayIcon(HostContext host, WallpaperEngine? engine)
    {
        _host = host;
        _engine = engine;
        _ru = string.Equals(host.Config.App.Language, "ru", StringComparison.OrdinalIgnoreCase);

        _icon = new TaskbarIcon
        {
            ToolTipText = "NNA Wallpaper",
            Icon = LoadIcon(),
        };
        _icon.TrayMouseDoubleClick += (_, _) => OpenSettings();

        _pauseItem = new MenuItem { Header = _ru ? "Пауза обоев" : "Pause wallpaper", IsCheckable = true };
        _pauseItem.Click += (_, _) => TogglePause();

        var menu = new ContextMenu();
        menu.Items.Add(MakeItem(_ru ? "Открыть настройки" : "Open settings", OpenSettings));
        menu.Items.Add(_pauseItem);
        menu.Items.Add(MakeItem(_ru ? "Перезагрузить обои" : "Reload wallpaper", Reload));
        menu.Items.Add(MakeItem(_ru ? "Импорт из папки..." : "Import from folder...", ImportFromFolder));
        menu.Items.Add(MakeItem(_ru ? "Проверить обновления" : "Check for updates", CheckUpdates));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem(_ru ? "Выход" : "Exit", ExitApp));
        _icon.ContextMenu = menu;
    }

    public void Show() => _icon.Visibility = Visibility.Visible;

    public void ShowBalloon(string title, string message, BalloonIcon icon = BalloonIcon.Info) =>
        _icon.ShowBalloonTip(title, message, icon);

    private static MenuItem MakeItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void OpenSettings() => SettingsWindow.Open(_host, null);

    private void TogglePause() => _engine?.SetUserPause(_pauseItem.IsChecked);

    private void Reload() => _engine?.ReloadWallpaper();

    private void ImportFromFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = _ru ? "Выберите папку для импорта" : "Choose a folder to import",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var report = Host.Import.Run(_host.Paths, _host.Config, _host.Log, dialog.FolderName, _host.App.Monitors);
            _host.Log.Info("import (tray): " + report);
            _engine?.ReloadWallpaper();
            ShowBalloon("NNA Wallpaper", _ru ? "Импорт завершён" : "Import complete");
        }
        catch (Exception ex)
        {
            _host.Log.Error("import (tray) failed", ex);
            ShowBalloon("NNA Wallpaper", (_ru ? "Импорт не удался: " : "Import failed: ") + ex.Message, BalloonIcon.Error);
        }
    }

    private async void CheckUpdates()
    {
        var check = await Updates.CheckAsync(_host).ConfigureAwait(true);
        if (!check.ok)
        {
            ShowBalloon("NNA Wallpaper", (_ru ? "Не удалось проверить обновления: " : "Update check failed: ") + check.error, BalloonIcon.Error);
            return;
        }
        if (check.installed != true)
        {
            ShowBalloon("NNA Wallpaper", _ru ? "Сборка без установщика: обновления вручную" : "Unpacked build: update manually");
            return;
        }
        if (check.available is null)
        {
            ShowBalloon("NNA Wallpaper", (_ru ? "Обновлений нет, версия " : "Up to date, version ") + check.current);
            return;
        }
        ShowBalloon("NNA Wallpaper", (_ru ? "Скачиваю обновление " : "Downloading update ") + check.available + (_ru ? ", приложение перезапустится" : ", the app will restart"));
        var apply = await Updates.ApplyAsync(_host).ConfigureAwait(true);
        if (!apply.ok) ShowBalloon("NNA Wallpaper", (_ru ? "Обновление не удалось: " : "Update failed: ") + apply.error, BalloonIcon.Error);
    }

    private void ExitApp() => _host.App.RequestExit();

    private static System.Drawing.Icon? LoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            return string.IsNullOrEmpty(path) ? null : System.Drawing.Icon.ExtractAssociatedIcon(path);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Dispose();
    }
}
