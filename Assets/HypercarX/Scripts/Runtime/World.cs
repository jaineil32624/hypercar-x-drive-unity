using System.Collections.Generic;
using UnityEngine;

namespace HypercarX
{
    /// <summary>Procedurally builds the test ground: a closed circuit with curbs and checkpoints,
    /// a quarter-mile drag strip with a start tree, slalom, jump ramps, trees, grandstand and hills.</summary>
    public class World : MonoBehaviour
    {
        public const float RoadWidth = 14f;
        public List<Vector3> track = new List<Vector3>();      // dense centreline samples
        public List<Transform> checkpoints = new List<Transform>();
        public Vector3 gridPos; public Quaternion gridRot; public int gridIndex0;
        public Vector3 dragPos; public Quaternion dragRot;
        public Vector3 freePos; public Quaternion freeRot;
        public Transform dragFinish;
        public Renderer[] startLights = new Renderer[3];

        static Material M(string n) => Resources.Load<Material>("Materials/" + n);

        public void Build(bool lowDetail)
        {
            RenderSettings.skybox = M("Sky");
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.62f, 0.7f, 0.8f);
            RenderSettings.fogStartDistance = 250f;
            RenderSettings.fogEndDistance = lowDetail ? 900f : 1500f;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.55f, 0.65f, 0.8f);
            RenderSettings.ambientEquatorColor = new Color(0.45f, 0.45f, 0.45f);
            RenderSettings.ambientGroundColor = new Color(0.2f, 0.2f, 0.18f);

            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.95f, 0.86f);
            sun.intensity = 1.25f;
            sun.shadows = LightShadows.Soft;
            sunGo.transform.rotation = Quaternion.Euler(42f, -35f, 0f);
            RenderSettings.sun = sun;

            ApplyTextures();

            // ground
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(260f, 1f, 260f);
            ground.GetComponent<Renderer>().sharedMaterial = M("Grass");
            Object.Destroy(ground.GetComponent<MeshCollider>());
            var gb = ground.AddComponent<BoxCollider>();
            gb.center = new Vector3(0, -0.5f, 0); gb.size = new Vector3(10f, 1f, 10f);

            BuildCircuit();
            BuildDragStrip();
            BuildPlayground();
            Scatter(lowDetail ? 160 : 420);
            Hills();
        }

        // ---------------------------------------------------------------- textures (generated once)
        void ApplyTextures()
        {
            M("Asphalt").mainTexture = Noise(256, new Color(0.13f, 0.135f, 0.145f), new Color(0.2f, 0.2f, 0.21f), 0.9f);
            M("Grass").mainTexture = Noise(256, new Color(0.16f, 0.3f, 0.1f), new Color(0.26f, 0.42f, 0.15f), 0.6f);
            var curb = new Texture2D(2, 1) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
            curb.SetPixels(new[] { new Color(0.85f, 0.08f, 0.08f), Color.white }); curb.Apply();
            M("Curb").mainTexture = curb;
            var chk = new Texture2D(8, 2) { filterMode = FilterMode.Point };
            for (int x = 0; x < 8; x++) for (int y = 0; y < 2; y++) chk.SetPixel(x, y, (x + y) % 2 == 0 ? Color.white : Color.black);
            chk.Apply();
            M("Checker").mainTexture = chk;
        }

        static Texture2D Noise(int n, Color a, Color b, float grain)
        {
            var t = new Texture2D(n, n, TextureFormat.RGB24, true) { wrapMode = TextureWrapMode.Repeat };
            var px = new Color[n * n];
            var rnd = new System.Random(3);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float p = Mathf.PerlinNoise(x * 0.05f, y * 0.05f) * 0.6f + (float)rnd.NextDouble() * 0.4f * grain;
                    px[y * n + x] = Color.Lerp(a, b, p);
                }
            t.SetPixels(px); t.Apply(true);
            return t;
        }

        // ---------------------------------------------------------------- circuit
        void BuildCircuit()
        {
            // control points (metres) of a ~3 km technical circuit
            Vector3[] cp =
            {
                new Vector3(0, 0, -150), new Vector3(0, 0, 150), new Vector3(30, 0, 260), new Vector3(140, 0, 300),
                new Vector3(250, 0, 250), new Vector3(270, 0, 140), new Vector3(200, 0, 60), new Vector3(250, 0, -30),
                new Vector3(330, 0, -90), new Vector3(330, 0, -220), new Vector3(230, 0, -300), new Vector3(110, 0, -280),
                new Vector3(60, 0, -230),
            };
            int n = cp.Length;
            track.Clear();
            for (int i = 0; i < n; i++)
                for (int s = 0; s < 24; s++)
                {
                    float t = s / 24f;
                    track.Add(CatmullRom(cp[(i - 1 + n) % n], cp[i], cp[(i + 1) % n], cp[(i + 2) % n], t));
                }
            var road = Strip("Circuit", track, RoadWidth, 0.03f, M("Asphalt"), true, 0.08f);
            road.AddComponent<MeshCollider>();
            // curbs both sides
            Strip("CurbL", Offset(track, RoadWidth / 2f + 0.7f), 1.4f, 0.05f, M("Curb"), true, 0.5f);
            Strip("CurbR", Offset(track, -RoadWidth / 2f - 0.7f), 1.4f, 0.05f, M("Curb"), true, 0.5f);
            // centre dashes
            var dash = new List<Vector3>(track);
            Strip("CentreLine", dash, 0.25f, 0.045f, M("Line"), true, 0.15f);

            // start/finish line + grid
            Vector3 p0 = track[0], dir = (track[1] - track[0]).normalized;
            var sf = GameObject.CreatePrimitive(PrimitiveType.Cube);
            sf.name = "StartFinish"; Object.Destroy(sf.GetComponent<Collider>());
            sf.transform.position = p0 + Vector3.up * 0.05f; sf.transform.rotation = Quaternion.LookRotation(dir);
            sf.transform.localScale = new Vector3(RoadWidth, 0.02f, 2.5f);
            sf.GetComponent<Renderer>().sharedMaterial = M("Checker");
            Gantry(p0, dir, "HYPERCAR-X  CIRCUIT");
            gridPos = p0 - dir * 12f + Vector3.up * 0.6f;
            gridRot = Quaternion.LookRotation(dir);
            gridIndex0 = track.Count - 2;
            freePos = gridPos; freeRot = gridRot;

            // checkpoints every ~1/12 of the lap; index 0 is the finish line
            checkpoints.Clear();
            int count = 12;
            for (int i = 0; i < count; i++)
            {
                int k = i * track.Count / count;
                Vector3 a = track[k], b = track[(k + 1) % track.Count];
                var go = new GameObject("Checkpoint_" + i);
                go.transform.position = a + Vector3.up * 2f;
                go.transform.rotation = Quaternion.LookRotation((b - a).normalized);
                var bc = go.AddComponent<BoxCollider>();
                bc.isTrigger = true; bc.size = new Vector3(RoadWidth + 40f, 8f, 4f);
                go.AddComponent<Checkpoint>().index = i;
                checkpoints.Add(go.transform);
            }
            // grandstand by the main straight
            for (int row = 0; row < 6; row++)
            {
                var st = GameObject.CreatePrimitive(PrimitiveType.Cube);
                st.name = "Grandstand";
                st.transform.position = new Vector3(-26f - row * 1.6f, 0.6f + row * 0.9f, 0f);
                st.transform.localScale = new Vector3(1.6f, 1.2f + row * 1.8f * 0f, 120f);
                st.GetComponent<Renderer>().sharedMaterial = M(row % 2 == 0 ? "Concrete" : "Seats");
            }
        }

        static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        static List<Vector3> Offset(List<Vector3> pts, float d)
        {
            var o = new List<Vector3>(pts.Count);
            for (int i = 0; i < pts.Count; i++)
            {
                Vector3 f = (pts[(i + 1) % pts.Count] - pts[(i - 1 + pts.Count) % pts.Count]).normalized;
                Vector3 right = Vector3.Cross(Vector3.up, f);
                o.Add(pts[i] + right * d);
            }
            return o;
        }

        /// <summary>Builds a flat ribbon mesh along a polyline.</summary>
        GameObject Strip(string name, List<Vector3> pts, float width, float y, Material mat, bool closed, float vScale)
        {
            int n = pts.Count, segs = closed ? n + 1 : n;
            var v = new Vector3[segs * 2]; var uv = new Vector2[segs * 2]; var tri = new int[(segs - 1) * 6];
            float dist = 0f;
            for (int i = 0; i < segs; i++)
            {
                Vector3 p = pts[i % n];
                Vector3 f = closed ? (pts[(i + 1) % n] - pts[(i - 1 + n) % n]).normalized
                                   : (pts[Mathf.Min(i + 1, n - 1)] - pts[Mathf.Max(i - 1, 0)]).normalized;
                Vector3 r = Vector3.Cross(Vector3.up, f) * width * 0.5f;
                if (i > 0) dist += Vector3.Distance(pts[(i - 1) % n], p);
                v[i * 2] = p - r + Vector3.up * y; v[i * 2 + 1] = p + r + Vector3.up * y;
                uv[i * 2] = new Vector2(0, dist * vScale); uv[i * 2 + 1] = new Vector2(width / 8f, dist * vScale);
                if (i < segs - 1)
                {
                    int k = i * 6, a = i * 2;
                    tri[k] = a; tri[k + 1] = a + 2; tri[k + 2] = a + 1; tri[k + 3] = a + 1; tri[k + 4] = a + 2; tri[k + 5] = a + 3;
                }
            }
            var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = v; mesh.uv = uv; mesh.triangles = tri; mesh.RecalculateNormals(); mesh.RecalculateBounds();
            var go = new GameObject(name);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = mat;
            return go;
        }

        void Gantry(Vector3 p, Vector3 dir, string label)
        {
            Vector3 right = Vector3.Cross(Vector3.up, dir);
            for (int s = -1; s <= 1; s += 2)
            {
                var post = GameObject.CreatePrimitive(PrimitiveType.Cube);
                post.transform.position = p + right * s * (RoadWidth / 2f + 1.5f) + Vector3.up * 3.5f;
                post.transform.localScale = new Vector3(0.6f, 7f, 0.6f);
                post.GetComponent<Renderer>().sharedMaterial = M("Carbon");
            }
            var beam = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.Destroy(beam.GetComponent<Collider>());
            beam.transform.position = p + Vector3.up * 7f;
            beam.transform.rotation = Quaternion.LookRotation(dir);
            beam.transform.localScale = new Vector3(RoadWidth + 4f, 1.2f, 0.6f);
            beam.GetComponent<Renderer>().sharedMaterial = M("Gate");
        }

        // ---------------------------------------------------------------- drag strip
        void BuildDragStrip()
        {
            var pts = new List<Vector3>();
            for (int z = -40; z <= 760; z += 10) pts.Add(new Vector3(-160f, 0, z));
            var strip = Strip("DragStrip", pts, 18f, 0.03f, M("Asphalt"), false, 0.08f);
            strip.AddComponent<MeshCollider>();
            var lane = new List<Vector3>(); foreach (var p in pts) lane.Add(p);
            Strip("DragLane", lane, 0.3f, 0.045f, M("Line"), false, 0.1f);
            dragPos = new Vector3(-163.5f, 0.6f, -2f); dragRot = Quaternion.identity;
            // start line + finish at 402.3 m
            foreach (float z in new[] { 0f, 402.336f })
            {
                var l = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.Destroy(l.GetComponent<Collider>());
                l.transform.position = new Vector3(-160f, 0.05f, z); l.transform.localScale = new Vector3(18f, 0.02f, 1.5f);
                l.GetComponent<Renderer>().sharedMaterial = M("Checker");
            }
            Gantry(new Vector3(-160f, 0, 402.336f), Vector3.forward, "1/4 MILE");
            var fin = new GameObject("DragFinish");
            fin.transform.position = new Vector3(-160f, 2f, 402.336f);
            dragFinish = fin.transform;
            // christmas tree start lights
            var tree = GameObject.CreatePrimitive(PrimitiveType.Cube);
            tree.transform.position = new Vector3(-160f, 1.6f, 4f); tree.transform.localScale = new Vector3(0.6f, 3.2f, 0.4f);
            tree.GetComponent<Renderer>().sharedMaterial = M("Carbon");
            for (int i = 0; i < 3; i++)
            {
                var b = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Object.Destroy(b.GetComponent<Collider>());
                b.transform.position = new Vector3(-160f, 2.6f - i * 0.8f, 3.75f);
                b.transform.localScale = Vector3.one * 0.45f;
                var r = b.GetComponent<Renderer>(); r.material = new Material(M("Lamp"));
                startLights[i] = r;
            }
            SetLights(-1);
        }

        public void SetLights(int stage)
        {
            // stage: -1 off, 0..2 amber count, 3 green
            for (int i = 0; i < 3; i++)
            {
                Color c = Color.black;
                if (stage == 3) c = new Color(0.1f, 1f, 0.2f) * 6f;
                else if (stage >= i) c = new Color(1f, 0.6f, 0.05f) * 6f;
                startLights[i].material.SetColor("_EmissionColor", c);
                startLights[i].material.color = stage == 3 ? Color.green : (stage >= i ? new Color(1f, 0.6f, 0f) : Color.gray);
            }
        }

        // ---------------------------------------------------------------- playground: slalom, ramps
        void BuildPlayground()
        {
            var pad = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pad.name = "SkidPad";
            pad.transform.position = new Vector3(-60f, -0.45f, -260f); pad.transform.localScale = new Vector3(120f, 1f, 120f);
            pad.GetComponent<Renderer>().sharedMaterial = M("Asphalt");
            for (int i = 0; i < 10; i++) Cone(new Vector3(-60f + (i % 2 == 0 ? -4f : 4f), 0.35f, -310f + i * 11f));
            for (int i = 0; i < 40; i++)
            {
                float a = i / 40f * Mathf.PI * 2f;
                Cone(new Vector3(-60f + Mathf.Cos(a) * 30f, 0.35f, -260f + Mathf.Sin(a) * 30f));
            }
            // jump ramps
            for (int i = 0; i < 2; i++)
            {
                var ramp = GameObject.CreatePrimitive(PrimitiveType.Cube);
                ramp.name = "Ramp";
                ramp.transform.position = new Vector3(-100f + i * 80f, 1.1f, -200f);
                ramp.transform.rotation = Quaternion.Euler(-12f, 0, 0);
                ramp.transform.localScale = new Vector3(10f, 0.6f, 14f);
                ramp.GetComponent<Renderer>().sharedMaterial = M("Concrete");
            }
        }

        void Cone(Vector3 p)
        {
            var c = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            c.name = "Cone";
            c.transform.position = p; c.transform.localScale = new Vector3(0.45f, 0.35f, 0.45f);
            c.GetComponent<Renderer>().sharedMaterial = M("Cone");
            Object.Destroy(c.GetComponent<CapsuleCollider>());
            c.AddComponent<BoxCollider>();
            var rb = c.AddComponent<Rigidbody>(); rb.mass = 3f;
        }

        // ---------------------------------------------------------------- scenery
        void Scatter(int count)
        {
            var rnd = new System.Random(11);
            Mesh trunk = Prim(PrimitiveType.Cylinder), crown = Prim(PrimitiveType.Sphere);
            int placed = 0, tries = 0;
            while (placed < count && tries < count * 20)
            {
                tries++;
                var p = new Vector3((float)rnd.NextDouble() * 1000f - 500f, 0, (float)rnd.NextDouble() * 1000f - 500f);
                if (NearRoad(p, 18f) || (p.x > -185f && p.x < -135f && p.z > -60f && p.z < 780f) || (Mathf.Abs(p.x + 60f) < 75f && Mathf.Abs(p.z + 255f) < 75f)) continue;
                float h = 5f + (float)rnd.NextDouble() * 6f;
                var t = new GameObject("Tree");
                t.transform.position = p;
                var tr = new GameObject("Trunk"); tr.transform.SetParent(t.transform, false);
                tr.transform.localPosition = Vector3.up * h * 0.25f; tr.transform.localScale = new Vector3(0.5f, h * 0.25f, 0.5f);
                tr.AddComponent<MeshFilter>().sharedMesh = trunk; tr.AddComponent<MeshRenderer>().sharedMaterial = M("Bark");
                var cr = new GameObject("Crown"); cr.transform.SetParent(t.transform, false);
                cr.transform.localPosition = Vector3.up * h * 0.7f; cr.transform.localScale = new Vector3(h * 0.55f, h * 0.7f, h * 0.55f);
                cr.AddComponent<MeshFilter>().sharedMesh = crown; cr.AddComponent<MeshRenderer>().sharedMaterial = M("Leaf");
                var col = t.AddComponent<CapsuleCollider>(); col.radius = 0.4f; col.height = h; col.center = Vector3.up * h * 0.5f;
                placed++;
            }
        }

        static Mesh Prim(PrimitiveType t)
        {
            var go = GameObject.CreatePrimitive(t);
            var m = go.GetComponent<MeshFilter>().sharedMesh;
            Object.Destroy(go);
            return m;
        }

        bool NearRoad(Vector3 p, float d)
        {
            float d2 = (RoadWidth * 0.5f + d) * (RoadWidth * 0.5f + d);
            for (int i = 0; i < track.Count; i += 2) if ((track[i] - p).sqrMagnitude < d2) return true;
            return false;
        }

        void Hills()
        {
            var rnd = new System.Random(5);
            for (int i = 0; i < 26; i++)
            {
                float a = i / 26f * Mathf.PI * 2f, r = 900f + (float)rnd.NextDouble() * 250f;
                var h = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Object.Destroy(h.GetComponent<Collider>());
                float s = 220f + (float)rnd.NextDouble() * 260f;
                h.transform.position = new Vector3(Mathf.Cos(a) * r, -s * 0.25f, Mathf.Sin(a) * r);
                h.transform.localScale = new Vector3(s, s * (0.35f + (float)rnd.NextDouble() * 0.3f), s);
                h.GetComponent<Renderer>().sharedMaterial = M("Hill");
                h.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
        }

        /// <summary>Starting-grid slot laid out along the real track behind the start line (two columns).</summary>
        public void GridSlot(int slot, out Vector3 pos, out Quaternion rot)
        {
            int n = track.Count;
            int i = ((gridIndex0 - slot) % n + n) % n;          // ~9.6 m per sample, staggered columns
            Vector3 a = track[i], b = track[(i + 1) % n];
            Vector3 f = (b - a).normalized, right = Vector3.Cross(Vector3.up, f);
            pos = a + right * ((slot % 2 == 0) ? -3.4f : 3.4f) + Vector3.up * 0.6f;
            rot = Quaternion.LookRotation(f);
        }

        /// <summary>Distance along the circuit of the closest centreline sample (for the minimap / reset).</summary>
        public int ClosestTrackIndex(Vector3 p)
        {
            int best = 0; float bd = float.MaxValue;
            for (int i = 0; i < track.Count; i++) { float d = (track[i] - p).sqrMagnitude; if (d < bd) { bd = d; best = i; } }
            return best;
        }
    }
}
