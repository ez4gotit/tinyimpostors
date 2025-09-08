// Assets/Impostors/Editor/ImpostorBakerArray.cs
// Bake OCTA impostors into Texture2DArray assets (solves 16k atlas limit)
// Requires the bake replacement shaders: Hidden/TI/BakeColor, Hidden/TI/BakeNormal, Hidden/TI/BakeDepth
// Runtime shader: Impostors/OctaImpostorArray
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public class ImpostorBakerArray : EditorWindow
{
    [MenuItem("Tools/Impostors/Baker (Array)")]
    static void Open() => GetWindow<ImpostorBakerArray>("Impostor Baker (Array)");

    [SerializeField] MeshRenderer targetRenderer;
    [SerializeField] int tiles = 8;            // NxN
    [SerializeField] int tileRes = 1024;       // per-slice resolution
    [SerializeField] int proxySubdivs = 1;     // 0..3
    [SerializeField] float bakeFOV = 40f;
    [SerializeField] float distMul = 2.0f;
    [SerializeField] bool createProxy = true;

    const string SH_BAKE_COLOR = "Hidden/TI/BakeColor";
    const string SH_BAKE_NORMAL = "Hidden/TI/BakeNormal";
    const string SH_BAKE_DEPTH = "Hidden/TI/BakeDepth";
    const string SH_RUNTIME = "Impostors/OctaImpostorArray";

    void OnGUI()
    {
        targetRenderer = (MeshRenderer)EditorGUILayout.ObjectField("Target Renderer", targetRenderer, typeof(MeshRenderer), true);
        tiles = Mathf.Max(2, EditorGUILayout.IntField("Tiles (NxN)", tiles));
        tileRes = Mathf.Clamp(EditorGUILayout.IntField("Tile Resolution", tileRes), 64, 8192);
        proxySubdivs = Mathf.Clamp(EditorGUILayout.IntField("Proxy Subdivs", proxySubdivs), 0, 3);
        bakeFOV = Mathf.Clamp(EditorGUILayout.FloatField("Bake FOV (deg)", bakeFOV), 5f, 90f);
        distMul = Mathf.Max(1.01f, EditorGUILayout.FloatField("Bake Distance Mult", distMul));
        createProxy = EditorGUILayout.Toggle("Create Proxy", createProxy);

        EditorGUILayout.Space();
        if (GUILayout.Button("Bake OCTA (Texture2DArray)"))
        {
            if (!targetRenderer) { Debug.LogError("[Impostors] Assign a MeshRenderer."); return; }
            BakeOctaArray();
        }
    }

    void BakeOctaArray()
    {
        var target = targetRenderer.gameObject;
        var bounds = targetRenderer.bounds;
        Vector3 centerWS = bounds.center;
        float radius = bounds.extents.magnitude;

        // camera
        var camGO = new GameObject("__ImpostorBakeCam__");
        var cam = camGO.AddComponent<Camera>();
        cam.enabled = false;
        cam.orthographic = false;
        cam.fieldOfView = bakeFOV;
        cam.allowMSAA = false;
        cam.useOcclusionCulling = false;
        cam.cullingMask = ~0; // replacement shaders will cull via tag

        int layers = tiles * tiles;

        // tile RTs
        var tileColor = NewTileRT(tileRes, RenderTextureReadWrite.sRGB);
        var tileLinear = NewTileRT(tileRes, RenderTextureReadWrite.Linear);

        // arrays (simple RGBA32; normals packed to rgb, depth to r)
        var arrColor = new Texture2DArray(tileRes, tileRes, layers, TextureFormat.RGBA32, false, false);
        var arrNormal = new Texture2DArray(tileRes, tileRes, layers, TextureFormat.RGBA32, false, true);
        var arrDepth = new Texture2DArray(tileRes, tileRes, layers, TextureFormat.RGBA32, false, true);
        ConfigureArray(arrColor);
        ConfigureArray(arrNormal);
        ConfigureArray(arrDepth);

        int layer = 0;
        for (int ty = 0; ty < tiles; ty++)
            for (int tx = 0; tx < tiles; tx++, layer++)
            {
                // view dir for this tile (object-space octahedral)
                float u = (tx + 0.5f) / tiles;
                float v = (ty + 0.5f) / tiles;
                Vector3 dirOS = OctaDecode(u, v);
                Vector3 dirWS = targetRenderer.transform.TransformDirection(dirOS).normalized;

                float dist = radius * distMul;
                Vector3 pos = centerWS - dirWS * dist;

                cam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(dirWS, Vector3.up));
                cam.nearClipPlane = Mathf.Max(0.01f, dist - radius * 2.0f);
                cam.farClipPlane = dist + radius * 2.5f;

                // COLOR — transparent background
                cam.targetTexture = tileColor;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0, 0, 0, 0);
                cam.RenderWithShader(Shader.Find(SH_BAKE_COLOR), "");
                CopyTileToArraySlice(tileColor, arrColor, layer, false); // sRGB

                // NORMAL — sky normal background
                cam.targetTexture = tileLinear;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.5f, 0.5f, 1f, 0);
                cam.RenderWithShader(Shader.Find(SH_BAKE_NORMAL), "");
                CopyTileToArraySlice(tileLinear, arrNormal, layer, true);

                // DEPTH — clear to WHITE (far = 1.0)
                cam.targetTexture = tileLinear;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.white;
                cam.RenderWithShader(Shader.Find(SH_BAKE_DEPTH), "");
                CopyTileToArraySlice(tileLinear, arrDepth, layer, true);
            }

        // Save assets
        string folder = EnsureFolder($"Assets/ImpostorBakes/{target.name}");
        string pC = $"{folder}/{target.name}_OI_ColorArray.asset";
        string pN = $"{folder}/{target.name}_OI_NormalArray.asset";
        string pD = $"{folder}/{target.name}_OI_DepthArray.asset";
        CreateOrReplaceAsset(arrColor, pC);
        CreateOrReplaceAsset(arrNormal, pN);
        CreateOrReplaceAsset(arrDepth, pD);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // Material (array shader)
        var sh = Shader.Find(SH_RUNTIME);
        if (!sh) { Debug.LogError("[Impostors] Runtime shader not found: " + SH_RUNTIME); return; }

        var mat = new Material(sh) { name = $"{target.name}_OI_Mat" };
        mat.SetTexture("_ColorArray", AssetDatabase.LoadAssetAtPath<Texture2DArray>(pC));
        mat.SetTexture("_NormalArray", AssetDatabase.LoadAssetAtPath<Texture2DArray>(pN));
        mat.SetTexture("_DepthArray", AssetDatabase.LoadAssetAtPath<Texture2DArray>(pD));
        mat.SetInt("_Tiles", tiles);
        mat.SetInt("_TileRes", tileRes);
        mat.SetFloat("_Cutoff", 0.05f);
        mat.SetFloat("_OpacityBoost", 2.0f);
        mat.SetFloat("_DepthBG", 0.9995f);
        string matPath = $"{folder}/{mat.name}.mat";
        AssetDatabase.CreateAsset(mat, matPath);

        // Proxy (mesh saved once)
        if (createProxy)
        {
            var proxy = new GameObject($"{target.name}_OI_Proxy");
            proxy.transform.position = bounds.center;
            proxy.transform.rotation = target.transform.rotation;

            var mf = proxy.AddComponent<MeshFilter>();
            var mr = proxy.AddComponent<MeshRenderer>();
            var runtimeMesh = BuildIcoSphere(proxySubdivs);
            var savedMesh = SaveMeshIfMissing(folder, target.name, runtimeMesh);
            mf.sharedMesh = savedMesh;
            DestroyImmediate(runtimeMesh);
            mr.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(matPath);

            var fitter = proxy.AddComponent<ImpostorProxyAutoFit>();
            fitter.Target = target.transform;
        }

        DestroyImmediate(camGO);
        tileColor.Release(); tileLinear.Release();

        Debug.Log("[Impostors] OCTA array bake complete.");
    }

    // ---------- helpers ----------
    static RenderTexture NewTileRT(int res, RenderTextureReadWrite rw)
    {
        var rt = new RenderTexture(res, res, 24, RenderTextureFormat.ARGB32, rw)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            antiAliasing = 1,
            useMipMap = false,
            autoGenerateMips = false
        };
        rt.Create(); return rt;
    }
    static void ConfigureArray(Texture2DArray arr)
    {
        arr.wrapMode = TextureWrapMode.Clamp;
        arr.filterMode = FilterMode.Bilinear;
        arr.Apply(false, false);
    }
    static void CreateOrReplaceAsset(Object obj, string path)
    {
        var existing = AssetDatabase.LoadAssetAtPath<Object>(path);
        if (existing) AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(obj, path);
    }
    static string EnsureFolder(string path)
    {
        if (!AssetDatabase.IsValidFolder("Assets/ImpostorBakes"))
            AssetDatabase.CreateFolder("Assets", "ImpostorBakes");
        var rel = path.Substring("Assets/".Length);
        string cur = "Assets";
        foreach (var p in rel.Split('/'))
        {
            if (string.IsNullOrEmpty(p)) continue;
            string next = $"{cur}/{p}";
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(cur, p);
            cur = next;
        }
        return path;
    }
    static Vector3 OctaDecode(float u, float v)
    {
        float x = u * 2f - 1f, y = v * 2f - 1f;
        Vector3 n = new(x, y, 1f - Mathf.Abs(x) - Mathf.Abs(y));
        if (n.z < 0) n = new(
            (1 - Mathf.Abs(n.y)) * Mathf.Sign(n.x),
            (1 - Mathf.Abs(n.x)) * Mathf.Sign(n.y), 0);
        return n.normalized;
    }

    // GPU copy when possible, else robust CPU fallback (fixes "white arrays")
    static Texture2D s_ScratchSrgb;   // color
    static Texture2D s_ScratchLinear; // normal/depth
    static void CopyTileToArraySlice(RenderTexture srcRT, Texture2DArray dst, int layer, bool linear)
    {
        bool canGPUCopy = (SystemInfo.copyTextureSupport & CopyTextureSupport.Basic) != 0
                       && (SystemInfo.copyTextureSupport & CopyTextureSupport.DifferentTypes) != 0
                       && srcRT.antiAliasing <= 1;

        if (canGPUCopy)
        {
            try { Graphics.CopyTexture(srcRT, 0, 0, dst, layer, 0); return; }
            catch { /* fall back */ }
        }

        var bak = RenderTexture.active;
        RenderTexture.active = srcRT;

        if (linear)
        {
            if (s_ScratchLinear == null || s_ScratchLinear.width != srcRT.width || s_ScratchLinear.height != srcRT.height)
            {
                if (s_ScratchLinear != null) Object.DestroyImmediate(s_ScratchLinear);
                s_ScratchLinear = new Texture2D(srcRT.width, srcRT.height, TextureFormat.RGBA32, false, true);
            }
            s_ScratchLinear.ReadPixels(new Rect(0, 0, srcRT.width, srcRT.height), 0, 0, false);
            s_ScratchLinear.Apply(false, false);
            Graphics.CopyTexture(s_ScratchLinear, 0, 0, dst, layer, 0);
        }
        else
        {
            if (s_ScratchSrgb == null || s_ScratchSrgb.width != srcRT.width || s_ScratchSrgb.height != srcRT.height)
            {
                if (s_ScratchSrgb != null) Object.DestroyImmediate(s_ScratchSrgb);
                s_ScratchSrgb = new Texture2D(srcRT.width, srcRT.height, TextureFormat.RGBA32, false, false);
            }
            s_ScratchSrgb.ReadPixels(new Rect(0, 0, srcRT.width, srcRT.height), 0, 0, false);
            s_ScratchSrgb.Apply(false, false);
            Graphics.CopyTexture(s_ScratchSrgb, 0, 0, dst, layer, 0);
        }

        RenderTexture.active = bak;
    }

    // Save procedural mesh as an asset once and reuse
    static Mesh SaveMeshIfMissing(string folderPath, string baseName, Mesh source)
    {
        string assetPath = $"{folderPath}/{baseName}_OI_ProxyMesh.asset";
        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
        if (existing != null) return existing;
        var copy = Object.Instantiate(source);
        copy.name = $"{baseName}_OI_ProxyMesh";
        AssetDatabase.CreateAsset(copy, assetPath);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(assetPath);
        return copy;
    }

    // Icosphere proxy
    static Mesh BuildIcoSphere(int subdivisions)
    {
        float t = (1f + Mathf.Sqrt(5f)) / 2f;
        var verts = new System.Collections.Generic.List<Vector3>{
            new(-1,  t,  0), new( 1,  t,  0), new(-1, -t,  0), new( 1, -t,  0),
            new( 0, -1,  t), new( 0,  1,  t), new( 0, -1, -t), new( 0,  1, -t),
            new( t,  0, -1), new( t,  0,  1), new(-t,  0, -1), new(-t,  0,  1)
        };
        int[] faces = {
            0,11,5,0,5,1,0,1,7,0,7,10,0,10,11,1,5,9,5,11,4,11,10,2,10,7,6,7,1,8,
            3,9,4,3,4,2,3,2,6,3,6,8,3,8,9,4,9,5,2,4,11,6,2,10,8,6,7,9,8,1
        };
        var v = new System.Collections.Generic.List<Vector3>();
        var idx = new System.Collections.Generic.List<int>();
        var mid = new System.Collections.Generic.Dictionary<long, int>();
        int Add(Vector3 p) { p.Normalize(); v.Add(p * 0.5f); return v.Count - 1; }
        foreach (var p in verts) Add(p);
        int Mid(int a, int b)
        {
            long key = ((long)Mathf.Min(a, b) << 32) + Mathf.Max(a, b);
            if (mid.TryGetValue(key, out var m)) return m;
            m = v.Count; v.Add(((v[a] + v[b]) * 0.5f).normalized * 0.5f); mid[key] = m; return m;
        }
        var tris = new System.Collections.Generic.List<(int, int, int)>();
        for (int i = 0; i < faces.Length; i += 3) tris.Add((faces[i], faces[i + 1], faces[i + 2]));
        for (int s = 0; s < subdivisions; s++)
        {
            var n = new System.Collections.Generic.List<(int, int, int)>();
            foreach (var (a, b, c) in tris)
            {
                int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                n.Add((a, ab, ca)); n.Add((b, bc, ab)); n.Add((c, ca, bc)); n.Add((ab, bc, ca));
            }
            tris = n;
        }
        foreach (var (a, b, c) in tris) { idx.Add(a); idx.Add(b); idx.Add(c); }
        var m = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        m.SetVertices(v); m.SetTriangles(idx, 0); m.RecalculateNormals(); return m;
    }
}
