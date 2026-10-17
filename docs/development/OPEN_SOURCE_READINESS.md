# 仓库整理与开源准备

更新日期：2026-10-18。应用与 core 自有代码采用 GPL-3.0-only，代码已上传现有 GitHub 私有仓库。本文记录准备进度，不改变仓库可见性、签名身份或已发布产物。

## 本轮已完成

- 删除被当前指南取代的旧总体设计、八月会话交接、九月会话知识库和 AI V3 设计图；删除前已在仓库外备份。当前工作树不再包含这些开发对话截图，Git 历史仍保留原内容。
- 4 个本机发布脚本/审核状态文件停止 Git 跟踪，本机副本仍保留并由现有 artifacts 忽略规则保护。
- 更新 [0.3.5 / 135 发布结果](../RELEASE_0.3.5_135.md)与[发布索引](../releases/README.md)，根 README 提供匹配二进制的匿名对应源码下载入口。
- 中英文演示和此前产品短片归入 [feature-demos](../media/feature-demos/README.md)，保留最终视频、字幕、封面及 README 预览。
- 应用 README 补充参考项目、实际组件/资源与来源声明；core 有中英文 README、独立签名依赖包和对应源码 Release。
- 源码归档支持按显式应用/core revision 导出，使用该 commit 的锁文件和原生库 Git 对象；不要求最新文档 HEAD 与旧二进制来源相同。IncludeCoreVendor 可附完整锁定 Rust 依赖和离线配置。
- [依赖清单](../licenses/dependency-inventory.json)记录 Linux 双架构 Server 实际链接模块、AI Web runtime、Android/.NET 声明与 network-core Rust 锁。Server 的 Go runtime 和 71 个链接模块的原始许可文本已汇总到随镜像复制的许可包；AI Web 保留 108 份运行依赖声明。
- 新增文档链接、精确测试向量秘密扫描、Server/Web 和源码打包检查，以及 Issue/PR 模板。旧 Docker 发布 workflow 已移除，GitHub 检查不发布镜像或客户端。

## 保留的边界与剩余事项

| 事项 | 状态与下一步 |
| --- | --- |
| 公开历史范围 | 用户明确保留旧 Git 历史。删除当前文件不会清除旧 blob；公开前仍需确认历史开发对话/工作站信息的可见范围，本轮不重写历史 |
| GitHub 可见性和保护 | 两仓库维持私有。当前 main 未保护；规则接口因私有仓库套餐限制返回 403，私密漏洞报告接口不可核验。公开或升级套餐后再配置分支保护和安全功能，不为开启规则自动公开 |
| 第三方授权审核 | 已提供来源与可追溯清单；Android/.NET 的完整传递依赖以及商标、历史分发仍需逐项人工审核，清单不能当作全部权属审计通过 |
| 干净检出构建 | 复现步骤与本轮实际验证结果见下方；Linux/macOS 客户端及 Android Linux 原生工具适配尚未验收，不作通用平台承诺 |
| 公开签名信任 | Windows 仍使用既有签名者，自签信任、SmartScreen 信誉与签名完整性不同；未自动更换证书 |
| 产品功能门禁 | core RC、设备 JNI 冒烟和源码构建不代替完整图形/OEM/混版本/性能/长稳验收；MI 6 实际发声按维护者要求不测 |

## 扫描与源码交付

2026-10-18 的只读复扫覆盖两仓库当前跟踪文件与可达历史。应用当前 23、历史 27 条 Gitleaks 候选与前次已复核的固定协议向量、测试夹具和公开指纹完全一致，当前候选 Git blob 逐字节不变；core 无候选。应用 11 次 Actions 运行已核对：10 份非空日志无 Gitleaks 命中，另 1 份为有效空 ZIP。未确认新增凭据泄露；不把检测器结果解释为任意秘密都不存在。

新增检查对合法测试数据采用规则、路径、行列和整个文件 SHA-256 的精确基线。文件内容变化必须重新复核，不整目录豁免、不删除有价值的协议测试向量。扫描与本机备份位于仓库外，不随 Git 上传。

[0.3.5 / 135 完整对应源码](https://appship.kget.cc:8910/api/v1/artifacts/ca7763b8-ae84-4a24-9c78-d0e5698bbca6/download)绑定应用 8503acb、core 1c907c5，不能用另一 core RC 的源码代替。后续带 vendor 的补充源码包必须保留真实制品 revision，不覆盖已有发布附件。详细规则见[发布运行手册](../RELEASE_RUNBOOK.md)。

## 本轮验证记录

完成后以实际命令和结果更新本节。基础检查可在本地运行：

```powershell
python scripts/check_public_docs.py --self-test
python scripts/check_secrets.py --self-test --gitleaks <已有可执行文件>
python scripts/update_dependency_inventory.py --verify
./scripts/Test-CorrespondingSourceArchive.ps1
```

应用普通构建继续只消费已锁定的 DLL/SO；不要求 checkout core，不更改现有二进制或锁文件。维护者本机工具遵循 D:\Dev 清单，其他贡献者通过参数设置已有工具路径，见[构建指南](BUILDING.md)。
