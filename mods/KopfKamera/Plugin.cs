// KopfKamera für R.E.P.O. – DEBUG
// Wenn du tot bist: 3rd-Person-Kamera hinter deinem eigenen Kopf.
//  - geht automatisch an, solange KopfZieher deinen Kopf zieht (Einstellung "Auto mit KopfZieher")
//  - F10 schaltet sie jederzeit von Hand an/aus
//  - Maus = um den Kopf drehen, Mausrad = Abstand
//  - Oben links: Debug-Text (Kopfposition, Tempo, Abstand zum Ziel, ob KopfZieher läuft)
//
// Technik: SpectateCamera setzt in LateUpdate die Position seiner Kamera-Halterung (transform).
// Ein Harmony-Postfix danach überschreibt sie. Nur die eigene Kamera, nichts geht ans Netzwerk.
using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace KopfKamera
{
    [BepInPlugin(Guid, "KopfKamera", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "strehl.kopfkamera";
        public const string Version = "0.1.0";

        internal static ConfigEntry<Key> ToggleKey;
        internal static ConfigEntry<bool> AutoWithPuller;
        internal static ConfigEntry<float> Distance;
        internal static ConfigEntry<bool> ShowDebug;
        internal static ManualLogSource Log;

        void Awake()
        {
            Log = Logger;
            ToggleKey = Config.Bind("Allgemein", "Taste", Key.F10, "3rd-Person-Kamera auf den eigenen Kopf an/aus (nur wenn tot).");
            AutoWithPuller = Config.Bind("Allgemein", "Auto mit KopfZieher", true, "Automatisch an, solange KopfZieher den Kopf zieht.");
            Distance = Config.Bind("Allgemein", "Abstand", 3f, "Start-Abstand der Kamera zum Kopf (m). Mausrad ändert ihn im Spiel.");
            ShowDebug = Config.Bind("Debug", "Text anzeigen", true, "Debug-Text oben links anzeigen.");

            var go = new GameObject("KopfKamera");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Cam>();
            new Harmony(Guid).PatchAll(typeof(SpectatePatch));
            Log.LogInfo($"KopfKamera {Version} geladen, Taste {ToggleKey.Value}");
        }
    }

    [HarmonyPatch(typeof(SpectateCamera), "LateUpdate")]
    internal static class SpectatePatch
    {
        static void Postfix(SpectateCamera __instance)
        {
            try { Cam.Instance?.Apply(__instance); }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }
    }

    internal class Cam : MonoBehaviour
    {
        internal static Cam Instance;

        static readonly AccessTools.FieldRef<PlayerAvatar, bool> DeadSet = AccessTools.FieldRefAccess<PlayerAvatar, bool>("deadSet");
        static readonly AccessTools.FieldRef<PlayerAvatar, PlayerDeathHead> DeathHead = AccessTools.FieldRefAccess<PlayerAvatar, PlayerDeathHead>("playerDeathHead");
        static readonly AccessTools.FieldRef<PlayerDeathHead, bool> Triggered = AccessTools.FieldRefAccess<PlayerDeathHead, bool>("triggered");
        static readonly AccessTools.FieldRef<PlayerDeathHead, PhysGrabObject> HeadPgo = AccessTools.FieldRefAccess<PlayerDeathHead, PhysGrabObject>("physGrabObject");
        static readonly AccessTools.FieldRef<SpectateCamera, Camera> MainCam = AccessTools.FieldRefAccess<SpectateCamera, Camera>("MainCamera");

        // KopfZieher ist optional: nur per Reflection angefasst
        static PropertyInfo pullingProp, targetProp;
        static bool pullerLooked;

        bool manual;
        float yaw, pitch = 20f, dist;
        bool wasActive;
        Vector3 lastHeadPos; float headSpeed;
        string status = "";

        void Awake() { Instance = this; dist = Plugin.Distance.Value; }

        static void FindPuller()
        {
            if (pullerLooked) return;
            pullerLooked = true;
            var t = AccessTools.TypeByName("KopfZieher.Runner");
            if (t == null) { Plugin.Log.LogInfo("KopfZieher nicht gefunden – nur Handbetrieb (F10)."); return; }
            pullingProp = t.GetProperty("Pulling", BindingFlags.Public | BindingFlags.Static);
            targetProp = t.GetProperty("PullTarget", BindingFlags.Public | BindingFlags.Static);
            Plugin.Log.LogInfo("KopfZieher gefunden – Kamera geht beim Ziehen automatisch an.");
        }

        static bool PullerActive(out Vector3 target)
        {
            target = default;
            FindPuller();
            if (pullingProp == null) return false;
            bool on = (bool)pullingProp.GetValue(null);
            if (on && targetProp != null) target = (Vector3)targetProp.GetValue(null);
            return on;
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb[Plugin.ToggleKey.Value].wasPressedThisFrame)
            {
                manual = !manual;
                Plugin.Log.LogInfo($"KopfKamera {(manual ? "an" : "aus")}");
                try { SemiFunc.UIFocusText(manual ? "KopfKamera: AN" : "KopfKamera: AUS", Color.white, manual ? Color.green : Color.red, 2f); } catch { }
            }
        }

        static PhysGrabObject MyHead()
        {
            var me = SemiFunc.PlayerAvatarLocal();
            if (!me || !DeadSet(me)) return null;
            var head = DeathHead(me);
            if (!head || !Triggered(head)) return null;
            return HeadPgo(head);
        }

        internal void Apply(SpectateCamera sc)
        {
            var pgo = MyHead();
            bool pulling = PullerActive(out var target);
            bool active = pgo && (manual || (pulling && Plugin.AutoWithPuller.Value));

            if (pgo)
            {
                var p = pgo.centerPoint;
                if (Time.deltaTime > 0f) headSpeed = Mathf.Lerp(headSpeed, Vector3.Distance(p, lastHeadPos) / Time.deltaTime, 0.1f);
                lastHeadPos = p;
                status = $"KopfKamera {(active ? "AN" : "aus")} ({(manual ? "Hand" : "auto")})\n" +
                         $"Kopf: {p.x:F1} {p.y:F1} {p.z:F1}   Tempo: {headSpeed:F1} m/s\n" +
                         (pulling ? $"KopfZieher: zieht   Ziel noch {Vector3.Distance(p, target):F1} m" : "KopfZieher: wartet");
            }
            else status = "";

            if (!active)
            {
                if (wasActive) wasActive = false;
                return;
            }

            // Beim Einschalten hinter den aktuellen Blick setzen
            if (!wasActive)
            {
                yaw = sc.transform.eulerAngles.y;
                wasActive = true;
            }

            yaw += SemiFunc.InputMouseX() * 2f;
            pitch = Mathf.Clamp(pitch - SemiFunc.InputMouseY() * 2f, -30f, 80f);
            dist = Mathf.Clamp(dist - SemiFunc.InputScrollY() * 0.5f, 1f, 10f);

            var center = pgo.centerPoint;
            var rot = Quaternion.Euler(pitch, yaw, 0f);
            var back = rot * Vector3.back;

            // Nicht durch Wände schauen
            float d = dist;
            if (Physics.SphereCast(center, 0.2f, back, out var hit, dist, SemiFunc.LayerMaskGetVisionObstruct(), QueryTriggerInteraction.Ignore))
                d = Mathf.Max(hit.distance - 0.1f, 0.3f);

            sc.transform.position = center + back * d;
            sc.transform.rotation = rot;
            // CameraAim hängt unter der Halterung – im Head-Modus dreht er sonst mit
            if (CameraAim.Instance) CameraAim.Instance.transform.localRotation = Quaternion.identity;
            var cam = MainCam(sc);
            if (cam)
            {
                cam.transform.localRotation = Quaternion.identity;
                cam.nearClipPlane = 0.01f;
            }
        }

        void OnGUI()
        {
            if (!Plugin.ShowDebug.Value || string.IsNullOrEmpty(status)) return;
            GUI.color = Color.black;
            GUI.Label(new Rect(11, 11, 600, 80), status);
            GUI.color = Color.yellow;
            GUI.Label(new Rect(10, 10, 600, 80), status);
        }
    }
}
