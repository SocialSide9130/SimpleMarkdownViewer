using System.Text;

namespace SimpleMarkdownViewer;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Shift_JIS (CP932) などのレガシーエンコーディングを使えるようにする
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args));
    }
}
