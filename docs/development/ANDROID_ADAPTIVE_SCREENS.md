# Android 平板与折叠屏适配

本轮实现日期：2026-10-18。开发包沿用 Android 最后成功发布的 0.3.6 / 136，包名为
`dev.termlink.harness.adaptivevalidation`，默认测试地址为 `https://termlink2.kget.cc:8910`。
这是发布后的新增实现，不表示已发布的 0.3.6 APK 已包含本页改动。
本轮正式版本准备为 [0.3.7 / 137](../RELEASE_0.3.7_137.md)，发布状态以该轮渠道验收记录为准。

## 当前行为

| 窗口或姿态 | 行为 |
| --- | --- |
| 平板、展开的大屏 | 按实际窗口宽度选择底部导航或侧边导航；宽度至少 700dp、高度至少 520dp 且键盘未遮挡时，CLI 保留会话列表与终端双栏 |
| 竖向分隔铰链、书本姿态 | 主操作区使用较大的连续一侧，宽度相同时按界面方向选择起始侧；内容不跨铰链 |
| 横向分隔折痕、悬停姿态 | 主操作区位于折痕上方，输入法仅扣除与该区域实际重叠的高度，避免重复缩小 |
| 内折、外折、小折叠的收起/展开 | 根据窗口大小和 WindowManager 折叠特征重新布局；系统重建 Activity 时保留运行时、原生引擎与已有导航状态 |
| 自由窗口及分屏中的小窗口 | 使用当前窗口的可用宽高和坐标，不使用整块显示器的尺寸判断布局 |

侧边导航在悬停等高度不足的窗口中支持滚动，保持四个入口可达。

折叠特征由固定版本 `androidx.window:window:1.5.1` 提供，只在 Activity 可见时监听。
窗口无折痕或设备没有上报分隔特征时，继续使用完整可用窗口。第三方原始许可随 APK
保留在 `assets/licenses/androidx-window-LICENSE.txt`，声明清单也已更新。

布局变化保持终端原有字符网格；需要电脑端匹配手机可视区域时，仍由用户主动点击
[按手机窗口调整会话](../PHONE_WINDOW_RESIZE.md)。折叠或旋转不会自行调整 Windows 终端。
登录用户名通过系统保存状态恢复，密码继续仅保存在当前页面的临时内存中。

书本和悬停姿态采用连续单面布局；本轮没有增加跨铰链的双终端、双应用或独立下屏键盘。
小折叠外屏的系统是否允许启动第三方应用，仍取决于设备厂商；模拟器可显示不等于绕过厂商限制。

## 已执行的模拟器验证

统一使用已安装的 Android 36 Google APIs x86_64 系统镜像，未接触 MI 6 和既有正式包。

| 模拟器配置 | 实际窗口用例 |
| --- | --- |
| 7.6 英寸 Fold-in，1768×2208 / 420dpi | 展开、竖折半开、旋转后的横折、收起、重新展开，5 项通过 |
| 8 英寸 Fold-out，2200×2480 / 420dpi | 展开、半折、旋转后的半折、收起、重新展开，5 项通过 |
| 6.7 英寸 Fold-in，1080×2636 / 480dpi | 展开、横折半开、旋转后的竖折、收起、重新展开，5 项通过 |
| Pixel Tablet，2560×1600 / 320dpi | 横屏、竖屏，2 项通过；另通过自由窗口缩放与会话保持的自动化断言 |

17 项设备窗口用例由 `ProductPhysicalWindowInstrumentedTest` 验证真实 WindowManager
回调、操作区边界、JNI 终端存在、marker 文本与原始 80×24 网格。
`ProductAdaptiveLayoutInstrumentedTest` 另外在内折和平板各执行 4 项：导航断点、铰链避让、
Activity 重建后的终端/会话恢复、登录草稿与实际输入法弹出。AI workspace/未发送草稿与
原生引擎是否复用也有显式断言。几何单元测试覆盖窗口偏移、RTL、无效/外部折痕和多条分隔折痕。

本轮应用单元测试 479 项通过，Android Lint 无错误。Windows 主窗口默认尺寸调整至
820×600 的构建与桌面 Headless 回归也已通过。模拟器曾出现 System UI 无响应弹窗，
清理测试环境弹窗后重跑平板界面测试通过；没有把受弹窗阻挡的失败运行计为通过。

多台模拟器并行时，当前 Emulator 的内部初始化命令使用 `adb -e`，日志报告
`more than one emulator`，无法自动写入折痕区域。半折用例按各自 AVD 的铰链坐标，
通过带明确序列号的 ADB 写入 AOSP `display_features` 系统测试设置，并使用真实 hinge sensor
角度变化和 AndroidX 系统回调。测试注入用例与该系统配置下的姿态用例分别记录，不冒称厂商实机测试。

## 复现入口

工具和环境按[构建指南](BUILDING.md)准备。需要生成 AI Web 资源，保留现有原生库与锁文件。

```powershell
$buildArgs = @(
    '-PtermlinkVersionName=0.3.6', '-PtermlinkVersionCode=136',
    '-PtermlinkDebugApplicationIdSuffix=.adaptivevalidation',
    '-PtermlinkServerUrl=https://termlink2.kget.cc:8910'
)
Push-Location android
./gradlew.bat :app:testDebugUnitTest :app:assembleDebug :app:assembleDebugAndroidTest @buildArgs
Pop-Location

adb -s <测试模拟器序列号> install -r android/app/build/outputs/apk/debug/app-debug.apk
adb -s <测试模拟器序列号> install -r android/app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk
adb -s <测试模拟器序列号> shell settings put secure show_ime_with_hard_keyboard 1
adb -s <测试模拟器序列号> shell am instrument -w `
    -e class dev.termlink.product.ProductAdaptiveLayoutInstrumentedTest `
    dev.termlink.harness.adaptivevalidation.test/androidx.test.runner.AndroidJUnitRunner
```

专用 Activity 位于 `src/debug`，要求 `.adaptivevalidation` 后缀；使用本地 fixture 与真实 JNI
renderer，不连接 Windows 主机，不发送真实输入。生产登录 Activity 仍可在同一开发包中单独打开。
因此，本页的 UI 测试不代替真实登录、Relay 收发、厂商输入法、外接键鼠和真机展开连续性验收。

参考：[WindowManager](https://developer.android.com/jetpack/androidx/releases/window)、
[折叠特征与姿态](https://developer.android.com/develop/ui/compose/layouts/adaptive/foldables/make-your-app-fold-aware)。
