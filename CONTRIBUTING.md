# 参与开发

感谢你帮助改进 ShotShelf。提交改动前，请先搜索现有 Issue，避免重复工作。

## 环境

- Windows 10 / 11 64 位
- PowerShell 5.1
- .NET Framework 4.8 开发工具（提供 `csc.exe`）
- 运行内置编辑器测试时还需要 Microsoft Edge WebView2 Runtime

项目没有引入 dotnet SDK，`build.ps1` 直接调用系统自带的 .NET Framework 编译器。

## 本地构建与测试

```powershell
.\build.ps1
.\build.ps1 -UpdateTests
.\build.ps1 -ReleaseTests
.\build.ps1 -BasketLayoutTests
.\build.ps1 -DesktopTests
.\tests\CaptureWindowTests.ps1
```

`build.ps1` 默认会编译应用并执行核心测试。桌面、更新和布局测试需要真实交互环境；提交前至少运行与改动相关的专项测试。

所有 `.ps1` 文件应保存为 UTF-8 with BOM，避免 Windows PowerShell 5.1 将中文按系统代码页读取。

## 打包

```powershell
.\build.ps1 -Package
```

命令会生成 `releases\ShotShelf-<version>-windows-x64.zip` 和对应 `.sha256`，并检查压缩包白名单、测试残留和个人路径。

## 提交与发布

- 一个提交只处理一个明确主题，避免把无关格式化混入功能改动。
- Pull Request 请说明用户可见的行为变化、验证命令和仍存在的限制。
- 正式版本从 `main` 创建附注标签 `v<version>`，并新增 `docs/releases/v<version>.md`。
- `.github/workflows/release.yml` 会校验标签、程序版本和资产，然后创建正式 Release。
- `main` 上的推送和 Pull Request 会运行 `.github/workflows/ci.yml`。

## 行为准则

讨论问题时聚焦代码、复现步骤和用户影响。请尊重不同经验水平的贡献者，不提交他人的隐私数据、访问令牌或未获授权的图片。
