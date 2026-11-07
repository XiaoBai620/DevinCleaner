# AGENTS

- 测试：`dotnet test DevinCleaner.slnx -c Release`（合成数据，不碰真实库）
- 发布：`dotnet publish src/DevinCleaner -c Release -o dist`，再 `python scripts/release.py package`
- 真实数据：`%APPDATA%\devin\cli\sessions.db`（WAL），只允许只读访问；写入验证一律在副本上做
- Devin 升级后结构指纹会变：同步更新 `Engine/Catalog.cs` 的 `SchemaHash` 和 `tests/DevinCleaner.Tests/fixture-schema.json`
