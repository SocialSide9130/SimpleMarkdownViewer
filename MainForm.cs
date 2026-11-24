using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace SimpleMarkdownViewer;

public sealed class MainForm : Form
{
    const string AppTitle = "Simple Markdown Viewer";
    const string ShellUrl = PathUrl.Origin + PathUrl.AppPrefix + "index.html";

    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    readonly string[] initialArgs;
    bool initialSent;
    string theme = "auto";

    readonly Dictionary<string, FileSystemWatcher> watchers = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, System.Windows.Forms.Timer> pendingChanges = new(StringComparer.OrdinalIgnoreCase);

    public MainForm(string[] args)
    {
        initialArgs = args;
        Text = AppTitle;
        ClientSize = new Size(1280, 820);
        MinimumSize = new Size(480, 320);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        Controls.Add(web);
        Load += async (_, _) => await InitializeWebViewAsync();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTitleBarTheme();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            foreach (var w in watchers.Values) w.Dispose();
            foreach (var t in pendingChanges.Values) t.Dispose();
        }
        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------ WebView2 初期化

    async Task InitializeWebViewAsync()
    {
        try
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SimpleMarkdownViewer", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await web.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this,
                "Microsoft Edge WebView2 ランタイムが見つかりません。\n" +
                "https://developer.microsoft.com/microsoft-edge/webview2/ からインストールしてください。",
                AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "WebView2 の初期化に失敗しました。\n" + ex.Message,
                AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        var core = web.CoreWebView2;
#if DEBUG
        core.Settings.AreDevToolsEnabled = true;
#else
        core.Settings.AreDevToolsEnabled = false;
#endif
        core.Settings.IsStatusBarEnabled = true;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;

        core.AddWebResourceRequestedFilter(PathUrl.Origin + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.NavigationStarting += OnNavigationStarting;
        core.FrameNavigationStarting += OnFrameNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.WebMessageReceived += OnWebMessageReceived;

        core.Navigate(ShellUrl);
    }

    // ------------------------------------------------------------------ リソース配信

    void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var env = web.CoreWebView2.Environment;
        Uri uri;
        try { uri = new Uri(e.Request.Uri); }
        catch { return; }

        try
        {
            if (uri.AbsolutePath.StartsWith(PathUrl.AppPrefix, StringComparison.Ordinal))
            {
                e.Response = ServeEmbedded(env, uri.AbsolutePath[PathUrl.AppPrefix.Length..]);
                return;
            }

            var path = PathUrl.ToPath(uri);
            if (path == null)
            {
                e.Response = TextResponse(env, 404, "Not Found", "text/plain; charset=utf-8", "Not Found");
                return;
            }

            bool render = PathUrl.IsMarkdown(path) ||
                          uri.Query.Contains("view=md", StringComparison.OrdinalIgnoreCase);

            if (render)
            {
                Watch(path);
                string html;
                int status = 200;
                if (File.Exists(path))
                {
                    try { html = MarkdownRenderer.RenderPage(path); }
                    catch (Exception ex) { html = MarkdownRenderer.RenderError(path, ex.Message); status = 500; }
                }
                else
                {
                    html = MarkdownRenderer.RenderError(path, "ファイルが見つかりません。");
                    status = 404;
                }
                e.Response = TextResponse(env, status, status == 200 ? "OK" : "Error",
                    "text/html; charset=utf-8", html,
                    "Content-Security-Policy: " + MarkdownRenderer.PageCsp);
                return;
            }

            if (File.Exists(path))
            {
                var bytes = MarkdownRenderer.ReadAllBytesShared(path);
                var headers = $"Content-Type: {PathUrl.ContentTypeOf(path)}\r\nCache-Control: no-store";
                if (!PathUrl.IsImage(path))
                    headers += "\r\nContent-Security-Policy: sandbox"; // 生ファイルとして開かれたHTML等でスクリプトを実行させない
                e.Response = env.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK", headers);
                return;
            }

            e.Response = TextResponse(env, 404, "Not Found", "text/plain; charset=utf-8", "Not Found: " + path);
        }
        catch (Exception ex)
        {
            e.Response = TextResponse(env, 500, "Error", "text/plain; charset=utf-8", ex.ToString());
        }
    }

    static CoreWebView2WebResourceResponse TextResponse(CoreWebView2Environment env, int status, string reason,
        string contentType, string body, string? extraHeader = null)
    {
        var headers = $"Content-Type: {contentType}\r\nCache-Control: no-store";
        if (extraHeader != null) headers += "\r\n" + extraHeader;
        return env.CreateWebResourceResponse(
            new MemoryStream(Encoding.UTF8.GetBytes(body)), status, reason, headers);
    }

    static CoreWebView2WebResourceResponse ServeEmbedded(CoreWebView2Environment env, string name)
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("web/" + name);
        if (stream == null)
            return TextResponse(env, 404, "Not Found", "text/plain; charset=utf-8", "Not Found");
        var type = Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream",
        };
        return env.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {type}\r\nCache-Control: no-store");
    }

    // ------------------------------------------------------------------ ナビゲーション制御

    static bool IsInternal(string uri) =>
        uri.StartsWith(PathUrl.Origin + "/", StringComparison.OrdinalIgnoreCase);

    void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // シェル画面以外へのトップレベル遷移はさせない
        if (e.Uri.StartsWith(ShellUrl, StringComparison.OrdinalIgnoreCase)) return;
        e.Cancel = true;
        if (e.IsUserInitiated) OpenExternal(e.Uri);
    }

    void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsInternal(e.Uri) || e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;

        if (e.IsUserInitiated)
        {
            // Markdown 内の外部リンクは既定のブラウザで開く
            e.Cancel = true;
            OpenExternal(e.Uri);
            return;
        }
        // 埋め込みコンテンツ（動画等）は http(s) のみ許可。file: などへの遷移は拒否
        if (!(e.Uri.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
              e.Uri.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
              e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)))
            e.Cancel = true;
    }

    void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (IsInternal(e.Uri))
        {
            // 内部リンクを新しいウィンドウで開こうとした場合（Ctrl+クリック等）は新しいペインで開く
            var path = PathUrl.ToPath(new Uri(e.Uri));
            if (path != null) OpenPaths([path], -1);
            return;
        }
        OpenExternal(e.Uri);
    }

    static void OpenExternal(string uri)
    {
        if (!(uri.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
              uri.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
              uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)))
            return;
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch { }
    }

    // ------------------------------------------------------------------ シェルとのメッセージ

    void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(ShellUrl, StringComparison.OrdinalIgnoreCase)) return;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(e.WebMessageAsJson); }
        catch { return; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var typeEl)) return;
            int at = root.TryGetProperty("at", out var atEl) && atEl.ValueKind == JsonValueKind.Number
                ? atEl.GetInt32() : -1;

            switch (typeEl.GetString())
            {
                case "ready":
                    if (!initialSent)
                    {
                        initialSent = true;
                        OpenPaths(initialArgs, -1);
                    }
                    break;

                case "openDialog":
                    BeginInvoke(() => ShowOpenDialog(at));
                    break;

                case "drop":
                    var paths = new List<string>();
                    if (e.AdditionalObjects != null)
                        foreach (var obj in e.AdditionalObjects)
                            if (obj is CoreWebView2File f && !string.IsNullOrEmpty(f.Path))
                                paths.Add(f.Path);
                    OpenPaths(paths, at);
                    break;

                case "theme":
                    if (root.TryGetProperty("value", out var v)) SetTheme(v.GetString() ?? "auto");
                    break;

                case "title":
                    var t = root.TryGetProperty("value", out var tv) ? tv.GetString() : null;
                    Text = string.IsNullOrEmpty(t) ? AppTitle : $"{t} - {AppTitle}";
                    break;
            }
        }
    }

    void Post(object message)
    {
        if (web.CoreWebView2 == null) return;
        web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    void ShowOpenDialog(int at)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Markdown ファイルを開く",
            Multiselect = true,
            Filter = "Markdown (*.md;*.markdown;*.mdown;*.mkd;*.mkdn;*.mdwn)|*.md;*.markdown;*.mdown;*.mkd;*.mkdn;*.mdwn|" +
                     "テキスト (*.txt)|*.txt|すべてのファイル (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            OpenPaths(dlg.FileNames, at);
    }

    void OpenPaths(IEnumerable<string> inputs, int at)
    {
        var files = new List<object>();
        var missing = new List<string>();
        foreach (var input in inputs)
        {
            string full;
            try { full = Path.GetFullPath(input); }
            catch { missing.Add(input); continue; }

            if (Directory.Exists(full))
            {
                // フォルダがドロップされたら直下の Markdown を全部開く
                var mds = Directory.EnumerateFiles(full)
                    .Where(PathUrl.IsMarkdown)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (mds.Count == 0) missing.Add(input + " (Markdown なし)");
                files.AddRange(mds.Select(MakeEntry));
            }
            else if (File.Exists(full))
            {
                files.Add(MakeEntry(full));
            }
            else missing.Add(input);
        }

        if (files.Count > 0) Post(new { type = "open", at, files });
        if (missing.Count > 0)
            Post(new { type = "error", message = "開けませんでした: " + string.Join(", ", missing) });
    }

    static object MakeEntry(string path)
    {
        var url = PathUrl.ToUrl(path);
        if (!PathUrl.IsMarkdown(path) && !PathUrl.IsImage(path)) url += "?view=md";
        return new { path, name = Path.GetFileName(path), url };
    }

    // ------------------------------------------------------------------ ファイル監視（自動再読込）

    void Watch(string file)
    {
        var dir = Path.GetDirectoryName(file);
        if (string.IsNullOrEmpty(dir) || watchers.ContainsKey(dir) || !Directory.Exists(dir)) return;
        try
        {
            var w = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = true, // サブフォルダ内の画像の更新も拾う
            };
            w.Changed += (_, ev) => OnFsEvent(ev.FullPath);
            w.Created += (_, ev) => OnFsEvent(ev.FullPath);
            w.Renamed += (_, ev) => OnFsEvent(ev.FullPath);
            w.EnableRaisingEvents = true;
            watchers[dir] = w;
        }
        catch
        {
            // ネットワークドライブ等で監視できない場合は自動再読込なし
        }
    }

    void OnFsEvent(string path)
    {
        if (!PathUrl.IsMarkdown(path) && !PathUrl.IsImage(path)) return;
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => ScheduleChanged(path)); } catch { }
    }

    void ScheduleChanged(string path)
    {
        // 保存時にイベントが連続するため 250ms デバウンス
        if (pendingChanges.TryGetValue(path, out var existing))
        {
            existing.Stop();
            existing.Start();
            return;
        }
        var timer = new System.Windows.Forms.Timer { Interval = 250 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            pendingChanges.Remove(path);
            Post(new { type = "changed", path });
        };
        pendingChanges[path] = timer;
        timer.Start();
    }

    // ------------------------------------------------------------------ テーマ

    void SetTheme(string value)
    {
        theme = value is "light" or "dark" ? value : "auto";
        if (web.CoreWebView2 != null)
        {
            web.CoreWebView2.Profile.PreferredColorScheme = theme switch
            {
                "light" => CoreWebView2PreferredColorScheme.Light,
                "dark" => CoreWebView2PreferredColorScheme.Dark,
                _ => CoreWebView2PreferredColorScheme.Auto,
            };
        }
        ApplyTitleBarTheme();
    }

    void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (theme == "auto" && IsHandleCreated) BeginInvoke(ApplyTitleBarTheme);
    }

    void ApplyTitleBarTheme()
    {
        if (!IsHandleCreated) return;
        bool dark = theme == "dark" || (theme == "auto" && SystemUsesDarkTheme());
        int value = dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref value, sizeof(int));
        BackColor = dark ? Color.FromArgb(13, 17, 23) : Color.White;
        web.DefaultBackgroundColor = BackColor;
    }

    static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
