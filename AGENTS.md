# AGENTS

- 测试：`dotnet test DevinCleaner.slnx -c Release`（合成数据，不碰真实库）
- 发布：`dotnet publish src/DevinCleaner -c Release -o dist`，再 `python scripts/release.py package`
- 发版：远端 `XiaoBai620/DevinCleaner`（public，分支 main）。改 csproj `<Version>` + CHANGELOG 新增 `## x.y.z` → commit/push → `git tag vx.y.z` 并推送，CI 自动测试、编译并创建 Release（exe + SHA256SUMS）；不要手动上传本地 dist
- 真实数据：`%APPDATA%\devin\cli\sessions.db`（WAL），只允许只读访问；写入验证一律在副本上做
- Devin 升级后结构指纹会变：同步更新 `Engine/Catalog.cs` 的 `SchemaHash` 和 `tests/DevinCleaner.Tests/fixture-schema.json`
