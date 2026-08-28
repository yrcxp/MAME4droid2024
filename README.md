# MAME4droid (Current)

![Release: v1.40](https://img.shields.io/badge/Release-v1.40-blue)
![MAME Core: 0.289](https://img.shields.io/badge/MAME_Core-0.289-orange)
![Platform: Android](https://img.shields.io/badge/Platform-Android-brightgreen.svg)
![License: GPL v2](https://img.shields.io/badge/License-GPL%20v2-blue.svg)

**MAME4droid (Current)** is an Android port of the **MAME 0.289** emulator. Developed by **David Valdeita (Seleuco)**, this project aims to provide a highly optimized, native Android experience for arcade and classic computer emulation. It supports over 40,000 ROMs and introduces several custom features and architectural improvements specifically tailored for modern mobile hardware.

---

## ✨ Notable Features

Beyond the standard MAME core, this version includes several native implementations designed to improve multiplayer, performance, and visual accuracy on Android devices.

### 🌐 Native NetPlay (Rollback & Lockstep)
Built from scratch as a native part of the app, allowing you to play over Wi-Fi or mobile data. No accounts, no relay servers, and nothing sitting between the two devices once they are talking.
* **Dual Sync Modes:** Features deterministic **Rollback** for near-zero input lag (ideal for fighting games and fast action) and **Lockstep** for games where rollback isn't feasible.
* **Smart Desync Recovery:** A real-time CRC detector warns you if the session drifts. A 1-tap "Resync" option recovers the game on the fly without having to disconnect. Network-aware frameskip and adaptive input delay handle latency spikes automatically.
* **Direct Connections (IPv6 / CGNAT Bypass):** Full IPv6 support allows direct peer-to-peer connections even over mobile networks (4G/5G) where traditional IPv4 NAT fails.
* **Zero Router Setup:** Includes a custom STUN client, automatic UPnP port mapping, and UDP hole punching. 
* **Easy Invites:** Tap 'Share' to send your connection details via any messaging app. The app automatically parses the IP address when the other player pastes the text.

### 📋 Public Rooms (Optional Matchmaking)
NetPlay could always connect two people who had already agreed to play. **Public Rooms** is the missing half: an opt-in board where hosts advertise a game they are waiting on, so you can find someone without swapping addresses by hand. It introduces the two players and then steps aside, because the match itself still runs directly between the devices and nothing about it passes through the server.
* **Room Codes & Private Rooms:** Every room gets a short shareable code (e.g. `K7M2QP4A`, with no I/L/O/U so it survives being read out loud), and can be locked with a PIN. The PIN never reaches the server, only a hash of it.
* **Connection Forecast:** Rooms are coloured green, amber or red from what STUN actually measured about both routers. A pairing that cannot work says so, instead of leaving you waiting for a minute.
* **ROM Check:** The app confirms you really have the game before joining, and names the missing file if you don't, rather than letting you break the host's room.

### 🚪 Drop-in NetPlay
Waiting for an opponent used to mean staring at a dialog. Tick **Start now, let anyone join** and simply play: your room stays on the board while you do, and when somebody claims it your machine's state is transferred over, so you carry on together from that exact moment. When they leave, you keep playing on your own as if NetPlay had never happened.
* **Rollback only.** The state transfer that makes drop-in possible does not exist in Lockstep.
* **Why it sometimes restarts the game.** NetPlay pins a set of things the instant a game boots (your saved settings and NVRAM, Lua plugins and cheats, the sound rate, the working copy of a CHD) and none of them can be applied to a game already running. Turning drop-in on mid-game therefore restarts it unless that game was itself started by NetPlay, and the app warns you before you commit. Ignoring it would not fail loudly: the two machines would simply drift apart over the following minutes, with no error to point at.

### 🔒 Privacy
MAME4droid is written by one person who never quite got over arcades, and it is given away. No ads, nothing to unlock, nothing to subscribe to, and no budget for servers either. Public Rooms is the compromise that makes finding an opponent possible at all: a tiny free instance with no database, which keeps nothing about you.

It is switched off until you say otherwise and can be turned off at any time; with it off, the app never contacts the board and manual NetPlay works exactly as before. There are no accounts, no logins and no advertising identifiers. Your address is held in memory only while your room is up, is handed only to the player who joins you, and is **never written to a log**: the session records kept contain no address at all, and are discarded within two months. There is no chat. The [board server](MAME4droid-lobby/) is open source too, so you can point the app at your own instead. Full details in the [privacy policy](PRIVACY_POLICY.md).

### ⚡ Performance & Android Integration
Designed to run demanding titles smoothly on mobile hardware:
* **ADPF & Frame Pacing:** Integrated Android Dynamic Performance Framework (ADPF) to manage thermal states and CPU/GPU scaling dynamically, paired with accurate frame pacing for a smoother experience.
* **arm64 DRC (Dynamic Recompiler):** Includes a dedicated 64-bit ARMv8 recompiler, providing a significant performance boost for heavy 90s 2D and 3D hardware (e.g., CPS-3, Killer Instinct).
* **SAF Persistent Cache:** A concurrent persistent cache with lazy loading heavily improves boot times, specifically when reading large ROM sets via Android's Scoped Storage.

### 📺 Graphics & Visuals
A rebuilt GLES graphics pipeline focused on display accuracy:
* **Physical CRT Vector Simulation:** A custom, pure GPU-based vector rendering engine that physically simulates optical bloom and phosphor persistence for classic vector monitors.
* **HDR Rendering:** True FP16 HDR rendering path to utilize peak panel brightness on modern OLED/AMOLED displays, including SDR-to-HDR highlight expansion (Inverse Tone Mapping) for raster games.
* **Typography & Shaders:** Crisp native system font rendering for the OSD, combined with customizable CRT overlay filters, scanlines, and advanced shader support.

---

## 🛠️ Recommended Setup (External Storage)

To save internal space and ensure fast boot times, it is highly recommended to store your ROMs on external storage using Android's **Scoped Storage** system.

### First-Time Installation
1. **Prepare your storage**: Create a folder named `MAME4droid` (or any name you prefer) on your SD card or internal memory.
2. **Initial Launch**: Open the app for the first time.
3. **Path Selection**: When asked for your ROMs location, select **EXTERNAL STORAGE**.
4. **Grant Permission**: The Android file picker will open. Navigate to your created folder, tap **"Use this folder"**, and allow access.
5. **Romset Version**: Ensure you are using the **0.289 romset** for proper compatibility.

*Note: You can change the path anytime in **Options > Settings > General > Change ROMs path**.*

---

## 🎮 Controls & UI
* **Gamepads:** Plug-and-play support for Bluetooth/USB gamepads with analog support.
* **Touch & Sensors:** Customizable on-screen button layouts (1 to 6 buttons), touch lightgun support, autorotation, and tilt-sensor support.
* **Haptics:** Fine-tuned, smarter haptic feedback for UI and gameplay.
* **i18n:** Fully internationalized interface.

---

## ⚠️ Important Disclaimers

* **No ROMs Included**: MAME4droid is strictly an EMULATOR and DOES NOT INCLUDE ROMS or copyrighted material.
* **Hardware Requirements**: Despite optimizations, MAME is a demanding emulator. Late-90s 3D arcade games require a high-end Android device to run at full speed.
* **Not Affiliated with MAMEDev**: This port is an independent project and is not officially supported by the mainline MAMEDev team.
* **Support**: Due to the massive library of supported titles, I cannot provide support for specific individual games.

---

## 📜 License

Copyright (C) 1997-2026 MAMEDev and contributors.

This program is free software; you can redistribute it and/or modify it under the terms of the **GNU General Public License** as published by the Free Software Foundation; either version 2 of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
