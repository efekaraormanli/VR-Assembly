using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation;
using UnityEngine.XR.Interaction.Toolkit.UI;
using Object = UnityEngine.Object;

/// <summary>
/// Editor tool: Tools > Build VR Assembly Scene (or VRAssemblySceneBuilder.BuildInBatch from the command line).
/// Builds the whole assembly scene: room, lighting, and one workstation per entry in <see cref="Objects"/>
/// (workbench, assembly pad, parts scattered on the table, targets from the assembled pose, marker,
/// world-space instruction panel with reset button, AssemblyManager, teleport anchor), plus the
/// XR Origin (XR Rig) from Starter Assets and an EventSystem with XRUIInputModule.
/// Safe to run again: it deletes and rebuilds everything under "VR Assembly (Generated)".
/// </summary>
public static class VRAssemblySceneBuilder
{
    public const string ScenePath = "Assets/VR Assembly/Scene_VRAssembly.unity";
    const string RootName = "VR Assembly (Generated)";
    const string MatFolder = "Assets/VR Assembly/Materials/Generated";
    const float TableTop = 0.9f;

    // ---------------- Data ----------------

    class PartDef
    {
        public string name, display, mat;
        public PrimitiveType type;
        public Vector3 pos, scale;
        // pos: x/z relative to the assembly pad centre, y = height above the floor (assembled pose)
        public PartDef(string name, string display, PrimitiveType type, Vector3 pos, Vector3 scale, string mat)
        {
            this.name = name; this.display = display; this.type = type;
            this.pos = pos; this.scale = scale; this.mat = mat;
        }
    }

    class AssemblyObjectDef
    {
        public string name;          // e.g. "Robot"
        public string hint;          // where the other station is
        public Vector3 stationPos;   // workbench centre on the floor
        public float stationYaw;     // bench faces the user along its local -Z
        public int shuffleSeed;      // table layout, so the scatter doesn't give away the order
        public PartDef[] parts;      // assembly order = list order; parts[0] is the base
    }

    static readonly PartDef[] RobotParts =
    {
        new PartDef("Base",       "Base Plate",  PrimitiveType.Cylinder, new Vector3( 0.00f, 0.915f,  0.000f), new Vector3(0.25f, 0.015f, 0.25f),   "RobotBody"),
        new PartDef("LeftFoot",   "Left Foot",   PrimitiveType.Cube,     new Vector3(-0.04f, 0.945f, -0.010f), new Vector3(0.07f, 0.03f, 0.09f),    "RobotAccent"),
        new PartDef("RightFoot",  "Right Foot",  PrimitiveType.Cube,     new Vector3( 0.04f, 0.945f, -0.010f), new Vector3(0.07f, 0.03f, 0.09f),    "RobotAccent"),
        new PartDef("LeftLeg",    "Left Leg",    PrimitiveType.Cube,     new Vector3(-0.04f, 1.020f,  0.000f), new Vector3(0.05f, 0.12f, 0.05f),    "RobotBody"),
        new PartDef("RightLeg",   "Right Leg",   PrimitiveType.Cube,     new Vector3( 0.04f, 1.020f,  0.000f), new Vector3(0.05f, 0.12f, 0.05f),    "RobotBody"),
        new PartDef("Torso",      "Torso",       PrimitiveType.Cube,     new Vector3( 0.00f, 1.160f,  0.000f), new Vector3(0.16f, 0.16f, 0.10f),    "RobotBody"),
        new PartDef("ChestPanel", "Chest Panel", PrimitiveType.Cube,     new Vector3( 0.00f, 1.170f, -0.055f), new Vector3(0.08f, 0.06f, 0.01f),    "RobotGlow"),
        new PartDef("LeftArm",    "Left Arm",    PrimitiveType.Capsule,  new Vector3(-0.10f, 1.170f,  0.000f), new Vector3(0.04f, 0.06f, 0.04f),    "RobotAccent"),
        new PartDef("RightArm",   "Right Arm",   PrimitiveType.Capsule,  new Vector3( 0.10f, 1.170f,  0.000f), new Vector3(0.04f, 0.06f, 0.04f),    "RobotAccent"),
        new PartDef("LeftHand",   "Left Hand",   PrimitiveType.Sphere,   new Vector3(-0.10f, 1.090f,  0.000f), new Vector3(0.045f, 0.045f, 0.045f), "RobotDark"),
        new PartDef("RightHand",  "Right Hand",  PrimitiveType.Sphere,   new Vector3( 0.10f, 1.090f,  0.000f), new Vector3(0.045f, 0.045f, 0.045f), "RobotDark"),
        new PartDef("Head",       "Head",        PrimitiveType.Sphere,   new Vector3( 0.00f, 1.295f,  0.000f), new Vector3(0.11f, 0.11f, 0.11f),    "RobotAccent"),
        new PartDef("LeftEye",    "Left Eye",    PrimitiveType.Sphere,   new Vector3(-0.02f, 1.305f, -0.048f), new Vector3(0.025f, 0.025f, 0.025f), "RobotGlow"),
        new PartDef("RightEye",   "Right Eye",   PrimitiveType.Sphere,   new Vector3( 0.02f, 1.305f, -0.048f), new Vector3(0.025f, 0.025f, 0.025f), "RobotGlow"),
        new PartDef("Antenna",    "Antenna",     PrimitiveType.Cylinder, new Vector3( 0.00f, 1.390f,  0.000f), new Vector3(0.015f, 0.04f, 0.015f),  "RobotAccent"),
    };

    static readonly PartDef[] RocketParts =
    {
        new PartDef("LaunchPad",  "Launch Pad",    PrimitiveType.Cylinder, new Vector3( 0.000f, 0.910f,  0.000f), new Vector3(0.22f, 0.01f, 0.22f),    "RocketDark"),
        new PartDef("Nozzle",     "Engine Nozzle", PrimitiveType.Cylinder, new Vector3( 0.000f, 0.945f,  0.000f), new Vector3(0.06f, 0.025f, 0.06f),   "RocketDark"),
        new PartDef("LowerBody",  "Lower Body",    PrimitiveType.Cylinder, new Vector3( 0.000f, 1.030f,  0.000f), new Vector3(0.09f, 0.06f, 0.09f),    "RocketWhite"),
        new PartDef("FinLeft",    "Left Fin",      PrimitiveType.Cube,     new Vector3(-0.055f, 1.000f,  0.000f), new Vector3(0.02f, 0.08f, 0.05f),    "RocketRed"),
        new PartDef("FinRight",   "Right Fin",     PrimitiveType.Cube,     new Vector3( 0.055f, 1.000f,  0.000f), new Vector3(0.02f, 0.08f, 0.05f),    "RocketRed"),
        new PartDef("FinFront",   "Front Fin",     PrimitiveType.Cube,     new Vector3( 0.000f, 1.000f, -0.055f), new Vector3(0.05f, 0.08f, 0.02f),    "RocketRed"),
        new PartDef("FinBack",    "Back Fin",      PrimitiveType.Cube,     new Vector3( 0.000f, 1.000f,  0.055f), new Vector3(0.05f, 0.08f, 0.02f),    "RocketRed"),
        new PartDef("UpperBody",  "Upper Body",    PrimitiveType.Cylinder, new Vector3( 0.000f, 1.150f,  0.000f), new Vector3(0.08f, 0.06f, 0.08f),    "RocketWhite"),
        new PartDef("Window",     "Window",        PrimitiveType.Sphere,   new Vector3( 0.000f, 1.160f, -0.040f), new Vector3(0.035f, 0.035f, 0.015f), "RocketGlass"),
        new PartDef("NoseCone",   "Nose Cone",     PrimitiveType.Sphere,   new Vector3( 0.000f, 1.240f,  0.000f), new Vector3(0.08f, 0.14f, 0.08f),    "RocketRed"),
        new PartDef("Tip",        "Antenna Tip",   PrimitiveType.Cylinder, new Vector3( 0.000f, 1.325f,  0.000f), new Vector3(0.01f, 0.02f, 0.01f),    "RocketDark"),
    };

    // Two workbenches in an L around the user (who starts at the corner): Robot in front, Rocket on the right.
    static readonly AssemblyObjectDef[] Objects =
    {
        new AssemblyObjectDef
        {
            name = "Robot", hint = "Rocket station: turn right",
            stationPos = new Vector3(-0.15f, 0f, 0.80f), stationYaw = 0f, shuffleSeed = 7, parts = RobotParts,
        },
        new AssemblyObjectDef
        {
            name = "Rocket", hint = "Robot station: turn left",
            stationPos = new Vector3(0.95f, 0f, -0.35f), stationYaw = 90f, shuffleSeed = 3, parts = RocketParts,
        },
    };

    // Bench-local layout (bench is 1.6 x 0.7 m, local -Z faces the user)
    static readonly Vector3 PadCenter = new Vector3(0f, TableTop, 0.08f);
    static readonly Vector3 BaseSpot = new Vector3(0f, TableTop, -0.21f);       // in front of the pad
    static readonly float[] SlotColumns = { -0.64f, -0.46f, -0.28f, 0.28f, 0.46f, 0.64f };
    static readonly float[] SlotRows = { -0.20f, 0.00f, 0.20f };

    static readonly Vector3 OriginStart = new Vector3(0.05f, 0f, -0.10f);

    // ---------------- Entry points ----------------

    [MenuItem("Tools/Build VR Assembly Scene")]
    public static void Build()
    {
        if (!HasTMPSettings())
        {
            EditorUtility.DisplayDialog("TextMeshPro needed",
                "First import TMP Essentials:\nWindow > TextMeshPro > Import TMP Essential Resources.\n\nThen run Tools > Build VR Assembly Scene again.",
                "OK");
            return;
        }
        BuildInActiveScene();
        Debug.Log("VR Assembly scene built. Press Ctrl+S to save the scene, then Play.");
    }

    /// <summary>
    /// Command line: Unity.exe -batchmode -quit -projectPath . -executeMethod VRAssemblySceneBuilder.BuildInBatch -logFile build.log
    /// </summary>
    public static void BuildInBatch()
    {
        try
        {
            if (!HasTMPSettings())
            {
                ImportTMPEssentials();
                if (!HasTMPSettings())
                {
                    Debug.LogError("[VRAssembly] TMP Essential Resources were imported but are not available yet. Run BuildInBatch again.");
                    EditorApplication.Exit(2);
                    return;
                }
            }

            EnsureFolder("Assets/VR Assembly");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            BuildInActiveScene();
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new Exception("Could not save " + ScenePath);

            var others = EditorBuildSettings.scenes.Where(s => s.path != ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) }.Concat(others).ToArray();
            AssetDatabase.SaveAssets();
            Debug.Log("[VRAssembly] BuildInBatch finished: " + ScenePath);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }

    static bool HasTMPSettings() => AssetDatabase.FindAssets("t:TMP_Settings").Length > 0;

    static void ImportTMPEssentials()
    {
        var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(TMP_Settings).Assembly);
        if (info == null) throw new Exception("Could not find the package that contains TextMeshPro.");
        string pkg = info.resolvedPath + "/Package Resources/TMP Essential Resources.unitypackage";
        Debug.Log("[VRAssembly] Importing " + pkg);
        // AssetDatabase.ImportPackage is deferred and never runs in a -batchmode -quit session,
        // so unpack the package (a gzipped tar of guid/pathname, guid/asset, guid/asset.meta) directly.
        int count = ExtractUnityPackage(pkg);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        Debug.Log($"[VRAssembly] Extracted {count} TMP assets");
    }

    static int ExtractUnityPackage(string packagePath)
    {
        var files = new Dictionary<string, Dictionary<string, byte[]>>();
        using (var gz = new System.IO.Compression.GZipStream(System.IO.File.OpenRead(packagePath), System.IO.Compression.CompressionMode.Decompress))
        {
            var header = new byte[512];
            string longName = null;
            while (ReadFully(gz, header, 512) && header[0] != 0)
            {
                string name = longName ?? TarString(header, 0, 100);
                longName = null;
                string prefix = TarString(header, 345, 155);
                if (prefix.Length > 0) name = prefix + "/" + name;
                string sizeField = TarString(header, 124, 12).Trim();
                long size = sizeField.Length > 0 ? Convert.ToInt64(sizeField, 8) : 0;
                char type = (char)header[156];

                var data = new byte[size];
                ReadFully(gz, data, (int)size);
                int pad = (int)((512 - size % 512) % 512);
                if (pad > 0) ReadFully(gz, new byte[pad], pad);

                if (type == 'L') { longName = System.Text.Encoding.UTF8.GetString(data).TrimEnd('\0'); continue; }
                if (type != '0' && type != '\0') continue;

                name = name.TrimStart('.', '/');
                int slash = name.IndexOf('/');
                if (slash < 0) continue;
                string guid = name.Substring(0, slash), entry = name.Substring(slash + 1);
                if (!files.TryGetValue(guid, out var entries)) files[guid] = entries = new Dictionary<string, byte[]>();
                entries[entry] = data;
            }
        }

        int written = 0;
        foreach (var entries in files.Values)
        {
            if (!entries.TryGetValue("pathname", out var pn)) continue;
            string assetPath = System.Text.Encoding.UTF8.GetString(pn).Split('\n')[0].Trim();
            string full = System.IO.Path.GetFullPath(assetPath);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full));
            if (entries.TryGetValue("asset", out var asset)) { System.IO.File.WriteAllBytes(full, asset); written++; }
            else System.IO.Directory.CreateDirectory(full);
            if (entries.TryGetValue("asset.meta", out var meta)) System.IO.File.WriteAllBytes(full + ".meta", meta);
        }
        return written;
    }

    static string TarString(byte[] buf, int offset, int length)
    {
        int end = offset;
        while (end < offset + length && buf[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(buf, offset, end - offset);
    }

    static bool ReadFully(System.IO.Stream s, byte[] buf, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = s.Read(buf, read, count - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    // ---------------- Build ----------------

    static void BuildInActiveScene()
    {
        var old = GameObject.Find(RootName);
        if (old) Object.DestroyImmediate(old);

        EnsureFolder(MatFolder);
        var root = new GameObject(RootName);

        var mats = new Dictionary<string, Material>
        {
            ["Floor"]       = Mat("Mat_Floor",       new Color(0.30f, 0.31f, 0.33f)),
            ["Wood"]        = Mat("Mat_Wood",        new Color(0.55f, 0.37f, 0.22f)),
            ["Metal"]       = Mat("Mat_Metal",       new Color(0.25f, 0.26f, 0.28f), smooth: 0.6f, metallic: 0.8f),
            ["Wall"]        = Mat("Mat_Wall",        new Color(0.80f, 0.77f, 0.70f)),
            ["Pad"]         = Mat("Mat_AssemblyPad", new Color(0.12f, 0.30f, 0.45f), new Color(0.02f, 0.08f, 0.12f)),
            ["Anchor"]      = Mat("Mat_TeleportAnchor", new Color(0.20f, 0.55f, 0.85f), new Color(0.05f, 0.2f, 0.35f)),
            ["LampShade"]   = Mat("Mat_LampShade",   new Color(1.00f, 0.95f, 0.85f), new Color(1f, 0.92f, 0.75f) * 2f),
            ["Marker"]      = Mat("Mat_Marker",      new Color(1.00f, 0.85f, 0.20f), new Color(1f, 0.8f, 0.1f) * 1.5f),
            ["RobotBody"]   = Mat("Mat_RobotBody",   new Color(0.78f, 0.80f, 0.83f)),
            ["RobotAccent"] = Mat("Mat_RobotAccent", new Color(0.95f, 0.50f, 0.12f)),
            ["RobotDark"]   = Mat("Mat_RobotDark",   new Color(0.18f, 0.18f, 0.20f)),
            ["RobotGlow"]   = Mat("Mat_RobotGlow",   new Color(0.25f, 0.90f, 1.00f), new Color(0.2f, 0.8f, 1f) * 1.5f),
            ["RocketWhite"] = Mat("Mat_RocketWhite", new Color(0.92f, 0.92f, 0.95f), smooth: 0.6f),
            ["RocketRed"]   = Mat("Mat_RocketRed",   new Color(0.85f, 0.15f, 0.15f), smooth: 0.5f),
            ["RocketDark"]  = Mat("Mat_RocketDark",  new Color(0.22f, 0.23f, 0.26f), smooth: 0.6f, metallic: 0.7f),
            ["RocketGlass"] = Mat("Mat_RocketGlass", new Color(0.35f, 0.75f, 1.00f), new Color(0.2f, 0.6f, 1f) * 1.2f),
        };
        var ghostMat = GhostMat();

        BuildRoom(root, mats);
        BuildLighting(root);

        var origin = EnsureXROrigin();
        EnsureXREventSystem();

        foreach (var def in Objects)
            BuildStation(root, def, mats, ghostMat, origin);

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        AssetDatabase.SaveAssets();
        Selection.activeGameObject = root;
    }

    static void BuildRoom(GameObject root, Dictionary<string, Material> mats)
    {
        var env = Child(root, "Environment");
        // Room: x -2.5..1.75, z -2.5..1.6
        Prim(PrimitiveType.Plane, "Floor", env, new Vector3(-0.375f, 0f, -0.45f), new Vector3(0.5f, 1f, 0.5f), mats["Floor"]);
        const float h = 2.6f;
        Prim(PrimitiveType.Cube, "BackWall",  env, new Vector3(-0.375f, h / 2, 1.65f),  new Vector3(4.35f, h, 0.1f), mats["Wall"]);
        Prim(PrimitiveType.Cube, "FrontWall", env, new Vector3(-0.375f, h / 2, -2.55f), new Vector3(4.35f, h, 0.1f), mats["Wall"]);
        Prim(PrimitiveType.Cube, "RightWall", env, new Vector3(1.80f, h / 2, -0.45f),   new Vector3(0.1f, h, 4.3f),  mats["Wall"]);
        Prim(PrimitiveType.Cube, "LeftWall",  env, new Vector3(-2.55f, h / 2, -0.45f),  new Vector3(0.1f, h, 4.3f),  mats["Wall"]);
    }

    static void BuildLighting(GameObject root)
    {
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.62f, 0.66f, 0.74f);
        RenderSettings.ambientEquatorColor = new Color(0.45f, 0.43f, 0.40f);
        RenderSettings.ambientGroundColor = new Color(0.22f, 0.20f, 0.18f);
        var sky = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Skybox.mat");
        if (sky) RenderSettings.skybox = sky;

        Light sun = null;
        foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            if (l.type == LightType.Directional) { sun = l; break; }
        if (sun == null)
        {
            sun = new GameObject("Directional Light").AddComponent<Light>();
            sun.type = LightType.Directional;
        }
        sun.transform.rotation = Quaternion.Euler(65f, 30f, 0f);
        sun.color = new Color(1f, 0.96f, 0.9f);
        sun.shadows = LightShadows.Soft;
        sun.intensity = 1.1f;
        RenderSettings.sun = sun;
    }

    static void BuildStation(GameObject root, AssemblyObjectDef def, Dictionary<string, Material> mats, Material ghostMat, XROrigin origin)
    {
        int index = Array.IndexOf(Objects, def) + 1;
        var station = Child(root, $"Station {index} - {def.name}");
        station.transform.SetPositionAndRotation(def.stationPos, Quaternion.Euler(0f, def.stationYaw, 0f));

        // ---------- Workbench ----------
        var bench = Child(station, "Workbench");
        Prim(PrimitiveType.Cube, "Top", bench, new Vector3(0f, TableTop - 0.025f, 0f), new Vector3(1.6f, 0.05f, 0.7f), mats["Wood"], local: true);
        foreach (var x in new[] { -0.74f, 0.74f })
            foreach (var z in new[] { -0.29f, 0.29f })
                Prim(PrimitiveType.Cube, "Leg", bench, new Vector3(x, (TableTop - 0.05f) / 2f, z), new Vector3(0.06f, TableTop - 0.05f, 0.06f), mats["Metal"], local: true);
        Prim(PrimitiveType.Cube, "Shelf", bench, new Vector3(0f, 0.18f, 0f), new Vector3(1.5f, 0.03f, 0.6f), mats["Metal"], local: true);
        // Small rim so round parts can't roll off the table
        Prim(PrimitiveType.Cube, "Rim_Front", bench, new Vector3(0f, TableTop + 0.015f, -0.345f), new Vector3(1.6f, 0.03f, 0.01f), mats["Wood"], local: true);
        Prim(PrimitiveType.Cube, "Rim_Back",  bench, new Vector3(0f, TableTop + 0.015f, 0.345f),  new Vector3(1.6f, 0.03f, 0.01f), mats["Wood"], local: true);
        Prim(PrimitiveType.Cube, "Rim_Left",  bench, new Vector3(-0.795f, TableTop + 0.015f, 0f), new Vector3(0.01f, 0.03f, 0.7f), mats["Wood"], local: true);
        Prim(PrimitiveType.Cube, "Rim_Right", bench, new Vector3(0.795f, TableTop + 0.015f, 0f),  new Vector3(0.01f, 0.03f, 0.7f), mats["Wood"], local: true);
        // Flat pad marking the assembly area
        var pad = Prim(PrimitiveType.Cylinder, "Assembly Area", bench, PadCenter + Vector3.up * 0.0005f, new Vector3(0.32f, 0.0005f, 0.32f), mats["Pad"], local: true);
        Object.DestroyImmediate(pad.GetComponent<Collider>());

        // ---------- Work lamp ----------
        var lamp = new GameObject("Work Lamp").AddComponent<Light>();
        lamp.transform.SetParent(station.transform, false);
        lamp.transform.localPosition = new Vector3(0f, 2.25f, 0.05f);
        lamp.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        lamp.type = LightType.Spot;
        lamp.color = new Color(1f, 0.94f, 0.82f);
        lamp.range = 4f;
        lamp.spotAngle = 75f;
        lamp.intensity = 2.5f;
        lamp.shadows = LightShadows.Soft;
        var shade = Prim(PrimitiveType.Cylinder, "Lamp Shade", station, new Vector3(0f, 2.3f, 0.05f), new Vector3(0.35f, 0.02f, 0.35f), mats["LampShade"], local: true);
        Object.DestroyImmediate(shade.GetComponent<Collider>());
        shade.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;

        // ---------- Parts + targets ----------
        var partsRoot = Child(station, $"{def.name} Parts");
        var targetsRoot = Child(station, "Assembly Targets");
        var slots = ShuffledSlots(def.shuffleSeed);
        if (def.parts.Length - 1 > slots.Count)
            throw new Exception($"{def.name}: {def.parts.Length} parts but only {slots.Count + 1} table spots.");

        var parts = new List<AssemblyPart>();
        for (int i = 0; i < def.parts.Length; i++)
        {
            var d = def.parts[i];
            var go = Prim(d.type, d.name, partsRoot, Vector3.zero, d.scale, mats[d.mat], local: true);
            if (d.type == PrimitiveType.Cylinder)
            {
                // Unity's cylinder primitive ships with a CapsuleCollider, which turns flat discs into balls
                Object.DestroyImmediate(go.GetComponent<Collider>());
                go.AddComponent<MeshCollider>().convex = true;
            }

            var grab = go.AddComponent<XRGrabInteractable>();
            grab.useDynamicAttach = true;
            var rb = go.GetComponent<Rigidbody>();
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.angularDamping = 2f;
            rb.mass = 0.3f;

            var part = go.AddComponent<AssemblyPart>();
            part.displayName = d.display;
            part.snapAngle = 180f; // snapping fixes the rotation, so any angle is accepted
            part.snapDistance = 0.1f;

            // Target = the assembled pose
            var t = new GameObject("Target_" + d.name).transform;
            t.SetParent(targetsRoot.transform, false);
            t.localPosition = new Vector3(PadCenter.x + d.pos.x, d.pos.y, PadCenter.z + d.pos.z);
            t.localRotation = Quaternion.identity;
            part.target = t;

            // Start pose: lying on the table
            Vector3 spot = i == 0 ? BaseSpot : slots[i - 1];
            go.transform.localRotation = RestRotation(d, out float halfHeight);
            go.transform.localPosition = new Vector3(spot.x, TableTop + halfHeight + 0.002f, spot.z);
            parts.Add(part);
        }

        // ---------- Marker ----------
        var marker = Prim(PrimitiveType.Cube, "Next Part Marker", station, new Vector3(0f, 1.3f, 0f), Vector3.one * 0.035f, mats["Marker"], local: true);
        marker.transform.rotation = Quaternion.Euler(45f, 0f, 45f);
        Object.DestroyImmediate(marker.GetComponent<Collider>());
        marker.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;

        // ---------- Manager ----------
        var mgrGo = Child(station, "Assembly Manager");
        var mgr = mgrGo.AddComponent<AssemblyManager>();
        mgr.objectName = def.name;
        mgr.steps = parts;
        mgr.ghostMaterial = ghostMat;
        mgr.marker = marker.transform;
        var audio = mgrGo.AddComponent<AudioSource>();
        audio.playOnAwake = false;
        audio.spatialBlend = 0.6f;
        mgrGo.transform.localPosition = PadCenter;
        mgr.audioSource = audio;

        // ---------- Instruction panel ----------
        BuildPanel(station, mgr, origin, $"Station {index}: {def.name} ({def.parts.Length} parts)", def.hint);

        // ---------- Teleport anchor in front of the bench (Starter Assets teleport) ----------
        var anchor = Prim(PrimitiveType.Cylinder, "Teleport Anchor", station, new Vector3(0f, 0.005f, -0.75f), new Vector3(0.4f, 0.005f, 0.4f), mats["Anchor"], local: true);
        Object.DestroyImmediate(anchor.GetComponent<Collider>());
        anchor.AddComponent<BoxCollider>();
        var tp = anchor.AddComponent<TeleportationAnchor>();
        tp.interactionLayers = InteractionLayerMask.GetMask("Teleport");
        tp.matchOrientation = MatchOrientation.TargetUpAndForward;
        tp.teleportAnchorTransform = anchor.transform;
    }

    /// <summary>Rotation that lays the part on its flattest side, and the resulting half height.</summary>
    static Quaternion RestRotation(PartDef d, out float halfHeight)
    {
        // Half extents of Unity's primitive meshes (cylinder / capsule are 2 units tall)
        bool tall = d.type == PrimitiveType.Cylinder || d.type == PrimitiveType.Capsule;
        Vector3 e = new Vector3(d.scale.x * 0.5f, d.scale.y * (tall ? 1f : 0.5f), d.scale.z * 0.5f);

        if (tall)
        {
            // Keep squat cylinders upright so they don't roll; lay long ones down
            if (e.y <= e.x * 1.5f) { halfHeight = e.y; return Quaternion.identity; }
            halfHeight = e.x;
            return Quaternion.Euler(0f, 0f, 90f);
        }

        if (e.y <= e.x && e.y <= e.z) { halfHeight = e.y; return Quaternion.identity; }
        if (e.z <= e.x) { halfHeight = e.z; return Quaternion.Euler(90f, 0f, 0f); }
        halfHeight = e.x;
        return Quaternion.Euler(0f, 0f, 90f);
    }

    static List<Vector3> ShuffledSlots(int seed)
    {
        var slots = new List<Vector3>();
        foreach (var z in SlotRows)
            foreach (var x in SlotColumns)
                slots.Add(new Vector3(x, TableTop, z));
        var rng = new System.Random(seed);
        for (int i = slots.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (slots[i], slots[j]) = (slots[j], slots[i]);
        }
        return slots;
    }

    // ---------------- XR rig / UI ----------------

    static XROrigin EnsureXROrigin()
    {
        var origin = Object.FindFirstObjectByType<XROrigin>();
        if (origin == null)
        {
            string found = null;
            foreach (var guid in AssetDatabase.FindAssets("XR Origin t:Prefab"))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (p.EndsWith("XR Origin (XR Rig).prefab")) { found = p; break; }
            }

            if (found != null)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(found);
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                origin = inst.GetComponent<XROrigin>();
            }
            else
            {
                const string msg = "Could not find 'XR Origin (XR Rig)'. Import Starter Assets from Package Manager > XR Interaction Toolkit > Samples, then run the tool again.";
                if (Application.isBatchMode) throw new Exception(msg);
                EditorUtility.DisplayDialog("XR Origin not found", msg, "OK");
            }
        }

        if (origin != null)
        {
            // User starts in the corner between the two benches, facing Station 1
            origin.transform.SetPositionAndRotation(OriginStart, Quaternion.identity);

            // Remove the default Main Camera if it's not part of the XR rig
            foreach (var cam in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
                if (cam.gameObject.name == "Main Camera" && !cam.transform.IsChildOf(origin.transform))
                    Object.DestroyImmediate(cam.gameObject);
        }
        return origin;
    }

    static void EnsureXREventSystem()
    {
        var es = Object.FindFirstObjectByType<EventSystem>();
        if (es == null)
        {
            new GameObject("EventSystem", typeof(EventSystem), typeof(XRUIInputModule));
            return;
        }
        if (es.GetComponent<XRUIInputModule>() == null)
        {
            foreach (var m in es.GetComponents<BaseInputModule>()) Object.DestroyImmediate(m);
            es.gameObject.AddComponent<XRUIInputModule>();
        }
    }

    static void BuildPanel(GameObject station, AssemblyManager mgr, XROrigin origin, string titleText, string hintText)
    {
        var canvasGo = new GameObject("Instruction Panel", typeof(RectTransform), typeof(Canvas), typeof(TrackedDeviceGraphicRaycaster));
        canvasGo.transform.SetParent(station.transform, false);
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        if (origin != null && origin.Camera != null) canvas.worldCamera = origin.Camera;

        // Behind the bench, slightly tilted back, facing the user (canvas front = local -Z)
        var rt = (RectTransform)canvasGo.transform;
        rt.sizeDelta = new Vector2(1000f, 620f);
        rt.localPosition = new Vector3(0f, 1.68f, 0.42f);
        rt.localRotation = Quaternion.Euler(-8f, 0f, 0f);
        rt.localScale = Vector3.one * 0.0009f;

        var bg = UIObject("Background", canvasGo.transform, Vector2.zero, Vector2.one).gameObject.AddComponent<Image>();
        bg.color = new Color(0.08f, 0.09f, 0.12f, 0.92f);

        var title = Text(canvasGo.transform, "Title", new Vector2(0f, 0.87f), new Vector2(0.62f, 1f), 30, FontStyles.Bold);
        title.text = titleText;
        title.color = new Color(0.55f, 0.8f, 1f);

        mgr.stepText = Text(canvasGo.transform, "Step Text", new Vector2(0f, 0.71f), new Vector2(0.62f, 0.87f), 42, FontStyles.Bold);
        mgr.instructionText = Text(canvasGo.transform, "Instruction Text", new Vector2(0f, 0.45f), new Vector2(0.62f, 0.71f), 30, FontStyles.Normal);
        mgr.feedbackText = Text(canvasGo.transform, "Feedback Text", new Vector2(0f, 0.30f), new Vector2(0.62f, 0.45f), 30, FontStyles.Bold);
        mgr.checklistText = Text(canvasGo.transform, "Checklist Text", new Vector2(0.62f, 0.02f), new Vector2(1f, 0.98f), 22, FontStyles.Normal);

        // Placeholder text so the panel isn't empty in the editor; replaced at runtime
        mgr.stepText.text = $"Assembly: Step 1 / {mgr.steps.Count}";
        mgr.instructionText.text = $"Pick up the <b>{mgr.steps[0].displayName}</b>\nand place it on the glowing outline";

        var hint = Text(canvasGo.transform, "Hint", new Vector2(0.29f, 0.03f), new Vector2(0.62f, 0.2f), 19, FontStyles.Italic);
        hint.text = hintText;
        hint.color = new Color(0.6f, 0.63f, 0.7f);
        hint.alignment = TextAlignmentOptions.BottomLeft;

        // Reset button
        var btnRt = UIObject("Reset Button", canvasGo.transform, new Vector2(0.03f, 0.05f), new Vector2(0.28f, 0.2f));
        var img = btnRt.gameObject.AddComponent<Image>();
        img.color = new Color(0.25f, 0.45f, 0.9f);
        var btn = btnRt.gameObject.AddComponent<Button>();
        btn.targetGraphic = img;
        var label = Text(btnRt, "Label", Vector2.zero, Vector2.one, 30, FontStyles.Bold);
        label.text = "Reset";
        label.alignment = TextAlignmentOptions.Center;
        label.rectTransform.offsetMin = label.rectTransform.offsetMax = Vector2.zero;
        UnityEventTools.AddPersistentListener(btn.onClick, mgr.ResetAssembly);
    }

    static RectTransform UIObject(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        return rt;
    }

    static TextMeshProUGUI Text(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, float size, FontStyles style)
    {
        var rt = UIObject(name, parent, anchorMin, anchorMax);
        rt.offsetMin = new Vector2(24f, 8f);
        rt.offsetMax = new Vector2(-24f, -8f);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        t.fontSize = size;
        t.fontStyle = style;
        t.alignment = TextAlignmentOptions.TopLeft;
        t.color = Color.white;
        t.raycastTarget = false;
        t.text = "";
        return t;
    }

    // ---------------- XR Device Simulator ----------------

    [MenuItem("Tools/VR Assembly/Toggle XR Device Simulator")]
    public static void ToggleDeviceSimulator()
    {
        var existing = GameObject.Find("XR Device Simulator");
        if (existing)
        {
            Object.DestroyImmediate(existing);
            Debug.Log("XR Device Simulator removed (use this when playing with the Quest).");
        }
        else
        {
            string path = AssetDatabase.FindAssets("XR Device Simulator t:Prefab")
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => p.EndsWith("/XR Device Simulator.prefab"));
            if (path == null)
            {
                Debug.LogError("XR Device Simulator prefab not found. Import it from Package Manager > XR Interaction Toolkit > Samples.");
                return;
            }
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path));
            inst.name = "XR Device Simulator";
            Debug.Log("XR Device Simulator added. Remove it again before testing on the Quest.");
        }
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
    }

    // ---------------- Helpers ----------------

    static GameObject Child(GameObject parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        return go;
    }

    static GameObject Prim(PrimitiveType type, string name, GameObject parent, Vector3 pos, Vector3 scale, Material mat, bool local = false)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(parent.transform, false);
        if (local) go.transform.localPosition = pos;
        else go.transform.position = pos;
        go.transform.localScale = scale;
        go.GetComponent<Renderer>().sharedMaterial = mat;
        return go;
    }

    static Shader LitShader()
    {
        var s = Shader.Find("Universal Render Pipeline/Lit");
        return s != null ? s : Shader.Find("Standard");
    }

    static Material Mat(string name, Color color, Color? emission = null, float smooth = 0.35f, float metallic = 0f)
    {
        string path = $"{MatFolder}/{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(LitShader());
            AssetDatabase.CreateAsset(m, path);
        }
        m.SetColor("_BaseColor", color);
        m.SetFloat("_Smoothness", smooth);
        m.SetFloat("_Metallic", metallic);
        if (emission.HasValue)
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", emission.Value);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }
        EditorUtility.SetDirty(m);
        return m;
    }

    static Material GhostMat()
    {
        string path = $"{MatFolder}/Mat_Ghost.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(LitShader());
            AssetDatabase.CreateAsset(m, path);
        }
        m.SetColor("_BaseColor", new Color(0.3f, 0.9f, 1f, 0.45f));
        // URP Lit: transparent, alpha blended
        m.SetFloat("_Surface", 1f);
        m.SetFloat("_Blend", 0f);
        m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
        m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_ZWrite", 0f);
        m.SetOverrideTag("RenderType", "Transparent");
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.renderQueue = (int)RenderQueue.Transparent;
        m.SetShaderPassEnabled("DepthOnly", false);
        m.SetShaderPassEnabled("SHADOWCASTER", false);
        m.EnableKeyword("_EMISSION");
        m.SetColor("_EmissionColor", new Color(0.15f, 0.6f, 0.8f));
        EditorUtility.SetDirty(m);
        return m;
    }

    static void EnsureFolder(string path)
    {
        var bits = path.Split('/');
        string cur = bits[0];
        for (int i = 1; i < bits.Length; i++)
        {
            string next = cur + "/" + bits[i];
            if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, bits[i]);
            cur = next;
        }
    }
}
