using UnityEngine;

[ExecuteAlways]
public class BillboardQuad : MonoBehaviour
{
    [Tooltip("Original object we're faking")]
    public Transform target;
    [Tooltip("Object-space half extents (mesh.bounds.extents) of the original mesh")]
    public Vector3 extentsOS;
    [Tooltip("Extra percentage to avoid clipping")]
    [Range(0f, 0.2f)] public float padding = 0.02f;

    void LateUpdate()
    {
        var cam = Camera.main;
        if (!cam || !target) return;

        // Face camera
        var toCam = cam.transform.position - transform.position;
        var W = -toCam.normalized;
        var up = Mathf.Abs(Vector3.Dot(W, Vector3.up)) > 0.99f ? Vector3.right : Vector3.up;
        var U = Vector3.Normalize(Vector3.Cross(up, W));
        var V = Vector3.Normalize(Vector3.Cross(W, U));
        transform.rotation = Quaternion.LookRotation(-W, V);

        // Project AABB to view plane -> exact half-sizes along U and V:
        var w2o = target.worldToLocalMatrix;
        var Uos = (Vector3)w2o.MultiplyVector(U);
        var Vos = (Vector3)w2o.MultiplyVector(V);
        var absU = new Vector3(Mathf.Abs(Uos.x), Mathf.Abs(Uos.y), Mathf.Abs(Uos.z));
        var absV = new Vector3(Mathf.Abs(Vos.x), Mathf.Abs(Vos.y), Mathf.Abs(Vos.z));
        float halfU = Vector3.Dot(absU, extentsOS);
        float halfV = Vector3.Dot(absV, extentsOS);

        float pad = 1f + padding;
        transform.localScale = new Vector3(2f * halfU * pad, 2f * halfV * pad, 1f);
    }
}
