# Changelog

## 0.7.0

A full review of the launcher for safety, crashes and performance.

**Your PC and files**
- Uninstalling with "delete mods and settings" deletes only the launcher's own files and folders, never anything
  else in its data folder.
- Uninstalling now really removes the install folder (it was left behind).
- Read-only files (a local mod copied from a git clone) no longer stop mods updating, the launcher rebuilding its
  BepInEx, FREE UP SPACE or uninstalling. Deleting never follows a folder link out of the launcher's folders.
- If another program (antivirus, OneDrive) has settings.json or library.json open, the launcher waits for it or
  stops with a message, instead of starting from empty files and saving those over yours. Saves are flushed to disk.
- Mod zips can't unpack more than 2 GB or 20,000 files, and download file names from mod lists can't point outside
  the launcher's folder.
- The loader in the game folder is added in an order that always leaves it marked as the launcher's, and removing it
  leaves alone a .doorstop_version the launcher didn't add.
- A voidcrewmod:// link only ever passes the link: the launcher ignores anything else on its command line then. The
  self-update only replaces the launcher's own exe.

**The game connection**
- The launcher and the game prove to each other that they know this install's secret without sending it, so another
  program on the PC can't take the launcher's place, and the game moves on from a port where something else answers.
- A join from the launcher is refused while you're on a mission; leave it in the game first.
- Update notices go into the chat one line at a time, and the same one never twice.
- Less work in the game while it lists lobbies; lobby details that change (roles, quests, mods) now show.

**Mods**
- A mod's GitHub release only counts when its files are named like the mod, and a GitHub version that fails its
  checks falls back to Thunderstore's instead of blocking the install.
- A download that stops sending data fails after 30 seconds instead of waiting forever.
- Thunderstore, GitHub and the website are asked with 20-second limits, and GitHub checks run side by side, so the
  launcher starts quickly when a site doesn't answer.
- With no internet, the game still starts with the BepInEx you already have.
- Lobbies listing many mods can't flood GitHub or Thunderstore with lookups.

**The launcher**
- Clicking a voidcrewmod:// link while the launcher is open no longer leaves a stuck launcher behind.
- Links are handled one at a time and wait for the launcher to finish what it's doing; turning on a switched-off
  mod from a link asks first.
- CHECK FOR UPDATES and closing the window wait for, or ask about, work in progress.
- Turning a mod off goes through the same checks as turning it on.
- READMEs that are huge or built to be slow can't freeze the launcher; an unclosed code block no longer hides the rest.
- Briefly turning off a mod no longer lowers your saved max players for good.
- Fewer redraws while the server browser looks up lobby mods, and the game is no longer searched for every second.

## 0.6.2

- Mods from voidcrewmod.com show their README from the site in MODS (it was only read from Thunderstore).

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
