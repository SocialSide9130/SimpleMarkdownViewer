# Simple Markdown Viewer

閲覧専用のシンプルな Markdown ビューワ（Windows）。

## 機能

- 複数ファイルを同時に開き、左右に並べて表示（仕切りをドラッグで幅変更、ダブルクリックで均等割り）
- ドラッグ＆ドロップで開く（ペイン上にドロップ → そのペインの右に追加 / それ以外 → 末尾に追加。フォルダをドロップすると直下の Markdown をすべて開く）
- ファイル変更時の自動再読込（スクロール位置を保持。参照している画像の更新にも追従）
- 画像表示（相対パス・リモート画像）
- コードのシンタックスハイライト（highlight.js 同梱・オフライン動作）
- ダークモード（OS に追従 / ライト / ダーク）
- スクロール同期のオン/オフ
- Markdown 内のリンクで別の Markdown に移動、ペインごとの「戻る」
- 外部リンクは既定のブラウザで開く
- 文字コード自動判定（UTF-8 / UTF-8 BOM / UTF-16 / Shift_JIS）
- GitHub 風の表示（表・タスクリスト・脚注など。YAML front matter は非表示）

## 動作環境

- Windows 10 / 11 (x64)
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)
- Microsoft Edge WebView2 Runtime（Windows 11 は標準搭載）

## 使い方

```
SimpleMarkdownViewer.exe [ファイル...]
```

exe にファイルをドロップしても起動できます。

| 操作 | キー |
|---|---|
| ファイルを開く | Ctrl+O |
| アクティブなペインを再読み込み | F5 / Ctrl+R |
| アクティブなペインを閉じる | Ctrl+W（ペインのタイトル部分をホイールクリックでも可） |
| スクロール同期の切り替え | Ctrl+L |
| 検索 | Ctrl+F |
| 拡大 / 縮小 | Ctrl+マウスホイール |

## ビルド

```
dotnet publish -c Release -o publish
```

`publish\SimpleMarkdownViewer.exe` が単一の実行ファイルとして出力されます。

## 補足

- Markdown 中の生 HTML に含まれるスクリプトは実行されません（CSP で遮断）。
- WebView2 のデータは `%LOCALAPPDATA%\SimpleMarkdownViewer` に保存されます。

## 同梱ライブラリ

- [Markdig](https://github.com/xoofx/markdig) (BSD-2-Clause)
- [highlight.js](https://highlightjs.org/) (BSD-3-Clause)
- [github-markdown-css](https://github.com/sindresorhus/github-markdown-css) (MIT)
