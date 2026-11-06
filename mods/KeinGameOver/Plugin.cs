// KeinGameOver für R.E.P.O. – TEMPORÄR, NUR ZUM TESTEN
// Nur im Singleplayer:
//  1) Wenn du stirbst, endet die Runde nicht. RunManager.Update prüft jeden Frame "sind alle isDisabled?" und
//     wechselt dann das Level als gescheitert (allPlayersDead -> Save wird gelöscht). Das Spiel hat dafür einen
//     eigenen Aus-Schalter (allPlayersDeadCheckDisabled), den die Mod im Level einfach an lässt.
//     Du bleibst tot im Zuschauer-Modus und kannst KopfTeleport/KopfZieher/KopfKamera testen.
//  2) Sicherheitsnetz: DataDirector.SaveDeleteCheck (automatisches Löschen bei "alle tot" oder beim Verlassen im
//     ersten Level) wird übersprungen. Löschen über das Speicherstand-Menü geht weiter normal.
// Im Multiplayer macht die Mod gar nichts.
// F11 schaltet sie an/aus. Wieder raus: Menü -> Spiel verlassen (Save bleibt).
using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace KeinGameOver
{
    [BepInPlugin(Guid, "KeinGameOver", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "strehl.keingameover";
        public const string Version = "0.1.0";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<Key> ToggleKey;
        internal static ManualLogSource Log;

        void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("Allgemein", "Aktiv", true, "Singleplayer: kein Game Over beim Sterben, Save wird nicht automatisch gelöscht.");
            ToggleKey = Config.Bind("Allgemein", "Taste", Key.F11, "Schaltet die Mod im Spiel an/aus.");

            var go = new GameObject("KeinGameOver");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Keys>();
            new Harmony(Guid).PatchAll(typeof(Patches));
            Log.LogInfo($"KeinGameOver {Version} geladen (Singleplayer, {(Enabled.Value ? "AN" : "AUS")}, Taste {ToggleKey.Value})");
        }

        internal static bool ActiveHere => Enabled.Value && !SemiFunc.IsMultiplayer();
    }

    internal class Keys : MonoBehaviour
    {
        void Update()
        {
            var kb = Keyboard.current;
            if (kb == null || !kb[Plugin.ToggleKey.Value].wasPressedThisFrame) return;
            Plugin.Enabled.Value = !Plugin.Enabled.Value;
            string t = Plugin.Enabled.Value ? "KeinGameOver: AN (Singleplayer)" : "KeinGameOver: AUS – Sterben beendet die Runde wieder";
            Plugin.Log.LogInfo(t);
            try { SemiFunc.UIFocusText(t, Color.white, Plugin.Enabled.Value ? Color.green : Color.red, 3f); } catch { }
        }
    }

    internal static class Patches
    {
        static readonly AccessTools.FieldRef<RunManager, bool> CheckDisabled = AccessTools.FieldRefAccess<RunManager, bool>("allPlayersDeadCheckDisabled");
        static readonly AccessTools.FieldRef<PlayerAvatar, bool> IsDisabled = AccessTools.FieldRefAccess<PlayerAvatar, bool>("isDisabled");
        static readonly AccessTools.FieldRef<RunManager, bool> AllDead = AccessTools.FieldRefAccess<RunManager, bool>("allPlayersDead");
        static bool turnedOff;   // hat die Mod den Schalter selbst gesetzt?
        static bool saidOnce;

        [HarmonyPatch(typeof(RunManager), "Update")]
        [HarmonyPrefix]
        static void RunManagerUpdate(RunManager __instance)
        {
            try
            {
                if (Plugin.ActiveHere && SemiFunc.RunIsLevel())
                {
                    if (!CheckDisabled(__instance)) { CheckDisabled(__instance) = true; turnedOff = true; }
                    AllDead(__instance) = false;

                    var me = SemiFunc.PlayerAvatarLocal();
                    if (me && IsDisabled(me) && !saidOnce)
                    {
                        saidOnce = true;
                        try { SemiFunc.UIFocusText("KeinGameOver: Runde läuft weiter – zum Beenden Menü > Verlassen", Color.white, Color.green, 4f); } catch { }
                    }
                    if (me && !IsDisabled(me)) saidOnce = false;
                }
                else if (turnedOff)
                {
                    // Mod ausgeschaltet -> Spiel-Prüfung wieder zurückgeben
                    CheckDisabled(__instance) = false;
                    turnedOff = false;
                }
            }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }

        // Ein echter Levelwechsel (Truck fährt los) setzt den Schalter selbst; danach nicht mehr "unserer"
        [HarmonyPatch(typeof(RunManager), nameof(RunManager.ChangeLevel))]
        [HarmonyPrefix]
        static void ChangeLevel() { turnedOff = false; }

        [HarmonyPatch(typeof(DataDirector), nameof(DataDirector.SaveDeleteCheck))]
        [HarmonyPrefix]
        static bool SaveDeleteCheck()
        {
            if (!Plugin.ActiveHere || SemiFunc.RunIsArena()) return true;
            Plugin.Log.LogInfo("KeinGameOver: automatisches Save-Löschen übersprungen.");
            return false;
        }
    }
}
