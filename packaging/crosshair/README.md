# Lertaro 屏幕准星插件

Lertaro 原生插件：在屏幕中央叠加一个自定义准星（全屏置顶、点击穿透、不抢焦点）。
参数体系 1:1 对齐 **okiaimx.com 的准星编辑器**（也就是 **VALORANT 的准星参数**），
并支持 **VALORANT 准星代码的导入与导出**。

> 验证环境：Lertaro **5.6.7**（PluginSdk 1.8.6）/ Windows 11 / .NET 10（宿主自带运行时，用户无需安装 .NET）

## 功能

- **okiaimx / VALORANT 同款参数**：颜色（8 预设 + 自定义十六进制）、描边、中心点、内部线条、外部线条；
  范围与默认值与 okiaimx 完全一致（例如外线偏移 0-40、内线长度 0-20、描边粗细 1-6、不透明度 0-1）
- **VALORANT 准星代码互导**：把 okiaimx / 游戏里复制的代码粘进来即可应用；编辑器里的「复制」也能生成
  可粘回 okiaimx 或游戏的代码（等于默认值的字段自动省略，与官方写法一致）
- **可视化编辑器**（动作菜单 → 准星编辑器）：与 okiaimx 同款双栏布局 + 实时预览 + 应用 / 复制 / 恢复默认
- **命令与热键**：全局热键一键开关；搜索框里 `zx k` 打开、`zx g` 关闭（前缀跟随你配置的触发词）
- **几何与游戏一致**：1 单位 = 屏幕宽度 ÷ 1920（4K 下准星同比放大），开火误差开启且未被 `P:m` 覆盖时偏移 +4
- **点击穿透**：`WS_EX_TRANSPARENT + LAYERED + NOACTIVATE`，鼠标完全穿过，不影响游戏操作

## 安装

### 方式一：install.ps1（推荐）

1. 下载 Release 里的 `Crosshair-x.y.z.zip` 并解压
2. **先从托盘图标右键退出 Lertaro**（安装脚本会等你退出，不会强杀进程）
3. 右键 `install.ps1` → 使用 PowerShell 运行（会自动请求管理员权限）
4. 启动 Lertaro，按 `Ctrl+Alt+C` 或输入 `zx k` 验证

### 方式二：手动

1. 解压 zip
2. 把 `Lertaro.Plugins.Crosshair` 文件夹整个复制到 `C:\Program Files\Lertaro\Plugins\`（需管理员权限）
3. 启动 Lertaro

日志里看到 `Loaded plugin: 'CrosshairPlugin' (vX.Y.Z)` 即安装成功；数据目录在
`%LOCALAPPDATA%\Lertaro\Lertaro.Plugins.Crosshair\`。

## 使用

| 操作 | 方式 |
|---|---|
| 开关准星 | 全局热键 `Ctrl+Alt+C`（可在设置里改，留空 = 不注册） |
| 打开准星 | 搜索框输入 `zx k`（紧贴写法 `zxk`、`zx on`、`zx 开`、`zx 打开` 也认） |
| 关闭准星 | 搜索框输入 `zx g`（`zxg`、`zx off`、`zx 关`、`zx 关闭` 也认） |
| 查看状态 / 提示 | 搜索框输入 `zx`（Tab 补全成 `zx k`），或输入你的触发词 `准星` |
| 可视化调参 | 动作菜单 → **准星编辑器**（滑杆 + 实时预览 + 代码栏） |
| 粘贴 VALORANT 代码 | 编辑器底部「代码（VALORANT 兼容）」粘贴 → 应用；或宿主设置面板的「VALORANT 准星代码」栏 |
| 复制当前代码 | 编辑器底部「复制」 |

触发词默认 `准星` / `cross`，`zx k` / `zx g` 的前缀会跟着触发词自动变（改成别的触发词也不用改代码）。

## 设置项

颜色 · 描边（显示 / 粗细 / 不透明度）· 中心点（显示 / 大小 / 不透明度）·
内部线条（显示 / 粗细 / 长度 / 偏移 / 不透明度 / 单独设置竖线 / 竖线长度）·
外部线条（同上，长度 0-10、偏移 0-40）· 自定义颜色 ·
扩展项：总开关 · 位置偏移 X/Y · 整体不透明度 · 触发词 · 全局热键 ·
**VALORANT 准星代码**（粘贴后保存即应用，会覆盖上面的细项）

## 注意

- 准星是**屏幕叠加层**：不注入、不读写游戏内存、不修改游戏文件；但它会显示在**所有**窗口之上，
  且部分竞技游戏的服务条款禁止任何第三方叠加层 —— **使用前请确认你所在游戏的规则**，风险自负。
- 插件与宿主同进程加载：宿主大版本升级后可能需要重新编译适配。

## 构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)（本仓库以 10.0.401 构建）与 Lertaro 安装目录下的 `Lertaro.PluginSdk.dll`：

```powershell
C:\Users\<你>\.dotnet\dotnet.exe build src\Lertaro.Plugins.Crosshair\Lertaro.Plugins.Crosshair.csproj -c Release
```

产物在 `src\Lertaro.Plugins.Crosshair\bin\Release\net10.0-windows\`，复制 dll 到 Lertaro 的 `Plugins\` 目录即可。
打包发布件：`powershell -File packaging\build-crosshair-release.ps1`（生成 `release\Crosshair-<版本>.zip`）。

## License

MIT（与仓库根 LICENSE 一致）
