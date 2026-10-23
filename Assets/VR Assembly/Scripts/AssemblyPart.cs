using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// One grabbable part of the assembly.
/// Assembly: when released close to its target slot, it asks the AssemblyManager whether
/// it is this part's turn, and snaps into place if so.
/// Disassembly (Bonus 2): when the manager unlocks it, pulling it far enough from its slot
/// counts as removed.
/// </summary>
[RequireComponent(typeof(XRGrabInteractable))]
public class AssemblyPart : MonoBehaviour
{
    [Tooltip("Name shown in the step instructions, e.g. 'Left Arm'")]
    public string displayName = "Part";

    [Tooltip("Empty object marking where this part belongs in the finished assembly. " +
             "Right-click this component > Create Target At Current Pose to make one.")]
    public Transform target;

    [Tooltip("How close (meters) the part must be released to its slot to snap")]
    public float snapDistance = 0.12f;

    [Tooltip("Max rotation difference (degrees) allowed to snap. 180 = any rotation works")]
    [Range(0f, 180f)] public float snapAngle = 90f;

    [Tooltip("Disassembly: how far (meters) the part must be pulled from its slot to count as removed")]
    public float removeDistance = 0.2f;

    [HideInInspector] public AssemblyManager manager;

    public bool IsPlaced { get; private set; }
    public bool IsRemoved { get; private set; }
    public bool IsHeld => grab != null && grab.isSelected;
    public GameObject Ghost { get; private set; }

    XRGrabInteractable grab;
    Rigidbody rb;
    Vector3 startPos;
    Quaternion startRot;
    bool removable;
    bool throwOnDetachDefault;

    void Awake()
    {
        CacheComponents();
        startPos = transform.position;
        startRot = transform.rotation;
        throwOnDetachDefault = grab.throwOnDetach;
    }

    // Also called from methods the editor screenshot tool uses outside Play mode (no Awake there)
    void CacheComponents()
    {
        if (grab == null) grab = GetComponent<XRGrabInteractable>();
        if (rb == null) rb = GetComponent<Rigidbody>();
    }

    void OnEnable() => grab.selectExited.AddListener(OnReleased);
    void OnDisable() => grab.selectExited.RemoveListener(OnReleased);

    void Update()
    {
        // Disassembly: count as removed once the user pulls it far enough out of its slot
        if (removable && !IsRemoved && IsHeld &&
            Vector3.Distance(transform.position, target.position) > removeDistance)
        {
            IsRemoved = true;
            removable = false;
            manager.OnPartRemoved(this);
        }
    }

    void OnReleased(SelectExitEventArgs args)
    {
        // Ignore releases caused by Reset / the part being disabled
        if (args.isCanceled || manager == null || target == null) return;

        if (IsRemoved)
        {
            // Removed parts fall onto the table like normal objects
            StartCoroutine(MakeDynamicNextFrame());
            return;
        }

        if (removable)
        {
            // Let go before pulling it out far enough: put it back in its slot
            StartCoroutine(ReturnToSlotNextFrame());
            return;
        }

        if (IsPlaced) return;
        // Near its own slot, or dropped onto the slot of the part that is due now (wrong order)
        if (IsNearTarget() || manager.IsNearCurrentSlot(this)) manager.TryPlace(this);
    }

    public bool IsNearTarget()
    {
        float dist = Vector3.Distance(transform.position, target.position);
        float angle = Quaternion.Angle(transform.rotation, target.rotation);
        return dist <= snapDistance && angle <= snapAngle;
    }

    public void SnapIntoPlace()
    {
        IsPlaced = true;
        StartCoroutine(SnapNextFrame());
    }

    IEnumerator SnapNextFrame()
    {
        // Wait one frame: XRI restores the Rigidbody and applies the throw velocity in the
        // LateUpdate after release, which must happen while the body is still non-kinematic.
        yield return null;
        PlaceAtTarget();
    }

    /// <summary>Locks the part in its slot immediately (also used by the editor screenshot tool).</summary>
    public void PlaceAtTarget()
    {
        CacheComponents();
        IsPlaced = true;
        grab.enabled = false;            // placed parts can't be grabbed until disassembly
        rb.isKinematic = true;           // locked in place
        SetPose(target.position, target.rotation);
        SetGhostVisible(false);
    }

    /// <summary>Disassembly: unlock this part so the user can pull it off.</summary>
    public void EnableForRemoval()
    {
        CacheComponents();
        removable = true;
        // The part is kinematic while in its slot, and XRI can't throw a kinematic body on release
        grab.throwOnDetach = false;
        grab.enabled = true;
    }

    /// <summary>Marks the part as pulled out and drops it at the given pose (editor screenshot tool).</summary>
    public void MarkRemovedAt(Vector3 position, Quaternion rotation)
    {
        CacheComponents();
        IsRemoved = true;
        removable = false;
        grab.enabled = true;
        rb.isKinematic = false;
        SetPose(position, rotation);
    }

    IEnumerator MakeDynamicNextFrame()
    {
        yield return null;
        rb.isKinematic = false;
    }

    IEnumerator ReturnToSlotNextFrame()
    {
        yield return null;
        rb.isKinematic = true;
        SetPose(target.position, target.rotation);
    }

    public void ResetPart()
    {
        StopAllCoroutines();
        // Force the hand to let go if the part is being held
        if (grab.isSelected && grab.interactionManager != null)
            grab.interactionManager.CancelInteractableSelection((IXRSelectInteractable)grab);

        IsPlaced = false;
        IsRemoved = false;
        removable = false;
        grab.throwOnDetach = throwOnDetachDefault;
        grab.enabled = true;
        rb.isKinematic = false;
        SetPose(startPos, startRot);
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        SetGhostVisible(false);
    }

    void SetPose(Vector3 position, Quaternion rotation)
    {
        // Move both the Transform and the Rigidbody so interpolation doesn't drag it back
        transform.SetPositionAndRotation(position, rotation);
        rb.position = position;
        rb.rotation = rotation;
    }

    public void SetGhostVisible(bool visible)
    {
        if (Ghost != null) Ghost.SetActive(visible);
    }

    /// <summary>Builds a see-through copy of this part sitting at the target slot.</summary>
    public void BuildGhost(Material ghostMat)
    {
        if (Ghost != null || target == null) return;

        Ghost = new GameObject(name + "_Ghost");
        Ghost.transform.SetParent(target, false);
        Ghost.transform.localPosition = Vector3.zero;
        Ghost.transform.localRotation = Quaternion.identity;
        Vector3 rootScale = transform.lossyScale;
        Vector3 parentScale = target.lossyScale;
        Ghost.transform.localScale = new Vector3(rootScale.x / parentScale.x, rootScale.y / parentScale.y, rootScale.z / parentScale.z);

        foreach (var mf in GetComponentsInChildren<MeshFilter>())
        {
            if (mf.GetComponent<MeshRenderer>() == null) continue;

            var piece = new GameObject(mf.name);
            piece.transform.SetParent(Ghost.transform, false);
            piece.transform.localPosition = transform.InverseTransformPoint(mf.transform.position);
            piece.transform.localRotation = Quaternion.Inverse(transform.rotation) * mf.transform.rotation;
            Vector3 s = mf.transform.lossyScale;
            piece.transform.localScale = new Vector3(s.x / rootScale.x, s.y / rootScale.y, s.z / rootScale.z);

            piece.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
            var r = piece.AddComponent<MeshRenderer>();
            r.sharedMaterial = ghostMat;
            r.shadowCastingMode = ShadowCastingMode.Off;
        }

        Ghost.SetActive(false);
    }

#if UNITY_EDITOR
    [ContextMenu("Create Target At Current Pose")]
    void CreateTargetHere()
    {
        var root = GameObject.Find("Assembly Targets");
        if (root == null)
        {
            root = new GameObject("Assembly Targets");
            UnityEditor.Undo.RegisterCreatedObjectUndo(root, "Create Targets Root");
        }

        var t = new GameObject("Target_" + name).transform;
        UnityEditor.Undo.RegisterCreatedObjectUndo(t.gameObject, "Create Target");
        t.SetParent(root.transform, true);
        t.SetPositionAndRotation(transform.position, transform.rotation);

        UnityEditor.Undo.RecordObject(this, "Assign Target");
        target = t;
        UnityEditor.EditorUtility.SetDirty(this);
    }

    void OnDrawGizmosSelected()
    {
        if (target == null) return;
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(target.position, snapDistance);
        Gizmos.DrawLine(transform.position, target.position);
    }
#endif
}
