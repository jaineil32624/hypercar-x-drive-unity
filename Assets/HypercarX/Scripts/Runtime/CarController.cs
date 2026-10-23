using UnityEngine;

namespace HypercarX
{
    /// <summary>
    /// HYPERCAR-X vehicle physics on Unity WheelColliders.
    /// Tractive force = min(torque-limited, power-limited) exactly like the Python engineering
    /// model; grip comes from the tyre friction curves (stiffness = μ), aero drag and downforce
    /// are applied as forces, and the motor rpm limit caps top speed.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class CarController : MonoBehaviour
    {
        const float G = 9.81f, RHO = 1.225f, ETA = 0.92f;

        public ResolvedSpec spec;
        [HideInInspector] public float throttle, brake, steer, handbrake;
        [HideInInspector] public bool locked;   // held on the grid during the countdown
        public bool audioEnabled = true;

        public WheelCollider[] wheels = new WheelCollider[4];   // FL, FR, RL, RR
        public Transform[] visuals = new Transform[4];
        Quaternion[] visualOffset = new Quaternion[4];
        Transform[] steerVisuals = new Transform[2];

        public Renderer[] brakeLights;
        public Transform wing;
        Quaternion wingRest;
        Vector3 wingAxis = Vector3.right;

        Rigidbody rb;
        ParticleSystem[] smoke = new ParticleSystem[2];
        TrailRenderer[] skids = new TrailRenderer[4];
        AudioSource engine;

        // telemetry
        public float SpeedKmh { get; private set; }
        public float ForwardSpeed { get; private set; }
        public float MotorRpm { get; private set; }
        public Vector3 GForce { get; private set; }
        public bool Slipping { get; private set; }
        public float DownforceN { get; private set; }
        public float DragN { get; private set; }
        public bool Grounded { get; private set; }
        Vector3 lastVel;
        float steerSmoothed;

        public void Setup(ResolvedSpec s, Transform[] wheelVisuals, Transform[] steerParents)
        {
            rb = GetComponent<Rigidbody>();
            visuals = wheelVisuals;
            steerVisuals = steerParents;
            var wheelRoot = new GameObject("WheelColliders").transform;
            wheelRoot.SetParent(transform, false);
            for (int i = 0; i < 4; i++)
            {
                var go = new GameObject("WC_" + i);
                go.transform.SetParent(wheelRoot, false);
                var wc = go.AddComponent<WheelCollider>();
                wheels[i] = wc;
                Vector3 local = transform.InverseTransformPoint(visuals[i].position);
                wc.suspensionDistance = 0.14f;
                go.transform.localPosition = local + Vector3.up * wc.suspensionDistance * 0.5f;
                visualOffset[i] = Quaternion.Inverse(transform.rotation) * visuals[i].rotation;

                // skid trail at the contact patch
                var sk = new GameObject("Skid").AddComponent<TrailRenderer>();
                sk.transform.SetParent(transform, false);
                sk.time = 8f; sk.minVertexDistance = 0.3f; sk.widthMultiplier = 0.28f;
                sk.alignment = LineAlignment.TransformZ;
                sk.material = Resources.Load<Material>("Materials/Skid");
                sk.emitting = false;
                sk.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                skids[i] = sk;
            }
            for (int i = 0; i < 2; i++) smoke[i] = MakeSmoke(wheels[2 + i].transform);
            engine = gameObject.AddComponent<AudioSource>();
            engine.clip = EngineClip();
            engine.loop = true; engine.volume = 0.35f; engine.spatialBlend = 0f; engine.playOnAwake = false;
            engine.Play();
            if (wing) { wingRest = wing.localRotation; wingAxis = wing.InverseTransformDirection(transform.right).normalized; }
            Apply(s);
        }

        public void Apply(ResolvedSpec s)
        {
            spec = s;
            rb.mass = s.mass;
            rb.centerOfMass = new Vector3(0f, 0.32f, 0.05f);
            rb.linearDamping = 0f;
            rb.angularDamping = 0.6f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.maxAngularVelocity = 12f;
            float mCorner = s.mass / 4f;
            for (int i = 0; i < 4; i++)
            {
                var wc = wheels[i];
                wc.mass = 22f;
                wc.radius = s.radius;
                wc.wheelDampingRate = 0.25f;
                wc.forceAppPointDistance = 0.12f;
                var sp = wc.suspensionSpring;
                sp.spring = mCorner * Mathf.Pow(2f * Mathf.PI * 2.2f, 2f);
                sp.damper = 2f * 0.38f * Mathf.Sqrt(sp.spring * mCorner);
                sp.targetPosition = 0.5f;
                wc.suspensionSpring = sp;
                var f = wc.forwardFriction;
                f.extremumSlip = 0.35f; f.extremumValue = 1f; f.asymptoteSlip = 0.9f; f.asymptoteValue = 0.75f; f.stiffness = s.mu;
                wc.forwardFriction = f;
                var sd = wc.sidewaysFriction;
                sd.extremumSlip = 0.22f; sd.extremumValue = 1f; sd.asymptoteSlip = 0.5f; sd.asymptoteValue = 0.78f; sd.stiffness = s.mu;
                wc.sidewaysFriction = sd;
            }
            if (wing) wing.gameObject.SetActive(s.cl > 0.05f);
            wheels[0].ConfigureVehicleSubsteps(5f, 12, 15);   // accurate tyre forces at launch
        }

        public string Diag()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 4; i++)
                if (wheels[i].GetGroundHit(out WheelHit h))
                    sb.Append($"[w{i} Fz={h.force:0} sx={h.forwardSlip:0.00} sy={h.sidewaysSlip:0.00} T={wheels[i].motorTorque:0} rpm={wheels[i].rpm:0}] ");
            sb.Append($"a={GForce.z:0.00}g v={SpeedKmh:0}");
            return sb.ToString();
        }

        void FixedUpdate()
        {
            if (rb == null) return;
            Vector3 vel = rb.linearVelocity;
            float v = Vector3.Dot(vel, transform.forward);
            ForwardSpeed = v;
            float speed = vel.magnitude;
            SpeedKmh = speed * 3.6f;

            // ---- tractive force (Python model: min(T·G·η/r, P·η/v)), split across 4 driven wheels
            float r = spec.radius;
            float fTorque = spec.torque * spec.gear * ETA / r;
            float fPower = spec.powerW * ETA / Mathf.Max(Mathf.Abs(v), 1f);
            float vMotorMax = spec.rpmMax * 2f * Mathf.PI / 60f / spec.gear * r;
            float force = Mathf.Min(fTorque, fPower);
            if (Mathf.Abs(v) >= vMotorMax) force = 0f;
            MotorRpm = Mathf.Abs(v) / r * spec.gear * 60f / (2f * Mathf.PI);

            if (locked) { throttle = 0f; brake = 1f; }
            float drive = throttle;
            bool reversing = false;
            if (brake > 0.1f && v < 1.0f && throttle < 0.1f) { drive = -0.35f * brake; reversing = true; }
            float wheelTorque = drive * force * r / 4f;

            // ---- brakes with simple ABS
            float brakeTorque = (reversing ? 0f : brake) * spec.mu * spec.mass * G * r / 4f * 1.15f * spec.brakeScale;
            if (Mathf.Abs(v) > vMotorMax * 1.01f) brakeTorque = Mathf.Max(brakeTorque, 400f); // motor regen holds the rpm limit

            Slipping = false;
            Grounded = false;
            for (int i = 0; i < 4; i++)
            {
                var wc = wheels[i];
                wc.motorTorque = 0f;
                float bt = brakeTorque;
                if (wc.GetGroundHit(out WheelHit hit))
                {
                    Grounded = true;
                    // traction control: never ask a tyre for more torque than μ·Fz·r (Python model's grip limit)
                    float maxT = wc.forwardFriction.stiffness * Mathf.Max(0f, hit.force) * wc.radius;
                    float tq = Mathf.Clamp(wheelTorque, -maxT, maxT);
                    float over = Mathf.Abs(hit.forwardSlip) - wc.forwardFriction.extremumSlip;
                    if (over > 0f) tq *= Mathf.Clamp(1f - over * 1.5f, 0.35f, 1f);   // ease off only past peak grip
                    wc.motorTorque = tq;
                    if (brake > 0.1f && Mathf.Abs(hit.forwardSlip) > 0.45f) bt *= 0.45f; // ABS
                    bool slip = Mathf.Abs(hit.sidewaysSlip) > 0.35f || Mathf.Abs(hit.forwardSlip) > 0.6f;
                    if (slip && speed > 3f) Slipping = true;
                    skids[i].transform.position = hit.point + hit.normal * 0.02f;
                    skids[i].transform.rotation = Quaternion.LookRotation(hit.normal, transform.forward);
                    skids[i].emitting = slip && speed > 4f;
                    if (i >= 2)
                    {
                        var em = smoke[i - 2].emission;
                        em.rateOverTime = (slip && speed > 3f) ? 60f : 0f;
                    }
                }
                else
                {
                    skids[i].emitting = false;
                    if (i >= 2) { var em = smoke[i - 2].emission; em.rateOverTime = 0f; }
                }
                if (i >= 2 && handbrake > 0.5f) bt = Mathf.Max(bt, 6000f);
                wc.brakeTorque = bt;
            }
            // handbrake drift: soften rear lateral grip
            for (int i = 2; i < 4; i++)
            {
                var sd = wheels[i].sidewaysFriction;
                sd.stiffness = spec.mu * (handbrake > 0.5f ? 0.45f : 1f);
                wheels[i].sidewaysFriction = sd;
            }

            // ---- steering (speed-sensitive, Ackermann)
            float maxSteer = 34f / (1f + speed * speed / 650f);
            steerSmoothed = Mathf.MoveTowards(steerSmoothed, steer, Time.fixedDeltaTime * 3.2f);
            float angle = steerSmoothed * maxSteer;
            float inner = angle, outer = angle * 0.82f;
            wheels[0].steerAngle = angle > 0 ? outer : inner;
            wheels[1].steerAngle = angle > 0 ? inner : outer;

            // ---- aerodynamics: drag, downforce, rolling resistance
            float q = 0.5f * RHO * speed * speed;
            DragN = q * spec.cd * spec.area;
            DownforceN = q * spec.cl * spec.area;
            if (speed > 0.3f) rb.AddForce(-vel.normalized * (DragN + (Grounded ? spec.crr * spec.mass * G : 0f)));
            if (Grounded) rb.AddForce(-transform.up * DownforceN);

            // ---- anti-roll bars
            AntiRoll(0, 1, 18000f);
            AntiRoll(2, 3, 14000f);

            // g-forces
            Vector3 acc = (vel - lastVel) / Time.fixedDeltaTime;
            lastVel = vel;
            GForce = transform.InverseTransformDirection(acc) / G;
        }

        void AntiRoll(int a, int b, float k)
        {
            float travelA = 1f, travelB = 1f;
            bool gA = wheels[a].GetGroundHit(out WheelHit ha), gB = wheels[b].GetGroundHit(out WheelHit hb);
            if (gA) travelA = (-wheels[a].transform.InverseTransformPoint(ha.point).y - wheels[a].radius) / wheels[a].suspensionDistance;
            if (gB) travelB = (-wheels[b].transform.InverseTransformPoint(hb.point).y - wheels[b].radius) / wheels[b].suspensionDistance;
            float f = (travelA - travelB) * k;
            if (gA) rb.AddForceAtPosition(wheels[a].transform.up * -f, wheels[a].transform.position);
            if (gB) rb.AddForceAtPosition(wheels[b].transform.up * f, wheels[b].transform.position);
        }

        void Update()
        {
            if (rb == null) return;
            for (int i = 0; i < 4; i++)
            {
                wheels[i].GetWorldPose(out Vector3 pos, out Quaternion rot);
                visuals[i].position = pos;
                // collider pose is relative to the car; keep the mesh's own axle orientation
                visuals[i].rotation = rot * visualOffset[i];
            }
            // brake lights
            if (brakeLights != null)
                foreach (var bl in brakeLights)
                    if (bl) bl.material.SetColor("_EmissionColor", new Color(1f, 0.05f, 0.1f) * (brake > 0.1f ? 9f : 2.2f));
            // active wing: DRS flat at speed, air-brake under braking
            if (wing && wing.gameObject.activeSelf)
            {
                float target = (brake > 0.3f && SpeedKmh > 60f) ? 38f : (SpeedKmh > 160f && Mathf.Abs(steer) < 0.1f ? 3f : 14f);
                wing.localRotation = Quaternion.Slerp(wing.localRotation, wingRest * Quaternion.AngleAxis(-(target - 14f), wingAxis), Time.deltaTime * 6f);
            }
            // EV motor whine: pitch follows motor rpm
            if (engine)
            {
                engine.mute = !audioEnabled || AudioListener.pause;
                float t = Mathf.Clamp01(MotorRpm / spec.rpmMax);
                engine.pitch = Mathf.Lerp(0.55f, 2.6f, t);
                engine.volume = Mathf.Lerp(engine.volume, 0.12f + 0.35f * Mathf.Max(throttle, t * 0.6f), Time.deltaTime * 4f);
            }
        }

        ParticleSystem MakeSmoke(Transform parent)
        {
            var go = new GameObject("TyreSmoke");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.down * 0.3f;
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.startLifetime = 1.8f; main.startSpeed = 1.2f; main.startSize = new ParticleSystem.MinMaxCurve(0.8f, 1.8f);
            main.startColor = new Color(0.9f, 0.9f, 0.92f, 0.35f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 300;
            main.gravityModifier = -0.05f;
            var em = ps.emission; em.rateOverTime = 0f;
            var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Sphere; sh.radius = 0.2f;
            var sz = ps.sizeOverLifetime; sz.enabled = true; sz.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0, 0.6f, 1, 2.2f));
            var col = ps.colorOverLifetime; col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                         new[] { new GradientAlphaKey(0.45f, 0), new GradientAlphaKey(0f, 1) });
            col.color = grad;
            var pr = go.GetComponent<ParticleSystemRenderer>();
            pr.material = Resources.Load<Material>("Materials/Smoke");
            ps.Play();
            return ps;
        }

        /// <summary>Procedural electric-motor whine (fundamental + harmonics + light noise), looped.</summary>
        static AudioClip EngineClip()
        {
            int rate = 44100, n = rate; // 1 s loop
            var data = new float[n];
            var rnd = new System.Random(7);
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / rate;
                float f = 220f;
                float s = 0.5f * Mathf.Sin(2 * Mathf.PI * f * t) + 0.25f * Mathf.Sin(2 * Mathf.PI * f * 2 * t)
                        + 0.12f * Mathf.Sin(2 * Mathf.PI * f * 3.01f * t) + 0.08f * Mathf.Sin(2 * Mathf.PI * 55f * t);
                s += ((float)rnd.NextDouble() - 0.5f) * 0.05f;
                data[i] = s * 0.6f;
            }
            var clip = AudioClip.Create("ev_motor", n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        public void ResetTo(Vector3 pos, Quaternion rot)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            transform.SetPositionAndRotation(pos, rot);
            rb.position = pos; rb.rotation = rot;
            foreach (var s in skids) if (s) { s.emitting = false; s.Clear(); }
            lastVel = Vector3.zero;
        }
    }
}
