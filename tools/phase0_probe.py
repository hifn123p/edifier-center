#!/usr/bin/env python3
"""Edifier Center — Phase 0 实机验证探针（MR5BT / ConneX BLE 协议）。

用途（见 docs/01 §4）：
  1. 确认 PC 蓝牙能够发现并连接这台 MR5BT 的控制服务（Go/No-Go）；
  2. 只读标定：输入源"字节→名称"对照、EQ 增益 0dB 码点、声学调校解码；
  3. 观察音箱主动推送（物理旋钮 / 手机 App 改动会实时出现）。

安全红线（务必遵守，见 docs/02 §10）：
  - 只发送本脚本白名单内的查询/设置命令；
  - 不发送 0x07(复位) / 0xCE(关机) / 0xCD(断开蓝牙) / OTA / 恢复出厂；
  - set-volume 仅用于验证读写通路，用完请调回原值。

用法：
  python phase0_probe.py scan                  # 扫描（扫不到请按音箱背面键进配对模式）
  python phase0_probe.py info                  # 连接 + 首读全部状态 + 监听推送 10 秒
  python phase0_probe.py watch --seconds 60    # 长时间监听推送（期间去拧旋钮/操作手机 App）
  python phase0_probe.py calibrate             # 交互式标定：按提示切输入/调 EQ，记录字节值
  python phase0_probe.py set-volume --value 22 # 验证写通路（先读取原值，结束后恢复）

依赖：Python 3.11+，pip install -r tools/requirements.txt（bleak）
"""
from __future__ import annotations

import asyncio
import contextlib
import json
import sys
from pathlib import Path

# 与 profiles/mr5.json 保持一致
SEARCH_UUIDS = [
    "00003a01-0000-1000-8000-00805f9b34fb",  # MR5 标准版
    "00007e03-0000-1000-8000-00805f9b34fb",  # MR5 (8625 DSP 改版)
]
SERVICE_UUIDS = {
    "48093a01-1a48-11e9-ab14-d663bd873d93",
    "48097e03-1a48-11e9-ab14-d663bd873d93",
}
NOTIFY_UUID = "48090001-1a48-11e9-ab14-d663bd873d93"
WRITE_UUID = "48090002-1a48-11e9-ab14-d663bd873d93"
MANUFACTURER_ID = 0x07E0  # 2016, Edifier / Actions Semiconductor

APPCODE = 0x0C  # ConneX v2；已验证 0x06/0x0C/0xEC 音箱均接受

# 白名单命令（docs/02 §5）
READ_CMDS = {
    "support": 0xD8,   # 能力 blob（22 字节）
    "volume": 0x66,    # <max><current>
    "eq": 0xD5,        # <mode> + 声学调校尾部（≤7 字节）
    "customEq": 0x43,  # eqIndex=12 九段布局
    "version": 0xC6,   # 固件版本
    "state": 0xF2,     # 设备状态
    "input": 0x61,     # <max?><current>
    "decode": 0x68,    # 解码状态 00/04/05/09
}
WRITE_CMDS = {"setVolume": 0x67, "setEq": 0xC4}  # 仅验证写通路使用

NOTES_FILE = Path(__file__).resolve().parent.parent / "profiles" / "phase0-notes.json"


def build_frame(command: int, payload: bytes = b"") -> bytes:
    body = bytes([0xAA, APPCODE, command, (len(payload) >> 8) & 0xFF, len(payload) & 0xFF]) + payload
    return body + bytes([sum(body) & 0xFF])


def parse_frame(data: bytes) -> dict | None:
    if len(data) < 6:
        return None
    if data[0] not in (0xBB, 0xCC, 0xDD):
        return {"raw": data.hex(" "), "note": "非 ConneX v2 帧头"}
    length = (data[3] << 8) | data[4]
    if len(data) != length + 6:
        return {"raw": data.hex(" "), "note": f"长度不符(期望 {length + 6})"}
    ok = (sum(data[:-1]) & 0xFF) == data[-1]
    return {
        "type": {0xBB: "data", 0xCC: "ack", 0xDD: "event"}.get(data[0], hex(data[0])),
        "cmd": f"0x{data[2]:02X}",
        "payload": data[5:-1].hex(" "),
        "checksum_ok": ok,
    }


def pretty(frame: dict) -> str:
    if "payload" not in frame:
        return str(frame)
    p = bytes.fromhex(frame["payload"].replace(" ", ""))
    cmd = int(frame["cmd"], 16)
    note = ""
    if cmd == 0x66 and len(p) >= 2:
        note = f"→ 音量 max={p[0]} current={p[1]}"
    elif cmd == 0x61 and len(p) >= 2:
        note = f"→ 输入(待标定) [{p[0]} {p[1]}]"
    elif cmd == 0x61 and len(p) == 1:
        note = f"→ 输入(待标定) [{p[0]}]"
    elif cmd == 0x68 and len(p) >= 1:
        mapping = {0: "标准", 4: "无线 Hi-Res", 5: "有线 Hi-Res", 9: "192kHz"}
        note = f"→ 解码 {mapping.get(p[0], hex(p[0]))}"
    elif cmd == 0xD5 and len(p) >= 1:
        modes = {0: "监听?", 1: "音乐?", 2: "自定义?"}
        note = f"→ 模式 {modes.get(p[0], hex(p[0]))}，尾部声学调校 [{p[1:].hex(' ')}]"
        if len(p) >= 7:
            note += f"（低切 {p[3]}Hz / 斜率{[-6,-12,-18,-24][min(p[4],3)]}dB/oct / 空间-{p[5]}dB / 桌面{'开' if p[6] else '关'}）"
    elif cmd == 0xC6:
        note = f"→ 固件 {'.'.join(str(b) for b in p)}"
    elif frame["type"] == "ack":
        note = "→ ACK"
    return f"{frame['type']:<5} {frame['cmd']:<5} {frame['payload']:<40} {note}"


async def pick_device(timeout: float = 15.0):
    from bleak import BleakScanner

    print(f"扫描 {timeout:.0f}s（按服务 UUID 匹配；扫不到请按音箱背面键进配对模式）…")
    devices = await BleakScanner.discover(timeout=timeout, service_uuids=SEARCH_UUIDS)
    for i, d in enumerate(devices):
        print(f"  [{i}] {d.address}  {d.name!r}  RSSI {d.rssi}")
    if not devices:
        print("未发现 MR5BT。确认音箱开机、PC 蓝牙可用，或按背面键进配对模式后重试。")
        sys.exit(1)
    return devices[0] if len(devices) == 1 else devices[int(input("选择序号: "))]


class Session:
    def __init__(self, address: str):
        from bleak import BleakClient

        self.client = BleakClient(address)
        self.service = None
        self.frames: list[dict] = []
        self._notify_event = asyncio.Event()

    async def __aenter__(self):
        await self.client.connect()
        svcs = await self.client.get_services()
        self.service = next((s for s in svcs if s.uuid in SERVICE_UUIDS), None)
        if self.service is None:
            print(f"已连接但未暴露控制服务：{[s.uuid for s in svcs]}")
            print("提示：Windows 偶发 GATT 缓存问题，重试或进配对模式后重连。")
            sys.exit(1)
        print(f"已连接 {self.client.address}，控制服务 {self.service.uuid}")
        await self.client.start_notify(NOTIFY_UUID, self._on_notify)
        return self

    async def __aexit__(self, *exc):
        with contextlib.suppress(Exception):
            await self.client.stop_notify(NOTIFY_UUID)
        with contextlib.suppress(Exception):
            await self.client.disconnect()

    def _on_notify(self, _char, value: bytearray):
        f = parse_frame(bytes(value))
        if f:
            self.frames.append(f)
            print("  推送", pretty(f))
            self._notify_event.set()

    async def send(self, command: int, payload: bytes = b""):
        frame = build_frame(command, payload)
        await self.client.write_gatt_char(WRITE_UUID, frame, response=True)
        print(f"发送  0x{command:02X} {frame.hex(' ')}")

    async def read_all(self):
        for name, cmd in READ_CMDS.items():
            await self.send(cmd)
            self._notify_event.clear()
            with contextlib.suppress(asyncio.TimeoutError):
                await asyncio.wait_for(self._notify_event.wait(), timeout=1.5)
            await asyncio.sleep(0.15)

    async def drain(self, seconds: float):
        print(f"监听推送 {seconds:.0f}s（拧旋钮 / 操作手机 App，观察实时同步）…")
        await asyncio.sleep(seconds)


async def cmd_scan(args):
    await pick_device(args.timeout)


async def cmd_info(args):
    dev = await pick_device(args.timeout)
    async with Session(dev.address) as s:
        await s.send(READ_CMDS["support"])
        await asyncio.sleep(0.3)
        await s.read_all()
        await s.drain(args.seconds)
        (NOTES_FILE.parent).mkdir(exist_ok=True)
        NOTES_FILE.write_text(json.dumps(s.frames, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"原始帧已存 {NOTES_FILE}")


async def cmd_watch(args):
    dev = await pick_device(args.timeout)
    async with Session(dev.address) as s:
        await s.send(READ_CMDS["support"])
        await s.drain(args.seconds)


async def cmd_calibrate(args):
    """交互式只读标定：结果手工回填 profiles/mr5.json。"""
    dev = await pick_device(args.timeout)
    async with Session(dev.address) as s:
        print("\n== 标定 1/2：输入源 ==")
        print("按提示用手机 App 依次切换输入，每次切换后回车读取当前值。")
        labels = {}
        while True:
            await s.send(READ_CMDS["input"])
            await asyncio.sleep(1.0)
            raw = input("  请输入音箱当前输入的名称（如 XLR/BT/AUX，直接回车结束）: ").strip()
            if not raw:
                break
            frames = [f for f in s.frames if f.get("cmd") == "0x61"]
            if frames:
                p = bytes.fromhex(frames[-1]["payload"].replace(" ", ""))
                key = p[-1] if len(p) >= 1 else None
                labels[str(key)] = raw
                print(f"  记录：字节 {key} → {raw}")
        print("\n== 标定 2/2：EQ 增益 0dB 码点 ==")
        print("在手机 App 把 62Hz 波段调到 0dB，回车读取 customEq：")
        input()
        await s.send(READ_CMDS["customEq"])
        await asyncio.sleep(1.0)
        frames = [f for f in s.frames if f.get("cmd") == "0x43"]
        if frames:
            print("  customEq 载荷:", frames[-1]["payload"])
            print("  → 对照 docs/02 §9 解析各波段字节，0dB 所在码点即 gainCodeZeroDb")
        print("\n完成。请把以上结果回填 profiles/mr5.json 的 inputs / customEq 字段。")


async def cmd_set_volume(args):
    dev = await pick_device(args.timeout)
    async with Session(dev.address) as s:
        await s.send(READ_CMDS["volume"])
        await asyncio.sleep(1.0)
        print(f"目标音量 {args.value}（结束后请手动调回原值）")
        await s.send(WRITE_CMDS["setVolume"], bytes([args.value]))
        await asyncio.sleep(1.0)
        await s.send(READ_CMDS["volume"])


def main():
    import argparse

    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("scan", help="扫描音箱")
    p.add_argument("--timeout", type=float, default=15)
    p = sub.add_parser("info", help="连接并读取全部状态")
    p.add_argument("--timeout", type=float, default=15)
    p.add_argument("--seconds", type=float, default=10)
    p = sub.add_parser("watch", help="长时间监听推送")
    p.add_argument("--timeout", type=float, default=15)
    p.add_argument("--seconds", type=float, default=60)
    sub.add_parser("calibrate", help="交互式只读标定")
    p = sub.add_parser("set-volume", help="验证写通路")
    p.add_argument("--timeout", type=float, default=15)
    p.add_argument("--value", type=int, choices=range(0, 31), required=True)
    args = ap.parse_args()

    handlers = {"scan": cmd_scan, "info": cmd_info, "watch": cmd_watch,
                "calibrate": cmd_calibrate, "set-volume": cmd_set_volume}
    try:
        import bleak  # noqa: F401
    except ImportError:
        print("缺少依赖 bleak，请先执行：pip install -r tools/requirements.txt")
        sys.exit(1)
    asyncio.run(handlers[args.cmd](args))


if __name__ == "__main__":
    main()
