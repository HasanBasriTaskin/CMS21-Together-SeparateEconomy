# Changelog

## 1.0.0

First public release (experimental).

- Per-player money, inventory and warehouse for CMS21-Together co-op sessions.
- Host stores each client's record in `Mods\TogetherSeparateEconomy\Records\save_<n>\<guid>.tse`, with rolling backups.
- One-time split of an existing co-op save: host money is halved and the host inventory is copied to the joining player; the host's money is only deducted after the client confirms, and the host profile is backed up first.
- New players start with configurable money and level (`NewPlayerMoney`, `NewPlayerLevel`).
- Clients keep a local backup (`LocalBackup\`) and upload snapshots to the host.
- Startup crash guard for an invalid `selectedProfile` value, plus `tools/StartCMS21.bat`.
- English log lines; popups localized (`Language` = `en` or `tr`).
- Optional support for FixForTogether (its inventory sync packets 68 and 200 are ignored while separated).
