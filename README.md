# DSH 网页端启动器(DSH Web Launcher)

把本机已安装的 [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)(DSH)的网页界面启动起来,  
并可选地对外提供一个访问入口,让你用浏览器远程使用 DSH。

- 🖥️ **图形界面**:双击即用,启动/停止/查看链接都在一个窗口里完成
- 🔌 **插件环境同步**:以你的 DSH 客户端为准,把客户端已装的插件同步到网页端使用的那套配置里
- 🔒 **单浏览器占用**:第一个用链接打开的浏览器被登记,其它浏览器一律拒绝
- 🚪 **与客户端互斥**:网页端与桌面客户端同时只能存在一个(两个方向都自动处理)
- 📦 **单文件**:启动器本身只有一个 exe,无第三方依赖、无安装期联网
- 🧩 **安装包**:自带向导式安装程序(选择位置 → 许可协议 → 快捷方式 → 完成/立即启动)
- 📖 **开源**:Apache License 2.0,源码完整公开

> ⚠️ **安全提示**:本软件会把 DSH 的网页界面暴露到网络,而 DSH 能在你的机器上执行命令、读写文件。  
> 请务必在云安全组 / 路由器上**限制来源 IP**,不要把带令牌的链接转发给不信任的人。  
> 默认是明文 HTTP;长期公网使用请自行前置反向代理并启用 TLS。详见 [EULA.md](EULA.md)。

## 下载

| 文件 | 说明 |
| --- | --- |
| [**DSH-Web-Setup-v1.3.exe**](https://github.com/rxsmzfg/dsh-web-launcher/releases/latest) | **安装包(推荐)**:向导式安装、自动建快捷方式、自带卸载程序 |
| [DSH-Web-v1.3.exe](https://github.com/rxsmzfg/dsh-web-launcher/releases/latest) | 免安装版:单文件直接运行 |

从 [Releases](https://github.com/rxsmzfg/dsh-web-launcher/releases) 页面下载。

---

## 它做什么 / 不做什么

| 做                                 | 不做                          |
| --------------------------------- | --------------------------- |
| 启动本机 DSH 的网页服务(默认只监听 `127.0.0.1`) | ❌ 不包含 DSH 本体,需要你**先安装 DSH** |
| 在本机监听对外端口(默认 `18080`)并转发给上面的服务    | ❌ 不提供 TLS / 账号体系            |
| 可自动添加 Windows 防火墙入站放行规则           | ❌ 不修改你云服务商的安全组(必须你自己开)      |
| 把客户端插件同步到网页端配置(必要时从 npm 下载插件)     | ❌ 不收集、不上传任何数据               |
| 创建桌面/开始菜单快捷方式(可选)                 | ❌ 不写入系统目录、不注册服务             |

## 运行前提

1. **本机已安装 DSH**(启动器会调用它的 `dsh.cmd`)
   - 自动搜索位置:`%LOCALAPPDATA%\Programs`、`C:\Program Files`、`C:\Program Files (x86)`、`%LOCALAPPDATA%`
   - 找不到时会**弹窗让你选择 DSH 的安装目录**,并自动记住(不需要手写任何配置文件)
2. **要公网访问**:机器需有公网 IP,并在安全组/路由器放行所用端口
3. **管理员权限**:首次启动会请求提权(用于结束旧实例、写防火墙规则)

## 使用

```
安装包:DSH-Web-Setup-vX.Y.exe   ← 双击,按向导下一步
启动器:DSH-Web.exe               ← 安装后从桌面/开始菜单启动
```

启动后窗口里会:

1. 检查环境(DSH 位置、插件同步、防火墙)
2. 启动服务并显示**带令牌的公网链接**(自动复制到剪贴板)
3. 显示当前"浏览器占用"状态,并可手动释放

关闭窗口即停止服务,不留后台进程。

### 命令行

```powershell
DSH-Web.exe probe                     # 环境自检(结果写 probe.txt)
DSH-Web.exe sync                      # 手动同步客户端插件
DSH-Web.exe prepare                   # 只做环境准备(自举 profile/部署插件/同步),不启动服务
DSH-Web.exe finddsh [目录]            # 定位 dsh.cmd(可指定目录)
DSH-Web.exe stop                      # 停止服务
DSH-Web.exe --local-port 8080 --public-port 18080 --skip-guard --no-watchdog
```

安装包静默安装 / 卸载:

```powershell
DSH-Web-Setup-vX.Y.exe /S /D="C:\Tools\DSH 网页端" /desktopicon=1 /startmenu=1 /launch=1
"C:\Tools\DSH 网页端\Uninstall.exe" /uninstall /S
```

## 构建

只需 Windows 自带的 .NET Framework 编译器(csc.exe),**不需要 SDK、不需要联网**:

```powershell
pwsh -File build.ps1              # 版本号取自源码常量
pwsh -File build.ps1 -Version 1.3 # 或显式指定
```

产物在 `dist/`:

| 文件                       | 说明                |
| ------------------------ | ----------------- |
| `DSH-Web.exe`            | 启动器               |
| `DSH-Web-Setup-vX.Y.exe` | 安装程序(已内嵌启动器与用户协议) |

也可以用 Visual Studio / MSBuild 打开 `DSH-Web-Launcher.sln` 构建(见 `src/`)。

## 目录结构

```
dsh-web-launcher/
├─ src/
│  ├─ Launcher/            启动器
│  │  ├─ DSH-Core.cs       进程判定、服务生命周期、配置、DSH 定位
│  │  ├─ DSH-Ui.cs         对话式界面(气泡 + 流式动效)
│  │  ├─ DSH-Gate.cs       公网闸门(单浏览器占用 + 字节级转发)
│  │  └─ DSH-Sync.cs       profile 自举、插件同步、回环视图插件部署
│  └─ Setup/
│     └─ Setup.cs          向导式安装程序 + 卸载
├─ resources/
│  ├─ DSH-Web.ico          图标(同时用于启动器与安装包)
│  └─ DSH-Web.manifest     应用清单(requireAdministrator)
├─ EULA.md                 用户许可协议(安装时展示,构建时内嵌)
├─ build.ps1               一键构建
├─ DSH-Web-Launcher.sln
└─ dist/                   构建产物(.gitignore 中)
```

## 工作原理(简述)

```
浏览器 ──HTTP──▶ 公网端口(闸门,本程序监听)
                     │  ① 校验浏览器占用 cookie(第一个浏览器被登记,其余 403)
                     │  ② 通过则按字节转发
                     ▼
              127.0.0.1:8080(DSH 网页服务)
```

- **为什么自己监听公网端口**:DSH 官方出于安全考虑拒绝 `dsh web --host 0.0.0.0`;  
  本程序不改动 DSH,而是让 DSH 保持只监听回环,由闸门对外并提供占用控制。
- **回环视图**:远程浏览器默认会被 DSH 判定为"非回环",导致设置/模型页不可用。  
  本程序向网页端配置集部署一个小插件,注入官方的 `__DSH_TRANSPORT__ = { ownsHost: true }`,  
  使页面以宿主视图呈现(不修改 DSH 源码)。
- **插件同步**:DSH 的 profile 之间相互隔离;本程序以客户端(`desktop` profile)为准,  
  把缺失的插件安装到网页端所用的 `web` profile,并自动修正 pnpm 构建脚本确认项。

## 许可

Apache License 2.0,见 [LICENSE](LICENSE)。  
本软件会调用你本机已安装的 DSH 及其插件,这些组件遵循其自身许可。

## 在受限网络下推送到 GitHub

若你的机器访问 `github.com` 超时(常见于国内服务器),但 `api.github.com` 可达,
可以用仓库自带的脚本通过 REST API 推送:

```powershell
$env:GH_TOKEN = "github_pat_xxx"        # 细粒度令牌:仅本仓库 + Contents: Read and write
pwsh -File tools/push-to-github.ps1 -DryRun   # 先看看会推哪些文件
pwsh -File tools/push-to-github.ps1           # 正式推送
```

令牌只从环境变量/参数读取,不写入任何文件、不打印。用完即可在 GitHub 上撤销。

### 远程浏览器添加工作区

在公网浏览器中点击「添加工作区」时,目录选择器展示的是**服务器本机的目录**(而不是访问者电脑的目录)。
可浏览、进入并新建服务器上的文件夹,选中后作为 DSH 工作区。v1.5 起启动器会强制使用官方的
`browse` 目录选择后端,避免远程浏览器误触发服务器桌面上的原生 Windows 对话框而报 `Failed to fetch`。

> 这意味着远程访问者可以看到并选择启动 DSH 的 Windows 账户有权限读取的目录。请配合安全组来源 IP 限制、
> 带令牌链接和单浏览器占用机制使用;不应把链接提供给不受信任的人。
