using System.Net;
using System.Text;
using Markdig;

namespace SimpleMarkdownViewer;

static class MarkdownRenderer
{
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()   // 表・タスクリスト・脚注・自動リンク・見出しID など
        .UseYamlFrontMatter()      // 先頭の YAML front matter は表示しない
        .Build();

    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>BOM / UTF-8 / Shift_JIS を判定してテキストを読み込む。</summary>
    public static string ReadText(string path)
    {
        var bytes = ReadAllBytesShared(path);
        return Decode(bytes);
    }

    public static byte[] ReadAllBytesShared(string path)
    {
        // エディタが保存中でも読めるよう共有モードで開き、短時間リトライする
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                return ms.ToArray();
            }
            catch (IOException) when (attempt < 5 && File.Exists(path))
            {
                Thread.Sleep(40);
            }
        }
    }

    static string Decode(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
            return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
            return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        try
        {
            return StrictUtf8.GetString(b);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(932).GetString(b);
        }
    }

    public static string RenderPage(string path)
    {
        var body = Markdown.ToHtml(ReadText(path), Pipeline);
        return Wrap(path, Path.GetFileName(path), body);
    }

    public static string RenderError(string path, string message)
    {
        var body = $"""
            <div class="mdview-error">
              <h2>ファイルを表示できません</h2>
              <p><code>{WebUtility.HtmlEncode(path)}</code></p>
              <p>{WebUtility.HtmlEncode(message)}</p>
            </div>
            """;
        return Wrap(path, Path.GetFileName(path), body);
    }

    static string Wrap(string path, string title, string body) => $"""
        <!DOCTYPE html>
        <html lang="ja">
        <head>
        <meta charset="utf-8">
        <meta name="mdview-path" content="{WebUtility.HtmlEncode(path)}">
        <title>{WebUtility.HtmlEncode(title)}</title>
        <link rel="stylesheet" href="/__app/github-markdown.css">
        <link rel="stylesheet" href="/__app/hljs-light.css" media="(prefers-color-scheme: light)">
        <link rel="stylesheet" href="/__app/hljs-dark.css" media="(prefers-color-scheme: dark)">
        <link rel="stylesheet" href="/__app/page.css">
        </head>
        <body>
        <article class="markdown-body">
        {body}
        </article>
        <script src="/__app/highlight.min.js"></script>
        <script src="/__app/page.js"></script>
        </body>
        </html>
        """;

    /// <summary>
    /// 表示ページ用 CSP。Markdown 内の生 HTML に含まれるスクリプトは実行させない
    /// （アプリ同梱スクリプトのみ許可）。リモート画像は許可。
    /// </summary>
    public const string PageCsp =
        "default-src 'self'; " +
        "script-src " + PathUrl.Origin + PathUrl.AppPrefix + "; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src * data: blob:; media-src * data: blob:; font-src 'self' data:; " +
        "frame-src *; connect-src 'none'; object-src 'none'; form-action 'none'";
}
