# Changelog

## 0.9.3

- **Void Crew VR includes UUVR:** from version 4.0.0 the VR mod (Void Crew VR UI Fix on Thunderstore) starts VR itself, so the launcher no longer installs UUVR for it, and leaves UUVR out of its launches, your copy from Rai Pal too, as both would start VR. Older versions of the VR mod still get UUVR as before.

## 0.9.2

- **Join and host errors from the game are shown in your language** (older launchers still get the English text), and hosting tells you straight away when Void Crew couldn't open the lobby, instead of after 45 seconds.
- **The server browser keeps your place:** your selected lobby, your place in the list and the keyboard focus stay while new lobby lists come in, and an open WHO NEEDS IT list on the HOST tab stays open.
- At the window's smallest size, lobby names show again, and long words in the lobby list, INSTALLED and MODS end in "…" instead of being cut off.
- A mod added from your PC now loads the Thunderstore mods its manifest.json needs, when you play, host or join.
- With both Void Manager packages turned on, only the newer one is loaded and listed in your lobby, as BepInEx would load anyway; both stay on in INSTALLED.
- JOIN from your hub or a mission says, in orange, that Void Crew restarts and leaves the game you're in, as HOST LOBBY does.
- FIX AND REJOIN no longer leaves out all your mods when the game names a mod without a usable name, or every mod whose name merely ends the same; a mod the join already loads is listed as loaded, not "not found on Thunderstore".
- The server browser checks lobbies' GitHub versions with one request per mod, so it no longer uses up GitHub's hourly limit.
- A lobby with more mods than its list has room for lists the ones everyone needs first and says it is full; joining one also loads the Void Manager "All" mods it had no room for.
- A mod whose Void Crew listing Thunderstore rejected comes into a join only from its author's GitHub release (for authors the launcher knows), never with Thunderstore's files, and the JOIN window says where it comes from.
- A lobby whose host you've blocked leaves the list as soon as the game knows, and host names fill in.
- The launcher's helper now loads before your other mods, so a lobby you host reliably lists the mods whose missions or items the game requires.
- Uninstalling asks you to close the launcher and Void Crew first, and lists anything it couldn't delete.
- After LATER on a launcher update, the status bar still says it's out once the mod list has loaded; the start-up update check gives up after 20 seconds, and a launcher update that stops downloading fails after 30 seconds instead of waiting forever.
- UPDATE ALL and CHECK FOR UPDATES say your mods are up to date only when they are.
- Big downloads update the progress bar about ten times a second and no longer slow the window.
- Typing a lobby name, dragging the crew size or searching lobbies no longer writes the settings file on every key; if another program holds that file, the status bar says so.
- A HOST tab choice is no longer lost after a restart when the mod's id differs in capital letters, and a mod link the launcher was started with no longer asks again after a language change.
- READMEs built to be huge show as plain text instead of freezing the window, and a mod's website link only ever opens a web page.
- The ••• menu of a mod works with a right click too, and the launcher no longer jumps in front of the game when their connection drops and comes back while you browse.
- An error that keeps happening shows one message, not a stack of them, and a launcher that fails while starting closes instead of staying open unseen.
- voidcrewmod:// links only go to a launcher run by your own Windows user, and error messages you read in your language are written in English in the launcher's log.

## 0.9.1

- **Fixed: after you quit Void Crew, the launcher said the game had started without its mods.** The game drops its connection to the launcher a moment before it finishes closing. The top bar now says CLOSING until it's gone, the server browser no longer offers RESTART, and the launcher log no longer gets a "never connected" report for a game that did connect.
- **Fixed: joining from a running game kept mods the JOIN dialog said were off**, and Void Manager then threw you out of unmodded lobbies without saying why. The launcher now restarts Void Crew whenever the running game has a mod the join doesn't load.
- Fixed: hosting from a game that was already running listed each mod's "who needs it" as it was when the game started. A change on the HOST tab now restarts Void Crew before your lobby opens.
- Lobbies you host with Void Crew Fleet can now be set to up to 48 players on the HOST tab (Fleet's own MaxPlayers setting, 24 unless you raise it, still caps the lobby). With Max_Players it goes up to 24, and its current package (NihilityShift) is recognised.
- When a lobby turns you away because the mods don't match, only the window with FIX AND REJOIN opens, without a second "Couldn't join" message under it.
- ADD MOD FILE, IMPORT FROM BEPINEX, removing a mod, FREE UP SPACE and the SETTINGS tab no longer freeze the window with big mods or a slow disk. The size of your downloaded mods shows once it has been counted.
- Pressing Enter no longer picks FORCE CLOSE or DELETE THEM: it waits for the game, and keeps your mods when you uninstall.
- Double-clicking the lobby list's scroll bar or the empty space under the lobbies no longer opens JOIN.
- The server browser reads other players' lobby details within limits (size, number of mods, length of names) and without hidden characters that can flip or break names, so a lobby can't slow the launcher down or garble its list or log.
- A mod file Windows won't let the launcher read no longer stops it starting.

## 0.9.0

- **Releases are built by GitHub from this repository**, ready for code signing through SignPath (see the code signing policy in the README), so Windows can show who made the setup instead of warning about an unknown app.
- The setup says where it installs and what it adds (shortcuts, voidcrewmod:// links) and waits for INSTALL before changing anything.
- An old BepInEx loader (Doorstop 3) from another install is replaced after one REPLACE AND CONTINUE, and removing the launcher's loader puts the old one back, so a manual BepInEx install works again.

## 0.8.2

- **Local mods in multiplayer:** when the host plays with mods added from their PC (Jack's role and weapon zips, say), players who have the same mods now join with their own copies. The launcher finds them by their plugin, whatever the files are called on each PC and even when they're switched off, and turns them on for that game. Without one, JOIN says which mod (with its plugin id) and that it can't be downloaded: get it from the host or its author.
- **Mods players are checked for are always listed as needed:** Void Manager "All" mods, Void Manager itself while one of those is on, and mods whose missions or items the game requires of every player (Warzone Mission Type) are listed for everyone in your lobby, whatever the HOST tab says, so other launchers set them up before joining. Before, a host with such a mod on Auto (or set to less) let players join from the launcher without it, and the game then said they didn't have the required mods. When the launcher overrides your choice, the row says "Players can't join without it".
- **Auto follows what each mod tells Void Manager**, read from the mod itself (Speedometer just you, Improved Scoop host only), and remembers the mods the game required when you last hosted.
- Your own Void Manager "All" mods stay off when you join a lobby that doesn't run them (Void Manager would turn you away).
- When a host on an older launcher didn't list a mod the game needs, FIX AND REJOIN also loads the host's other mods.
- Works with lobbies hosted from older launchers: their local mods are matched through Void Manager's mod list or by name.

## 0.8.1

- A new launcher version is no longer installed straight away: a window says it is out, with UPDATE and LATER. LATER asks again next time the launcher starts; CHECK FOR UPDATES on the SETTINGS tab installs it any time.

## 0.8.0

- **The launcher speaks 12 languages**, the same as Void Crew: English, French, German, Spanish, Italian, Polish, Portuguese (Brazil), Russian, Ukrainian, Chinese (Simplified), Japanese and Korean. It starts in your Windows language (English when that isn't one of them); LANGUAGE on the SETTINGS tab changes it and restarts the launcher. Mod names and descriptions stay as their authors wrote them, and the launcher's log stays in English.

## 0.7.4

- **Fixed: the game started without mods when its folder had an old BepInEx loader** (Doorstop 3, from a manual BepInEx install). The launcher now puts its own loader in its place by itself when you start the game; the old files are kept next to it with .old-doorstop on the end.

## 0.7.3

- When Void Crew runs after a launch but never connects, the launcher writes what it can see about why to its log (the command line the game got, the loader in the game folder, whether BepInEx ran), and says so in the status bar.

## 0.7.2

- Fixed: with a space in your Windows user name, Void Crew started without mods and the launcher never saw it (no lobbies). Steam drops the quotes around paths it passes to the game, so the launcher now gives it the short form of the path, or starts the game directly with the path in quotes.

## 0.7.1

- Joining a lobby finds mods whose Void Crew listing Thunderstore left out of its list (it rejected Ship Customizer's), through the mod's GitHub page, instead of showing CAN'T GET.

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
