# CloudpunkVR 开发说明

Cloudpunk(Unity 2018.4.29,IL2CPP)的第一人称 VR 模组和性能优化，跑在 BepInEx 6 IL2CPP 上。安装和使用见仓库根的 `README.md`,
玩家手册是 `README-CloudpunkVR.md`;这里是开发说明：做法、踩过的坑、测试通道。路径都相对仓库根。
文中的「操作员」是戴头显玩的人，「可缇」是直播里的 Coo(Cortico Bot)。`G:\Games\CloudpunkVR` 是作者机器上装好的副本。

## 目录

| 路径 | 内容 |
|---|---|
| `src/` | 插件源码 |
| `README-CloudpunkVR.md` | 玩家说明，构建时复制到游戏目录 |
| `src/Coo/` | 直播同行：给 Cortico World([cortico-world-cloudpunk-vr](https://github.com/Phantivia/cortico-world-cloudpunk-vr))的桥、Spout 接收和通讯频道、头显里的直播 overlay、路线和自动驾驶、直播画面 |
| `tools/spout-test-sender/` | 测试用 Spout2 发送端(透明底、上绿下蓝、左上红块、转圈的白盘)，不开 VTube Studio 也能验接收；`bash build.sh <输出目录>` |
| `tools/obs-scene.mjs` | 经 obs-websocket 建 OBS 场景"云朋克直播间"(游戏捕获 + MC直播间 的覆盖层和配音);`--shot` 截捕获源 |
| `tools/il2cpp/gadis.py` | 反汇编 GameAssembly.dll 里的一个方法：用 `nativemap` 开发命令导出的地址表标出调用的方法名、浮点常量、icall 名、字段偏移 |
| `tools/aircar/` | 逆向 Aircar 的飞行参数(开车手感的对照来源):pak 解包(密钥用环境变量传)、exe 反汇编和标注，步骤和结果见其 README(这组脚本没有随仓库发布) |
| `tools/ControlsEditor/` | 按键设置小程序(WinForms,.NET Framework 4.x),和插件共用 `src/ControlMap.cs`,构建为游戏目录里的 `Cloudpunk VR 按键设置.exe` |
| `preload/` | BepInEx 预加载补丁器 `CloudpunkVR.Preload`：在引擎建 Job 系统之前限定工作线程数(装到 `BepInEx/patchers/CloudpunkVR`) |
| `bindings/` | SteamVR Input 动作清单(每个物理按键一个动作)和 Touch / Index / Vive 的 1:1 默认绑定 |
| `PERF.md` | 性能分析报告：测量方法、瓶颈、试过的办法和实测数字 |
| `tools/build.sh` | 编译并部署到 `$GAME`(装好的副本)。编译器是 Roslyn 的 csc:Unity 2022.3 编辑器自带的，或已装的 .NET SDK 里的 |
| `tools/install.ps1` | 从 Steam 原版重建 VR 副本(自己找 Steam 库；仓库根的 `install.bat` 调它)，或 `-PluginOnly -Target <副本>` 只更新插件 |
| `tools/ggm_vr_patch.py` | 给 `globalgamemanagers` 的 BuildSettings.enabledVRDevices 加 `None,OpenVR`(格式 17,无类型树)。`install.ps1` 里有同样逻辑的 PowerShell 版，这份留着 `inspect` 和 `restore` |
| `tools/asmdump/` | 列出互操作程序集里的类型和成员，逆向游戏代码用 |
| `vendor/` | BepInEx 6.0.0-be.790 IL2CPP 包(不入库，发布的补丁包里带着) |

构建：`GAME=/d/Games/CloudpunkVR bash tools/build.sh`(Git Bash;`--no-deploy` 只编译到 `build/`)。引用的是副本里 `dotnet`、`BepInEx/core`、
`BepInEx/interop` 的程序集，所以副本要先装好并启动过一次(BepInEx 第一次启动时生成 interop)。

## 工作原理

- **开启 VR**:Unity 2018.4 内置 OpenVR。设备列表首项留 `None`,平时是平面模式，带 `-vrmode OpenVR` 启动才进 VR。
- **头显跟踪**(`VRCameras`):关掉 Unity 的自动相机跟踪。Cinemachine 每次把游戏位姿写进相机(`PushStateToUnityCamera`)
  之后立刻乘上头部位姿；没有 Cinemachine 的相机在当帧第一台头显相机剔除前(`Camera.onPreCull`;这个版本的
  `Application.onBeforeRender` 被 IL2CPP 裁掉了)补上，帧末还原。原来全在 onPreCull 里做，Hx 体积光在它自己的 OnPreCull 里
  按未加头部姿态的相机建光体，探照灯和车灯光柱于是贴在屏幕上；挪到写入后马上做，之后所有读相机的脚本都看到实际视角。
  但 LateUpdate 里读到的头显姿态比渲染时的旧(运行时在渲染前再更新一次，合成器按新的做重投影)，只用它真头显上整个画面
  随转头乱晃(空头显不动，看不出来)：当帧第一台头显相机剔除前重读姿态，已摆过的相机从保存的游戏位姿重摆
  (`VRCameras.RefreshHead`)。两次读数的差每 30 秒记一行 `head:` 日志。游戏逻辑
  (Cinemachine、移动、交互)看到的一直是它自己摆的相机。步行时基准只取偏航，驾驶舱按 `CockpitTilt` 混入车身倾斜。
- **驾驶舱内饰**:游戏把内饰做成放在世界原点附近的一份副本，由 `MainCameraVehicleRenderer.OnPostRender` 在主相机每只眼
  渲染完后再画一遍。补丁在它之前给内饰相机设好这只眼的视图矩阵(换算到内饰坐标系)、投影矩阵和这只眼的缓冲。
  游戏原本在内饰相机的 `OnPreRender` 里设目标缓冲，那时已经来不及生效，两眼轮换缓冲时会隔帧画到另一只眼上，表现为闪烁。
- **输入**(`SteamVRInput` + `ControlLayout` + `VRControls` + `VirtualPad`):SteamVR 动作清单里每个物理按键一个动作(两根摇杆、
  扳机、握把、ABXY、摇杆按下、左手菜单键)，默认绑定 1:1 原样上报。它们各管什么由 `BepInEx/config/CloudpunkVR.controls.cfg`
  决定(格式见 `src/ControlMap.cs` 开头)，按场景(通用 / 驾驶 / 步行 / 菜单)映射到一个 InControl 虚拟 Xbox 手柄，所以用的是游戏
  自己的手柄绑定；只有键盘绑定的全图用 `ActionInjector` 直接按下游戏动作。文件每秒查一次修改时间，改了就重读。驾驶默认照
  Aircar 的 Touch 绑定。游戏的"操作控制"页(`VRControlsPage`)按这份配置显示 VR 键位，图示标签用游戏自己的本地化文字拼。
- **自动校准**(`VRHost.AutoRecenter`):读档后第一次进入可操作状态、在驾驶舱 / 追尾视角 / 步行之间切换时，0.6 秒后以当时的
  头部位姿重新校准。菜单和读条不算切换。Cinemachine 的镜头过渡在 VR 里改成直接切换。
- **步行方向**(`VRControls`):游戏步行不是相对相机走，而是相对一个世界朝向的参考系(`Player.moveTo`,目前所见都是 0°;
  `MoveToPoint.localPosition` 被当作世界方向，`Player.Movement` 朝它 RotateTowards 后沿朝向走)。摇杆按"相机偏航 + 头部偏航"
  (或左手柄偏航)转进这个参考系；参考系由稳定推杆时摇杆方向与 `MoveToPoint` 的夹角实测，换镜头区也对。第一人称时
  `Movement` 前缀让角色立即面向摇杆方向(侧移不画弧)，站着时面向视线(交互目标跟着视线选)。
- **转身**(`VRControls.Turn`):默认平滑(`TurnMode = smooth`),照 VRChat:转速 = 摇杆(去死区后)的 1.5 次方 × `SmoothTurnSpeed`,
  再经 0.05 s 的指数缓冲，每帧把增量加到第一人称 POV 的偏航轴上(`m_HorizontalAxis`,范围 ±180 且 wrap,不限角度)。
  脑在 LateUpdate 里求值，所以转角每个渲染帧都更新，`turntrace` 实测每帧增量正比于帧时间。转身以头为轴：同一偏航
  `VRCameras.TurnAroundHead` 把校准中心绕当前头位置反向转过去，眼睛和手柄的世界位置不变(实测头偏 0.3 m 转 90°,眼位不动);
  界面锚点同样反转，身体朝向加上游戏视角偏航的增量。分段转身(`TurnMode = snap`)走同一条路径。旧配置项 `SmoothTurn`
  (默认关)换成 `TurnMode` 后，启动时从配置文件的孤立项里删掉。
- **第一人称身体**(`VRBody`):游戏里有一台第一人称虚拟相机 `PlayerFirstPersonCam` 但从不启用；步行时把它的优先级提到 50。
  角色是一节节刚体体素(`thin_factory` 下 Hips/Spine/Head/UpperArm/LowerArm/Hand 变换，MeshAnimator 摆姿势，没有蒙皮)，
  渲染前把模型横向挪到头显下方(颈部在眼睛后 0.12 m)、按身体朝向转过去，两臂按肩、肘、腕分开解(`VRBody.Arm`):手的目标朝向 = 手柄朝向 × `Arm.Grip`(`HandPitch`/`HandYaw`/`HandRoll`,
  左手偏航和横滚取反；默认 25/0/14,即指节比手柄指向低 25°、手背在握把外侧略朝上)，手腕在握把中心后 8.5 cm。手型校准
  (`VRControls.HandCalibration`,步行时按住左摇杆 2 秒，3 秒倒计时)按"两手掌心相对、手指朝头的水平朝向"反解每只手的握持旋转，
  两手平均后写回三个配置项(`SolveGrip`,`handcaltest` 用已知握持往返验证：40/10/30、-10/-20/-5 都原样解回)。手模型的轴
  (手背 = 手骨 +Y,厚度方向；拇指在使"掌心 = -手背"成立的一侧)按 `handmesh` 导出的网格核过，右手网格是镜像的(缩放 z = -1);够不到时肩膀前送至多 6 cm;肘的极向量默认下、外、后，按手背相对拇指朝上的转角转一半(手心朝上收肘、朝下外张)，
  并保证不进胸口；前臂分担手腕拧转的 65%(swing-twist 分解),手的朝向完全等于手柄(原来手腕摆动限 70°、拧转限 35°,真头显上手跟不上手柄，
  操作员嫌不灵活；`armcfg` 后两个参数小于 180 可以恢复限位做对比)。骨骼轴在绑定时从层级位置和手部
  网格中心推出。头部渲染器改成只投影；
  帧末全部还原，游戏逻辑看到的还是它自己的姿势。GDOC 遮挡剔除会把贴着相机的身体判成被遮挡，绑定时把角色从它的登记表里移除。
- **后处理**(`VRPostFx`):景深在 VR 里关掉(驾驶舱的后处理体积会对焦在挡风玻璃上)；Amplify Bloom 的镜头鬼影、光环、
  脏污、星芒和 WhiteCat 的车灯屏幕光斑关掉(按屏幕位置画，两眼对不上)；WhiteCat 全局雾的视线向量
  (`_CameraVectorX/Y/Z`，前向分量等于远裁剪面，原点在 uv(0,0))每只眼渲染前按这只眼的视图和投影重算；车窗着色器
  (Roman's Windshield Shader)的抓屏模糊按 `WindshieldBlur` 缩小、采样数随之减少，水珠改成白色、亮度 1.3 倍、折射 2.2 倍。
  反汇编和常量缓冲区布局的取法见下面"着色器"。
- **车灯光晕**(`VRGlows`):带 WhiteCat `LensFlareSource` 的灯(交通车头灯)，渲染前在灯的位置放朝向视线的加法混合方片
  (`Legacy Shaders/Particles/Additive`,关卡加载后才找得到)，亮度按灯朝向、距离、灯光强度算，能被建筑挡住。光源靠补丁在
  `LensFlareSource.OnEnable/OnDisable` 登记(车流进出对象池也会触发)，固定参数登记时读一次，灯的开关、亮度、颜色每帧轮流
  刷新 16 个，每帧每个光源只读一次变换矩阵；全部方片在一个网格里，颜色走顶点色，每只眼一次绘制。原来每秒全场景搜一次、
  每个光晕一个物体、每个光源每帧十几次引擎调用：中城区 0.48 ms，现 0.14 ms。
  标题画面主相机关着，城市由 `Global/UI/Menu/MenuCamera` 画；光晕、立体雨和 Hx 截图取相机都走 `VRCameras.WorldCamera`
  (主相机在画用它，否则用菜单相机)。原来按关着的主相机摆，标题出租车的车灯光晕被朝它拉出几米、转头时拖在后面。
- **按键提示**(`VRPrompts`):游戏的按键提示(`InputIcon` 的各个 Setup* 方法)在 VR 里显示的是键盘键或 Xbox 图标，补丁把它们
  改成当前按键文件里实际执行那个动作的 VR 键(A/B/X/Y/LT/RT/LG/RG/LS/RS/MENU),画在游戏键盘提示用的键框里。按键文件第 2 版
  让 A/B/X/Y 和 Xbox 同名键一致(X = 切换第一 / 第三人称或驾驶舱视角，原 X 上的全图挪到左握把，握把横移去掉，右摇杆横移);
  旧文件里还是旧默认值的项自动迁移(`ControlMap.Migrations`)。
- **界面**(`VRUI`):游戏的 UI 相机改为单眼渲染到 1920x1080 贴图，贴在私有空间里的一块面板上，由一台深度 100 的立体相机
  最后叠加，不会被驾驶舱挡住。Screen Space Overlay 画布(读条画面)改挂到 UI 相机上。每帧渲染前重新设定 UI 相机的目标贴图和
  纯色清屏(游戏会把它改回只清深度，界面上移动的元素就会叠出重影)；UI 相机停画时清空贴图并隐藏面板。UI 相机关掉 XR 自动
  跟踪、旋转每帧放回单位旋转：游戏把它改回画屏幕的那几帧里(下车时就有)Unity 会按头的姿态转它，转过的角度留下来;HUD 画布
  跟着相机看不出来，但步行时小地图的图标被设成世界旋转为零(`Minimap.DrawRadarMarkers`,驾驶舱里才用本地角度)，就斜着立出地图
  (11-10 操作员报「下车后小地图标记全飞起来」;日志里 `UI: the game's UI camera was turned` 记着纠正的角度，没在头显上验)。面板跟着头的位置
  (0.15 s 平滑);开车时朝向固定在座位前方，步行和菜单里头转开 22°/35° 后跟上来直到正对。没有别的相机往头显画世界时(全图会关主相机)
  面板相机自己清成黑色，否则面板每帧叠在上一帧上拖出重影；判断看所有启用的、无目标贴图的头显相机，不只主相机：标题画面由
  `Global/UI/Menu/MenuCamera` 画，只看主相机那一版把标题和立体菜单清黑了。`VRPostFx` 的逐眼雾向量也对菜单相机生效
  (凡是带 PostProcessLayer 的头显相机)。
- **可视距离**(`VRFarView`):游戏的视野在四处收在约 600 m:主相机远裁剪面(经 Cinemachine 每帧设回)、Unity 线性雾
  (300–600 m,`foginfo` 查)、WhiteCat 的后处理 GlobalFog(城区 near 500 / far 600,density 0.0176,transmittance 2.744,
  它的 FloatParameter 值读不出，`fogglobals` 读它写的 shader 全局量)、主相机 `layerCullDistances`(Default、Floor、Streets
  等 600,Vehicle 300,MidRangeDetail、StreetDetails 250,Lamps 200,Character、SmallDetail、Interior 100)。渲染前：远裁剪面
  推到 `DrawDistance`(默认 2500);Unity 雾改成长坡(`DrawDistance` 的 30% 起，1.05 倍处满);GlobalFog 在
  `UpdateGlobalParams` 后置补丁里 near/far 乘 k、density 除以 k(k = DrawDistance / 600,只对主相机、只在远景生效的帧);
  只有裁到游戏全程(600)的图层拉到 `DrawDistance`,短程图层不动。帧末还原。取舍的实测(第 3 层驾驶舱同一画面，空头显):
  帧时间卡在渲染线程(约 11 ms,显卡 5.5–6.5 ms),每多一个渲染器就多两次绘制；600–1500 m 可见渲染器约 630 个，几乎全是
  Default 和 Floor;0–600 m 里 Vehicle 有 600 个，原来按平方根拉远短程图层多出的主要是车流。LOD 只有两级，`lodBias` 从
  4 降到 1.5 帧时间不变。指数平方雾(远处剩 3%)也试过，中景雾得更早(1500 m 时 600 m 处只透 57%)，没用。
- **两眼一致的天空雾色**(`VRStereoFx`):WhiteCat 雾把一张屏幕空间的小天空贴图(`_GlobalFogSkybox`,`ViewToGlobalTexture`
  每帧由 SkyboxCamera 画)混进远处。原来按左眼投影画一张两眼共用，Quest 3 两眼视野镜像不对称，右眼的雾里是左眼的云光。
  现在 SkyboxCamera 用两眼视野的并集画(`SkyUnionProjection`),它画完后按每只眼在并集里的范围各 Blit 一张，主相机每只眼
  渲染前切全局贴图。先试过每只眼在渲染回调里各 `Camera.Render` 一次：显卡多 0.7 ms,主线程 12% 时间卡在等渲染线程。
- **Hx 体积光逐眼**(`VRHx`):Hx 把同时画两只眼的相机当单通道立体(`IsRenderBoth` 时置 `SinglePassStereoUsed`):主矩阵组
  取右眼、"2" 组取左眼，留给 shader 按眼序号挑。游戏是多通道，shader 只读主组，左眼画的是右眼的画面。Quest 3 上测试灯放在
  4 m 前，左眼体积光缓冲里光晕在 u 0.384,左眼该在 0.626;右眼 0.377 对 0.376。空头显两眼投影对称，量不出来。Hx 每帧在自己的
  `OnPreCull` 里录全部命令缓冲(`BuildBuffer`:每次清掉旧缓冲换新的)，两只眼执行同一份。现在那次录左眼，左眼渲染完
  (onPostRender)再调一次它的 `OnPreCull` 录右眼(单调 `BuildBuffer` 录不进灯：灯要 `FindActiveLights` 先找出来，右眼一片空)。
  每次录：`CreateLightbuffers` 前把相机的 `MatrixVP`(GPU 投影 true × 视图)、`MatrixV` 设成这只眼的(点光、聚光的体积矩阵
  `VolumetricMVP` 由它乘出)，在 `VolumetricRender` 和灯的缓冲里追加这只眼的 `InverseProjectionMatrix`/`1`/`2`(原始投影的逆)、
  `hxInverseP1/2`(GPU 投影 false 的逆)、`InverseViewMatrix`、`hxCameraToWorld`/`2`、`_InvViewProj`、`hxLastVP`/`2`
  (上一帧同一只眼的，按眼保存)。时间滤波历史按眼分开：右眼那次录换上自己的 `TemporalTexture`(`DontUnloadUnusedAsset`,
  否则切场景被卸掉)。之后两眼光晕 0.625/0.375(应为 0.626/0.376)。第二次录主线程 0.07–0.11 ms,显卡不变。录制时取的眼矩阵
  和当帧渲染时一致(查过不是滞后一帧)。灯光剔除平面按 `fieldOfView` 算(对称)，不对称视野的外缘可能漏剔/误剔，没处理。
- **高度限制提醒**(`VRAltitude`):车的 `currentFloorLevel`(车底下那个平面，车本身高约 2.6 m)被限制在 `layerMinY..layerMaxY`。
  推着升降杆往限制走、距离 10 m 内显示，琥珀色；到限制(0.5 m 内)还在推就红色闪烁，两手轻震(1.5 s 一次);松杆后 0.35 亮度
  留 1.5 s 再淡出。驾驶舱里挂在内饰下(PlayerCar 层，由内饰相机画)，上限在挡风玻璃上沿、下限在仪表台上方；追尾视角和平面模式
  挂在 UI 相机上。升降输入来自 `VRControls`(玩家)和 `CooPilot`(自动驾驶),写在 `VRAltitude.LastClimb`。实测第 3 层：上限
  289.5,推杆时 6.6 m 前出现，到顶变红，松杆淡出；往下在楼顶上方停住(实物挡住，不是下限)不提醒。
- **雨**(`VRRain`):游戏的雨是两个后处理效果：`RainAndSheets`(按 `InverseView` 从视线方向程序生成雨幕)和
  `SplashesAndDrips`(地面水花、镜头水痕、闪电)，VR 里两眼同一片雨幕，雨挂在无穷远。两者都不覆写 `IsEnabledAndSupported`,
  所以补丁打在基类 `PostProcessEffectSettings.IsEnabledAndSupported` 上，按原生类指针挑出这两个返回 false;`RainAndSheets`
  被问到且本来要开，就记为"游戏在下雨"。代替它的是一张预生成的雨丝网格：6 m 的格子里 34 根，横向 5×5、纵向 3 层(7 m 一层)
  重复，所以整体有周期；每帧按格子吸附到世界坐标再往下平移，雨点固定在世界里下落。离眼睛 1.1 m 内的雨丝压成零面积(集合变了
  才重传顶点)。放在 TransparentFX 层：主相机和标题的菜单相机都画这层(菜单相机不画 Default)。标题画面 `SceneLoader.IsLoading()`
  一直为真，所以不用它判断。下雨时所有 Hx 相机的 `Density` 乘 1.5(雨停还原);Hx 密度 0.1 与 0.3 的显卡时间 5.08 / 5.25 ms。
  空头显以外的实测都在操作员的 Quest 上做(头显开着省电模式，游戏被限速到 36 帧，帧时间不可比，看每帧 CPU、GPU 毫秒；见 `PERF.md`)。
  合成器的逐帧记录(每帧显示次数、重投影与限速标志、合成器空等时间)在 `[Debug] FrameTimingLog` 打开或开发命令 `frametiming` 后，
  每 30 秒写进日志 `frame timing:` 行(`VRFrameTiming`),默认关。原来一次用 `GetFrameTimings` 取 128 条进按 184 字节排的数组，
  SteamVR 运行时按自己更长的条目写，越界写坏进程(.NET 抓不住的访问冲突，游戏秒退);现在用 `GetFrameTiming` 一次一条，写进 1 KB 的独立缓冲再读。
- **开车手感**(`VRControls`、`ControlLayout`):车的物理不动，只在输入层对照 Aircar(数值是从 Aircar 的程序里读出来的)。
  默认死区照 Aircar 的 `DefaultInput.ini`(摇杆 0,这里留 0.08;扳机 0.15),升降输入按 Aircar 的 FInterpTo 速度 0.95 平滑
  (`ClimbSmoothing`)。原作的阶跃响应用 `carlog <秒> <间隔>` 测过：原地转向 0.3 秒到位、最快 85°/s,行驶中 2.4 秒到 62°/s,
  升降 0.4 秒，横移 0.8 秒；Aircar 转动 0.5 秒、升降约 1 秒。原作比 Aircar 慢的轴不加平滑。
- **游戏内设置**(`VROptions`、`VRControlsPage`):克隆游戏选项页的"泛光"行做 10 个 VR 设置，隐藏 7 个桌面显示设置，
  重建显式导航链；"操作控制"页改写成 VR 键位并禁止在那里改键。中文字库只含游戏用过的字，建行时逐字检查，缺字回退英文。
- **性能**(`Perf`、`CpuTopology`、`preload/`，分析和实测见 `PERF.md`):
  - `boot.config` 加 `gfx-enable-gfx-jobs=1`(`tools/install.ps1` 写入)：D3D11 上是旧式图形 Job，两只眼的绘制命令在 Job
    线程上准备，VR 主线程每帧少约 3 ms。引擎实际的线程模式写在 "VR mode on" 那行日志里(从 `UnityPlayer.dll` 读，见 `PERF.md`)。
  - 预加载补丁器把引擎的 Job 工作线程从"逻辑处理器数 − 1"限到最多 8 个(`[Performance] JobWorkerThreads`)。
  - 进程退出 Windows 的功耗节流(`FullSpeedInBackground`)。
  - 小地图、后视镜、雨水遮罩、停车屏几台辅助相机隔帧渲染(渲染前临时关、帧末恢复)：小地图每 3 帧、后视镜 6、雨水遮罩 4、
    停车屏 6。配置还停在旧默认值的升到新值(`TuningVersion`)，玩家改过的不动。
  - 全场景搜索的维护(相机整理、天空相机、泛光的镜头效果、车窗、舒适滤镜、拖尾)只在状态变化(场景、上下车、视角、菜单)后
    1 s 和 5 s、以及相关选项改动时跑；原来每 2 秒一次，每次 6–14 ms，每 2 秒掉一两帧。
  - 车流拖尾(`TrailRenderer`，朝相机展开，每台相机每只眼重建)每 0.6 m 记一个点，原来 0.1 m。
  - VR 下另有：
    - 天空盒相机改成单眼，每帧只画一次雾效用的小天空贴图，原来是两只眼各按全分辨率画一遍再被盖掉。
    - 关掉 Cinemachine 的碰撞扩展和待机更新。
    - Cinemachine 推送状态时临时把主相机标成非立体，避免每帧 "Cannot set field of view" 警告。
    - 阴影 120 m 两级级联。舒适滤镜(关景深、动态模糊等)默认不开，操作员不晕 VR。
    - 分辨率按 SteamVR 推荐值的百分比给。Unity 在缩放 1 时画多大因 SteamVR 设置而异(见过推荐值的一半，也见过等于推荐值)，插件启动时实测换算系数。

## 直播同行(`src/Coo/`)

和 [cortico-world-cloudpunk-vr](https://github.com/Phantivia/cortico-world-cloudpunk-vr) 配套，协议见那边的 `docs/protocol.md`(第 2 版)。场景：操作员戴头显玩，可缇照常经
vtuber World 说话，同时接在游戏的通讯频道上;直播间通过操作员的视角看到她和她的台词。World 点「开始」后才显示
(`onAir`),断线即撤。

- **桥**(`CooBridge`):`TcpListener` 只绑 127.0.0.1(`[Coo] Port`,默认 47660),一行一个 JSON。后台线程收发，所有消息在主线程
  `Pump()` 里处理；请求可以延后回复(截图、预览要等帧末)。
- **游戏状态**(`CooGame`):模式(读条/菜单/对话/全图/驾驶舱/追尾/步行)、城区、钱、车(速度、油、在不在停车位)、任务栏
  (`Global.questTexts`,`QuestText.markerPos` 是地图标记)、屏幕上的对话(`DialogHUD` 的名字和正文，打字效果停了才报)、
  面前可交互的对象(`Player.interaction.closestObject`)。每秒四次发状态帧，比对出事件(任务增删改、对话、城区、钱、上下车)。
- **Spout 接收**(`CooSpout`):Spout2 发送端在共享内存 `SpoutSenderNames` 里列名字(每个 256 字节 ANSI,遇空止)，以发送端名
  命名的共享内存里是 SharedTextureInfo(32 位共享句柄、宽、高、DXGI 格式…)。句柄在游戏自己的 D3D11 设备上
  `OpenSharedResource`(设备从任一 Unity 贴图的原生指针 `GetDevice` 取),建着色器视图交给 `Texture2D.CreateExternalTexture`。
  每帧把方框要的那块(放大倍数和中心)用一个 GL 四边形、`Legacy Shaders/Particles/Alpha Blended` 叠到深色底上，画进自己的
  256² 贴图，游戏读的是这份拷贝。踩过的坑：
  - `Graphics.Blit` 带材质时会重置贴图的缩放和偏移，裁切不生效，所以改成 GL 四边形。
  - 游戏是线性色彩空间：发送端的字节是 sRGB,视图按普通值读，画进 sRGB 贴图时关掉 sRGB 写入(`GL.sRGBWrite`),字节原样落下，
    游戏采样时再解码；材质的颜色属性在线性空间会被线性化，0.5 的着色要按 gamma 给(0.7354)。
  - 这条路径下画面要上下翻转(`FlipY`,默认开)。
  - 共享贴图不能跨显卡：本机是核显 + RTX 5090 笔记本，默认适配器是核显。发送端(VTube Studio)要在 Windows 的显示卡设置里
    设成高性能;打不开(0x80070057)时插件给出这句提示。
  - 每秒查一次发送端列表和句柄(VTube Studio 改输出尺寸会换贴图)。
- **通讯频道**(`CooChannel`):游戏的通话在两处显示：驾驶舱里方向盘左边的通讯屏(内饰副本里的世界空间画布
  `Global/_Vehicle_Interior_Local/Canvas/Dialog_Interior_Global`,头像 129²)，其余时候界面左下角(`Global/UI/Canvas-UI/HUD/
  Dialog/Canvas_Group_Exterior_Global`,头像 120²)。驾驶舱那块(游戏 NPC 的和可缇的副本)在头像和台词后面垫一块深色底板
  (`CloudpunkVR_Backing`,0.82 不透明),原来的扫描线底图 `Scanlines Background (2)` 调淡到 45%:原来台词底下透着城市，头显里
  像隔着纱窗。两处各复制一份成自己的方框(副本不带 `DialogHUD`,游戏的对话逻辑碰不到),
  头像 `Image` 换成同材质的 `RawImage`(`Dialog UI Holo`,着色器 `Sci-Fi/SSFS/Base`:NPC 头像的像素化、扫描线、撕裂)放 Spout
  拷贝；名字写「可缇」,正文是 vtuber World 字幕的当前句，按发声时长逐字显示(`maxVisibleCharacters`),字号放不下自动缩小。
  VR 步行时左前臂背上竖着一块自己的小屏(`CooWristScreen`,约 11×14 cm):可缇居中在上，名字在下，再下面是台词。内容是一张
  屏幕外画布，由一台离屏相机在 Update 里画进贴图;臂上只是一块面片，在渲染回调里按左手柄姿态和手型的握把坐标系摆，和手模型
  同一时刻，所以走动转身时跟手臂刚性一起动(原来的世界空间画布走动时会落后一帧)。`wristbox <沿臂> <抬高> [宽]` 调。名字用天蓝色加外发光，和 NPC 区分。
  读给 World 的游戏信息(`CooGame`):对话(带是不是通讯)、任务、获得物品/钱/新地点的弹窗(`UnlockedItem`)、教程提示、油量、
  交互提示(角色名、拾取物的物品名、触发器头顶的提示字;游戏不显示名字的不报)。读档或连上后玩满 2 秒前的变化不算新消息，
  模式稳定 1.2 秒才报。`DialogHUD` 的物体一激活就算 NPC 占线：方框
  0.25 s 淡出，并给 World 发 `channel` 事件(带当时可缇是不是在说)。游戏字库 `CNs Text` 覆盖常用中文。
- **副驾驶玩偶**(`CooFufu`):坐姿的可缇 fufu(模型没有随仓库发布，没有时插件跳过这一项),`tools/fufu-export/export_fufu.py`(Blender 后台)把一个 FBX 导出成三角网格
  (坐姿静止，Unity 轴向、朝 -z)和两张 2048 贴图到 `assets/fufu/`,`build.sh` 部署到 `plugins/CloudpunkVR/fufu/`。插件在工作
  线程读网格、解 PNG、算整条 mip 链(`LoadRawTextureData` 对带 mip 的贴图要全部层级),Standard 着色，自带一盏小补光。挂在
  `interiorLocal` 下、PlayerCar 层，从相机座位(`interiorCameraTransform`,不跟头)算位置：右 0.77、下 0.555、后 0.04 m,朝驾驶员
  转 40°,放大 1.5 倍(约 47 cm);随车的加速和转弯轻轻晃。座垫位置用 `interiorparts` 量的。`fufu`/`fufulight`/`fufushader` 调。
- **出框全息**(`CooHoloFigure`):驾驶舱里可缇的全身站在通讯屏头像框的上沿。游戏是 2018 IL2CPP,编不了新 shader,
  全用构建里都有的:全身取景每帧画进一张 256×512 的贴图(Spout 画面切成横条，偶尔一条横移=撕裂;再用 `Hidden/Internal-Colored`
  设成相乘画滚动扫描线和闪烁),车里是两块面片(这张图按透明混合，再加色放大一点当辉光，游戏的泛光会吃到);投影光锥、脚下
  光环、上升光粒是顶点色网格走加色粒子 shader;一盏青色点光源照驾驶舱。驾驶舱通讯屏的画布是内饰里的世界空间画布，全息挂在
  `interiorLocal` 下、PlayerCar 层(内饰相机只画这层),按头像框四角摆。全身取景图用 `UI/Default` 画(旧粒子 shader 是
  `ColorMask RGB`,不写透明度，画出来整张透明)。
  `holofig <身高> <抬高> <外移> [偏航] [取景]` 调位置。
- **地点和领航**(`CooPlaces`、`CooPilot`):地点来自游戏地图自己登记的雷达物体(`Minimap.radarObjects`):有地名标签的用标签
  (`LocationText`/`GetLocalizedText(LabelID)`),绑了角色的用角色名，其余按全图图例(`MapLegendID` 旁边的字)归类;再加任务
  标记。10 秒重建一次，编号由名字和位置定。`navigate` 的 `place` 可以是编号或名字/种类(最匹配里最近的)。操作员自己开着
  路线时，偏离 55 m(步行 12 m)3 秒就重算(至多 6 秒一次，没规划出来就 20 秒，事件 `rerouted`);前方 40° 以上的转弯、30 m 内
  15 m 的升降在约 8 秒(至少 120 m)前报一次(`maneuver`),快到时报一次 arrive。`places [词]`、`navto <地点> [drive]` 是开发频道的
  对应命令。
  任务在另一层(`QuestText.markerInScene` 不是当前场景)时用游戏自己的跨层标记：`MinimapObject.Setup` 按标记比本层上限高
  20 m 以上、低 20 m 以上或之间，在朝上、朝下或平层的升降梯(`Lift.direction`)里挑「玩家→梯 + 梯→标记」最短的(目的地不是
  任务那层的，2、3、7、8 号场景不选)，把任务的地图标记挪到梯口。`CooGame.MarkerPos` 读这个标记的位置，领航把路线规划到那台
  `Lift` 的触发器里(没有停车位)，报 `maneuver` 的 `lift`;车进梯(`PlayerCar.isInLift`)或读条时进入 `lift` 阶段，出梯在新的一层
  飞稳 1.5 秒后用同样的请求再规划一次(事件 `rerouted` 带 `afterLift`;最多连换 4 次)。状态帧的任务带 `otherLevel`。
- **头显里的直播 overlay**(`CooOverlay`):World 在无头 Edge 里打开 OBS 叠在游戏上的那几个浏览器源，透明背景截 PNG,
  变了才发(`overlay_frame`)。插件在 VR 界面的私有空间里(`VRUI.Space`、它的层)放一块 OBS 画布大小的世界空间画布，每层一个
  RawImage,按 `config.overlay.layers` 的左上角坐标和尺寸摆(和 OBS 一样)，由界面面板相机最后画，不会被驾驶舱挡住。每帧按
  当帧头部姿态摆在 偏航/俯仰/距离 处，跟头刚性锁定。PNG 在工作线程上解(.NET 的 zlib + 这里的行过滤器),主线程每帧最多
  上传一层。直播画面不画它(OBS 自己叠同样的页面)。
- **路线**(`CooRoutes`、`CooSpace`):交通网是 `AIManager.tracks`(VehicleTrack 的采样点，约 500 条)，端点 14 m 内相连，A*;
  逆行代价 ×1.35。网络两头用局部三维自由空间连起来：以起点和终点为中心、5 m 一格的 Dijkstra 洪泛，每格用车大小的
  `Physics.CheckBox` 判断能不能过(只对城市本身的层：Default、Floor、Obstacle、Streets、StreetDetails、MidRangeDetail、Lamps、
  Water;这个版本的 `Physics.GetIgnoreLayerCollision` 对车的层全报忽略，只能按名字列)，高度覆盖这一层车能飞的范围(停车平台
  到航线常差六十多米)，再用球形投射拉直。
  - 规划分帧做(`CooRoutes.Job`,每帧 1.5 ms,`planbudget` 调):11-10 直播里每次重算在主线程上一口气 0.3–0.4 秒，偏航重算时
    整个游戏卡一下。现在一条 1.3 km 的路线约 100–160 帧、总共 200–300 ms 的规划时间；重算时旧路线留在图上，新的好了才换。
    `navigate` 请求等规划完才回复(World 等 15 秒)。
  - 洪泛带目标点：附近的航线采样点和终点都定下代价、或者比第一个定下的远出 150 m 就停。终点一侧的洪泛按终点留着，偏航重算只
    洪泛车这一侧。
  - 车大小的盒子只量格子中心，所以：上下两格之间要从下面那格中心往上扫一块扁盒子(扫过的面都算挡，两列都要通)，否则停车平台
    这类楼板会被穿过去；盒子整个落在厚楼板里面时重叠测试看不到(网格碰撞体只有表面)，用开着 `queriesHitBackfaces` 的上下两条
    射线判断：上下都打到背面且相距不到 10 m 就是在楼板里(升降梯管子是 38 m 高的空腔，不算)。路线最后一段从格子中心到终点也要
    没东西挡，不然会从楼板下面那格直上终点。
- **自动驾驶**(`CooPilot`):纯追踪(前视距离随速度 10–45 m,离终点 40 m 内最短 5 m)，速度按前方弯度(含上下)和剩余距离限，
  再按前方坡度限到车升降(约 5 m/s)跟得上；比所在路段高或低 3 m 以上先调高度。高度跟路线并夹在楼层范围内，低速时用横移纠偏，
  前方有东西就减速抬升，卡住(油门踩着不动，或要升降升不动)后倒一下再重规划，竖直的路段原地升降。刹车就是倒车，油门刹车按
  朝前的速度算。停车位和升降梯离 30 m 内、且车大小的盒子直线扫过去没挡(起点就贴着东西也算挡)才直线进去，否则沿路线最后一段走；
  11-10 以前 30 m 内就直线开过去，停车平台在上面时从楼板底下往上顶。输入写在虚拟手柄上(VR 时在玩家
  输入之后覆盖)，所以油耗、限速、楼层都是游戏的。进了停车位停稳按 A 停车，游戏把人放下车算到达。VR 时玩家一碰摇杆或扳机
  就交还。步行用 `NavMesh.CalculatePath`(这个版本只留了这一个 NavMesh 方法)的拐点，按步行参考系换算成摇杆。
- **地图上的路线**(`CooRouteLine`):小地图和全图是俯视相机看 Minimap 层，每台相机的裁剪范围是它下方的一段高度；各放一条
  LineRenderer 在相机正下方，只出现在对应的地图上，盖在地图之上，走过的部分去掉。驾驶舱里的小地图是内饰下的
  `_Minimap_Mesh/Canvas-Map/Small/MiniMapCamera`(画进 `MiniMap` 贴图),以前被当成预览副本排除了，所以车里一直没线；现在所有
  `MiniMapCamera*` 都画。找相机要遍历游戏所有相机(约 3.6 ms),只在路线显示时、进出驾驶舱时和每 15 秒找一次(原来每 3 秒一次);
  点只在路线、进度或相机高度变了才重写，平时每帧只改第一个点(玩家)。`Perf` 在跳过的帧里渲染前把地图相机关掉，所以线只看相机物体在不在，不看 enabled。
  线是青色带白箭头的贴图(`Particles/Alpha Blended` 认贴图缩放偏移，`Sprites/Default` 不认),按长度平铺、朝终点流动。
- **直播画面**(`CooView`,VR 的 spectator):帧末把主相机再按单眼渲染一次(78° 视野、去侧倾、转向平滑)到窗口大小的贴图，
  平滑在跟车走的追踪空间里做(头部姿态 `HeadPos`/`HeadRot` 平滑，再乘当帧的 `MainBasis`、加 `MainGamePos`);原来在世界坐标里
  平滑位置，车一动画面就落后 车速/25 秒(60 km/h 落后 0.7 m),驾驶舱里看起来像坐到了座椅后面。
  叠上游戏界面贴图,`Graphics.Blit` 到窗口。界面只在重新渲染的那帧叠一次(原来每帧都叠，隔帧时半透明处会越叠越浓)。头显镜像保持开着(关掉后这个版本不再往窗口呈现，目标眼为 None 的相机也是黑的)，帧末覆盖镜像。
  内饰相机在这次渲染里要画进主相机当前的临时缓冲(`activeTexture`,后处理会再盖到最终贴图上)，雾的视线向量按这次的单眼
  投影算。截图(`snapshot`):VR 时 `ScreenCapture` 拿到的是左眼，不是窗口。
- **IL2CPP 互操作里不能用的**:`RectTransform.GetWorldCorners(Vector3[])`(结果不回写托管数组，全是零;用 `rect` + `TransformPoint`)、`Graphics.DrawTexture`(参数结构体封送失败)、`ColorUtility.ToHtmlStringRGB`(走 Il2Cpp 的
  `String.Format`)、`Font` 构造函数(`UiFont.Os` 手工建)、`NavMesh.SamplePosition`、`AsyncGPUReadback.Request`(被裁掉)。

## 测试通道

操作员在用电脑时用 `tools/launch-dev.ps1` 起测试通道：窗口不激活、开在副屏(`-cloudpunkvr-window x,y`)、焦点还给原来的窗口
(`-cloudpunkvr-refocus`)、鼠标不锁(`src/DevWindow.cs`);启动前把注册表窗口设置改成 1280x720 窗口化，收工后 `-RestoreRegistry` 改回。
启动参数加 `-cloudpunkvr-dev -cloudpunkvr-nosave`:插件轮询 `BepInEx/plugins/CloudpunkVR/cmd.txt` 逐行执行命令，
窗口固定 1280x720,存档写入被跳过。常用命令：`waitstate <子串> [秒]`、`wait <秒>`、`continue`、`press <InControl 按键> [秒]`、
`gopark`、`freeze 1`、`shot <名>`、`burst <名> <帧数>`、`uirt <名>`、`bench <秒> <标签>`、`perf <minimap|mirror|rain|parking> <0|1>`、
`film <job.json>` / `filmstop` / `campose [名]`(宣传片取景：按关键帧驱动主相机、固定步长逐帧存彩色、可多子帧做动态模糊，
可在游戏里重建 VRStage 的舞台和人（`src/FilmStage.cs`）、整组搬动标题布景、跑自己的 HLSL 全屏 pass（`src/FilmFx.cs`，直接调 D3D11）、
复制游戏的飞车跑自己的车道（`src/FilmTraffic.cs`）、强制下雨、按名字关掉 NPC 名牌和交互提示等世界空间画布；PNG 在后台线程编码
（`src/FilmPng.cs`），每 30 帧日志给出一帧的时间花在哪：游戏自己的更新渲染、读回、动态模糊、等编码，以及我们自己的人、退场粒子、
舞台、道具、车流、fx、画布各占多少；见 `src/Film.cs` 开头与 VRStage `workspaces/cloudpunk/NOTES.md`)、
`scene <编号>`(游戏自己的换区，7 = 阿瓦隆高区)、`tpto <x> <y> <z>`(挪玩家的车：城区和车流只在玩家附近加载)、
选点用 `slice`(按高度出空地图 PNG)、`pathcheck`(镜头路径扫碰撞)、`neon`、`findclear`、`probe`、`probehits`、`where`、`filmlayers`、`scenes`(`src/FilmScout.cs`)、
`dump`、`tree <options|controls|路径>`、`xref <类型.方法>`、`click <路径>`、`fakehead <偏航> <俯仰> [abs]`(abs 时取代头显
朝向，平放在桌上的头显也能朝前看)。完整列表见 `src/DevCommands.cs`。
性能相关：`prof`(CPU 采样剖析)、`gpu`(GPU 时间线)、`census`(场景普查)、`carpose`(固定车头朝向)、`nopause`(失焦不暂停)，
以及 A/B 开关 `physsync`、`comps`、`fullspeed`、`pin`、`perf rig`，用法见 `PERF.md`。插件自己的耗时：`ptime <秒>`(每个钩子
每帧毫秒和调用次数)。运行中改：`res <百分比>`(VR 分辨率)、`every <minimap|mirror|rain|parking> <N>`(辅助相机间隔)、
`trails <0|1>`、`trailinfo`(拖尾归属、点数、距离)、`interiorpath <forward|deferred>`(驾驶舱内饰相机渲染路径)、
`timescale 0`(冻住画面做可重复的渲染 A/B)。剖析时设环境变量
`CLOUDPUNKVR_SYMBOLS` 指向 Unity 符号文件 `UnityPlayer_Win64_il2cpp_x64.pdb` 所在目录(符号服务器 symbolserver.unity3d.com，
按 PDB 的 GUID 取，不入库)，引擎函数就有名字；`CLOUDPUNKVR_JOBWORKERS` 覆盖工作线程数配置。

注意：先关游戏再写 `cmd.txt`,否则还开着的实例会把它读走；在标题画面直接 `continue` 会留下选中的菜单按钮，之后第一次按 A
会重新弹出标题菜单，要测完整流程就用两次 `press Action1`。
测试通道把窗口固定成 1280x720 窗口化，Unity 退出时会把它写进 `HKCU\Software\ION LANDS\Cloudpunk` 的 `Screenmanager *` 几项
(Steam 原版也读这里)。测完改回游戏自己的 `WindowWidth`/`WindowHeight`、全屏窗口(`Fullscreen mode` = 1、`Use Native` = 1)。
平面模式下窗口失去焦点游戏会自己弹暂停菜单，无人值守的测试要剔除 `menu=True` 的结果。

Coo 相关：`nav [drive] [任务编号]`(像 World 那样规划到任务标记)、`lifts`(这一层的升降梯和每个任务标记在游戏里显示在哪)、
`navlift <x> <y> <z> [drive]`(当作那点附近的升降梯通往任务所在层)、`planbudget <毫秒>`、`carbox`(车的碰撞体对枢轴)、`above [米]`(车正上方所有层的碰撞体)、`space`(起点的自由空间洪泛和堵住周围格子的碰撞体)、
`holo <右> <上> <前> [缩放]`(驾驶舱全息位置)、`holoinfo`、`cockpitinfo`(仪表盘部件相对内饰相机的位置)、`view <off|firstperson|spectator>`、
`specshot <名>`(直播画面贴图)、`grab <名>`(Windows 合成后的游戏窗口，即 OBS 捕获到的，需环境变量 `CLOUDPUNKVR_GRAB` 指向
截窗口的脚本)、`walkinfo`(步行参考系)、`vrinput <输入> <值> [秒]`(假装按着某个 VR 键，走按键文件)、`posecam <名> <偏航> <距离> <高度>`
(从外面拍第一人称身体的姿势)、`armlog [帧]`、`armcfg`、`icons`(游戏的按键提示)、`hxlights`、`nativemap <文件>`、`unlesscar <命令>`。
从标题画面读档别用 `continue`(会留下打开的标题菜单，之后 `IsInMenu` 一直为真)，按两次 `press Action1`(任意键、继续游戏)。
测试默认静音(`AudioListener.volume` 每帧压到 0),要声音加 `-cloudpunkvr-audible`。

VR 相关：`shot <名> both|left|right` 截两只眼的眼睛贴图(窗口镜像只有一只眼)、`fakehands <侧向> <高度> <前向>|off` 假手柄、
`fakeheadpos <x> <y> <z>`(头离校准位置的假偏移，空头显不会动)、`fakestereo <瞳距> <0|1>`(假的眼距和 Quest 式不对称视野；
视图覆盖有效，投影覆盖在这个版本里不生效)、`testlight <前> <右> <范围> <强度> [hx]`/`testlight on|off|hx 0|1`/`testlightpos`
(带 Hx 体积光的测试灯和两眼应在的位置；在 Update 里算，没有头部姿态，真头显上不准，用 `hxmatrices` 的)、
`hxcapture <名> [vol|temporal]`(同一帧两只眼各自渲染完时 Hx 缓冲的拷贝)、`hxmatrices`(下一帧两只眼渲染前后 Hx 留在全局里的
矩阵、它的 `MatrixVP`,对照这只眼自己的，外加测试灯在这只眼里应在的 u/v)、`hxeyes <0|1>`(Hx 逐眼开关)、`hxhist <0|1>`
(右眼独立的时间滤波历史)、`glows`(车灯光晕数、按哪台相机摆、面片离灯最远多少)、
`savert <名> <hxtemporal|hxvolumetric|hxfullblur|hxdownblur|sky>`、`stereofx sky <0|1>`、`xrinfo`(立体模式和两眼矩阵)、
`eyelog <帧>`、`cams`、`lightinfo`、`atmoinfo`(环境光、Hx 全局参数、雨粒子)、`foginfo`、`fogglobals`、`fogcfg <距离倍数>
<密度倍数>`、`fogtest <off|Linear|Exponential|ExponentialSquared> [密度]`、`layerstats <近> <远>`(各图层可见渲染器数)、
`lodinfo`、`carinfo [次数]`(高度和本层上下限)、`altcfg`(高度提醒的位置)、`handcaltest <俯仰> <偏航> <横滚>`、`handmesh`、
`typeinfo <正则>`、`findmember <正则>`、`particles <半径>`、`findname <子串>`、`rain3d <0|1> [速度 长度 宽度 不透明度]`、
`drips <0|1>`、`hxset <密度> [mie 消光 环境光]`(所有 Hx 相机)、`padinfo <帧>`(手柄键、虚拟手柄、InControl 当前设备)、`setmember <路径|main> <组件> <属性> <值>`、`farshot <名> <抬高> <偏航> <俯仰>`(主相机连同雾和裁剪
从玩家上方往外拍一张，看能看多远)、`foginfo`(Unity 雾、远裁剪面、各图层裁剪距离)、`cams`(所有相机的目标、清屏方式)、
`setmember <路径|main> <组件类型> <属性> <值>`(改组件属性做 A/B,路径里的空格写成 `+`)、`povinfo`(Cinemachine 脑的更新时机和 POV 偏航轴设置)、
`turntrace <帧数>`(逐帧记主相机的游戏偏航、最终视角、眼睛世界位置、身体朝向)、
`body`(第一人称身体状态)、`glows`(车灯光晕数)、`vrfx <fog|dof|flare|glass> <0|1>`(画面修正 A/B)、`cfg <段> <键> <值>`(改插件设置)、
`layoutset <段.功能> <输入>`(像设置程序那样改按键文件)、`glass [属性 值]...`(车窗材质)、`tpcar`(站到车边，接 `press Action1` 上车)、
`closemenu`、`inspect <路径> [类型] [深度] [成员过滤]`(反射读组件字段)、`mats`、`matprops`、`find <类型>`、`ppdump`。路径里的空格写成 `%20`。

着色器：游戏着色器只有 DXBC 字节码，没有类型树。用 UnityPy 读出 Shader 对象，把子程序里 `DXBC` 起的那段交给
`d3dcompiler_47.dll` 的 `D3DDisassemble` 反汇编；常量缓冲区和贴图槽位在 `m_ParsedForm` 的 pass 里(`m_ConstantBuffers`、
`m_TextureParams`,名字查 `m_NameIndices`)。

没有真头显时用 SteamVR 空驱动：`steamvr.vrsettings` 里 `steamvr.forcedDriver = "null"`,
`drivers/null/resources/settings/default.vrsettings` 里 `enable = true`,测完改回。

## 实测

完整分析和数字在 `PERF.md`。概要：
- 平面 720p 驾驶舱，交付配置对比引擎默认：3.81 → 2.52 ms/帧，p95 5.29 → 3.58 ms，进程占用 3.05 → 2.13 核。
- SteamVR 空头显、每眼 1344x1440、90 Hz：主线程每帧 CPU 7.6 → 5.4 ms；GPU 每帧 2.83 → 2.0–2.15 ms(显卡高频状态，降频时约翻倍)。
- 第一版时测的：辅助相机降频 7.7 → 6.6 ms；关 GDOC 遮挡剔除反而 +2.3 ms；车库一带 12–15 ms 那次之后没有重测。
- 空驱动的帧间隔不准，VR 比较看每帧 CPU 和 GPU 时间；同一配置不同轮次能差 15–30%，只信同一局里的 A/B。

## 未完成

- 空头显两眼投影对称、瞳距为 0,左右眼视野不对称的问题只在 Quest 3 上量得出来(Hx 体积光的逐眼位置是在操作员的 Quest 3
  会话上量的缓冲，雾效和天空没在真机上量过)。
  空头显的头不动，转头晃动的修复(渲染前重读姿态)只能戴上确认；日志里的 `head:` 行记着两次读数差了多少。
  手型要在 Quest 上做一次手型校准再看对不对。
- 有些地方游戏不让车往上(车的 `currentFloorLevel` 升不上去，上面没有任何碰撞体):11-10 在中城区停车平台(-1075,57,-401)北边
  47.5 m 就是顶，规划器不知道，按自由空间规划的路线在那里往上爬时会卡住，倒车重规划后从南边进去。原因没查。
- 全屏体积雾没做：Hx 只在每盏灯的体积里算(全局密度 0.1、消光 0.05、环境光 0.5);全屏的需要一盏带阴影的方向光，阴影
  投射物会加大量绘制，而帧时间卡在渲染线程上。环境光(三色，天光偏暖、地平暗)保持游戏原样。
- 资料片 City of Ghosts(`City of Ghosts\` 里的另一个 exe)没装。
