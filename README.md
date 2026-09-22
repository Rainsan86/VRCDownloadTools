# VRC下载工具

一个用于**找回你自己丢失的 VRChat 资产**的 Unity 插件，配合 [AssetRipper](https://github.com/AssetRipper/AssetRipper) 使用。

> # ⚠️ 严禁用于任何恶意用途！⚠️
>
> 本工具的目的是**找回你自己丢失的资产**。
>
> *请不要用它、也不要改造它去提取不属于你的资产！*

## 功能

- **多线程下载**：大文件自动拆分成多个分段并行下载，速度明显更快
- **随时取消**：下载过程中可以随时取消
- **连原图一起保存**：下载资产时会把资产原本的图片一并保存，文件名和资产相同
- **在资源管理器中显示**：一键定位已下载的资产文件
- **AssetRipper 一站式管理**：下载、重装、手动指定位置、解包
- **全中文界面**
- **推荐插件入口**：内置 FACS Utilities 的快捷跳转

每个资产会下载到一个**以资产自身命名的独立文件夹**里，文件夹名和资产一样：

```
Assets/VRCDownloadTools/Output/Bundles/
└── My World_wrld_1234abcd-..../
    ├── My World_wrld_1234abcd-....vrcw   ← 资产本体（世界 .vrcw / 模型 .vrca）
    └── My World_wrld_1234abcd-....png    ← 资产原本的图片（按实际格式存为 png/jpg/gif/webp/bmp）
```

这样资产和它的图片不会和其他资产混在一起，删除资产时整个文件夹会一起删掉。

## 安装与使用

1) 把 `VRCDownloadTools` 文件夹放进带 VRCSDK 的 Unity 工程的 `Assets` 目录下
   （下载目录会自动跟随这个文件夹名，改名也不影响）
2) 打开 Unity 顶部菜单 **VRChat SDK**，登录你的账号
3) 进入 **Content Manager** 标签页，确认你的内容列表已经加载出来
4) 打开 Unity 顶部菜单 **VRC下载工具 > 主窗口**
5) （可选）准备 **AssetRipper**，不打算解包可以跳过：
    - 点 **下载 AssetRipper** 让工具自动下载并安装，或者
    - 点 **手动选择 AssetRipper** 指定你已经自己下载好的可执行文件（`AssetRipper.GUI.Free.exe`）
6) 选择要下载的 **模型** 或 **世界**（列表左上角有 **← 返回** 按钮）
7) 点开对应资产，选择要下载的平台版本（Windows / Android / iOS / Web）
8) 下载完成后，在主页的 **已下载的资产** 列表里点击文件即可：
    - **载入到当前场景**：预览资产（世界需要先进入播放模式）
    - **在资源管理器中显示**：在文件夹里定位这个文件
    - **在资源管理器中显示图片**：定位随资产一起保存的原图
    - **删除该资产**：从磁盘删除（同名图片会一起删除）
    - **选择输出文件夹并解包**：用 AssetRipper 解包成 Unity 工程
      + 输出文件夹必须是**空文件夹**，AssetRipper 会**清空该文件夹内所有内容**
      + 输出文件夹**不要选在当前 Unity 工程内**
      + 解包完成后用 Unity 打开导出的工程，如有脚本/Shader 报错，重新导入旧版本即可

## 多线程下载

下载大体积 AssetBundle 时会自动使用多个连接并行下载：

1) 先用一个只取 1 字节的 `Range` 请求探测服务器是否支持分段
2) 服务器返回 `206 Partial Content` → 把文件等分成若干段并行下载，每段直接写入文件的对应偏移
3) 服务器返回 `200 OK`（忽略 Range）→ 这个响应本身就是完整文件，直接保留，不会浪费流量
4) 其它任何异常 → 自动删除半成品文件，并回退到 VRChat SDK 原本的单线程下载器

网络类错误会自动重试（重试次数可在设置里调整），尽量不触发回退。
整个下载过程不占用大块内存，失败时不会留下损坏的文件。

图片一律使用单连接下载，并且**图片失败不会影响资产本体**，只是少一张图。

### 下载设置

在主窗口底部展开 **下载设置** 可以调整：

| 选项 | 默认值 | 说明 |
| --- | --- | --- |
| 并发连接数 | 8 | 分段下载使用的连接数，1~16 |
| 最小分块大小 (MB) | 8 | 小于「该值 × 2」的文件只用单个连接 |
| 失败重试次数 | 5 | 单个分段失败后的重试次数，调大更不容易回退到单线程 |

### 关于 AssetRipper 的下载

工具从 [AssetRipper 官方 Releases](https://github.com/AssetRipper/AssetRipper/releases) 取最新版：

| 平台 | 发布包 |
| --- | --- |
| Windows | `AssetRipper_win_x64.zip` / `AssetRipper_win_arm64.zip` |
| Linux | `AssetRipper_linux_x64.tar.xz` / `AssetRipper_linux_arm64.tar.xz` |
| macOS | `AssetRipper_mac_x64.tar.xz` / `AssetRipper_mac_arm64.tar.xz` |

> 注意：AssetRipper 从 **2.0.0** 起把 Linux / macOS 的发布包从 `.zip` 改成了 `.tar.xz`。
> 工具会自动区分：`.zip` 用内置解压，`.tar.xz` 交给系统自带的 `tar` 命令处理。

如果自动下载不可用，随时可以点 **手动选择 AssetRipper** 指定本地已下载好的可执行文件；
选择结果会保存在 `EditorPrefs` 里，下次打开工程依然有效。

## 本工具不会修改 VRChat SDK

多线程下载所需的认证信息，是通过调用 VRChat SDK **自带的公开接口** 附加的
（`API.CertVerifyUnityWebRequest`、`API.AuthenticateUnityWebRequest`、`API.PopulateUnityWebRequestHeaders`），
这和 SDK 自己发起请求时的做法完全一致。工具只读取 SDK 的公开字段（以及用于读取内容列表的私有字段），
**不会写入、覆盖或重新编译 SDK 的任何文件**。

## 推荐插件

**FACS Utilities** —— Avatar 工程文件修复插件。

用 AssetRipper 解包出 Avatar 工程、并用 Unity 打开后，可以先用它过一遍，检查并修复工程里的问题。

GitHub：<https://github.com/Rainsan86/com.facs01.utilities>

主窗口底部「推荐插件」里也有一个按钮可以直接跳转。

## 会被封号吗？

只要 SDK 保持最新，检测使用本工具是比较困难的，因为它使用的就是 [VRChat SDK 内置的资产下载器](https://github.com/200Tigersbloxed/dVRC/blob/main/Editor/VRCAsset.cs)。
但*已知*的使用行为仍可能导致处罚。本工具定位是**找回工具**，只能找回**你自己上传的资产**，
无法用它提取其他用户的资产。**本工具绝不会修改 VRChat SDK。**

## 致谢

原项目：[200Tigersbloxed/dVRC](https://github.com/200Tigersbloxed/dVRC)
