# かんたんPDF分割

名古屋テクノロジーが無料配布するWindows用PDF分割アプリ。Versionは `Directory.Build.props` を唯一の情報源としてビルド・配布物に反映します。C# / WPF / .NET 10 LTS / PDFsharp 6.2.4 / NSIS 3.13。

## 使い方

1. PDFを画面にドラッグするかクリックして選びます。
2. 「1ページずつ」または「指定ページの後」を選びます。指定位置は `5,12` のように半角数字をカンマで区切ります。
3. 出力ファイル名・ファイル数・ページ範囲を確認し、保存先を選びます。
4. 「PDFを分割する」を押します。完了後「保存先を開く」で結果を確認できます。

元PDFは変更しません。画像化せずページの内容・サイズ・回転・各ページボックスをコピーします。連番は最低3桁、ページ数に応じて増やします。重複指定・範囲外・空要素・不正文字はエラー。最終ページ指定は空区間を作りません。同名出力は上書きせず停止。出力先で一時生成・再読込後に公開し、失敗時は今回の出力を削除します。OSや他アプリに削除を拒否された場合は残留ファイルを表示します。電源断・プロセス強制終了の場合は `.kantan-pdf-*` 一時フォルダーや一部の出力が残る可能性があります。

## 対応範囲

Windows 10 / 11 **x64**。Windows 10は22H2以降を対象。Self-contained配布のため.NETの事前インストールもインターネット接続も不要。Windows ARM64 / x86は本リリースの対象外。長いパスに対応したマニフェストを同梱していますが、Windows側の設定とファイルシステムの制限は引き続き適用されます。セキュリティ設定は変更しません。

初版は通常の静的PDFを対象とします。暗号化（開くパスワードが空でも利用制限があれば拒否）、電子署名、フォーム、注釈（リンクを含む）、添付、しおり、タグ付き文書、レイヤー、特殊なページ情報や表示アクションなどは保存を保証できないため分割を拒否します。パーサーで圧縮オブジェクトと名前のエスケープを解釈して検査します。PDFsharpが読み取れない破損PDF・特殊形式も拒否します。**電子署名の有効性は分割後に維持されません。** 文書全体のメタデータは複製せず、出力の作成者は本アプリ名になります。PDF/AやPDF/UA準拠の維持は保証しません。すべてのPDF作成ソフトの互換性を保証するものではありません。

処理はバックグラウンドで行います。二重実行と処理中の入力変更・終了を防止。PDFsharpは文書をメモリに読み込むため、大容量PDFは空きメモリに依存し、解析中・1ファイルの保存中の中断はできません。UIは応答します。

## インストール・更新

`KantanPdfSplitter-Setup-1.0.0.exe` を実行。管理者権限不要、`%LOCALAPPDATA%\Programs\KantanPdfSplitter` にインストールし、デスクトップ・スタートメニューと「インストールされているアプリ」に登録します。同一バージョンの再実行で修復、上位版で更新可能。下位版への戻しは警告して拒否。実行中はインストール・アンインストールを拒否します。

アンインストールは**インストーラーが所有する個別ファイルだけ**を削除します。元PDF・生成PDFやユーザーが追加したファイルを削除しません。更新時も古い配布ファイルを記録した管理リストで処理します。署名は現時点で行いません。Windowsの警告を回避するためのセキュリティ変更はしないでください。

## 開発・検証

PowerShell 7、.NET SDK（`global.json`）、Windowsが必要です。

```powershell
./scripts/Build-Assets.ps1
dotnet restore KantanPdfSplitter.slnx --locked-mode
dotnet build KantanPdfSplitter.slnx -c Release --no-restore
dotnet test KantanPdfSplitter.slnx -c Release --no-build
./scripts/Build-Installer.ps1
./scripts/Test-InstallerSmoke.ps1
```

通常CIはWindows 2025でrestore / build / tests / self-contained publish / NSIS / installer smokeを実施し、大きな配布物は保存しません。手動実行またはReleaseでSetup・ZIP・SHA-256を7日保存します。将来の署名は `Build-Installer.ps1 -SignToolCommand` を使用し、publishしたPEファイルを署名してからNSISに渡してください。署名証明書や認証情報はリポジトリに保存しません。

## ブランド・ライセンス

参考：`numazawasystem/omoide-dvd-writer` commit `3549583a5948c490485a095ca81292e55cda677b`。参考リポジトリは変更していません。正式ロゴは2172×724 RGBA、SHA-256 `B8BB696071B466374CC95C8427AD563C5D48E36C14A1164D7CBB2B981D487041`。原本をバイト単位で再利用。UI・Splashは同じライトテーマ、Splashは非同期で約1.1秒。専用アイコンは独自の図形描画でPDF書類・分離したページと矢印を表現し、9サイズをICOに収録します。

ソースコードの利用条件は `LICENSE`、会社ロゴの権利はブランド所有者に帰属します。使用ライブラリの条件は `THIRD_PARTY_NOTICES.md` と `licenses/` に同梱。
