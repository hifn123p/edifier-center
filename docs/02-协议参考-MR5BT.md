# 02 · MR5BT BLE 控制协议速查

> 本文档是从 `research/` 三方逆向成果整理出的 **M1 编码依据**。所有条目标注来源与置信度：
> **[实机]** = 在真实 MR5(MR5BT) 上由开源实现验证；**[家族]** = ConneX 家族多型号一致（M60/M90、S880DB MKII），MR5 高概率相同但未单独验证；**[推断]** = 结构推断，实现时须以 Phase 0 实测为准。
> 来源缩写：EC = D4n13l3k00/EdifierControl；OM = Devis99/omarchy-edifier-mr5；M60 = kralonur/edifier-speakers-ble；S880 = MikeBirdTech/liberated-edifier-s880db-mkii。

## 1. 发现与广播

| 项 | 值 | 置信 |
|---|---|---|
| 广播名 | `EDIFIER BLE`（可能缺失，**不要依赖名字匹配**） | [实机] |
| 厂商数据 ID | `0x07E0`（2016，Actions Semiconductor），数据 = Classic MAC(6B) + 协议版本/标志(2B)，实测 `b4e7b3d76153 0200` | [实机] |
| 搜索用服务 UUID（广播中） | **`00000a03-0000-1000-8000-00805f9b34fb`（MR5BT 实测值）**；同族还有 `00003a01…`（MR5 标准分支）、`00007e03…`（8625）等 | [实机] |
| LE 地址 | `B4:E7:B0:D7:61:53`（静态随机，与 Classic MAC 仅差一字节） | [实机] |
| 首次扫描 | 可能需要按音箱背面键进入**配对模式**；地址随后缓存复用 | [实机/OM] |
| 配对/绑定 | **无**（与官方 App 一致，无鉴权） | [实机/OM] |
| 广播可用性 | XLR 输入、无音频流时**可持续广播**（2026-11-02 实测 25s 内稳定命中）；同族 M60 空闲时可能间歇广播 | [实机] |

## 2. GATT 结构

```
service   48090a03-1a48-11e9-ab14-d663bd873d93    （MR5BT 实测值；同族 48093a01 / 48097e03 等，连接时逐个尝试）
  char    48090001-1a48-11e9-ab14-d663bd873d93    [read, notify]   音箱 → 主机
  char    48090002-1a48-11e9-ab14-d663bd873d93    [write, write-without-response]  主机 → 音箱
```

特征码全家族统一 [实机]。读写建议 `Uncached`（Windows GATT 缓存怪癖）[EC]。

### 2026-11-02 实机首测（MR5BT 固件 1.0.3，Win11 + Intel 蓝牙，bleak 直连）

| 查询 | 响应 | 解读 |
|---|---|---|
| `0x66` 音量 | `1E 16` | max=30，current=22（与 App 截图一致） |
| `0x61` 输入 | `04 01` | 两字节（疑似 max=4 / current=1）；字节→名称由内置向导标定 |
| `0xD5` 模式+声学 | `02 02 00 28 01 04 01` | 模式=02；低切 40Hz / 斜率 1(-12dB/oct) / 空间 4(-4dB) / 桌面开——与官方截图一致 |
| `0x43` 自定义 EQ | `0c 09 01 …` 43B | eqIndex=12、9 段；增益码 [8,9,10,6,5,10,9,8,7] → (code−6)×0.5dB = App 截图值；尾部含 4B 时间戳 + UTF-8 名称"我的音效2" |
| `0xC6` 固件 | `01 00 03` | 1.0.3 |
| `0x68` 解码 | `0D` | 新值（不在 00/04/05/09 映射内，XLR 场景；UI 显示原始码） |
| `0xD8` 能力 | 25B | 比 M60(22B) 多 3 字节，解析保持不透明 |

## 3. 帧格式（ConneX v2）

```
主机→音箱:  AA <appCode> <CMD> <LEN_HI> <LEN_LO> <PAYLOAD…> <CHECKSUM>
音箱→主机:  BB <appCode> <CMD> <LEN_HI> <LEN_LO> <PAYLOAD…> <CHECKSUM>   数据/查询响应
            CC …                                                          ACK（如改名等）
            DD …                                                          事件（EC 解析器接受）
```

- `LEN`：载荷字节数，16 位大端 [家族]。
- `CHECKSUM`：前面所有字节之和的低 8 位 [家族]。
- **appCode 不被严格校验**：OM 用 `0x06`、EC 用 `0x0C`、M60/S880 抓包见 `0xEC`，均工作。本项目固定 `0x0C`。
- 写入方式：EC 使用 WriteWithResponse；S880 观察官方 App 用 write-without-response。两者均被接受，本项目用 **WriteWithResponse**（便于失败检测）。
- 帧长校验：总长 = LEN + 6。

## 4. 连接仪式与保活 [家族/EC]

1. 订阅 `…0001` 通知（写 CCCD `01 00`）；
2. 首读序列：`D8`（能力）→ `66 / D5 / 43 / C6 / F2`（EC 的 `GetVolume/GetEq/GetCustomEq/GetVersion/GetDeviceState`）；
3. 官方 App 每数秒轮询 `C9 C3 68 61 D5 66` [S880]——本项目 ≥5s 轻量轮询仅作保活对账，状态同步以通知推送为主；
4. 断线（连接状态变更 / 写失败）→ 指数退避重连（1/2/5/10/30s，成功后清零）；Windows 睡眠唤醒（`PowerModeChanged`）触发立即重连 [EC]。

## 5. 命令表（本项目白名单）

| Cmd | 方向 | 名称 | 载荷 | 响应 | 置信 |
|---|---|---|---|---|---|
| `0x43` | 查 | 自定义 EQ | 无 | `[eqIndex=12][bandCount=9][9×4B 波段][4B 时间戳][UTF-8 名]` | [实机/OM] |
| `0x44` | 设 | 单段 EQ | `[byte0][index][freqHi][freqLo][gain]` | ACK `BB …01 01…` | [实机/OM+EC] |
| `0x47` | 设 | EQ 名称 | `[4B 日期][UTF-8]` | ACK | [家族/M60] |
| `0x61` | 查 | 输入源 | 无 | `[max?][current]`（M60/S880 同构） | [家族]，MR5 字段序 Phase 0 确认 |
| `0x62` | 设 | 输入源 | `[group][source]` | ACK + `0x61` 推送 | [家族]，**MR5 group/ID 未标定，本项目不实现** |
| `0x66` | 查 | 音量 | 无 | `[max][current]`，max=30 | [实机/EC+OM] |
| `0x67` | 设 | 音量 | `[V]` 0–30 | ACK + `0x66` 推送（旋钮同源） | [实机/EC+OM] |
| `0xC4` | 设 | 声音模式（+声学调校） | `[mode]` 或 7B 声学载荷（§8） | ACK + `0xD5` 推送 | [实机/EC+OM] |
| `0xC6` | 查 | 固件版本 | 无 | `02 01 00` → `1.0.3` 风格 | [实机/OM+EC] |
| `0xC9` | 查/设 | 设备名 | — / UTF-8 | ASCII | [家族]（改名不做） |
| `0xC8` | 查 | Classic MAC | 无 | 6B | [家族/M60+OM] |
| `0xD5` | 查 | 声音模式+声学调校 | 无 | `[mode][index][byte0][lowCut][slope][space][desktop]`（可短至 1B） | [实机/OM+EC] |
| `0xD8` | 查 | 能力 blob | 无 | 22B 静态 | [家族] |
| `0xF2` | 查 | 设备状态 | 无 | 型号相关 | [家族] |
| `0x68` | 查 | 解码状态 | 无 | `00`标准 / `04`无线Hi-Res / `05`有线Hi-Res / `09`192kHz | [家族/M60]，MR5 映射 Phase 0 确认 |

**禁用命令**（绝不发送）：`0x07` 复位、`0xCE` 关机、`0xCD` 断开蓝牙、`0x45/0x46` EQ 存储族（未验证）、OTA/恢复出厂/解绑相关一切。

## 6. 已知测试向量（单元测试用）

```
# GetVolume 请求（appCode=0x0C）                     [EC selfTest]
AA 0C 66 00 00 1C
# SetVolume(30) 请求                                 [EC selfTest]
AA 0C 67 00 01 1E 3C
# 音量响应（current=42 样例）                         [EC selfTest]
BB 0C 66 00 01 2A 58
# M60 音量查询/设置(appCode=0xEC 对照)                [M60]
AA EC 66 00 00 FC        →  BB EC 66 00 02 10 00 1F
AA EC 67 00 01 00 FE     →  BB EC 67 00 02 10 00 20
# 声学调校实测解码：30Hz / -24dB/oct / -2dB / 桌面开   [实机/OM]
02 02 00 1E 03 02 01
# M60 输入源（group=0F，Bluetooth）                   [M60]
AA EC 62 00 02 0F 01 0A  →  BB EC 61 00 02 0F 01 1A
```

## 7. MR5BT 专属参数（已知 / 待标定）

| 参数 | 值 | 状态 |
|---|---|---|
| 服务 UUID | **`48090a03…`（本机实测）**；同族 `48093a01…` / `48097e03…` 作为回退 | 已知 [实机] |
| 音量上限 | 30 | 已知 [实机] |
| 自定义 EQ | eqIndex=12，9 段 × 4B，62Hz–16kHz 倍频程；尾部 4B 时间戳 + UTF-8 名称 | 已知 [实机] |
| 声学调校 | §8 的 7 字节结构 | 已知 [实机] |
| EQ 增益换算 | **(code−6)×0.5dB，0dB 码点 6**（App 截图与实机查询逐段吻合） | 已知 [实机] |
| 解码状态码 | 00/04/05/09；本机实测另见 `0D`（XLR 场景） | 已知 [实机+家族]，未知值 UI 显示原始码 |
| 声音模式 ID | 实测当前 02（自定义激活中）→ 02=自定义；0/1 由向导确认 | 基本已知 |
| 输入源 ID→名称 | 实测响应 `[04, 01]`（当前=01，推测=XLR）；完整映射由向导标定 | 待标定 |
| 固件 | 实机 1.0.3 | 已知 |

标定方法见 `tools/phase0_probe.py calibrate`，结果回填 `profiles/mr5.json`。

## 8. 声学调校载荷（0xC4 扩展 / 0xD5 尾部）

7 字节：`[mode][index][byte0][lowCutoffHz][lowCutoffSlope][acousticSpace][desktopControl]`

| 字段 | 范围 | 说明 |
|---|---|---|
| `mode` | 0/1/2 | 监听/音乐/自定义（**MR5 具体值待标定**） |
| `index` | — | 官方 App 原样回写 |
| `byte0` | 0/1 | 0=正常；**1=DSP 复位声学调校**（"恢复默认"）[EC] |
| `lowCutoffHz` | 20–100，步进 5 | 低频切除频率 |
| `lowCutoffSlope` | 0–3 | 对应 −6/−12/−18/−24 dB/oct（部分固件直接上报 6/12/18/24，解析时归一化 [EC]） |
| `acousticSpace` | 0–4 | 对应 0/−1/−2/−3/−4 dB（自由场→墙角） |
| `desktopControl` | 0/1 | 桌面反射控制开关 |

实测样例（OM，固件验证）：`02 02 00 1E 03 02 01` = 模式 02、30Hz、−24dB/oct、−2dB、桌面开。

## 9. 自定义 EQ（eqIndex=12 布局）

`0x43` 响应：`[12][09][9×(freqHi freqLo gain …)]`，每波段 4 字节（OM 实测：`+0` 占位、`+2..3` 频率大端、增益在下一波段起点/末尾，解析时按 OM `parse_custom_eq` 偏移实现）；尾部 `[4B 时间戳][UTF-8 名称]`。
`0x44` 单段写入 5 字节：`[byte0][bandIndex][freqHi][freqLo][gain]`（byte0 取查询响应首位原样回写）。
App 显示增益步进 0.5dB；换算与 ±量程以 Phase 0 校准为准（推断 `(code−6)×0.5dB`）。
槽位管理（音效/音效2/…）协议未逆向，**不实现**——仅读写当前激活曲线。

## 10. 安全规则（代码与实验共同遵守）

1. **命令白名单**：代码中仅存在 §5 白名单枚举，profile 之外的 opcode 无法从 UI 触发；
2. 不实现 OTA / 恢复出厂 / 关机 / 断开蓝牙 / 解绑；固件升级永远走官方 App；
3. 实验性操作（Phase 0）改完即恢复原值；不盲扫 opcode / payload；
4. ACK ≠ 生效：设置后延迟读回确认（输入源切换需 2–3s 稳定期 [M60]）；
5. 写入失败/校验失败计数，连续异常触发重连而非重试轰炸；
6. 无鉴权通道：不做任何涉及账号/绑定的操作。

## 11. 来源

- `research/edifier-m60-ble.md`、`research/s880db-mkii-PROTOCOL.md`（协议细节与安全经验）
- `research/edifiercontrol/protocol.{hpp,cpp}`、`ble_client.cpp`（v2 帧、命令枚举、连接仪式、MR5 profile）
- `research/omarchy-mr5/edifier_protocol.py`（MR5 实机验证的命令表、EQ 布局、声学调校解码）
- 上游仓库链接见 `research/README.md` 与根 `README.md`。
