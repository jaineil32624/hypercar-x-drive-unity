using UnityEngine;

namespace HypercarX
{
    /// <summary>Per-player camera: chase, far chase, bonnet and cinematic trackside views.</summary>
    public class ChaseCamera : MonoBehaviour
    {
        public Transform target;
        public int mode;                       // 0 chase, 1 far, 2 bonnet, 3 cinematic
        public static readonly string[] Names = { "Chase", "Far chase", "Bonnet", "Cinematic" };
        Camera cam;
        Vector3 vel;
        Vector3 cinePoint;
        float cineTimer;

        void Awake() { cam = GetComponent<Camera>(); }

        public void Next() { mode = (mode + 1) % 4; cineTimer = 0f; }

        void LateUpdate()
        {
            if (!target) return;
            var rb = target.GetComponent<Rigidbody>();
            float speed = rb ? rb.linearVelocity.magnitude : 0f;
            Vector3 fwd = Vector3.ProjectOnPlane(target.forward, Vector3.up).normalized;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            switch (mode)
            {
                case 0:
                case 1:
                {
                    float back = mode == 0 ? 6.8f : 11f, up = mode == 0 ? 2.1f : 3.6f;
                    Vector3 want = target.position - fwd * back + Vector3.up * up;
                    transform.position = Vector3.SmoothDamp(transform.position, want, ref vel, 0.06f);
                    transform.rotation = Quaternion.Slerp(transform.rotation,
                        Quaternion.LookRotation(target.position + Vector3.up * 0.9f + fwd * 3f - transform.position), Time.deltaTime * 10f);
                    break;
                }
                case 2:
                    transform.position = target.position + target.up * 1.22f + target.forward * 0.9f;
                    transform.rotation = Quaternion.LookRotation(target.forward, target.up);
                    break;
                default:
                    cineTimer -= Time.deltaTime;
                    if (cineTimer <= 0f || (cinePoint - target.position).magnitude > 70f)
                    {
                        Vector3 side = Vector3.Cross(Vector3.up, fwd);
                        cinePoint = target.position + fwd * (25f + speed * 0.8f) + side * (Random.value > 0.5f ? 9f : -9f) + Vector3.up * (1f + Random.value * 4f);
                        cineTimer = 4f;
                    }
                    transform.position = cinePoint;
                    transform.rotation = Quaternion.LookRotation(target.position + Vector3.up * 0.6f - transform.position);
                    break;
            }
            if (cam) cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, (mode == 3 ? 40f : 62f) + Mathf.Min(22f, speed * 0.22f), Time.deltaTime * 3f);
        }
    }
}
