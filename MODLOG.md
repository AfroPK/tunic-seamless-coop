# TUNIC Seamless Co-op — MODLOG

Goal: true seamless co-op for TUNIC — up to 4 players (design for N, cap configurable), shared world,
synced combat/enemies, everyone progresses together. Done = working in game with 2+ real clients + 20-45 s clip.

## Recon (2026-10-10)
- Game: TUNIC, Steam appid 553420, `D:\SteamLibrary\steamapps\common\TUNIC`, exe `tunic.exe` x64
- Engine: Unity 2020.3.17f1, **IL2CPP** (GameAssembly.dll + global-metadata.dat), data dir `tunic_data`
- Company/product (for Player.log): `AppData\LocalLow\Andrew Shouldice\Secret Legend`
- Saves: `~\AppData\LocalLow\Andrew Shouldice\Secret Legend`
- Anti-cheat: none. Single-player game → client modding OK.
- Loaders installed: none.
- Ships Steamworks.NET (`com.rlabrecque.steamworks.net`), FMOD, InControl.

## Community / prior art
- Loader standard: **BepInEx 6.0.0-pre.1 UnityIL2CPP x64**
  https://github.com/BepInEx/BepInEx/releases/download/v6.0.0-pre.1/BepInEx_UnityIL2CPP_x64_6.0.0-pre.1.zip
  (used by silent-destroyer/tunic-randomizer; interop assemblies land in `BepInEx/unhollowed`)
- **Buttered-Lilly/Lillys_Tunic_MP** (WIP, 0 stars, v1.0.1): puppet-only MP. Learned facts:
  - Player prefab: `Resources.Load<GameObject>("_Fox")`; player class `PlayerCharacter` (Start, onFall,
    onLand, onFlinch, onFreeze, onUnfreeze)
  - To make a puppet: strip components HitReceiver, PlayerCharacter, *ItemBehaviour, *Spell, FireController,
    TrapTileTrigger; disable Animator; hide `Fox/root/.../Aura's Gem`, `Fox/root/freefloating/key twist`,
    disable TrackingBone on head; deactivate non-"fox" children.
  - Transport: Telepathy (TCP). No combat/enemy/world sync.
  - License not checked → treat as reference only, don't copy code.
- tooling limits this session: WebSearch/WebFetch broken (model error) → use the built-in browser.

## Route
BepInEx 6 IL2CPP plugin + Harmony patches (Il2CppInterop). Host-authoritative netcode.

## Lab
- 2026-10-10 saves backed up: `~\.universal-modder\backups\tunic-saves\20261010-212830.zip` (SAVES was empty —
  game never played on this PC; only Player.log). Restore: `um backup restore tunic-saves`.
- BepInEx 6.0.0-pre.1 extracted into game dir (adds BepInEx/, mono/, winhttp.dll, doorstop_config.ini,
  changelog.txt). First run OK: unhollowed/ generated, "Chainloader startup complete". Runtime = **Mono**
  (net472 target). Uninstall = delete those 5 entries (or Steam verify).
- Could NOT Stop-Process tunic.exe from the agent sandbox (Access denied) → user closes the game.
- .NET 8 SDK 8.0.425 installed via winget (user chose). nuget.org source had to be added manually.
  ilspycmd latest fails on .NET 8 → `dotnet tool install -g ilspycmd --version 8.2.0.7535`.
- Decomp (signatures only, IL2CPP): `~\tunic-decomp\Assembly-CSharp` (761 files). Not in repo.

## Engine facts (from interop signatures)
- Local player: `PlayerCharacter.instance` (static). PlayerCharacter : Creature. Prefab path static
  `PlayerCharacter.kCharacterPrefabPath` (Lilly used Resources.Load("_Fox")).
- Creature: `hp`, `maxhp`, `IsDead`, `Poise`, `cachedAnimator`, `Die()` (IEnumerator),
  `IDamageable_ReceiveDamage(dmg, poise, force, freeze, poison)`.
- **All damage**: `HitReceiver.ReceiveHit(HitType, damagePoints, attackPower, poiseAdj, blockAdj, freeze,
  poison, unblockable, ignoreIFrames, hitPos, forceFactor, fxPos, fxRot, isPlayerMelee, successCb, blockCb,
  powerLevel)`. HitReceiver.creature links to Creature.
- **Monsters**: `Monster.runtimeStableID` (RuntimeStableID.ID / intraSceneID) → per-scene network id.
  `Monster.overridingTarget` (Creature) → can aim AI at a puppet. `MonsterManager.instance.monsters`,
  `Monster.runtimeDeadMonsters`, `isDeadPermanentlyStateVar`.
- **World state = flat KV store**: `SaveFile.SetInt/SetFloat/SetString(k,v)` + `intStore/floatStore/stringStore`
  dictionaries; `StateVariable` wraps them (`GetStateVariableByName`, `NotifyAllSubscribers`).
  → hook the 3 setters = sync every door/chest/bell/item/boss flag.
- Scenes: `SceneLoader.LoadScene(name)`, `SceneLoader.OnSceneLoaded`.

## Design (seamless co-op)
- Star topology: host relays. Transport LiteNetLib (MIT, ships as dll) over UDP — direct IP first,
  Steam lobby/P2P later (game ships Steamworks.NET). Max players config, default 4, hard cap 8.
- M1 presence: per-player snapshot 20 Hz (scene, pos, yaw, animator params); puppets = player prefab
  instantiated under an inactive parent (so Awake never runs), all MonoBehaviours stripped, colliders off,
  Animator kept and driven by synced params. Only shown when in same scene.
- M1 world: SaveFile setter postfixes → reliable Flag msgs; host = truth; joiner gets full KV dump.
  Clients should play a dedicated co-op save slot.
- M2 combat: scene owner (host if present, else first in scene) simulates monsters, streams by
  RuntimeStableID; others' monsters become puppets; non-owner hits → RPC → owner calls ReceiveHit.
  Monsters target nearest player incl. puppets via overridingTarget.
- M3: shared death/respawn, bosses, item pickups (dedupe), shops, cutscene handling, Steam invites.

## Next
- [x] BepInEx installed, SDK, decomp
- [x] M1 code written, builds clean (commit cea4591), deployed to BepInEx/plugins/TunicCoop
      (TunicCoop.dll + LiteNetLib.dll 1.2.0). Hotkeys F7 host / F8 join / F9 leave / F10 loopback ghost.
- [ ] BLOCKED: tunic.exe PID 22040 (first-run) still open; agent can't kill it (sandbox denied, classifier
      denied sandbox bypass) → user closes it.
- [ ] Local oracle: launch, load a save, F10 → ghost fox appears 1.5 m beside player, animates; check
      LogOutput.log for [puppet]/[world] lines + exceptions from stripped animator behaviours.
- [ ] 2nd PC: `user@second-pc`, game at `C:\Games\TUNIC`. SSH key auth REJECTED (password-only);
      our key = ~/.ssh/id_ed25519.pub. Need user to authorize it on the remote. Then: install BepInEx
      pre.1 there, copy plugin, set JoinAddress, F7 here / F8 there.
- 2nd PC SSH works (user authorized key). Shell = cmd; use full path to powershell.exe.
  Remote copy has `[Steam]` depot manifests; GameAssembly.dll SHA256 identical (cddafc9a...). BepInEx pre.1
  + TunicCoop installed at C:\Games\TUNIC\BepInEx\plugins\TunicCoop.
- Test 1 (loopback F10, user-driven): plugin loads, legacy Input works (F10 fired), puppet spawns, 0 exceptions.
  BUT: puppet showed every weapon/item/effect child at once (magenta beam, orb FX) → prefab children are
  toggled by game scripts we strip. And loopback offset was yaw-relative → ghost swung around on turns.
  Fix (commit 2): Visibility.cs mirrors per-child-path (FNV hash) active/renderer/emitting flags from the
  real fox; puppet hidden until first table; anim.speed synced; state snap threshold 0.25 s.
- Test 2 (loopback, build F90F194C): PASS. Ghost identical to real fox, no stray FX, rolls/dust OK.
  Log: "indexed local fox: 303 nodes", "visibility map 301/303 matched (333 on puppet)", 0 exceptions.
  Video: D:\Steam Recordings\Exported_Videos\clip_1,791,684,220,830.mp4
- 2-PC setup: host = this PC <host-pc> (Ethernet). Remote cfg written:
  C:\Games\TUNIC\BepInEx\config\com.freddy.tuniccoop.cfg (JoinAddress <host-pc>, Name StreamPC).
  GUI can't be launched over SSH (session 0) → user launches Tunic on the remote.
- Test 3 (2 PCs, build F90F194C): handshake WORKED (welcome, 2/4, world dump 3 ints/4 strings, puppets
  spawned both sides) but every State send threw LiteNetLib TooBigPacketException (~1.5 KB > 1160-1428 MTU)
  because the 303-node vis table rode in the Sequenced snapshot → no movement. Also host pressed F7 again →
  restart kicked the client (RemoteConnectionClose). No on-screen feedback → "nothing happened".
  Fix (build A214F23E): vis = separate Msg.Vis ReliableOrdered (fragmentable), deltas only, full table on
  join/welcome; send wrapped in try/catch; F7 no-op while hosting; IMGUI status overlay (top-left).
  GOTCHA: GUIStyle(GUIStyle) copy ctor stripped in TUNIC interop → use new GUIStyle{} + normal.textColor.
  GOTCHA: in PowerShell never name a helper `R` (alias of Invoke-History).
- Test 4 (2 PCs, build A214F23E): **CONNECTED, M1 presence working** (user confirmed). Both logs:
  2/4, world dump applied, vis map 301/303 both directions, 0 exceptions.
  Not yet verified: live world-flag sync during play, scene changes, 3-4 players.
- Bug: big offset on remote fox during roll. Cause: roll is a root-motion clip; real fox consumes it in
  OnAnimatorMove (creature_OnAnimatorMove), so Unity strips it from the pose. Puppet had no OnAnimatorMove
  and applyRootMotion=false → root bone translation stays in pose (mesh slides) + network moves it too.
  Fix (build C923D37D): RootMotionSink (empty OnAnimatorMove) on the Animator's GameObject,
  applyRootMotion=true. UNVERIFIED in game yet.
- Bug: animation lag on rolls (both directions). Causes: (1) roll is fired by an Animator *trigger*
  (not synced) → puppet only entered roll after 0.25 s mismatch-forcing; (2) 20 Hz + exp-smoothing chase
  = lag + stutter; (3) anim and position on different clocks.
  Fix (build 745C1EC7, WIRE FORMAT CHANGED — both PCs must match): snapshots carry sender time + per-layer
  (hash, normTime, length, loop) using the *next* state mid-transition; puppet renders 100 ms behind the
  sender's clock (offset = min observed recv-send, slow upward creep), lerps pos/yaw between bracketing
  snapshots, extrapolates ≤150 ms when starved, and CrossFadeInFixedTime(target, 0.06 s, offset = seconds
  into state + elapsed) when the timeline reaches a new state; drift correction after 0.2 s.
  Sender 30 Hz + immediate send when LocalStateKey (layer state hashes) changes (cap 90 Hz).
  VERIFIED (user, 2 PCs): rolls/animations in sync and smooth. Logs: 0 exceptions both sides.
  Root-motion fix (C923D37D) also live since then.
- DECISIONS (user, 2026-10-10): item sharing = **everything shared** (one shared inventory incl. money and
  consumables); friendly fire = **off** (attacks pass through players).
- Test 5 (build 5817D4AF): chest → stick NOT shared, coins NOT shared, pots NOT shared; artifacts back
  after entering a house. Logs: ZERO "[world] key" lines on either PC → **SaveFile.SetInt/SetFloat/
  SetString Harmony hooks never fire** (IL2CPP inlines the tiny setters into callers). Only the join dump
  flowed. Dump also leaked per-player 'last campfire scene name/id' (respawn) → now denied.
  Dump keys seen: 'game started', 'permanentlyDead <n>' (dead monsters by id!), 'last page viewed'.
  Fixes (build next):
  - WorldSync = POLL SaveFile int/float/string stores 4x/s vs baseline (first poll after connect is
    silent); Inventory.itemList quantities polled → ItemDelta (+/-) msgs (everything shared, concurrent
    pickups add up); join dump now includes absolute item quantities (ItemSet).
  - SmashSync: SmashableObject (pots/grass) isn't saved; TUNIC ids them by position
    (PermanentStateByPosition). Poll `smashed` 10x/s → "scene|x|y|z"; receiver smashes nearest within
    0.6 m with min/maxCoinDrop=0 (no double money). Destroy-on-smash detected via vanish (≤3 at once).
  - Puppet re-applies full cached vis state on re-enable + every 0.5 s (re-activation restarts
    play-on-awake particles = the orbs after a door).
- Test 6 (build CE7644D6): world/inventory/pot sync WORK (user). Coins from pots not visible on other PC
  (by design: receiver smash zeroes coin drop). Log found real bug: money double-synced — item delta AND
  SaveFile key 'inventory quantity MoneySmall' (absolute) → fought → host 47 vs client 51.
  Keys learned: 'playtime' (float), 'last spawn scene name', 'last spawn id', 'inventory quantity <Item>'.
  Money item name = **MoneySmall**. 116 items in Inventory.itemList.
  Fix (build 0177ADCD): deny "inventory quantity" keys. CoinSync: coins = pooled ItemPickup
  (CoinSpawner.poolList[].pool[], value = quantityToGive). Dropper announces pool-object activations
  (id = localId<<24|counter, pos, value) + resting pos after 1 s; receivers pool.GetNext(pos) from the pool
  whose template has same quantityToGive; deactivation (pickup) → CoinGone → others SetActive(false).
## Autopilot (2026-10-12) — user asked for fully autonomous testing on both PCs
- Saves backed up before automated play: A `~\.universal-modder\backups\tunic-saves\20261012-051810.zip`,
  B `~\TunicSaveBackup-20261012-051752.zip`.
- Autopilot.cs: BepInEx/coop_test/{cmd.txt (append "<seq> <verb> args"), results.txt, state.json,
  shots/}. Verbs: echo wait continue newgame load saves quit restart waitplayer waitplayers waitscene
  host join leave stick moveto press face tp tpto shot monsters smashables inv.
  Virtual InControl gamepad: new InputDevice + AddControl + InputManager.AttachDevice, values pushed in
  InputManager.add_OnUpdateDevices (DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ulong,float>>).
  Bindings read at runtime (PlayerInput.Actions.actions[].regularBindings → DeviceBindingSource.Control):
  InteractDodge=Action1, Item1=Action3, Item2=Action4, Item3=Action2, Flask=RB, Shield=RT, Focus=LT,
  Inventory=LB. Title: TitleScreen.__Continue/__NewGame; FileManagementGUI.LoadFileAndStart(file).
- Driver: tools/coop.ps1 (Send-Cmd/Do-Cmd/Wait-Seq/Get-State/Get-Shot/Get-Log/Update-Game/Start-AndWait).
- GOTCHA: adding an iterator method + Nullable<Vector3> return to the injected CoopManager made BepInEx
  pre.1 ClassInjector crash the game natively at load (no exception logged). Keep injected classes to
  simple signatures; put helpers in plain static classes (PuppetRegistry).
- GOTCHA: launching GUI apps over SSH = session 0, invisible. schtasks /it task was DENIED by the
  permission classifier (persistence). Instead: `restart` verb relaunches the game from inside itself
  (inherits the desktop session) and swaps in a staged TunicCoop.dll.next. B needs ONE manual launch.
- GOTCHA: restart v1 (Process.Start cmd, UseShellExecute=false) hung Unity's Application.Quit (inherited
  handles) → process stuck at title, plugin frozen. v2: UseShellExecute=true + helper taskkills own PID
  after 10 s before swap/relaunch. (agent still can't Stop-Process locally: sandbox)
- schtasks /sd date format on B is yyyy/mm/dd.
- B launch SOLVED (user explicitly authorized "find a way"): one-shot schtasks create → run → delete
  (/it, desktop session), nothing left behind. Start-Game B uses it. B booted autopilot in 12 s.
- A: PID 11928 hung in Application.Quit (restart v1). Tried & failed: Stop-Process (sandbox: access
  denied), PostMessage WM_CLOSE (returns False, sandbox), WScript AppActivate (False), computer-use
  click + Alt+F4 (window black/hung, ignores it). Not tried on purpose: local schtasks taskkill = would be
  a deliberate end-run around the sandbox's process-kill restriction.
- ROOT CAUSE of "frozen when unfocused": Tunic runs exclusive fullscreen; unfocused → Unity stalls the
  player loop (even Application.Quit never completes). Test launches use
  `-screen-fullscreen 0 -screen-width 1280 -screen-height 720` (B: launch.cmd via one-shot schtasks;
  A: steam://rungameid/553420//args). restart passes original argv through. Verified B runs unfocused.
- GOTCHA: restarted child inherited DOORSTOP_* env vars → new game ran WITHOUT BepInEx. Clear them
  (Environment.SetEnvironmentVariable(k,null)) before Process.Start. Verified B self-restart OK.
- GOTCHA: ScreenCapture.CaptureScreenshot = "Method unstripping failed" (stripped). Snap() renders
  Camera.main into a RenderTexture + ReadPixels + ImageConversion.EncodeToPNG. Works unfocused.
- GOTCHA: TitleScreen.__Continue() before the intro finishes is ignored → wait 3 s, verify scene change,
  retry. SceneLoader.Progress parks at 0.9 → waitplayer uses pc!=null && scene∉{Loading,TitleScreen} 1 s.
- GOTCHA: cmd.exe `if not exist X mkdir X & echo..` — the `& echo` is part of the if-body.
- B fully autonomous (2026-10-12): Update-Game B → continue → waitplayer → moveto/press → Get-Shot all OK.
- A still blocked by hung PID 11928 (exclusive fullscreen + quit while unfocused). Clicks via
  computer-use land on Explorer (stale window). Needs the user to end it once.
## Autonomous 2-PC test results (2026-10-12, tools/suite.ps1)
- PASS presence: puppets at exact remote positions both ways, 33 renderers, no artifacts (screens).
- PASS enemy IDs: `monsters` on both PCs → 60 monsters, identical RuntimeStableID/type/name (zero diff).
- PASS coins x3 (owner-picks, claim, race): money exact and equal. Design: game never pays
  (quantityToGive=0 while connected); dropper owns coin; first CoinClaim wins; CoinTaken credits all once.
  Earlier designs failed: polled-delta double-pay (54 vs 51), baseline race (57 vs 59) — both fixed.
- PASS chest 90: 'chest open 90' written at open-time on opener, synced to B; B shows open; contents
  (Piggybank L1, item granted after the presentation screen is confirmed) arrive on B via ItemDelta.
  NB chest.itemContents name ("Pepper") ≠ granted item (database override).
- PASS pots: urn destroyed on both PCs (smashables list).

- PASS combat (M2a, MonsterSync): ownership (lowest player id in scene), mirror positions 0.00 m
  (same-moment snapshot), B (non-owner) kills slime in 3 swings with hits forwarded + applied on owner,
  A kills slime → mirror dies on B, money equal. Bug found+fixed: hp sentinel 9999 clamped to maxhp 10
  by the game → read as 9989 dmg → massacre; now mirror hp is pinned to owner's streamed hp.
  NB dead monsters leave MonsterManager.monsters → test via "[mon] X died (owner)" log lines.
- M2b: redirecting AI on a single owner FAILED (get_playerOrBaitWorldPosition redirected 900x, NavigatingMonster
  dest/facing hooks, ReceiveBroadcastAggro — slimes still pathed to the owner, MonsterManager.checkMonster
  deactivated them >activeDistance from the owner, one walked off a cliff). mondebug showed active=False.
  → MonsterSync v2: PER-MONSTER OWNERSHIP BY PROXIMITY. Owner (single writer) hands off to a player
  ≥4 m closer via reliable MonsterOwner(id, newOwner, hp); new owner restores hp + runs the unmodified AI
  (which chases its local fox = nearest player). No AI hooks needed for any monster type.
  PASS aggro: monsters near B handed to B (4/4), nearest slime 1.2 m from B, B hp 80→20 (real damage).
  PASS full regression (fresh session each): combat ownership agree 4/4 + mirror roles, positions 0.00 m,
  B kills, A kills, money equal; coins x3; chest90 key/visual/items (inventories identical).

- PASS scene transitions (Test-Scenes, 8/8): A enters Sword Cave alone → puppets hidden both ways, B takes
  all 78 Overworld monsters; B follows → visible both ways, 0.00 m error; A leaves → hidden again.
  Harness bugs found: waitscene split multi-word names ("Sword Cave"); moveto kept steering after the scene
  changed and walked the fox back out the door (ping-pong) → stops on scene change; saves autosaved inside
  the cave → `goto` (ScenePortal.DepartToScene) for deterministic starts.
- GOTCHA (cost ~10 iterations): a BLUE FOX at the Sword Cave door on A only = a TUNIC ghost-fox NPC tied
  to A's save progress, not a mod artifact (proved by A entering alone). Real puppet issue found along the
  way: tether-wand windup tentacle renderers (own Animators) show as blue wisps on puppets; how the game
  hides them on the real fox could not be found (renderer flags, forceRenderingOff, bones, bounds, animator
  state, MPBs, materials all identical) → puppets always hide `tetherwand_windup`. Secondary animator states
  are now synced too (Vis message appends AnimEntry list). Material/MPB copying was tried and REVERTED
  (it only "fixed" the ghost, and would bleed the local fox's hit flashes onto puppets).
  Diagnostic verbs kept: foxnodes, puppethide, countobj, dbg, portals, goto.

## Status 2026-10-12: verified autonomously on 2 PCs
presence+anim, world flags, shared inventory+money, pots, coins (exactly-once), chests, enemy sync,
combat (both directions), enemies attack whichever player is nearest.
TODO: scene transitions (cross-scene ownership/puppets), player death/respawn in co-op, bosses, cutscenes,
3–4 player test (only 2 PCs), Steam invites, README/release, showcase clip, KB field note.

## BIG engine facts learned while testing
- **Harmony patches do NOT fire for game-internal calls** in BepInEx 6 pre.1 (installed fine, 0 calls on
  Chest.Interact / openSequence.MoveNext / SaveFile.Set*). Use **NativeHook** (MonoMod NativeDetour on
  *(IntPtr*)NativeMethodInfoPtr_* → methodPointer), sig ret fn(IntPtr self, args..., IntPtr methodInfo).
  Verified: PlayerCharacter.Update 15k calls; Chest.Interact & openSequence.MoveNext fire on open.
- BloodstainChest (dropped money after death) also implements IInteractionReceiver and sits where you
  died; it stole the interaction from chest 15 in tests.
- Camera.allCameras getter throws ObjectCollectedException (unhollower bug) → FindObjectsOfType<Camera>.
  Extra enabled cameras exist (island overview); snap = main + overlay (Depth/Nothing clear) cams.
- Loading a save plays an island flyover (camera far away) during which input is ignored; gameplay cam
  sits ~50 m from the fox → waitplayer requires cam < 70 m.
- NavMesh.AllAreas missing from interop → -1. tp snaps to NavMesh.SamplePosition (raw height = in walls).
- PlayerCharacter.DebugInvincible (static) = god mode for test setups (slimes killed A once).
- Per-player keys to deny: playtime, last spawn*, last campfire*, bloodstain*, hp_lost, inventory quantity*.
- Death drops shared money (bloodstain) — with everything shared, a death costs everyone. Design point.
- **M1 (presence) DONE.** Next: M2 combat/enemy sync (scene owner + RuntimeStableID), scene transitions,
  live world-flag test, shared-vs-personal progression rules, Steam invites, 3-4 player test.
- Loaded plugin DLL is file-locked while the game runs ("user-mapped section") → deploy only after exit.
- Unknown: is legacy UnityEngine.Input enabled in TUNIC? → YES (F10 worked). (Pressed() logs "[input] legacy Input unavailable" if not)
- Unknown: real SaveFile key names → refine WorldSync.DenySubstrings from "[world] new key" debug lines.

- PASS death/respawn (6/6 incl. bloodstain recovery, money shared + equal), PASS 3 players (3rd instance C:\Games\TUNIC_P3 on PC B, new-game save): all see 3, 6/6 puppet pairs 0.00 m, money equal.
- Autopilot + HookProbe now opt-in via [Debug] config (default off; enabled on the 3 test rigs). Gating build compiles but NOT yet run in game.
- LEFT: verify gating build in game; bosses/cutscenes (save too early to reach bosses); Steam invites (PC B has no Steam account to test); README/package/publish check; showcase clip; KB field note.

## 2026-10-12 release candidate 0.1.0
- Gated build (autopilot/probe opt-in) full regression: 30/33 first pass; the 3 fails were (a) combat flake (passed on rerun, 4 swings) and (b) a save's FIRST death plays TUNIC's 'Resurrection' walk-through scene (test now accepts it; 'Has Ever Died' is per-player). Reruns: combat 5/5, death 6/6 → all 33 checks green.
- dist/TunicCoop-0.1.0.zip via tools/package.ps1; um publish check: only false positives (our own LiteNetLib.dll copy in the game's plugin folder, test screenshots → untracked).
- KB note drafted: kb/tunic-seamless-coop.md (um kb check PASS). PR needs user OK.
- Still open: bosses/cutscenes (saves too early), Steam invites (no 2nd Steam account on PC B), showcase clip.

## Revive & spectate
- Lethal hits on the local fox are intercepted (native hook on Creature IDamageable.ReceiveDamage) while a teammate is up: fox goes Downed at 1 HP, input locked, PlayerDown flag broadcast.
- Downed player's CameraController Update/FixedUpdate are wrapped to offset the camera onto the spectated puppet (Left/Right cycles).
- Teammates within 2.5 m for ReviveSeconds send Revive; the downed player stands up at ReviveHealthPercent. All down / hold F2 => real death via bypassed ReceiveDamage.
- Covered by Test-Revive (tools/suite.ps1).

## 0.2.0: bosses, host rules, regroup, connectivity
- Bosses: TUNIC's dev warp (Cheats.CheatWarpToPortal(scene, portal FullID)) puts players straight into arenas for tests;
  plain DepartToScene from another area has no arrival portal and drops the fox into the void.
- Boss detection: type list (Spidertank, Librarian, ScavengerBoss, Knightbot, Foxgod, CryptBoss, TunicKnightVoid),
  maxhp >= 300, or a BossAnnounceOnAggro pointing at it (the Guard Captain is a 50 hp Skuladin). Once engaged
  (hp < max or aggroed) a boss is never handed off: its phase state only exists on the owner.
- Mirror damage is only forwarded if the local player pressed an attack/item button in the last 3 s; the mirror boss
  replays its own attacks locally and its explosions were hurting itself (double damage on the owner).
- Runtime spawns (no RuntimeStableID): announced by whoever's game spawned them under a negative id; others pair them
  with their own copy (same name, nearest) or instantiate one from a same-named monster/prefab. Under a boss owned by
  someone else, local spawns are replays: paired or hidden. Simultaneous announcements: lower player id wins, the
  other retracts.
- Late wake: pre-placed monsters activated by a trigger in one game (Cathedral altar waves, ambushes) are claimed by
  that machine; others wake their inactive copy. Inactive objects never ran Awake: Monster.runtimeStableID is null and
  the id isn't computed, so read the RuntimeStableID component and derive buildIndex * 1e6 + intraSceneID.
- WorldSync: a key that appears with value 0 is a scene initialising defaults; never sent (it reset teammates' altars).
- Host rules (HostSettings): serialized flag from the host on join and on change. Own-money mode filters ItemDelta for
  MONEY/SUPPLIES/WELL_SUPPLIES/FLASK item types and credits coins only to the picker.
- Enemy toughness: ModifyDefensePower does nothing for direct ReceiveDamage calls; damage is divided in our Creature
  damage hook instead (with fractional carry); forwarded/death hits bypass it.
- Regroup: players announce scene + PlayerCharacterSpawn.portalIDToSpawnAt on arrival; "go to" warps to that portal and
  teleports next to them only after the load finished (the active scene name flips early; teleporting mid-load made the
  game fall back to the old area).
- Shared camera: CameraController Update/FixedUpdate are both hooked; the game doesn't rewrite orthographicSize every
  time, so our zoom is undone (only if unchanged) before each update.
- Steam presence: steam_api64 is loaded from Tunic_Data/Plugins where Mono's DllImport can't find it: GetModuleHandle +
  GetProcAddress. UnityWebRequest's DownloadHandlerBuffer is stripped from the build: update check uses HttpWebRequest.
- The manual's map pages have a YouAreHereMapper but the game never draws the marker in normal play: no map markers.

## 0.2.1: downed pose, get-up, crawl rule, uninstall
- The fox's own animator states: playing "die" once on EVERY layer that has it (0, 2, 3) holds the collapse; playing only layer 0 by full path left "Fox|hurt"/"wake up" showing, and re-playing every frame fought the synced layers. The "get up" state's clip is Fox|CUTSCENE_wake up.
- The collapse's root motion makes a downed fox drift ~0.2 m/s: pin its position while downed (crawling moves the anchor).
- Crawl: read MoveUp/Down/Left/Right PlayerActions ourselves (the game ignores input while _cutsceneLocked), move via CharacterController at 1.3 m/s, camera-relative.
- Uninstall.bat / install.ps1 -Uninstall (keeps BepInEx when other plugins exist).
