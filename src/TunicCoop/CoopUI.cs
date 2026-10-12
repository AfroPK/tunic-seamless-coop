using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace TunicCoop
{
    /// <summary>
    /// The co-op UI: status panel, toasts, nameplates over other players, and an F1 menu to host/join without
    /// editing config files. Built with uGUI + TextMeshPro using TUNIC's own "Latin Rounded" font.
    ///
    /// Rendered by our own overlay camera (Screen Space - Camera canvas) instead of Screen Space - Overlay:
    /// TUNIC's tilt-shift/DOF post-processing never touches it, and the test autopilot's camera-based
    /// screenshots capture it. (IMGUI was dropped: GUI calls only work inside OnGUI and look out of place.)
    /// </summary>
    public static class CoopUI
    {
        const int UiLayer = 31;
        static readonly Color Panel = new Color(0.06f, 0.05f, 0.11f, 0.78f);
        static readonly Color PanelEdge = new Color(1f, 0.78f, 0.35f, 0.9f);
        static readonly Color Dim = new Color(0.72f, 0.7f, 0.8f, 1f);
        static readonly Color Good = new Color(0.45f, 0.9f, 0.5f, 1f);
        static readonly Color Info = new Color(0.45f, 0.8f, 1f, 1f);
        static readonly Color Warn = new Color(1f, 0.75f, 0.3f, 1f);
        static readonly Color Bad = new Color(1f, 0.42f, 0.4f, 1f);
        static readonly Color Off = new Color(0.55f, 0.55f, 0.6f, 1f);
        public static readonly Color[] PlayerColors =
        {
            new Color(1f, 0.6f, 0.25f), new Color(0.4f, 0.8f, 1f), new Color(0.55f, 0.95f, 0.45f), new Color(1f, 0.5f, 0.75f),
            new Color(1f, 0.9f, 0.35f), new Color(0.7f, 0.55f, 1f), new Color(0.35f, 0.95f, 0.85f), new Color(1f, 0.45f, 0.4f),
        };

        static GameObject _root;
        static Camera _cam;
        static RectTransform _canvasRt;
        static TMP_FontAsset _font;
        static bool _built, _failed;

        // HUD
        static RectTransform _hud;
        static Image _hudDot;
        static TextMeshProUGUI _hudTitle, _hudStatus, _hudLine, _hudFooter;
        class Row { public RectTransform Rt; public Image Dot; public TextMeshProUGUI Name, Area, Ping; public Image HpBg, Hp; }
        static readonly List<Row> _rows = new List<Row>();
        static float _nextHud;

        // Toasts
        class ToastItem { public RectTransform Rt; public CanvasGroup Cg; public float Born; public float Life; }
        static readonly List<ToastItem> _toasts = new List<ToastItem>();

        // Nameplates
        class Plate { public RectTransform Rt; public TextMeshProUGUI Text; public Image HpBg, Hp; }
        static readonly Dictionary<byte, Plate> _plates = new Dictionary<byte, Plate>();

        // Menu
        static RectTransform _menu;
        static readonly List<TextMeshProUGUI> _menuRows = new List<TextMeshProUGUI>();
        static readonly List<Image> _menuRowBg = new List<Image>();
        static TextMeshProUGUI _menuInfo;
        static int _sel;
        static string _editName, _editAddr, _editPort;
        static int _editMax;
        static bool _menuOpen;
        public static bool MenuOpen => _menuOpen;

        public enum Kind { Info, Good, Warn, Bad }

        // ---------------------------------------------------------------- build

        static bool Ensure()
        {
            if (_built) return true;
            if (_failed) return false;
            try
            {
                foreach (var o in Resources.FindObjectsOfTypeAll(UnhollowerRuntimeLib.Il2CppType.Of<TMP_FontAsset>()))
                {
                    var f = o.TryCast<TMP_FontAsset>();
                    if (f == null) continue;
                    if (f.name == "Latin Rounded") { _font = f; break; }
                    if (_font == null && f.name.StartsWith("Latin")) _font = f;
                }
                if (_font == null) return false;   // fonts load with the first scene; try again later

                _root = new GameObject("TunicCoop_UI");
                Object.DontDestroyOnLoad(_root);
                _root.transform.position = new Vector3(0, -10000, 0);   // far from anything the game renders

                var camGo = new GameObject("TunicCoop_UICamera");
                camGo.transform.SetParent(_root.transform, false);
                _cam = camGo.AddComponent<Camera>();
                _cam.clearFlags = CameraClearFlags.Depth;
                _cam.cullingMask = 1 << UiLayer;
                _cam.depth = 100;
                _cam.nearClipPlane = 0.1f; _cam.farClipPlane = 50f;
                _cam.orthographic = true;

                var canvasGo = new GameObject("TunicCoop_Canvas");
                canvasGo.transform.SetParent(_root.transform, false);
                canvasGo.layer = UiLayer;
                var canvas = canvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = _cam;
                canvas.planeDistance = 5f;
                var scaler = canvasGo.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = 0.5f;
                _canvasRt = canvasGo.GetComponent<RectTransform>();

                BuildHud();
                BuildMenu();
                BuildRevive();
                _built = true;
                Plugin.L.LogInfo($"[ui] built (font {_font.name})");
                return true;
            }
            catch (Exception e)
            {
                _failed = true;
                Plugin.L.LogError("[ui] build failed, falling back to log-only messages: " + e);
                return false;
            }
        }

        static RectTransform Node(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.layer = UiLayer;
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            return rt;
        }

        static RectTransform Box(string name, Transform parent, Color c)
        {
            var rt = Node(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = c; img.raycastTarget = false;
            return rt;
        }

        static TextMeshProUGUI Label(string name, Transform parent, float size, Color c, TextAlignmentOptions align = TextAlignmentOptions.Left)
        {
            var rt = Node(name, parent);
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            t.font = _font; t.fontSize = size; t.color = c; t.alignment = align;
            t.enableWordWrapping = false; t.overflowMode = TextOverflowModes.Ellipsis; t.raycastTarget = false;
            return t;
        }

        /// <summary>Anchor at a corner/edge of the parent and place by pixel offset/size (1920x1080 space).</summary>
        static void Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = anchor; rt.anchorMax = anchor; rt.pivot = pivot;
            rt.anchoredPosition = pos; rt.sizeDelta = size;
        }

        static void BuildHud()
        {
            _hud = Box("Hud", _canvasRt, Panel);
            Place(_hud, new Vector2(0, 1), new Vector2(0, 1), new Vector2(24, -24), new Vector2(470, 120));
            var edge = Box("Edge", _hud, PanelEdge);
            Place(edge, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, new Vector2(4, 120));
            edge.anchorMin = new Vector2(0, 0); edge.anchorMax = new Vector2(0, 1); edge.sizeDelta = new Vector2(4, 0);

            _hudTitle = Label("Title", _hud, 24, PanelEdge);
            Place(_hudTitle.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(18, -10), new Vector2(120, 32));
            _hudTitle.text = "CO-OP";
            var dot = Box("Dot", _hud, Off);
            Place(dot, new Vector2(0, 1), new Vector2(0, 1), new Vector2(108, -20), new Vector2(12, 12));
            _hudDot = dot.GetComponent<Image>();
            _hudStatus = Label("Status", _hud, 22, Color.white);
            Place(_hudStatus.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(128, -11), new Vector2(330, 32));
            _hudLine = Label("Line", _hud, 19, Dim);
            Place(_hudLine.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(18, -44), new Vector2(440, 28));
            _hudFooter = Label("Footer", _hud, 16, Dim);
            Place(_hudFooter.rectTransform, new Vector2(0, 0), new Vector2(0, 0), new Vector2(18, 8), new Vector2(440, 24));

            for (int i = 0; i < Plugin.HardPlayerCap; i++)
            {
                var r = new Row();
                r.Rt = Node("Row" + i, _hud);
                Place(r.Rt, new Vector2(0, 1), new Vector2(0, 1), new Vector2(18, -78 - i * 30), new Vector2(440, 28));
                var d = Box("Dot", r.Rt, Color.white); Place(d, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0), new Vector2(10, 10));
                r.Dot = d.GetComponent<Image>();
                r.Name = Label("Name", r.Rt, 20, Color.white); Place(r.Name.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(18, 0), new Vector2(150, 28));
                r.Area = Label("Area", r.Rt, 17, Dim); Place(r.Area.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(172, 0), new Vector2(150, 28));
                r.Ping = Label("Ping", r.Rt, 17, Dim, TextAlignmentOptions.Right); Place(r.Ping.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(318, 0), new Vector2(60, 28));
                var hb = Box("HpBg", r.Rt, new Color(1, 1, 1, 0.15f)); Place(hb, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(386, 0), new Vector2(54, 8));
                r.HpBg = hb.GetComponent<Image>();
                var h = Box("Hp", hb, Good); Place(h, new Vector2(0, 0.5f), new Vector2(0, 0.5f), Vector2.zero, new Vector2(54, 8));
                r.Hp = h.GetComponent<Image>();
                r.Rt.gameObject.SetActive(false);
                _rows.Add(r);
            }
        }

        // ---------------------------------------------------------------- per frame

        public static void Tick(Net net, string scene)
        {
            if (!Ensure()) return;
            try
            {
                HandleMenuKeys(net);
                if (Time.unscaledTime >= _nextHud) { _nextHud = Time.unscaledTime + 0.25f; UpdateHud(net, scene); }
                UpdateToasts();
                UpdatePlates(net, scene);
                UpdateRevive(net);
            }
            catch (Exception e) { Plugin.L.LogWarning("[ui] tick: " + e.Message); }
        }

        static void UpdateHud(Net net, string scene)
        {
            bool active = net.Active;
            string status; Color sc;
            if (!active) { status = "Offline"; sc = Off; }
            else if (net.IsHost) { status = $"Hosting  ·  {net.Players.Count}/{net.MaxPlayers}"; sc = Good; }
            else if (net.LocalId == 255) { status = "Connecting…"; sc = Warn; }
            else { status = $"Connected  ·  {net.Players.Count}/{net.MaxPlayers}"; sc = Info; }
            _hudStatus.text = status; _hudStatus.color = sc; _hudDot.color = sc;

            int n = 0;
            if (!active)
            {
                _hudLine.text = "Press F1 to host or join a session";
            }
            else if (net.IsHost)
            {
                var addrs = GameInfo.LocalAddresses();
                _hudLine.text = Plugin.StreamerMode.Value ? "Share your address privately  ·  hidden (streamer mode)" : $"Share:  {(addrs.Count > 0 ? addrs[0] : "your IP")} : {net.LastPort}";
            }
            else _hudLine.text = Plugin.StreamerMode.Value ? "Host:  hidden (streamer mode)" : $"Host:  {net.LastAddress} : {net.LastPort}";

            if (active && net.LocalId != 255)
            {
                var ids = new List<byte>(net.Players.Keys); ids.Sort();
                string myGame = GameInfo.Version;
                foreach (var id in ids)
                {
                    if (n >= _rows.Count) break;
                    var r = _rows[n++];
                    r.Rt.gameObject.SetActive(true);
                    bool me = id == net.LocalId;
                    r.Dot.color = PlayerColors[id % PlayerColors.Length];
                    // (TUNIC's font has no ♦/arrow glyphs: words only)
                    string name = net.Players[id] + (me ? " (you)" : "") + (id == Net.HostId ? "  <size=70%><color=#FFC75A>HOST</color></size>" : "");
                    bool mismatch = net.Meta.TryGetValue(id, out var meta) && meta.game != "?" && meta.game != myGame;
                    r.Name.text = name; r.Name.color = mismatch ? Warn : Color.white;
                    // area + hp
                    string area; int hp = 0, maxhp = 0;
                    if (me) { area = scene; var pc = PlayerCharacter.instance; if (pc != null) { try { hp = pc.hp; maxhp = pc.maxhp; } catch { } } }
                    else
                    {
                        var p = FindPuppet(id);
                        var s = p != null ? p.Latest : null;
                        area = s?.Scene ?? ""; if (s != null) { hp = s.Hp; maxhp = s.MaxHp; }
                    }
                    r.Area.text = (mismatch ? $"game {meta.game}  " : "") + GameInfo.FriendlyArea(area) + (!me && area == scene ? "" : "");
                    r.Area.color = !me && area != scene ? Dim * 0.85f : Dim;
                    int ping = net.PingTo(id);
                    r.Ping.text = me ? "" : (ping >= 0 ? ping + " ms" : "");
                    float f = maxhp > 0 ? Mathf.Clamp01((float)hp / maxhp) : 0f;
                    r.Hp.rectTransform.sizeDelta = new Vector2(54 * f, 8);
                    r.Hp.color = f > 0.5f ? Good : f > 0.25f ? Warn : Bad;
                    r.HpBg.gameObject.SetActive(maxhp > 0);
                }
            }
            for (int i = n; i < _rows.Count; i++) _rows[i].Rt.gameObject.SetActive(false);
            _hudFooter.text = active ? "F1 menu   ·   F9 leave" : $"Mod {Plugin.Version}   ·   game {GameInfo.Version} ({GameInfo.Store})";
            _hud.sizeDelta = new Vector2(470, 78 + n * 30 + 34);
        }

        static Puppet FindPuppet(byte id)
        {
            foreach (var p in PuppetRegistry.All()) if (p.PlayerId == id) return p;
            return null;
        }

        // ---------------------------------------------------------------- toasts

        public static void Toast(string msg, Kind kind = Kind.Info, float life = 5f)
        {
            if (kind == Kind.Bad && life < 10f) life = 10f;   // errors need time to read
            Plugin.L.LogMessage("[coop] " + msg);
            if (!Ensure()) return;
            try
            {
                var c = kind == Kind.Good ? Good : kind == Kind.Warn ? Warn : kind == Kind.Bad ? Bad : Info;
                var rt = Box("Toast", _canvasRt, Panel);
                var cg = rt.gameObject.AddComponent<CanvasGroup>();
                var bar = Box("Bar", rt, c); bar.anchorMin = new Vector2(0, 0); bar.anchorMax = new Vector2(0, 1); bar.pivot = new Vector2(0, 0.5f); bar.sizeDelta = new Vector2(6, 0); bar.anchoredPosition = Vector2.zero;
                var t = Label("Text", rt, 22, Color.white);
                t.enableWordWrapping = true; t.overflowMode = TextOverflowModes.Overflow;
                t.text = msg;
                t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one;
                t.rectTransform.offsetMin = new Vector2(22, 8); t.rectTransform.offsetMax = new Vector2(-16, -8);
                float h = msg.Length > 70 ? 76 : 48;
                Place(rt, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -24), new Vector2(720, h));
                _toasts.Insert(0, new ToastItem { Rt = rt, Cg = cg, Born = Time.unscaledTime, Life = life });
                while (_toasts.Count > 4) { Object.Destroy(_toasts[_toasts.Count - 1].Rt.gameObject); _toasts.RemoveAt(_toasts.Count - 1); }
            }
            catch (Exception e) { Plugin.L.LogWarning("[ui] toast: " + e.Message); }
        }

        static void UpdateToasts()
        {
            float y = -24, now = Time.unscaledTime;
            for (int i = 0; i < _toasts.Count; i++)
            {
                var t = _toasts[i];
                float age = now - t.Born;
                if (age > t.Life) { Object.Destroy(t.Rt.gameObject); _toasts.RemoveAt(i--); continue; }
                t.Cg.alpha = Mathf.Clamp01(age / 0.15f) * Mathf.Clamp01((t.Life - age) / 0.6f);
                var p = t.Rt.anchoredPosition; p.y = Mathf.Lerp(p.y, y, 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime)); t.Rt.anchoredPosition = p;
                y -= t.Rt.sizeDelta.y + 8;
            }
        }

        // ---------------------------------------------------------------- nameplates

        static void UpdatePlates(Net net, string scene)
        {
            var seen = new HashSet<byte>();
            var main = Camera.main;
            foreach (var p in PuppetRegistry.All())
            {
                if (p.PlayerId == 250) continue;   // loopback echo
                seen.Add(p.PlayerId);
                if (!_plates.TryGetValue(p.PlayerId, out var pl)) _plates[p.PlayerId] = pl = MakePlate(p.PlayerId);
                bool show = main != null && p.gameObject.activeInHierarchy && !_menuOpen;
                Vector3 sp = Vector3.zero;
                if (show) { sp = main.WorldToScreenPoint(p.transform.position + Vector3.up * 2.1f); show = sp.z > 0; }
                pl.Rt.gameObject.SetActive(show);
                if (!show) continue;
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRt, new Vector2(sp.x, sp.y), _cam, out var local))
                    pl.Rt.anchoredPosition = local;
                string name = net.Players.TryGetValue(p.PlayerId, out var nm) ? nm : p.PlayerName;
                bool down = ReviveSystem.IsDowned(p.PlayerId);
                pl.Text.text = down ? $"{name}  <size=80%>DOWNED</size>" : name;
                pl.Text.color = down ? Bad : PlayerColors[p.PlayerId % PlayerColors.Length];
                var s = p.Latest;
                var rp = ReviveSystem.ReviveProgress;
                float f = down ? (rp.target == p.PlayerId ? rp.progress : 0f)
                               : (s != null && s.MaxHp > 0 ? Mathf.Clamp01((float)s.Hp / s.MaxHp) : 1f);
                pl.Hp.rectTransform.sizeDelta = new Vector2(80 * f, 6);
                pl.Hp.color = down ? Info : f > 0.5f ? Good : f > 0.25f ? Warn : Bad;
            }
            foreach (var id in new List<byte>(_plates.Keys))
                if (!seen.Contains(id)) { Object.Destroy(_plates[id].Rt.gameObject); _plates.Remove(id); }
        }

        static Plate MakePlate(byte id)
        {
            var pl = new Plate();
            pl.Rt = Node("Plate" + id, _canvasRt);
            pl.Rt.anchorMin = pl.Rt.anchorMax = new Vector2(0.5f, 0.5f); pl.Rt.pivot = new Vector2(0.5f, 0f); pl.Rt.sizeDelta = new Vector2(200, 40);
            pl.Text = Label("Name", pl.Rt, 22, PlayerColors[id % PlayerColors.Length], TextAlignmentOptions.Center);
            pl.Text.fontStyle = FontStyles.Bold;
            pl.Text.outlineWidth = 0.25f; pl.Text.outlineColor = new Color32(0, 0, 0, 220);
            Place(pl.Text.rectTransform, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 10), new Vector2(240, 30));
            var bg = Box("HpBg", pl.Rt, new Color(0, 0, 0, 0.55f)); Place(bg, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 2), new Vector2(82, 8));
            pl.HpBg = bg.GetComponent<Image>();
            var hp = Box("Hp", bg, Good); Place(hp, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(1, 0), new Vector2(80, 6));
            pl.Hp = hp.GetComponent<Image>();
            return pl;
        }

        // ---------------------------------------------------------------- downed / revive

        static RectTransform _downed, _reviving, _reviveBar;
        static TextMeshProUGUI _downedTitle, _downedSub, _revivingText;

        static void BuildRevive()
        {
            _downed = Box("Downed", _canvasRt, new Color(0.25f, 0.02f, 0.05f, 0.82f));
            Place(_downed, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -170), new Vector2(820, 132));
            var bar = Box("Bar", _downed, Bad); Place(bar, new Vector2(0.5f, 1), new Vector2(0.5f, 1), Vector2.zero, new Vector2(820, 4));
            _downedTitle = Label("Title", _downed, 40, Bad, TextAlignmentOptions.Center);
            Place(_downedTitle.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -12), new Vector2(780, 48));
            _downedSub = Label("Sub", _downed, 22, Color.white, TextAlignmentOptions.Center);
            _downedSub.enableWordWrapping = true; _downedSub.overflowMode = TextOverflowModes.Overflow;
            Place(_downedSub.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -62), new Vector2(780, 64));
            _downed.gameObject.SetActive(false);

            _reviving = Box("Reviving", _canvasRt, Panel);
            Place(_reviving, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 180), new Vector2(460, 74));
            _revivingText = Label("Text", _reviving, 24, Color.white, TextAlignmentOptions.Center);
            Place(_revivingText.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -8), new Vector2(440, 32));
            var bg = Box("BarBg", _reviving, new Color(1, 1, 1, 0.15f)); Place(bg, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 14), new Vector2(400, 12));
            _reviveBar = Box("BarFill", bg, Info); Place(_reviveBar, new Vector2(0, 0.5f), new Vector2(0, 0.5f), Vector2.zero, new Vector2(0, 12));
            _reviving.gameObject.SetActive(false);
        }

        static void UpdateRevive(Net net)
        {
            bool down = ReviveSystem.Downed;
            _downed.gameObject.SetActive(down);
            if (down)
            {
                _downedTitle.text = "YOU'RE DOWN";
                var elsewhere = ReviveSystem.ElsewhereUp();
                string spec = ReviveSystem.Spectating != 255
                    ? $"Spectating <color=#FFC75A>{ReviveSystem.Name(ReviveSystem.Spectating)}</color>   ·   Left / Right to switch"
                    : (elsewhere.Count > 0 ? "No teammate in this area. Up elsewhere: " + string.Join(", ", elsewhere) : "Waiting for a teammate…");
                _downedSub.text = spec + "\nA teammate can revive you by standing next to you   ·   hold F2 to respawn at your campfire";
            }
            var rp = ReviveSystem.ReviveProgress;
            bool reviving = !down && rp.target != 255 && rp.progress > 0f;
            _reviving.gameObject.SetActive(reviving);
            if (reviving)
            {
                _revivingText.text = $"Reviving {ReviveSystem.Name(rp.target)}…";
                _reviveBar.sizeDelta = new Vector2(400 * Mathf.Clamp01(rp.progress), 12);
            }
        }

        // ---------------------------------------------------------------- menu (F1)

        static readonly string[] MenuLabels = { "Your name", "Join address", "Port", "Max players", "Streamer mode", "Host a session", "Join", "Leave session", "Close" };
        const int R_NAME = 0, R_ADDR = 1, R_PORT = 2, R_MAX = 3, R_STREAM = 4, R_HOST = 5, R_JOIN = 6, R_LEAVE = 7, R_CLOSE = 8;

        static void BuildMenu()
        {
            _menu = Box("Menu", _canvasRt, new Color(0.05f, 0.04f, 0.1f, 0.92f));
            Place(_menu, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(760, 620));
            var top = Box("Top", _menu, PanelEdge); Place(top, new Vector2(0.5f, 1), new Vector2(0.5f, 1), Vector2.zero, new Vector2(760, 4));
            var title = Label("Title", _menu, 40, PanelEdge, TextAlignmentOptions.Center);
            Place(title.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -20), new Vector2(700, 50));
            title.text = "TUNIC Co-op";
            for (int i = 0; i < MenuLabels.Length; i++)
            {
                var bg = Box("RowBg" + i, _menu, new Color(1, 1, 1, 0f));
                Place(bg, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -88 - i * 46), new Vector2(680, 42));
                _menuRowBg.Add(bg.GetComponent<Image>());
                var t = Label("Row" + i, bg, 24, Color.white);
                t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one;
                t.rectTransform.offsetMin = new Vector2(18, 0); t.rectTransform.offsetMax = new Vector2(-18, 0);
                _menuRows.Add(t);
            }
            _menuInfo = Label("Info", _menu, 17, Dim, TextAlignmentOptions.Center);
            _menuInfo.enableWordWrapping = true; _menuInfo.overflowMode = TextOverflowModes.Overflow;
            Place(_menuInfo.rectTransform, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 14), new Vector2(700, 70));
            _menu.gameObject.SetActive(false);
        }

        static bool _prevLock;

        public static void OpenMenu(bool open)
        {
            if (!Ensure() || open == _menuOpen) return;
            _menuOpen = open;
            _menu.gameObject.SetActive(open);
            if (open)
            {
                _editName = Plugin.PlayerName.Value; _editAddr = Plugin.JoinAddress.Value;
                _editPort = Plugin.Port.Value.ToString(CultureInfo.InvariantCulture); _editMax = Plugin.MaxPlayers.Value;
                // First time with the default name: start on the name field so players pick what others see.
                _sel = !Plugin.NameChosen.Value ? R_NAME : CoopManager.NetOrNull != null && CoopManager.NetOrNull.Active ? R_LEAVE : R_HOST;
                _prevLock = PlayerCharacter._cutsceneLocked;
                RefreshMenu(CoopManager.NetOrNull);
            }
            else
            {
                PlayerCharacter._cutsceneLocked = _prevLock;
                SaveEdits();
            }
        }

        static void SaveEdits()
        {
            if (!string.IsNullOrWhiteSpace(_editName))
            {
                string nn = _editName.Trim();
                if (nn != Plugin.PlayerName.Value)
                {
                    Plugin.PlayerName.Value = nn;
                    Plugin.NameChosen.Value = true;
                    var net = CoopManager.NetOrNull;
                    if (net != null && net.Active) net.Rename(nn);   // live: everyone sees the new name now
                }
            }
            if (!string.IsNullOrWhiteSpace(_editAddr)) Plugin.JoinAddress.Value = _editAddr.Trim();
            if (int.TryParse(_editPort, out var port) && port > 0 && port < 65536) Plugin.Port.Value = port;
            Plugin.MaxPlayers.Value = Mathf.Clamp(_editMax, 2, Plugin.HardPlayerCap);
        }

        static bool RowEnabled(int i, Net net)
        {
            bool active = net != null && net.Active;
            if (i == R_LEAVE) return active;
            if (i == R_HOST || i == R_JOIN) return !active;
            return true;
        }

        static void RefreshMenu(Net net)
        {
            for (int i = 0; i < MenuLabels.Length; i++)
            {
                bool hide = Plugin.StreamerMode.Value;
                string v = i == R_NAME ? _editName
                         : i == R_ADDR ? (hide ? new string('*', _editAddr.Length) : _editAddr)
                         : i == R_PORT ? _editPort
                         : i == R_MAX ? $"‹ {_editMax} ›"
                         : i == R_STREAM ? (hide ? "‹ On ›  <size=75%><color=#B8B3CC>addresses hidden</color></size>" : "‹ Off ›") : null;
                bool editing = i == _sel && i <= R_PORT;
                string text = v != null ? $"{MenuLabels[i]}<pos=42%>{v}{(editing && (Time.unscaledTime % 1f) < 0.55f ? "_" : "")}" : MenuLabels[i];
                bool en = RowEnabled(i, net);
                _menuRows[i].text = text;
                _menuRows[i].alignment = v != null ? TextAlignmentOptions.Left : TextAlignmentOptions.Center;
                _menuRows[i].color = !en ? new Color(1, 1, 1, 0.3f) : i == _sel ? PanelEdge : Color.white;
                _menuRowBg[i].color = i == _sel ? new Color(1f, 0.78f, 0.35f, 0.14f) : new Color(1, 1, 1, i <= R_STREAM ? 0.04f : 0f);
            }
            var addrs = GameInfo.LocalAddresses();
            string namePrompt = !Plugin.NameChosen.Value && _sel == 0 ? "<color=#FFC75A>Choose the name other players will see — type it, then press Enter.</color>\n" : "";
            _menuInfo.text = namePrompt + (Plugin.StreamerMode.Value
                                 ? "Your address is hidden (streamer mode). Turn it off briefly to see it, or send it to friends privately.\n"
                                 : $"Your address{(addrs.Count > 1 ? "es" : "")}: {(addrs.Count > 0 ? string.Join(", ", addrs) : "unknown")}   ·   share it with friends to let them join\n") +
                             $"Game {GameInfo.Version} ({GameInfo.Store})   ·   Mod {Plugin.Version}   ·   works with any PC store\n" +
                             "Up/Down select   ·   type to edit   ·   Left/Right change   ·   Enter confirm   ·   F1 close";
        }

        /// <summary>Keyboard driving of the menu. Also called by the test autopilot (ui key/type).</summary>
        public static void Key(KeyCode k, Net net)
        {
            if (k == KeyCode.F1) { OpenMenu(!_menuOpen); return; }
            if (!_menuOpen) return;
            int n = MenuLabels.Length;
            if (k == KeyCode.UpArrow) { do { _sel = (_sel + n - 1) % n; } while (!RowEnabled(_sel, net)); }
            else if (k == KeyCode.DownArrow) { do { _sel = (_sel + 1) % n; } while (!RowEnabled(_sel, net)); }
            else if (_sel == R_MAX && (k == KeyCode.LeftArrow || k == KeyCode.RightArrow)) _editMax = Mathf.Clamp(_editMax + (k == KeyCode.RightArrow ? 1 : -1), 2, Plugin.HardPlayerCap);
            else if (_sel == R_STREAM && (k == KeyCode.LeftArrow || k == KeyCode.RightArrow || k == KeyCode.Return || k == KeyCode.KeypadEnter))
                Plugin.StreamerMode.Value = !Plugin.StreamerMode.Value;
            else if (k == KeyCode.Backspace && _sel <= R_PORT)
            {
                if (_sel == 0 && _editName.Length > 0) _editName = _editName.Substring(0, _editName.Length - 1);
                if (_sel == 1 && _editAddr.Length > 0) _editAddr = _editAddr.Substring(0, _editAddr.Length - 1);
                if (_sel == 2 && _editPort.Length > 0) _editPort = _editPort.Substring(0, _editPort.Length - 1);
            }
            else if (k == KeyCode.Return || k == KeyCode.KeypadEnter)
            {
                if (_sel <= R_MAX) { _sel = Mathf.Min(_sel + 1, n - 1); while (!RowEnabled(_sel, net)) _sel = (_sel + 1) % n; }
                else
                {
                    SaveEdits();
                    var mgr = CoopManager.Instance;
                    if (_sel == R_HOST) { OpenMenu(false); mgr.StartHost(); }
                    else if (_sel == R_JOIN) { OpenMenu(false); mgr.StartJoin(null); }
                    else if (_sel == R_LEAVE) { OpenMenu(false); mgr.Leave(); }
                    else OpenMenu(false);
                    return;
                }
            }
            RefreshMenu(net);
        }

        public static void Type(string s, Net net)
        {
            if (!_menuOpen || _sel > R_PORT || string.IsNullOrEmpty(s)) return;
            foreach (char ch in s)
            {
                if (ch == '\b' || ch == '\n' || ch == '\r') continue;
                if (_sel == 0 && _editName.Length < 20 && (char.IsLetterOrDigit(ch) || " _-.".IndexOf(ch) >= 0)) _editName += ch;
                if (_sel == 1 && _editAddr.Length < 64 && (char.IsLetterOrDigit(ch) || ".:-".IndexOf(ch) >= 0)) _editAddr += ch;
                if (_sel == 2 && _editPort.Length < 5 && char.IsDigit(ch)) _editPort += ch;
            }
            RefreshMenu(net);
        }

        static readonly KeyCode[] MenuKeys = { KeyCode.F1, KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.LeftArrow, KeyCode.RightArrow, KeyCode.Backspace, KeyCode.Return, KeyCode.KeypadEnter };

        static void HandleMenuKeys(Net net)
        {
            foreach (var k in MenuKeys) { bool down; try { down = Input.GetKeyDown(k); } catch { return; } if (down) Key(k, net); }
            if (_menuOpen)
            {
                PlayerCharacter._cutsceneLocked = true;   // typing must not move the fox
                string typed = null; try { typed = Input.inputString; } catch { }
                if (!string.IsNullOrEmpty(typed)) Type(typed, net);
                RefreshMenu(net);                          // caret blink
            }
        }

        public static string Describe() =>
            $"built={_built} menu={_menuOpen} sel={_sel} rows={_rows.FindAll(r => r.Rt.gameObject.activeSelf).Count} toasts={_toasts.Count} plates={_plates.Count} name={_editName} addr={_editAddr}";
    }
}
