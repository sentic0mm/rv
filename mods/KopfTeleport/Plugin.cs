// KopfTeleport für R.E.P.O.
//
// 1) Teleport beim Sterben (geht OHNE Mod beim Host):
//    Den eigenen Tod meldet das eigene Spiel (PlayerHealth.Hurt -> PlayerAvatar.PlayerDeath -> PlayerDeathRPC).
//    Der Host setzt den Kopf 0,5 s danach an die Stelle, an der der Spieler bei ihm steht
//    (playerAvatarCollision.deathHeadPosition), und die Position des eigenen Körpers schickt man selbst
//    (25x pro Sekunde, Host übernimmt sie per rb.MovePosition). Die Mod hält deshalb den Tod kurz an,
//    versetzt den eigenen Körper zum Ziel und meldet den Tod erst dann -> der Kopf entsteht dort.
//
// 2) F7 (wenn tot): eigener Kopf -> aktiver Extraction Point; ist keiner an, aber noch welche offen -> ein Cart / Pocket Cart
// (F7 nochmal innerhalb von 5 s = nächstes Cart); sind alle Punkte fertig -> in den Truck.
//
// Netzwerk: Den Kopf darf in R.E.P.O. nur der Host bewegen (SetPositionRPC ist MasterOnly, Teleport() schickt
// als Mitspieler für Nicht-Spieler-Objekte nichts mehr). Deshalb:
//   Host / Singleplayer -> teleportiert direkt.
//   Mitspieler          -> schickt eine Anfrage (Photon-Event) an den Host. Hat der Host die Mod, rechnet ER das Ziel
//                          aus und teleportiert den Kopf des Absenders – und nur dessen Kopf. Kommt keine Antwort,
//                          hat der Host die Mod nicht, und der Spieler bekommt das angezeigt.
using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.InputSystem;

namespace KopfTeleport
{
    [BepInPlugin(Guid, "KopfTeleport", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "strehl.kopfteleport";
        public const string Version = "1.2.0";

        internal static ConfigEntry<Key> TeleportKey;
        internal static ConfigEntry<bool> OnDeath;
        internal static ConfigEntry<Key> OnDeathToggleKey;
        internal static ConfigEntry<bool> OnDeathCarts;
        internal static ManualLogSource Log;

        void Awake()
        {
            Log = Logger;
            TeleportKey = Config.Bind("Allgemein", "Taste", Key.F7,
                "Taste für den eigenen toten Kopf: aktiver Extraction Point, sonst Cart/Pocket Cart (nochmal drücken = nächstes), " +
                "sind alle Extraction Points fertig, der Truck. Braucht die Mod beim Host. Namen siehe UnityEngine.InputSystem.Key.");
            OnDeath = Config.Bind("Beim Sterben", "Aktiv", true,
                "Kurz vor dem Tod zum aktiven Extraction Point teleportieren (sind alle fertig: in den Truck), " +
                "damit der Kopf gleich dort landet. Braucht KEINE Mod beim Host.");
            OnDeathToggleKey = Config.Bind("Beim Sterben", "Umschalt-Taste", Key.F5,
                "Schaltet den Teleport beim Sterben im Spiel an/aus (wird in 'Aktiv' gespeichert).");
            OnDeathCarts = Config.Bind("Beim Sterben", "Auch Carts", true,
                "Ist kein Extraction Point aktiv, beim Sterben ins nächste Cart / Pocket Cart teleportieren.");

            // Eigenes Objekt: das BepInEx-Manager-Objekt überlebt in R.E.P.O. den Szenenwechsel nicht
            var go = new GameObject("KopfTeleport");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Runner>();
            new Harmony(Guid).PatchAll(typeof(DeathPatch));
            Log.LogInfo($"KopfTeleport {Version} geladen, Taste {TeleportKey.Value}");
        }
    }

    [HarmonyPatch(typeof(PlayerAvatar), nameof(PlayerAvatar.PlayerDeath))]
    internal static class DeathPatch
    {
        static bool Prefix(PlayerAvatar __instance, int enemyIndex)
        {
            try { return !Runner.TryDeathWarp(__instance, enemyIndex); }
            catch (Exception e) { Plugin.Log.LogError(e); return true; }
        }
    }

    internal class Runner : MonoBehaviour
    {
        internal static Runner Instance;
        static bool allowDeath;     // true, während die Mod den angehaltenen Tod selbst auslöst
        static bool deathPending;   // Tod angehalten, Teleport läuft

        static readonly AccessTools.FieldRef<PlayerAvatar, bool> IsLocal = AccessTools.FieldRefAccess<PlayerAvatar, bool>("isLocal");
        static readonly AccessTools.FieldRef<PlayerAvatar, PlayerTumble> Tumble = AccessTools.FieldRefAccess<PlayerAvatar, PlayerTumble>("tumble");
        static readonly AccessTools.FieldRef<PlayerTumble, bool> IsTumbling = AccessTools.FieldRefAccess<PlayerTumble, bool>("isTumbling");
        static readonly AccessTools.FieldRef<PlayerTumble, PhysGrabObject> TumblePgo = AccessTools.FieldRefAccess<PlayerTumble, PhysGrabObject>("physGrabObject");

        void Awake() { Instance = this; }

        // true = Tod wird angehalten und gleich nach dem Teleport nachgeholt
        internal static bool TryDeathWarp(PlayerAvatar p, int enemyIndex)
        {
            if (allowDeath || !Plugin.OnDeath.Value || !Instance) return false;
            if (!p || !IsLocal(p) || DeadSet(p)) return false;
            if (deathPending) return true; // weiterer Schaden während des Teleports: Tod kommt ja gleich
            if (!SemiFunc.RunIsLevel() || SemiFunc.RunIsArena()) return false;

            var pc = PlayerController.instance;
            if (!pc) return false;
            var target = BodyTarget(pc.transform.position);
            if (target == null) return false;

            deathPending = true;
            Instance.StartCoroutine(Instance.DeathWarp(p, enemyIndex, target.Value));
            return true;
        }

        // Ziel für den lebenden Körper: Kopf entsteht ca. 1,5 m über den Füßen und fällt dann hinein
        static Target? BodyTarget(Vector3 from)
        {
            var point = ActivePoint();
            if (point)
                return new Target { Name = "Extraction Point", Pos = point.safetySpawn.position, Rot = point.safetySpawn.rotation };
            var rd = RoundDirector.instance;
            if (rd && AllCompleted(rd))
            {
                var truck = TruckSafetySpawnPoint.instance;
                if (truck) return new Target { Name = "Truck", Pos = truck.transform.position, Rot = truck.transform.rotation };
                return null;
            }
            if (!Plugin.OnDeathCarts.Value) return null;
            var t = Targets(from);
            if (t.Count == 0) return null;
            var c = t[0];
            c.Pos += Vector3.up * 0.3f;
            return c;
        }

        System.Collections.IEnumerator DeathWarp(PlayerAvatar p, int enemyIndex, Target t)
        {
            float wait = 0.3f; // >= 7 Positions-Updates bei 25/s
            try
            {
                if (p.playerHealth) p.playerHealth.InvincibleSet(1.5f);
                var tumble = Tumble(p);
                if (tumble && IsTumbling(tumble) && TumblePgo(tumble))
                {
                    // Als Mitspieler -> SetPositionRPC für den eigenen Tumble, das nimmt der Host an
                    TumblePgo(tumble).Teleport(t.Pos, t.Rot);
                    wait = 0.45f; // Host muss erst umsetzen und zurückschicken
                }
                else
                {
                    var pc = PlayerController.instance;
                    pc.transform.position = t.Pos;
                    pc.transform.rotation = t.Rot;
                    if (pc.rb)
                    {
                        pc.rb.position = t.Pos;
                        pc.rb.linearVelocity = Vector3.zero;
                    }
                }
                Plugin.Log.LogInfo($"Beim Sterben -> {t.Name} {t.Pos}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(e);
            }

            yield return new WaitForSeconds(wait);

            deathPending = false;
            if (!p || DeadSet(p)) yield break;
            allowDeath = true;
            try { p.PlayerDeath(enemyIndex); }
            catch (Exception e) { Plugin.Log.LogError(e); }
            finally { allowDeath = false; }
            Say($"Gestorben -> Kopf landet im {t.Name}", true);
        }

        // Eigene Event-Codes (das Spiel selbst nutzt 123 und 124)
        const byte EvRequest = 187;
        const byte EvReply = 188;
        const string Magic = "strehl.kopfteleport";
        const int Proto = 1;

        const float CycleWindow = 5f;   // so lange nach einem Teleport schaltet die Taste zum nächsten Cart
        const float ReplyTimeout = 2f;  // so lange wartet ein Mitspieler auf die Antwort des Hosts

        static readonly AccessTools.FieldRef<PlayerAvatar, bool> DeadSet = AccessTools.FieldRefAccess<PlayerAvatar, bool>("deadSet");
        static readonly AccessTools.FieldRef<PlayerAvatar, bool> IsDisabled = AccessTools.FieldRefAccess<PlayerAvatar, bool>("isDisabled");
        static readonly AccessTools.FieldRef<PlayerAvatar, PlayerDeathHead> DeathHead = AccessTools.FieldRefAccess<PlayerAvatar, PlayerDeathHead>("playerDeathHead");
        static readonly AccessTools.FieldRef<PlayerDeathHead, bool> Triggered = AccessTools.FieldRefAccess<PlayerDeathHead, bool>("triggered");
        static readonly AccessTools.FieldRef<PlayerDeathHead, PhysGrabObject> HeadPgo = AccessTools.FieldRefAccess<PlayerDeathHead, PhysGrabObject>("physGrabObject");
        static readonly AccessTools.FieldRef<RoundDirector, bool> ExtractionActive = AccessTools.FieldRefAccess<RoundDirector, bool>("extractionPointActive");
        static readonly AccessTools.FieldRef<RoundDirector, bool> AllCompleted = AccessTools.FieldRefAccess<RoundDirector, bool>("allExtractionPointsCompleted");
        static readonly AccessTools.FieldRef<RoundDirector, ExtractionPoint> ExtractionCurrent = AccessTools.FieldRefAccess<RoundDirector, ExtractionPoint>("extractionPointCurrent");
        static readonly AccessTools.FieldRef<ExtractionPoint, ExtractionPoint.State> PointState = AccessTools.FieldRefAccess<ExtractionPoint, ExtractionPoint.State>("currentState");
        static readonly AccessTools.FieldRef<PhysGrabCart, Transform> InCart = AccessTools.FieldRefAccess<PhysGrabCart, Transform>("inCart");
        static readonly AccessTools.FieldRef<ItemEquippable, bool> IsEquipped = AccessTools.FieldRefAccess<ItemEquippable, bool>("isEquipped");

        // Durchschalten der Carts merkt sich der Host pro Spieler (ActorNumber; 0 = Singleplayer)
        class CycleState { public float Until; public int Index; public Vector3 Origin; }
        readonly Dictionary<int, CycleState> cycles = new Dictionary<int, CycleState>();

        LoadBalancingClient hooked;
        float cooldownUntil;
        float waitingReplyUntil;

        void Update()
        {
            try
            {
                HookEvents();
                var kb = Keyboard.current;
                if (kb != null && kb[Plugin.TeleportKey.Value].wasPressedThisFrame)
                    OnKey();
                if (kb != null && kb[Plugin.OnDeathToggleKey.Value].wasPressedThisFrame)
                {
                    Plugin.OnDeath.Value = !Plugin.OnDeath.Value; // BepInEx speichert die cfg automatisch
                    Say(Plugin.OnDeath.Value ? "Teleport beim Sterben: AN" : "Teleport beim Sterben: AUS", Plugin.OnDeath.Value);
                }
                if (waitingReplyUntil > 0f && Time.time > waitingReplyUntil)
                {
                    waitingReplyUntil = 0f;
                    Say("Keine Antwort vom Host – er braucht KopfTeleport auch (als Mitspieler geht es sonst nicht).", false);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(e);
            }
        }

        void OnDestroy()
        {
            if (hooked != null) hooked.EventReceived -= OnEvent;
        }

        // PhotonNetwork.NetworkingClient kann neu erzeugt werden – immer am aktuellen hängen
        void HookEvents()
        {
            var client = PhotonNetwork.NetworkingClient;
            if (client == hooked) return;
            if (hooked != null) hooked.EventReceived -= OnEvent;
            hooked = client;
            if (client != null) client.EventReceived += OnEvent;
        }

        void OnKey()
        {
            if (Time.time < cooldownUntil) return;
            cooldownUntil = Time.time + 0.4f;

            // Vorab lokal prüfen, damit man sofort eine Rückmeldung bekommt
            var me = SemiFunc.PlayerAvatarLocal();
            var problem = HeadProblem(me, out _);
            if (problem != null)
            {
                Say(problem, false);
                return;
            }

            if (SemiFunc.IsMasterClientOrSingleplayer())
            {
                int actor = SemiFunc.IsMultiplayer() ? PhotonNetwork.LocalPlayer.ActorNumber : 0;
                var (text, ok) = HandleRequest(me, actor);
                Say(text, ok);
                return;
            }

            // Mitspieler: Anfrage an den Host
            PhotonNetwork.RaiseEvent(EvRequest, new object[] { Magic, Proto },
                new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }, SendOptions.SendReliable);
            waitingReplyUntil = Time.time + ReplyTimeout;
        }

        void OnEvent(EventData ev)
        {
            try
            {
                if (ev.Code != EvRequest && ev.Code != EvReply) return;
                if (!(ev.CustomData is object[] data) || data.Length < 2 || !(data[0] is string m) || m != Magic) return;

                if (ev.Code == EvRequest)
                {
                    if (!PhotonNetwork.IsMasterClient) return;
                    var sender = PhotonNetwork.CurrentRoom?.GetPlayer(ev.Sender);
                    if (sender == null) return;
                    var avatar = SemiFunc.PlayerAvatarGetFromPhotonPlayer(sender);
                    var (text, ok) = HandleRequest(avatar, ev.Sender);
                    Plugin.Log.LogInfo($"Anfrage von {sender.NickName}: {text}");
                    PhotonNetwork.RaiseEvent(EvReply, new object[] { Magic, Proto, text, ok },
                        new RaiseEventOptions { TargetActors = new[] { ev.Sender } }, SendOptions.SendReliable);
                }
                else
                {
                    // Antwort nur vom Host annehmen
                    if (PhotonNetwork.MasterClient == null || ev.Sender != PhotonNetwork.MasterClient.ActorNumber) return;
                    if (data.Length < 4 || !(data[2] is string text) || !(data[3] is bool ok)) return;
                    waitingReplyUntil = 0f;
                    Say(text, ok);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(e);
            }
        }

        // ---- Ab hier läuft nur beim Host / im Singleplayer ----

        (string, bool) HandleRequest(PlayerAvatar avatar, int actor)
        {
            var problem = HeadProblem(avatar, out var head);
            if (problem != null) return (problem, false);
            var pgo = HeadPgo(head);
            if (!pgo) return ("Kopf hat kein PhysGrabObject?", false);

            if (!cycles.TryGetValue(actor, out var cyc)) cycles[actor] = cyc = new CycleState();
            bool cycling = Time.time < cyc.Until;
            // Carts nach Entfernung zum Startpunkt sortieren, sonst ändert jeder Teleport die Reihenfolge
            if (!cycling) cyc.Origin = head.transform.position;

            var targets = Targets(cyc.Origin);
            if (targets.Count == 0)
                return ("Kein Ziel: kein Extraction Point aktiv und kein Cart auf der Map.", false);

            cyc.Index = cycling ? (cyc.Index + 1) % targets.Count : 0;
            var t = targets[cyc.Index];
            pgo.Teleport(t.Pos, t.Rot); // als Host -> SetPositionLogic -> an alle verteilt
            cyc.Until = Time.time + CycleWindow;

            string more = targets.Count > 1 ? $"  ({cyc.Index + 1}/{targets.Count}, nochmal {Plugin.TeleportKey.Value} = nächstes)" : "";
            return ($"Kopf -> {t.Name}{more}", true);
        }

        static string HeadProblem(PlayerAvatar p, out PlayerDeathHead head)
        {
            head = null;
            if (!p) return "Gerade kein Spieler da.";
            if (!DeadSet(p)) return "Du lebst doch noch.";
            head = DeathHead(p);
            // Erst nach der Sterbe-Animation liegt der Kopf wirklich in der Welt
            if (!IsDisabled(p) || !head || !Triggered(head)) return "Noch einen Moment, dein Kopf ist noch nicht da.";
            return null;
        }

        struct Target { public string Name; public Vector3 Pos; public Quaternion Rot; }

        static ExtractionPoint ActivePoint()
        {
            var rd = RoundDirector.instance;
            if (!rd || !ExtractionActive(rd)) return null;
            var point = ExtractionCurrent(rd);
            if (!point || !point.safetySpawn) return null;
            switch (PointState(point))
            {
                case ExtractionPoint.State.None:
                case ExtractionPoint.State.Idle:
                case ExtractionPoint.State.Complete:
                case ExtractionPoint.State.Surplus:
                case ExtractionPoint.State.TaxReturn:
                    return null;
            }
            return point;
        }

        // Genau eine Phase je nach Rundenstand
        static List<Target> Targets(Vector3 from)
        {
            var list = new List<Target>();

            var point = ActivePoint();
            if (point)
            {
                // safetySpawn = derselbe Punkt, den das Spiel für Köpfe außerhalb der Map nimmt
                list.Add(new Target { Name = "Extraction Point", Pos = point.safetySpawn.position, Rot = point.safetySpawn.rotation });
                return list;
            }

            var rd = RoundDirector.instance;
            if (rd && AllCompleted(rd))
            {
                var truck = TruckSafetySpawnPoint.instance;
                if (truck)
                    list.Add(new Target { Name = "Truck (wirst dort wiederbelebt)", Pos = truck.transform.position, Rot = truck.transform.rotation });
                return list;
            }

            foreach (var c in UnityEngine.Object.FindObjectsByType<PhysGrabCart>(FindObjectsSortMode.None)
                         .Where(UsableCart)
                         .OrderBy(c => Vector3.Distance(from, c.transform.position)))
            {
                list.Add(new Target
                {
                    Name = c.isSmallCart ? "Pocket Cart" : "Cart",
                    // Mitte der "In Cart"-Zone, leicht angehoben, damit der Kopf nicht im Boden steckt
                    Pos = InCart(c).position + Vector3.up * 0.2f,
                    Rot = c.transform.rotation,
                });
            }
            return list;
        }

        static bool UsableCart(PhysGrabCart c)
        {
            if (!c || !c.isActiveAndEnabled || !InCart(c)) return false;
            // Eingesteckte Pocket Carts parkt das Spiel bei physDisabledPosition (weit oben)
            if (c.transform.position.y > 1000f) return false;
            var eq = c.GetComponent<ItemEquippable>();
            return !eq || !IsEquipped(eq);
        }

        static void Say(string text, bool good)
        {
            Plugin.Log.LogInfo(text);
            try { SemiFunc.UIFocusText(text, Color.white, good ? Color.green : Color.red, 3f); }
            catch { /* im Menü gibt es keine Anzeige */ }
        }
    }
}
