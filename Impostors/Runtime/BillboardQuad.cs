// Assets/Impostors/Runtime/BillboardQuad.cs
using UnityEngine;

[ExecuteAlways]
public class BillboardQuad : MonoBehaviour
{
    void LateUpdate()
    {
        if (!Camera.main) return;
        var cam = Camera.main.transform;
        var tr = transform;
        tr.rotation = Quaternion.LookRotation((tr.position - cam.position).normalized, Vector3.up);
    }
}
