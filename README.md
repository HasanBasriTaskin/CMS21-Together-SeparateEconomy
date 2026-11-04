# Together Separate Economy

A MelonLoader mod for **Car Mechanic Simulator 2021** that works on top of the CMS21-Together co-op mod. Each player gets their **own money, inventory and warehouse** instead of sharing them.

> **Status: experimental.** The code builds, but it has **not yet been tested in a full 2-player session**. See [Known issues](#known-issues). Back up your saves.

## What it does

- Every player in a CMS21-Together session has a private **money balance, inventory and warehouse**.
- The **host stores the clients' records** on disk (`Records\save_<n>\<guid>.tse`, with rolling backups). The host's own money and inventory stay in the normal game save.
- **One-time split for an existing co-op save:** when a player who already played in the shared session joins and has no record yet, the host's current money is split in half and a copy of the host inventory is given to that player (items get fresh UIDs). Details and safety rules below.
- **New players** (never seen before) start with an empty inventory and configurable money and level (`NewPlayerMoney`, `NewPlayerLevel`).
- **Still shared:** scrap, XP/level progression as handled by Together, cars, jobs, garage upgrades, tools and quests.

## Requirements

- Car Mechanic Simulator 2021 **1.0.40**
- [MelonLoader](https://github.com/LavaGang/MelonLoader) **0.5.7**
- CMS21-Together **0.4.16hf3** by Fozkais ([CMS21-Together on GitHub](https://github.com/Fozkais/CMS21-Together))
- FixForTogether is **optional** and supported (the mod ignores its inventory sync packets 68 and 200 while separated).

## Installation

1. Copy `TogetherSeparateEconomy.dll` into `Car Mechanic Simulator 2021\Mods\` on **every player's PC** (host and all clients).
2. All players must run the **same mod version**; a client with a different version is refused by the host and stays on shared sync.
3. Start the game (see [Known issues](#known-issues) for the launcher batch), host or join a CMS21-Together session as usual.

## Configuration

File: `Mods\TogetherSeparateEconomy\TogetherSeparateEconomy.cfg` (created on first start). Category `TogetherSeparateEconomy`.

| Entry | Default | Description |
|---|---|---|
| `SeparateInventory` | `true` | Each player has their own inventory |
| `SeparateWarehouse` | `true` | Each player has their own warehouse |
| `SeparateMoney` | `true` | Each player has their own money |
| `SnapshotIntervalSeconds` | `60` | Automatic snapshot interval in seconds (minimum 5) |
| `BackupCount` | `30` | Rolling backups kept per player on the host (the very first backup is always kept) |
| `NewPlayerMoney` | `15000` | Starting money of a brand-new player |
| `NewPlayerLevel` | `5` | Starting level of a brand-new player |
| `DebugLog` | `false` | Verbose logging |
| `StartupCrashGuard` | `true` | Reset an invalid `selectedProfile` value to a vanilla slot |
| `Language` | `en` | Language of on-screen popups: `en` or `tr` |

The three `Separate*` switches are decided by the **host**: clients use the host's values for the session.

## Where files are stored

All under `Car Mechanic Simulator 2021\Mods\TogetherSeparateEconomy\`:

- `TogetherSeparateEconomy.cfg`: settings.
- `Records\save_<n>\<guid>.tse` (host): one record per player. `Records\save_<n>\backups\` holds the rolling backups, `Records\save_<n>\before-first-split\<timestamp>\` the profile backup taken before a first split.
- `LocalBackup\save_<n>_<guid>.tse` (client): the client's own local copy (last 5 versions).

Files are written to a `.tmp` file first and then moved into place, so an interrupted write cannot corrupt a record.

## How the first split works

1. A client joins, completes the handshake and asks the host for its record.
2. If the host has no record and the player is a known Together player (already in the shared save), the host takes a **backup of the game profile and the Together save** into `before-first-split\<timestamp>\`. If the backup fails, the split is cancelled.
3. The host sends the client half of its current money and a copy of its inventory.
4. The client applies the record and sends an acknowledgement.
5. **Only after that acknowledgement** is the host's money reduced by the same amount and the host game saved.

Safety rules: if the acknowledgement does not arrive, the client leaves, the connection drops, or 120 seconds pass, the host's money is **not** changed and the split is retried on the next join. If the client cannot apply the record, it falls back to Together's normal shared behaviour.

## Uninstall

Delete `Mods\TogetherSeparateEconomy.dll`. Together's shared behaviour comes back. Optionally delete the `Mods\TogetherSeparateEconomy\` folder (it contains the records; back it up first if you may want them again).

## Known issues

- **Experimental:** not yet tested in a full 2-player session. Expect rough edges and keep backups.
- **Startup crash with Together's slot 4:** CMS21-Together stores its extra save slot (4) in the game's `selectedProfile` setting. The vanilla game only has slots 0-3, so if the game was last closed with that slot selected, it can crash on startup. The value is stored in the registry as `selectedProfile_h2169348711` under `HKCU\Software\Red Dot Games\Car Mechanic Simulator 2021`. Setting it to `0` fixes the crash. The mod's `StartupCrashGuard` tries to do this automatically, and `tools/StartCMS21.bat` does it before launching the game through Steam (`steam://rungameid/1190000`).

## Building and releasing

The mod references game assemblies (`MelonLoader\Managed\*.dll`, `Assembly-CSharp*.dll`) and `CMS21-Together.dll`. These must not be committed or downloaded in CI, so **GitHub Actions only runs repository checks** (privacy scan, no binaries tracked, markdown links, release asset guard); it never compiles the mod. Builds are done locally.

Requirements: .NET SDK (the project targets `net472`; reference assemblies come from NuGet) and a local game install with MelonLoader and CMS21-Together.

```
dotnet build -c Release
dotnet build -c Release -p:GameDir="D:\Games\Car Mechanic Simulator 2021"
```

`GameDir` defaults to `C:\Program Files (x86)\Steam\steamapps\common\Car Mechanic Simulator 2021`. To set it without editing the project, create an untracked `GameDir.props.user` next to the csproj:

```xml
<Project>
  <PropertyGroup>
    <GameDir>D:\Games\Car Mechanic Simulator 2021</GameDir>
  </PropertyGroup>
</Project>
```

The output is `bin\Release\net472\TogetherSeparateEconomy.dll`.

To prepare a release run `powershell -ExecutionPolicy Bypass -File tools\release.ps1`. It builds, reads the full version (`1.0.0+<build stamp>`) from the DLL, and creates in `dist\` the zip `TogetherSeparateEconomy-<version>.zip` (DLL under `Mods\`, launcher batch, README, LICENSE, CHANGELOG), the raw DLL and `SHA256SUMS.txt`. It then prints a `gh release create ... --draft` command for you to review and run yourself; it does not publish anything. When a release is published, the `release-guard` workflow fails if `TogetherSeparateEconomy.dll` or `SHA256SUMS.txt` is missing from the assets.

## Credits

- **CMS21-Together** by Fozkais, the co-op mod this one builds on.
- **FixForTogether** and its authors, whose packets and late-join behaviour this mod stays compatible with.
- MelonLoader and Harmony.

## Disclaimer

This is an unofficial fan project, **not affiliated with Red Dot Games** or the CMS21-Together authors. Use at your own risk and **back up your saves** before using it. No game files are distributed here.

License: [MIT](LICENSE).

---

## Türkçe özet

**Together Separate Economy**, Car Mechanic Simulator 2021 için CMS21-Together (ortak oyun) ile çalışan bir MelonLoader modudur. Her oyuncuya kendi **parasını, envanterini ve deposunu** verir; arabalar, işler, garaj geliştirmeleri, hurda ve XP ortak kalır.

- **Kurulum:** `TogetherSeparateEconomy.dll` dosyasını `Car Mechanic Simulator 2021\Mods\` klasörüne **tüm oyuncuların bilgisayarına** kopyalayın; sürümler aynı olmalıdır. Gereksinimler: CMS21 1.0.40, MelonLoader 0.5.7, CMS21-Together 0.4.16hf3.
- **Dil:** ayar dosyasında `Language = tr` yaparsanız ekran mesajları Türkçe olur (loglar hep İngilizcedir).
- **İlk bölüşme:** Together'da daha önce oynamış bir oyuncu ilk kez katılınca host'un parası ikiye bölünür ve envanter kopyalanır. Host'un parası yalnızca istemci "yükledim" onayını verdikten sonra düşer; öncesinde profil yedeği alınır.
- **Bilinen sorun:** Deneysel; tam 2 kişilik oturumda henüz denenmedi. Together'ın 4. kayıt yuvası `selectedProfile` değerine yazıldığı için oyun açılışta çökebilir; `HKCU\Software\Red Dot Games\Car Mechanic Simulator 2021` altındaki `selectedProfile_h2169348711` değerini 0 yapmak çözer. `tools\StartCMS21.bat` bunu yapıp oyunu Steam üzerinden başlatır.
- **Kaldırma:** `Mods\TogetherSeparateEconomy.dll` dosyasını silin.
- Resmi bir proje değildir; Red Dot Games ile bağlantısı yoktur. Oynamadan önce kayıtlarınızı yedekleyin.
