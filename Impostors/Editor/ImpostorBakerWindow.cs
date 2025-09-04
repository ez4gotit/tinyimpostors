// Assets/Impostors/Editor/ImpostorBakerWindow.cs
// Unity 2021.3+  |  Built-in/URP/HDRP friendly (no pipeline hooks required)
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public class ImpostorBakerWindow : EditorWindow
{
    enum BakeMode { TrueImpostor, Octahedral }

    [MenuItem("Tools/Impostors Baker")]
    private static void Open() => GetWindow<ImpostorBakerWindow>("Impostors Baker");

    // Input
    SerializedObject so;
    BakeMode mode = BakeMode.TrueImpostor;
    MeshRenderer targetRenderer;
    int trueImpResolution = 512;        // per-texture (color/normal/depth RG)
    int octaTileCount = 8;              // N x N tiles (quantized octa mapping)
    int tileResolution = 256;           // per-tile resolution
    int octaPadding = 2;                // atlas gutter
    int raymarchSteps = 32;             // runtime TI shader default
    int binarySearchSteps = 4;          // runtime TI shader default
    Shader bakeFrontShader, bakeBackShader;
    Material matBakeFront, matBakeBack;

    // Material creation
    Shader tiShader, oiShader;
    Shader bakeDepthShader, bakeGBufferShader;

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
                new[] { "4 x 4", "6 x 6", "8 x 8", "10 x 10" }, new[] { 4, 6, 8, 10 });
            tileResolution = EditorGUILayout.IntPopup("Per-Tile Resolution", tileResolution,
                new[] { "128", "256", "512" }, new[] { 128, 256, 512 });
            octaPadding = EditorGUILayout.IntSlider("Tile Padding (px)", octaPadding, 0, 8);

            if (GUILayout.Button("Bake Octahedral Impostor"))
                BakeOctaImpostor();
        }
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

        // Whoever reaad this DO NOT add a MeshRenderer here — the baker does that right after calling this.
        return go;
    }

    #region TRUE IMPOSTOR BAKE
    void BakeTrueImpostor()
    {
        var go = targetRenderer.gameObject;
        var mf = go.GetComponent<MeshFilter>();
        if (!mf || !mf.sharedMesh) { Debug.LogError("No MeshFilter/Mesh."); return; }

        // Bounds in object space
        var meshBounds = mf.sharedMesh.bounds;
        var worldBounds = TransformBounds(meshBounds, go.transform.localToWorldMatrix);

        // Create bake camera (orthographic, looks down impostor W axis)
        SetupBakeCamera();
        bakeCam.orthographic = true;
        float extent = Mathf.Max(worldBounds.extents.x, worldBounds.extents.y, worldBounds.extents.z);
        bakeCam.orthographicSize = extent;
        bakeCam.transform.position = worldBounds.center + Vector3.back * (extent * 2f);
        bakeCam.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
        bakeCam.nearClipPlane = 0.0f;
        bakeCam.farClipPlane = extent * 4f;

        // Make temporary RTs (MRT if supported)
        ReleaseRTs();
        rt0 = NewRT(trueImpResolution, trueImpResolution, RenderTextureFormat.ARGB32); // color
        rt1 = NewRT(trueImpResolution, trueImpResolution, RenderTextureFormat.ARGBHalf); // normals
        rt2 = NewRT(trueImpResolution, trueImpResolution, RenderTextureFormat.ARGBHalf); // depth front/back

        if (!matBakeFront) matBakeFront = new Material(bakeFrontShader);
        if (!matBakeBack) matBakeBack = new Material(bakeBackShader);
        matBakeFront.SetFloat("_Near", bakeCam.nearClipPlane);
        matBakeFront.SetFloat("_Far", bakeCam.farClipPlane);
        matBakeBack.SetFloat("_Near", bakeCam.nearClipPlane);
        matBakeBack.SetFloat("_Far", bakeCam.farClipPlane);


        // Set target renderer as only layer
        int originalLayer = targetRenderer.gameObject.layer;
        const int BakeLayer = 30;
        targetRenderer.gameObject.layer = BakeLayer;
        bakeCam.cullingMask = 1 << BakeLayer;

        // Depth render
        Graphics.SetRenderTarget(rt2);
        GL.Clear(true, true, Color.clear);
        // FRONT: writes R
        DrawRendererWithMaterial(targetRenderer, matBakeFront);

        // BACK: writes G (must keep the depth from the front pass)
        DrawRendererWithMaterial(targetRenderer, matBakeBack);

        // GBuffer-ish (color + world normal)
        // Color into rt0
        Graphics.SetRenderTarget(rt0);
        GL.Clear(true, true, Color.clear);
        matBakeG.SetInt("_OutputMode", 0);
        DrawRendererWithMaterial(targetRenderer, matBakeG);

        // Normal into rt1
        Graphics.SetRenderTarget(rt1);
        GL.Clear(true, true, Color.clear);
        matBakeG.SetInt("_OutputMode", 1);
        DrawRendererWithMaterial(targetRenderer, matBakeG);

        // Readback textures
        var pathBase = GetAssetBasePath(mf.sharedMesh);
        if (!rt0 || !rt1 || !rt2)
        {
            Debug.LogError("[Impostors] One or more bake RTs are null — aborting bake.");
            return;
        }
        var texColor = SaveRTAsTexture(rt0, pathBase + "_TI_Color.png", true);
        var texNormal = SaveRTAsTexture(rt1, pathBase + "_TI_Normal.png", true);
        var texDepth = SaveRTAsTexture(rt2, pathBase + "_TI_DepthRG.png", true);

        var mat = new Material(tiShader);
        mat.name = mf.sharedMesh.name + "_TI_Mat";
        mat.SetTexture("_AlbedoTex", texColor);
        mat.SetTexture("_NormalTex", texNormal);
        mat.SetTexture("_DepthRG", texDepth);
        mat.SetFloat("_Near", bakeCam.nearClipPlane);
        mat.SetFloat("_Far", bakeCam.farClipPlane);
        mat.SetInt("_RaymarchSteps", raymarchSteps);
        mat.SetInt("_BinarySteps", binarySearchSteps);

        //A unique path under Assets/...
        var matPath = AssetDatabase.GenerateUniqueAssetPath(pathBase + "_TI_Mat.mat");
        AssetDatabase.CreateAsset(mat, matPath);


        // Create quad with renderer
        var quad = CreateImpostorQuad(mf.sharedMesh.name + "_TI_Quad", targetRenderer.transform, extent);
        var renderer = quad.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = mat;
        quad.AddComponent<BillboardQuad>();
        quad.AddComponent<ImpostorRenderer>().Mode = ImpostorRenderer.RenderMode.TrueImpostor;

        // restore
        targetRenderer.gameObject.layer = originalLayer;

        Cleanup();
        Debug.Log("TRUE Impostor bake complete.");
    }
    #endregion

    #region OCTAHEDRAL BAKE
    void BakeOctaImpostor()
    {
        var go = targetRenderer.gameObject;
        var mf = go.GetComponent<MeshFilter>();
        if (!mf || !mf.sharedMesh) { Debug.LogError("No MeshFilter/Mesh."); return; }

        int atlasSize = octaTileCount * tileResolution + (octaTileCount + 1) * octaPadding;

        SetupBakeCamera();
        var worldBounds = TransformBounds(mf.sharedMesh.bounds, go.transform.localToWorldMatrix);
        float radius = worldBounds.extents.magnitude;
        bakeCam.orthographic = false;
        bakeCam.fieldOfView = 30f;
        bakeCam.nearClipPlane = 0.01f;
        bakeCam.farClipPlane = radius * 6f;

        ReleaseRTs();
        rt0 = NewRT(tileResolution, tileResolution, RenderTextureFormat.ARGB32);     // albedo
        rt1 = NewRT(tileResolution, tileResolution, RenderTextureFormat.ARGBHalf);   // normal
        rt2 = NewRT(tileResolution, tileResolution, RenderTextureFormat.RHalf);      // depth (linear z)

        if (!matBakeG) matBakeG = new Material(bakeGBufferShader);
        matBakeG.SetInt("_OutputMode", 0);

        int originalLayer = targetRenderer.gameObject.layer;
        const int BakeLayer = 30;
        targetRenderer.gameObject.layer = BakeLayer;
        bakeCam.cullingMask = 1 << BakeLayer;

        // Atlases
        var atlasColor = new Texture2D(atlasSize, atlasSize, TextureFormat.RGBA32, false, true);
        var atlasNormal = new Texture2D(atlasSize, atlasSize, TextureFormat.RGBAHalf, false, true);
        var atlasDepth = new Texture2D(atlasSize, atlasSize, TextureFormat.RHalf, false, true);
        ClearTexture(atlasColor, Color.clear);
        ClearTexture(atlasNormal, new Color(0.5f, 0.5f, 1f, 1f));
        ClearTexture(atlasDepth, new Color(1f, 0, 0, 0)); // far

        // Bake directions using quantized octa mapping
        for (int ty = 0; ty < octaTileCount; ty++)
        {
            for (int tx = 0; tx < octaTileCount; tx++)
            {
                Vector3 dir = DecodeOcta((new Vector2((tx + 0.5f) / octaTileCount, (ty + 0.5f) / octaTileCount)) * 2f - Vector2.one);
                // position camera
                bakeCam.transform.position = worldBounds.center - dir * (radius * 2.0f);
                bakeCam.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);

                // Albedo
                Graphics.SetRenderTarget(rt0);
                GL.Clear(true, true, Color.clear);
                matBakeG.SetInt("_OutputMode", 0);
                DrawRendererWithMaterial(targetRenderer, matBakeG);

                // Normal
                Graphics.SetRenderTarget(rt1);
                GL.Clear(true, true, Color.clear);
                matBakeG.SetInt("_OutputMode", 1);
                DrawRendererWithMaterial(targetRenderer, matBakeG);

                // Depth (linear01)
                Graphics.SetRenderTarget(rt2);
                GL.Clear(true, true, new Color(1,0,0,0));
                // Reuse depth material, write linear depth in R
                if (!matBakeDepth) matBakeDepth = new Material(bakeDepthShader);
                matBakeDepth.SetFloat("_Near", bakeCam.nearClipPlane);
                matBakeDepth.SetFloat("_Far", bakeCam.farClipPlane);
                matBakeDepth.SetFloat("_CullSign", +1); // front only for z
                DrawRendererWithMaterial(targetRenderer, matBakeDepth);

                // Blit into atlas with padding
                int px = tx * tileResolution + (tx + 1) * octaPadding;
                int py = ty * tileResolution + (ty + 1) * octaPadding;

                ReadInto(atlasColor, rt0, px, py);
                ReadInto(atlasNormal, rt1, px, py);
                ReadInto(atlasDepth, rt2, px, py);
            }
        }

        var pathBase = GetAssetBasePath(mf.sharedMesh);
        var texColor = SaveTex(atlasColor, pathBase + "_OI_Color.png");
        var texNormal = SaveTex(atlasNormal, pathBase + "_OI_Normal.png");
        var texDepth = SaveTex(atlasDepth, pathBase + "_OI_Depth.png");

        var mat = new Material(oiShader);
        mat.name = mf.sharedMesh.name + "_OI_Mat";
        mat.SetTexture("_AtlasColor", texColor);
        mat.SetTexture("_AtlasNormal", texNormal);
        mat.SetTexture("_AtlasDepth", texDepth);
        mat.SetInt("_Tiles", octaTileCount);
        mat.SetFloat("_Near", bakeCam.nearClipPlane);
        mat.SetFloat("_Far", bakeCam.farClipPlane);
        mat.SetInt("_TileRes", tileResolution);
        mat.SetInt("_TilePad", octaPadding);

        var matPath = AssetDatabase.GenerateUniqueAssetPath(pathBase + "_OI_Mat.mat");
        AssetDatabase.CreateAsset(mat, matPath);

        var quad = CreateImpostorQuad(mf.sharedMesh.name + "_OI_Quad", targetRenderer.transform, worldBounds.extents.magnitude);
        var renderer = quad.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = mat;
        quad.AddComponent<BillboardQuad>();
        quad.AddComponent<ImpostorRenderer>().Mode = ImpostorRenderer.RenderMode.Octahedral;

        targetRenderer.gameObject.layer = originalLayer;

        Cleanup();
        Debug.Log("Octahedral Impostor bake complete.");
    }
    #endregion

    #region Helpers



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

    static void DrawRendererWithMaterial(Renderer r, Material m)
    {
        for (int i = 0; i < r.sharedMaterials.Length; i++)
            Graphics.DrawMesh(r.GetComponent<MeshFilter>().sharedMesh, r.localToWorldMatrix, m, r.gameObject.layer, null, i);
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


    void ReadInto(Texture2D atlas, RenderTexture tile, int px, int py)
    {
        var prev = RenderTexture.active;
        RenderTexture.active = tile;
        var tmp = new Texture2D(tile.width, tile.height, atlas.format, false, true);
        tmp.ReadPixels(new Rect(0, 0, tile.width, tile.height), 0, 0, false);
        tmp.Apply(false, false);
        RenderTexture.active = prev;
        atlas.SetPixels(px, py, tile.width, tile.height, tmp.GetPixels());
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
