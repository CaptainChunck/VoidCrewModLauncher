# Changelog

## 0.1.0

First release.

- **Server browser:** every public lobby of your region, read from your own game by the launcher's helper mod, with
  the mods each lobby runs (from the launcher's lobby advert or VoidManager's list), what joining players need, and
  whether you have them. Filters, search and a region switch. Lobbies the game wouldn't let you into (another
  platform without crossplay, a host you've blocked) aren't listed.
- **JOIN:** downloads the lobby's mods at its versions with their dependencies, restarts Void Crew with them and your
  client-side mods (only when the running game doesn't already fit), switches the game to the lobby's region if it
  started in another one, and joins. When the game turns you away for missing or disallowed mods, the launcher looks
  them up on Thunderstore and offers to fix the mods and join again.
- **Mod manager:** INSTALLED with on/off switches, updates, per-mod behaviour when joining lobbies, IMPORT FROM BEPINEX
  (Rai Pal, r2modman, manual installs) and ADD MOD FILE; MODS (Thunderstore) with search, categories, sorting and READMEs.
- **Host lobby:** name, public/private, crew size, crossplay; starts the game straight into your hub with your enabled
  mods and lists them in the lobby for other launcher users, with a per-mod choice of what joining players need.
- Looks like Void Crew's main menu and settings screens: dark backdrop with meander bands, the settings tabs, charcoal
  panels, thin pointed buttons, the game's toggles and diamond check boxes.
- BepInEx and all mods live in the launcher's folder; the game folder only gets BepInEx's loader (after asking, and
  switched off for normal Steam launches), and an existing loader or its settings are never touched.
- **One-click install:** `VoidCrewModLauncher-Setup.exe` installs itself for your Windows user (no admin rights) with
  Start menu and desktop shortcuts, and uninstalls from Windows' installed apps.
