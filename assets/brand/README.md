# TermLink 品牌图标

正式方向：`01B Prompt Ligature`，由 `01 Terminal Bridge` 与 `03 TL Monogram` 融合。

## 图形语义

- 圆角窗口：Terminal 与跨端会话；
- 左侧 `>`：命令提示符；
- `TL`：TermLink 品牌字标；
- 绿色 L：持续链路与设备连接；
- 黄色节点：在线状态、可信设备或授权完成。

## 文件

- `termlink-app-icon.svg`：App、Windows 包、安装器和大尺寸产品入口；
- `termlink-favicon.svg`：网页 favicon；
- `termlink-mark-color.svg`：深色界面上的透明彩色标记；
- `termlink-mark-light.svg`：深色托盘、标题栏和高对比界面；
- `termlink-mark-dark.svg`：浅色托盘、标题栏和打印场景。

## 颜色

| Token | Value | 用途 |
| --- | --- | --- |
| Ink | `#10151D` | App 图标背景 |
| Foreground | `#F4F7FB` | 外框与 T |
| Link | `#62E6C5` | 命令提示符与 L |
| Presence | `#FFCF5A` | 状态节点 |
| Dark mono | `#17202A` | 浅色界面单色标记 |

## 使用规则

- 16～24px 托盘和标题栏优先使用单色版本；
- 32px 以上可以使用彩色版本；
- 不旋转、不拉伸、不重新排列提示符、TL 和状态节点；
- 不在图标内部增加产品名；
- 高对比模式由系统前景色替换单色版本，不依赖彩色状态点表达唯一信息。
