# Changelog

## 0.6.1

- **Fixed: Void Crew crashed when "minimise the game while browsing" was on.** Minimised while it loads, Void Crew
  loses its graphics device and crashes. The launcher no longer minimises the game: the option is now KEEP THE
  LAUNCHER IN FRONT WHILE BROWSING, which brings the launcher in front of the game when it starts and again when its
  main menu comes up.

## 0.6.0

- **voidcrewmod.com first:** once the site's mod list is up, its mods come before Thunderstore's. For a mod on both,
  the site's details, versions and downloads are used (each download checked against the SHA-256 the site lists), and
  Thunderstore fills in the rest. Mods only on the site show in MODS with a WEBSITE badge. While the site is down or
  not live yet, Thunderstore covers everything as before.
- **WEBSITE button** at the top of the launcher opens voidcrewmod.com.

## 0.5.0

- **Install from a website:** voidcrewmod:// links open the launcher and install that mod, with what it needs, after
  you confirm. A link that arrives while the launcher is open goes to that window. The setup registers the links
  for your Windows user, and uninstalling removes them. Formats: `voidcrewmod://install/Namespace-Name` (newest
  version), `voidcrewmod://install/Namespace-Name/1.2.3`, `voidcrewmod://install/github-Raicuparta-UUVR` and
  `voidcrewmod://open`.
- Releases no longer include a Thunderstore zip.

## 0.4.0

- **UUVR comes with the VR UI Fix:** installing or turning on the Void Crew VR UI Fix also installs the newest UUVR
  (Universal Unity VR by Raicuparta) from its GitHub releases: the Mono "modern" build Void Crew needs, checked
  against GitHub's checksum and for UUVR's plugin. It then updates like your other mods, and loads whenever the VR UI
  Fix is on. If you already have UUVR (imported from Rai Pal, say), that copy is used instead of a second one.

## 0.3.0

- **Newer versions from GitHub:** for a mod whose Thunderstore page links its GitHub repo, the launcher also checks the
  repo's latest release. When that is newer than Thunderstore's version, the mod installs and updates from the release:
  its package zip (which must say it is that mod at that version, and match GitHub's checksum), or its plugin DLLs put
  into the Thunderstore files. Such versions show a GITHUB badge. Players joining a lobby whose host runs a GitHub
  version get the same release. SETTINGS > UPDATES has a switch for it.
- **Told in the game when a mod updates:** while Void Crew runs from the launcher, the launcher checks its mods every 15
  minutes. When one of them gets an update, one quiet line appears in your own text chat (only you see it) saying to
  restart Void Crew from the launcher. Nothing pops up, pauses or makes a sound, and each update is told once.
- GitHub's answers are kept and asked for again with their ETag, so the checks stay well inside GitHub's limits.

## 0.2.0

- **The launcher updates itself:** at start it looks for a new version on GitHub. When there is one, it downloads it
  (checked against the checksum GitHub lists), installs it and opens again, with a window saying so.
- **Mods update themselves:** at start and on REFRESH, installed Thunderstore mods update to their newest versions.
  SETTINGS > UPDATES has a switch for each, and CHECK FOR UPDATES looks right away.
- **Newest versions from Thunderstore:** Thunderstore caches its community list on its side, at times an hour or more
  behind, and a package can even be missing from it for a while. Installs, updates and joins now check each mod with
  Thunderstore directly, so a mod updated minutes ago installs at its new version. The list itself comes from
  Thunderstore's listing index, which it rebuilds more often.
- **Joining modded lobbies:** the launcher turns on Void Crew's "Can join [mod_session] games" setting, so modded
  lobbies can be joined from the launcher and from the game's own list.
- A lobby running a mod version the list doesn't have yet shows it as a download instead of "can't get".

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
