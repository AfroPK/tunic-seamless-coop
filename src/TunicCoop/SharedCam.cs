using System;
using UnityEngine;

namespace TunicCoop
{
    /// <summary>
    /// Optional shared camera (Options tab / Player.SharedCamera): centres the view on the group of players in
    /// this area and zooms out as they spread, so nobody walks off screen. Applied on top of TUNIC's own camera
    /// after it updates (the offset is removed again before its next update, see ReviveSystem.CamStep).
    /// </summary>
    public static class SharedCam
    {
        const float GroupRadius = 30f;       // teammates farther than this aren't framed (they're "elsewhere")
        const float MaxShift = 12f;
        static Vector3 _shift;
        static float _zoom = 1f;
        public static float Zoom => _zoom;
        public static Vector3 Shift => _shift;

        /// <summary>Called after the game positioned its camera; returns the extra offset to apply this frame.</summary>
        public static Vector3 Step(CameraController cc)
        {
            var pc = PlayerCharacter.instance;
            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.1f);
            Vector3 want = Vector3.zero; float wantZoom = 1f;
            if (Plugin.SharedCamera.Value && pc != null && !CoopUI.MenuOpen)
            {
                var me = pc.transform.position;
                Vector3 sum = me; int n = 1;
                string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                foreach (var p in PuppetRegistry.All())
                {
                    if (p.PlayerId == 250 || !p.gameObject.activeSelf || p.Latest == null || p.Latest.Scene != scene) continue;
                    var q = p.transform.position;
                    if ((q - me).sqrMagnitude > GroupRadius * GroupRadius) continue;
                    sum += q; n++;
                }
                if (n > 1)
                {
                    var centre = sum / n;
                    want = Vector3.ClampMagnitude(centre - me, MaxShift);
                    float spread = 0f;
                    foreach (var p in PuppetRegistry.All())
                        if (p.PlayerId != 250 && p.gameObject.activeSelf && p.Latest != null && p.Latest.Scene == scene)
                            spread = Mathf.Max(spread, (p.transform.position - centre).magnitude);
                    spread = Mathf.Max(spread, (me - centre).magnitude);
                    wantZoom = Mathf.Clamp(1f + (spread - 6f) / 18f, 1f, 1.7f);
                }
            }
            // smooth both, so the frame glides instead of snapping
            float k = 1f - Mathf.Exp(-4f * dt);
            _shift = Vector3.Lerp(_shift, want, k);
            _zoom = Mathf.Lerp(_zoom, wantZoom, k);
            if (_shift.sqrMagnitude < 0.0004f && want == Vector3.zero) _shift = Vector3.zero;
            if (Mathf.Abs(_zoom - 1f) > 0.002f) ApplyZoom(cc, _zoom); else _zoom = wantZoom == 1f ? 1f : _zoom;
            return _shift;
        }

        static float _applied = 1f, _base, _set;

        /// <summary>Before the game updates its camera: take our zoom back out (the game doesn't rewrite the size
        /// on every update, so scaling it again would compound).</summary>
        public static void Undo(CameraController cc)
        {
            if (Mathf.Approximately(_applied, 1f)) return;
            try
            {
                var cam = cc.cachedCamera;
                // Restore only if the size is still what we set; if the game rewrote it in between, that value is the base.
                if (cam != null) { if (cam.orthographic) { if (Mathf.Approximately(cam.orthographicSize, _set)) cam.orthographicSize = _base; } else if (Mathf.Approximately(cam.fieldOfView, _set)) cam.fieldOfView = _base; }
            }
            catch { }
            _applied = 1f;
        }

        static void ApplyZoom(CameraController cc, float z)
        {
            try
            {
                var cam = cc.cachedCamera;
                if (cam == null) return;
                if (cam.orthographic) { _base = cam.orthographicSize; cam.orthographicSize = _set = _base * z; }
                else { _base = cam.fieldOfView; cam.fieldOfView = _set = Mathf.Min(120f, _base * z); }
                _applied = z;
            }
            catch (Exception e) { Plugin.L.LogInfo("[cam] zoom: " + e.Message); }
        }

        public static string Describe()
        {
            string size = "?";
            try { var c = CameraController.instance?.cachedCamera; if (c != null) size = c.orthographic ? $"ortho {c.orthographicSize:F2}" : $"fov {c.fieldOfView:F1}"; } catch { }
            return $"shared={Plugin.SharedCamera.Value} shift={_shift} zoom={_zoom:F2} camera={size}";
        }
    }
}
