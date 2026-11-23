# CloudpunkVR

把 [Cloudpunk](https://store.steampowered.com/app/746850/Cloudpunk/)(Unity 2018.4.29,IL2CPP)改成第一人称 VR 的模组：坐进驾驶舱开飞车，下车后有第一人称的身体和手臂，
SteamVR 手柄操作，外加一组平面模式也生效的性能优化。跑在 BepInEx 6 IL2CPP 上。

模组里还带一座给 [Cortico](https://github.com/Pal-AI-Lab/Cortico) 的桥：装上配套的 World
[cortico-world-cloudpunk-vr](https://github.com/Phantivia/cortico-world-cloudpunk-vr) 后，Coo(Cortico 的 Bot)出现在游戏的通讯频道里，
看得到任务、对话和地图，能在小地图上画路线，也能替你把车开过去。不装 Cortico 时这座桥只是在本机开着一个端口，不影响单独玩 VR。

这个仓库不含游戏文件。你需要自己在 Steam 上有 Cloudpunk。安装时 Steam 里的原版只被读取，不会被改动。

## 三个包

| 包 | 在哪下载 | 是什么 |
|---|---|---|
| `CloudpunkVR-<版本>-patch.zip` | 本仓库 [Releases](https://github.com/Phantivia/CloudpunkVR/releases) | VR 补丁：安装脚本、编好的插件、BepInEx,连同全部源码。从 Steam 原版做出 VR 副本用这个 |
| `CloudpunkVR-<版本>-plugin.zip` | 同上 | 只有植入游戏的插件(BepInEx 插件、预加载补丁器、SteamVR 绑定、按键设置程序)。已经有 VR 副本、只想更新插件用这个 |
| `cortico-world-cloudpunk-vr-<版本>.tgz` | [World 仓库的 Releases](https://github.com/Phantivia/cortico-world-cloudpunk-vr/releases) | Cortico World,让 Coo 进游戏。只玩 VR 不需要 |

## 需要什么

- Windows 10 / 11,64 位。
- Steam 上的 Cloudpunk。做的时候是 Steam build 8245592;游戏更新后引擎版本不变就还能用，安装脚本遇到不认识的文件格式会停下来报错，不会写坏文件。
- SteamVR(从 Steam 安装)和一台 SteamVR 能用的头显。开发时用的是 Quest 3 经 Virtual Desktop 串流；绑定文件有 Touch、Index、Vive 三套。
- 游戏所在的盘上再有约 15 GB 空间(安装会复制一份游戏)。
- 资料片 City of Ghosts 是另一个程序，没有做 VR。

## 安装

1. 在 Steam 里装好 Cloudpunk 和 SteamVR,Cloudpunk 至少正常启动过一次。
2. 下载 `CloudpunkVR-<版本>-patch.zip`,解压到任意位置。
3. 双击 `install.bat`。它做这些事：
   - 在 Steam 的各个库里找到 Cloudpunk,复制一份到同一个盘的 `\Games\CloudpunkVR`(Steam 原版不动，存档两边共用);
   - 解开 BepInEx 6 IL2CPP(包里 `vendor\` 带着 be.790);
   - 从 SteamVR 复制 `openvr_api.dll`;
   - 在 `Cloudpunk_Data\globalgamemanagers` 的 VR 设备列表里加上 `None,OpenVR`(原文件备份成同目录的 `globalgamemanagers.cloudpunkvr-orig`);
   - 在 `Cloudpunk_Data\boot.config` 里打开图形 Job;
   - 放入插件、预加载补丁器、SteamVR 绑定、按键设置程序和玩家手册，写两个启动脚本。
4. 打开 Steam,双击副本里的 `Cloudpunk VR.bat`。第一次启动要多等一两分钟：BepInEx 要联网下载 Unity 基础库并生成互操作程序集，期间窗口是黑的。

想装到别处，或者脚本没找到游戏：

```powershell
powershell -ExecutionPolicy Bypass -File tools\install.ps1 -Source "E:\SteamLibrary\steamapps\common\Cloudpunk" -Target "D:\Games\CloudpunkVR"
```

再跑一次 `install.bat` 是安全的：已经做过的步骤会跳过，插件文件会被覆盖成包里的版本。

### 只更新插件

把 `CloudpunkVR-<版本>-plugin.zip` 解压到 VR 副本的根目录(有 `Cloudpunk.exe` 的那一层)，覆盖同名文件。或者在补丁包里：

```powershell
powershell -ExecutionPolicy Bypass -File tools\install.ps1 -PluginOnly -Target "D:\Games\CloudpunkVR"
```

### 卸载

删掉 VR 副本那个文件夹。Steam 原版和存档不受影响。

## 使用

完整的玩家手册是副本里的 [`README-CloudpunkVR.md`](README-CloudpunkVR.md):键位表、改键、手型校准、VR 设置的每一项、性能调法、已知限制。最常用的几条：

- 启动：先开 Steam(Quest 先在 Virtual Desktop 里连上电脑)，双击 `Cloudpunk VR.bat`,SteamVR 会跟着起来。`Cloudpunk (flat).bat` 是带同样性能优化的平面模式。
- 开车：右扳机油门，左扳机刹车和倒车，左摇杆左右转向、前后升降，右摇杆左右横移。A 交互，B 取消，X 切换驾驶舱和追尾视角，Y 物品栏，左握把全图，左手菜单键暂停。
- 步行：左摇杆朝视线方向走，右摇杆转身，X 在第一人称和原版镜头之间切换。
- 视角在读档、上下车、切换视角时自动校准；想重新对齐就长按右摇杆。
- 改键：双击副本里的 `Cloudpunk VR 按键设置.exe`,保存后一秒内生效。
- VR 设置在游戏自己的「选项」页里(分辨率、转身方式、可视距离、驾驶舱倾斜等 10 项);其余参数在 `BepInEx\config\palai.cloudpunkvr.cfg`。
- 掉帧先把「VR 分辨率」降一档，或把「可视距离」调近。

## 让 Coo 一起玩(Cortico World)

需要一个能跑的 [Cortico](https://github.com/Pal-AI-Lab/Cortico) 部署。World 的安装、页面和工具说明在
[cortico-world-cloudpunk-vr](https://github.com/Phantivia/cortico-world-cloudpunk-vr) 的 README 里。游戏这一侧不用另外装东西：
插件启动后在 `127.0.0.1:47660` 听着(`palai.cloudpunkvr.cfg` 的 `[Coo]` 段可改端口)，World 连上来、点「开始」之后才显示 Coo。

World 用到而这个仓库没带的两样东西：

- Coo 在通讯频道里的画面来自 VTube Studio 的 Spout2 输出，你要有自己的 VTube Studio 模型。
- 副驾驶座上的玩偶模型没有随仓库发布。想放自己的：用 `tools/fufu-export/export_fufu.py`(Blender 后台)把一个坐姿 FBX 导成
  `fufu.mesh`、`fufu_base.png`、`fufu_normal.png`,放进副本的 `BepInEx\plugins\CloudpunkVR\fufu\`。没有这个文件夹时插件跳过这一项。

## 从源码构建

1. 先按上面装好 VR 副本并启动过一次(编译要引用 BepInEx 生成的 `BepInEx\interop\*.dll`)。
2. 装一个 [.NET SDK](https://dotnet.microsoft.com/download)(8 或更新；只用它带的 C# 编译器)，以及 Git for Windows(脚本在 Git Bash 里跑)。
3. 在仓库根：

```bash
GAME=/d/Games/CloudpunkVR bash tools/build.sh
```

它把 `src/` 编成 `build/CloudpunkVR.dll`,把 `preload/` 编成 `build/CloudpunkVR.Preload.dll`,把按键设置程序编成 exe,然后部署进 `$GAME`。
加 `--no-deploy` 只编译。之后 `tools\install.ps1 -PluginOnly -Target <副本>` 也能把 `build/` 里的东西放进副本。

源码怎么组织、每个部分的做法和踩过的坑、测试通道的命令，在 [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md);性能分析在 [`PERF.md`](PERF.md)。

| 路径 | 内容 |
|---|---|
| `src/` | 插件源码(VR 相机、输入、界面、身体、画面修正、性能) |
| `src/Coo/` | 给 Cortico World 的桥、Spout 接收、通讯频道、路线和自动驾驶、直播画面 |
| `preload/` | BepInEx 预加载补丁器：限定引擎的 Job 工作线程数 |
| `bindings/` | SteamVR Input 动作清单和 Touch / Index / Vive 的默认绑定 |
| `tools/install.ps1` | 安装脚本(`install.bat` 调它) |
| `tools/build.sh` | 构建脚本 |
| `tools/ggm_vr_patch.py` | `globalgamemanagers` 补丁的 Python 版，带 `inspect` 和 `restore` |
| `tools/ControlsEditor/` | 按键设置程序的源码 |
| `tools/obs-scene.mjs` | 经 obs-websocket 建直播用的 OBS 场景 |
| `tools/spout-test-sender/` | 测试用的 Spout2 发送端 |

## 许可和声明

- 本仓库的代码按 [MIT](LICENSE) 许可。
- 补丁包里的 BepInEx(`vendor\`)是 [BepInEx](https://github.com/BepInEx/BepInEx) 项目的原样发行包，LGPL-2.1,许可文件在它的压缩包里。
- Cloudpunk 是 ION LANDS 的作品。这个模组与 ION LANDS 无关，也不包含游戏的任何文件。
