using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace HypercarX.EditorTools
{
    /// <summary>
    /// One-click project setup and builds. Run from the menu (HYPERCAR-X ▸ …) or headless:
    ///   Unity -batchmode -quit -projectPath . -executeMethod HypercarX.EditorTools.ProjectBuilder.BuildAll
    /// </summary>
    public static class ProjectBuilder
    {
        const string MatDir = "Assets/Resources/Materials";
        const string TexDir = "Assets/HypercarX/Textures";
        const string ScenePath = "Assets/Scenes/Drive.unity";

        [MenuItem("HYPERCAR-X/1. Setup project (materials, scene, player settings)")]
        public static void Setup()
        {
            ConfigureImports();
            CreateMaterials();
            CreateScene();
            ConfigurePlayer();
            AssetDatabase.SaveAssets();
            Debug.Log("[HX] Setup complete");
        }

        // ------------------------------------------------------------------ imports
        static void ConfigureImports()
        {
            var fbx = AssetImporter.GetAtPath("Assets/Resources/HypercarX.fbx") as ModelImporter;
            if (fbx != null)
            {
                fbx.globalScale = 1f;
                fbx.useFileScale = true;
                fbx.materialImportMode = ModelImporterMaterialImportMode.None;
                fbx.importAnimation = false;
                fbx.importCameras = false;
                fbx.importLights = false;
                fbx.isReadable = false;
                fbx.meshCompression = ModelImporterMeshCompression.Off;
                fbx.SaveAndReimport();
            }
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { TexDir }))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                var ti = (TextureImporter)AssetImporter.GetAtPath(p);
                bool normal = p.Contains("_normal");
                ti.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                ti.sRGBTexture = !normal && !p.Contains("_roughness");
                ti.maxTextureSize = 1024;
                ti.mipmapEnabled = true;
                ti.SaveAndReimport();
            }
        }

        // ------------------------------------------------------------------ materials
        static Texture2D Tex(string n) => AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/{n}.png");

        static Material Std(string name, Color c, float metal, float smooth, string albedo = null, string normal = null, Color? emission = null)
        {
            Directory.CreateDirectory(MatDir);
            string path = $"{MatDir}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find("Standard")); AssetDatabase.CreateAsset(m, path); }
            m.shader = Shader.Find("Standard");
            m.color = c;
            m.SetFloat("_Metallic", metal);
            m.SetFloat("_Glossiness", smooth);
            if (albedo != null) m.mainTexture = Tex(albedo);
            if (normal != null && Tex(normal) != null)
            {
                m.SetTexture("_BumpMap", Tex(normal)); m.SetFloat("_BumpScale", 0.6f); m.EnableKeyword("_NORMALMAP");
            }
            if (emission.HasValue)
            {
                m.EnableKeyword("_EMISSION");
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                m.SetColor("_EmissionColor", emission.Value);
            }
            m.enableInstancing = true;
            EditorUtility.SetDirty(m);
            return m;
        }

        static void Transparent(Material m, bool fade)
        {
            m.SetFloat("_Mode", fade ? 2 : 3);
            m.SetInt("_SrcBlend", fade ? (int)BlendMode.SrcAlpha : (int)BlendMode.One);
            m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.DisableKeyword("_ALPHATEST_ON");
            if (fade) { m.EnableKeyword("_ALPHABLEND_ON"); m.DisableKeyword("_ALPHAPREMULTIPLY_ON"); }
            else { m.DisableKeyword("_ALPHABLEND_ON"); m.EnableKeyword("_ALPHAPREMULTIPLY_ON"); }
            m.renderQueue = (int)RenderQueue.Transparent;
        }

        static void CreateMaterials()
        {
            // car
            Std("Paint", new Color(0.05f, 0.55f, 0.55f), 0.65f, 0.86f, null, "HX_Paint_normal");
            Std("Carbon", Color.white, 0.3f, 0.8f, "HX_Carbon_diffuse", "HX_Carbon_normal");
            Std("Tire", Color.white, 0f, 0.12f, "HX_Tire_diffuse", "HX_Tire_normal");
            Std("Rim", new Color(0.66f, 0.68f, 0.72f), 1f, 0.78f, null, "HX_Rim_normal");
            Std("Disc", Color.white, 0.6f, 0.45f, "HX_Disc_diffuse", "HX_Disc_normal");
            Std("Caliper", new Color(0.75f, 0.04f, 0.03f), 0f, 0.8f);
            Std("Trim", new Color(0.02f, 0.022f, 0.026f), 0.3f, 0.55f);
            Std("HeadLight", new Color(0.9f, 0.97f, 1f), 0f, 0.9f, emission: new Color(0.8f, 0.95f, 1f) * 3f);
            Std("TailLight", new Color(1f, 0.05f, 0.1f), 0f, 0.9f, emission: new Color(1f, 0.05f, 0.1f) * 2.2f);
            var glass = Std("Glass", new Color(0.02f, 0.05f, 0.08f, 0.6f), 0.4f, 0.97f); Transparent(glass, false);
            var ghost = Std("Ghost", new Color(0.4f, 0.9f, 1f, 0.3f), 0f, 0.8f, emission: new Color(0.1f, 0.4f, 0.5f)); Transparent(ghost, true);
            // world
            Std("Asphalt", Color.white, 0f, 0.25f);
            Std("Grass", Color.white, 0f, 0.05f);
            Std("Curb", Color.white, 0f, 0.35f);
            Std("Checker", Color.white, 0f, 0.3f);
            Std("Line", new Color(0.95f, 0.95f, 0.9f), 0f, 0.3f);
            Std("Concrete", new Color(0.55f, 0.56f, 0.58f), 0f, 0.15f);
            Std("Seats", new Color(0.12f, 0.3f, 0.75f), 0f, 0.3f);
            Std("Bark", new Color(0.3f, 0.2f, 0.12f), 0f, 0.05f);
            Std("Leaf", new Color(0.12f, 0.38f, 0.14f), 0f, 0.1f);
            Std("Cone", new Color(1f, 0.42f, 0.05f), 0f, 0.4f);
            Std("Hill", new Color(0.24f, 0.34f, 0.24f), 0f, 0f);
            Std("Gate", new Color(0.05f, 0.4f, 0.38f), 0.2f, 0.6f, emission: new Color(0.2f, 0.85f, 0.8f) * 1.6f);
            Std("Lamp", Color.gray, 0f, 0.8f, emission: Color.black);

            // sky
            string skyPath = $"{MatDir}/Sky.mat";
            var sky = AssetDatabase.LoadAssetAtPath<Material>(skyPath);
            if (sky == null) { sky = new Material(Shader.Find("Skybox/Procedural")); AssetDatabase.CreateAsset(sky, skyPath); }
            sky.SetFloat("_SunSize", 0.035f); sky.SetFloat("_AtmosphereThickness", 0.85f);
            sky.SetColor("_SkyTint", new Color(0.45f, 0.55f, 0.7f)); sky.SetColor("_GroundColor", new Color(0.3f, 0.32f, 0.3f));
            sky.SetFloat("_Exposure", 1.2f);
            // particles / skids
            string smokePath = $"{MatDir}/Smoke.mat";
            var smoke = AssetDatabase.LoadAssetAtPath<Material>(smokePath);
            if (smoke == null) { smoke = new Material(Shader.Find("Legacy Shaders/Particles/Alpha Blended")); AssetDatabase.CreateAsset(smoke, smokePath); }
            smoke.mainTexture = AssetDatabase.GetBuiltinExtraResource<Texture2D>("Default-Particle.psd");
            smoke.SetColor("_TintColor", new Color(0.6f, 0.6f, 0.62f, 0.5f));
            string skidPath = $"{MatDir}/Skid.mat";
            var skid = AssetDatabase.LoadAssetAtPath<Material>(skidPath);
            if (skid == null) { skid = new Material(Shader.Find("Sprites/Default")); AssetDatabase.CreateAsset(skid, skidPath); }
            skid.color = new Color(0f, 0f, 0f, 0.55f);
            EditorUtility.SetDirty(sky); EditorUtility.SetDirty(smoke); EditorUtility.SetDirty(skid);
            AssetDatabase.SaveAssets();
        }

        // ------------------------------------------------------------------ scene
        static void CreateScene()
        {
            Directory.CreateDirectory("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var go = new GameObject("HYPERCAR-X Game");
            go.AddComponent<HypercarX.GameManager>();
            RenderSettings.skybox = AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/Sky.mat");
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }

        // ------------------------------------------------------------------ player settings
        static void ConfigurePlayer()
        {
            PlayerSettings.companyName = "Jaineil Rana";
            PlayerSettings.productName = "HYPERCAR-X Drive";
            PlayerSettings.bundleVersion = "1.0.0";
            PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android, "com.jaineil.hypercarxdrive");
            PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Standalone, "com.jaineil.hypercarxdrive");
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.runInBackground = true;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.defaultScreenWidth = 1600; PlayerSettings.defaultScreenHeight = 900;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.resizableWindow = true;
            var icon = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/icon.png");
            if (icon) PlayerSettings.SetIcons(UnityEditor.Build.NamedBuildTarget.Unknown, new[] { icon }, IconKind.Any);

            // Android: IL2CPP + ARM64 (required for 64-bit phones / Play Store), API 24+
            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel24;
            PlayerSettings.Android.bundleVersionCode = 1;

            // WebGL: uncompressed so it runs on GitHub Pages and Unity Play without server headers
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
            PlayerSettings.WebGL.decompressionFallback = false;
            PlayerSettings.WebGL.dataCaching = true;
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;
        }

        // ------------------------------------------------------------------ builds
        static BuildReport Build(BuildTarget target, string path, BuildOptions opts = BuildOptions.None)
        {
            Setup();
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath }, locationPathName = path, target = target, options = opts,
            });
            Debug.Log($"[HX] Build {target}: {report.summary.result}, {report.summary.totalSize / 1e6:0.0} MB, {report.summary.totalErrors} errors → {path}");
            return report;
        }

        [MenuItem("HYPERCAR-X/Build WebGL")]
        public static void BuildWebGL() => Exit(Build(BuildTarget.WebGL, "Builds/WebGL"));

        [MenuItem("HYPERCAR-X/Build Android APK")]
        public static void BuildAndroid()
        {
            EditorUserBuildSettings.buildAppBundle = false;
            Exit(Build(BuildTarget.Android, "Builds/Android/HypercarX-Drive.apk"));
        }

        [MenuItem("HYPERCAR-X/Build Windows")]
        public static void BuildWindows() => Exit(Build(BuildTarget.StandaloneWindows64, "Builds/Windows/HypercarX-Drive.exe"));

        public static void SetupOnly() { Setup(); if (Application.isBatchMode) EditorApplication.Exit(0); }

        static void Exit(BuildReport r)
        {
            if (Application.isBatchMode) EditorApplication.Exit(r.summary.result == BuildResult.Succeeded ? 0 : 1);
        }
    }
}
