# Chronicler

Chroniclerは、Lore向けのWindowsデスクトップクライアントです。

現在は開発初期段階です。ブラウザーログイン、リポジトリ一覧の取得、clone、プロジェクトの切り替え、ローカルファイルの表示、ステージ・アンステージ、ローカルコミットとプッシュ、ファイルロックの取得・自分のロック一覧表示・解除に対応しています。プロジェクトごとにサーバー接続先を記録し、異なるサーバーのプロジェクトを同じ一覧に表示します。新規プロジェクト向けに接続先ショートカットも登録できます。起動時にサーバーへ接続したり、ブラウザーを開いたりすることはありません。

## 開発環境

- Windows x64
- .NET 10 SDK
- Lore C# SDK `LoreVcs` 0.12.0（NuGetから復元）

Lore SDKのネイティブライブラリを復元できるように、プロジェクトのランタイム識別子は `win-x64` に設定しています。初回ビルドにはNuGetへのアクセスが必要です。ローカルの `lore-dotnet` リポジトリはビルドに必要ありません。

## リリース

`vX.Y.Z` 形式の注釈付きタグを `main` にプッシュすると、GitHub Actions が Release 構成のテストを実行し、Windows x64 向け自己完結型の単一ファイル版をビルドします。生成された `Chronicler-win-x64.zip` は、同じタグの GitHub Release に添付されます。

```powershell
git tag -a v0.0.1 -m "Release v0.0.1"
git push origin v0.0.1
```

## ドキュメント

- [ビルドと起動](docs/development/build-and-run.md)
- [ログイン](docs/guide/login.md)
- [プロジェクトの作成](docs/guide/projects.md)
- [テスト](docs/development/testing.md)
- [開発ノート](docs/development/development-notes.md)
