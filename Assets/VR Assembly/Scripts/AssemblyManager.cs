using System.Collections;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Runs the step-by-step assembly, then (Bonus 2) the disassembly in reverse order.
/// Visual cues: a pulsing ghost outline where the current part goes (turns green when the held
/// part is close enough to snap), a floating marker over the part to pick up (then over its slot
/// while held), and step text + checklist on a world-space panel. Parts only snap / come off in
/// the correct order. Several managers can run side by side (one per assembly object).
/// </summary>
public class AssemblyManager : MonoBehaviour
{
    public enum Phase { Assembly, Disassembly, Finished }

    [Tooltip("Name of the object being built, shown on the panel")]
    public string objectName = "Object";

    [Header("Parts in assembly order (Element 0 = Step 1)")]
    public List<AssemblyPart> steps = new List<AssemblyPart>();

    [Header("Visual cues")]
    [Tooltip("Transparent URP material for the ghost outline of the next part")]
    public Material ghostMaterial;
    [Tooltip("Ghost color while the held part is close enough to snap")]
    public Color ghostInRangeColor = new Color(0.3f, 1f, 0.4f, 0.6f);
    [Tooltip("Small glowing object that hovers over the part to grab, then over its slot")]
    public Transform marker;
    public float markerHeight = 0.08f;
    public float ghostPulseSpeed = 3f;

    [Header("UI (world-space canvas, TextMeshPro)")]
    public TMP_Text stepText;
    public TMP_Text instructionText;
    public TMP_Text checklistText;
    public TMP_Text feedbackText;

    [Header("Audio (optional)")]
    public AudioSource audioSource;
    public AudioClip snapClip;
    public AudioClip wrongClip;
    public AudioClip completeClip;
    [Tooltip("If no clips are assigned, simple beeps are generated at runtime")]
    public bool generateBeepsIfEmpty = true;

    [Header("Bonus 2: Disassembly in reverse order")]
    public bool enableDisassembly = true;
    [Tooltip("Seconds to show the 'assembly complete' message before disassembly starts")]
    public float disassemblyDelay = 3f;

    [Header("Events")]
    public UnityEvent onAssemblyComplete;
    public UnityEvent onDisassemblyComplete;

    public Phase CurrentPhase { get; private set; }
    public int CurrentStep { get; private set; }

    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly Color OkColor = new Color(0.35f, 1f, 0.45f);
    static readonly Color WrongColor = new Color(1f, 0.4f, 0.35f);

    Material ghostInstance;
    Color ghostBaseColor;
    float startTime;
    float assemblyTime;
    float disassemblyStart;
    int removeIndex;
    Coroutine feedbackRoutine;

    void Start()
    {
        if (!Initialize()) return;
        BeginAssembly();
    }

    /// <summary>Builds ghosts, links parts and creates beeps. Safe to call more than once.</summary>
    bool Initialize()
    {
        if (ghostInstance != null) return true;
        if (ghostMaterial == null)
        {
            Debug.LogError("AssemblyManager: assign a Ghost Material in the Inspector.", this);
            return false;
        }

        // Runtime copy so pulsing never changes the material asset on disk
        ghostInstance = new Material(ghostMaterial) { name = ghostMaterial.name + " (" + objectName + ")" };
        ghostBaseColor = ghostInstance.HasProperty(BaseColorId)
            ? ghostInstance.GetColor(BaseColorId)
            : new Color(0.3f, 0.9f, 1f, 0.45f);

        foreach (var part in steps)
        {
            part.manager = this;
            part.BuildGhost(ghostInstance);
        }

        if (generateBeepsIfEmpty && Application.isPlaying)
        {
            if (!audioSource) audioSource = gameObject.AddComponent<AudioSource>();
            if (!snapClip) snapClip = MakeBeep("snap", 880f, 880f, 0.12f);
            if (!wrongClip) wrongClip = MakeBeep("wrong", 240f, 180f, 0.3f);
            if (!completeClip) completeClip = MakeBeep("complete", 660f, 1320f, 0.6f);
        }
        return true;
    }

    static AudioClip MakeBeep(string clipName, float freqStart, float freqEnd, float duration)
    {
        const int rate = 44100;
        int n = (int)(rate * duration);
        var data = new float[n];
        float phase = 0f;
        for (int i = 0; i < n; i++)
        {
            float k = i / (float)n;
            float f = Mathf.Lerp(freqStart, freqEnd, k);
            phase += 2f * Mathf.PI * f / rate;
            float envelope = Mathf.Min(1f, i / 400f) * (1f - k);
            data[i] = Mathf.Sin(phase) * 0.3f * envelope;
        }
        var clip = AudioClip.Create(clipName, n, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    // ---------------- Assembly ----------------

    void BeginAssembly()
    {
        CurrentPhase = Phase.Assembly;
        CurrentStep = 0;
        removeIndex = steps.Count - 1;
        startTime = Time.time;
        if (marker) marker.gameObject.SetActive(true);
        SetFeedback("", Color.white, 0f);
        ShowCurrentStep();
    }

    void ShowCurrentStep()
    {
        foreach (var p in steps) p.SetGhostVisible(false);
        UpdateChecklist();

        if (CurrentStep >= steps.Count)
        {
            CompleteAssembly();
            return;
        }

        var part = steps[CurrentStep];
        part.SetGhostVisible(true);

        if (stepText) stepText.text = $"Assembly: Step {CurrentStep + 1} / {steps.Count}";
        if (instructionText)
            instructionText.text = $"Pick up the <b>{part.displayName}</b>\nand place it on the glowing outline";
    }

    /// <summary>True if the part (not the one due now) was dropped onto the slot of the current step.</summary>
    public bool IsNearCurrentSlot(AssemblyPart part)
    {
        if (CurrentPhase != Phase.Assembly || CurrentStep >= steps.Count) return false;
        var expected = steps[CurrentStep];
        return part != expected && expected.target != null &&
               Vector3.Distance(part.transform.position, expected.target.position) <= expected.snapDistance;
    }

    /// <summary>Called by a part when it is released near its own slot (or the current slot).</summary>
    public void TryPlace(AssemblyPart part)
    {
        if (CurrentPhase != Phase.Assembly || CurrentStep >= steps.Count) return;

        var expected = steps[CurrentStep];
        if (part == expected)
        {
            part.SnapIntoPlace();
            Play(snapClip);
            SetFeedback($"{part.displayName} attached!", OkColor, 1.5f);
            CurrentStep++;
            ShowCurrentStep();
        }
        else
        {
            Play(wrongClip);
            SetFeedback($"Not yet! First place the {expected.displayName}", WrongColor, 2.5f);
        }
    }

    void CompleteAssembly()
    {
        assemblyTime = Time.time - startTime;
        ShowAssemblyCompleteText();
        if (marker) marker.gameObject.SetActive(false);
        Play(completeClip);
        onAssemblyComplete?.Invoke();

        if (enableDisassembly) StartCoroutine(BeginDisassemblyAfter(disassemblyDelay));
        else CurrentPhase = Phase.Finished;
    }

    void ShowAssemblyCompleteText()
    {
        if (stepText) stepText.text = $"{objectName} assembled!";
        if (instructionText)
        {
            instructionText.text = $"All {steps.Count} parts placed in {FormatTime(assemblyTime)}";
            if (enableDisassembly) instructionText.text += "\nGet ready to take it apart...";
        }
    }

    // ---------------- Disassembly (Bonus 2) ----------------

    IEnumerator BeginDisassemblyAfter(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        CurrentPhase = Phase.Disassembly;
        removeIndex = steps.Count - 1;
        disassemblyStart = Time.time;
        if (marker) marker.gameObject.SetActive(true);
        ShowRemovalStep();
    }

    void ShowRemovalStep()
    {
        UpdateChecklist();

        if (removeIndex < 0)
        {
            CompleteDisassembly();
            return;
        }

        var part = steps[removeIndex];
        part.EnableForRemoval();

        int n = steps.Count - removeIndex;
        if (stepText) stepText.text = $"Disassembly: Step {n} / {steps.Count}";
        if (instructionText)
            instructionText.text = $"Reverse order: remove the <b>{part.displayName}</b>\nand pull it away from the object";
    }

    /// <summary>Called by a part once it has been pulled out of its slot.</summary>
    public void OnPartRemoved(AssemblyPart part)
    {
        if (CurrentPhase != Phase.Disassembly || removeIndex < 0 || part != steps[removeIndex]) return;

        Play(snapClip);
        SetFeedback($"{part.displayName} removed!", OkColor, 1.5f);
        removeIndex--;
        ShowRemovalStep();
    }

    void CompleteDisassembly()
    {
        CurrentPhase = Phase.Finished;
        float t = Time.time - disassemblyStart;
        if (stepText) stepText.text = "Disassembly complete!";
        if (instructionText)
            instructionText.text = $"Assembly: {FormatTime(assemblyTime)}   Disassembly: {FormatTime(t)}\nPress Reset to build it again";
        if (marker) marker.gameObject.SetActive(false);
        Play(completeClip);
        onDisassemblyComplete?.Invoke();
    }

    /// <summary>Puts every part back where it started. Hooked to the panel's Reset button.</summary>
    public void ResetAssembly()
    {
        StopAllCoroutines();
        feedbackRoutine = null;
        foreach (var p in steps) p.ResetPart();
        BeginAssembly();
    }

    // ---------------- Per-frame cues ----------------

    void Update()
    {
        if (ghostInstance == null) return;
        UpdateCues(Time.time, Time.deltaTime);
    }

    void UpdateCues(float time, float deltaTime)
    {
        AssemblyPart focusPart = null;
        bool pointAtSlot = false;

        if (CurrentPhase == Phase.Assembly && CurrentStep < steps.Count)
        {
            focusPart = steps[CurrentStep];
            pointAtSlot = focusPart.IsHeld && focusPart.Ghost != null;

            if (ghostInstance.HasProperty(BaseColorId))
            {
                Color c;
                if (focusPart.IsHeld && focusPart.IsNearTarget())
                {
                    // Close enough: release now to snap
                    c = ghostInRangeColor;
                }
                else
                {
                    // Pulse the ghost's transparency
                    c = ghostBaseColor;
                    float k = (Mathf.Sin(time * ghostPulseSpeed) + 1f) * 0.5f;
                    c.a = Mathf.Lerp(0.12f, ghostBaseColor.a, k);
                }
                ghostInstance.SetColor(BaseColorId, c);
            }
        }
        else if (CurrentPhase == Phase.Disassembly && removeIndex >= 0)
        {
            focusPart = steps[removeIndex];
        }

        if (marker && focusPart != null)
        {
            GameObject focus = pointAtSlot ? focusPart.Ghost : focusPart.gameObject;
            Vector3 top = TopOf(focus);
            float bob = Mathf.Sin(time * 4f) * 0.015f;
            marker.position = top + Vector3.up * (markerHeight + bob);
            marker.Rotate(0f, 90f * deltaTime, 0f, Space.World);
        }
    }

    void UpdateChecklist()
    {
        if (!checklistText) return;
        var sb = new StringBuilder();
        bool disassembling = CurrentPhase == Phase.Disassembly || (CurrentPhase == Phase.Finished && enableDisassembly);

        sb.AppendLine(disassembling ? "<b>Disassembly order</b>" : "<b>Assembly order</b>");
        for (int k = 0; k < steps.Count; k++)
        {
            int i = disassembling ? steps.Count - 1 - k : k;
            string line = $"{k + 1}. {steps[i].displayName}";
            bool done = disassembling ? i > removeIndex : i < CurrentStep;
            bool current = disassembling ? i == removeIndex : i == CurrentStep;

            if (done) sb.AppendLine($"<color=#5EE67A>{line}  (done)</color>");
            else if (current) sb.AppendLine($"<color=#FFD84A><b>> {line}</b></color>");
            else sb.AppendLine($"<color=#8A8A8A>{line}</color>");
        }
        checklistText.text = sb.ToString();
    }

    void SetFeedback(string msg, Color color, float duration)
    {
        if (!feedbackText) return;
        if (feedbackRoutine != null) StopCoroutine(feedbackRoutine);
        feedbackRoutine = null;
        feedbackText.text = msg;
        feedbackText.color = color;
        if (duration > 0f) feedbackRoutine = StartCoroutine(ClearFeedbackAfter(duration));
    }

    IEnumerator ClearFeedbackAfter(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        feedbackText.text = "";
        feedbackRoutine = null;
    }

    void Play(AudioClip clip)
    {
        if (audioSource && clip) audioSource.PlayOneShot(clip);
    }

    static string FormatTime(float t) => $"{(int)(t / 60f)}:{(int)(t % 60f):00}";

    static Vector3 TopOf(GameObject go)
    {
        var rends = go.GetComponentsInChildren<Renderer>();
        if (rends.Length == 0) return go.transform.position;
        Bounds b = rends[0].bounds;
        for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
        return new Vector3(b.center.x, b.max.y, b.center.z);
    }

    // ---------------- Editor preview (screenshots) ----------------

    /// <summary>
    /// Editor-only helper for the screenshot tool: jumps straight to a state of the task without
    /// playing through it. placedCount parts are snapped in; if removedCount >= 0 the object is in
    /// disassembly with that many parts already pulled off (dropped back at their table spots).
    /// The timer values shown on the panel come from previewSeconds.
    /// </summary>
    public void PreviewState(int placedCount, int removedCount, float previewSeconds)
    {
        if (!Initialize()) return;

        var startPoses = new List<Pose>();
        foreach (var p in steps) startPoses.Add(new Pose(p.transform.position, p.transform.rotation));

        placedCount = Mathf.Clamp(placedCount, 0, steps.Count);
        for (int i = 0; i < placedCount; i++) steps[i].PlaceAtTarget();
        if (marker) marker.gameObject.SetActive(true);
        assemblyTime = previewSeconds;

        if (removedCount < 0)
        {
            CurrentPhase = Phase.Assembly;
            CurrentStep = placedCount;
            removeIndex = steps.Count - 1;
            if (CurrentStep >= steps.Count)
            {
                UpdateChecklist();
                ShowAssemblyCompleteText();
                if (marker) marker.gameObject.SetActive(false);
                SetFeedback("", Color.white, 0f);
                return;
            }
            ShowCurrentStep();
            if (placedCount > 0) SetFeedback($"{steps[placedCount - 1].displayName} attached!", OkColor, 0f);
        }
        else
        {
            CurrentPhase = Phase.Disassembly;
            CurrentStep = steps.Count;
            removedCount = Mathf.Clamp(removedCount, 0, steps.Count - 1);
            removeIndex = steps.Count - 1 - removedCount;
            for (int i = steps.Count - 1; i > removeIndex; i--)
                steps[i].MarkRemovedAt(startPoses[i].position, startPoses[i].rotation);
            ShowRemovalStep();
            if (removedCount > 0) SetFeedback($"{steps[removeIndex + 1].displayName} removed!", OkColor, 0f);
        }

        UpdateCues(0.4f, 0f);
    }
}
