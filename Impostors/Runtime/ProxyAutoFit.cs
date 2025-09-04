// Assets/Impostors/Runtime/ProxyAutoFit.cs
using UnityEngine;

[ExecuteAlways]
public class ProxyAutoFit : MonoBehaviour
{
    public Transform target;
    public Vector3 extentsOS;
    [Range(0f, 0.2f)] public float padding = 0.02f;

    static readonly int _HalfU = Shader.PropertyToID("_HalfU");
    static readonly int _HalfV = Shader.PropertyToID("_HalfV");
    MaterialPropertyBlock mpb;
    Renderer rend;

    void OnEnable() { rend = GetComponent<Renderer>(); if (mpb == null) mpb = new MaterialPropertyBlock(); }

    void LateUpdate()
    {
        var cam = Camera.main;
        if (!cam || !target || !rend) return;

        var center = target.position;
        var viewDir = (cam.transform.position - center).normalized;

        var up = Mathf.Abs(Vector3.Dot(viewDir, Vector3.up)) > 0.99f ? Vector3.right : Vector3.up;
        var U = Vector3.Normalize(Vector3.Cross(up, viewDir));
        var V = Vector3.Normalize(Vector3.Cross(viewDir, U));

        var w2o = target.worldToLocalMatrix;
        var Uos = (Vector3)w2o.MultiplyVector(U);
        var Vos = (Vector3)w2o.MultiplyVector(V);
        var absU = new Vector3(Mathf.Abs(Uos.x), Mathf.Abs(Uos.y), Mathf.Abs(Uos.z));
        var absV = new Vector3(Mathf.Abs(Vos.x), Mathf.Abs(Vos.y), Mathf.Abs(Vos.z));

        float halfU = Vector3.Dot(absU, extentsOS);
        float halfV = Vector3.Dot(absV, extentsOS);
        float r = Mathf.Max(halfU, halfV) * (1f + padding);              // proxy size
        transform.localScale = new Vector3(2f * r, 2f * r, 2f * r);

        // feed exact footprint to shader
        mpb.SetFloat(_HalfU, halfU * (1f + padding));
        mpb.SetFloat(_HalfV, halfV * (1f + padding));
        rend.SetPropertyBlock(mpb);
    }
}
