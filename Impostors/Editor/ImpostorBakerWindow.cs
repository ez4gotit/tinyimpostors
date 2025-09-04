// Assets/Impostors/Editor/ImpostorBakerWindow.cs
// Unity 2021.3+  |  Built-in/URP/HDRP friendly (no pipeline hooks required)
using Codice.Utils;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public class ImpostorBakerWindow : EditorWindow
{
    enum BakeMode { TrueImpostor, Octahedral }

    [MenuItem("Tools/Impostors Baker")]
    private static void Open() => GetWindow<ImpostorBakerWindow>("Impostors Baker");


    SerializedObject so;
    BakeMode mode = BakeMode.TrueImpostor;
    MeshRenderer targetRenderer;
    int trueImpResolution = 512;        // per-texture (color/normal/depth RG)
    int octaTileCount = 8;              // N x N tiles (quantized octa mapping)
    int tileResolution = 256;           // per-tile resolution
    int octaPadding = 2;                // atlas gutter
    int raymarchSteps = 32;             // runtime TI shader default
    int binarySearchSteps = 4;
    float bakeFOV;// runtime TI shader default
    bool isPersective;// runtime TI shader default
    Shader bakeFrontShader, bakeBackShader;
    Material matBakeFront, matBakeBack, matBakeLinearDepth;
    enum ProxyMesh { Quad, Octahedron, SphereLow }
    ProxyMesh octaProxy = ProxyMesh.Octahedron;   // default for Octa impostor

    // Material creation
    Shader tiShader, oiShader;
    Shader bakeDepthShader, bakeGBufferShader;
    Shader bakeLinearDepthShader;
    // Scratch
    Camera bakeCam;
    RenderTexture rt0, rt1, rt2;
    Material matBakeDepth, matBakeG;

    void OnEnable()
    {
        tiShader = Shader.Find("Impostors/TrueImpostor");
        oiShader = Shader.Find("Impostors/OctaImpostor");
        bakeDepthShader = Shader.Find("Hidden/TI/BakeDepth");
        bakeGBufferShader = Shader.Find("Hidden/TI/BakeNormalAlbedo");
        bakeFrontShader = Shader.Find("Hidden/TI/BakeDepthFront");
        bakeBackShader = Shader.Find("Hidden/TI/BakeDepthBack");
        bakeLinearDepthShader = Shader.Find("Hidden/TI/BakeLinearDepth");

    }

    void OnGUI()
    {
        EditorGUILayout.Space();
        mode = (BakeMode)EditorGUILayout.EnumPopup("Mode", mode);
        targetRenderer = (MeshRenderer)EditorGUILayout.ObjectField("Target Renderer", targetRenderer, typeof(MeshRenderer), true);

        if (!targetRenderer)
        {
            EditorGUILayout.HelpBox("Assign a MeshRenderer to bake.", MessageType.Info);
            return;
        }

        if (mode == BakeMode.TrueImpostor)
        {
            trueImpResolution = EditorGUILayout.IntPopup("Texture Resolution", trueImpResolution,
                new[] { "256", "512", "1024" }, new[] { 256, 512, 1024 });
            raymarchSteps = EditorGUILayout.IntSlider("Raymarch Steps", raymarchSteps, 8, 96);
            binarySearchSteps = EditorGUILayout.IntSlider("Binary Search Steps", binarySearchSteps, 0, 8);

            if (GUILayout.Button("Bake TRUE Impostor"))
                BakeTrueImpostor();
        }
        else
        {
            EditorGUILayout.LabelField("Atlas Layout", EditorStyles.boldLabel);
            octaTileCount = EditorGUILayout.IntPopup("Tiles (N x N)", octaTileCount,
                new[] { "4 x 4", "6 x 6", "8 x 8", "10 x 10", "12 x 12", "14 x 14", "16 x 16" }, new[] { 4, 6, 8, 10,12,14,16 });
            tileResolution = EditorGUILayout.IntPopup("Per-Tile Resolution", tileResolution,
                new[] { "128", "256", "512", "1024", "2048" }, new[] { 128, 256, 512,1024,2048 });
            octaPadding = EditorGUILayout.IntSlider("Tile Padding (px)", octaPadding, 0, 8);
            octaProxy = (ProxyMesh)EditorGUILayout.EnumPopup("Proxy Mesh", octaProxy);
            bakeFOV = EditorGUILayout.FloatField("FOV", bakeFOV);
            isPersective = EditorGUILayout.Toggle("Is Scan Perspective",isPersective);
            if (GUILayout.Button("Bake Octahedral Impostor"))
                BakeOctaImpostor();
        }
    }
    bool CheckShaders(params Shader[] list)
    {
        foreach (var s in list)
        {
            if (!s) { Debug.LogError("[Impostors] Missing shader: " + s); return false; }
        }
        return true;
    }

    // Creates a camera-facing quad mesh centered at the target transform.
    // 'extent' is used as half-size, so the quad covers the original bounds.
    static GameObject CreateImpostorQuad(string name, Transform reference, float extent)
    {
        var go = new GameObject(name);
        go.transform.SetPositionAndRotation(reference.position, Quaternion.identity);
        go.transform.localScale = Vector3.one;
        if (reference.parent) go.transform.SetParent(reference.parent, true);

        var mf = go.AddComponent<MeshFilter>();

        // Build a Z-facing quad of size (2*extent)
        float s = Mathf.Max(0.001f, extent);
        var verts = new Vector3[]
        {
        new Vector3(-s, -s, 0),
        new Vector3( s, -s, 0),
        new Vector3( s,  s, 0),
        new Vector3(-s,  s, 0),
        };
        var uvs = new Vector2[]
        {
        new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1)
        };
        var tris = new int[] { 0, 1, 2, 0, 2, 3 };

        var mesh = new Mesh();
        mesh.name = name + "_QuadMesh";
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.bounds = new Bounds(Vector3.zero, new Vector3(2 * s, 2 * s, 0.1f));
#if UNITY_EDITOR
        UnityEditor.Undo.RegisterCreatedObjectUndo(go, "Create Impostor Quad");
#endif
        mf.sharedMesh = mesh;

        // Do NOT add a MeshRenderer here — the baker does that right after calling this.
        return go;
    }

    #region TRUE IMPOSTOR BAKE
    void BakeTrueImpostor()
    {
        if (!targetRenderer) { Debug.LogError("[Impostors] No target renderer."); return; }
        var mf = targetRenderer.GetComponent<MeshFilter>();
        if (!mf || !mf.sharedMesh) { Debug.LogError("[Impostors] MeshFilter/Mesh missing."); return; }

        // Required shaders
        var tiShader = Shader.Find("Impostors/TrueImpostor");
        var bakeGBufferShader = Shader.Find("Hidden/TI/BakeNormalAlbedo");
        var bakeFrontShader = Shader.Find("Hidden/TI/BakeDepthFront");
        var bakeBackShader = Shader.Find("Hidden/TI/BakeDepthBack");
        if (!tiShader || !bakeGBufferShader || !bakeFrontShader || !bakeBackShader)
        { Debug.LogError("[Impostors] Missing TI shaders."); return; }

        var worldBounds = TransformBounds(mf.sharedMesh.bounds, targetRenderer.transform.localToWorldMatrix);
        float extent = Mathf.Max(worldBounds.extents.x, worldBounds.extents.y, worldBounds.extents.z);

        // Camera
        SetupBakeCamera();
        bakeCam.orthographic = true;
        bakeCam.orthographicSize = extent;
        bakeCam.transform.position = worldBounds.center + Vector3.back * (extent * 2f);
        bakeCam.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
        bakeCam.nearClipPlane = 0.001f;
        bakeCam.farClipPlane = extent * 4f;

        // Temp layer & cull mask
        const int BakeLayer = 30;
        int originalLayer = targetRenderer.gameObject.layer;
        targetRenderer.gameObject.layer = BakeLayer;
        bakeCam.cullingMask = 1 << BakeLayer;

        // RTs
        ReleaseRTs();
        rt0 = NewRT(trueImpResolution, trueImpResolution, RenderTextureFormat.ARGB32);
        rt1 = NewRT(trueImpResolution, trueImpResolution, RenderTextureFormat.ARGBHalf);
        rt2 = NewRT(trueImpResolution, trueImpResolution, RenderTextureFormat.ARGBHalf);
        if (!rt0 || !rt1 || !rt2) { Debug.LogError("[Impostors] RT alloc failed."); targetRenderer.gameObject.layer = originalLayer; return; }

        // FRONT depth (R)
        bakeCam.targetTexture = rt2;
        GL.Clear(true, true, Color.clear);
        Shader.SetGlobalFloat("_Near", bakeCam.nearClipPlane);
        Shader.SetGlobalFloat("_Far", bakeCam.farClipPlane);
        bakeCam.RenderWithShader(bakeFrontShader, "");

        // BACK depth (G) – requires depth from previous pass, so don't clear depth
        // We only need to keep the RT bound; RenderWithShader will draw into it.
        // (Depth buffer is attached to rt2 because we created it with depth=24.)
        bakeCam.RenderWithShader(bakeBackShader, "");

        // COLOR (albedo)
        bakeCam.targetTexture = rt0;
        GL.Clear(true, true, Color.clear);
        Shader.SetGlobalInt("_OutputMode", 0);
        // Copy base color/texture from source mat to globals so the replacement shader sees them
        var src = targetRenderer.sharedMaterial;
        if (src)
        {
            var tex = src.HasProperty("_MainTex") ? src.GetTexture("_MainTex") :
                      (src.HasProperty("_BaseMap") ? src.GetTexture("_BaseMap") : null);
            var col = src.HasProperty("_Color") ? src.GetColor("_Color") :
                      (src.HasProperty("_BaseColor") ? src.GetColor("_BaseColor") : Color.white);
            Shader.SetGlobalTexture("_MainTex", tex);
            Shader.SetGlobalColor("_Color", col);
        }
        else { Shader.SetGlobalTexture("_MainTex", null); Shader.SetGlobalColor("_Color", Color.white); }
        bakeCam.RenderWithShader(bakeGBufferShader, "");

        // NORMAL (world)
        bakeCam.targetTexture = rt1;
        GL.Clear(true, true, Color.clear);
        Shader.SetGlobalInt("_OutputMode", 1);
        bakeCam.RenderWithShader(bakeGBufferShader, "");

        // Reset
        bakeCam.targetTexture = null;
        targetRenderer.gameObject.layer = originalLayer;

        // Save
        var pathBase = GetAssetBasePath(mf.sharedMesh);
        var texColor = SaveRTAsTexture(rt0, pathBase + "_TI_Color.png", true);
        var texNormal = SaveRTAsTexture(rt1, pathBase + "_TI_Normal.png", true);
        var texDepthRG = SaveRTAsTexture(rt2, pathBase + "_TI_DepthRG.png", true);
        if (!texColor || !texNormal || !texDepthRG) { Debug.LogError("[Impostors] Saving TI textures failed."); return; }

        // Material
        var mat = new Material(tiShader);
        mat.name = mf.sharedMesh.name + "_TI_Mat";
        mat.SetTexture("_AlbedoTex", texColor);
        mat.SetTexture("_NormalTex", texNormal);
        mat.SetTexture("_DepthRG", texDepthRG);
        mat.SetFloat("_Near", bakeCam.nearClipPlane);
        mat.SetFloat("_Far", bakeCam.farClipPlane);
        mat.SetInt("_RaymarchSteps", raymarchSteps);
        mat.SetInt("_BinarySteps", binarySearchSteps);

        var matPath = AssetDatabase.GenerateUniqueAssetPath(pathBase + "_TI_Mat.mat");
        AssetDatabase.CreateAsset(mat, matPath);

        var quad = CreateImpostorQuad(mf.sharedMesh.name + "_TI_Quad", targetRenderer.transform, extent);
        var r = quad.AddComponent<MeshRenderer>(); r.sharedMaterial = mat;
        quad.AddComponent<BillboardQuad>();
        quad.AddComponent<ImpostorRenderer>().Mode = ImpostorRenderer.RenderMode.TrueImpostor;
        var fit = quad.AddComponent<BillboardQuad>();
        fit.target = targetRenderer.transform;
        fit.extentsOS = mf.sharedMesh.bounds.extents;
        Cleanup();
        Debug.Log("[Impostors] TRUE impostor bake complete.");
    }


    #endregion

    #region OCTAHEDRAL BAKE
    void BakeOctaImpostor()
    {
        if (!targetRenderer) { Debug.LogError("[Impostors] No target renderer."); return; }
        var mf = targetRenderer.GetComponent<MeshFilter>();
        if (!mf || !mf.sharedMesh) { Debug.LogError("[Impostors] MeshFilter/Mesh missing."); return; }

        var oiShader = Shader.Find("Impostors/OctaImpostor");
        var bakeGBufferShader = Shader.Find("Hidden/TI/BakeNormalAlbedo");
        var bakeDepthShader = Shader.Find("Hidden/TI/BakeLinearDepth");
        if (!oiShader || !bakeGBufferShader || !bakeDepthShader)
        { Debug.LogError("[Impostors] Missing OI shaders."); return; }

        var worldBounds = TransformBounds(mf.sharedMesh.bounds, targetRenderer.transform.localToWorldMatrix);
        float radius = worldBounds.extents.magnitude;

        SetupBakeCamera();
        bakeCam.orthographic = !isPersective;
        bakeCam.orthographicSize = radius;
        bakeCam.nearClipPlane = 0.01f;
        bakeCam.farClipPlane = radius * 6f;

        const int BakeLayer = 30;
        int originalLayer = targetRenderer.gameObject.layer;
        targetRenderer.gameObject.layer = BakeLayer;
        bakeCam.cullingMask = 1 << BakeLayer;

        ReleaseRTs();
        rt0 = NewRT(tileResolution, tileResolution, RenderTextureFormat.ARGB32);
        rt1 = NewRT(tileResolution, tileResolution, RenderTextureFormat.ARGBHalf);
        rt2 = NewRT(tileResolution, tileResolution, RenderTextureFormat.RHalf);

        int atlasSize = octaTileCount * tileResolution + (octaTileCount + 1) * octaPadding;
        var atlasColor = new Texture2D(atlasSize, atlasSize, TextureFormat.RGBA32, false, true);
        var atlasNormal = new Texture2D(atlasSize, atlasSize, TextureFormat.RGBAHalf, false, true);
        var atlasDepth = new Texture2D(atlasSize, atlasSize, TextureFormat.RHalf, false, true);
        ClearTexture(atlasColor, Color.clear);
        ClearTexture(atlasNormal, new Color(0.5f, 0.5f, 1, 1));
        ClearTexture(atlasDepth, new Color(1, 0, 0, 0));

        // source albedo → globals for replacement shader
        var src = targetRenderer.sharedMaterial;
        if (src)
        {
            var tex = src.HasProperty("_MainTex") ? src.GetTexture("_MainTex") :
                      (src.HasProperty("_BaseMap") ? src.GetTexture("_BaseMap") : null);
            var col = src.HasProperty("_Color") ? src.GetColor("_Color") :
                      (src.HasProperty("_BaseColor") ? src.GetColor("_BaseColor") : Color.white);
            Shader.SetGlobalTexture("_MainTex", tex);
            Shader.SetGlobalColor("_Color", col);
        }
        else { Shader.SetGlobalTexture("_MainTex", null); Shader.SetGlobalColor("_Color", Color.white); }

        Shader.SetGlobalFloat("_Near", bakeCam.nearClipPlane);
        Shader.SetGlobalFloat("_Far", bakeCam.farClipPlane);

        for (int ty = 0; ty < octaTileCount; ty++)
            for (int tx = 0; tx < octaTileCount; tx++)
            {
                Vector2 e = (new Vector2((tx + 0.5f) / octaTileCount, (ty + 0.5f) / octaTileCount)) * 2f - Vector2.one;
                Vector3 dir = DecodeOcta(e);
                // camera settings (perspective)
                bakeCam.orthographic = !isPersective;
                bakeCam.fieldOfView = bakeFOV;            // add a public float bakeFOV = 35f;
                float fovRad = bakeCam.fieldOfView * Mathf.Deg2Rad * 0.5f;
                float distance = (worldBounds.extents.magnitude) / Mathf.Tan(fovRad) * 1.15f; // ~15% padding
                bakeCam.nearClipPlane = 0.01f;
                bakeCam.farClipPlane = distance + worldBounds.extents.magnitude * 4f;

                // per-tile placement (camera looks *towards* center)
                bakeCam.transform.position = worldBounds.center + dir * distance;
                bakeCam.transform.rotation = Quaternion.LookRotation(-dir, Vector3.up);


                // COLOR
                bakeCam.targetTexture = rt0;
                GL.Clear(true, true, new Color(0, 0, 0, 0));
                Shader.SetGlobalInt("_OutputMode", 0);
                bakeCam.RenderWithShader(bakeGBufferShader, "");

                // NORMAL
                bakeCam.targetTexture = rt1;
                GL.Clear(true, true, new Color(0.5f, 0.5f, 1, 0));
                Shader.SetGlobalInt("_OutputMode", 1);
                bakeCam.RenderWithShader(bakeGBufferShader, "");

                // DEPTH (linear01 in R) – background must be white
                bakeCam.targetTexture = rt2;
                GL.Clear(true, true, new Color(1, 0, 0, 0));
                bakeCam.RenderWithShader(bakeDepthShader, "");

                int px = tx * tileResolution + (tx + 1) * octaPadding;
                int py = ty * tileResolution + (ty + 1) * octaPadding;
                ReadInto(atlasColor, rt0, px, py);
                ReadInto(atlasNormal, rt1, px, py);
                ReadInto(atlasDepth, rt2, px, py);




            }

        bakeCam.targetTexture = null;
        targetRenderer.gameObject.layer = originalLayer;

        var pathBase = GetAssetBasePath(mf.sharedMesh);
        var texColor = SaveTex(atlasColor, pathBase + "_OI_Color.png");
        var texNormal = SaveTex(atlasNormal, pathBase + "_OI_Normal.png");
        var texDepth = SaveTex(atlasDepth, pathBase + "_OI_Depth.png");
        if (!texColor || !texNormal || !texDepth) { Debug.LogError("[Impostors] Saving OI textures failed."); return; }

        var mat = new Material(oiShader);
        mat.name = mf.sharedMesh.name + "_OI_Mat";
        mat.SetTexture("_AtlasColor", texColor);
        mat.SetTexture("_AtlasNormal", texNormal);
        mat.SetTexture("_AtlasDepth", texDepth);
        mat.SetInt("_Tiles", octaTileCount);
        mat.SetInt("_TileRes", tileResolution);
        mat.SetInt("_TilePad", octaPadding);
        mat.SetFloat("_Near", bakeCam.nearClipPlane);
        mat.SetFloat("_Far", bakeCam.farClipPlane);
        mat.SetFloat("_Radius", worldBounds.extents.magnitude);
        mat.SetFloat("_BakeDistMul", 2.0f);

        var matPath = AssetDatabase.GenerateUniqueAssetPath(pathBase + "_OI_Mat.mat");
        AssetDatabase.CreateAsset(mat, matPath);

        var proxy = CreateImpostorProxy(mf.sharedMesh.name + "_OI_Proxy", targetRenderer.transform, worldBounds.extents.magnitude, octaProxy);
        var r = proxy.AddComponent<MeshRenderer>(); r.sharedMaterial = mat;
        proxy.AddComponent<ImpostorRenderer>().Mode = ImpostorRenderer.RenderMode.Octahedral;
        var fit = proxy.AddComponent<ProxyAutoFit>();
        fit.target = targetRenderer.transform;
        fit.extentsOS = mf.sharedMesh.bounds.extents;
        Cleanup();
        Debug.Log("[Impostors] OCTA impostor bake complete.");
    }


    #endregion

    #region Helpers
    sealed class TempMats : System.IDisposable
    {
        System.Collections.Generic.List<Object> objs = new();
        public T Track<T>(T obj) where T : Object { objs.Add(obj); return obj; }
        public void Dispose() { foreach (var o in objs) if (o) Object.DestroyImmediate(o); objs.Clear(); }
    }
    // usage: var m = _.Track(new Material(shader));

    static Mesh BuildQuad(float r)
    {
        var m = new Mesh { name = "ImpostorQuad" };
        var s = Mathf.Max(0.001f, r);
        m.SetVertices(new[]{
        new Vector3(-s,-s,0), new Vector3(s,-s,0),
        new Vector3(s,s,0),   new Vector3(-s,s,0)
    });
        m.SetUVs(0, new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) });
        m.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
        m.RecalculateNormals(); m.RecalculateBounds();
        return m;
    }

    static Mesh BuildOctahedron(float r)
    {
        var m = new Mesh { name = "ImpostorOcta" };
        var v0 = new Vector3(r, 0, 0);
        var v1 = new Vector3(-r, 0, 0);
        var v2 = new Vector3(0, r, 0);
        var v3 = new Vector3(0, -r, 0);
        var v4 = new Vector3(0, 0, r);
        var v5 = new Vector3(0, 0, -r);
        var verts = new[] { v0, v1, v2, v3, v4, v5 };
        var tris = new[]{
        2,0,4,  2,4,1,  2,1,5,  2,5,0,   // top half
        3,4,0,  3,1,4,  3,5,1,  3,0,5    // bottom half
    };
        m.SetVertices(verts); m.SetTriangles(tris, 0);
        m.RecalculateNormals(); m.RecalculateBounds();
        return m;
    }

    static Mesh BuildSphereLow(float r, int lon = 12, int lat = 8)
    {
        var m = new Mesh { name = "ImpostorSphereLow" };
        var verts = new System.Collections.Generic.List<Vector3>();
        var tris = new System.Collections.Generic.List<int>();

        for (int y = 0; y <= lat; y++)
        {
            float v = (float)y / lat; float phi = (v - 0.5f) * Mathf.PI;
            for (int x = 0; x <= lon; x++)
            {
                float u = (float)x / lon; float th = u * Mathf.PI * 2f;
                verts.Add(new Vector3(Mathf.Cos(th) * Mathf.Cos(phi), Mathf.Sin(phi), Mathf.Sin(th) * Mathf.Cos(phi)) * r);
            }
        }
        int stride = lon + 1;
        for (int y = 0; y < lat; y++)
        {
            for (int x = 0; x < lon; x++)
            {
                int i0 = y * stride + x, i1 = i0 + 1, i2 = i0 + stride, i3 = i2 + 1;
                tris.AddRange(new[] { i0, i2, i1, i1, i2, i3 });
            }
        }
        m.SetVertices(verts); m.SetTriangles(tris, 0);
        m.RecalculateNormals(); m.RecalculateBounds();
        return m;
    }

    static GameObject CreateImpostorProxy(string name, Transform reference, float extent, ProxyMesh shape)
    {
        var go = new GameObject(name);
        if (reference.parent) go.transform.SetParent(reference.parent, true);
        go.transform.position = reference.position;
        go.transform.rotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;

        var mf = go.AddComponent<MeshFilter>();
        switch (shape)
        {
            case ProxyMesh.Octahedron: mf.sharedMesh = BuildOctahedron(extent); break;
            case ProxyMesh.SphereLow: mf.sharedMesh = BuildSphereLow(extent); break;
            default: mf.sharedMesh = BuildQuad(extent); break;
        }
        return go;
    }


    static Bounds TransformBounds(Bounds b, Matrix4x4 m)
    {
        var c = m.MultiplyPoint(b.center);
        var ext = b.extents;
        var ax = m.MultiplyVector(new Vector3(ext.x, 0, 0));
        var ay = m.MultiplyVector(new Vector3(0, ext.y, 0));
        var az = m.MultiplyVector(new Vector3(0, 0, ext.z));
        var extents = new Vector3(
            Mathf.Abs(ax.x) + Mathf.Abs(ay.x) + Mathf.Abs(az.x),
            Mathf.Abs(ax.y) + Mathf.Abs(ay.y) + Mathf.Abs(az.y),
            Mathf.Abs(ax.z) + Mathf.Abs(ay.z) + Mathf.Abs(az.z));
        return new Bounds(c, extents * 2f);
    }

    void SetupBakeCamera()
    {
        if (!bakeCam)
        {
            var go = new GameObject("~ImpostorBakeCamera");
            go.hideFlags = HideFlags.HideAndDontSave;
            bakeCam = go.AddComponent<Camera>();
            bakeCam.clearFlags = CameraClearFlags.Color;
            bakeCam.backgroundColor = Color.clear;
            bakeCam.enabled = false;
        }
    }

    RenderTexture NewRT(int w, int h, RenderTextureFormat fmt)
    {
        var rt = new RenderTexture(w, h, 24, fmt, RenderTextureReadWrite.Linear);
        rt.hideFlags = HideFlags.HideAndDontSave;
        rt.Create();
        return rt;
    }

    static void DrawRendererWithMaterial(Renderer r, Material m, Camera cam)
    {
        if (!r || !m || !cam) { Debug.LogError("[Impostors] DrawRendererWithMaterial missing r/m/cam."); return; }
        var mf = r.GetComponent<MeshFilter>();
        if (!mf || !mf.sharedMesh) { Debug.LogError("[Impostors] Missing mesh."); return; }

        var mesh = mf.sharedMesh;
        var l2w = r.localToWorldMatrix;
        int sub = mesh.subMeshCount;

        for (int i = 0; i < sub; i++)
            Graphics.DrawMesh(mesh, l2w, m, r.gameObject.layer, cam, i);
        GL.Flush();
    }

    static void ClearTexture(Texture2D tex, Color c)
    {
        var cols = tex.GetPixels();
        for (int i = 0; i < cols.Length; i++) cols[i] = c;
        tex.SetPixels(cols);
        tex.Apply();
    }

    static string SanitizeName(string s)
    {
        if (string.IsNullOrEmpty(s)) return "Object";
        foreach (var c in System.IO.Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s.Replace(' ', '_');
    }

    static string EnsureFolder(string parent, string child)
    {
        parent = parent.Replace('\\', '/'); child = SanitizeName(child);
        var combined = $"{parent}/{child}";
        if (!AssetDatabase.IsValidFolder(combined))
            AssetDatabase.CreateFolder(parent, child);
        return combined;
    }

    // If mesh is a built-in primitive (Library/...), fall back to Assets/ImpostorBakes/<MeshName>/
    static string GetAssetBasePath(Mesh m)
    {
        var meshPath = AssetDatabase.GetAssetPath(m);
        string baseName = SanitizeName(m ? m.name : "Object");

        string dir;
        if (string.IsNullOrEmpty(meshPath) || meshPath.StartsWith("Library"))
        {
            // e.g., built-in Cube/Sphere
            var root = EnsureFolder("Assets", "ImpostorBakes");
            dir = EnsureFolder(root, baseName);
        }
        else
        {
            var folder = System.IO.Path.GetDirectoryName(meshPath)?.Replace('\\', '/') ?? "Assets";
            dir = folder;
            baseName = SanitizeName(System.IO.Path.GetFileNameWithoutExtension(meshPath));
        }

        return $"{dir}/{baseName}";
    }

    Texture2D SaveRTAsTexture(RenderTexture rt, string path, bool linear)
    {
        if (rt == null)
        {
            Debug.LogError($"[Impostors] SaveRTAsTexture: RT is null for '{path}'");
            return null;
        }
        if (!rt.IsCreated())
            rt.Create();

        // Pick a matching TextureFormat for readback
        TextureFormat PickFormat(RenderTextureFormat rtf)
        {
            switch (rtf)
            {
                case RenderTextureFormat.ARGBHalf: return TextureFormat.RGBAHalf;
                case RenderTextureFormat.ARGB32: return TextureFormat.RGBA32;
                case RenderTextureFormat.RHalf: return TextureFormat.RHalf;
                case RenderTextureFormat.RFloat: return TextureFormat.RFloat;
                default: return TextureFormat.RGBA32;
            }
        }

        var prev = RenderTexture.active;
        RenderTexture.active = rt;

        var tex = new Texture2D(rt.width, rt.height, PickFormat(rt.format), /*mipChain*/ false, /*linear*/ linear);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, /*recalculateMipMaps*/ false);
        tex.Apply(false, false);

        RenderTexture.active = prev;

        // Ensure folder exists
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // Write PNG and import
        var png = tex.EncodeToPNG();
        File.WriteAllBytes(path, png);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

        // Try to load the imported asset; if it’s not ready yet, retry once
        Texture2D t = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (t == null)
        {
            AssetDatabase.Refresh();
            t = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // Set basic sampling if the asset is available; otherwise just return the in-memory tex
        if (t != null)
        {
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            EditorUtility.SetDirty(t);
            return t;
        }
        else
        {
            Debug.LogWarning($"[Impostors] Imported texture not yet available at '{path}'. Using in-memory texture for now.");
            return tex;
        }
    }

    Texture2D SaveTex(Texture2D tex, string path)
    {
        if (tex == null)
        {
            Debug.LogError($"[Impostors] SaveTex: input texture is null for '{path}'");
            return null;
        }
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var png = tex.EncodeToPNG();
        File.WriteAllBytes(path, png);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

        Texture2D t = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (t == null)
        {
            AssetDatabase.Refresh();
            t = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        if (t != null)
        {
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            EditorUtility.SetDirty(t);
            return t;
        }
        else
        {
            Debug.LogWarning($"[Impostors] Imported atlas not yet available at '{path}'. Returning in-memory texture.");
            return tex;
        }
    }

    static void ReadInto(Texture2D atlas, RenderTexture rt, int x, int y)
    {
        var prev = RenderTexture.active;
        if (!rt.IsCreated()) rt.Create();
        RenderTexture.active = rt;
        atlas.ReadPixels(new Rect(0, 0, rt.width, rt.height), x, y, false);
        atlas.Apply(false, false);
        RenderTexture.active = prev;
    }


    static Vector3 DecodeOcta(Vector2 e)
    {
        // standard octa decode ([-1,1]^2 → unit vector)
        Vector3 v = new Vector3(e.x, e.y, 1.0f - Mathf.Abs(e.x) - Mathf.Abs(e.y));
        float t = Mathf.Clamp01(-v.z);
        v.x += v.x >= 0 ? -t : t;
        v.y += v.y >= 0 ? -t : t;
        return v.normalized;
    }

    void ReleaseRTs()
    {
        if (rt0) rt0.Release();
        if (rt1) rt1.Release();
        if (rt2) rt2.Release();
        rt0 = rt1 = rt2 = null;
    }

    void Cleanup()
    {
        ReleaseRTs();
        if (bakeCam) DestroyImmediate(bakeCam.gameObject);
        bakeCam = null;
    }
    #endregion
}
