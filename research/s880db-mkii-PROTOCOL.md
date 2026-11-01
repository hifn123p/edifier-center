# Edifier S880DB MKII — BLE Control Protocol

Reverse-engineered protocol reference. Every fact is labeled:

- **CONFIRMED** — observed in an HCI capture of the official app AND/OR
  verified live against a physical S880DB MKII.
- **LIKELY** — inferred from structure; not yet verified.
- **UNKNOWN** — not yet discovered. Do not guess.

Sources: Android Bluetooth HCI snoop of the official Edifier app
(Galaxy S9+), plus live experiments from macOS via bleak (2026-08).

## Discovery & connection (CONFIRMED)

**The speaker only advertises/exposes its BLE control service while a
Bluetooth Classic audio connection to it is active.** With no audio
link, there is nothing to scan for. The Classic link does NOT need to be
the active input: BLE control stays reachable with the speaker switched
to optical or USB, as long as the Classic connection persists.

The BLE advertisement carries **no device name** in some states (macOS
showed none until after the first connect, then cached `EDIFIER BLE`):

- Advertised 16-bit service UUID: `0xF100`
- Manufacturer data company ID: `0x07E0` (Actions Semiconductor pattern)

### GATT layout (CONFIRMED, live-inspected)

```text
service 4809f101-1a48-11e9-ab14-d663bd873d93
  char 48090001-1a48-11e9-ab14-d663bd873d93   [read, notify]              <- speaker->host (TX)
    CCCD at ATT handle 0x000d
  char 48090002-1a48-11e9-ab14-d663bd873d93   [write, write-without-response]  <- host->speaker
```

Earlier Edifier reverse engineering suggested service `48093801-...`;
the S880DB MKII actually uses `4809f101-...`. The characteristic UUIDs
match earlier models.

### Connect ritual (CONFIRMED — required)

A cold write is **silently ignored**. The official app's sequence, which
works:

1. Enable notifications on `48090001` (CCCD write `01 00`).
2. Write query `D8` (`AA EC D8 00 00 6E`) — returns the state blob.
3. Then send commands. All writes are ATT Write Command
   (write-without-response).

## Framing (CONFIRMED)

```text
host -> speaker:  AA EC <CMD> <LEN_HI> <LEN_LO> <PAYLOAD...> <CHECKSUM>
speaker -> host:  BB EC <CMD> <LEN_HI> <LEN_LO> <PAYLOAD...> <CHECKSUM>
```

- `LEN` is the 16-bit big-endian payload byte count (LIKELY; consistent
  with every packet seen).
- `CHECKSUM = sum(all preceding bytes) & 0xFF` (CONFIRMED both
  directions).

## Commands (host -> speaker)

### 0x62 — set input (CONFIRMED)

```text
AA EC 62 00 02 10 <SOURCE> <CK>
```

| Source | ID |
|---|---|
| USB | 0x01 |
| Bluetooth | 0x04 |
| Optical | 0x05 |

Coax / Line 1 / Line 2 IDs: UNKNOWN. `0x02`, `0x03` and others are
untested — do not probe blindly.

Ack: `BB EC 62 00 01 01 <CK>` — payload `01` is a success code (same
for every source; CONFIRMED). The speaker also pushes an `0x61` input
status (below).

### 0x67 — set absolute volume (CONFIRMED)

```text
AA EC 67 00 01 <VOLUME> <CK>
```

Observed working values: 0–19. Max is LIKELY 30 (see `0x66`).
Ack: `BB EC 67 00 02 1E <VOLUME> <CK>`.

### Zero-length queries (CONFIRMED shape: `AA EC <CMD> 00 00 <CK>`)

| Cmd | Reply payload (after 2-byte len) | Meaning |
|---|---|---|
| 0x61 | `10 <SOURCE>` | Input status (CONFIRMED). Also pushed unsolicited on change. |
| 0x66 | `1E <VOL>` | Volume status (CONFIRMED); first byte constant 0x1E=30, LIKELY the max. |
| 0x68 | `00` | UNKNOWN — single flag; mute state is a plausible guess (adjacent to volume). |
| 0xC3 | `0D` | UNKNOWN — single byte, saw 13. |
| 0xC6 | `02 00 00` | UNKNOWN — version-shaped. |
| 0xC9 | ASCII text | Device name (CONFIRMED): `EDIFIER S880DB MKII`. |
| 0xD5 | `<PRESET>` | EQ preset (CONFIRMED): 0=classic, 1=monitor (directly verified against the display), 2=dynamic, 3=vocal, 4=custom (0/2/3/4 LIKELY via the EQ button's cycle order). Pushed unsolicited on change. |
| 0xD8 | 22 bytes | Device state/settings blob (below). |

The official app polls `C9 C3 68 61 D5 66` (~100 ms apart) every few
seconds and sends `D8` (+`C6`) at connect.

## Notifications (speaker -> host)

- `0x61` input status — pushed on every input change (CONFIRMED).
- `0x66` volume status — pushed on every volume change, including the
  physical knob (CONFIRMED); knob and BLE volume are one state.
- `0xD5` EQ preset — pushed on every EQ change (CONFIRMED).
- `0x86` (payload `02 01`) + `0xD3` (payload `00 00`) — a periodic
  pair pushed every ~30 s regardless of activity. Meaning UNKNOWN.
- `0x50` — event with 3-byte payload, seen `00 00 00` and `01 00 00`.
  The first byte's flips correlate loosely with audio stream
  start/stop (LIKELY stream state; unverified).

### Remote-control interaction (CONFIRMED, behavioral)

The physical remote is proprietary 2.4 GHz RF, but state changes it
causes surface as the pushes above. On Bluetooth input, play/pause and
next/previous are forwarded to the connected host as AVRCP media
commands (no BLE traffic, no local mute); play/pause acts as a local
mute only on non-Bluetooth, non-USB inputs.

### 0xD8 state blob (fields UNMAPPED)

```text
BB EC D8 00 16 00 00 01 04 01 01 01 00 00 02 01 01 0A 0A 01 00 00 20 04 03 04 20 <CK>
```

Byte-identical across input and volume changes → static device/settings
info, not live state. The `0A 0A` pair is LIKELY bass/treble at
mid-point (10/10) — unverified. Mapping strategy: change one setting in
the official app, re-query `D8`, diff.

## UNKNOWN / future work

- Mute/unmute, standby, wake, EQ preset set-commands, bass/treble
  set-commands, coax/line source IDs.
- Meaning of `0x50` events, `0x68`, `0xC3`, `0xC6`, `0xD5` values.
- Whether volume >30 is rejected or clamped (do not test blindly on
  attached ears/equipment).

## Safety rules for experimenters

- Only send packets observed in a capture or documented here.
- No brute-force sweeps of command IDs or payloads: unknown commands on
  this platform may include factory reset, firmware, or pairing ops.
- Standby/wake experiments may take the BLE link (and this transport)
  down — expect to re-establish the Classic audio link.
