# DevinCleaner · Devin 对话清理台

Windows 桌面小工具：浏览本机 Devin CLI / Devin 桌面版的对话记录，核对预览后删除选中的会话，删除前自动备份，可随时恢复。

**独立社区项目，与 Cognition 无官方隶属关系。** 只支持已验证的本机存储结构；Devin 数据格式变化时会停止删除，只允许浏览。

## 使用

需要 64 位 Windows 10/11 和 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)。双击 `DevinCleaner.exe` 运行，不占用端口。

1. 按标题、工作目录或会话 ID 搜索，也可按状态筛选。点选一行在右侧查看详情，勾选框用于选择要删除的会话。
2. 点击「查看对话」或双击列表行，以聊天窗口只读查看对话主线：你的消息在右，Devin 的回复在左；工具调用折叠为一行，点击展开参数和结果；思考过程和系统消息默认隐藏，可勾选显示；双击消息看全文，右键复制。
3. 正在 Devin 中打开的会话标为「运行中」，不能勾选；其他会话在 Devin 开着时也能删除。
4. 「孤立锁文件」默认不勾选，可以单独执行。
5. 点击「预览清理范围」，核对会话、各表行数和锁文件后点击「确认清理」。
6. 误删时点击「备份与恢复」，选中备份，勾选会话后恢复。

## 会删除什么

| 选项 | 处理内容 |
|---|---|
| 勾选会话 | `sessions.db` 中该会话在 `sessions`、`message_nodes`、`tool_call_state`、`prompt_history`、`rendered_commits` 的所有行，以及它的 `session_locks\<id>.lock` |
| 孤立锁文件 | `session_locks` 中没有对应会话、也未被任何进程占用的 `.lock` 文件。Devin 打开会话时用它标记“正在使用”，只记录进程号（几字节），会话删除后常被遗留；删除不影响任何对话 |

`summaries` 目录中的历史摘要文件无法可靠对应到会话，本工具不修改。设置、登录信息、技能、MCP 配置和云端记录都不在清理范围。

## 安全

- 数据库结构指纹（所有表和触发器定义的 SHA-256）来自 **2026-11-08** 的 Devin（迁移版本 16），不匹配时只允许浏览。
- 运行状态按锁文件是否被占用判断：Devin 会话打开期间一直占用 `session_locks\<id>.lock`。锁文件里的 PID 可能已被系统复用，不作为依据。
- 执行时先独占所选会话的锁文件，再复查结构指纹和每个会话的行数、最大消息行号、最后活动时间；预览后有变化就拒绝。
- 所选会话的全部行先写入 `%LOCALAPPDATA%\DevinCleaner\Backups\sessions-*.db`，逐表校验行数并通过 `quick_check` 后才在单个事务中删除；任何一步失败都会回滚并移除备份。
- 恢复只接受备份目录中的本工具备份，且要求备份与当前数据库的表结构、迁移版本完全一致；当前库中已有的同 ID 会话会被跳过。

## 开发

需要 [.NET 10 SDK](https://dotnet.microsoft.com/)。

```text
dotnet test tests/DevinCleaner.Tests
dotnet publish src/DevinCleaner -c Release -o dist
```

测试在系统临时目录用 `fixture-schema.json` 重建只含结构的数据库，写入虚构数据，不读取也不修改真实的 Devin 数据。

```text
src/DevinCleaner/        WinForms 桌面界面
src/DevinCleaner/Engine/ 清理引擎：结构校验、预览计划、备份、删除、恢复、锁检测
tests/                   合成数据与回归测试
scripts/release.py       版本校验、更新记录提取、SHA-256 校验文件
```

适配新版 Devin 时，除了更新 `Catalog.cs` 中的指纹和 `fixture-schema.json`，还要逐个核查新增的表和列是否引用会话 ID，并补充删除、备份和恢复逻辑及回归测试。

## License

[MIT](LICENSE)
