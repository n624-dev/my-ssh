# my-ssh

Windows / Linux向けの対話式SSHランチャー兼SFTPファイルマネージャーです。

[MIT License](LICENSE)

接続先とユーザーを選択した後、通常のSSHセッションを開くか、ローカルとリモートを左右に表示するファイル転送画面を開けます。

## 必要なもの

- OpenSSHの`ssh`コマンド
- 接続先でSFTPサブシステムが利用できること
- Cloudflare Accessを使う場合は`cloudflared`とOpenSSHの`ProxyCommand`設定

.NET SDKや.NET Runtimeを利用端末へ別途インストールする必要はありません。ReleaseまたはGitHub Actionsの成果物として生成されるself-contained版を使用します。

SSHの鍵、`known_hosts`、ホスト別設定、`ProxyCommand`はOpenSSH自身に処理させます。my-ssh側へ秘密鍵やCloudflareの設定を複製しません。

## 起動

配布された実行ファイルを`PATH`に置き、次のどちらかで起動します。

```text
my-ssh
myssh
```

既存の登録情報を変更せずに試す場合は、`my-ssh --config-dir <別の設定フォルダ>`で起動できます。

## 操作の流れ

```text
Server
  -> User
       -> Action
            |- Open SSH
            `- File Transfer
```

サーバーとユーザーは追加・削除できます。登録ユーザーが1件の場合は自動入力されます。ユーザー名入力ではTabによる最長共通接頭辞までの補完を利用できます。

## File Transfer

左右2ペインでローカルとリモートを操作します。

- `Tab`: ペイン/転送一覧のフォーカス移動
- `Space`: 複数選択
- `Enter`: ディレクトリを開く / ファイルをプレビュー
- `F2`: 名前変更
- `F5`: 反対側へコピー
- `F6`: 反対側へ移動
- `F7`: ディレクトリ作成
- `F9`: その他の操作、転送制御
- `Delete`: 永久削除
- `Esc`: 接続先のAction選択へ戻る

転送キューでは一時停止、再開、再試行、上書き、キャンセル、完了項目の削除を行えます。

### 転送の安全性

通常ファイルは転送先と同じディレクトリの一時ファイルへ書き込み、完了後に確定します。中断した一時ファイルから再開する場合は、既存部分と元ファイルを比較して一致を確認してから続行します。

上書き時、リモート側ではOpenSSHの`posix-rename@openssh.com`が利用できる場合のみ、既存ファイルを置き換える確定処理を行います。対応していないサーバーでは既存ファイルを先に削除せず、エラーとして扱います。

移動はコピーが完了してから元データを削除します。コピー後の削除だけ失敗した場合は部分成功として区別します。

シンボリックリンクは既定でリンクそのものを扱い、再帰コピーでリンク先を自動追跡しません。

## 設定

Windows:

```text
%APPDATA%\my-ssh\config.json
%APPDATA%\my-ssh\state.json
```

Linux:

```text
$XDG_CONFIG_HOME/my-ssh/
```

`XDG_CONFIG_HOME`が未指定の場合は`~/.config/my-ssh/`です。

旧PowerShell版の`config.json`は初回起動時に読み込み、`config.json.v1.bak`を作成してから新形式へ移行します。

## 開発・ビルド

ソースはC# / .NET 10です。通常利用者がローカルでコンパイルすることは前提としていません。

GitHub ActionsでWindowsとUbuntuのビルド・テストを実行し、次のself-contained single-file成果物を生成します。

- `win-x64`
- `linux-x64`
- `linux-arm64`

ローカルで開発する場合のみ.NET 10 SDKが必要です。

```text
dotnet restore MySsh.slnx
dotnet build MySsh.slnx -c Release
dotnet run --project tests/MySsh.Tests/MySsh.Tests.csproj -c Release
```

## 構成

```text
src/
  MySsh.App/             Terminal.GuiによるCUI
  MySsh.Core/            接続・ファイル・転送モデル
  MySsh.Infrastructure/  OpenSSH/SFTP、ローカルFS、設定、転送処理

tests/
  MySsh.Tests/           クロスプラットフォームのスモークテスト

.github/workflows/
  ci.yml                 Windows/Linuxビルド、テスト、配布物生成
```

CIのUbuntuジョブでは、一時的な鍵を使うループバック限定のOpenSSHサーバーに接続し、1 MiB超の転送、Unicode名、再帰コピー、上書き、リンク、権限、移動、中断後の再開、不一致の途中データの拒否も検証します。実際の利用先サーバーやCloudflare Accessの認証とは別のテストです。

WSL / Linuxでも、CIの`my-ssh-tests-linux-x64`成果物を展開し、`chmod +x MySsh.Tests`の後、`bash tests/run-sftp-local.sh /展開先/MySsh.Tests`で同じ実転送テストを実行できます。OpenSSHサーバーの`sshd`が必要です。テスト用サーバーは通常ユーザーでループバックの22222番ポートに起動し、終了時に停止します。既存のSSH設定・サービスは変更しません。ポートが使用中の場合は`MYSSH_TEST_PORT`で空きポートを指定してください。

## ライセンス

my-sshは[MITライセンス](LICENSE)で公開しています。

同梱のTerminal.Guiのソースには[元のMITライセンスと著作権表示](vendor/Terminal.Gui/LICENSE)が適用されます。[取り込み元と修正内容](vendor/Terminal.Gui/LOCAL-CHANGES.md)も記載しています。配布物には両方のライセンスを含めます。
