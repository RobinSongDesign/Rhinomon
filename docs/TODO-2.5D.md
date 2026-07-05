# TODO：实现 2.5D 世界模式（交接文档）

**写给**：接手实现的 agent。读完本文档 + `PRD.md`（尤其 §7 性能硬指标、§12.3）+
`docs/CONTRACT.md` 即可开工，无需其他上下文。
**状态**：渲染 spike 已在真机 Rhino 8 验证通过（2026-07-05），可以放心在这套原语上盖楼。

---

## 1. 背景与已验证事实

Rhinomon 是 Rhino 8 视口桌宠（C# / net7.0-windows / RhinoCommon 8.x），v1 屏幕空间
模式已上真机跑通。v2 要加"伪透视 2.5D 世界模式"：宠物有绝对世界尺寸、生活在场景
里、随镜头远近缩放、被几何体遮挡、爬包围盒面。

**真机 spike（`src/Rhinomon/Spike25D.cs`，隐藏命令 RhinomonTest25D）已确认**：
1. `e.Display.DrawSprite(DisplayBitmap, Point3d, float size, sizeInWorldSpace:true)`
   在 `PostDrawObjects` 通道：随相机距离正确缩放 ✅
2. 同通道深度测试生效：前方几何体正确遮挡 sprite ✅
3. 图集预 4× 最近邻放大后像素清晰度可接受 ✅
4. sprite 自动 billboard（始终面向相机）✅

## 2. 现有架构速览（改哪些，别破坏什么）

| 文件 | 角色 | 本任务动它吗 |
|---|---|---|
| `RhinomonPlugin.cs` | PetSettings + PetSystem 装配器 + 插件类 | ✅ 设置、模式切换、装配 |
| `PetEngine.cs` | 屏幕空间状态机（v1 行为，勿改逻辑） | ✅ 仅抽接口 |
| `PetConduit.cs` | conduit：DrawForeground 画图 + 帧耗时采样 | ✅ 加 PostDrawObjects 世界绘制 |
| `ActivityMonitor.cs` | 事件计数/空闲判定/锚对象监视 | ✅ 仅 Engine 字段类型 |
| `ClickInterceptor.cs` | 命中 `Conduit.LastPetRect` | ❌ 不动（矩形由 conduit 供给） |
| `PerchScanner.cs` | bbox 扫描选爬高目标 | ✅ 加世界模式变体方法 |
| `PerfGovernor.cs` | 定时器/避让/降级 | ❌ 不动 |
| `SpriteAtlas.cs` | 帧切片/预放大/DisplayBitmap 缓存 | ❌ 基本不动 |
| `RhinomonCommand.cs` / `RhinomonPanel.cs` | 命令 + Eto 面板（双向镜像） | ✅ 加 Mode/WorldSize |
| `Spike25D.cs` | 渲染 spike | ✅ 最后一步删除 |

**不可破坏的红线**（PRD §7）：事件回调 O(1) 零堆分配；conduit 单帧 <0.3ms；命令运行
期间插件主动重绘为 0（`PetSystem.RedrawActiveView` 是唯一重绘入口，带闸门）；禁止
渲染网格射线求交/closest point——**导航拓扑只允许用缓存 BoundingBox 和世界 XY 地面**。
每个回调外层 try/catch + `Guard.Fail` 计数（连续 3 次自动禁用），照抄现有写法。

## 3. 设计决策（已拍板，不要重新发明）

- **双引擎并列**，不改造 PetEngine：抽 `IPetEngine` 接口，新建 `WorldPetEngine`。
  模式切换 = PetSystem Disable + Enable（简单可靠，宠物重新出生可接受）。
- **导航拓扑**：世界 XY 平面 z=0 当地板；目标对象的世界对齐 bbox：侧面=墙、顶面
  =平台。非盒状物体的悬浮误差接受（与 v1 一致的哲学）。
- **世界模式图集固定 scale=4**（spike 验证的清晰度），忽略用户 Scale 与 DPI 乘数；
  用户 Scale 只作用于屏幕模式，世界模式的大小由 WorldSize（模型单位）决定。
- **表情图标仍画屏幕空间**（DrawForeground，投影点上方悬浮）：宠物被完全遮挡时
  表情仍可见，兼作"宠物在哪"指示——这是特性不是 bug。
- **点击命中**：conduit 每帧把世界位置投影成屏幕矩形喂给 `LastPetRect`，
  ClickInterceptor 完全不用改。
- **强打断（命令/编辑开始）**：世界模式下宠物本来就不挡视线，不需要"回角落"；
  规则简化为：立即苏醒，若在爬升中转坠落，落地后原地待机。
- **视图无关性**：宠物是世界锚定的，视图旋转/切换不移动它（这正是卖点）。
  可发现性兜底：若连续 >60s 不在激活视口可见范围内，下一次进入 Walk 状态时
  把漫步目标改选在当前相机目标点附近的地面（自然"走回视野"，不瞬移）。

## 4. 实施步骤（每步可编译、单独提交）

### Step 1 — 接口抽取（纯重构，行为零变化）
新建 `IPetEngine.cs`：
```csharp
internal interface IPetEngine
{
    int DesiredFps { get; }
    void ResetToHome();
    void OnStrongInterrupt();
    void OnViewportChanged();
    void OnPetted();
    void React(PetReaction reaction);
    bool Tick(double dtMs);
    // Screen-space payload: real sprite in Screen mode; emote-only (sprite=null,
    // petRect still filled for click hit-testing) in World mode.
    bool TryGetScreenDrawInfo(RhinoViewport vp, out DisplayBitmap sprite,
        out System.Drawing.Rectangle petRect, out DisplayBitmap emote,
        out System.Drawing.Rectangle emoteRect);
    // World-space payload: false in Screen mode.
    bool TryGetWorldDrawInfo(out DisplayBitmap sprite, out Rhino.Geometry.Point3d position,
        out float worldSize);
}
```
`PetEngine : IPetEngine`（TryGetDrawInfo 改名为 TryGetScreenDrawInfo，
TryGetWorldDrawInfo 返回 false）。PetSystem/ActivityMonitor/ClickInterceptor/
PetConduit 里的 `PetEngine` 字段全部改为 `IPetEngine`。构建 → 提交。

### Step 2 — 设置与 UI 面
- `PetSettings` 加：`PetDisplayMode Mode`（enum Screen=0/World=1，默认 Screen）、
  `double WorldSize`（默认 0=自动）。Load/Save 用 GetEnumValue/GetDouble。
- `RhinomonCommand`：加 `Mode` 选项列表（Screen/World）；选 World 后追加
  `WorldSize` 数字选项（`AddOptionDouble` 或数字直输均可）。改动后走
  Disable+Enable 重启系统。**注意坑：Rhino 命令行选项 token 必须字母开头**
  （历史 bug：纯数字列表值是死的，见 git log 87bdd04）。
- `RhinomonPanel`：加 Mode 下拉 + WorldSize `NumericStepper`（Mode=World 才
  Enabled），照抄现有控件的 `_updating` 防回环模式。
- 此步 World 模式仍装配 PetEngine（行为暂等同 Screen），保证可编译可提交。

### Step 3 — WorldPetEngine 最小可用（地面生活）
新建 `WorldPetEngine.cs : IPetEngine`。可复用 PetEngine 的常量与结构
（空闲阶梯 `IdleThresholdsMs`、表情/心情逻辑、`AdvanceFrame` 模式——复制即可，
两个引擎共 <700 行时不值得抽基类）。

- 出生：`_pos` = 相机目标点投到 z=0 地面（`vp.CameraTarget`，z 置 0）。
- WorldSize 解析：设置 >0 用设置值；=0 自动 = `doc.Objects.BoundingBoxVisible`
  （或遍历可见对象 bbox 并集，上限 4096 个）对角线长 × 2%，clamp 到
  [0.5, 1000] 模型单位；空文档用相机目标距离 × 5%。自动值仅会话内，不回写设置。
- Tick：Idle/Walk 与 PetEngine 同构，只是坐标换成世界地面：漫步目标 =
  相机目标点半径 R（= 20×worldSize）内随机地面点。速度 = 1.6×worldSize/s（沿用
  bodies-per-sec 常量）。
- 朝向：`facingLeft` = 世界速度与相机右向量点积 <0；相机右向量 =
  `vp.CameraX`（每 Tick 取一次，别在绘制里算）。
- `TryGetWorldDrawInfo`：sprite = 当前帧位图，position = `_pos + Vector3d.ZAxis * worldSize * 0.5`
  （DrawSprite 以中心定位，脚底在 _pos），worldSize 转 float。
- `TryGetScreenDrawInfo`：投影 `_pos` → 屏幕矩形：像素高 =
  `vp.GetWorldToScreenScale(_pos) * worldSize`（**该 API 签名需构建验证**，
  失败则用两点投影法：投影 _pos 与 _pos+ZAxis*worldSize 取屏幕距离）；填
  petRect（点击用）与表情位置，sprite 输出 null（宠物本体不在此通道画）。
- PetConduit：`PostDrawObjects` 里 `TryGetWorldDrawInfo` 成立就 DrawSprite
  （照抄 Spike25D 的调用与 try/catch）；DrawForeground 维持现有逻辑（世界模式下
  它拿到 sprite=null 只画表情 + 更新 LastPetRect——注意 sprite 为 null 时也要
  更新 LastPetRect 和 LastPetViewportId，当前实现只在 sprite 非空时更新，要调整）。
- 帧耗时采样/DpiScale 通知逻辑保持不变。
构建 → 提交。

### Step 4 — 爬高/栖息/睡觉/坠落
- `PerchScanner` 加 `TryFindWorldPerch(RhinoDoc doc, Point3d petPos, double worldSize,
  out Guid objectId, out BoundingBox bbox)`：遍历可见对象（沿用 >5000 跳过、单次
  上限 4096、每空闲 episode 一次的规则），条件：`bbox.Min.Z <= worldSize*0.25`
  （从地面够得着）且 `bbox.Max.Z - bbox.Min.Z >= worldSize`（值得爬）且水平距离
  < 40×worldSize；取最近+次近随机二选一（沿用现有风格）。
- 状态流：Idle →(空闲够 30s)→ WalkToPerch：目标 = bbox footprint 最近边上的点，
  向外偏移 worldSize/2 → Climb：沿该垂直面上升（水平锁定，z += 1.1×worldSize/s）
  至 `bbox.Max.Z` → 踏上顶面（向内收 worldSize/2）→ Perched：在顶面矩形内小范围
  漫步/待机 → Sleep。
- 锚对象监视：沿用 `Monitor.WatchObject(objectId)` / `ConsumeAnchorDeleted()`。
  对象被删或 Undo 消失 → Fall：z 以 14×worldSize/s 降到 0，落地转 Idle，
  Exclaim 表情 1.2s（照抄 StartFall 模式）。
- OnStrongInterrupt：Sleep/Perched → 苏醒待机（留在原地）；Climb → Fall。
构建 → 提交。

### Step 5 — 收尾
- 可发现性兜底（§3 最后一条）。
- 删除 `Spike25D.cs`（其注释已声明 ships 后删除）。
- `PRD.md` §12.3 标注"已实现，真机验证通过 spike"。
- `README.md` 加 World mode 一段（Mode/WorldSize 用法，两句话即可）。
- 全量 `dotnet build -c Release -warnaserror` 零错零警 → 提交。

## 5. 构建与验证约束

- **本机无法运行 Rhino 做行为测试**，唯一可执行验证是构建。命令：
  `dotnet build src/Rhinomon/Rhinomon.csproj -c Release -warnaserror -v minimal`
- **用户的 Rhino 经常开着并锁定 `bin/Release/Rhinomon.rhp`**：构建报 MSB3026/3027
  文件锁定时，用 `-o <临时目录>` 绕开验证编译，不要试图杀 Rhino 进程。
- 不确定的 RhinoCommon API 签名：先写、构建报错再查 NuGet 包内 `RhinoCommon.xml`
  或 developer.rhino3d.com，不要猜（本仓库全部 API 都经过这种验证）。
- 代码风格：注释英文、只写代码表达不了的约束；每个 conduit/事件回调
  try/catch + `Guard.Fail`；不 push；提交信息英文、结尾
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`。

## 6. 完成后给用户的真机验收清单（写进最终汇报）

1. `Rhinomon Mode=World`：宠物以正确世界尺寸站在相机目标附近地面；
2. 地面漫步自然；空闲 30s 走向一个盒子、贴面爬上顶部；2min 睡着；
3. 转视图宠物在世界里不动；被前方物体正确遮挡，遮挡时表情图标仍可见；
4. 点击宠物（含远/近不同距离）触发被摸反应，无误选背后几何；
5. 删除它站着的物体 → 坠落回地面；
6. `TestMaxSpeed` 开关宠物帧率差 <2%（PRD §7 验收不回退）；
7. Mode=Screen 切回后 v1 行为完好。
