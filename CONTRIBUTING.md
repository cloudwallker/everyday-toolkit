# 贡献 / Contributing

先用合成数据复现问题，并说明 Windows 版本、应用版本、显示缩放、操作顺序、预期与实际结果。剪贴板或粘贴问题说明内容类型、来源/目标应用、是否按住修饰键；不要附真实剪贴板、办公截图、常用语 JSON、数据库、环境秘密或个人路径。

Reproduce with synthetic data. Include OS/app version, display scaling, steps, expected and actual behavior. For clipboard/paste issues include content type, source/target application and held modifiers. Never attach private clipboard content, work screenshots, snippets, databases, credentials or identifying paths.

## 开发流程 / Workflow

1. 获取完整源码，阅读 README、用户指南以及对应模块和行为测试。`EverydayToolkit.Core` 提供数据模型、存储和清洗规则；`EverydayToolkit.Windows` 提供操作系统适配；`EverydayToolkit.App` 协调界面和生命周期。在相关模块中实现小范围改动，保留原文、加密、配额和安全粘贴规则。
2. 新增行为先编写能失败的真实测试，再实现；不以搜索源码文字代替行为验证。纯文档修订无需机械增加测试。
3. 执行 `.\scripts\build.ps1` 与 `.\scripts\test.ps1`。真实剪贴板测试需串行进行；安装测试数据放在 artifacts，勿使用个人数据库。
4. PR 说明问题触发条件、最终行为、验证命令/结果和未验证项。界面变化可附仅含合成内容的截图。请勿声明未实际运行的测试通过。
5. 新依赖更新 `THIRD-PARTY-NOTICES.md`；保持 MIT 自有代码与依赖声明。新增工具需证明能改善现有复制/整理/复用流程。

Read the README, user guide, affected module and behavioral tests; add a failing behavioral test before implementing changes, build and run all tests, then describe the trigger, resulting behavior, actual verification and remaining limitations in your PR. UI screenshots must contain synthetic data only. Update notices when adding dependencies.

## 提交与公开前检查 / Before committing or publishing

Git 作者使用贡献者自己的账户及已验证的 GitHub noreply 邮箱，不冒用他人或助手身份。检查待上传文件、暂存区、本次提交和发布包，排除真实密码、令牌、私钥、凭据、含秘密的环境配置、个人数据及构建缓存；示例采用占位符。更新双语 README 与实际功能简介。不得把测试库或自用剪贴板导出提交到仓库。

Use your own verified Git identity, preferably GitHub's noreply address. Review files, staging, commits and release payloads for secrets, personal data and build caches. Use placeholders in examples, update both READMEs to match actual functionality, and exclude test databases and personal exports.
