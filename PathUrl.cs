namespace SimpleMarkdownViewer;

/// <summary>
/// ローカルファイルパスと内部URL (https://mdview.example/D/dir/file.md) の相互変換。
/// パス構造をそのままURLにすることで、Markdown内の相対パス（画像・リンク）がブラウザ側で自然に解決される。
/// </summary>
static class PathUrl
{
    public const string Host = "mdview.example"; // .local は mDNS 解決待ちで約2秒遅延するため使わない
    public const string Origin = "https://" + Host;
    public const string AppPrefix = "/__app/";

    static readonly HashSet<string> MarkdownExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".mdown", ".mkd", ".mkdn", ".mdwn", ".mdtxt", ".mdtext",
    };

    static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".bmp"] = "image/bmp",
        [".ico"] = "image/x-icon",
        [".avif"] = "image/avif",
    };

    static readonly Dictionary<string, string> OtherTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8",
        [".pdf"] = "application/pdf",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
    };

    public static bool IsMarkdown(string path) => MarkdownExts.Contains(Path.GetExtension(path));
    public static bool IsImage(string path) => ImageTypes.ContainsKey(Path.GetExtension(path));

    public static string ContentTypeOf(string path)
    {
        var ext = Path.GetExtension(path);
        if (ImageTypes.TryGetValue(ext, out var t)) return t;
        if (OtherTypes.TryGetValue(ext, out t)) return t;
        return "application/octet-stream";
    }

    public static string ToUrl(string path)
    {
        var full = Path.GetFullPath(path);
        string rest;
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            rest = @"UNC\" + full[2..];
        else if (full.Length >= 2 && full[1] == ':')
            rest = char.ToUpperInvariant(full[0]) + full[2..];
        else
            throw new ArgumentException("Unsupported path: " + path);

        var segs = rest.Split('\\').Select(Uri.EscapeDataString);
        return Origin + "/" + string.Join("/", segs);
    }

    public static string? ToPath(Uri uri)
    {
        if (!uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase)) return null;
        var segs = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
        if (segs.Length == 0) return null;

        string raw;
        if (segs[0] == "UNC")
        {
            if (segs.Length < 3) return null;
            raw = @"\\" + string.Join('\\', segs.Skip(1));
        }
        else if (segs[0].Length == 1 && char.IsAsciiLetter(segs[0][0]))
        {
            raw = segs[0] + @":\" + string.Join('\\', segs.Skip(1));
        }
        else return null;

        try { return Path.GetFullPath(raw); }
        catch { return null; }
    }
}
