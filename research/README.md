# research/ — 第三方逆向资料存档

本目录存放项目调研阶段收集的**第三方**协议文档与源码样本，**版权归原作者所有**，仅作本地参考存档（学习目的），请勿再分发。来源与许可见各子目录内文件及下列仓库：

| 文件/目录 | 来源 | 内容 |
|---|---|---|
| `edifier-m60-ble.md` | [kralonur/edifier-speakers-ble](https://github.com/kralonur/edifier-speakers-ble)（`docs/edifier-m60-ble.md`） | Edifier M60 BLE 协议参考（实机验证，帧格式/命令表） |
| `s880db-mkii-PROTOCOL.md` | [MikeBirdTech/liberated-edifier-s880db-mkii](https://github.com/MikeBirdTech/liberated-edifier-s880db-mkii)（`PROTOCOL.md`） | S880DB MKII 协议（HCI 抓包逐条标注 CONFIRMED/LIKELY） |
| `edifiercontrol/` | [D4n13l3k00/EdifierControl](https://github.com/D4n13l3k00/EdifierControl)（MIT） | Windows C++ 托盘实现关键源码（protocol/ble/ui），支持 MR5；含 ConneX v2 帧、9 段 EQ、声学调校、双硬件版本 profile |
| `omarchy-mr5/` | [Devis99/omarchy-edifier-mr5](https://github.com/Devis99/omarchy-edifier-mr5)（MIT） | MR5 专属逆向：`edifier_protocol.py`（帧/命令/EQ 布局，实机验证）与 `edifier_daemon.py`（单长连接守护进程） |
| `metadata/` | GitHub REST API | 调研时抓取的仓库元数据与文件树快照（无代码内容） |

> 整理后的 MR5BT 协议速查（本项目 M1 编码依据）见 [`../docs/02-协议参考-MR5BT.md`](../docs/02-协议参考-MR5BT.md)。
