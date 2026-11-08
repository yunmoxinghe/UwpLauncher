# 文件项样式审计（对齐 files-community/Files）

## 0. 基线与方法

| 项 | 值 |
| --- | --- |
| 上游仓库 | `files-community/Files` |
| 取用 commit | `ee5ba5648541880c98a56436d65d1f9d108332b1`（v4.3.3，2026-11-08，默认分支 HEAD） |
| 说明 | 任务里给的 `3836c9c` 在上游查不到（GitHub API `No commit found`），改用可解析的 HEAD |
| 颜色/字号数值来源 | Files 只引用 WinUI 的 `ThemeResource` 键名，数值取自 WinUI 3：`microsoft/microsoft-ui-xaml` 分支 `winui3/release/1.7-stable` 的 `src/controls/dev/CommonStyles/GridViewItem_themeresources.xaml`、`Common_themeresources_any.xaml` |
| 我们的运行时 | UWP XAML Islands（Windows.UI.Xaml，非 WinUI 3），个别键值以本机 SDK `UAP\10.0.26100.0\Generic\generic.xaml` 校对 |
| 本文档约定 | 「行号」均指该文件在 `ee5ba56` 下的行号；`generic.xaml` 指本机 10.0.26100 SDK 文件 |

上游文件路径（下文用简称）：

- `Layout` = `src/Files.App/Helpers/Layout/LayoutSizeKindHelper.cs`
- `GridPage` = `src/Files.App/Views/Layouts/GridLayoutPage.xaml`
- `GridPage.cs` = `src/Files.App/Views/Layouts/GridLayoutPage.xaml.cs`
- `App.xaml` = `src/Files.App/App.xaml`
- `TextStyles` = `src/Files.App/Styles/TextBlockStyles.xaml`

## 1. 上游「文件项展示」资源全量清单

### 1.1 槽位 / 容器

| # | 资源或属性 | 上游取值 | 出处 |
| --- | --- | --- | --- |
| 1 | GridView 项宽密度档 | 80 / 100 / 120 / 140 / 160 / **180** / 200 / 220 / 240 / 260 / 280 / 300 | `Layout:83-114` |
| 2 | 项宽绑定 | `ItemWidthGridView`（由密度档计算） | `GridPage:87,101-102,116-117`；`GridPage.cs:87-88` |
| 3 | 项高 | 不设固定值：图标方格 = 项宽的正方形（180）+ 名称区 60 + 文字区下边距 8 | `GridPage:99-159,161-210` |
| 4 | ItemsWrapGrid | Grid 布局 `Orientation=Horizontal`；列表布局 `Vertical`；不显式设 ItemWidth/ItemHeight | `GridPage:28-59` |
| 5 | GridView 内边距 | `Padding="8"` | `GridPage:990` |
| 6 | 容器最小高 | `MinHeight="1"` | `GridPage:763,832,903` |
| 7 | 容器圆角 | 列表/紧凑容器 = `GridViewItemCornerRadius`（4）；常规容器 = **8** | `GridPage:791,861`；`GridPage:915` |
| 8 | 容器过渡 | `ItemContainerTransitions="{x:Null}"` | `GridPage:1009` |
| 9 | 滚动占位 | `ShowsScrollingPlaceholders="True"` | `GridPage:1016` |
| 10 | 焦点视觉 | `FocusVisualPrimaryThickness="0"`、`SecondaryThickness="0"` | `GridPage:1005-1006` |

### 1.2 状态（背景 / 描边 / 透明度）

| # | 资源或属性 | 上游取值 | 出处 |
| --- | --- | --- | --- |
| 11 | 悬停背景 | `App.Theme.CardBackgroundFillColorTertiaryBrush` = Dark `#12FFFFFF` | `GridPage:927`；`App.xaml:154` |
| 12 | 悬停描边 | `GridViewItemPointerOverBorderBrush` → `ControlStrokeColorOnAccentTertiaryBrush` | `GridPage:928`；`GridViewItem_themeresources.xaml:26` |
| 13 | 按下背景 | 同悬停（`App.Theme.CardBackgroundFillColorTertiaryBrush`） | `GridPage:930` |
| 14 | 选中背景 | `CardBackgroundFillColorDefault` = Dark `#0DFFFFFF` | `GridPage:932`；`Common_themeresources_any.xaml:55` |
| 15 | 选中描边 | `GridViewItemSelectedBorderBrush` → `AccentFillColorDefaultBrush`（系统强调色） | `GridPage:933`；`GridViewItem_themeresources.xaml:27,113` |
| 16 | 选中描边粗细 | `GridViewItemSelectedBorderThickness` = `2` | `GridPage:934`；`GridViewItem_themeresources.xaml:25,111` |
| 17 | 选中内描边 | `GridViewItemSelectedInnerBorderBrush` → `ControlSolidFillColorDefaultBrush` | `GridPage:938`；`GridViewItem_themeresources.xaml:31` |
| 18 | 选中+悬停 | 背景 = 三级卡色；描边 = `GridViewItemSelectedPointerOverBorderBrush` | `GridPage:939-940` |
| 19 | 选中+按下 | 背景 = `GridViewItemBackgroundSelectedPressed`；描边 = `GridViewItemSelectedPressedBorderBrush` | `GridPage:941-942` |
| 20 | 选中+禁用 | 背景 `GridViewItemBackgroundSelectedDisabled`；描边 `GridViewItemSelectedDisabledBorderBrush` | `GridPage:935-936` |
| 21 | 禁用透明度 | `ListViewItemDisabledThemeOpacity` = `0.3` | `GridPage:916`；`ListViewItem_themeresources.xaml:6` |
| 22 | 拖拽视觉 | `DragBackground` / `DragForeground` / `DragOpacity` = `0.80` | `GridPage:917-919`；`ListViewItem_themeresources.xaml:7` |
| 23 | 拖拽占位 | `GridViewItemPlaceholderBackground` | `GridPage:926` |
| 24 | 重排提示偏移 | `GridViewItemReorderHintThemeOffset` = `16.0` | `GridPage:931`；`generic.xaml:64` |
| 25 | 焦点框 | `GridViewItemFocusBorderBrush` → `FocusStrokeColorOuterBrush` = Dark `#FFFFFF` | `GridPage:920`；`GridViewItem_themeresources.xaml:16`；`Common_themeresources_any.xaml:53` |
| 26 | 勾选模式 | `CheckMode` = `Overlay` | `GridPage:911`；`generic.xaml:1028` |
| 27 | 勾选框圆角 | `GridViewItemCheckBoxCornerRadius` = `3` | `GridViewItem_themeresources.xaml:24` |
| 28 | 容器最小尺寸 | `GridViewItemMinWidth` / `MinHeight` = `44` | `generic.xaml:122-123` |

### 1.3 数据模板（图标 / 文字）

| # | 元素 | 上游取值 | 出处 |
| --- | --- | --- | --- |
| 29 | 图标方格 | 宽高都等于项宽（正方形），`Opacity` 绑定项不透明度 | `GridPage:99-104` |
| 30 | 图标内边距 | `ContentPresenter Margin="12"`，居中 | `GridPage:106-110` |
| 31 | 缩略图圆角 | `GridViewThumbnailCornerRadius` = `2` | `GridPage:113`；`App.xaml:23` |
| 32 | 缩略图填充 | `Stretch="Uniform"`，Max = 项宽 | `GridPage:114-119` |
| 33 | 主图标尺寸档（网格） | ≤Small = 96；≤Large = 128；其余 = 256 | `Layout:47-50` |
| 34 | 角标（覆盖图标） | 32×32，`Margin="16"`，左下 | `GridPage:122-131` |
| 35 | 快捷方式角标 | 16×16，`Margin="14"`，图形 12 | `GridPage:133-146` |
| 36 | 提权盾牌 | 16×16，`Margin="14"`，右下 | `GridPage:148-158` |
| 37 | 文字区外边距 | `Margin="4,0,4,8"`，水平居中 | `GridPage:161-166` |
| 38 | 名称 | `Height=60`、`TextAlignment=Center`、`TextTrimming=CharacterEllipsis`、`TextWrapping=Wrap`、未设字号/字重 | `GridPage:198-208` |
| 39 | 名称字号 | 继承 `BodyTextBlockFontSize` = `14`（WinUI 默认 `ControlContentThemeFontSize` = 14） | `TextStyles:6`；`generic.xaml:40` |
| 40 | 副信息（列表视图） | `App.Theme.CaptionTextBlockStyle` → `CaptionTextBlockFontSize` = `12` | `TextStyles:30-36` |
| 41 | 主/次文字色 | `TextFillColorPrimary` = `#FFFFFF`；`TextFillColorSecondary` = `#C5FFFFFF`（Dark） | `Common_themeresources_any.xaml:5-6` |
| 42 | 同步状态图标 | 16×16，`Margin="0,0,4,0"` | `GridPage:181-185` |
| 43 | 文件标签圆点 | 12×12，`Margin="0,0,4,0"` | `GridPage:188-196` |
| 44 | 多选勾选框 | 32×32，`Margin=6`，`Padding=0`，默认 `Opacity=0` | `GridPage:212-227` |

### 1.4 动画

| # | 项 | 上游取值 | 出处 |
| --- | --- | --- | --- |
| 45 | 容器状态切换 | 离散切换，`KeyTime="0"`，无时长/缓动 | `generic.xaml` 的 GridViewItem 模板（`ListViewItemPresenter`） |
| 46 | 项容器过渡 | 无（`ItemContainerTransitions="{x:Null}"`） | `GridPage:1009` |
| 47 | 重排动画 | 350 ms | `GridPage:993` |

## 2. 逐键对照（本次修改后）

状态说明：**一致** = 取值与上游相同；**偏离** = 有上游对应物但取值不同；**缺失** = 上游有、我们没有；**有意不照搬** = 架构/产品形态不适用。

### 2.1 已对齐（本次修改）

| 资源键 | Files 取值（出处） | 我们的取值 | 状态 |
| --- | --- | --- | --- |
| 名称字号 | 14（`TextStyles:6`、`generic.xaml:40`） | `ItemNameFontSize` 13 → **14** | 一致 |
| 名称字重 | 未设（Normal）（`GridPage:198-208`） | `SemiBold` → **Normal** | 一致 |
| 名称行高 | Body 档 20 | `ItemNameLineHeight` = **20** | 一致 |
| 主文字色 | `#FFFFFF`（`Common:5`） | `#FFE6EEF9` → **`#FFFFFFFF`** | 一致 |
| 次文字色 | `#C5FFFFFF`（`Common:6`） | `#FF9FB0C4` → **`#C5FFFFFF`** | 一致 |
| 悬停底色 | `#12FFFFFF`（`App.xaml:154`、`GridPage:927`） | `#24FFFFFF` → **`#12FFFFFF`** | 一致 |
| 按下底色 | `#12FFFFFF`（`GridPage:930`） | `#33FFFFFF` → **`#12FFFFFF`** | 一致 |
| 选中底色 | `#0DFFFFFF`（`Common:55`、`GridPage:932`） | 强调色叠层 `#2E4CC2FF` → **`#0DFFFFFF`** | 一致 |
| 选中描边色 | 系统强调色（`GridViewItem_themeresources:27,113`） | 无 → **`{ThemeResource SystemAccentColor}`** | 一致 |
| 选中描边粗细 | 2（同文件 `:25,111`） | 无 → **2** | 一致 |
| 文字区边距 | `4,0,4,8`（`GridPage:164`） | `8,6` → **`4,6,4,8`** | 一致（上/下沿用瓦片高度） |
| 项内容宽 | = 槽宽 180（`GridPage:87`） | 172 → **180** | 一致 |
| 槽宽 | 180（`Layout:97-98`，Six 档） | 180 | 一致 |
| 图标与文字对齐 | 均居中（`GridPage:106-110,198-208`） | 居中 | 一致 |
| 名称省略 | Wrap + CharacterEllipsis（`GridPage:198-208`） | Wrap + MaxLines 2 + CharacterEllipsis | 一致 |
| 容器最小高 | 1（`GridPage:763`） | 0 → **1** | 一致 |
| 容器过渡 | 无（`GridPage:1009`） | `ItemContainerTransitions="{x:Null}"` | 一致 |
| 状态切换动画 | 即时、无缓动（`generic.xaml` 模板） | 自绘即时切换 | 一致 |
| 空闲底色 | `SubtleFillColorTransparent`（透明） | `#00FFFFFF` | 一致 |
| 网格无滚动 | 上游允许滚动，我们按可用区域分页 | 滚动/滚动条全 Disabled | 产品形态 |
| 页内边距 | 8（`GridPage:990`） | 0 → **8**（列行计算同步扣减 2×8） | 一致 |
| 副信息行高 | Caption 档 16 | 未设 → **16** | 一致 |
| 图标与文字间距 | 0：图标方格（180）与文字区紧邻，`Margin=12` 是图标方格内边距（`GridPage:106-110`） | 0（图标行 32 → 名称行紧邻） | 一致 |

### 2.2 偏离（有上游对应物，取值不同）

| 资源键 | Files 取值（出处） | 我们的取值 | 理由 |
| --- | --- | --- | --- |
| 项容器圆角 | 常规容器 8（`GridPage:915`） | 4 | 用户明确要求「悬停底色 4 px 圆角」；Win11 默认 `GridViewItemCornerRadius` 也是 4（`GridViewItem_themeresources:23`） |
| 项槽高 | 约 240–248（180 方格 + 60 名称区，`GridPage:99-210`） | 104 | 桌面嵌入形态：一屏要放 6 行 × 7 列；用户要求分页而非长列表 |
| 主图标尺寸 | 96 / 128 / 256（`Layout:47-50`） | 32（字形 20） | 图标解码固定 `IconSizePx=32` 且带 LRU 缓存（`FileViewPage.xaml.cs`），改 96 会连带改解码尺寸、缓存上限与槽高；桌面瓦片用 32 更接近「叠加角标」规格（`GridPage:124-125`） |
| 名称区高度 | 60（3 行） | 40（2 行） | 用户明确要求「名称两行省略」 |
| 名称字号语义 | 继承 WinUI Body 档 | 固定 14 | 我们不在页面里引用 WinUI 类型梯度，值等同 |
| 容器模板 | `ListViewItemPresenter` + VSM（`GridPage:766-820`） | 自定义 `Grid` + 自绘状态 | WinUI 2 在嵌入桌面/非激活窗口下不触发 PointerOver/Selected 视觉状态（已实测），只能自绘 |

### 2.3 缺失 / 有意不照搬

| 项 | Files 取值（出处） | 状态 | 理由 |
| --- | --- | --- | --- |
| 悬停描边 | `GridViewItemPointerOverBorderBrush`（`GridPage:928`） | 有意不照搬 | 透明叠层上该描边几乎不可见，且悬停已由底色表达；保持「无描边」 |
| 选中+悬停 / 选中+按下 独立底色 | `GridPage:939-942` | 缺失 | 选中优先级高于悬停/按下，两态叠加未实现（视觉差异极小） |
| 选中+禁用 | `GridPage:935-936` | 有意不照搬 | 无禁用项概念 |
| 禁用透明度 | `0.3`（`ListViewItem_themeresources:6`） | 有意不照搬 | 无禁用项 |
| 拖拽背景/前景/透明度 | `0.80`（`GridPage:917-919`） | 有意不照搬 | 不支持拖放（桌面层拖放未实现） |
| 拖拽占位 | `GridViewItemPlaceholderBackground`（`GridPage:926`） | 有意不照搬 | 容器复用不显示占位 |
| 重排提示偏移 | 16.0（`generic.xaml:64`） | 有意不照搬 | 不支持重排 |
| 重排动画 | 350 ms（`GridPage:993`） | 有意不照搬 | 同上 |
| 焦点视觉 | `FocusVisual*=0`、`FocusBorderBrush`（`GridPage:1005-1006,920`） | 已对齐 | 文件项层显式写三条 0/False，取值同上游（见 2.4） |
| 多选勾选框 | 32×32 / Margin 6 / Overlay（`GridPage:212-227,911`） | 有意不照搬 | 单选（`SelectionMode=Single`） |
| 勾选框圆角 | 3（`GridViewItem_themeresources:24`） | 有意不照搬 | 无勾选框 |
| 覆盖图标角标 | 32×32 / Margin 16（`GridPage:122-131`） | 缺失 | 未解析 Shell 覆盖图标 |
| 快捷方式角标 | 16×16 / Margin 14（`GridPage:133-146`） | 缺失 | 未区分 .lnk |
| 提权盾牌 | 16×16 / Margin 14（`GridPage:148-158`） | 缺失 | 未解析提权标记 |
| 图标内边距 12 | `GridPage:108` | 有意不照搬 | 32 px 图标 + 104 槽高放不下 12 px 四边距（32+24+40+16+14 > 104） |
| 同步状态图标 | 16×16（`GridPage:181-185`） | 有意不照搬 | 无云盘同步 |
| 文件标签圆点 | 12×12（`GridPage:188-196`） | 有意不照搬 | 无标签系统 |
| 副信息行（类型/大小） | 上游网格模板没有 | 自有扩展 | 用户要求保留大小信息，放在副信息行 |
| 列表/紧凑容器样式 | `LocalListItemContainerStyle`、`LocalCompactListItemContainerStyle`（`GridPage:757-893`） | 有意不照搬 | 只有网格布局，无 List/Cards |
| 分组头样式 | `GridViewHeaderItem`（`GridPage:668-754`） | 有意不照搬 | 不分组 |

## 2.4 上一轮「有意不照搬」的逐项复核（跟 / 不跟）

| 项 | 上游 | 结论 | 依据 |
| --- | --- | --- | --- |
| 页边距 8 px | `GridPage:990` | **跟（已实现）** | 纯实现成本：`GridView.Padding=8`，同时把列行计算改为 `floor((宽-16)/180) × floor((高-16)/104)`，1280×672 DIP 下仍是 7×6=42 项/页，不会裁切 |
| 图标与文字间距 | 0（`GridPage:106-110` 的 12 px 是图标方格内边距） | **跟（已实现）** | 图标行 32 与名称行紧邻，无额外间距；不再额外加 4–8 px 自造间距 |
| 副信息行字号/行高/色值 | 12 / 16 / `#C5FFFFFF` | **跟（已实现）** | 保留副信息属于产品扩展，但字号/行高/色值统一按 Caption 档 |
| 按下与悬停过渡时长 | 0 ms（`ObjectAnimationUsingKeyFrames KeyTime="0"`） | **跟（已一致）** | 自绘状态切换也是即时，不引入缓动；不需要新增时长资源 |
| 焦点视觉 | `FocusVisual*=0`（`GridPage:1005-1006`） | **跟（已实现）** | 上游本身就是「厚度 0、不画焦点框」；文件项层显式写 `UseSystemFocusVisuals=False` + `FocusVisualPrimary/SecondaryThickness=0`，取值与上游一致，也契合 `WS_EX_NOACTIVATE` 不抢焦点 |
| 禁用态 | `ListViewItemDisabledThemeOpacity=0.3`（`ListViewItem_themeresources:6`） | **不跟** | 没有“禁用文件项”概念（无权限项仍可选中/进入提示），实现出来不可达 |
| 槽高 104 vs 240–248 | 图标方格 180 + 名称 60（`GridPage:99-210`） | **不跟** | 不是实现成本，是产品冲突：240 槽高在 1280×672 DIP 只有 2 行 × 7 列 = 14 项/页（现 42 项）；桌面层要求一屏尽量多放，且会连带把图标放大到 96+ |
| 图标 32 vs 96/128/256 | `Layout:47-50` | **不跟** | 与上一条同源：96 px 图标要求槽高 ≥ 140，每页降到 24 项以内；且 `IconSizePx=32` 是全链路约定（Shell 缩略图解码、`MaxCachedIcons=160` 的 LRU 约 0.65 MB，改 96 后约 5.9 MB） |
| 名称两行 40 vs 60 | `GridPage:198-208` | **不跟** | 用户明确要求「名称两行省略」；60 会让槽高再 +20、每页行数从 6 降到 5 |
| 多选 | `SelectionMode` + 勾选框（`GridPage:212-227`） | **不跟** | 桌面双击直接进入目录，多选没有后续动作（无批量删除/移动）；勾选框还会盖住图标 |
| 拖拽 | `GridPage:917-919` | **不跟** | 桌面层没有 OLE 拖放源/目标实现，也没有“拖到资源管理器”的落点语义 |
| 重排 | `GridPage:993` 350 ms | **不跟** | 没有排序/手动排列功能，重排不可达 |
| 角标（覆盖/快捷方式/盾牌） | `GridPage:122-158` | **不跟** | 需要 `IShellItemImageFactory` 的覆盖图标 + `.lnk` 解析 + 提权标记三套 Shell 能力；与“桌面图标”观感无关，收益低 |

## 3. 本次落到文件项层的改动

`src/WinUI2Demo/ThemeResources.xaml`

1. 文字色改 `TextFillColorPrimary/Secondary`；悬停/按下 `#12FFFFFF`；选中改「`#0DFFFFFF` + 2px 系统强调色描边」。
2. 名称 13 → 14、SemiBold → Normal、行高 20、两行；副信息 11 → 12、行高 16。
3. 文字区内边距改 `4,6,4,8`；内容宽 172 → 180；容器最小高 0 → 1。
4. 新增 `ItemSelectedBorderBrush`、`ItemSelectedBorderThickness`、`ItemIdleBorderBrush`、`ItemNameLineHeight`、`ItemMetaLineHeight`、`ItemPagePadding`、`ItemPagePaddingSize`。

`src/WinUI2Demo/FileViewPage.xaml`

5. 条目根 `Border` 增加 `BorderBrush`/`BorderThickness` 承接选中描边。
6. `GridView.Padding` 改 `8`（引用 `ItemPagePadding`）。

`src/WinUI2Demo/FileViewPage.xaml.cs`

7. `ApplySurfaceVisual` 在选中时写描边与 2px 粗细，切走时清零（`GridPage:932-934` 同语义）。
8. `UpdatePageMetrics` 先扣 `2 × ItemPagePaddingSize` 再算列行，避免末列/末行被 Padding 裁切。
9. `FileItemContainerStyle` 增加焦点视觉三条：`UseSystemFocusVisuals=False`、`FocusVisualPrimaryThickness=0`、`FocusVisualSecondaryThickness=0`（对齐 `GridPage:1005-1006`）。

## 4. 未覆盖与限制

- 上游 `3836c9c` 不可解析，本审计无法与该 SHA 对照；若必须锁该 SHA，请提供完整 40 位哈希或可达分支。
- 颜色数值取自 WinUI 3 1.7 主题字典；我们的运行时是 UWP（WinUI 2），键名一致但字典版本不同，个别默认值可能差 1–2 个色阶。
- 本审计只覆盖「文件项展示」；不含 Files 的图标服务、ViewModel、导航、地址栏、工具栏等。
- 未启动 UI 做目视确认（按要求）。
