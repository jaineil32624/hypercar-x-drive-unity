using System;
using System.Collections.Generic;
using UnityEngine;

namespace HypercarX
{
    /// <summary>One variant from the HYPERCAR-X engineering model (exported from reports/results.json).</summary>
    [Serializable]
    public class VariantData
    {
        public string id;
        public string name;
        public float mass;
        public float powerKW;
        public float torque;
        public float gear;
        public float wheelD;
        public float mu;
        public float cd;
        public float cl;
        public float area;
        public float crr;
        public float spring;
        public float travel;
        public float clearance;
        public float battery;
        public float zero60;
        public float topKmh;
        public float rangeKm;
    }

    [Serializable]
    public class VariantList { public VariantData[] variants; }

    /// <summary>A swappable part option: name, pros, cons and how it changes the physics.</summary>
    public class PartOption
    {
        public string name, pro, con;
        public float dMass, dPowerKW, dTorque, dMu, dCd, dCl, dCrr, dBrake;
        public Color tint = Color.clear;
        public PartOption(string n, string p, string c) { name = n; pro = p; con = c; }
    }

    /// <summary>Resolved physics values the CarController uses (variant + parts).</summary>
    public struct ResolvedSpec
    {
        public string title;
        public float mass, powerW, torque, gear, radius, mu, cd, cl, area, crr, brakeScale, travel, rpmMax, wheelD;
    }

    public static class Garage
    {
        public static VariantData[] Variants;
        public static int variant = 2; // C — Electric Hypercar
        public static readonly Dictionary<string, PartOption[]> Parts = new Dictionary<string, PartOption[]>();
        public static readonly Dictionary<string, int> Picked = new Dictionary<string, int>();
        public static readonly string[] Slots = { "Wheels", "Tyres", "Aero", "Motors", "Battery", "Brakes" };

        public static readonly Color[] Paints =
        {
            new Color(0.05f, 0.55f, 0.55f), new Color(0.75f, 0.05f, 0.05f), new Color(0.95f, 0.6f, 0.05f),
            new Color(0.1f, 0.2f, 0.75f), new Color(0.9f, 0.9f, 0.92f), new Color(0.06f, 0.06f, 0.07f), new Color(0.45f, 0.1f, 0.6f)
        };
        public static readonly string[] PaintNames = { "Teal Pearl", "Rosso", "Solar Orange", "Cobalt", "Ice White", "Stealth Black", "Ultraviolet" };
        public static int paint;
        public static int p2Variant = 1, p2Paint = 1;

        static Garage()
        {
            Parts["Wheels"] = new[]
            {
                new PartOption("20\" Forged Al", "Balanced unsprung mass, good ride", "Not the lightest option"),
                new PartOption("21\" Carbon", "-18 kg unsprung, sharper response", "$22k per set, cracks on kerbs") { dMass = -18, dMu = 0.03f },
                new PartOption("18\" Rally", "Tall sidewalls soak up rough roads", "+22 kg, slower steering, less grip") { dMass = 22, dMu = -0.15f },
            };
            Parts["Tyres"] = new[]
            {
                new PartOption("Performance", "All-weather, 40k km life", "Grip fades on track"),
                new PartOption("Semi-slick", "+15% grip, faster laps", "Unsafe in rain, 8k km life") { dMu = 0.2f, dCrr = 0.002f },
                new PartOption("Eco low-RR", "+8-10% range", "Longer braking, less cornering grip") { dMu = -0.2f, dCrr = -0.004f },
            };
            Parts["Aero"] = new[]
            {
                new PartOption("Active wing", "DRS on straights, downforce in turns", "Actuators add complexity"),
                new PartOption("Track pack", "2.5x downforce, 3 g+ corners", "More drag: lower top speed and range") { dCl = 1.2f, dCd = 0.13f, dMass = 18 },
                new PartOption("Slick / no wing", "Lowest drag, best range", "Rear goes light at speed") { dCl = -0.7f, dCd = -0.05f, dMass = -28 },
            };
            Parts["Motors"] = new[]
            {
                new PartOption("Stock quad", "Torque vectoring on every wheel", "Heavy, heat at full power"),
                new PartOption("Overboost +350 kW", "Sub-2 s 0-60 potential", "Traction-limited, +35 kg, thermal derate") { dPowerKW = 350, dTorque = 300, dMass = 35 },
                new PartOption("Dual 600 kW", "-48 kg, cheaper, simpler", "Half the power, no per-wheel vectoring") { dPowerKW = -500, dTorque = -600, dMass = -48 },
            };
            Parts["Battery"] = new[]
            {
                new PartOption("110 kWh NMC", "~750 km range", "~525 kg pack"),
                new PartOption("80 kWh LFP", "-90 kg, long cycle life", "Range drops ~25%") { dMass = -90 },
                new PartOption("130 kWh solid-state", "Lighter and more energy (2035 tech)", "Not production-ready, very expensive") { dMass = -120 },
            };
            Parts["Brakes"] = new[]
            {
                new PartOption("Carbon-ceramic", "Fade-free, -20 kg vs iron", "$15k to replace, weak when cold"),
                new PartOption("Cast iron", "Cheap, strong cold bite", "+20 kg unsprung, fades on track") { dMass = 20, dBrake = -0.15f },
            };
            foreach (var s in Slots) Picked[s] = 0;
        }

        public static void Load()
        {
            if (Variants != null) return;
            var ta = Resources.Load<TextAsset>("variants");
            Variants = JsonUtility.FromJson<VariantList>(ta.text).variants;
            variant = PlayerPrefs.GetInt("hx_variant", 2);
            paint = PlayerPrefs.GetInt("hx_paint", 0);
            foreach (var s in Slots) Picked[s] = Mathf.Clamp(PlayerPrefs.GetInt("hx_part_" + s, 0), 0, Parts[s].Length - 1);
        }

        public static void Save()
        {
            PlayerPrefs.SetInt("hx_variant", variant);
            PlayerPrefs.SetInt("hx_paint", paint);
            foreach (var s in Slots) PlayerPrefs.SetInt("hx_part_" + s, Picked[s]);
            PlayerPrefs.Save();
        }

        public static ResolvedSpec Resolve() => Resolve(variant, true);

        public static ResolvedSpec Resolve(int variantIndex, bool withParts)
        {
            Load();
            var v = Variants[Mathf.Clamp(variantIndex, 0, Variants.Length - 1)];
            float mass = v.mass, p = v.powerKW, t = v.torque, mu = v.mu, cd = v.cd, cl = v.cl, crr = v.crr, brake = 1f;
            if (withParts) foreach (var s in Slots)
            {
                var o = Parts[s][Picked[s]];
                mass += o.dMass; p += o.dPowerKW; t += o.dTorque; mu += o.dMu; cd += o.dCd; cl += o.dCl; crr += o.dCrr; brake += o.dBrake;
            }
            return new ResolvedSpec
            {
                title = v.id + " · " + v.name,
                mass = mass, powerW = p * 1000f, torque = t, gear = v.gear, radius = v.wheelD * 0.5f,
                mu = mu, cd = cd, cl = Mathf.Max(0f, cl), area = v.area, crr = Mathf.Max(0.004f, crr),
                brakeScale = brake, travel = v.travel, rpmMax = 21000f, wheelD = v.wheelD,
            };
        }

        /// <summary>Same closed-form estimate as the web garage: 0-60, top speed, lateral g at 250 km/h.</summary>
        public static Vector3 QuickStats(ResolvedSpec s)
        {
            const float g = 9.81f, rho = 1.225f;
            float trac = s.mu * g, pw = s.powerW * 0.92f / s.mass, v60 = 26.82f;
            float t60 = v60 / trac + Mathf.Max(0, (v60 * v60 / 2f) / pw - v60 * v60 / (2f * trac)) * 0.6f;
            float lo = 1, hi = 200;
            for (int i = 0; i < 60; i++)
            {
                float v = (lo + hi) / 2f;
                if (s.powerW * 0.92f > (0.5f * rho * v * v * s.cd * s.area + s.crr * s.mass * g) * v) lo = v; else hi = v;
            }
            float vmax = Mathf.Min(lo, s.rpmMax * 2f * Mathf.PI / 60f / s.gear * s.radius);
            float q = 0.5f * rho * Mathf.Pow(250f / 3.6f, 2);
            float lat = s.mu * (s.mass * g + q * s.cl * s.area) / (s.mass * g);
            return new Vector3(t60, vmax * 3.6f, lat);
        }
    }
}
