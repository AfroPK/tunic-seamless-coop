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
    /// The co-op UI: status panel, toasts, nameplates (with quick-chat bubbles), pings and screen-edge markers,
    /// a chat log, the downed/revive banners and the tabbed F1 menu. Built with uGUI + TextMeshPro using TUNIC's
    /// own "Latin Rounded" font.
    ///
    /// Rendered by our own overlay camera (Screen Space - Camera canvas) instead of Screen Space - Overlay:
    /// TUNIC's tilt-shift/DOF post-processing never touches it, and the test autopilot's camera-based
    /// screenshots capture it. (TUNIC's font has no arrow/diamond glyphs: shapes are drawn as rotated boxes.)
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
        static TextMeshProUGUI _hudTitle, _hudStatus, _hudLine, _hudLine2, _hudFooter;
        class Row { public RectTransform Rt; public Image Dot; public TextMeshProUGUI Name, Area, Ping; public Image HpBg, Hp; }
        static readonly List<Row> _rows = new List<Row>();
        static float _nextHud;

        // Toasts
        class ToastItem { public RectTransform Rt; public CanvasGroup Cg; public float Born; public float Life; }
        static readonly List<ToastItem> _toasts = new List<ToastItem>();

        // Nameplates
        class Plate { public RectTransform Rt; public TextMeshProUGUI Text, Bubble; public Image HpBg, Hp; }
        static readonly Dictionary<byte, Plate> _plates = new Dictionary<byte, Plate>();

        // Markers (pings, downed teammates) and chat
        class Marker { public RectTransform Rt; public RectTransform Diamond; public Image DiamondImg; public TextMeshProUGUI Label; }
        static readonly List<Marker> _markers = new List<Marker>();
        static RectTransform _chatLog, _chatPicker;
        static readonly List<TextMeshProUGUI> _chatLines = new List<TextMeshProUGUI>();
        static TextMeshProUGUI _chatPickerText;

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
                BuildChat();
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
            _hudLine2 = Label("Line2", _hud, 17, Dim);
            Place(_hudLine2.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(18, -70), new Vector2(440, 24));
            _hudFooter = Label("Footer", _hud, 16, Dim);
            Place(_hudFooter.rectTransform, new Vector2(0, 0), new Vector2(0, 0), new Vector2(18, 8), new Vector2(440, 24));

            for (int i = 0; i < Plugin.HardPlayerCap; i++)
            {
                var r = new Row();
                r.Rt = Node("Row" + i, _hud);
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

        static void BuildChat()
        {
            _chatLog = Node("ChatLog", _canvasRt);
            Place(_chatLog, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-28, 150), new Vector2(520, 200));
            for (int i = 0; i < 6; i++)
            {
                var t = Label("Line" + i, _chatLog, 21, Color.white, TextAlignmentOptions.Right);
                t.outlineWidth = 0.2f; t.outlineColor = new Color32(0, 0, 0, 200);
                Place(t.rectTransform, new Vector2(1, 0), new Vector2(1, 0), new Vector2(0, i * 30), new Vector2(520, 28));
                _chatLines.Add(t);
            }
            _chatPicker = Box("ChatPicker", _canvasRt, Panel);
            Place(_chatPicker, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-28, 360), new Vector2(300, 250));
            _chatPickerText = Label("Text", _chatPicker, 21, Color.white);
            _chatPickerText.enableWordWrapping = true; _chatPickerText.overflowMode = TextOverflowModes.Overflow;
            _chatPickerText.rectTransform.anchorMin = Vector2.zero; _chatPickerText.rectTransform.anchorMax = Vector2.one;
            _chatPickerText.rectTransform.offsetMin = new Vector2(16, 10); _chatPickerText.rectTransform.offsetMax = new Vector2(-12, -10);
            _chatPicker.gameObject.SetActive(false);
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
                UpdateMarkers(net, scene);
                UpdateChat();
                UpdateRevive(net);
            }
            catch (Exception e) { Plugin.L.LogWarning("[ui] tick: " + e.Message); }
        }

        /// <summary>Join code for this host: internet code when the router forwarded the port (or a public
        /// address is known), plus the LAN/VPN one.</summary>
        public static (string internet, string lan, bool forwarded) HostCodes(Net net)
        {
            int port = net != null && net.LastPort > 0 ? net.LastPort : Plugin.Port.Value;
            string ext = PortMapper.ExternalIp ?? PublicAddress.Ip;
            string internet = null, lan = null;
            try { if (ext != null) internet = JoinCode.Encode(System.Net.IPAddress.Parse(ext), port); } catch { }
            var addrs = GameInfo.LocalAddresses();
            try { if (addrs.Count > 0) lan = JoinCode.Encode(System.Net.IPAddress.Parse(addrs[0]), port); } catch { }
            return (internet, lan, PortMapper.Status == PortMapper.State.Mapped);
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

            bool hide = Plugin.StreamerMode.Value;
            string line2 = null;
            if (!active)
            {
                _hudLine.text = "Press F1 to host or join a session";
            }
            else if (net.IsHost)
            {
                var codes = HostCodes(net);
                var addrs = GameInfo.LocalAddresses();
                if (hide) _hudLine.text = "Join code hidden (streamer mode)  ·  share it privately";
                else _hudLine.text = $"Code  <color=#FFC75A>{codes.internet ?? codes.lan ?? "?"}</color>   ·   {(addrs.Count > 0 ? addrs[0] : "your IP")} : {net.LastPort}";
                line2 = PortMapper.Status == PortMapper.State.Working ? "Setting up internet access…"
                      : codes.forwarded ? "Friends anywhere can join with the code"
                      : codes.internet != null ? "Code needs UDP " + net.LastPort + " forwarded (or use LAN / a VPN)"
                      : "Same network or VPN: share the address";
            }
            else _hudLine.text = hide ? "Host:  hidden (streamer mode)" : $"Host:  {net.LastAddress} : {net.LastPort}";
            if (UpdateCheck.Latest != null) line2 = $"<color=#7FCBFF>Update {UpdateCheck.Latest} available</color>" + (line2 != null ? "  ·  " + line2 : "");
            _hudLine2.text = line2 ?? "";
            float top = line2 != null ? 98 : 78;

            int n = 0;
            if (active && net.LocalId != 255)
            {
                var ids = new List<byte>(net.Players.Keys); ids.Sort();
                string myGame = GameInfo.Version;
                foreach (var id in ids)
                {
                    if (n >= _rows.Count) break;
                    var r = _rows[n];
                    Place(r.Rt, new Vector2(0, 1), new Vector2(0, 1), new Vector2(18, -top - n * 30), new Vector2(440, 28));
                    n++;
                    r.Rt.gameObject.SetActive(true);
                    bool me = id == net.LocalId;
                    r.Dot.color = PlayerColors[id % PlayerColors.Length];
                    string name = net.Players[id] + (me ? " (you)" : "") + (id == Net.HostId ? "  <size=70%><color=#FFC75A>HOST</color></size>" : "");
                    bool mismatch = net.Meta.TryGetValue(id, out var meta) && meta.game != "?" && meta.game != myGame;
                    r.Name.text = name; r.Name.color = mismatch ? Warn : Color.white;
                    string area; int hp = 0, maxhp = 0;
                    if (me) { area = scene; var pc = PlayerCharacter.instance; if (pc != null) { try { hp = pc.hp; maxhp = pc.maxhp; } catch { } } }
                    else
                    {
                        var p = FindPuppet(id);
                        var s = p != null ? p.Latest : null;
                        area = s?.Scene ?? ""; if (s != null) { hp = s.Hp; maxhp = s.MaxHp; }
                    }
                    bool down = me ? ReviveSystem.Downed : ReviveSystem.IsDowned(id);
                    r.Area.text = down ? "<color=#FF6B66>DOWNED</color>" : (mismatch ? $"game {meta.game}  " : "") + GameInfo.FriendlyArea(area);
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
            _hudFooter.text = active ? "F1 menu  ·  F3 ping  ·  F4 chat  ·  F5 go to teammate" : $"Mod {Plugin.Version}   ·   game {GameInfo.Version} ({GameInfo.Store})";
            _hud.sizeDelta = new Vector2(470, top + n * 30 + 34);
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

        // ---------------------------------------------------------------- nameplates (+ quick-chat bubbles)

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
                string bubble = Signals.BubbleFor(p.PlayerId);
                pl.Bubble.gameObject.SetActive(bubble != null);
                if (bubble != null) pl.Bubble.text = "“" + bubble + "”";
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
            pl.Bubble = Label("Bubble", pl.Rt, 24, Color.white, TextAlignmentOptions.Center);
            pl.Bubble.outlineWidth = 0.25f; pl.Bubble.outlineColor = new Color32(0, 0, 0, 230);
            Place(pl.Bubble.rectTransform, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 40), new Vector2(320, 32));
            pl.Bubble.gameObject.SetActive(false);
            return pl;
        }

        // ---------------------------------------------------------------- pings + screen-edge markers

        static Marker GetMarker(int i)
        {
            while (_markers.Count <= i)
            {
                var m = new Marker();
                m.Rt = Node("Marker" + _markers.Count, _canvasRt);
                m.Rt.anchorMin = m.Rt.anchorMax = new Vector2(0.5f, 0.5f); m.Rt.sizeDelta = new Vector2(10, 10);
                m.Diamond = Box("Diamond", m.Rt, Warn); Place(m.Diamond, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(22, 22));
                m.Diamond.localRotation = Quaternion.Euler(0, 0, 45);
                m.DiamondImg = m.Diamond.GetComponent<Image>();
                var inner = Box("Inner", m.Diamond, new Color(0, 0, 0, 0.55f)); Place(inner, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(10, 10));
                m.Label = Label("Label", m.Rt, 20, Color.white, TextAlignmentOptions.Center);
                m.Label.outlineWidth = 0.25f; m.Label.outlineColor = new Color32(0, 0, 0, 230);
                Place(m.Label.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0f), new Vector2(0, 16), new Vector2(300, 28));
                _markers.Add(m);
            }
            return _markers[i];
        }

        static void UpdateMarkers(Net net, string scene)
        {
            int used = 0;
            var main = Camera.main;
            var pc = PlayerCharacter.instance;
            if (main != null && pc != null && !_menuOpen)
            {
                float now = Time.unscaledTime;
                foreach (var p in Signals.Pings)
                {
                    if (p.Scene != scene) continue;
                    float age = now - p.At;
                    float pulse = 1f + 0.25f * Mathf.Sin(age * 8f) * Mathf.Clamp01(2f - age);
                    string who = net.Players.TryGetValue(p.From, out var nm) ? nm : "?";
                    ShowMarker(GetMarker(used++), main, p.Pos + Vector3.up * 0.5f, PlayerColors[p.From % PlayerColors.Length], $"{who}  {Mathf.RoundToInt((p.Pos - pc.transform.position).magnitude)} m", pulse, true);
                }
                // Downed teammates in this area: always findable, on screen or not.
                foreach (var id in ReviveSystem.DownedPlayers)
                {
                    var pp = FindPuppet(id);
                    if (pp == null || pp.Latest == null || pp.Latest.Scene != scene || !pp.gameObject.activeSelf) continue;
                    string who = net.Players.TryGetValue(id, out var nm) ? nm : "?";
                    ShowMarker(GetMarker(used++), main, pp.transform.position + Vector3.up * 1f, Bad, $"{who} is down  {Mathf.RoundToInt((pp.transform.position - pc.transform.position).magnitude)} m", 1f, false);
                }
            }
            for (int i = used; i < _markers.Count; i++) _markers[i].Rt.gameObject.SetActive(false);
        }

        /// <summary>On screen: a marker at the spot. Off screen: clamped to the screen edge, pointing toward it.</summary>
        static void ShowMarker(Marker m, Camera main, Vector3 world, Color c, string label, float scale, bool showOnScreen)
        {
            var sp = main.WorldToScreenPoint(world);
            bool behind = sp.z < 0;
            if (behind) sp = -sp;
            float w = Screen.width, h = Screen.height, margin = 60f * h / 1080f;
            bool onScreen = !behind && sp.x > margin && sp.x < w - margin && sp.y > margin && sp.y < h - margin;
            if (onScreen && !showOnScreen) { m.Rt.gameObject.SetActive(false); return; }
            var centre = new Vector2(w / 2, h / 2);
            var p = new Vector2(sp.x, sp.y);
            if (!onScreen)
            {
                var d = p - centre; if (d.sqrMagnitude < 1f) d = Vector2.up;
                float k = Mathf.Min((w / 2 - margin) / Mathf.Max(1e-3f, Mathf.Abs(d.x)), (h / 2 - margin) / Mathf.Max(1e-3f, Mathf.Abs(d.y)));
                p = centre + d * k;
                m.Diamond.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg - 45f);   // a corner points at it
            }
            else m.Diamond.localRotation = Quaternion.Euler(0, 0, 45);
            m.Rt.gameObject.SetActive(true);
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRt, p, _cam, out var local)) m.Rt.anchoredPosition = local;
            m.DiamondImg.color = c;
            m.Diamond.localScale = Vector3.one * scale;
            m.Label.text = label; m.Label.color = c;
        }

        // ---------------------------------------------------------------- chat

        static void UpdateChat()
        {
            float now = Time.unscaledTime;
            var log = Signals.Log;
            for (int i = 0; i < _chatLines.Count; i++)
            {
                int li = log.Count - 1 - i;
                var t = _chatLines[i];
                if (li < 0 || now - log[li].at > 12f) { t.gameObject.SetActive(false); continue; }
                t.gameObject.SetActive(true);
                var e = log[li];
                t.text = $"<color=#{ColorUtility.ToHtmlStringRGB(e.color)}>{e.who}</color>: {e.text}";
                t.alpha = Mathf.Clamp01((12f - (now - e.at)) / 1.5f);
            }
            _chatPicker.gameObject.SetActive(Signals.ChatOpen && !_menuOpen);
            if (Signals.ChatOpen)
            {
                var sb = new System.Text.StringBuilder("<color=#FFC75A>Quick chat</color>\n");
                for (int i = 0; i < Signals.Phrases.Length; i++) sb.Append($"<color=#FFC75A>{i + 1}</color>  {Signals.Phrases[i]}\n");
                sb.Append("<size=80%><color=#B8B3CC>F4 / Esc to close</color></size>");
                _chatPickerText.text = sb.ToString();
            }
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
                float left = ReviveSystem.BleedOutLeft;
                _downedTitle.text = float.IsInfinity(left) ? "YOU'RE DOWN" : $"YOU'RE DOWN  <size=70%>{Mathf.CeilToInt(Mathf.Max(0, left))} s</size>";
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

        // ---------------------------------------------------------------- menu (F1): tabs of rows

        class Item
        {
            public string Label;
            public Func<string> Value;          // null: a button
            public Action<int> Change;          // Left/Right (and Enter on toggles)
            public Action Enter;                // buttons
            public int Field = -1;              // text field index (0 name, 1 address, 2 port)
            public Func<bool> Enabled;
            public string Help;
        }

        static readonly string[] Tabs = { "Session", "Rules", "Players", "Options" };
        const int MaxRows = 10;
        static RectTransform _menu;
        static readonly List<TextMeshProUGUI> _tabLabels = new List<TextMeshProUGUI>();
        static readonly List<TextMeshProUGUI> _menuRows = new List<TextMeshProUGUI>();
        static readonly List<Image> _menuRowBg = new List<Image>();
        static TextMeshProUGUI _menuInfo;
        static int _tab, _sel;
        static string _editName = "", _editAddr = "", _editPort = "";
        static int _editMax;
        static bool _menuOpen;
        public static bool MenuOpen => _menuOpen;
        static List<Item> _items = new List<Item>();

        static void BuildMenu()
        {
            _menu = Box("Menu", _canvasRt, new Color(0.05f, 0.04f, 0.1f, 0.94f));
            Place(_menu, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(820, 720));
            var top = Box("Top", _menu, PanelEdge); Place(top, new Vector2(0.5f, 1), new Vector2(0.5f, 1), Vector2.zero, new Vector2(820, 4));
            var title = Label("Title", _menu, 38, PanelEdge, TextAlignmentOptions.Center);
            Place(title.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -16), new Vector2(760, 46));
            title.text = "TUNIC Co-op";
            for (int i = 0; i < Tabs.Length; i++)
            {
                var t = Label("Tab" + i, _menu, 24, Dim, TextAlignmentOptions.Center);
                Place(t.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(-285 + i * 190, -72), new Vector2(180, 34));
                _tabLabels.Add(t);
            }
            var line = Box("TabLine", _menu, new Color(1, 1, 1, 0.12f)); Place(line, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -110), new Vector2(760, 2));
            for (int i = 0; i < MaxRows; i++)
            {
                var bg = Box("RowBg" + i, _menu, new Color(1, 1, 1, 0f));
                Place(bg, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -124 - i * 46), new Vector2(740, 42));
                _menuRowBg.Add(bg.GetComponent<Image>());
                var t = Label("Row" + i, bg, 24, Color.white);
                t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one;
                t.rectTransform.offsetMin = new Vector2(18, 0); t.rectTransform.offsetMax = new Vector2(-18, 0);
                _menuRows.Add(t);
            }
            _menuInfo = Label("Info", _menu, 17, Dim, TextAlignmentOptions.Center);
            _menuInfo.enableWordWrapping = true; _menuInfo.overflowMode = TextOverflowModes.Overflow;
            Place(_menuInfo.rectTransform, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 12), new Vector2(760, 100));
            _menu.gameObject.SetActive(false);
        }

        static Net N => CoopManager.NetOrNull;
        static bool Active => N != null && N.Active;
        static bool CanEditRules => !Active || N.IsHost;

        static string Toggle(bool on, string onText = "On", string offText = "Off") => $"‹ {(on ? onText : offText)} ›";
        static int Step(int v, int dir, int[] steps) { int i = Array.IndexOf(steps, v); if (i < 0) i = 0; return steps[Mathf.Clamp(i + dir, 0, steps.Length - 1)]; }
        static void RulesChanged() { if (Active && N.IsHost) CoopManager.Instance?.SendSettings(); else HostSettings.LoadLocal(); }

        static List<Item> BuildItems(int tab)
        {
            var l = new List<Item>();
            switch (tab)
            {
                case 0:
                    l.Add(new Item { Label = "Your name", Value = () => _editName, Field = 0, Help = "What other players see above your fox." });
                    l.Add(new Item { Label = "Join address or code", Value = () => Plugin.StreamerMode.Value ? new string('*', _editAddr.Length) : _editAddr, Field = 1,
                                     Help = "The host's join code (like 4T7JA-E1QJK) or address." });
                    l.Add(new Item { Label = "Port", Value = () => _editPort, Field = 2, Help = "UDP port (27015 unless the host changed it)." });
                    l.Add(new Item { Label = "Max players", Value = () => $"‹ {_editMax} ›", Change = d => _editMax = Mathf.Clamp(_editMax + d, 2, Plugin.HardPlayerCap), Help = "Including the host (2-8)." });
                    l.Add(new Item { Label = "Host a session", Enter = () => { OpenMenu(false); CoopManager.Instance.StartHost(); }, Enabled = () => !Active });
                    l.Add(new Item { Label = "Join", Enter = () => { OpenMenu(false); CoopManager.Instance.StartJoin(null); }, Enabled = () => !Active });
                    l.Add(new Item { Label = "Leave session", Enter = () => { OpenMenu(false); CoopManager.Instance.Leave(); }, Enabled = () => Active });
                    l.Add(new Item { Label = "Close", Enter = () => OpenMenu(false) });
                    break;
                case 1:
                    l.Add(new Item { Label = "Revive teammates", Value = () => Toggle(CanEditRules ? Plugin.Revive.Value : HostSettings.Revive),
                                     Change = d => { Plugin.Revive.Value = !Plugin.Revive.Value; RulesChanged(); }, Enabled = () => CanEditRules,
                                     Help = "Downed instead of dead while a teammate is up; they revive you by standing next to you." });
                    l.Add(new Item { Label = "Revive time", Value = () => $"‹ {(CanEditRules ? Plugin.ReviveSeconds.Value : HostSettings.ReviveSeconds):0.#} s ›",
                                     Change = d => { Plugin.ReviveSeconds.Value = Mathf.Clamp(Plugin.ReviveSeconds.Value + d, 1f, 10f); RulesChanged(); }, Enabled = () => CanEditRules });
                    l.Add(new Item { Label = "Health after revive", Value = () => $"‹ {(CanEditRules ? Plugin.ReviveHealthPercent.Value : HostSettings.ReviveHealthPercent)} % ›",
                                     Change = d => { Plugin.ReviveHealthPercent.Value = Step(Plugin.ReviveHealthPercent.Value, d, new[] { 10, 25, 50, 75, 100 }); RulesChanged(); }, Enabled = () => CanEditRules });
                    l.Add(new Item { Label = "Bleed-out", Value = () => { int v = CanEditRules ? Plugin.BleedOutSeconds.Value : HostSettings.BleedOutSeconds; return v == 0 ? "‹ Never ›" : $"‹ {v} s ›"; },
                                     Change = d => { Plugin.BleedOutSeconds.Value = Step(Plugin.BleedOutSeconds.Value, d, new[] { 0, 15, 30, 60, 120 }); RulesChanged(); }, Enabled = () => CanEditRules,
                                     Help = "A downed player dies for real after this long without help." });
                    l.Add(new Item { Label = "Enemy toughness", Value = () => { int v = CanEditRules ? Plugin.EnemyHealthPerExtraPlayer.Value : HostSettings.EnemyHealthPerExtraPlayer; return v == 0 ? "‹ Vanilla ›" : $"‹ +{v}% per extra player ›"; },
                                     Change = d => { Plugin.EnemyHealthPerExtraPlayer.Value = Step(Plugin.EnemyHealthPerExtraPlayer.Value, d, new[] { 0, 25, 50, 75, 100, 150, 200 }); RulesChanged(); }, Enabled = () => CanEditRules,
                                     Help = "Enemies and bosses take longer to kill with more players (applies to enemies met after the change)." });
                    l.Add(new Item { Label = "Money & consumables", Value = () => Toggle(CanEditRules ? Plugin.ShareMoneyAndConsumables.Value : HostSettings.ShareMoneyAndConsumables, "Shared", "Each their own"),
                                     Change = d => { Plugin.ShareMoneyAndConsumables.Value = !Plugin.ShareMoneyAndConsumables.Value; RulesChanged(); }, Enabled = () => CanEditRules,
                                     Help = "Shared: one purse and one bag of potions/bombs. Own: everyone keeps theirs (items and upgrades stay shared)." });
                    break;
                case 2:
                    if (Active && N.LocalId != 255)
                    {
                        var ids = new List<byte>(N.Players.Keys); ids.Sort();
                        foreach (var id in ids)
                        {
                            if (id == N.LocalId) continue;
                            byte pid = id;
                            l.Add(new Item
                            {
                                Label = "Go to " + N.Players[pid],
                                Value = () => { var p = FindPuppet(pid); var s = p?.Latest?.Scene; return (ReviveSystem.IsDowned(pid) ? "<color=#FF6B66>down</color>  " : "") + GameInfo.FriendlyArea(s ?? "?"); },
                                Enter = () => { OpenMenu(false); Regroup.GoTo(N, pid); },
                                Help = "Travel to them, even in another area (F5 does the same).",
                            });
                        }
                    }
                    l.Add(new Item { Label = "Ping my spot (F3)", Enter = () => { OpenMenu(false); Signals.PingHere(N); }, Enabled = () => Active });
                    l.Add(new Item { Label = "Quick chat (F4)", Enter = () => { OpenMenu(false); Signals.ChatOpen = true; }, Enabled = () => Active });
                    break;
                case 3:
                    l.Add(new Item { Label = "Streamer mode", Value = () => Toggle(Plugin.StreamerMode.Value), Change = d => Plugin.StreamerMode.Value = !Plugin.StreamerMode.Value,
                                     Help = "Hides every IP address and join code on screen, so they never show on stream." });
                    l.Add(new Item { Label = "Shared camera", Value = () => Toggle(Plugin.SharedCamera.Value), Change = d => Plugin.SharedCamera.Value = !Plugin.SharedCamera.Value,
                                     Help = "Frames teammates in the same area and zooms out as you spread apart. Only affects your screen." });
                    l.Add(new Item { Label = "Automatic port forwarding", Value = () => Toggle(Plugin.AutoPortForward.Value), Change = d => Plugin.AutoPortForward.Value = !Plugin.AutoPortForward.Value,
                                     Help = "When hosting, asks your router (UPnP) to let friends in over the internet." });
                    l.Add(new Item { Label = "Steam 'Join Game'", Value = () => Toggle(Plugin.SteamInvites.Value), Change = d => Plugin.SteamInvites.Value = !Plugin.SteamInvites.Value,
                                     Help = "Steam copies: friends can join you from their Steam friends list." });
                    l.Add(new Item { Label = "Check for updates", Value = () => Toggle(Plugin.CheckForUpdates.Value), Change = d => Plugin.CheckForUpdates.Value = !Plugin.CheckForUpdates.Value });
                    break;
            }
            return l;
        }

        public static void OpenMenu(bool open)
        {
            if (!Ensure() || open == _menuOpen) return;
            _menuOpen = open;
            _menu.gameObject.SetActive(open);
            if (open)
            {
                _editName = Plugin.PlayerName.Value; _editAddr = Plugin.JoinAddress.Value;
                _editPort = Plugin.Port.Value.ToString(CultureInfo.InvariantCulture); _editMax = Plugin.MaxPlayers.Value;
                _tab = 0;
                SetTab(0);
                // First time with the default name: start on the name field so players pick what others see.
                _sel = !Plugin.NameChosen.Value ? 0 : IndexOf(Active ? "Leave session" : "Host a session");
                _prevLock = PlayerCharacter._cutsceneLocked;
                RefreshMenu();
            }
            else
            {
                PlayerCharacter._cutsceneLocked = _prevLock;
                SaveEdits();
            }
        }

        static bool _prevLock;
        static int IndexOf(string label) { for (int i = 0; i < _items.Count; i++) if (_items[i].Label == label) return i; return 0; }

        static void SetTab(int t)
        {
            SaveEdits();
            _tab = (t % Tabs.Length + Tabs.Length) % Tabs.Length;
            _items = BuildItems(_tab);
            _sel = 0;
            while (_sel < _items.Count - 1 && !Enabled(_items[_sel])) _sel++;
        }

        static bool Enabled(Item it) => it.Enabled == null || it.Enabled();

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
            if (_editMax >= 2) Plugin.MaxPlayers.Value = Mathf.Clamp(_editMax, 2, Plugin.HardPlayerCap);
        }

        static void RefreshMenu()
        {
            for (int i = 0; i < Tabs.Length; i++)
            {
                _tabLabels[i].text = i == _tab ? $"<u>{Tabs[i]}</u>" : Tabs[i];
                _tabLabels[i].color = i == _tab ? PanelEdge : Dim;
            }
            for (int i = 0; i < MaxRows; i++)
            {
                if (i >= _items.Count) { _menuRows[i].text = ""; _menuRowBg[i].color = new Color(1, 1, 1, 0); continue; }
                var it = _items[i];
                string v = it.Value?.Invoke();
                bool editing = i == _sel && it.Field >= 0;
                string text = v != null ? $"{it.Label}<pos=46%>{v}{(editing && (Time.unscaledTime % 1f) < 0.55f ? "_" : "")}" : it.Label;
                bool en = Enabled(it);
                _menuRows[i].text = text;
                _menuRows[i].alignment = v != null ? TextAlignmentOptions.Left : TextAlignmentOptions.Center;
                _menuRows[i].color = !en ? new Color(1, 1, 1, 0.3f) : i == _sel ? PanelEdge : Color.white;
                _menuRowBg[i].color = i == _sel ? new Color(1f, 0.78f, 0.35f, 0.14f) : new Color(1, 1, 1, v != null ? 0.04f : 0f);
            }

            string help = _sel < _items.Count ? _items[_sel].Help : null;
            string head = "";
            if (_tab == 0)
            {
                if (!Plugin.NameChosen.Value && _sel == 0) head = "<color=#FFC75A>Choose the name other players will see — type it, then press Enter.</color>\n";
                else if (Active && N.IsHost && !Plugin.StreamerMode.Value)
                {
                    var codes = HostCodes(N);
                    head = $"Your join code: <color=#FFC75A>{codes.internet ?? codes.lan ?? "?"}</color>" +
                           (codes.lan != null && codes.internet != null && codes.lan != codes.internet ? $"   ·   same network: {codes.lan}" : "") + "\n";
                }
                else if (Plugin.StreamerMode.Value) head = "Addresses and codes are hidden (streamer mode). Share them with friends privately.\n";
                else
                {
                    var addrs = GameInfo.LocalAddresses();
                    head = $"Your address{(addrs.Count > 1 ? "es" : "")}: {(addrs.Count > 0 ? string.Join(", ", addrs) : "unknown")}\n";
                }
            }
            else if (_tab == 1) head = CanEditRules ? (Active ? "You're the host: changes apply to everyone right away.\n" : "These rules apply when you host.\n")
                                                    : "The host decides the rules:  " + HostSettings.Describe() + "\n";
            else if (_tab == 2 && (!Active || N.Players.Count < 2)) head = "Nobody else is in the session yet.\n";
            string update = UpdateCheck.Latest != null ? $"<color=#7FCBFF>Version {UpdateCheck.Latest} is available: {UpdateCheck.RepoUrl}</color>\n" : "";
            _menuInfo.text = update + head + (help != null ? help + "\n" : "") +
                             $"<size=85%>Game {GameInfo.Version} ({GameInfo.Store})  ·  Mod {Plugin.Version}  ·  Tab / PgUp / PgDn switch tabs  ·  Up/Down  ·  Left/Right  ·  Enter  ·  F1 close</size>";
        }

        /// <summary>Keyboard driving of the menu. Also called by the test autopilot (ui key/type).</summary>
        public static void Key(KeyCode k, Net net)
        {
            if (k == KeyCode.F1) { OpenMenu(!_menuOpen); return; }
            if (!_menuOpen) return;
            int n = _items.Count;
            bool shift = false; try { shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift); } catch { }
            if (k == KeyCode.Tab || k == KeyCode.PageDown || k == KeyCode.PageUp)
                SetTab(_tab + (k == KeyCode.PageUp || (k == KeyCode.Tab && shift) ? -1 : 1));
            else if (n == 0) { }
            else if (k == KeyCode.UpArrow) { int g = 0; do { _sel = (_sel + n - 1) % n; } while (!Enabled(_items[_sel]) && ++g < n); }
            else if (k == KeyCode.DownArrow) { int g = 0; do { _sel = (_sel + 1) % n; } while (!Enabled(_items[_sel]) && ++g < n); }
            else if ((k == KeyCode.LeftArrow || k == KeyCode.RightArrow) && _items[_sel].Change != null && Enabled(_items[_sel]))
                _items[_sel].Change(k == KeyCode.RightArrow ? 1 : -1);
            else if (k == KeyCode.Backspace && _items[_sel].Field >= 0)
            {
                int f = _items[_sel].Field;
                if (f == 0 && _editName.Length > 0) _editName = _editName.Substring(0, _editName.Length - 1);
                if (f == 1 && _editAddr.Length > 0) _editAddr = _editAddr.Substring(0, _editAddr.Length - 1);
                if (f == 2 && _editPort.Length > 0) _editPort = _editPort.Substring(0, _editPort.Length - 1);
            }
            else if (k == KeyCode.Return || k == KeyCode.KeypadEnter)
            {
                var it = _items[_sel];
                if (!Enabled(it)) { }
                else if (it.Enter != null) { SaveEdits(); it.Enter(); if (!_menuOpen) return; }
                else if (it.Change != null && it.Field < 0) it.Change(1);
                else { int g = 0; do { _sel = (_sel + 1) % n; } while (!Enabled(_items[_sel]) && ++g < n); SaveEdits(); }
            }
            RefreshMenu();
        }

        public static void Type(string s, Net net)
        {
            if (!_menuOpen || _sel >= _items.Count || _items[_sel].Field < 0 || string.IsNullOrEmpty(s)) return;
            int f = _items[_sel].Field;
            foreach (char ch in s)
            {
                if (ch == '\b' || ch == '\n' || ch == '\r' || ch == '\t') continue;
                if (f == 0 && _editName.Length < 20 && (char.IsLetterOrDigit(ch) || " _-.".IndexOf(ch) >= 0)) _editName += ch;
                if (f == 1 && _editAddr.Length < 64 && (char.IsLetterOrDigit(ch) || ".:-".IndexOf(ch) >= 0)) _editAddr += ch;
                if (f == 2 && _editPort.Length < 5 && char.IsDigit(ch)) _editPort += ch;
            }
            RefreshMenu();
        }

        static readonly KeyCode[] MenuKeys = { KeyCode.F1, KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.LeftArrow, KeyCode.RightArrow, KeyCode.Backspace, KeyCode.Return, KeyCode.KeypadEnter, KeyCode.Tab, KeyCode.PageUp, KeyCode.PageDown };

        static void HandleMenuKeys(Net net)
        {
            foreach (var k in MenuKeys) { bool down; try { down = Input.GetKeyDown(k); } catch { return; } if (down) Key(k, net); }
            if (_menuOpen)
            {
                PlayerCharacter._cutsceneLocked = true;   // typing must not move the fox
                string typed = null; try { typed = Input.inputString; } catch { }
                if (!string.IsNullOrEmpty(typed)) Type(typed, net);
                RefreshMenu();                             // caret blink, live values
            }
        }

        public static string Describe() =>
            $"built={_built} menu={_menuOpen} tab={(_tab < Tabs.Length ? Tabs[_tab] : "?")} sel={_sel}:{(_sel < _items.Count ? _items[_sel].Label : "-")} rows={_rows.FindAll(r => r.Rt.gameObject.activeSelf).Count} toasts={_toasts.Count} plates={_plates.Count} markers={_markers.FindAll(m => m.Rt.gameObject.activeSelf).Count} name={_editName} addr={_editAddr}";
    }
}
