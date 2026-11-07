# AGENTS

- 测试：`dotnet test DevinCleaner.slnx -c Release`（合成数据，不碰真实库）
- 发版：远端 `XiaoBai620/DevinCleaner`（public，分支 main）。改 csproj `<Version>` + CHANGELOG 新增 `## x.y.z` → commit/push → `git tag vx.y.z` 并推送，CI 自动测试、编译（`dotnet publish ... -o dist` + `release.py package`）并创建 Release（exe + SHA256SUMS）
- `dist/` 只是 CI 和本地试打包的临时输出（已 gitignore），用完即删，不要在本地保留或手动上传
- 真实数据：`%APPDATA%\devin\cli\sessions.db`（WAL），只允许只读访问；写入验证一律在副本上做
- 图标：`python scripts/make_icon.py` 生成 `src/DevinCleaner/app.ico`（几何与 `DesktopTheme.cs` 的 `DevinLogo` 一致，改一处要同步另一处）
- 性能 / 界面实测：用 sqlite backup 把真实库复制到 `%TEMP%` 再测，测完删除副本
- Devin 升级后结构指纹会变：同步更新 `Engine/Catalog.cs` 的 `SchemaHash` 和 `tests/DevinCleaner.Tests/fixture-schema.json`
