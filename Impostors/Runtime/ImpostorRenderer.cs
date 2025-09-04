// Assets/Impostors/Runtime/ImpostorRenderer.cs
using UnityEngine;

[ExecuteAlways]
[RequireComponent(typeof(MeshRenderer), typeof(MeshFilter))]
public class ImpostorRenderer : MonoBehaviour
{
    public enum RenderMode { TrueImpostor, Octahedral }
    public RenderMode Mode = RenderMode.TrueImpostor;

    void OnValidate(){
        var r = GetComponent<MeshRenderer>();
        if (r && r.sharedMaterial)
            r.sharedMaterial.SetFloat("_ImpostorMode", (float)Mode);
    }
}