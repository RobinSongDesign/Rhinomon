# Rhinomon 开发契约（跨工作流共享，改动需同步更新所有引用方）

## 目录布局

```
d:\Dev\Rhinomon\
├── PRD.md                  产品需求（唯一需求来源）
├── docs\CONTRACT.md        本文件
├── src\Rhinomon\           C# 插件（SDK-style csproj）
├── assets\                 生成的 PNG 精灵表 + atlas.json（由 tools/spritegen 产出，禁止手改）
├── tools\spritegen\        Python 像素图生成器（Pillow）
├── packaging\              Yak 打包（manifest.yml 等）
└── README.md
```

## 精灵表规格（硬契约：生成器按此输出，C# 按此硬编码）

- 每只宠物一张 PNG：`assets/clawd.png`、`assets/crab.png`、`assets/nova.png`
- 网格 32×32 px/格，8 列；**行=动画，列=帧**（帧从第 0 列起左对齐，行内多余格全透明）
- 图片总尺寸固定 **256 宽 × 224 高**（8 列 × 7 行）
- 行分配、帧数、播放帧率：

| row | 动画      | 帧数 | fps | 备注 |
|-----|-----------|------|-----|------|
| 0   | idle      | 4    | 4   | 呼吸/眨眼循环 |
| 1   | walk      | 4    | 6   | 循环 |
| 2   | climb     | 4    | 6   | 循环 |
| 3   | sleep     | 2    | 1   | 循环 |
| 4   | petted    | 4    | 6   | 单次播放后回 idle |
| 5   | surprised | 2    | 6   | 单次 |
| 6   | happy     | 4    | 6   | 单次 |

- 朝向：所有帧默认**朝右**；C# 侧向左移动时水平翻转绘制
- 表情图标：`assets/emotes.png`，16×16 px/格，1 行 5 格，顺序固定
  `[heart, exclaim, question, zzz, sparkle]`
- `assets/atlas.json`：生成器输出上述规格的机器可读描述（供校验；C# 可直接硬编码本契约，
  不强制运行时读 JSON）
- 视觉规范：透明背景、硬边像素（无抗锯齿）、PNG-32；
  Clawd 主色 = Anthropic 橙 `#D97757`；Crab = 偏红橙、有蟹钳、行走帧为横移步态；
  Nova = 深蓝夜色系（`#1E3A5F` 一族）+ 少量星光点缀，暗色背景下可读

## C# 侧约定

- TargetFramework：`net7.0-windows`，`UseWindowsForms=true`
- 依赖：NuGet `RhinoCommon` 8.x（`ExcludeAssets=runtime`），Rhino 8 Windows only
- 输出改名 `.rhp`（`<TargetExt>.rhp</TargetExt>`）
- 资产嵌入：csproj 用通配符 `..\..\assets\*.png` + `..\..\assets\atlas.json` 作
  EmbeddedResource（带 Link 名 `Assets/<file>`）；**assets 目录为空时也必须能编译**
- SpriteAtlas 加载失败/资源缺失时回退到程序生成的占位纯色方块（洋红 32×32），
  保证无资产也能跑通全部逻辑
- 加载时把精灵表切成单帧位图，并按用户 Scale(1/2/3) 用最近邻放大后缓存为
  `DisplayBitmap`（DrawSprite 直绘可能线性滤波糊掉像素，禁止运行时缩放）
- 插件 GUID 一经生成不得更改
- 所有 conduit 回调与事件处理外层 try/catch；同一处连续 3 次异常 → 自动禁用宠物并
  RhinoApp.WriteLine 提示

## 性能红线（验收见 PRD §7）

事件回调 O(1) 零堆分配；conduit 单帧 <0.3ms；命令运行期间插件产生的主动重绘为 0。
