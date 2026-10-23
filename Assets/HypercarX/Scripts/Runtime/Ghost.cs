using System;
using System.Collections.Generic;
using UnityEngine;

namespace HypercarX
{
    /// <summary>Recorded lap: car pose sampled at 20 Hz. Stored in PlayerPrefs as JSON so you can
    /// race your own best lap ("ghost") and watch your two best laps race each other.</summary>
    [Serializable]
    public class GhostLap
    {
        public float lapTime;
        public string car;
        public string date;
        public List<float> d = new List<float>();   // x,y,z, qx,qy,qz,qw per sample
        public const float Hz = 20f;

        public void Add(Transform t)
        {
            var p = t.position; var q = t.rotation;
            d.Add(p.x); d.Add(p.y); d.Add(p.z); d.Add(q.x); d.Add(q.y); d.Add(q.z); d.Add(q.w);
        }
        public int Count => d.Count / 7;

        public void Sample(float time, out Vector3 pos, out Quaternion rot)
        {
            float f = Mathf.Clamp(time * Hz, 0, Count - 1.001f);
            int i = Mathf.FloorToInt(f); float t = f - i;
            pos = Vector3.Lerp(P(i), P(i + 1), t);
            rot = Quaternion.Slerp(Q(i), Q(i + 1), t);
        }
        Vector3 P(int i) { i = Mathf.Clamp(i, 0, Count - 1) * 7; return new Vector3(d[i], d[i + 1], d[i + 2]); }
        Quaternion Q(int i) { i = Mathf.Clamp(i, 0, Count - 1) * 7; return new Quaternion(d[i + 3], d[i + 4], d[i + 5], d[i + 6]); }
    }

    [Serializable] class GhostStore { public List<GhostLap> laps = new List<GhostLap>(); }

    public static class GhostBank
    {
        const string Key = "hx_ghosts_v1";
        static GhostStore store;

        static GhostStore S
        {
            get
            {
                if (store != null) return store;
                string j = PlayerPrefs.GetString(Key, "");
                try { store = string.IsNullOrEmpty(j) ? new GhostStore() : JsonUtility.FromJson<GhostStore>(j); }
                catch { store = new GhostStore(); }
                if (store == null) store = new GhostStore();
                return store;
            }
        }

        /// <summary>Best laps, fastest first (max 2 kept).</summary>
        public static List<GhostLap> Laps => S.laps;

        /// <summary>Returns true if the lap is a new personal best.</summary>
        public static bool Submit(GhostLap lap)
        {
            bool best = S.laps.Count == 0 || lap.lapTime < S.laps[0].lapTime;
            S.laps.Add(lap);
            S.laps.Sort((a, b) => a.lapTime.CompareTo(b.lapTime));
            while (S.laps.Count > 2) S.laps.RemoveAt(S.laps.Count - 1);
            PlayerPrefs.SetString(Key, JsonUtility.ToJson(S));
            PlayerPrefs.Save();
            return best;
        }
    }

    /// <summary>Plays a GhostLap back on a translucent, physics-free copy of the car.</summary>
    public class GhostPlayer : MonoBehaviour
    {
        public GhostLap lap;
        public float startTime = -1f;
        public bool loop = true;

        void Update()
        {
            if (lap == null || lap.Count < 2) return;
            float t = startTime < 0f ? 0f : Time.time - startTime;
            if (loop && t > lap.lapTime) { startTime = Time.time; t = 0f; }
            lap.Sample(t, out Vector3 p, out Quaternion q);
            transform.SetPositionAndRotation(p, q);
        }
    }

    [Serializable] public class ScoreEntry { public string mode, who, car, date; public float time; }
    [Serializable] class ScoreStore { public List<ScoreEntry> s = new List<ScoreEntry>(); }

    /// <summary>Local top-10 leaderboard per mode (lowest time wins).</summary>
    public static class Leaderboard
    {
        const string Key = "hx_scores_v1";
        static ScoreStore store;
        static ScoreStore S
        {
            get
            {
                if (store != null) return store;
                try { store = JsonUtility.FromJson<ScoreStore>(PlayerPrefs.GetString(Key, "{}")); } catch { }
                if (store == null) store = new ScoreStore();
                return store;
            }
        }

        /// <summary>Adds a score and returns its rank (1 = new record), or 0 if it did not make the top 10.</summary>
        public static int Add(string mode, string who, string car, float time)
        {
            var e = new ScoreEntry { mode = mode, who = who, car = car, time = time, date = DateTime.Now.ToString("yyyy-MM-dd") };
            S.s.Add(e);
            var list = Top(mode, 100);
            int rank = list.IndexOf(e) + 1;
            // trim each mode to 10
            S.s.RemoveAll(x => x.mode == mode && Top(mode, 100).IndexOf(x) >= 10);
            PlayerPrefs.SetString(Key, JsonUtility.ToJson(S));
            PlayerPrefs.Save();
            return rank <= 10 ? rank : 0;
        }

        public static List<ScoreEntry> Top(string mode, int n)
        {
            var l = S.s.FindAll(x => x.mode == mode);
            l.Sort((a, b) => a.time.CompareTo(b.time));
            if (l.Count > n) l.RemoveRange(n, l.Count - n);
            return l;
        }
    }
}
