# Void Crew Mod Launcher

See every public **Void Crew** lobby **with the mods it runs**, and press **JOIN**: the launcher downloads that lobby's
mods from Thunderstore, starts the game with exactly those mods and joins the lobby for you. No more guessing which
mods a lobby needs.

It is also a mod manager (your mods with on/off switches, and every Void Crew mod on Thunderstore) and hosts modded
lobbies in one click.

![Server browser](https://raw.githubusercontent.com/CaptainChunck/VoidCrewModLauncher/main/docs/screenshots/server-browser.png)

## Install

1. Download this package (**Manual Download**) and unzip it.
2. Double-click **`VoidCrewModLauncher-Setup.exe`**. It installs itself (Start menu and desktop shortcuts) and opens.

Windows may warn about an unrecognised app the first time: choose **More info**, then **Run anyway**.

This is a Windows program, not a BepInEx plugin: install it as above, not through r2modman or Gale. It finds Void
Crew through Steam and downloads BepInEx for you.

## What it does

- **Server browser**: the public lobbies of your region with host, crew, hub or mission, ship, and the mods each one
  runs (from lobbies hosted with VoidManager or with this launcher), what joining players need, and whether you have
  them. JOIN installs what's missing, restarts the game with the lobby's mods (your VR and other client-side mods stay
  on) and joins.
- **Mod manager**: INSTALLED with on/off switches, updates and import from another BepInEx folder (Rai Pal, r2modman);
  MODS with every Void Crew mod on Thunderstore.
- **Host lobby**: name, public or private, crew size, crossplay; starts the game straight into your hub with your mods.
  Players who join through the launcher get your mods automatically.
- **Updates**: the launcher updates itself from GitHub and keeps your mods on their newest versions, from Thunderstore
  or, when a mod's GitHub releases are newer, from GitHub (SETTINGS > UPDATES). While you play, a quiet line in your
  own chat tells you when one of the game's mods has an update. Installing the Void Crew VR UI Fix also installs
  UUVR from its GitHub releases.

The lobby list comes from your own game: start Void Crew from the launcher and the list fills once the main menu is up.
Your Void Crew folder only gets BepInEx's loader (after asking, and switched off when you start the game from Steam).
The launcher turns on the game's "Can join [mod_session] games" setting so modded lobbies can be joined.

## Uninstall

Windows **Settings > Apps > Installed apps > Void Crew Mod Launcher > Uninstall**.

Source, screenshots and bug reports: [GitHub](https://github.com/CaptainChunck/VoidCrewModLauncher).
