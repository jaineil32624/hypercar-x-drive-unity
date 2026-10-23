using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace HypercarX
{
    /// <summary>
    /// HYPERCAR-X Drive: game flow, menus, modes, split-screen, HUD and touch controls.
    /// Everything (world, cars, UI) is built at runtime so the scene only needs this component.
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        public static GameManager I;

        public enum Mode { Menu, Race, SplitRace, TimeTrial, Drag, FreeRoam, SplitFree, AIvsAI, GhostDuel }
        Mode mode = Mode.Menu;

        // ---------------------------------------------------------------- settings (saved)
        int laps = 3, aiCount = 5, difficulty = 1, touchMode = 0, units = 0, quality = 1;
        bool tilt, sound = true;
        static readonly string[] DiffNames = { "Easy", "Normal", "Hard" };
        static readonly float[] DiffSkill = { 0.72f, 0.86f, 0.97f };
        static readonly string[] TouchNames = { "Auto", "On", "Off" };

        // ---------------------------------------------------------------- runtime
        World world;
        Font font;
        Canvas canvas;
        RectTransform menuRoot, hudRoot;
        GameObject mapPanel;
        RawImage mapImage;
        readonly List<Image> mapDots = new List<Image>();
        Text toast; float toastUntil;

        class Racer
        {
            public CarController car; public AIDriver ai; public string label; public Color color;
            public int player = -1;      // -1 = AI, 0 = P1, 1 = P2
            public int lap, nextCp; public float lapStart, best = -1f, last = -1f, finishTime = -1f;
            public bool finished; public GhostLap rec; public float recNext; public int place;
            public float zero60 = -1f, zero100 = -1f, runStart = -1f;
        }
        class View
        {
            public Camera cam; public ChaseCamera cc; public RectTransform hud; public Racer racer;
            public Text speed, unit, top, center, autoLabel; public HoldButton left, right, gas, brake, hand;
            public bool autopilot; public GameObject touch;
        }
        readonly List<Racer> racers = new List<Racer>();
        readonly List<View> views = new List<View>();
        readonly List<GameObject> spawned = new List<GameObject>();
        bool running, paused, countdown;
        float raceStart;
        int spectateIndex;
        GhostPlayer ghost;
        float dragGreen = -1f; bool falseStart;

        // ================================================================= lifecycle
        void Awake()
        {
            I = this;
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 0;
            Input.multiTouchEnabled = true;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
            LoadSettings();
            Garage.Load();
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            ApplyQuality();
            world = new GameObject("World").AddComponent<World>();
            world.Build(quality == 0);
            BuildUI();
            ShowMain();
            StartAttract();
        }

        void LoadSettings()
        {
            laps = PlayerPrefs.GetInt("hx_laps", 3); aiCount = PlayerPrefs.GetInt("hx_ai", 5);
            difficulty = PlayerPrefs.GetInt("hx_diff", 1); touchMode = PlayerPrefs.GetInt("hx_touch", 0);
            units = PlayerPrefs.GetInt("hx_units", 0); tilt = PlayerPrefs.GetInt("hx_tilt", 0) == 1;
            sound = PlayerPrefs.GetInt("hx_sound", 1) == 1;
            quality = PlayerPrefs.GetInt("hx_quality", Application.isMobilePlatform ? 0 : 1);
        }

        void SaveSettings()
        {
            PlayerPrefs.SetInt("hx_laps", laps); PlayerPrefs.SetInt("hx_ai", aiCount); PlayerPrefs.SetInt("hx_diff", difficulty);
            PlayerPrefs.SetInt("hx_touch", touchMode); PlayerPrefs.SetInt("hx_units", units); PlayerPrefs.SetInt("hx_tilt", tilt ? 1 : 0);
            PlayerPrefs.SetInt("hx_sound", sound ? 1 : 0); PlayerPrefs.SetInt("hx_quality", quality); PlayerPrefs.Save();
        }

        void ApplyQuality()
        {
            QualitySettings.shadowDistance = quality == 0 ? 60f : 160f;
            QualitySettings.shadows = quality == 0 ? ShadowQuality.HardOnly : ShadowQuality.All;
            QualitySettings.antiAliasing = quality == 0 ? 0 : 2;
            QualitySettings.lodBias = quality == 0 ? 0.7f : 1.5f;
            AudioListener.volume = sound ? 1f : 0f;
        }

        bool TouchUI => touchMode == 1 || (touchMode == 0 && (Application.isMobilePlatform || Input.touchSupported));

        // ================================================================= car construction
        static Transform FindDeep(Transform t, string n)
        {
            if (t.name == n) return t;
            foreach (Transform c in t) { var r = FindDeep(c, n); if (r) return r; }
            return null;
        }

        static Material Mat(string n) => Resources.Load<Material>("Materials/" + n);

        /// <summary>Instantiates the Blender model, normalises scale/orientation and assigns materials.</summary>
        GameObject BuildModel(Transform parent, Color paint, float wheelScale, bool ghostLook,
                              out Transform[] spins, out Renderer[] tails, out Transform wing)
        {
            var prefab = Resources.Load<GameObject>("HypercarX");
            var model = Instantiate(prefab, parent);
            model.name = "Model";
            model.transform.localPosition = Vector3.zero; model.transform.localRotation = Quaternion.identity;
            string[] tags = { "FL", "FR", "RL", "RR" };
            spins = new Transform[4];
            for (int i = 0; i < 4; i++) spins[i] = FindDeep(model.transform, "HX_Spin_" + tags[i]);

            // scale so the body is 4.62 m long
            var body = FindDeep(model.transform, "HX_Body").GetComponent<Renderer>();
            Bounds b = body.bounds;
            float len = Mathf.Max(b.size.x, b.size.z);
            model.transform.localScale *= 4.62f / Mathf.Max(0.01f, len);
            // orient so front axle points +Z of the car
            Vector3 f = (spins[0].position + spins[1].position) * 0.5f, r = (spins[2].position + spins[3].position) * 0.5f;
            Vector3 dir = parent.InverseTransformDirection(f - r); dir.y = 0f;
            model.transform.localRotation = Quaternion.FromToRotation(dir.normalized, Vector3.forward) * model.transform.localRotation;
            // left wheels must be at -X (Unity left); if mirrored, swap tags
            if (parent.InverseTransformPoint(spins[0].position).x > 0f)
            {
                (spins[0], spins[1]) = (spins[1], spins[0]);
                (spins[2], spins[3]) = (spins[3], spins[2]);
            }
            // centre between axles, wheel centres at 0.355 m
            f = parent.InverseTransformPoint((spins[0].position + spins[1].position) * 0.5f);
            r = parent.InverseTransformPoint((spins[2].position + spins[3].position) * 0.5f);
            Vector3 mid = (f + r) * 0.5f;
            model.transform.localPosition += new Vector3(-mid.x, 0.355f - mid.y, -mid.z);
            foreach (var s in spins) s.localScale *= wheelScale;

            // materials by object name
            var paintMat = new Material(Mat(ghostLook ? "Ghost" : "Paint")) { color = ghostLook ? new Color(paint.r, paint.g, paint.b, 0.35f) : paint };
            var tailList = new List<Renderer>();
            foreach (var rend in model.GetComponentsInChildren<Renderer>(true))
            {
                string n = rend.gameObject.name;
                Material m;
                if (ghostLook) m = n.StartsWith("HX_Body") ? paintMat : Mat("Ghost");
                else if (n.StartsWith("HX_Body")) m = paintMat;
                else if (n.StartsWith("HX_Canopy")) m = Mat("Glass");
                else if (n.StartsWith("HX_Wing") || n.StartsWith("HX_Endplate") || n.StartsWith("HX_Splitter") || n.StartsWith("HX_DiffFin")) m = Mat("Carbon");
                else if (n.StartsWith("HX_Headlight")) m = Mat("HeadLight");
                else if (n.StartsWith("HX_Taillight")) { m = new Material(Mat("TailLight")); tailList.Add(rend); }
                else if (n.StartsWith("HX_Tire")) m = Mat("Tire");
                else if (n.StartsWith("HX_Rim") || n.StartsWith("HX_Spoke")) m = Mat("Rim");
                else if (n.StartsWith("HX_Disc")) m = Mat("Disc");
                else if (n.StartsWith("HX_Hub") || n.StartsWith("HX_Caliper")) m = Mat("Caliper");
                else m = Mat("Trim");
                var arr = rend.sharedMaterials;
                for (int k = 0; k < arr.Length; k++) arr[k] = m;
                rend.sharedMaterials = arr;
                if (ghostLook) rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            tails = tailList.ToArray();
            wing = FindDeep(model.transform, "HX_WingPivot");
            return model;
        }

        Racer SpawnCar(ResolvedSpec spec, Color paint, string label, Vector3 pos, Quaternion rot, int player)
        {
            var root = new GameObject(label);
            root.transform.SetPositionAndRotation(pos, rot);
            spawned.Add(root);
            var rb = root.AddComponent<Rigidbody>();
            rb.mass = spec.mass;
            var col = root.AddComponent<BoxCollider>();
            col.center = new Vector3(0f, 0.72f, 0.05f); col.size = new Vector3(1.86f, 0.7f, 4.45f);
            var phys = new PhysicsMaterial { dynamicFriction = 0.3f, staticFriction = 0.3f, bounciness = 0.05f };
            col.sharedMaterial = phys;
            BuildModel(root.transform, paint, spec.wheelD / 0.71f, false, out var spins, out var tails, out var wing);
            var car = root.AddComponent<CarController>();
            car.brakeLights = tails; car.wing = wing;
            car.Setup(spec, spins, new Transform[2]);
            car.audioEnabled = player >= 0 || mode == Mode.AIvsAI;
            var rc = new Racer { car = car, label = label, color = paint, player = player };
            racers.Add(rc);
            return rc;
        }

        GhostPlayer SpawnGhost(GhostLap lap, Color c)
        {
            var go = new GameObject("Ghost " + lap.car);
            spawned.Add(go);
            BuildModel(go.transform, c, 1f, true, out _, out _, out _);
            var gp = go.AddComponent<GhostPlayer>(); gp.lap = lap;
            return gp;
        }

        // ================================================================= starting modes
        void ClearSession()
        {
            StopAllCoroutines();
            foreach (var g in spawned) if (g) Destroy(g);
            spawned.Clear(); racers.Clear();
            foreach (var v in views) { if (v.cam) Destroy(v.cam.gameObject); if (v.hud) Destroy(v.hud.gameObject); }
            views.Clear();
            running = false; countdown = false; ghost = null; dragGreen = -1f; falseStart = false;
            SetPaused(false);
        }

        /// <summary>Menu background: AI cars lapping with a cinematic camera.</summary>
        void StartAttract()
        {
            ClearSession();
            mode = Mode.Menu;
            for (int i = 0; i < 3; i++)
            {
                var rc = SpawnCar(Garage.Resolve(i * 2 % 5, false), Garage.Paints[(i * 3) % Garage.Paints.Length], "Attract" + i,
                    GridPos(i), world.gridRot, -1);
                rc.car.audioEnabled = false;
                AddAI(rc, 0.8f, (i - 1) * 2.5f);
            }
            var v = MakeView(racers[0], new Rect(0, 0, 1, 1), -1);
            v.cc.mode = 3;
            mapPanel.SetActive(false);
            running = true;
        }

        Vector3 GridPos(int slot)
        {
            Vector3 dir = world.gridRot * Vector3.forward, right = world.gridRot * Vector3.right;
            return world.gridPos - dir * (slot / 2) * 9f + right * ((slot % 2 == 0) ? -3.2f : 3.2f) - dir * (slot % 2) * 4f;
        }

        void AddAI(Racer rc, float skill, float lane)
        {
            var ai = rc.car.gameObject.AddComponent<AIDriver>();
            ai.world = world; ai.skill = skill; ai.laneOffset = lane;
            rc.ai = ai;
        }

        void StartMode(Mode m)
        {
            ClearSession();
            mode = m;
            HideMenus();
            Garage.Save();
            var p1Spec = Garage.Resolve();
            Color p1Paint = Garage.Paints[Garage.paint];
            var p2Spec = Garage.Resolve(Garage.p2Variant, false);
            Color p2Paint = Garage.Paints[Garage.p2Paint];
            float skill = DiffSkill[difficulty];
            int slot = 0;

            switch (m)
            {
                case Mode.Race:
                case Mode.SplitRace:
                {
                    int humans = m == Mode.SplitRace ? 2 : 1;
                    var p1 = SpawnCar(p1Spec, p1Paint, "P1", GridPos(aiCount + humans - 1), world.gridRot, 0);
                    Racer p2 = null;
                    if (humans == 2) p2 = SpawnCar(p2Spec, p2Paint, "P2", GridPos(aiCount), world.gridRot, 1);
                    for (int i = 0; i < aiCount; i++)
                    {
                        var rc = SpawnCar(Garage.Resolve(i % 5, false), Garage.Paints[(i + 2) % Garage.Paints.Length], "AI " + Garage.Variants[i % 5].id + (i + 1),
                            GridPos(i), world.gridRot, -1);
                        AddAI(rc, skill * (0.94f + 0.03f * (i % 3)), (i % 3 - 1) * 2.2f);
                    }
                    if (humans == 1) MakeView(p1, new Rect(0, 0, 1, 1), 0);
                    else { MakeView(p1, new Rect(0, 0, 0.5f, 1), 0); MakeView(p2, new Rect(0.5f, 0, 0.5f, 1), 1); }
                    mapPanel.SetActive(true);
                    StartCoroutine(Countdown());
                    break;
                }
                case Mode.TimeTrial:
                {
                    var p1 = SpawnCar(p1Spec, p1Paint, "P1", GridPos(0), world.gridRot, 0);
                    MakeView(p1, new Rect(0, 0, 1, 1), 0);
                    if (GhostBank.Laps.Count > 0) ghost = SpawnGhost(GhostBank.Laps[0], new Color(0.4f, 0.9f, 1f));
                    mapPanel.SetActive(true);
                    StartCoroutine(Countdown());
                    break;
                }
                case Mode.Drag:
                {
                    var p1 = SpawnCar(p1Spec, p1Paint, "P1", new Vector3(-164.5f, 0.6f, -3f), Quaternion.identity, 0);
                    var rival = SpawnCar(Garage.Resolve(1, false), Garage.Paints[1], "AI Track Hypercar", new Vector3(-155.5f, 0.6f, -3f), Quaternion.identity, -1);
                    AddAI(rival, 1f, -155.5f); rival.ai.dragMode = true;
                    MakeView(p1, new Rect(0, 0, 1, 1), 0);
                    mapPanel.SetActive(false);
                    StartCoroutine(DragStart());
                    break;
                }
                case Mode.FreeRoam:
                case Mode.SplitFree:
                {
                    var p1 = SpawnCar(p1Spec, p1Paint, "P1", GridPos(0), world.gridRot, 0);
                    if (m == Mode.SplitFree)
                    {
                        var p2 = SpawnCar(p2Spec, p2Paint, "P2", GridPos(1), world.gridRot, 1);
                        MakeView(p1, new Rect(0, 0, 0.5f, 1), 0); MakeView(p2, new Rect(0.5f, 0, 0.5f, 1), 1);
                    }
                    else MakeView(p1, new Rect(0, 0, 1, 1), 0);
                    mapPanel.SetActive(true);
                    running = true;
                    Toast("Free roam: circuit, drag strip, skid pad, slalom and jump ramps");
                    break;
                }
                case Mode.AIvsAI:
                {
                    int n = Mathf.Max(2, aiCount + 1);
                    for (int i = 0; i < n; i++)
                    {
                        var rc = SpawnCar(Garage.Resolve(i % 5, false), Garage.Paints[i % Garage.Paints.Length], "AI " + Garage.Variants[i % 5].id + (i + 1),
                            GridPos(i), world.gridRot, -1);
                        AddAI(rc, skill * (0.92f + 0.03f * (i % 4)), (i % 3 - 1) * 2.2f);
                    }
                    var v = MakeView(racers[0], new Rect(0, 0, 1, 1), -1);
                    v.cc.mode = 0;
                    mapPanel.SetActive(true);
                    StartCoroutine(Countdown());
                    break;
                }
                case Mode.GhostDuel:
                {
                    var laps2 = GhostBank.Laps;
                    if (laps2.Count < 2) { Toast("Set two laps in Time Trial first. Your two best laps race here."); ShowMain(); StartAttract(); return; }
                    var g1 = SpawnGhost(laps2[0], new Color(0.3f, 0.9f, 1f));
                    var g2 = SpawnGhost(laps2[1], new Color(1f, 0.5f, 0.2f));
                    g1.loop = g2.loop = true;
                    g1.startTime = g2.startTime = Time.time;
                    var cam = MakeView(null, new Rect(0, 0, 1, 1), -1);
                    cam.cc.target = g1.transform; cam.cc.mode = 0;
                    Toast($"Ghost duel: best {Fmt(laps2[0].lapTime)} ({laps2[0].car}) vs {Fmt(laps2[1].lapTime)} ({laps2[1].car})");
                    mapPanel.SetActive(false);
                    running = true;
                    break;
                }
            }
            _ = slot;
        }

        IEnumerator Countdown()
        {
            countdown = true;
            foreach (var r in racers) r.car.locked = true;
            for (int i = 3; i > 0; i--)
            {
                Center(i.ToString());
                yield return new WaitForSeconds(0.9f);
            }
            Center("GO!");
            foreach (var r in racers) { r.car.locked = false; r.nextCp = 0; r.lap = 0; r.runStart = Time.time; }
            raceStart = Time.time;
            countdown = false; running = true;
            yield return new WaitForSeconds(0.8f);
            Center("");
        }

        IEnumerator DragStart()
        {
            countdown = true;
            foreach (var r in racers) r.car.locked = true;
            world.SetLights(-1);
            Center("Stage... hold GAS only after GREEN");
            yield return new WaitForSeconds(1.5f);
            for (int i = 0; i < 3; i++) { world.SetLights(i); Center(""); yield return new WaitForSeconds(0.5f); }
            world.SetLights(3);
            dragGreen = Time.time;
            foreach (var r in racers) { r.car.locked = false; r.runStart = Time.time; }
            countdown = false; running = true;
            Center("GO!");
            yield return new WaitForSeconds(0.7f);
            Center("");
        }

        // ================================================================= views / HUD
        View MakeView(Racer racer, Rect rect, int player)
        {
            var camGo = new GameObject("Cam" + views.Count);
            var cam = camGo.AddComponent<Camera>();
            cam.rect = rect; cam.nearClipPlane = 0.1f; cam.farClipPlane = 2200f; cam.fieldOfView = 62f;
            cam.depth = views.Count;
            if (views.Count == 0) camGo.AddComponent<AudioListener>();
            var cc = camGo.AddComponent<ChaseCamera>();
            cc.target = racer != null ? racer.car.transform : null;
            if (racer != null) camGo.transform.position = racer.car.transform.position - racer.car.transform.forward * 7f + Vector3.up * 3f;
            var v = new View { cam = cam, cc = cc, racer = racer };

            // HUD panel covering this viewport
            var hud = Rt("HUD" + views.Count, hudRoot);
            hud.anchorMin = new Vector2(rect.x, rect.y); hud.anchorMax = new Vector2(rect.x + rect.width, rect.y + rect.height);
            hud.offsetMin = hud.offsetMax = Vector2.zero;
            v.hud = hud;
            bool narrow = rect.width < 0.9f;
            v.top = Label(hud, "", 30, TextAnchor.UpperLeft, new Vector2(0, 1), new Vector2(0, 1), new Vector2(24, -20), new Vector2(620, 260));
            v.speed = Label(hud, "0", narrow ? 72 : 96, TextAnchor.LowerCenter, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 60), new Vector2(400, 120));
            v.unit = Label(hud, "km/h", 26, TextAnchor.LowerCenter, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 30), new Vector2(400, 40));
            v.center = Label(hud, "", narrow ? 80 : 110, TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, 80), new Vector2(1400, 200));
            v.center.color = new Color(0.4f, 1f, 0.95f);
            v.autoLabel = Label(hud, "AUTOPILOT", 30, TextAnchor.MiddleCenter, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 190), new Vector2(400, 50));
            v.autoLabel.color = new Color(1f, 0.75f, 0.2f); v.autoLabel.enabled = false;

            // top-right buttons
            var bar = Rt("Bar", hud);
            bar.anchorMin = bar.anchorMax = new Vector2(1, 1); bar.pivot = new Vector2(1, 1);
            bar.anchoredPosition = new Vector2(-16, -16); bar.sizeDelta = new Vector2(narrow ? 330 : 520, 80);
            var hl = bar.gameObject.AddComponent<HorizontalLayoutGroup>(); hl.spacing = 10; hl.childForceExpandWidth = true; hl.childControlWidth = true; hl.childControlHeight = true;
            if (player >= 0 || mode == Mode.AIvsAI || mode == Mode.GhostDuel || mode == Mode.Menu)
            {
                if (mode != Mode.Menu) Btn(bar, "II", () => SetPaused(!paused), 0);
                Btn(bar, "CAM", () => v.cc.Next(), 0);
                if (player >= 0)
                {
                    Btn(bar, "AUTO", () => ToggleAutopilot(v), 0);
                    Btn(bar, "RESET", () => ResetCar(v.racer), 0);
                }
                if (mode == Mode.AIvsAI) Btn(bar, "NEXT CAR", () => { spectateIndex = (spectateIndex + 1) % racers.Count; v.racer = racers[spectateIndex]; v.cc.target = v.racer.car.transform; }, 0);
                if (mode == Mode.Menu) bar.gameObject.SetActive(false);
            }

            // touch controls for human players
            if (player >= 0 && TouchUI)
            {
                var t = Rt("Touch", hud); t.anchorMin = Vector2.zero; t.anchorMax = Vector2.one; t.offsetMin = t.offsetMax = Vector2.zero;
                v.touch = t.gameObject;
                float s = narrow ? 150 : 190;
                v.left = Pad(t, "◀", new Vector2(0, 0), new Vector2(30, 30), s);
                v.right = Pad(t, "▶", new Vector2(0, 0), new Vector2(30 + s + 20, 30), s);
                v.brake = Pad(t, "BRAKE", new Vector2(1, 0), new Vector2(-30 - s - 20, 30), s);
                v.gas = Pad(t, "GAS", new Vector2(1, 0), new Vector2(-30, 30), s);
                v.hand = Pad(t, "DRIFT", new Vector2(1, 0), new Vector2(-30, 30 + s + 20), s * 0.7f);
            }
            views.Add(v);
            return v;
        }

        HoldButton Pad(RectTransform parent, string text, Vector2 anchor, Vector2 pos, float size)
        {
            var rt = Rt(text, parent);
            rt.anchorMin = rt.anchorMax = anchor; rt.pivot = anchor;
            rt.anchoredPosition = pos; rt.sizeDelta = new Vector2(size, size);
            var img = rt.gameObject.AddComponent<Image>(); img.color = new Color(1, 1, 1, 0.16f);
            var l = Label(rt, text, (int)(size * 0.2f), TextAnchor.MiddleCenter, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            l.rectTransform.offsetMin = l.rectTransform.offsetMax = Vector2.zero;
            l.raycastTarget = false;
            return rt.gameObject.AddComponent<HoldButton>();
        }

        void ToggleAutopilot(View v)
        {
            if (v.racer == null) return;
            v.autopilot = !v.autopilot;
            if (v.autopilot && v.racer.ai == null) AddAI(v.racer, DiffSkill[2], 0f);
            if (v.racer.ai) v.racer.ai.enabled = v.autopilot;
            v.autoLabel.enabled = v.autopilot;
            Toast(v.autopilot ? "Autopilot ON: the AI is driving your car" : "Autopilot OFF: you have control");
        }

        void ResetCar(Racer r)
        {
            if (r == null) return;
            if (mode == Mode.Drag)
            {
                r.car.ResetTo(new Vector3(r.player >= 0 ? -164.5f : -155.5f, 0.6f, r.car.transform.position.z), Quaternion.identity);
                return;
            }
            int i = world.ClosestTrackIndex(r.car.transform.position);
            var tr = world.track;
            r.car.ResetTo(tr[i] + Vector3.up * 0.7f, Quaternion.LookRotation(tr[(i + 1) % tr.Count] - tr[i]));
        }

        // ================================================================= per-frame
        void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape) && mode != Mode.Menu) SetPaused(!paused);
            if (toast && Time.unscaledTime > toastUntil) toast.text = "";
            if (paused || mode == Mode.Menu) { UpdateMap(); return; }

            for (int vi = 0; vi < views.Count; vi++)
            {
                var v = views[vi];
                var r = v.racer;
                if (r != null && r.player >= 0)
                {
                    HumanInput(v, r.player);
                    if (Input.GetKeyDown(r.player == 0 ? KeyCode.C : KeyCode.Period)) v.cc.Next();
                    if (Input.GetKeyDown(r.player == 0 ? KeyCode.P : KeyCode.Slash)) ToggleAutopilot(v);
                    if (Input.GetKeyDown(r.player == 0 ? KeyCode.R : KeyCode.Comma)) ResetCar(r);
                }
                else if (mode == Mode.AIvsAI && vi == 0 && Input.GetKeyDown(KeyCode.N))
                {
                    spectateIndex = (spectateIndex + 1) % racers.Count; v.racer = racers[spectateIndex]; v.cc.target = v.racer.car.transform;
                }
                if (mode == Mode.AIvsAI && Input.GetKeyDown(KeyCode.C)) v.cc.Next();
                UpdateHud(v);
            }

            // timers / ghost recording
            if (running && !countdown)
            {
                foreach (var r in racers)
                {
                    float mph = r.car.ForwardSpeed / 0.44704f;
                    if (r.runStart > 0)
                    {
                        if (r.zero60 < 0 && mph >= 60f) r.zero60 = Time.time - r.runStart;
                        if (r.zero100 < 0 && mph >= 100f) r.zero100 = Time.time - r.runStart;
                    }
                    if (mode == Mode.FreeRoam || mode == Mode.SplitFree)
                    {
                        if (r.car.SpeedKmh < 1f) { r.runStart = Time.time; r.zero60 = r.zero100 = -1f; }
                    }
                    if (r.rec != null && r.player >= 0 && Time.time >= r.recNext) { r.rec.Add(r.car.transform); r.recNext = Time.time + 1f / GhostLap.Hz; }
                }
                if (mode == Mode.Drag) DragUpdate();
                if (mode == Mode.Race || mode == Mode.SplitRace || mode == Mode.AIvsAI) UpdatePlaces();
            }
            UpdateMap();
        }

        void HumanInput(View v, int player)
        {
            var c = v.racer.car;
            if (v.autopilot) return;
            float thr = 0, brk = 0, st = 0, hb = 0;
            bool solo = views.FindAll(x => x.racer != null && x.racer.player >= 0).Count == 1;
            if (player == 0)
            {
                if (Input.GetKey(KeyCode.W) || (solo && Input.GetKey(KeyCode.UpArrow))) thr = 1;
                if (Input.GetKey(KeyCode.S) || (solo && Input.GetKey(KeyCode.DownArrow))) brk = 1;
                if (Input.GetKey(KeyCode.A) || (solo && Input.GetKey(KeyCode.LeftArrow))) st -= 1;
                if (Input.GetKey(KeyCode.D) || (solo && Input.GetKey(KeyCode.RightArrow))) st += 1;
                if (Input.GetKey(KeyCode.Space)) hb = 1;
                if (solo)
                {
                    float jx = Input.GetAxis("Horizontal"), jy = Input.GetAxis("Vertical");
                    if (Mathf.Abs(jx) > Mathf.Abs(st)) st = jx;
                    if (jy > 0.1f) thr = Mathf.Max(thr, jy); if (jy < -0.1f) brk = Mathf.Max(brk, -jy);
                }
            }
            else
            {
                if (Input.GetKey(KeyCode.UpArrow)) thr = 1;
                if (Input.GetKey(KeyCode.DownArrow)) brk = 1;
                if (Input.GetKey(KeyCode.LeftArrow)) st -= 1;
                if (Input.GetKey(KeyCode.RightArrow)) st += 1;
                if (Input.GetKey(KeyCode.RightShift)) hb = 1;
            }
            if (v.gas != null)
            {
                if (v.gas.held) thr = 1; if (v.brake.held) brk = 1; if (v.hand.held) hb = 1;
                if (v.left.held) st -= 1; if (v.right.held) st += 1;
                if (tilt && Application.isMobilePlatform && !v.left.held && !v.right.held) st = Mathf.Clamp(Input.acceleration.x * 2.2f, -1f, 1f);
            }
            if (mode == Mode.Drag && dragGreen < 0 && thr > 0.1f && !falseStart) { falseStart = true; }
            c.throttle = thr; c.brake = brk; c.steer = Mathf.Clamp(st, -1f, 1f); c.handbrake = hb;
        }

        string Spd(float kmh) => units == 0 ? Mathf.RoundToInt(kmh).ToString() : Mathf.RoundToInt(kmh / 1.609f).ToString();
        static string Fmt(float t) { if (t < 0) return "--:--.---"; int m = (int)(t / 60); return $"{m}:{(t - m * 60):00.000}"; }

        void UpdateHud(View v)
        {
            var r = v.racer;
            if (r == null) { v.speed.text = ""; v.unit.text = ""; v.top.text = "GHOST DUEL"; return; }
            v.speed.text = Spd(r.car.SpeedKmh);
            v.unit.text = (units == 0 ? "km/h" : "mph") + $"   {r.car.MotorRpm / 1000f:0.0}k rpm   {r.car.GForce.z:+0.0;-0.0}g";
            string s = $"<b>{r.label}</b>  {r.car.spec.title}\n";
            switch (mode)
            {
                case Mode.Race: case Mode.SplitRace: case Mode.AIvsAI:
                    s += $"POS {r.place}/{racers.Count}   LAP {Mathf.Clamp(r.lap, 1, laps)}/{laps}\n";
                    s += $"TIME {Fmt(running && !countdown ? (r.finished ? r.finishTime : Time.time - raceStart) : 0)}\n";
                    s += $"LAST {Fmt(r.last)}   BEST {Fmt(r.best)}";
                    break;
                case Mode.TimeTrial:
                    s += $"LAP {Mathf.Max(r.lap, 1)}   {Fmt(r.lap >= 1 ? Time.time - r.lapStart : 0)}\n";
                    s += $"LAST {Fmt(r.last)}   SESSION BEST {Fmt(r.best)}\n";
                    s += GhostBank.Laps.Count > 0 ? $"GHOST (your record) {Fmt(GhostBank.Laps[0].lapTime)}" : "No ghost yet: set a lap!";
                    break;
                case Mode.Drag:
                    s += $"0-60 {(r.zero60 > 0 ? r.zero60.ToString("0.00") + " s" : "--")}   0-100 {(r.zero100 > 0 ? r.zero100.ToString("0.00") + " s" : "--")}\n";
                    s += $"Distance {Mathf.Max(0, r.car.transform.position.z):0} / 402 m";
                    break;
                default:
                    s += $"0-60 mph {(r.zero60 > 0 ? r.zero60.ToString("0.00") + " s" : "--")}   0-100 mph {(r.zero100 > 0 ? r.zero100.ToString("0.00") + " s" : "--")}\n";
                    s += $"Downforce {r.car.DownforceN:0} N   Drag {r.car.DragN:0} N";
                    break;
            }
            v.top.text = s;
            if (v.touch) v.touch.SetActive(!v.autopilot);
        }

        // ================================================================= race logic
        public void OnCheckpoint(CarController car, int index)
        {
            if (!running || countdown) return;
            var r = racers.Find(x => x.car == car);
            if (r == null || r.finished) return;
            if (index != r.nextCp) return;
            int n = world.checkpoints.Count;
            if (index == 0)
            {
                if (r.lap >= 1)
                {
                    float lt = Time.time - r.lapStart;
                    r.last = lt;
                    if (r.best < 0 || lt < r.best) r.best = lt;
                    OnLapComplete(r, lt);
                }
                r.lap++;
                r.lapStart = Time.time;
                if (r.player >= 0) { r.rec = new GhostLap { car = r.car.spec.title }; r.recNext = 0f; }
                if (mode == Mode.TimeTrial && ghost != null) { ghost.startTime = Time.time; ghost.loop = false; }
                if ((mode == Mode.Race || mode == Mode.SplitRace || mode == Mode.AIvsAI) && r.lap > laps) FinishRacer(r);
            }
            r.nextCp = (index + 1) % n;
        }

        void OnLapComplete(Racer r, float lt)
        {
            if (r.player < 0) return;
            string who = r.player == 0 ? "P1" : "P2";
            int rank = Leaderboard.Add("Lap", who, r.car.spec.title, lt);
            if (r.rec != null && r.rec.Count > 10)
            {
                r.rec.lapTime = lt; r.rec.date = System.DateTime.Now.ToString("yyyy-MM-dd");
                bool pb = GhostBank.Submit(r.rec);
                if (pb) { Toast($"NEW RECORD {Fmt(lt)}! Your ghost has been saved."); if (mode == Mode.TimeTrial) { if (ghost) Destroy(ghost.gameObject); ghost = SpawnGhost(GhostBank.Laps[0], new Color(0.4f, 0.9f, 1f)); } }
                else Toast($"{who} lap {Fmt(lt)}" + (rank > 0 ? $"  (#{rank} on the leaderboard)" : ""));
            }
        }

        void FinishRacer(Racer r)
        {
            r.finished = true;
            r.finishTime = Time.time - raceStart;
            if (r.player >= 0)
            {
                int rank = Leaderboard.Add("Race", r.player == 0 ? "P1" : "P2", r.car.spec.title, r.finishTime);
                if (r.ai) r.ai.enabled = true; // let the AI cruise the car home after the flag
                var v = views.Find(x => x.racer == r);
                if (v != null) v.center.text = $"FINISHED P{r.place}";
                Toast($"{r.label} finished P{r.place} in {Fmt(r.finishTime)}" + (rank == 1 ? "  NEW RACE RECORD!" : ""));
            }
            bool humansDone = racers.TrueForAll(x => x.player < 0 || x.finished);
            bool allDone = racers.TrueForAll(x => x.finished);
            if ((mode != Mode.AIvsAI && humansDone) || allDone) StartCoroutine(ShowResultsSoon());
        }

        IEnumerator ShowResultsSoon()
        {
            yield return new WaitForSeconds(2.5f);
            var sorted = new List<Racer>(racers);
            sorted.Sort((a, b) => a.place.CompareTo(b.place));
            string t = "";
            foreach (var r in sorted)
                t += $"P{r.place}  {r.label,-22} {(r.finished ? Fmt(r.finishTime) : "racing...")}   best lap {Fmt(r.best)}\n";
            ShowResults("RACE RESULTS", t);
        }

        void UpdatePlaces()
        {
            int n = world.checkpoints.Count;
            var order = new List<Racer>(racers);
            float Score(Racer r)
            {
                if (r.finished) return 1e6f - r.finishTime;
                int passed = r.lap * n + (r.nextCp == 0 ? n : r.nextCp) - n;
                var cp = world.checkpoints[r.nextCp].position;
                return passed * 1000f - Vector3.Distance(r.car.transform.position, cp);
            }
            order.Sort((a, b) => Score(b).CompareTo(Score(a)));
            for (int i = 0; i < order.Count; i++) order[i].place = i + 1;
        }

        void DragUpdate()
        {
            foreach (var r in racers)
            {
                if (r.finished || r.car.transform.position.z < 402.336f) continue;
                r.finished = true;
                r.finishTime = Time.time - dragGreen;
                r.best = r.car.SpeedKmh;
                if (r.player >= 0)
                {
                    var rival = racers.Find(x => x.player < 0);
                    bool won = rival == null || !rival.finished;
                    string res = falseStart ? "FALSE START (red light)" : (won ? "YOU WIN!" : "Rival wins");
                    int rank = falseStart ? 0 : Leaderboard.Add("Drag", "P1", r.car.spec.title, r.finishTime);
                    var vd = Garage.Variants[Garage.variant];
                    ShowResults(res,
                        $"Quarter mile  {r.finishTime:0.000} s @ {Spd(r.car.SpeedKmh)} {(units == 0 ? "km/h" : "mph")}\n" +
                        $"0-60 mph {r.zero60:0.00} s    0-100 mph {r.zero100:0.00} s\n\n" +
                        $"Engineering model prediction for {vd.id}: 0-60 in {vd.zero60:0.00} s\n" +
                        (rank > 0 ? $"Leaderboard rank #{rank}" : ""));
                }
            }
        }

        // ================================================================= minimap
        Texture2D mapTex;
        const float MapMinX = -240f, MapMaxX = 380f, MapMinZ = -340f, MapMaxZ = 340f;

        void BuildMapTexture()
        {
            int w = 256, h = 280;
            mapTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(0, 0, 0, 0);
            void Dot(Vector3 p, Color32 c, int rad)
            {
                int x = (int)((p.x - MapMinX) / (MapMaxX - MapMinX) * (w - 1)), y = (int)((p.z - MapMinZ) / (MapMaxZ - MapMinZ) * (h - 1));
                for (int dx = -rad; dx <= rad; dx++) for (int dy = -rad; dy <= rad; dy++)
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx >= 0 && yy >= 0 && xx < w && yy < h) px[yy * w + xx] = c;
                }
            }
            var tr = world.track;
            for (int i = 0; i < tr.Count; i++)
                for (int k = 0; k < 6; k++) Dot(Vector3.Lerp(tr[i], tr[(i + 1) % tr.Count], k / 6f), new Color32(230, 235, 240, 230), 1);
            Dot(tr[0], new Color32(54, 211, 196, 255), 3);
            mapTex.SetPixels32(px); mapTex.Apply();
            mapImage.texture = mapTex;
        }

        void UpdateMap()
        {
            if (!mapPanel || !mapPanel.activeSelf) return;
            var rt = mapImage.rectTransform;
            while (mapDots.Count < racers.Count)
            {
                var d = Rt("dot", rt).gameObject.AddComponent<Image>();
                d.rectTransform.sizeDelta = new Vector2(14, 14);
                mapDots.Add(d);
            }
            for (int i = 0; i < mapDots.Count; i++)
            {
                bool on = i < racers.Count;
                mapDots[i].gameObject.SetActive(on);
                if (!on) continue;
                var p = racers[i].car.transform.position;
                float u = (p.x - MapMinX) / (MapMaxX - MapMinX), vv = (p.z - MapMinZ) / (MapMaxZ - MapMinZ);
                mapDots[i].rectTransform.anchorMin = mapDots[i].rectTransform.anchorMax = new Vector2(u, vv);
                mapDots[i].rectTransform.anchoredPosition = Vector2.zero;
                mapDots[i].color = racers[i].player >= 0 ? (racers[i].player == 0 ? Color.yellow : new Color(1f, 0.4f, 1f)) : racers[i].color;
                mapDots[i].rectTransform.sizeDelta = Vector2.one * (racers[i].player >= 0 ? 18 : 12);
            }
        }

        // ================================================================= UI construction
        RectTransform Rt(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        Text Label(RectTransform parent, string text, int size, TextAnchor anchor, Vector2 aMin, Vector2 aMax, Vector2 pos, Vector2 sizeDelta)
        {
            var rt = Rt("Text", parent);
            rt.anchorMin = aMin; rt.anchorMax = aMax; rt.pivot = new Vector2(aMin.x == aMax.x ? aMin.x : 0.5f, aMin.y == aMax.y ? aMin.y : 0.5f);
            rt.anchoredPosition = pos; rt.sizeDelta = sizeDelta;
            var t = rt.gameObject.AddComponent<Text>();
            t.font = font; t.fontSize = size; t.alignment = anchor; t.text = text; t.color = Color.white;
            t.supportRichText = true; t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Overflow;
            var sh = rt.gameObject.AddComponent<Shadow>(); sh.effectColor = new Color(0, 0, 0, 0.8f); sh.effectDistance = new Vector2(2, -2);
            t.raycastTarget = false;
            return t;
        }

        Button Btn(RectTransform parent, string text, UnityEngine.Events.UnityAction onClick, float height = 86)
        {
            var rt = Rt("Btn " + text, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = new Color(0.08f, 0.13f, 0.18f, 0.92f);
            var b = rt.gameObject.AddComponent<Button>();
            var cb = b.colors; cb.highlightedColor = new Color(0.4f, 1f, 0.95f); cb.pressedColor = new Color(0.2f, 0.8f, 0.75f); cb.selectedColor = Color.white; b.colors = cb;
            b.onClick.AddListener(onClick);
            if (height > 0) { var le = rt.gameObject.AddComponent<LayoutElement>(); le.minHeight = height; le.preferredHeight = height; }
            var l = Label(rt, text, 34, TextAnchor.MiddleCenter, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            l.rectTransform.offsetMin = new Vector2(10, 0); l.rectTransform.offsetMax = new Vector2(-10, 0);
            return b;
        }

        RectTransform Menu(string title, string subtitle = "")
        {
            foreach (Transform c in menuRoot) Destroy(c.gameObject);
            menuRoot.gameObject.SetActive(true);
            var bg = menuRoot.GetComponent<Image>();
            bg.color = new Color(0.02f, 0.04f, 0.06f, 0.55f);
            var t = Label(menuRoot, title, 92, TextAnchor.UpperCenter, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -50), new Vector2(1600, 120));
            t.color = new Color(0.4f, 1f, 0.95f);
            if (subtitle != "") Label(menuRoot, subtitle, 30, TextAnchor.UpperCenter, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -160), new Vector2(1600, 50)).color = new Color(0.75f, 0.85f, 0.95f);
            var col = Rt("Column", menuRoot);
            col.anchorMin = new Vector2(0.5f, 0); col.anchorMax = new Vector2(0.5f, 1); col.pivot = new Vector2(0.5f, 1);
            col.anchoredPosition = new Vector2(0, -230); col.sizeDelta = new Vector2(820, -270);
            var vl = col.gameObject.AddComponent<VerticalLayoutGroup>();
            vl.spacing = 14; vl.childForceExpandHeight = false; vl.childControlHeight = true; vl.childControlWidth = true; vl.childForceExpandWidth = true;
            return col;
        }

        void HideMenus() { menuRoot.gameObject.SetActive(false); hudRoot.gameObject.SetActive(true); }

        void BuildUI()
        {
            var cgo = new GameObject("Canvas");
            canvas = cgo.AddComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 10;
            var sc = cgo.AddComponent<CanvasScaler>();
            sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; sc.referenceResolution = new Vector2(1920, 1080); sc.matchWidthOrHeight = 0.6f;
            cgo.AddComponent<GraphicRaycaster>();
            if (FindFirstObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem"); es.AddComponent<EventSystem>(); es.AddComponent<StandaloneInputModule>();
            }
            hudRoot = Rt("HUDRoot", canvas.transform); hudRoot.anchorMin = Vector2.zero; hudRoot.anchorMax = Vector2.one; hudRoot.offsetMin = hudRoot.offsetMax = Vector2.zero;

            // minimap
            var mp = Rt("Minimap", canvas.transform);
            mp.anchorMin = mp.anchorMax = new Vector2(0, 0.5f); mp.pivot = new Vector2(0, 0.5f);
            mp.anchoredPosition = new Vector2(18, 40); mp.sizeDelta = new Vector2(250, 274);
            mp.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0.35f);
            mapPanel = mp.gameObject;
            var mi = Rt("Map", mp); mi.anchorMin = Vector2.zero; mi.anchorMax = Vector2.one; mi.offsetMin = mi.offsetMax = Vector2.zero;
            mapImage = mi.gameObject.AddComponent<RawImage>(); mapImage.raycastTarget = false;
            BuildMapTexture();

            menuRoot = Rt("Menus", canvas.transform); menuRoot.anchorMin = Vector2.zero; menuRoot.anchorMax = Vector2.one; menuRoot.offsetMin = menuRoot.offsetMax = Vector2.zero;
            menuRoot.gameObject.AddComponent<Image>();

            toast = Label((RectTransform)canvas.transform, "", 32, TextAnchor.UpperCenter, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -120), new Vector2(1500, 120));
            toast.color = new Color(1f, 0.9f, 0.5f);
        }

        void Toast(string s) { toast.text = s; toastUntil = Time.unscaledTime + 4f; }
        void Center(string s) { foreach (var v in views) v.center.text = s; }

        // ================================================================= menus
        void ShowMain()
        {
            SetPaused(false);
            var c = Menu("HYPERCAR-X DRIVE", "Parametric hypercar driving simulator · physics from the engineering model");
            Btn(c, "SINGLE PLAYER", ShowSingle);
            Btn(c, "2 PLAYERS · SPLIT-SCREEN", ShowSplit);
            Btn(c, "SPECTATE · AI vs AI / GHOSTS", ShowSpectate);
            Btn(c, "GARAGE", ShowGarage);
            Btn(c, "LEADERBOARD", ShowLeaderboard);
            Btn(c, "SETTINGS", ShowSettings);
            if (Application.platform != RuntimePlatform.WebGLPlayer) Btn(c, "QUIT", Application.Quit);
            hudRoot.gameObject.SetActive(false);
        }

        void ShowSingle()
        {
            var c = Menu("SINGLE PLAYER", $"{laps} laps · {aiCount} AI · {DiffNames[difficulty]} (change in Settings)");
            Btn(c, "RACE vs AI", () => StartMode(Mode.Race));
            Btn(c, "TIME TRIAL vs YOUR GHOST", () => StartMode(Mode.TimeTrial));
            Btn(c, "DRAG RACE ¼ MILE vs AI", () => StartMode(Mode.Drag));
            Btn(c, "FREE ROAM", () => StartMode(Mode.FreeRoam));
            Btn(c, "BACK", ShowMain);
        }

        void ShowSplit()
        {
            var c = Menu("2 PLAYERS · SPLIT-SCREEN", "P1: WASD + Space · C cam · P autopilot     P2: Arrows + Right Shift · . cam · / autopilot");
            Btn(c, $"P2 CAR: {Garage.Variants[Garage.p2Variant].id} · {Garage.Variants[Garage.p2Variant].name}", () => { Garage.p2Variant = (Garage.p2Variant + 1) % Garage.Variants.Length; ShowSplit(); });
            Btn(c, $"P2 PAINT: {Garage.PaintNames[Garage.p2Paint]}", () => { Garage.p2Paint = (Garage.p2Paint + 1) % Garage.Paints.Length; ShowSplit(); });
            Btn(c, "SPLIT-SCREEN RACE (+ AI)", () => StartMode(Mode.SplitRace));
            Btn(c, "SPLIT-SCREEN FREE ROAM", () => StartMode(Mode.SplitFree));
            Btn(c, "BACK", ShowMain);
        }

        void ShowSpectate()
        {
            var c = Menu("SPECTATE", "Watch the AI race, or watch your two best laps race each other");
            Btn(c, "AI vs AI RACE", () => StartMode(Mode.AIvsAI));
            Btn(c, GhostBank.Laps.Count >= 2 ? $"GHOST DUEL · {Fmt(GhostBank.Laps[0].lapTime)} vs {Fmt(GhostBank.Laps[1].lapTime)}" : "GHOST DUEL (set 2 laps in Time Trial)", () => StartMode(Mode.GhostDuel));
            Btn(c, "BACK", ShowMain);
        }

        void ShowGarage()
        {
            Garage.Load();
            var c = Menu("GARAGE", "Every part is a trade-off · stats update live");
            var v = Garage.Variants[Garage.variant];
            Btn(c, $"CAR ◀ {v.id} · {v.name} ▶", () => { Garage.variant = (Garage.variant + 1) % Garage.Variants.Length; Garage.Save(); ShowGarage(); }, 74);
            foreach (var slot in Garage.Slots)
            {
                var o = Garage.Parts[slot][Garage.Picked[slot]];
                string s = slot;
                Btn(c, $"{slot.ToUpper()}: {o.name}   <color=#5ee08a>+ {o.pro}</color>   <color=#ff8a3d>- {o.con}</color>",
                    () => { Garage.Picked[s] = (Garage.Picked[s] + 1) % Garage.Parts[s].Length; Garage.Save(); ShowGarage(); }, 74)
                    .GetComponentInChildren<Text>().fontSize = 24;
            }
            Btn(c, $"PAINT: {Garage.PaintNames[Garage.paint]}", () => { Garage.paint = (Garage.paint + 1) % Garage.Paints.Length; Garage.Save(); ShowGarage(); }, 74);
            var spec = Garage.Resolve();
            var q = Garage.QuickStats(spec);
            var stats = Label(c, $"{spec.mass:0} kg · {spec.powerW / 1000f:0} kW · 0-60 ≈ {q.x:0.00} s · top {q.y:0} km/h · {q.z:0.00} g @ 250 km/h", 28, TextAnchor.MiddleCenter, Vector2.zero, Vector2.zero, Vector2.zero, new Vector2(800, 60));
            stats.gameObject.AddComponent<LayoutElement>().minHeight = 60;
            stats.color = new Color(0.4f, 1f, 0.95f);
            Btn(c, "BACK", ShowMain, 74);
        }

        void ShowLeaderboard()
        {
            var c = Menu("LEADERBOARD", "Local records on this device");
            string t = "";
            foreach (var m in new[] { "Lap", "Race", "Drag" })
            {
                t += $"<b><color=#36d3c4>{(m == "Lap" ? "BEST LAPS" : m == "Race" ? "RACE TIMES" : "QUARTER MILE")}</color></b>\n";
                var top = Leaderboard.Top(m, 5);
                if (top.Count == 0) t += "  no times yet\n";
                for (int i = 0; i < top.Count; i++) t += $"  {i + 1}. {Fmt(top[i].time)}  {top[i].who}  {top[i].car}  {top[i].date}\n";
                t += "\n";
            }
            var l = Label(c, t, 26, TextAnchor.UpperLeft, Vector2.zero, Vector2.zero, Vector2.zero, new Vector2(800, 600));
            l.gameObject.AddComponent<LayoutElement>().minHeight = 600;
            Btn(c, "BACK", ShowMain);
        }

        void ShowSettings()
        {
            var c = Menu("SETTINGS");
            Btn(c, $"LAPS: {laps}", () => { laps = laps == 1 ? 3 : laps == 3 ? 5 : 1; SaveSettings(); ShowSettings(); }, 72);
            Btn(c, $"AI OPPONENTS: {aiCount}", () => { aiCount = (aiCount + 1) % 6; SaveSettings(); ShowSettings(); }, 72);
            Btn(c, $"AI DIFFICULTY: {DiffNames[difficulty]}", () => { difficulty = (difficulty + 1) % 3; SaveSettings(); ShowSettings(); }, 72);
            Btn(c, $"TOUCH CONTROLS: {TouchNames[touchMode]}", () => { touchMode = (touchMode + 1) % 3; SaveSettings(); ShowSettings(); }, 72);
            Btn(c, $"TILT STEERING (phones): {(tilt ? "On" : "Off")}", () => { tilt = !tilt; SaveSettings(); ShowSettings(); }, 72);
            Btn(c, $"UNITS: {(units == 0 ? "km/h" : "mph")}", () => { units = 1 - units; SaveSettings(); ShowSettings(); }, 72);
            Btn(c, $"GRAPHICS: {(quality == 0 ? "Fast (mobile)" : "High")}", () => { quality = 1 - quality; SaveSettings(); ApplyQuality(); ShowSettings(); }, 72);
            Btn(c, $"SOUND: {(sound ? "On" : "Off")}", () => { sound = !sound; SaveSettings(); ApplyQuality(); ShowSettings(); }, 72);
            Btn(c, "BACK", ShowMain, 72);
        }

        void ShowResults(string title, string body)
        {
            running = false;
            var c = Menu(title);
            var l = Label(c, body, 30, TextAnchor.UpperLeft, Vector2.zero, Vector2.zero, Vector2.zero, new Vector2(800, 420));
            l.gameObject.AddComponent<LayoutElement>().minHeight = 420;
            var again = mode;
            Btn(c, "RACE AGAIN", () => StartMode(again));
            Btn(c, "MAIN MENU", () => { ShowMain(); StartAttract(); });
        }

        void SetPaused(bool p)
        {
            paused = p;
            Time.timeScale = p ? 0f : 1f;
            AudioListener.pause = p;
            if (!menuRoot) return;
            if (p)
            {
                var c = Menu("PAUSED");
                Btn(c, "RESUME", () => { SetPaused(false); HideMenus(); });
                var m = mode;
                Btn(c, "RESTART", () => StartMode(m));
                Btn(c, "SETTINGS (applies on restart)", ShowSettings);
                Btn(c, "MAIN MENU", () => { ShowMain(); StartAttract(); });
            }
        }
    }
}
