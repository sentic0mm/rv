// KopfZieher für R.E.P.O. – EXPERIMENTELL
//
// Tot und Mitspieler: F9 zieht den eigenen Kopf mit dem EIGENEN (deaktivierten) Greifer durch die Gänge
// zum aktiven Extraction Point (sonst Truck, wenn alle fertig, sonst nächstes Cart). Der Host braucht keine Mod.
//
// Warum das gehen sollte (aus Assembly-CSharp):
//  - GrabStartedRPC / GrabLinkRPC laufen über die PhotonView des KOPFES und prüfen nur
//    SemiFunc.OwnerOnlyRPC(_info, physGrabber.photonView) – also "kommt das vom Besitzer des Greifers?".
//    Tot, Entfernung, Sichtlinie werden beim Host nicht geprüft (das macht nur PhysGrabLogic beim Greifer selbst).
//  - Die Physik-Schleife des Hosts (PhysGrabObject) zieht mit Kraft Richtung physGrabPointPullerPosition,
//    gemessen ab physGrabPoint.position – ohne Tot-Prüfung.
//  - Der Greifer eines Toten ist deaktiviert, schickt also seinen Zielpunkt nicht mehr per OnPhotonSerializeView.
//    GrabLinkRPC setzt aber selbst Positionen: neue Revision -> Ziel- UND Haltepunkt = point;
//    gleiche Revision -> nur Haltepunkt = point. Also zwei Nachrichten: (neu, Karotte) und (gleich, Kopfposition).
//  - Die Karotte läuft auf dem NavMesh-Weg (wie Gegner laufen) immer ein Stück vor dem Kopf her.
//
// Host oder Singleplayer: kein Teleport mehr, sondern dasselbe Ziehen. Weil es dort kein Netz gibt (Singleplayer:
// ViewID 0) bzw. GrabLinkRPC beim eigenen Greifer des Hosts den Zielpunkt ignoriert (!component.isLocal), setzt
// die Mod die Greifer-Werte (playerGrabbing, grabbed, physGrabPoint, physGrabPointPullerPosition) direkt –
// genau die Werte, die die Physik-Schleife des Hosts benutzt.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;

namespace KopfZieher
{
    [BepInPlugin(Guid, "KopfZieher", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "strehl.kopfzieher";
        public const string Version = "0.4.0";

        internal static ConfigEntry<Key> PullKey;
        internal static ConfigEntry<float> Lookahead;
        internal static ConfigEntry<float> CarryHeight;
        internal static ConfigEntry<float> MaxSeconds;
        internal static ConfigEntry<bool> DebugLog;
        internal static ConfigEntry<bool> ShowPath;
        internal static ManualLogSource Log;

        void Awake()
        {
            Log = Logger;
            PullKey = Config.Bind("Allgemein", "Taste", Key.F9,
                "Tot: eigenen Kopf zum aktiven Extraction Point ziehen (sonst Truck / Cart). Nochmal drücken = abbrechen.");
            Lookahead = Config.Bind("Feintuning", "Vorlauf", 1.5f,
                "So viele Meter läuft der Zielpunkt auf dem Weg vor dem Kopf her.");
            CarryHeight = Config.Bind("Feintuning", "Tragehoehe", 0.8f, "So hoch über dem Boden wird der Kopf getragen (m).");
            MaxSeconds = Config.Bind("Feintuning", "Max Sekunden", 0f, "Danach gibt die Mod auf. 0 = nie, läuft bis der Kopf am Ziel ist.");
            DebugLog = Config.Bind("Feintuning", "Debug Log", true, "Jeden Schritt ins BepInEx-Log schreiben.");
            ShowPath = Config.Bind("Feintuning", "Weg anzeigen", true, "Geplanten Weg (gelb) und Zielpunkt (rot) im Spiel anzeigen – nur bei dir.");

            var go = new GameObject("KopfZieher");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Runner>();
            Log.LogInfo($"KopfZieher {Version} geladen, Taste {PullKey.Value}");
        }
    }

    public class Runner : MonoBehaviour
    {
        const float Tick = 0.1f;

        // Für andere Mods (z.B. KopfKamera): läuft gerade ein Zug, und welcher Kopf / welches Ziel
        public static bool Pulling { get; private set; }
        public static Transform PulledHead { get; private set; }
        public static Vector3 PullTarget { get; private set; }
        public static string PathInfo { get; private set; } = "";

        static readonly AccessTools.FieldRef<PlayerAvatar, bool> DeadSet = AccessTools.FieldRefAccess<PlayerAvatar, bool>("deadSet");
        static readonly AccessTools.FieldRef<PlayerAvatar, bool> IsDisabled = AccessTools.FieldRefAccess<PlayerAvatar, bool>("isDisabled");
        static readonly AccessTools.FieldRef<PlayerAvatar, PlayerDeathHead> DeathHead = AccessTools.FieldRefAccess<PlayerAvatar, PlayerDeathHead>("playerDeathHead");
        static readonly AccessTools.FieldRef<PlayerDeathHead, bool> Triggered = AccessTools.FieldRefAccess<PlayerDeathHead, bool>("triggered");
        static readonly AccessTools.FieldRef<PlayerDeathHead, PhysGrabObject> HeadPgo = AccessTools.FieldRefAccess<PlayerDeathHead, PhysGrabObject>("physGrabObject");
        static readonly AccessTools.FieldRef<PhysGrabObject, PhotonView> PgoView = AccessTools.FieldRefAccess<PhysGrabObject, PhotonView>("photonView");
        static readonly AccessTools.FieldRef<RoundDirector, bool> ExtractionActive = AccessTools.FieldRefAccess<RoundDirector, bool>("extractionPointActive");
        static readonly AccessTools.FieldRef<RoundDirector, bool> AllCompleted = AccessTools.FieldRefAccess<RoundDirector, bool>("allExtractionPointsCompleted");
        static readonly AccessTools.FieldRef<RoundDirector, ExtractionPoint> ExtractionCurrent = AccessTools.FieldRefAccess<RoundDirector, ExtractionPoint>("extractionPointCurrent");
        static readonly AccessTools.FieldRef<ExtractionPoint, ExtractionPoint.State> PointState = AccessTools.FieldRefAccess<ExtractionPoint, ExtractionPoint.State>("currentState");
        static readonly AccessTools.FieldRef<PhysGrabCart, Transform> InCart = AccessTools.FieldRefAccess<PhysGrabCart, Transform>("inCart");
        static readonly AccessTools.FieldRef<ItemEquippable, bool> IsEquipped = AccessTools.FieldRefAccess<ItemEquippable, bool>("isEquipped");

        Coroutine running;
        bool cancel;
        float cooldownUntil;
        int revision = (Environment.TickCount & 0x3FFFFFF) + 1000;

        void Update()
        {
            try
            {
                var kb = Keyboard.current;
                if (kb == null || !kb[Plugin.PullKey.Value].wasPressedThisFrame || Time.time < cooldownUntil) return;
                cooldownUntil = Time.time + 0.4f;
                if (running != null) { cancel = true; Say("Abgebrochen.", false); return; }
                Begin();
            }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }

        void Begin()
        {
            var me = SemiFunc.PlayerAvatarLocal();
            if (!me) { Say("Gerade kein Spieler da.", false); return; }
            if (!DeadSet(me)) { Say("Du lebst doch noch.", false); return; }
            var head = DeathHead(me);
            if (!IsDisabled(me) || !head || !Triggered(head)) { Say("Noch einen Moment, dein Kopf ist noch nicht da.", false); return; }
            var pgo = HeadPgo(head);
            if (!pgo) { Say("Kopf hat kein PhysGrabObject?", false); return; }

            var target = FindTarget(pgo.transform.position);
            if (target == null) { Say("Kein Ziel: kein Extraction Point aktiv und kein Cart auf der Map.", false); return; }

            bool local = SemiFunc.IsMasterClientOrSingleplayer();
            var grabber = me.physGrabber;
            if (!grabber || (!local && !grabber.photonView)) { Say("Kein Greifer gefunden?", false); return; }
            var view = PgoView(pgo);
            if (!local && !view) { Say("Kopf hat keine PhotonView?", false); return; }
            int colliderId = ColliderId(pgo);

            cancel = false;
            running = StartCoroutine(Pull(me, head, pgo, view, grabber, colliderId, target.Value, local));
        }

        static int ColliderId(PhysGrabObject pgo)
        {
            foreach (var t in pgo.colliders)
            {
                if (!t) continue;
                var c = t.GetComponent<PhysGrabObjectCollider>();
                if (c) return c.colliderID;
            }
            return 0;
        }

        // ---------------- Ziehen ----------------

        IEnumerator Pull(PlayerAvatar me, PlayerDeathHead head, PhysGrabObject pgo, PhotonView headView,
                         PhysGrabber grabber, int colliderId, Target target, bool local)
        {
            int grabberId = local ? 0 : grabber.photonView.ViewID;
            float start = Time.time;
            var startPos = pgo.transform.position;
            bool movedOnce = false;
            float bestRemaining = float.MaxValue, lastProgressTime = Time.time;
            int stuckCount = 0;
            float liftUntil = 0f, backOffUntil = 0f, arrivedSince = -1f, nextPath = 0f;
            Vector3 lastCarrot = new Vector3(float.NaN, 0, 0);
            var corners = new List<Vector3>();
            string result = null; bool ok = false;

            Say($"Ziehe Kopf -> {target.Name} … ({Plugin.PullKey.Value} = abbrechen){(local ? "  [lokal/Host]" : "")}", true);
            Pulling = true; PulledHead = pgo.transform; PullTarget = target.Pos;

            // Evtl. hängender Kraft-Sperr-Timer beim Host zurücksetzen (MasterAndOwnerOnly -> wir sind Besitzer)
            if (local)
                LocalStart(pgo, grabber);
            else
            {
                TryRpc(grabber.photonView, "PhysGrabBeamDeactivateRPC", RpcTarget.MasterClient);
                // "Mein Greifer hält jetzt den Kopf" – genau wie das Spiel es schickt
                TryRpc(headView, "GrabStartedRPC", RpcTarget.MasterClient, grabberId);
            }

            while (true)
            {
                if (cancel) { result = "Abgebrochen."; break; }
                if (!me || !pgo || !DeadSet(me) || !Triggered(head)) { result = null; break; } // wiederbelebt / Level vorbei
                if (Plugin.MaxSeconds.Value > 0f && Time.time - start > Plugin.MaxSeconds.Value) { result = "Aufgegeben: dauert zu lange."; break; }

                var headPos = pgo.transform.position;
                if (!movedOnce && Vector3.Distance(headPos, startPos) > 0.4f) movedOnce = true;
                if (!movedOnce && Time.time - start > 4f)
                {
                    result = "Kopf bewegt sich nicht – der Host nimmt das so wohl nicht an.";
                    break;
                }

                // Ziel kann sich ändern (Cart wird geschoben, Extraction Point geht aus …)
                var fresh = FindTarget(headPos);
                if (fresh == null) { result = "Ziel ist weg."; break; }
                target = fresh.Value;
                PullTarget = target.Pos;

                if (Time.time >= nextPath)
                {
                    BuildPath(headPos, target.Pos, corners);
                    nextPath = Time.time + 1f;
                }

                float remaining = RemainingAlong(corners, headPos, out int seg, out Vector3 onPath);
                float direct = Vector3.Distance(headPos, target.Pos);

                // Angekommen? Ein Weilchen nah dran -> loslassen, Kopf fällt hinein
                if (direct < 0.9f)
                {
                    if (arrivedSince < 0f) arrivedSince = Time.time;
                    if (Time.time - arrivedSince > 0.8f) { result = $"Kopf ist im {target.Name}."; ok = true; break; }
                }
                else arrivedSince = -1f;

                // Hängt fest? -> kurz anheben, nach 3x aufgeben
                if (remaining < bestRemaining - 0.3f) { bestRemaining = remaining; lastProgressTime = Time.time; }
                else if (Time.time - lastProgressTime > 4f)
                {
                    // Nicht aufgeben: abwechselnd anheben und zurückziehen, Weg neu rechnen
                    stuckCount++;
                    liftUntil = Time.time + 1.2f;
                    backOffUntil = (stuckCount % 2 == 0) ? Time.time + 0.8f : 0f;
                    lastProgressTime = Time.time;
                    bestRemaining = remaining + 0.5f;
                    nextPath = 0f;
                    if (stuckCount % 3 == 0) Say($"Kopf hängt, neuer Versuch ({stuckCount}) …", false);
                    Dbg($"Festgehangen ({stuckCount}), hebe an{(backOffUntil > 0f ? " + zurück" : "")}");
                }

                Vector3 carrot;
                if (remaining < Plugin.Lookahead.Value + 0.5f || corners.Count < 2)
                    carrot = target.Pos + Vector3.up * 0.4f;      // letzter Meter: direkt aufs Ziel
                else
                {
                    var p = PointAhead(corners, seg, onPath, Plugin.Lookahead.Value);
                    p.y = FloorY(p) + Plugin.CarryHeight.Value;  // Höhe immer vom echten Boden aus
                    carrot = p;
                }
                if (Time.time < backOffUntil && corners.Count >= 2)
                {
                    // ein Stück den Weg zurück (zum letzten Eckpunkt), nicht einfach "weg von der Karotte"
                    var prev = corners[Mathf.Max(seg, 0)];
                    carrot = new Vector3(prev.x, FloorY(prev) + Plugin.CarryHeight.Value, prev.z);
                }
                // Anheben nur, wenn der Kopf nicht eh schon hoch hängt (sonst klebt er an der Decke)
                if (Time.time < liftUntil && headPos.y - FloorY(headPos) < 1.5f) carrot += Vector3.up * 1.0f;
                DrawDebug(corners, carrot);

                if (local)
                {
                    LocalSet(pgo, grabber, headPos, carrot);
                    Dbg($"[lokal] Kopf {headPos} Karotte {carrot} Rest {remaining:F1} m");
                    yield return null;   // lokal: jeden Frame, flüssiger
                    continue;
                }

                // Neue Revision nur, wenn sich die Karotte merklich bewegt hat
                if (float.IsNaN(lastCarrot.x) || Vector3.Distance(carrot, lastCarrot) > 0.25f)
                {
                    revision = revision >= int.MaxValue - 2 ? 1000 : revision + 1;
                    Link(headView, grabberId, colliderId, carrot, revision);   // Ziel- und Haltepunkt = Karotte
                    lastCarrot = carrot;
                }
                Link(headView, grabberId, colliderId, headPos, revision);      // Haltepunkt = Kopf -> Kraft Richtung Karotte
                Dbg($"Kopf {headPos} Karotte {carrot} Rest {remaining:F1} m");

                yield return new WaitForSeconds(Tick);
            }

            // Loslassen wie das Spiel es macht
            if (local) LocalEnd(pgo, grabber);
            else TryRpc(headView, "GrabEndedRPC", RpcTarget.MasterClient, grabberId);
            running = null;
            Pulling = false; PulledHead = null;
            HideDebug();
            if (result != null) Say(result, ok);
        }

        // ---- Weg sichtbar machen (nur lokal) ----
        static LineRenderer pathLine;
        static GameObject carrotMarker;

        static void DrawDebug(List<Vector3> corners, Vector3 carrot)
        {
            if (!Plugin.ShowPath.Value) { HideDebug(); return; }
            try
            {
                if (!pathLine)
                {
                    var go = new GameObject("KopfZieher_Weg");
                    pathLine = go.AddComponent<LineRenderer>();
                    pathLine.material = new Material(Shader.Find("Hidden/Internal-Colored"));
                    pathLine.startColor = pathLine.endColor = Color.yellow;
                    pathLine.startWidth = pathLine.endWidth = 0.06f;
                    pathLine.useWorldSpace = true;
                }
                if (!carrotMarker)
                {
                    carrotMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    carrotMarker.name = "KopfZieher_Ziel";
                    UnityEngine.Object.Destroy(carrotMarker.GetComponent<Collider>());
                    carrotMarker.transform.localScale = Vector3.one * 0.2f;
                    var r = carrotMarker.GetComponent<Renderer>();
                    r.material = new Material(Shader.Find("Hidden/Internal-Colored"));
                    r.material.color = Color.red;
                }
                pathLine.gameObject.SetActive(true);
                carrotMarker.SetActive(true);
                pathLine.positionCount = corners.Count;
                for (int i = 0; i < corners.Count; i++) pathLine.SetPosition(i, corners[i] + Vector3.up * 0.15f);
                carrotMarker.transform.position = carrot;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Weg anzeigen: " + e.Message); Plugin.ShowPath.Value = false; }
        }

        static void HideDebug()
        {
            if (pathLine) pathLine.gameObject.SetActive(false);
            if (carrotMarker) carrotMarker.SetActive(false);
        }

        // ---- lokaler Weg (Host / Singleplayer): Greifer-Werte direkt setzen ----
        static readonly AccessTools.FieldRef<PhysGrabber, PhysGrabObject> GrabbedPgo = AccessTools.FieldRefAccess<PhysGrabber, PhysGrabObject>("grabbedPhysGrabObject");

        static void LocalStart(PhysGrabObject pgo, PhysGrabber g)
        {
            g.physGrabForcesDisabledTimer = 0f;
            GrabbedPgo(g) = pgo;
            g.grabbedObjectTransform = pgo.transform;
            g.localGrabPosition = Vector3.zero;
            // Aktuelle Drehung des Kopfes halten – wie GrabLinkRPC es setzt
            try
            {
                var ot = g.playerAvatar && g.playerAvatar.localCamera ? g.playerAvatar.localCamera.GetOverrideTransform() : null;
                if (ot)
                {
                    g.cameraRelativeGrabbedForward = ot.InverseTransformDirection(pgo.transform.forward).normalized;
                    g.cameraRelativeGrabbedUp = ot.InverseTransformDirection(pgo.transform.up).normalized;
                }
                else { g.cameraRelativeGrabbedForward = Vector3.forward; g.cameraRelativeGrabbedUp = Vector3.up; }
            }
            catch { g.cameraRelativeGrabbedForward = Vector3.forward; g.cameraRelativeGrabbedUp = Vector3.up; }
            g.grabbed = true;
            if (!pgo.playerGrabbing.Contains(g)) pgo.playerGrabbing.Add(g);
        }

        static void LocalSet(PhysGrabObject pgo, PhysGrabber g, Vector3 headPos, Vector3 carrot)
        {
            g.grabbed = true;
            g.physGrabForcesDisabledTimer = 0f;
            if (!pgo.playerGrabbing.Contains(g)) pgo.playerGrabbing.Add(g);
            if (g.physGrabPoint) g.physGrabPoint.position = headPos;   // Haltepunkt = Kopf
            g.physGrabPointPullerPosition = carrot;                    // Zielpunkt = Karotte
            if (g.physGrabPointPuller) g.physGrabPointPuller.position = carrot;
        }

        static void LocalEnd(PhysGrabObject pgo, PhysGrabber g)
        {
            try
            {
                if (pgo) pgo.playerGrabbing.Remove(g);
                if (g)
                {
                    g.grabbed = false;
                    GrabbedPgo(g) = null;
                    g.grabbedObjectTransform = null;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("LocalEnd: " + e.Message); }
        }

        void Link(PhotonView headView, int grabberId, int colliderId, Vector3 point, int rev)
        {
            TryRpc(headView, "GrabLinkRPC", RpcTarget.MasterClient, grabberId, colliderId, point, Vector3.forward, Vector3.up, rev);
        }

        static void TryRpc(PhotonView view, string name, RpcTarget to, params object[] args)
        {
            try { view.RPC(name, to, args); }
            catch (Exception e) { Plugin.Log.LogWarning($"{name}: {e.Message}"); }
        }

        // ---------------- NavMesh ----------------

        static readonly NavMeshPath navPath = new NavMeshPath();

        static readonly int FloorMask = LayerMask.GetMask("Default");

        // Wie das Spiel selbst: erst senkrecht auf den Boden, dann den NavMesh-Punkt dort.
        // (Direkt um den Kopf suchen trifft sonst NavMesh-Stücke oben auf Felsen/Balken.)
        static bool FloorPoint(Vector3 p, out Vector3 nav)
        {
            nav = p;
            if (Physics.Raycast(p + Vector3.up * 0.3f, Vector3.down, out var hit, 12f, FloorMask, QueryTriggerInteraction.Ignore) &&
                NavMesh.SamplePosition(hit.point, out var n, 1.5f, NavMesh.AllAreas))
            { nav = n.position; return true; }
            if (NavMesh.SamplePosition(p, out var n2, 4f, NavMesh.AllAreas)) { nav = n2.position; return true; }
            return false;
        }

        static float FloorY(Vector3 p)
        {
            return Physics.Raycast(p + Vector3.up * 0.3f, Vector3.down, out var hit, 12f, FloorMask, QueryTriggerInteraction.Ignore)
                ? hit.point.y : p.y - 1f;
        }

        static void BuildPath(Vector3 from, Vector3 to, List<Vector3> corners)
        {
            corners.Clear();
            if (FloorPoint(from, out var a) && FloorPoint(to, out var b) &&
                NavMesh.CalculatePath(a, b, NavMesh.AllAreas, navPath) &&
                navPath.status != NavMeshPathStatus.PathInvalid && navPath.corners.Length >= 2)
            {
                corners.AddRange(navPath.corners);
                // Letzter Punkt genau aufs Ziel (Extraction-/Cart-Mitte liegt evtl. neben dem NavMesh)
                corners.Add(to);
                PathInfo = $"Weg: {(navPath.status == NavMeshPathStatus.PathComplete ? "komplett" : "nur teilweise!")}, {corners.Count} Punkte";
                Dbg($"{PathInfo}  Start {a} (Kopf {from})  Ende {b}");
                return;
            }
            // Kein Weg gefunden -> gerade Linie (klappt nur ohne Wand dazwischen)
            corners.Add(from);
            corners.Add(to);
            PathInfo = "Weg: KEINER gefunden – gerade Linie";
            Dbg($"{PathInfo}  Kopf {from}  Ziel {to}");
        }

        // Restweg ab dem Punkt auf dem Weg, der dem Kopf am nächsten ist (nur horizontal gemessen)
        static float RemainingAlong(List<Vector3> c, Vector3 p, out int seg, out Vector3 onPath)
        {
            seg = 0; onPath = c.Count > 0 ? c[0] : p;
            if (c.Count < 2) return Vector3.Distance(p, onPath);
            float best = float.MaxValue;
            for (int i = 0; i < c.Count - 1; i++)
            {
                // 3D, Höhe halb gewichtet: Kopf schwebt über dem Boden, aber andere Stockwerke sollen nicht passen
                var q = ClosestOnSegment(Squash(c[i]), Squash(c[i + 1]), Squash(p), out float t);
                float d = (q - Squash(p)).sqrMagnitude;
                if (d < best) { best = d; seg = i; onPath = Vector3.Lerp(c[i], c[i + 1], t); }
            }
            float rest = Vector3.Distance(onPath, c[seg + 1]);
            for (int i = seg + 1; i < c.Count - 1; i++) rest += Vector3.Distance(c[i], c[i + 1]);
            return rest;
        }

        static Vector3 PointAhead(List<Vector3> c, int seg, Vector3 onPath, float dist)
        {
            var cur = onPath;
            for (int i = seg; i < c.Count - 1; i++)
            {
                var next = c[i + 1];
                float len = Vector3.Distance(cur, next);
                if (len >= dist) return Vector3.MoveTowards(cur, next, dist);
                dist -= len;
                cur = next;
            }
            return c[c.Count - 1];
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
        static Vector3 Squash(Vector3 v) => new Vector3(v.x, v.y * 0.5f, v.z);

        static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 p, out float t)
        {
            var ab = b - a;
            float len2 = ab.sqrMagnitude;
            t = len2 < 1e-6f ? 0f : Mathf.Clamp01(Vector3.Dot(p - a, ab) / len2);
            return a + ab * t;
        }

        // ---------------- Ziel (wie KopfTeleport) ----------------

        struct Target { public string Name; public Vector3 Pos; public Quaternion Rot; }

        static Target? FindTarget(Vector3 from)
        {
            var rd = RoundDirector.instance;
            if (rd && ExtractionActive(rd))
            {
                var p = ExtractionCurrent(rd);
                if (p && p.safetySpawn)
                {
                    var s = PointState(p);
                    if (s != ExtractionPoint.State.None && s != ExtractionPoint.State.Idle && s != ExtractionPoint.State.Complete &&
                        s != ExtractionPoint.State.Surplus && s != ExtractionPoint.State.TaxReturn)
                        return new Target { Name = "Extraction Point", Pos = p.safetySpawn.position, Rot = p.safetySpawn.rotation };
                }
            }
            if (rd && AllCompleted(rd))
            {
                var truck = TruckSafetySpawnPoint.instance;
                if (truck) return new Target { Name = "Truck", Pos = truck.transform.position, Rot = truck.transform.rotation };
                return null;
            }
            var cart = UnityEngine.Object.FindObjectsByType<PhysGrabCart>(FindObjectsSortMode.None)
                .Where(UsableCart)
                .OrderBy(c => Vector3.Distance(from, c.transform.position))
                .FirstOrDefault();
            if (cart) return new Target { Name = cart.isSmallCart ? "Pocket Cart" : "Cart", Pos = InCart(cart).position, Rot = cart.transform.rotation };
            return null;
        }

        static bool UsableCart(PhysGrabCart c)
        {
            if (!c || !c.isActiveAndEnabled || !InCart(c)) return false;
            if (c.transform.position.y > 1000f) return false; // eingesteckt
            var eq = c.GetComponent<ItemEquippable>();
            return !eq || !IsEquipped(eq);
        }

        static void Dbg(string s) { if (Plugin.DebugLog.Value) Plugin.Log.LogInfo(s); }

        static void Say(string text, bool good)
        {
            Plugin.Log.LogInfo(text);
            try { SemiFunc.UIFocusText(text, Color.white, good ? Color.green : Color.red, 3f); } catch { }
        }
    }
}
