using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace MntWatch;

/// <summary>The tray icon and its menu. Everything MntWatch does is reachable from here.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private const string RepoUrl = "https://github.com/BoazCohenJ/MntWatch";

    private readonly Settings _settings = Settings.Load();
    private readonly NotifyIcon _tray;
    private readonly Control _ui = new();
    private readonly Icon _active = Icons.Make(Icons.Active, 32);
    private readonly Icon _waiting = Icons.Make(Icons.Waiting, 32);
    private readonly Icon _problem = Icons.Make(Icons.Problem, 32);
    private Session _session;

    public TrayApp(bool firstLaunchHint)
    {
        _ui.CreateControl();
        _tray = new NotifyIcon { Icon = _waiting, Text = "MntWatch", Visible = true, ContextMenuStrip = new ContextMenuStrip() };
        _tray.ContextMenuStrip.Opening += (_, _) => BuildMenu();
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowMenu();
        };
        Log.Write($"MntWatch {Program.Version} started.");
        Restart();

        if (_settings.FirstRun || (firstLaunchHint && _settings.Folders.Count == 0))
        {
            _tray.ShowBalloonTip(8000, "MntWatch is in the tray",
                "Click the icon and choose \"Add folder\" for each Windows project your Linux tools watch under /mnt/c.",
                ToolTipIcon.Info);
            _settings.Save();
        }
    }

    public void ShowFromOtherInstance() => _ui.BeginInvoke(new Action(ShowMenu));

    private void ShowMenu()
    {
        // NotifyIcon only shows its menu on right-click; this private method does it for left-click too.
        typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.Invoke(_tray, null);
    }

    private void Restart()
    {
        var old = _session;
        _session = null;
        old?.Dispose();
        var session = new Session(_settings.Distro, _settings.Folders, startWsl: false, paused: _settings.Paused);
        session.Log += Log.Write;
        session.Changed += () => _ui.BeginInvoke(new Action(UpdateIcon));
        _session = session;
        session.Start();
        UpdateIcon();
    }

    private void UpdateIcon()
    {
        var s = _session;
        if (s == null) return;
        var folders = s.Folders;
        int active = folders.Count(f => f.State == FolderState.Active);
        bool error = folders.Any(f => f.State == FolderState.Error);
        _tray.Icon = error ? _problem : active > 0 ? _active : _waiting;
        string text;
        if (folders.Count == 0) text = "MntWatch: no folders yet";
        else if (_settings.Paused) text = "MntWatch: paused";
        else if (error) text = "MntWatch: " + folders.First(f => f.State == FolderState.Error).Message;
        else if (active > 0) text = $"MntWatch: {active} of {folders.Count} folder(s) live in {s.Distro}";
        else text = "MntWatch: " + (folders.FirstOrDefault()?.Message ?? "waiting for WSL");
        _tray.Text = text.Length > 63 ? text.Substring(0, 60) + "..." : text;
    }

    private void BuildMenu()
    {
        var menu = _tray.ContextMenuStrip;
        menu.Items.Clear();
        var s = _session;
        string distro = _settings.Distro.Length > 0 ? _settings.Distro : (s?.Distro != null ? s.Distro + " (default)" : "default distro");
        menu.Items.Add(new ToolStripMenuItem($"MntWatch {Program.Version}: {distro}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        if (s != null && s.Folders.Count > 0)
        {
            foreach (var f in s.Folders)
            {
                string state = f.State switch
                {
                    FolderState.Active => "live",
                    FolderState.Paused => "paused",
                    FolderState.Error => "problem",
                    _ => "waiting",
                };
                var item = new ToolStripMenuItem($"{f.WindowsPath}  ({state})");
                item.DropDownItems.Add(new ToolStripMenuItem(f.Message.Length > 0 ? f.Message : state) { Enabled = false });
                if (f.State == FolderState.Active)
                    item.DropDownItems.Add(new ToolStripMenuItem($"{f.Nudged} change(s) forwarded, last in {Math.Max(0, f.LastLatencyMs)} ms") { Enabled = false });
                item.DropDownItems.Add(new ToolStripSeparator());
                string path = f.WindowsPath;
                item.DropDownItems.Add("Open in Explorer", null, (_, _) => Open(path));
                item.DropDownItems.Add("Stop watching", null, (_, _) =>
                {
                    _settings.Folders.RemoveAll(x => x.Equals(path, StringComparison.OrdinalIgnoreCase));
                    _settings.Save();
                    Restart();
                });
                menu.Items.Add(item);
            }
        }
        else
        {
            menu.Items.Add(new ToolStripMenuItem("No folders yet") { Enabled = false });
        }
        menu.Items.Add("Add folder...", null, (_, _) => AddFolder());
        menu.Items.Add(new ToolStripSeparator());

        var distros = new ToolStripMenuItem("WSL distro");
        var installed = Wsl.Installed() ?? new System.Collections.Generic.List<string>();
        var def = new ToolStripMenuItem("Default distro") { Checked = _settings.Distro.Length == 0 };
        def.Click += (_, _) => SetDistro("");
        distros.DropDownItems.Add(def);
        foreach (string d in installed)
        {
            var item = new ToolStripMenuItem(d) { Checked = d.Equals(_settings.Distro, StringComparison.OrdinalIgnoreCase) };
            string name = d;
            item.Click += (_, _) => SetDistro(name);
            distros.DropDownItems.Add(item);
        }
        menu.Items.Add(distros);

        var pause = new ToolStripMenuItem("Pause") { Checked = _settings.Paused };
        pause.Click += (_, _) =>
        {
            _settings.Paused = !_settings.Paused;
            _settings.Save();
            if (_session != null) _session.Paused = _settings.Paused;
            UpdateIcon();
        };
        menu.Items.Add(pause);
        var startup = new ToolStripMenuItem("Start with Windows") { Checked = Settings.StartWithWindows };
        startup.Click += (_, _) => Settings.StartWithWindows = !Settings.StartWithWindows;
        menu.Items.Add(startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Run self-test", null, (_, _) => RunSelfTest());
        menu.Items.Add("Open log", null, (_, _) => Open(Settings.LogPath));
        menu.Items.Add("Help and source (GitHub)", null, (_, _) => Open(RepoUrl));
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
    }

    private void AddFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose a Windows folder that Linux tools in WSL watch (for example a project you run with npm run dev).",
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        string folder = Paths.Resolve(dialog.SelectedPath) ?? dialog.SelectedPath;
        if (folder.StartsWith(@"\\"))
        {
            MessageBox.Show("Network and \\\\wsl$ paths aren't supported. Pick a folder on a local drive.", "MntWatch",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!_settings.AddFolder(folder))
        {
            MessageBox.Show("That folder, or one above or inside it, is already watched.", "MntWatch",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _settings.Save();
        Restart();
        _tray.ShowBalloonTip(6000, "Folder added",
            $"{folder} goes live once WSL is running. Restart Linux programs that were already running inside it.", ToolTipIcon.Info);
    }

    private void SetDistro(string name)
    {
        _settings.Distro = name;
        _settings.Save();
        Restart();
    }

    private static void RunSelfTest()
    {
        // The exe is a GUI app, so give the self-test a console window that stays open to read.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"\"{Application.ExecutablePath}\" --self-test\"") { UseShellExecute = true });
    }

    private static void Open(string target)
    {
        try
        {
            if (target.EndsWith(".log") && !File.Exists(target)) File.WriteAllText(target, "");
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is IOException)
        {
        }
    }

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _session?.Dispose();
        Log.Write("MntWatch exited.");
        _tray.Dispose();
        _active.Dispose();
        _waiting.Dispose();
        _problem.Dispose();
        base.ExitThreadCore();
    }
}
