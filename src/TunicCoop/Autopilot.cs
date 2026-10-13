using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using InControl;
using UnhollowerRuntimeLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace TunicCoop
{
    /// <summary>
    /// Test autopilot: lets an agent drive the real game with no human input.
    ///
    ///   BepInEx/coop_test/cmd.txt      "&lt;seq&gt; &lt;verb&gt; args..." one per line; appended by the tester (local or SSH)
    ///   BepInEx/coop_test/results.txt  "&lt;seq&gt; ok|fail &lt;verb&gt; | detail" per finished command
    ///   BepInEx/coop_test/state.json   snapshot of game + co-op state, rewritten every 0.25 s
    ///   BepInEx/coop_test/shots/*.png  screenshots
    ///
    /// Input goes through a virtual InControl gamepad, so the game sees ordinary controller input.
    /// Commands already in cmd.txt at startup are skipped (stale from a previous run).
    /// </summary>
    public static class Autopilot
    {
        static string Dir => Path.Combine(Paths.BepInExRootPath, "coop_test");
        static string CmdPath => Path.Combine(Dir, "cmd.txt");
        static string ResultPath => Path.Combine(Dir, "results.txt");
        static string StatePath => Path.Combine(Dir, "state.json");

        class Cmd { public int Seq; public string Verb; public string[] Args; public string Raw; public float Start; public bool Begun; public object Data; }

        static readonly Queue<Cmd> _queue = new Queue<Cmd>();
        static Cmd _cur;
        static int _lastSeq = -1;
        static float _nextRead, _nextState;
        static bool _init;

        // Virtual gamepad state (read in the InControl update callback).
        static InputDevice _dev;
        static Vector2 _stick;
        static readonly HashSet<InputControlType> _held = new HashSet<InputControlType>();
        static readonly List<string> _events = new List<string>();

        public static void Init()
        {
            if (_init) return;
            _init = true;
            try
            {
                Directory.CreateDirectory(Path.Combine(Dir, "shots"));
                if (File.Exists(CmdPath))
                    foreach (var line in File.ReadAllLines(CmdPath))
                        if (TryParse(line, out var c)) _lastSeq = Math.Max(_lastSeq, c.Seq);
                File.AppendAllText(ResultPath, $"# boot {DateTime.Now:O} lastSeq={_lastSeq}\n");
            }
            catch (Exception e) { Plugin.L.LogWarning("[auto] init: " + e.Message); }
            // Keep running (and reading input) when the window is not focused: two games, two PCs, no human.
            Application.runInBackground = true;
            Plugin.L.LogInfo($"[auto] ready, dir={Dir}, skipping seq<={_lastSeq}");
        }

        // ---------------- virtual gamepad ----------------

        static void EnsureDevice()
        {
            if (_dev != null) return;
            try
            {
                InputManager._SuspendInBackground_k__BackingField = false;
                _dev = new InputDevice("CoopAutopilot");
                foreach (InputControlType t in new[] {
                    InputControlType.LeftStickUp, InputControlType.LeftStickDown, InputControlType.LeftStickLeft, InputControlType.LeftStickRight,
                    InputControlType.Action1, InputControlType.Action2, InputControlType.Action3, InputControlType.Action4,
                    InputControlType.LeftBumper, InputControlType.RightBumper, InputControlType.LeftTrigger, InputControlType.RightTrigger,
                    InputControlType.DPadUp, InputControlType.DPadDown, InputControlType.DPadLeft, InputControlType.DPadRight,
                    InputControlType.Start, InputControlType.Back, InputControlType.Menu, InputControlType.LeftStickButton, InputControlType.RightStickButton })
                    _dev.AddControl(t, t.ToString());
                InputManager.AttachDevice(_dev);
                InputManager.add_OnUpdateDevices(DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ulong, float>>(new Action<ulong, float>(OnUpdateDevices)));
                Plugin.L.LogInfo("[auto] virtual gamepad attached");
                LogBindings();
            }
            catch (Exception e) { Plugin.L.LogError("[auto] virtual gamepad failed: " + e); _dev = null; }
        }

        static void OnUpdateDevices(ulong tick, float dt)
        {
            if (_dev == null) return;
            try
            {
                _dev.UpdateLeftStickWithValue(_stick, tick, dt);
                foreach (var t in _buttons) _dev.UpdateWithState(t, _held.Contains(t), tick, dt);
                if (_stick.sqrMagnitude > 0.01f || _held.Count > 0) _dev.RequestActivation();
            }
            catch (Exception e) { Plugin.L.LogWarning("[auto] device update: " + e.Message); }
        }

        static readonly InputControlType[] _buttons = {
            InputControlType.Action1, InputControlType.Action2, InputControlType.Action3, InputControlType.Action4,
            InputControlType.LeftBumper, InputControlType.RightBumper, InputControlType.LeftTrigger, InputControlType.RightTrigger,
            InputControlType.DPadUp, InputControlType.DPadDown, InputControlType.DPadLeft, InputControlType.DPadRight,
            InputControlType.Start, InputControlType.Back, InputControlType.Menu };

        /// <summary>Gamepad control bound to a named game action (e.g. InteractDodge, Item1, Shield).</summary>
        static InputControlType? ControlFor(string action)
        {
            var set = PlayerInput.Actions;
            if (set == null) return null;
            foreach (var a in set.actions)
            {
                if (a == null || !string.Equals(a.Name, action, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var b in a.regularBindings)
                {
                    var d = b?.TryCast<DeviceBindingSource>();
                    if (d != null) return d.Control;
                }
                foreach (var b in a.defaultBindings)
                {
                    var d = b?.TryCast<DeviceBindingSource>();
                    if (d != null) return d.Control;
                }
            }
            return null;
        }

        static void LogBindings()
        {
            var set = PlayerInput.Actions;
            if (set == null) return;
            var sb = new StringBuilder("[auto] gamepad bindings:");
            foreach (var a in set.actions) { var c = ControlFor(a.Name); if (c != null) sb.Append($" {a.Name}={c}"); }
            Plugin.L.LogInfo(sb.ToString());
        }

        // ---------------- command loop ----------------

        public static void Tick(CoopManager mgr, Net net, string scene)
        {
            Init();
            // TUNIC freezes when its window loses focus (observed: an unfocused game never finished quitting).
            // Re-assert every frame in case the game resets it; tests run both games unfocused.
            if (!Application.runInBackground) Application.runInBackground = true;
            float now = Time.unscaledTime;
            if (_dev == null && PlayerInput.Actions != null) EnsureDevice();

            if (now >= _nextRead) { _nextRead = now + 0.2f; ReadCommands(); }
            if (now >= _nextState) { _nextState = now + 0.25f; WriteState(net, scene); }

            if (now < _watchUntil && now >= _watchNext) { _watchNext = now + 0.1f; WatchChest(now); }

            if (_cur == null && _queue.Count > 0) _cur = _queue.Dequeue();
            if (_cur == null) return;
            if (!_cur.Begun) { _cur.Begun = true; _cur.Start = now; }
            string result;
            bool done;
            try { done = Step(_cur, mgr, net, scene, now - _cur.Start, out result); }
            catch (Exception e) { done = true; result = "fail | " + e.Message; Plugin.L.LogWarning($"[auto] {_cur.Verb} threw: {e}"); }
            if (done)
            {
                _stick = Vector2.zero; _held.Clear();
                Report(_cur, result);
                _cur = null;
            }
        }

        static bool Step(Cmd c, CoopManager mgr, Net net, string scene, float t, out string result)
        {
            result = "ok";
            var pc = PlayerCharacter.instance;
            switch (c.Verb)
            {
                case "echo": result = "ok | " + string.Join(" ", c.Args); return true;
                case "wait": return t >= Fl(c, 0, 1f);

                // ---- boot / menus ----
                case "continue":
                case "newgame":
                {
                    // The title ignores calls until its intro finishes, so: wait until it has been up 3 s, call,
                    // confirm the scene changed, retry (an early call returned "ok" but did nothing).
                    var st = (float[])(c.Data ??= new float[] { -1, 0, 0 }); // [firstSeen, calls, lastCall]
                    if (scene != "TitleScreen" && st[1] > 0) { result = $"ok | left title after {st[1]} call(s), now {scene}"; return true; }
                    if (t > Fl(c, 0, 60f)) { result = $"fail | still on {scene} after {st[1]} call(s)"; return true; }
                    var ts = Object.FindObjectOfType<TitleScreen>();
                    if (ts == null) return false;
                    if (st[0] < 0) st[0] = t;
                    if (t - st[0] < 3f || (st[1] > 0 && t - st[2] < 6f) || st[1] >= 3) return false;
                    st[1]++; st[2] = t;
                    if (c.Verb == "continue") ts.__Continue(); else ts.__NewGame();
                    return false;
                }
                case "load":
                    FileManagementGUI.LoadFileAndStart(c.Args[0]);
                    return true;
                case "saves":
                    result = "ok | " + string.Join(",", SaveFile.GetRootSaveFileNameList());
                    return true;
                case "quit": Application.Quit(); return true;
                case "restart":
                {
                    // Relaunch from inside the game: the new process inherits this desktop session (SSH can't
                    // start windowed apps). A staged TunicCoop.dll.next is swapped in while the game is closed.
                    string pluginDll = typeof(Plugin).Assembly.Location;
                    string staged = pluginDll + ".next";
                    string exe = Paths.ExecutablePath;
                    int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                    // Keep the original launch options (e.g. -screen-fullscreen 0: exclusive fullscreen stalls
                    // the game when unfocused, which freezes tests).
                    var argv = Environment.GetCommandLineArgs();
                    var passArgs = new StringBuilder();
                    for (int i = 1; i < argv.Length; i++) passArgs.Append(' ').Append(argv[i].Contains(" ") ? "\"" + argv[i] + "\"" : argv[i]);
                    // Wait ~10 s for a clean exit; if Unity's quit hangs, end exactly this PID, then swap + relaunch.
                    string script = $"/c timeout /t 10 /nobreak >nul & taskkill /pid {pid} /f >nul 2>&1 & timeout /t 2 /nobreak >nul" +
                                    $" & if exist \"{staged}\" move /y \"{staged}\" \"{pluginDll}\" & start \"\" \"{exe}\"{passArgs}";
                    // Doorstop marks this process with DOORSTOP_* env vars; a child inherits them and the relaunched
                    // game then skips loading BepInEx (verified: restarted game ran unmodded). Clear them first.
                    foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables())
                        if (kv.Key is string k && k.StartsWith("DOORSTOP", StringComparison.OrdinalIgnoreCase))
                            Environment.SetEnvironmentVariable(k, null);
                    // UseShellExecute = true: the helper must not inherit the game's handles (that hung Unity's quit).
                    var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", script)
                    { UseShellExecute = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(exe) };
                    System.Diagnostics.Process.Start(psi);
                    result = "ok | restarting " + (File.Exists(staged) ? "with staged dll" : "same dll");
                    Report(c, result);
                    _cur = null;
                    Application.Quit();
                    result = null;
                    return false; // already reported; we're quitting
                }

                // ---- waiting for conditions ----
                case "waitplayer":
                {
                    // SceneLoader.Progress never reaches 1 (async loads park at 0.9), so: player present in a real
                    // scene, stable for 1 s.
                    var since = (float[])(c.Data ??= new float[] { -1 });
                    // ...and the camera has arrived at the fox: loading a save plays an island flyover during which
                    // input is ignored (presses right after load did nothing).
                    var camNow = Camera.main;
                    bool camHome = camNow != null && pc != null && (camNow.transform.position - pc.transform.position).magnitude < 70f; // gameplay cam sits ~50 m away
                    bool ready = pc != null && scene != "Loading" && scene != "TitleScreen" && camHome;
                    if (!ready) since[0] = -1;
                    else if (since[0] < 0) since[0] = t;
                    else if (t - since[0] >= 1f) { result = "ok | " + scene; return true; }
                    if (t > Fl(c, 0, 60f)) { result = $"fail | timeout in {scene} pc={(pc != null)} camDist={(camNow != null && pc != null ? (camNow.transform.position - pc.transform.position).magnitude : -1):F1}"; return true; }
                    return false;
                }
                case "waitplayers":
                {
                    int want = (int)Fl(c, 0, 2);
                    if (net.Players.Count >= want && net.LocalId != 255) { result = $"ok | {net.Players.Count}"; return true; }
                    if (t > Fl(c, 1, 30f)) { result = $"fail | have {net.Players.Count}"; return true; }
                    return false;
                }
                case "waitscene":   // waitscene <scene name, may contain spaces> [timeout as last arg]
                {
                    // Scene names have spaces ("Sword Cave"): the last arg is the timeout only if numeric.
                    float tmo = 30f; int nameArgs = c.Args.Length;
                    if (nameArgs > 1 && float.TryParse(c.Args[nameArgs - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var tv)) { tmo = tv; nameArgs--; }
                    string want = string.Join(" ", c.Args, 0, nameArgs);
                    if (scene == want && pc != null) return true;
                    if (t > tmo) { result = $"fail | in {scene}, wanted {want}"; return true; }
                    return false;
                }

                // ---- session ----
                case "host": mgr.StartHost(); return true;
                case "join": mgr.StartJoin(c.Args.Length > 0 ? c.Args[0] : null); return true;
                case "leave": mgr.Leave(); return true;

                // ---- movement / actions ----
                case "stick":   // stick <x> <y> <seconds>   raw stick vector
                    _stick = new Vector2(Fl(c, 0, 0), Fl(c, 1, 0));
                    return t >= Fl(c, 2, 1f);
                case "moveto":  // moveto <x> <z> [timeout] [radius]
                {
                    // Stop when the scene changes: steering on toward old coordinates walked the fox straight back
                    // out of the door it had just entered (verified ping-pong Overworld ↔ Sword Cave).
                    if (c.Data == null) c.Data = scene;
                    if ((string)c.Data != scene) { result = $"ok | scene changed to {scene}"; return true; }
                    if (pc == null) { result = "fail | no player"; return true; }
                    var target = new Vector3(Fl(c, 0, 0), 0, Fl(c, 1, 0));
                    var p = pc.transform.position;
                    var d = new Vector3(target.x - p.x, 0, target.z - p.z);
                    if (d.magnitude <= Fl(c, 3, 0.6f)) { result = $"ok | at {V(p)}"; return true; }
                    if (t > Fl(c, 2, 10f)) { result = $"fail | stuck at {V(p)}"; return true; }
                    _stick = WorldToStick(d.normalized);
                    return false;
                }
                case "press":   // press <action|control> [holdSeconds]
                {
                    if (c.Data == null)
                    {
                        InputControlType ctl;
                        var bound = ControlFor(c.Args[0]);
                        if (bound != null) ctl = bound.Value;
                        else if (!Enum.TryParse(c.Args[0], true, out ctl)) { result = "fail | unknown action " + c.Args[0]; return true; }
                        c.Data = ctl;
                    }
                    var ctrl = (InputControlType)c.Data;
                    float hold = Fl(c, 1, 0.12f);
                    if (t < hold) { _held.Add(ctrl); return false; }
                    _held.Remove(ctrl);
                    if (t < hold + 0.1f) return false; // release frame(s) so the game sees the edge
                    result = "ok | " + ctrl;
                    return true;
                }
                case "face":    // face <x> <z> : nudge the stick briefly so the fox turns toward a point
                {
                    if (pc == null) { result = "fail | no player"; return true; }
                    var p = pc.transform.position;
                    var d = new Vector3(Fl(c, 0, 0) - p.x, 0, Fl(c, 1, 0) - p.z).normalized;
                    // Rotate in place: a stick nudge walked the fox out of interaction range.
                    if (d.sqrMagnitude > 0.01f) pc.transform.rotation = Quaternion.LookRotation(d, Vector3.up);
                    result = $"ok | yaw {F(pc.transform.eulerAngles.y)}";
                    return true;
                }
                case "tp":      // tp <x> <y> <z>
                {
                    if (pc == null) { result = "fail | no player"; return true; }
                    Teleport(pc, new Vector3(Fl(c, 0, 0), Fl(c, 1, 0), Fl(c, 2, 0)));
                    result = "ok | " + V(pc.transform.position);
                    return true;
                }
                case "tpto":    // tpto <playerId> [offsetX] : jump next to a remote player (same scene)
                {
                    var target = PuppetRegistry.Position((byte)Fl(c, 0, 0));
                    if (pc == null || target == null) { result = "fail | no target"; return true; }
                    Teleport(pc, target.Value + new Vector3(Fl(c, 1, 1.5f), 0.2f, 0));
                    result = "ok | " + V(pc.transform.position);
                    return true;
                }

                // ---- observation ----
                case "shot":
                {
                    string name = c.Args.Length > 0 ? c.Args[0] : $"shot_{c.Seq}";
                    // ScreenCapture.CaptureScreenshot silently writes nothing in TUNIC (verified), so render the
                    // main camera into a RenderTexture ourselves. Works unfocused; no UI overlay.
                    result = Snap(Path.Combine(Dir, "shots", name + ".png"));
                    return true;
                }
                case "rec":     // rec <seconds> <fps> <name> : frames for a showcase clip (shots/<name>_0000.png ...)
                {
                    float secs = Fl(c, 0, 6f), fps = Fl(c, 1, 10f);
                    string name = c.Args.Length > 2 ? c.Args[2] : "rec";
                    int frame = c.Data is int f0 ? f0 : 0;
                    if (t >= frame / fps)
                    {
                        Snap(Path.Combine(Dir, "shots", $"{name}_{frame:D4}.png"));
                        c.Data = frame + 1;
                    }
                    if (t > secs) { result = $"ok | {frame} frames"; return true; }
                    return false;
                }
                case "monsters": Diagnostics.DumpMonsters(scene); return true;
                case "spawncoins": // spawncoins <value> [dist] : drop coins ahead of the player via the game's CoinSpawner
                {
                    if (pc == null) { result = "fail | no player"; return true; }
                    var at = Ground(pc.transform.position + pc.transform.forward * Fl(c, 1, 5f));
                    CoinSpawner.SpawnCoins((int)Fl(c, 0, 5), at);
                    result = "ok | at " + V(at);
                    return true;
                }
                case "getint":  // getint <key with spaces> : live SaveFile int (e.g. "chest open 15")
                {
                    string key = string.Join(" ", c.Args);
                    bool has = SaveFile.intStore != null && SaveFile.intStore.ContainsKey(key);
                    result = $"ok | '{key}'={(has ? SaveFile.GetInt(key).ToString() : "absent")}";
                    return true;
                }
                case "watchchest": // watchchest <id> <seconds> : background-log the chest's state 10x/s
                    _watchChest = (int)Fl(c, 0, 0); _watchUntil = Time.unscaledTime + Fl(c, 1, 10f); _watchNext = 0;
                    result = "ok | watching in background";
                    return true;
                case "items":   // every inventory item with quantity > 0
                {
                    var parts = new List<string>();
                    foreach (var it in Inventory.itemList) { if (it == null) continue; int q = it.Quantity; if (q > 0) parts.Add($"{it.name}={q}"); }
                    parts.Sort();
                    result = "ok | " + string.Join(",", parts);
                    return true;
                }
                case "mons":    // mons [radius] : enemy sync view (owner, mirrors, hp, dead)
                    result = "ok | " + MonsterSync.Describe(pc != null ? pc.transform.position : Vector3.zero, Fl(c, 0, 20f));
                    return true;
                case "mondebug": // mondebug <id> : AI state of one monster (owner-side diagnosis)
                {
                    int id = (int)Fl(c, 0, 0);
                    var mm = MonsterManager.instance;
                    if (mm == null) { result = "fail | no MonsterManager"; return true; }
                    foreach (var m in mm.monsters)
                    {
                        if (m == null || m.runtimeStableID == null || m.runtimeStableID.ID != id) continue;
                        var nm = m.TryCast<NavigatingMonster>();
                        string nav = nm == null ? "notNav" :
                            $"loco={V(nm.locomotionTarget)} facing={V(nm.facingTarget)} hasPath={nm.hasPath} dest={(m.nma != null ? V(m.nma.destination) : "-")}";
                        result = $"ok | pos={V(m.transform.position)} aggro={m.IsAggroed} enabled={m.enabled} active={m.isActiveAndEnabled} sleepDist={m.SleepingDueToPlayerDistance} canSee={m.CanSeePlayerOrBait} aggroDist={F(m.monsterAggroDistance)} {nav}";
                        return true;
                    }
                    result = "fail | not registered (asleep/disabled/dead?)";
                    return true;
                }
                case "portals": // portals [radius] : x|y|z|destScene|id ; nearest first
                {
                    var from = pc != null ? pc.transform.position : Vector3.zero;
                    var found = new List<(float d, string s)>();
                    foreach (var obj in Object.FindObjectsOfType(Il2CppType.Of<ScenePortal>()))
                    {
                        var sp = obj.TryCast<ScenePortal>();
                        if (sp == null) continue;
                        var p = sp.transform.position; float d = (p - from).magnitude;
                        if (d <= Fl(c, 0, 60f)) found.Add((d, $"{F(p.x)}|{F(p.y)}|{F(p.z)}|{sp.destinationSceneName}|{sp.id}|{sp.FullID}"));
                    }
                    found.Sort((x, y) => x.d.CompareTo(y.d));
                    var parts = new List<string>(); foreach (var f in found) parts.Add(f.s);
                    result = "ok | " + string.Join(";", parts);
                    return true;
                }
                case "goto":    // goto <Scene Name>[|spawnId] : the game's own transition (test setup)
                {
                    var spec = string.Join(" ", c.Args).Split('|');
                    if (c.Data == null)
                    {
                        if (scene == spec[0] && pc != null) { result = "ok | already there"; return true; }
                        ScenePortal.DepartToScene(mgr, false, 0.5f, spec[0], spec.Length > 1 ? spec[1] : "", false, 0f);
                        c.Data = true;
                    }
                    if (scene == spec[0] && pc != null && SceneLoader.sceneNameToLoad == spec[0]) { result = "ok | " + scene; return true; }
                    if (t > 40f) { result = "fail | still in " + scene; return true; }
                    return false;
                }
                case "warp":    // warp <Scene Name>|<portal FullID> : TUNIC's dev warp — arrive AT a given portal of any scene
                {
                    var spec = string.Join(" ", c.Args).Split('|');
                    if (c.Data == null)
                    {
                        Cheats.CheatWarpToPortal(spec[0], spec.Length > 1 ? spec[1] : "");
                        c.Data = true;
                    }
                    if (scene == spec[0] && pc != null && SceneLoader.sceneNameToLoad == spec[0] && t > 1f) { result = "ok | " + scene + " @ " + PlayerCharacterSpawn.portalIDToSpawnAt; return true; }
                    if (t > 40f) { result = "fail | still in " + scene; return true; }
                    return false;
                }
                case "boss":    // boss : what the boss health bar shows on this machine
                {
                    var ba = BossAnnouncer.instance;
                    string cur = "none";
                    try { if (ba != null && ba.currentMonster != null) cur = $"{ba.currentMonster.GetIl2CppType().Name} hp={ba.currentMonster.hp}/{ba.currentMonster.maxhp}"; } catch { }
                    result = $"ok | bar={(ba != null && ba.barVisible)} monster={cur}";
                    return true;
                }
                case "setint":  // setint <value> <key with spaces> : write a SaveFile int (test setup)
                {
                    int v = (int)Fl(c, 0, 0);
                    string key = string.Join(" ", c.Args, 1, c.Args.Length - 1);
                    SaveFile.SetInt(key, v);
                    result = $"ok | {key} = {SaveFile.GetInt(key)}";
                    return true;
                }
                case "ping": Signals.PingHere(net); result = "ok | " + Signals.Describe(); return true;
                case "chat":    // chat <1-6> : quick chat phrase
                    Signals.Say(net, (int)Fl(c, 0, 1) - 1); result = "ok | " + Signals.Describe(); return true;
                case "signals": result = "ok | " + Signals.Describe(); return true;
                case "gotoplayer": // gotoplayer <id> : the F5 / menu "Go to" travel
                {
                    Regroup.GoTo(net, (byte)Fl(c, 0, 0));
                    result = "ok | " + Regroup.Describe(); return true;
                }
                case "regroup": result = "ok | " + Regroup.Describe(); return true;
                case "cam":     // cam [shared on|off] : shared camera state
                {
                    if (c.Args.Length > 1 && c.Args[0] == "shared") Plugin.SharedCamera.Value = c.Args[1] == "on";
                    result = "ok | " + SharedCam.Describe(); return true;
                }
                case "rule":    // rule <revive|reviveSeconds|reviveHealth|bleedOut|enemyHealth|share> <value> : host rules (as the Rules tab)
                {
                    if (c.Args.Length >= 2)
                    {
                        string v = c.Args[1];
                        switch (c.Args[0])
                        {
                            case "revive": Plugin.Revive.Value = v == "on" || v == "1"; break;
                            case "reviveSeconds": Plugin.ReviveSeconds.Value = Fl(c, 1, 3); break;
                            case "reviveHealth": Plugin.ReviveHealthPercent.Value = (int)Fl(c, 1, 50); break;
                            case "bleedOut": Plugin.BleedOutSeconds.Value = (int)Fl(c, 1, 0); break;
                            case "enemyHealth": Plugin.EnemyHealthPerExtraPlayer.Value = (int)Fl(c, 1, 0); break;
                            case "share": Plugin.ShareMoneyAndConsumables.Value = v == "on" || v == "1"; break;
                            case "crawl": Plugin.DownedCrawl.Value = v == "on" || v == "1"; break;
                        }
                        if (net != null && net.Active && net.IsHost) mgr.SendSettings(); else HostSettings.LoadLocal();
                    }
                    result = "ok | " + HostSettings.Serialize() + " fromHost=" + HostSettings.FromHost; return true;
                }
                case "connect": // connect : join codes / port forwarding / public address / update state
                {
                    var codes = CoopUI.HostCodes(net);
                    result = $"ok | upnp={PortMapper.Status} ext={(PortMapper.ExternalIp != null ? "yes" : "no")} stun={(PublicAddress.Ip != null ? "yes" : "no")} internetCode={codes.internet ?? "-"} lanCode={codes.lan ?? "-"} update={UpdateCheck.Latest ?? "none"}";
                    return true;
                }
                case "decode":  // decode <code> : what address a join code points to
                {
                    result = JoinCode.TryDecode(c.Args[0], out var a, out var p) ? $"ok | {a}:{p}" : "fail | not a code"; return true;
                }
                case "sfx": result = $"ok | revive sounds played={Sfx.Played}"; return true;
                case "mappage": // mappage open|close|state : the manual's map page for this area
                {
                    string sub = c.Args.Length > 0 ? c.Args[0] : "state";
                    var pd = PageDisplay.instance;
                    if (sub == "open")
                    {
                        var yahm = Object.FindObjectOfType<YouAreHereMapper>();
                        if (yahm == null || pd == null) { result = "fail | no map for this area"; return true; }
                        if (!Profile.IsPageUnlocked(yahm.leafNumber)) Profile.UnlockPage(yahm.leafNumber);   // test save: make sure the map page is in the book
                        PageDisplay.showSpecificPageOnOpen = true; PageDisplay.specificLeafToShowOnOpen = yahm.leafNumber;
                        PageDisplay.ShowPage(yahm.leafNumber);
                        result = $"ok | opened leaf {yahm.leafNumber}"; return true;
                    }
                    if (sub == "close" && pd != null) { pd.close(); }
                    if (sub == "show" && pd != null) { var y2 = Object.FindObjectOfType<YouAreHereMapper>(); if (y2 != null) PageDisplay.ShowPage(y2.leafNumber); }
                    result = $"ok | open={PageDisplay.Open}"; return true;
                }
                case "crash": net.SimulateCrash(); result = "ok | network vanished"; return true;
                case "relay":   // relay <host:port | off> : the NAT punch-through relay (Network.Introducer)
                    if (c.Args.Length > 0) Plugin.Introducer.Value = c.Args[0] == "off" ? "" : c.Args[0];
                    result = $"ok | introducer='{Plugin.Introducer.Value}' registered={(net.RegisteredCode != null)}"; return true;
                case "keys":    // keys <substring> : SaveFile int/string keys containing it, with values
                {
                    string sub = string.Join(" ", c.Args);
                    var parts = new List<string>();
                    if (SaveFile.intStore != null) foreach (var kv in SaveFile.intStore) if (kv.Key.Contains(sub)) parts.Add($"{kv.Key}={kv.Value}");
                    if (SaveFile.stringStore != null) foreach (var kv in SaveFile.stringStore) if (kv.Key.Contains(sub)) parts.Add($"{kv.Key}='{(kv.Value?.Length > 40 ? kv.Value.Substring(0, 40) + "…" : kv.Value)}'");
                    result = "ok | " + string.Join("; ", parts); return true;
                }
                case "freeze":  // freeze <monsterId> on|off : pause a monster's AI on its owner (test setups)
                {
                    var fm = MonsterSync.Find(int.Parse(c.Args[0], CultureInfo.InvariantCulture));
                    if (fm == null) { result = "fail | no monster"; return true; }
                    bool on = c.Args.Length < 2 || c.Args[1] == "on";
                    fm.enabled = !on;
                    try { if (fm.nma != null && fm.nma.isOnNavMesh) fm.nma.isStopped = on; } catch { }
                    try { var an = fm.cachedAnimator; if (an != null) an.speed = on ? 0f : 1f; } catch { }
                    result = $"ok | frozen={on}"; return true;
                }
                case "getstr":  // getstr <key with spaces> : read a SaveFile string
                {
                    string key = string.Join(" ", c.Args);
                    result = $"ok | '{key}'='{SaveFile.GetString(key)}'"; return true;
                }
                case "setstr":  // setstr <key with spaces>|<value> : write a SaveFile string (test setup)
                {
                    var kv = string.Join(" ", c.Args).Split('|');
                    SaveFile.SetString(kv[0], kv.Length > 1 ? kv[1] : "");
                    result = $"ok | {kv[0]} = '{SaveFile.GetString(kv[0])}'";
                    return true;
                }
                case "hurt":    // hurt <monsterId> <dmg> : damage a monster exactly like a local hit would (boss tests)
                {
                    int id = int.Parse(c.Args[0], CultureInfo.InvariantCulture); int dmg = (int)Fl(c, 1, 10);
                    MonsterSync.LastLocalAttack = Time.unscaledTime;   // counts as our hit
                    var synced = MonsterSync.Find(id);
                    if (synced != null)
                    {
                        int b0 = synced.hp;
                        synced.IDamageable_ReceiveDamage(dmg, 0, Vector3.zero, 0f, 0);
                        result = $"ok | {id} {synced.GetIl2CppType().Name} hp {b0} -> {synced.hp}";
                        return true;
                    }
                    var mm = MonsterManager.instance;
                    if (mm != null && mm.monsters != null)
                        foreach (var m in mm.monsters)
                        {
                            if (m == null || m.runtimeStableID == null || !m.runtimeStableID.HasValidID || m.runtimeStableID.ID != id) continue;
                            int before = m.hp;
                            m.IDamageable_ReceiveDamage(dmg, 0, Vector3.zero, 0f, 0);
                            result = $"ok | {id} {m.GetIl2CppType().Name} hp {before} -> {m.hp}";
                            return true;
                        }
                    result = "fail | no monster " + id; return true;
                }
                case "foxnodes": // foxnodes local|puppet : visible renderers / emitting particles by path (diagnostics)
                {
                    Transform root = null;
                    if (c.Args.Length > 0 && c.Args[0] == "puppet") { var all = PuppetRegistry.All(); if (all.Count > 0) root = all[0].transform; }
                    else if (pc != null) root = pc.transform;
                    if (root == null) { result = "fail | no fox"; return true; }
                    var parts = new List<string>();
                    foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                        var ps = r.GetComponent<ParticleSystem>();
                        if (ps != null && ps.particleCount == 0) continue;
                        var ls = r.transform.lossyScale; var an = r.GetComponentInParent<Animator>();
                        string extra = "";
                        try
                        {
                            var b = r.bounds; extra += $" ext={F(b.extents.x)},{F(b.extents.y)},{F(b.extents.z)}";
                            if (an != null) { var st = an.GetCurrentAnimatorStateInfo(0); extra += $" st={st.fullPathHash} n={F(st.normalizedTime)} spd={F(an.speed)} cull={an.cullingMode}"; }
                            var mat = r.sharedMaterial; if (mat != null) extra += $" mat={mat.name}";
                            var smr = r.TryCast<SkinnedMeshRenderer>();
                            if (smr != null)
                            {
                                float mn = float.MaxValue, mx = 0f;
                                foreach (var bone in smr.bones) { if (bone == null) continue; float s = bone.lossyScale.magnitude; mn = Mathf.Min(mn, s); mx = Mathf.Max(mx, s); }
                                extra += $" bones[{smr.bones.Length}] scale {F(mn)}..{F(mx)} mpb={r.HasPropertyBlock()}";
                            }
                        }
                        catch { }
                        parts.Add(PathOf(r.transform, root) + (ps != null ? $"[ps {ps.particleCount}]" : "") + $"<{r.GetIl2CppType().Name}> scale={F(ls.x)},{F(ls.y)},{F(ls.z)} animator={(an != null ? an.gameObject.name + (an.enabled ? "" : "(off)") : "-")}" + extra);
                    }
                    parts.Sort();
                    File.WriteAllLines(Path.Combine(Dir, $"foxnodes_{c.Args[0]}.txt"), parts.ToArray());
                    result = $"ok | {parts.Count} visible nodes written to foxnodes_{c.Args[0]}.txt";
                    return true;
                }
                case "puppethide": // puppethide <substring> : disable puppet renderers whose path contains it (diagnostics)
                {
                    var all = PuppetRegistry.All();
                    if (all.Count == 0) { result = "fail | no puppet"; return true; }
                    var root = all[0].transform; int n = 0;
                    foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                        if (r != null && PathOf(r.transform, root).Contains(c.Args[0])) { r.forceRenderingOff = true; n++; }
                    result = $"ok | forced off {n} renderers matching '{c.Args[0]}'";
                    return true;
                }
                case "countobj": // countobj <name prefix> : GameObjects (incl. DontDestroyOnLoad) whose name starts with it
                {
                    var parts = new List<string>();
                    foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppType.Of<GameObject>()))
                    {
                        var go = obj.TryCast<GameObject>();
                        if (go == null || !go.name.StartsWith(c.Args[0])) continue;
                        if (go.transform.parent != null) continue;   // roots only
                        var p = go.transform.position;
                        parts.Add($"{go.name}@{F(p.x)},{F(p.z)} active={go.activeInHierarchy} scene={go.scene.name}");
                    }
                    result = $"ok | {parts.Count}: " + string.Join("; ", parts);
                    return true;
                }
                case "gauntlet": // gauntlet start : what stepping into the Cathedral arena does on THIS machine only
                {
                    var gm = Object.FindObjectOfType<CathedralGauntletManager>();
                    if (gm == null) { result = "fail | no gauntlet here"; return true; }
                    if (c.Args.Length > 1 && c.Args[0] == "light")
                    {
                        // gauntlet light <altar substring> : what lighting that altar's candle does (interaction)
                        foreach (var o in Object.FindObjectsOfType<CathedralGauntletSummoner>())
                            if (o.gameObject.name.Contains(c.Args[1])) { o.playerLightCandle(); result = "ok | lit " + o.gameObject.name; return true; }
                        result = "fail | no altar " + c.Args[1]; return true;
                    }
                    gm.FightStart();
                    result = "ok | fight started"; return true;
                }
                case "findtype": // findtype <ComponentTypeName> : scene objects carrying that component (+ position)
                {
                    var parts = new List<string>();
                    var ty = typeof(PlayerCharacter).Assembly.GetType(c.Args[0]);
                    if (ty == null) { result = "fail | no type " + c.Args[0]; return true; }
                    foreach (var obj in Object.FindObjectsOfType(Il2CppType.From(ty)))
                    {
                        var comp = obj.TryCast<Component>(); if (comp == null) continue;
                        var p = comp.transform.position;
                        parts.Add($"{comp.gameObject.name}@{F(p.x)},{F(p.y)},{F(p.z)} active={comp.gameObject.activeInHierarchy}");
                    }
                    result = $"ok | {parts.Count}: " + string.Join("; ", parts);
                    return true;
                }
                case "revive":  // revive state | next | prev | giveup | force
                {
                    string sub = c.Args.Length > 0 ? c.Args[0] : "state";
                    if (sub == "next") ReviveSystem.Cycle(+1);
                    else if (sub == "prev") ReviveSystem.Cycle(-1);
                    else if (sub == "giveup") ReviveSystem.ReallyDie();
                    else if (sub == "force") ReviveSystem.ForceReviveLocal();
                    else if (sub == "down") ReviveSystem.ForceDownLocal();
                    result = "ok | " + ReviveSystem.Describe();
                    return true;
                }
                case "ui":      // ui key <KeyCode> | ui type <text> | ui state
                {
                    string sub = c.Args.Length > 0 ? c.Args[0] : "state";
                    if (sub == "key" && Enum.TryParse(c.Args[1], true, out KeyCode k)) CoopUI.Key(k, net);
                    else if (sub == "type") CoopUI.Type(string.Join(" ", c.Args, 1, c.Args.Length - 1), net);
                    result = "ok | " + CoopUI.Describe();
                    return true;
                }
                case "uiprobe": // what UI building blocks this TUNIC build offers
                {
                    var sb = new StringBuilder();
                    try
                    {
                        sb.Append("fonts=");
                        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<TMPro.TMP_FontAsset>())) sb.Append(o.name).Append(',');
                    }
                    catch (Exception e) { sb.Append("fontErr:" + e.Message); }
                    try { sb.Append(" eventSystem=" + (UnityEngine.EventSystems.EventSystem.current != null)); } catch (Exception e) { sb.Append(" esErr:" + e.Message); }
                    try
                    {
                        var go = new GameObject("TunicCoop_UIProbe");
                        var cv = go.AddComponent<Canvas>(); cv.renderMode = RenderMode.ScreenSpaceOverlay;
                        var tgo = new GameObject("t"); tgo.transform.SetParent(go.transform, false);
                        var tmp = tgo.AddComponent<TMPro.TextMeshProUGUI>(); tmp.text = "probe";
                        var igo = new GameObject("i"); igo.transform.SetParent(go.transform, false);
                        var img = igo.AddComponent<UnityEngine.UI.Image>(); img.color = Color.black;
                        sb.Append(" canvas+tmp+image=ok");
                        Object.Destroy(go);
                    }
                    catch (Exception e) { sb.Append(" uguiErr:" + e.Message); }
                    try { var gs = new GUIStyle(); GUI.Box(new Rect(0, 0, 1, 1), ""); sb.Append(" imguiBox=ok"); } catch (Exception e) { sb.Append(" imguiBoxErr:" + e.Message); }
                    result = "ok | " + sb;
                    return true;
                }
                case "dbg":     // transition/loader state
                    result = $"ok | scene={scene} departing={ScenePortal.DepartureSequenceRunning} pausing={ScenePortal.DepartureSequencePausingTime} loadLocked={SceneLoader.LoadingLocked} toLoad={SceneLoader.sceneNameToLoad} progress={F(SceneLoader.Progress)} timeScale={F(Time.timeScale)} cutsceneLocked={PlayerCharacter._cutsceneLocked}";
                    return true;
                case "hookstats": result = "ok | " + HookProbe.Stats(); return true;
                case "clips":   // clips [filter] : animation clips in the fox's animator controller
                {
                    var ctl = pc != null && pc.cachedAnimator != null ? pc.cachedAnimator.runtimeAnimatorController : null;
                    if (ctl == null) { result = "fail | no animator"; return true; }
                    var names = new SortedSet<string>();
                    foreach (var clip in ctl.animationClips) if (clip != null && (c.Args.Length == 0 || clip.name.IndexOf(c.Args[0], StringComparison.OrdinalIgnoreCase) >= 0)) names.Add($"{clip.name}({clip.length:F1}s)");
                    result = $"ok | {names.Count}: " + string.Join(", ", names); return true;
                }
                case "animprobe": // animprobe : fox animator parameters + which candidate state names exist + current state
                {
                    var an = pc != null ? pc.cachedAnimator : null;
                    if (an == null) { result = "fail | no animator"; return true; }
                    var ps = new List<string>();
                    foreach (var prm in an.parameters) ps.Add($"{prm.name}:{prm.type}");
                    var found = new List<string>();
                    foreach (var n in new[] { "die", "Die", "death", "Death", "dead", "Dead", "dying", "kneel", "Kneel", "get up", "Get Up", "getup", "get_up", "exhausted", "Exhausted", "character exhausted", "Fox|die", "Fox|kneel", "Fox|get up", "hurt", "stagger", "idle", "Idle", "sleeping", "CUTSCENE_sleeping", "wake up", "resurrection" })
                        for (int layer = 0; layer < an.layerCount; layer++)
                        {
                            if (an.HasState(layer, Animator.StringToHash(n))) found.Add($"{n}@L{layer}");
                            string full = an.GetLayerName(layer) + "." + n;
                            if (an.HasState(layer, Animator.StringToHash(full))) found.Add(full);
                        }
                    var st = an.GetCurrentAnimatorStateInfo(0);
                    var cl = an.GetCurrentAnimatorClipInfo(0);
                    result = $"ok | layers={an.layerCount} states=[{string.Join(", ", found)}] now={st.fullPathHash}/{(cl.Length > 0 ? cl[0].clip.name : "-")} params=[{string.Join(", ", ps)}]";
                    return true;
                }
                case "anim":    // anim layers | anim trigger <name> | anim play <state> : animator experiments
                {
                    var an = pc != null ? pc.cachedAnimator : null;
                    if (an == null) { result = "fail | no animator"; return true; }
                    string sub = c.Args.Length > 0 ? c.Args[0] : "layers";
                    string arg = c.Args.Length > 1 ? string.Join(" ", c.Args, 1, c.Args.Length - 1) : "";
                    if (sub == "trigger") an.SetTrigger(arg);
                    if (sub == "lock") PlayerCharacter._cutsceneLocked = arg == "on";
                    if (sub == "hp") pc.hp = int.Parse(arg, CultureInfo.InvariantCulture);
                    if (sub == "play") for (int l = 0; l < an.layerCount; l++) if (an.HasState(l, Animator.StringToHash(arg))) an.Play(Animator.StringToHash(arg), l, 0f);
                    var parts = new List<string>();
                    for (int l = 0; l < an.layerCount; l++)
                    {
                        var st = an.GetCurrentAnimatorStateInfo(l); var ci = an.GetCurrentAnimatorClipInfo(l);
                        parts.Add($"L{l} {an.GetLayerName(l)} w={an.GetLayerWeight(l):F2} clip={(ci.Length > 0 ? ci[0].clip.name : "-")} t={st.normalizedTime:F2}");
                    }
                    int animators = pc.GetComponentsInChildren<Animator>(true).Length;
                    result = $"ok | animators={animators} speed={an.speed} rootMotion={an.applyRootMotion} | " + string.Join(" ; ", parts); return true;
                }
                case "harmonystats": result = "ok | " + HarmonyProbe.Stats(); return true;
                case "god":     // god on|off : TUNIC's own debug invincibility, so test setups near enemies survive
                    PlayerCharacter.DebugInvincible = c.Args.Length == 0 || c.Args[0] != "off";
                    result = "ok | invincible=" + PlayerCharacter.DebugInvincible;
                    return true;
                case "chests":  // chests [radius] : x|y|z|id|openedFlag|showsOpen|item;...
                {
                    var from = pc != null ? pc.transform.position : Vector3.zero;
                    var parts = new List<string>();
                    foreach (var obj in Object.FindObjectsOfType(Il2CppType.Of<Chest>()))
                    {
                        var ch = obj.TryCast<Chest>();
                        if (ch == null) continue;
                        var p = ch.transform.position;
                        if ((p - from).magnitude > Fl(c, 0, 30f)) continue;
                        string opened = "?";
                        try { opened = ch.openedStateVar != null ? (ch.openedStateVar.BoolValue ? "1" : "0") : "noVar"; } catch { }
                        string item = "?";
                        try { item = ch.itemContents != null ? $"{ch.itemContents.name}x{ch.itemContentsQuantity}" : $"money{ch.moneySprayQuantity}"; } catch { }
                        parts.Add($"{F(p.x)}|{F(p.y)}|{F(p.z)}|{ch.chestID}|{opened}|{(ch.shouldShowAsOpen ? 1 : 0)}|{item}");
                    }
                    result = "ok | " + string.Join(";", parts);
                    return true;
                }
                case "coins":   // coins [radius] : active coin pickups near the player
                    result = "ok | " + CoinSync.Describe(pc != null ? pc.transform.position : Vector3.zero, Fl(c, 0, 15f));
                    return true;
                case "smashables":
                    result = "ok | " + SmashSync.Describe(pc != null ? pc.transform.position : Vector3.zero, Fl(c, 0, 12f));
                    return true;
                case "inv":
                {
                    var it = Inventory.GetItemByName(c.Args[0]);
                    result = it == null ? "fail | no item" : $"ok | {c.Args[0]}={it.Quantity}";
                    return true;
                }
            }
            result = "fail | unknown verb";
            return true;
        }

        static int _watchChest; static float _watchUntil, _watchNext;
        static void WatchChest(float now)
        {
            foreach (var obj in Object.FindObjectsOfType(Il2CppType.Of<Chest>()))
            {
                var ch = obj.TryCast<Chest>();
                if (ch == null || ch.chestID != _watchChest) continue;
                string anim = "-";
                try { var a = ch.cachedAnimator ?? ch.GetComponentInChildren<Animator>(); if (a != null) anim = a.GetCurrentAnimatorStateInfo(0).fullPathHash.ToString(); } catch { }
                string key = ChestSync.Key(_watchChest);
                Plugin.L.LogInfo($"[watch] t={now:F1} chest {_watchChest} opening={ch.opening} interrupted={ch.interrupted} showOpen={ch.shouldShowAsOpen} anim={anim} key={(SaveFile.intStore.ContainsKey(key) ? SaveFile.GetInt(key) : -1)}");
            }
        }

        /// <summary>World direction → left-stick vector relative to the camera (TUNIC's camera is fixed-angle).</summary>
        static Vector2 WorldToStick(Vector3 dir)
        {
            var cam = Camera.main;
            if (cam == null) return new Vector2(dir.x, dir.z);
            var f = cam.transform.forward; f.y = 0; f.Normalize();
            var r = cam.transform.right; r.y = 0; r.Normalize();
            return new Vector2(Vector3.Dot(dir, r), Vector3.Dot(dir, f)).normalized;
        }

        static string PathOf(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (var x = t; x != null && x != root; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        static string Snap(string path)
        {
            // Render every enabled camera by depth into one RenderTexture (TUNIC draws the fox through extra
            // cameras in some places; Camera.main alone showed only an empty ring where the fox stood).
            var cams = new List<Camera>();
            // NOT Camera.allCameras: its unhollowed getter (GetAllCamerasImpl) throws ObjectCollectedException.
            foreach (var obj in Object.FindObjectsOfType(Il2CppType.Of<Camera>()))
            {
                try
                {
                    var cc = obj.TryCast<Camera>();
                    if (cc == null || !cc.enabled || !cc.gameObject.activeInHierarchy || cc.targetTexture != null) continue;
                    // Main camera + overlay cameras only (higher depth, clearing just depth/nothing). TUNIC keeps
                    // other enabled cameras (an island overview) that would paint over the frame.
                    var main = Camera.main;
                    bool overlay = main != null && cc.depth > main.depth &&
                                   (cc.clearFlags == CameraClearFlags.Depth || cc.clearFlags == CameraClearFlags.Nothing);
                    if (cc == main || overlay) cams.Add(cc);
                }
                catch { }
            }
            if (cams.Count == 0) return "fail | no camera";
            cams.Sort((x, y) => x.depth.CompareTo(y.depth));
            int w = 1280, h = 720;
            var rt = new RenderTexture(w, h, 24);
            var prevActive = RenderTexture.active;
            try
            {
                foreach (var cam in cams)
                {
                    try
                    {
                        cam.targetTexture = rt;
                        try { cam.Render(); } finally { cam.targetTexture = null; }
                    }
                    catch (Exception e) { Plugin.L.LogDebug("[auto] snap skipped a camera: " + e.Message); }
                }
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                var png = ImageConversion.EncodeToPNG(tex);
                File.WriteAllBytes(path, png);
                Object.Destroy(tex);
                return $"ok | {png.Length} bytes";
            }
            finally
            {
                RenderTexture.active = prevActive;
                rt.Release();
                Object.Destroy(rt);
            }
        }

        /// <summary>Snap a requested point to walkable floor (teleporting to a raw height put foxes inside walls).</summary>
        internal static Vector3 Ground(Vector3 want)
        {
            if (UnityEngine.AI.NavMesh.SamplePosition(want, out var hit, 4f, -1 /* NavMesh.AllAreas, missing from interop */)) return hit.position;
            if (Physics.Raycast(want + Vector3.up * 8f, Vector3.down, out var rh, 30f)) return rh.point;
            return want;
        }

        internal static void Teleport(PlayerCharacter pc, Vector3 pos)
        {
            pos = Ground(pos);
            var cc = pc.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            pc.transform.position = pos;
            var rb = pc.GetComponent<Rigidbody>();
            if (rb != null) { rb.position = pos; rb.velocity = Vector3.zero; }
            if (cc != null) cc.enabled = true;
        }

        // ---------------- I/O ----------------

        static void ReadCommands()
        {
            try
            {
                if (!File.Exists(CmdPath)) return;
                string[] lines;
                using (var fs = new FileStream(CmdPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs)) lines = sr.ReadToEnd().Split('\n');
                foreach (var line in lines)
                {
                    if (!TryParse(line, out var c) || c.Seq <= _lastSeq) continue;
                    _lastSeq = c.Seq;
                    _queue.Enqueue(c);
                }
            }
            catch (IOException) { }
        }

        static bool TryParse(string line, out Cmd c)
        {
            c = null;
            line = line.Trim();
            if (line.Length == 0 || line[0] == '#') return false;
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[0], out int seq)) return false;
            var args = new string[parts.Length - 2];
            Array.Copy(parts, 2, args, 0, args.Length);
            c = new Cmd { Seq = seq, Verb = parts[1].ToLowerInvariant(), Args = args, Raw = line };
            return true;
        }

        static void Report(Cmd c, string result)
        {
            string line = $"{c.Seq} {(result.StartsWith("fail") ? "FAIL" : "ok")} {c.Raw.Substring(c.Raw.IndexOf(' ') + 1)} | {result} | t={Time.unscaledTime:F1}";
            Plugin.L.LogInfo("[auto] " + line);
            try { File.AppendAllText(ResultPath, line + "\n"); } catch { }
        }

        public static void Event(string e)
        {
            _events.Add($"{Time.unscaledTime:F1} {e}");
            if (_events.Count > 30) _events.RemoveAt(0);
        }

        static void WriteState(Net net, string scene)
        {
            try
            {
                var pc = PlayerCharacter.instance;
                var sb = new StringBuilder("{");
                sb.Append($"\"t\":{F(Time.unscaledTime)},\"scene\":\"{Esc(scene)}\",\"lastSeq\":{_lastSeq},\"busy\":{(_cur != null || _queue.Count > 0 ? "true" : "false")},");
                if (pc != null)
                {
                    sb.Append($"\"player\":{{\"pos\":[{F(pc.transform.position.x)},{F(pc.transform.position.y)},{F(pc.transform.position.z)}],");
                    try { sb.Append($"\"hp\":{pc.hp},\"maxhp\":{pc.maxhp}"); } catch { sb.Append("\"hp\":null"); }
                    try { sb.Append($",\"cutscene\":{B(PlayerCharacter.CutsceneLocked)},\"hidden\":{B(PlayerCharacter.CutsceneHidden)}"); } catch { }
                    sb.Append("},");
                }
                else sb.Append("\"player\":null,");
                int money = -1;
                try { var m = Inventory.GetItemByName("MoneySmall"); if (m != null) money = m.Quantity; } catch { }
                sb.Append($"\"money\":{money},");
                sb.Append($"\"net\":{{\"active\":{B(net.Active)},\"host\":{B(net.IsHost)},\"localId\":{net.LocalId},\"players\":[");
                bool first = true;
                foreach (var kv in net.Players) { if (!first) sb.Append(','); first = false; sb.Append($"{{\"id\":{kv.Key},\"name\":\"{Esc(kv.Value)}\"}}"); }
                sb.Append("]},\"puppets\":[");
                first = true;
                foreach (var p in PuppetRegistry.All())
                {
                    if (!first) sb.Append(','); first = false;
                    var pp = p.transform.position;
                    sb.Append($"{{\"id\":{p.PlayerId},\"active\":{B(p.gameObject.activeSelf)},\"pos\":[{F(pp.x)},{F(pp.y)},{F(pp.z)}],\"renderersOn\":{p.RenderersOn()}}}");
                }
                sb.Append("],\"events\":[");
                for (int i = 0; i < _events.Count; i++) { if (i > 0) sb.Append(','); sb.Append('"').Append(Esc(_events[i])).Append('"'); }
                sb.Append("]}");
                string tmp = StatePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(StatePath)) File.Delete(StatePath);
                File.Move(tmp, StatePath);
            }
            catch (Exception e) { Plugin.L.LogDebug("[auto] state: " + e.Message); }
        }

        static float Fl(Cmd c, int i, float def) =>
            c.Args.Length > i && float.TryParse(c.Args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
        static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        static string B(bool b) => b ? "true" : "false";
        static string V(Vector3 v) => $"({F(v.x)},{F(v.y)},{F(v.z)})";
        static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
