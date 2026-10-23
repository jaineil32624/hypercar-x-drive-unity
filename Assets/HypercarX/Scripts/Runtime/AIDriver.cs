using UnityEngine;

namespace HypercarX
{
    /// <summary>Racing-line follower used for AI opponents, AI-vs-AI and the player's autopilot.
    /// Steers toward a speed-dependent look-ahead point on the circuit centreline and brakes for
    /// corners using v = sqrt(μ·g·R)·skill, where R is estimated from the upcoming track curvature.</summary>
    public class AIDriver : MonoBehaviour
    {
        public World world;
        public float skill = 0.9f;        // fraction of the theoretical corner speed it dares to carry
        public float laneOffset;          // metres left/right of the centreline (keeps the pack apart)
        public bool dragMode;             // straight-line launch on the drag strip
        CarController car;
        float stuckTimer;
        int lastIndex = -1;

        void Awake() { car = GetComponent<CarController>(); }

        void FixedUpdate()
        {
            if (car == null || world == null || car.locked) return;
            if (dragMode)
            {
                bool past = transform.position.z > 430f;
                car.throttle = past ? 0f : 1f; car.brake = past ? 1f : 0f; car.handbrake = 0f;
                car.steer = SteerTo(new Vector3(laneOffset, 0f, transform.position.z + 60f));
                return;
            }

            var tr = world.track;
            int n = tr.Count;
            int i = lastIndex < 0 ? world.ClosestTrackIndex(transform.position) : LocalClosest(lastIndex);
            lastIndex = i;
            float speed = car.SpeedKmh / 3.6f;

            int look = Mathf.Clamp(2 + Mathf.RoundToInt(speed / 9f), 2, 9);
            Vector3 a = tr[(i + look) % n], b = tr[(i + look + 1) % n];
            Vector3 right = Vector3.Cross(Vector3.up, (b - a).normalized);
            Vector3 target = a + right * laneOffset;
            float steer = SteerTo(target);

            // corner speed from curvature over the next ~120 m
            float vTarget = 95f;
            for (int k = 2; k < 14; k += 2)
            {
                Vector3 p0 = tr[(i + k) % n], p1 = tr[(i + k + 3) % n], p2 = tr[(i + k + 6) % n];
                float ang = Vector3.Angle(p1 - p0, p2 - p1) * Mathf.Deg2Rad;
                float d = Vector3.Distance(p0, p1);
                if (ang < 0.01f) continue;
                float R = d / ang;
                float vCorner = Mathf.Sqrt(car.spec.mu * 9.81f * R * (1f + car.spec.cl * 0.15f)) * skill;
                // allow braking distance: v² = vCorner² + 2·a·dist
                float dist = Vector3.Distance(tr[i], p0);
                float vAllowed = Mathf.Sqrt(vCorner * vCorner + 2f * car.spec.mu * 9.81f * 0.8f * dist);
                vTarget = Mathf.Min(vTarget, vAllowed);
            }
            // off the road: slow down and rejoin a point further ahead
            float off = Vector3.Distance(Vector3.ProjectOnPlane(transform.position - tr[i], Vector3.up), Vector3.zero);
            if (off > World.RoadWidth * 0.5f + 2f)
            {
                vTarget = Mathf.Min(vTarget, 16f);
                steer = SteerTo(tr[(i + 4) % n]);
            }
            float err = vTarget - speed;
            car.throttle = Mathf.Clamp01(err * 0.25f) * (1f - Mathf.Abs(steer) * 0.6f);
            car.brake = err < -2f ? Mathf.Clamp01(-err * 0.12f) : 0f;
            car.handbrake = 0f;
            car.steer = steer;

            // unstick: if barely moving for 3 s, put the car back on the racing line
            if (speed < 1.5f) stuckTimer += Time.fixedDeltaTime; else stuckTimer = 0f;
            if (stuckTimer > 3f || Vector3.Dot(transform.up, Vector3.up) < 0.2f)
            {
                stuckTimer = 0f;
                Vector3 p = tr[i] + Vector3.up * 0.8f;
                car.ResetTo(p, Quaternion.LookRotation(tr[(i + 1) % n] - tr[i]));
            }
        }

        float SteerTo(Vector3 target)
        {
            Vector3 local = transform.InverseTransformPoint(target);
            float ang = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            return Mathf.Clamp(ang / 22f, -1f, 1f);
        }

        int LocalClosest(int from)
        {
            var tr = world.track; int n = tr.Count; int best = from; float bd = float.MaxValue;
            for (int k = -4; k <= 12; k++)
            {
                int j = (from + k + n) % n;
                float d = (tr[j] - transform.position).sqrMagnitude;
                if (d < bd) { bd = d; best = j; }
            }
            if (bd > 900f) best = world.ClosestTrackIndex(transform.position); // off track: global search
            return best;
        }
    }
}
