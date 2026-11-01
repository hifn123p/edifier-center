<div align="center">

# 🎛️ Edifier Center

**EDIFIER MR5BT 音箱的 Windows 托盘控制程序**

蓝牙低功耗（BLE）直连控制通道 · 无需手机 App · 与 XLR/TRS/RCA 音频链路并存

[![CI](https://github.com/hifn123p/edifier-center/actions/workflows/ci.yml/badge.svg)](https://github.com/hifn123p/edifier-center/actions/workflows/ci.yml)
[![Release](https://github.com/hifn123p/edifier-center/actions/workflows/release.yml/badge.svg)](https://github.com/hifn123p/edifier-center/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-3FB950.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6?logo=windows11&logoColor=white)]()

**技术路线**

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)]()
[![WPF](https://img.shields.io/badge/UI-WPF%20·%20Fluent%20·%20Mica-5C2D91?logo=windows&logoColor=white)]()
[![BLE GATT](https://img.shields.io/badge/控制通道-BLE%20GATT%20(WinRT)-0082FC?logo=bluetooth&logoColor=white)]()
[![xUnit](https://img.shields.io/badge/测试-xUnit-8A2BE2?logo=xunit&logoColor=white)]()
[![GitHub Actions](https://img.shields.io/badge/CI%2FCD-GitHub%20Actions-2088FF?logo=githubactions&logoColor=white)]()
[![单文件发布](https://img.shields.io/badge/产物-单文件%20exe-3FB950)]()

</div>

---

> **状态**：设计定稿 + 代码完成（静态编写），等待 CI 首次编译验证 → Phase 0 实机标定 → 联调。
> 路线图见 [docs/01-可行性分析与设计方案.md](docs/01-可行性分析与设计方案.md) §6。

## ✨ 功能（定稿范围）

| 类型 | 功能 | 说明 |
|---|---|---|
| 📖 显示 | 输入源 / 音量与静音 / 连接状态与信号档位 / 固件版本 / 解码状态 | 全部由音箱 BLE 推送实时驱动（含物理旋钮改动） |
| 🎚️ 控制 | 音量（含静音）· 声音模式（监听/音乐/自定义）· 9 段自定义 EQ | 与物理旋钮、手机 App 双向实时同步 |
| 🎛️ 控制 | 声学调校（低频切除 / 滚降斜率 / 空间声学 / 桌面反射控制） | M2 里程碑 |
| 🚫 明确不做 | 固件更新(OTA)、输入切换、改名、LED、关机、恢复出厂 | 安全红线：危险 opcode 在代码中无路径 |

界面与交互：**[docs/ui.html](docs/ui.html)**（交互式设计稿）。

## 🔬 工作原理

漫步者 ConneX 系列 App 通过一条独立的 **BLE GATT 控制通道**（服务 `48093a01-…`）与音箱通信，与蓝牙音频流（A2DP）完全分离。本项目逆向并复用该协议：

- 帧格式 `AA <appCode> <CMD> <LEN> <PAYLOAD> <CK>`（校验和 = 前缀字节和低 8 位）
- 命令覆盖音量 / 输入 / EQ / 声学调校 / 解码状态，音箱主动推送状态变化
- 无需系统级蓝牙配对（首次扫描按音箱背面键进配对模式）
- 协议细节：**[docs/02-协议参考-MR5BT.md](docs/02-协议参考-MR5BT.md)** · 逆向来源：[research/](research/README.md)

音频走你现有的链路（如 Focusrite 4i4 → XLR），BLE 仅传控制指令（流量约 KB/h 级），互不干扰。

## 📦 使用

1. 到 [Releases](../../releases) 下载 `EdifierCenter.exe`（自包含单文件，无需安装运行时）
2. 放到**有写权限的用户目录**后运行（程序会在运行目录生成 `config.json` / `logs\` / `diagnostics\`）
3. 首次连接成功后会自动弹出**校准向导**：按提示在手机 App 里切几次输入、把 EQ 调两个点、切三种模式——结果（输入标签 / EQ 增益码表 / 模式 ID）自动写入 `config.json`；全部可跳过，托盘菜单"校准向导"可重跑
4. 首次扫描若扫不到设备：按音箱背面按键进入配对模式
5. 托盘图标：左键=控制面板，右键=菜单（面板 / 校准 / 静音 / 重连 / 自启 / 日志 / 退出）

## 🛠️ 构建与发布（全部在 GitHub Actions，本地无需 .NET）

| 事件 | 工作流 | 产物 |
|---|---|---|
| push / PR | `ci.yml`：restore → build → xUnit 单测 | 测试报告 artifact |
| tag `v*` | `release.yml`：test → `dotnet publish` 单文件自包含 → GitHub Release | `EdifierCenter.exe` |

本地工具链（可选，Python 3.11+——调试用；标定已内置为程序首次启动向导）：

```bash
pip install -r tools/requirements.txt
python tools/phase0_probe.py scan        # 扫描音箱
python tools/phase0_probe.py info        # 连接 + 读全部状态 + 监听推送
```

其他工具：`tools/make_icon.py`（从设计令牌重新生成 `app.ico`）。

## 🧱 架构速览

```
托盘 UI (WPF, Mica/Fluent)
   │ 绑定只读 SpeakerState  ── 命令(150ms 节流) ──►
EdifierSession (单长连接 · 写队列 Channel · 轮询保活 · 指数退避重连)
   │ FrameCodec 编解码 ── ResponseParser 解析推送
WinRtTransport (BluetoothLEAdvertisementWatcher + GATT, Uncached)
   │ BLE GATT 仅控制通道（~KB/h）
MR5BT ◄── XLR 音频（Focusrite 4i4，不受影响）
```

细节：**[docs/03-代码设计文档.md](docs/03-代码设计文档.md)**（模块 / 状态机 / 线程模型 / 错误处理 / 测试）。

## 🗂️ 目录

```
├─ docs/          01 可行性设计 · 02 协议参考 · 03 代码设计 · ui.html 设计稿 · 官方 App 截图
├─ profiles/      mr5.json 型号参数（Phase 0 标定回填点）
├─ src/           Core（协议/领域，可单测）· Ble（WinRT 传输/会话）· App（WPF 托盘壳）
├─ tests/         xUnit 单元测试（协议向量 / 解析器 / EQ / 声学调校）
├─ tools/         phase0_probe.py 实机探针 · make_icon.py 图标生成
└─ research/      第三方逆向资料存档（版权归原作者）
```

## 🗺️ 路线图

- [x] 协议调研与可行性分析（M60/M90、S880DB MKII、EdifierControl、omarchy-mr5）
- [x] UI 设计定稿（docs/ui.html）
- [x] 代码骨架（Core/Ble/App + 测试 + CI/Release 流水线），**CI 编译与测试已通过**
- [x] 校准内置：首次启动校准向导（输入标签 / EQ 增益码表 / 模式 ID → config.json）
- [ ] **M1 收尾**：真机联调（连接、音量、EQ、状态显示逐项验收）
- [ ] **M2**：WPF-UI/H.NotifyIcon 控件精化 · Ble 状态机单测 · 自启打磨 · 首个 Release
- [ ] **M3**（可选）：托盘滚轮 OSD、全局热键、安装器

## ⚠️ 免责声明

本项目与漫步者（EDIFIER）无隶属关系，未获官方认可。EDIFIER 及相关名称/商标归其所有者所有。协议来自社区对官方 App 的逆向研究，仅供个人控制自有设备使用；**本程序不实现固件更新、恢复出厂等危险操作**，使用风险自负。

## 📄 许可与致谢

本项目以 [MIT](LICENSE) 许可发布。协议逆向依赖以下项目，致谢：

- [kralonur/edifier-speakers-ble](https://github.com/kralonur/edifier-speakers-ble)（M60/M90 协议文档 + HA 集成）
- [MikeBirdTech/liberated-edifier-s880db-mkii](https://github.com/MikeBirdTech/liberated-edifier-s880db-mkii)（协议文档 + CLI）
- [D4n13l3k00/EdifierControl](https://github.com/D4n13l3k00/EdifierControl)（Windows C++ 托盘实现，支持 MR5）
- [Devis99/omarchy-edifier-mr5](https://github.com/Devis99/omarchy-edifier-mr5)（MR5 专属逆向，Python/bleak）
- [hbldh/bleak](https://github.com/hbldh/bleak)（跨平台 BLE 客户端库）
