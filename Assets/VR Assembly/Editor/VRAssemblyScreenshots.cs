using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Renders README screenshots of the assembly scene from a camera at headset height.
/// Each shot reopens the saved scene and uses AssemblyManager.PreviewState to jump to a step
/// (nothing is saved). Output: "Screenshots" folder in the project root.
/// Command line (needs graphics, so no -nographics):
/// Unity.exe -batchmode -quit -projectPath . -executeMethod VRAssemblyScreenshots.CaptureInBatch -logFile shots.log
/// </summary>
public static class VRAssemblyScreenshots
{
    const int Width = 1920, Height = 1080;

    class Shot
    {
        public string file;
        public Vector3 camPos, lookAt;
        public float fov = 60f;
        public Action<Dictionary<string, AssemblyManager>> setup;
    }

    // Station 1 (Robot) bench centre (-0.15, 0, 0.8) facing -Z; Station 2 (Rocket) centre (0.95, 0, -0.35) facing -X
    static readonly Vector3 RobotEye = new Vector3(-0.15f, 1.7f, -0.5f);
    static readonly Vector3 RobotLook = new Vector3(-0.15f, 1.25f, 0.95f);
    static readonly Vector3 RocketEye = new Vector3(-0.35f, 1.7f, -0.35f);
    static readonly Vector3 RocketLook = new Vector3(1.1f, 1.25f, -0.35f);
    static readonly Vector3 OverviewEye = new Vector3(-1.25f, 1.75f, -1.45f);
    static readonly Vector3 OverviewLook = new Vector3(0.35f, 1.0f, 0.25f);

    static readonly Shot[] Shots =
    {
        new Shot { file = "01_start_overview.png", camPos = OverviewEye, lookAt = OverviewLook,
                   setup = m => { m["Robot"].PreviewState(0, -1, 0f); m["Rocket"].PreviewState(0, -1, 0f); } },
        new Shot { file = "02_robot_mid_assembly.png", camPos = RobotEye, lookAt = RobotLook, fov = 58f,
                   setup = m => m["Robot"].PreviewState(5, -1, 0f) },
        new Shot { file = "03_robot_complete.png", camPos = RobotEye, lookAt = RobotLook, fov = 58f,
                   setup = m => m["Robot"].PreviewState(15, -1, 154f) },
        new Shot { file = "04_robot_disassembly.png", camPos = RobotEye, lookAt = RobotLook, fov = 58f,
                   setup = m => m["Robot"].PreviewState(15, 4, 154f) },
        new Shot { file = "05_rocket_mid_assembly.png", camPos = RocketEye, lookAt = RocketLook, fov = 58f,
                   setup = m => m["Rocket"].PreviewState(7, -1, 0f) },
        new Shot { file = "06_rocket_complete.png", camPos = RocketEye, lookAt = RocketLook, fov = 58f,
                   setup = m => m["Rocket"].PreviewState(11, -1, 97f) },
        new Shot { file = "07_both_complete_overview.png", camPos = OverviewEye, lookAt = OverviewLook,
                   setup = m => { m["Robot"].PreviewState(15, -1, 154f); m["Rocket"].PreviewState(11, -1, 97f); } },
    };

    [MenuItem("Tools/VR Assembly/Capture README Screenshots")]
    public static void Capture()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        CaptureAll();
        EditorSceneManager.OpenScene(VRAssemblySceneBuilder.ScenePath);
    }

    public static void CaptureInBatch()
    {
        try
        {
            CaptureAll();
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }

    static void CaptureAll()
    {
        string outDir = Path.GetFullPath("Screenshots");
        Directory.CreateDirectory(outDir);

        // Otherwise shaders not compiled yet render as placeholders in the first shots
        bool asyncCompile = ShaderUtil.allowAsyncCompilation;
        ShaderUtil.allowAsyncCompilation = false;
        try
        {
            foreach (var shot in Shots)
            {
                EditorSceneManager.OpenScene(VRAssemblySceneBuilder.ScenePath, OpenSceneMode.Single);
                var managers = Object.FindObjectsByType<AssemblyManager>(FindObjectsSortMode.None)
                    .ToDictionary(m => m.objectName);
                shot.setup(managers);

                // The rig's controller models would float in front of the camera
                var origin = Object.FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>();
                if (origin) origin.gameObject.SetActive(false);

                string path = Path.Combine(outDir, shot.file);
                Render(shot, path);
                Debug.Log("[VRAssembly] Screenshot saved: " + path);
            }
        }
        finally
        {
            ShaderUtil.allowAsyncCompilation = asyncCompile;
        }
    }

    static void Render(Shot shot, string path)
    {
        var camGo = new GameObject("Screenshot Camera");
        var cam = camGo.AddComponent<Camera>();
        cam.transform.position = shot.camPos;
        cam.transform.LookAt(shot.lookAt);
        cam.fieldOfView = shot.fov;
        cam.nearClipPlane = 0.02f;
        cam.farClipPlane = 50f;
        cam.clearFlags = CameraClearFlags.Skybox;

        var desc = new RenderTextureDescriptor(Width, Height, RenderTextureFormat.ARGB32, 24) { sRGB = true, msaaSamples = 8 };
        var rt = new RenderTexture(desc);
        cam.targetTexture = rt;

        // Make sure world-space UI text is generated before rendering
        foreach (var t in Object.FindObjectsByType<TMP_Text>(FindObjectsSortMode.None)) t.ForceMeshUpdate(true, true);
        Canvas.ForceUpdateCanvases();

        // First render of a session can still show placeholder shaders: render once to warm up
        cam.Render();
        cam.Render();

        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        File.WriteAllBytes(path, tex.EncodeToPNG());

        cam.targetTexture = null;
        Object.DestroyImmediate(tex);
        Object.DestroyImmediate(rt);
        Object.DestroyImmediate(camGo);
    }
}
