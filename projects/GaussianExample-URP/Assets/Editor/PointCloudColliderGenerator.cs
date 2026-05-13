using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public class PointCloudColliderGenerator : EditorWindow
{
    const string PrefPrefix = "PointCloudColliderGenerator.";

    enum InputUnit
    {
        Millimeters = 0,
        Meters = 1
    }

    enum MeshingMode
    {
        MarchingCubes = 0,
        GreedyVoxels = 1
    }

    TextAsset m_PointFile;
    InputUnit m_InputUnit = InputUnit.Millimeters;
    float m_InputScale = 1f;

    MeshingMode m_MeshingMode = MeshingMode.MarchingCubes;
    float m_VoxelSizeMeters = 0.05f;
    int m_MinPointsPerVoxel = 3;
    int m_MinNeighborVoxels = 1;
    int m_SmoothIterations = 1;
    float m_IsoLevel = 0.5f;

    bool m_GreedyCloseHoles = true;
    int m_GreedyCloseRadius = 1;
    bool m_GreedyFillEnclosedVoids = true;
    bool m_GreedyGroundGapFill = true;
    int m_GreedyGroundMaxGap = 2;

    bool m_UseBoundsFilter;
    Vector3 m_BoundsMinMeters = new Vector3(-1f, -1f, -1f);
    Vector3 m_BoundsMaxMeters = new Vector3(1f, 1f, 1f);
    bool m_ShowBoundsPreview = true;
    Color m_BoundsPreviewColor = new Color(0.2f, 1f, 0.8f, 1f);

    bool m_AddMeshRenderer = true;
    bool m_LogStats = true;
    string m_DefaultOutputFolder = "Assets/Generated/PointCloudColliders";

    bool m_IsGenerating;
    bool m_CancelRequested;
    float m_Progress01;
    string m_ProgressText = "Idle";

    [MenuItem("Tools/Point Cloud Collider Generator")]
    static void Open()
    {
        var window = GetWindow<PointCloudColliderGenerator>("Point Cloud Collider");
        window.minSize = new Vector2(460f, 520f);
    }

    void OnEnable()
    {
        LoadPrefs();
        SceneView.duringSceneGui += OnSceneGUI;
    }

    void OnDisable()
    {
        SavePrefs();
        SceneView.duringSceneGui -= OnSceneGUI;
    }

    void OnGUI()
    {
        EditorGUI.BeginChangeCheck();

        using (new EditorGUI.DisabledScope(m_IsGenerating))
        {
            EditorGUILayout.LabelField("Input", EditorStyles.boldLabel);
            m_PointFile = (TextAsset)EditorGUILayout.ObjectField("XYZ File", m_PointFile, typeof(TextAsset), false);
            m_InputUnit = (InputUnit)EditorGUILayout.EnumPopup("Input Unit", m_InputUnit);
            m_InputScale = EditorGUILayout.FloatField("Input Scale Multiplier", m_InputScale);
            EditorGUILayout.HelpBox("Expected line format: x y z (space-separated). Tool settings are meters. Input Unit + Scale controls conversion.", MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Meshing", EditorStyles.boldLabel);
            m_MeshingMode = (MeshingMode)EditorGUILayout.EnumPopup("Mode", m_MeshingMode);
            m_VoxelSizeMeters = EditorGUILayout.FloatField("Voxel Size (m)", m_VoxelSizeMeters);
            m_MinPointsPerVoxel = EditorGUILayout.IntField("Min Points / Voxel", m_MinPointsPerVoxel);
            m_MinNeighborVoxels = EditorGUILayout.IntSlider("Min Neighbor Voxels", m_MinNeighborVoxels, 0, 26);

            using (new EditorGUI.DisabledScope(m_MeshingMode != MeshingMode.MarchingCubes))
            {
                m_SmoothIterations = EditorGUILayout.IntSlider("Smooth Iterations", m_SmoothIterations, 0, 4);
                m_IsoLevel = EditorGUILayout.Slider("Iso Level", m_IsoLevel, 0.1f, 1.5f);
            }

            using (new EditorGUI.DisabledScope(m_MeshingMode != MeshingMode.GreedyVoxels))
            {
                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("Greedy Hole Fill", EditorStyles.boldLabel);
                m_GreedyCloseHoles = EditorGUILayout.Toggle("Close Small Holes", m_GreedyCloseHoles);
                using (new EditorGUI.DisabledScope(!m_GreedyCloseHoles))
                    m_GreedyCloseRadius = EditorGUILayout.IntSlider("Close Radius (voxels)", m_GreedyCloseRadius, 0, 3);

                m_GreedyFillEnclosedVoids = EditorGUILayout.Toggle("Fill Enclosed Voids", m_GreedyFillEnclosedVoids);
                m_GreedyGroundGapFill = EditorGUILayout.Toggle("Ground Column Gap Fill", m_GreedyGroundGapFill);
                using (new EditorGUI.DisabledScope(!m_GreedyGroundGapFill))
                    m_GreedyGroundMaxGap = EditorGUILayout.IntSlider("Ground Max Gap (voxels)", m_GreedyGroundMaxGap, 1, 16);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Optional Bounds Filter (m)", EditorStyles.boldLabel);
            m_UseBoundsFilter = EditorGUILayout.Toggle("Use Bounds", m_UseBoundsFilter);
            using (new EditorGUI.DisabledScope(!m_UseBoundsFilter))
            {
                m_BoundsMinMeters = EditorGUILayout.Vector3Field("Min (m)", m_BoundsMinMeters);
                m_BoundsMaxMeters = EditorGUILayout.Vector3Field("Max (m)", m_BoundsMaxMeters);
                m_ShowBoundsPreview = EditorGUILayout.Toggle("Preview In Scene", m_ShowBoundsPreview);
                m_BoundsPreviewColor = EditorGUILayout.ColorField("Preview Color", m_BoundsPreviewColor);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            m_AddMeshRenderer = EditorGUILayout.Toggle("Add MeshRenderer", m_AddMeshRenderer);
            m_LogStats = EditorGUILayout.Toggle("Log Stats", m_LogStats);
            EditorGUILayout.BeginHorizontal();
            m_DefaultOutputFolder = EditorGUILayout.TextField("Default Output Folder", m_DefaultOutputFolder);
            if (GUILayout.Button("Pick", GUILayout.Width(50f)))
            {
                string picked = EditorUtility.OpenFolderPanel("Choose Output Folder", Application.dataPath, string.Empty);
                if (!string.IsNullOrEmpty(picked) && picked.StartsWith(Application.dataPath, StringComparison.OrdinalIgnoreCase))
                {
                    m_DefaultOutputFolder = "Assets" + picked.Substring(Application.dataPath.Length).Replace('\\', '/');
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        if (EditorGUI.EndChangeCheck())
        {
            SavePrefs();
            SceneView.RepaintAll();
        }

        EditorGUILayout.Space();
        Rect r = GUILayoutUtility.GetRect(18f, 18f, "TextField");
        EditorGUI.ProgressBar(r, Mathf.Clamp01(m_Progress01), m_ProgressText);

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(m_IsGenerating))
            {
                if (GUILayout.Button("Generate Prefab Collider Mesh", GUILayout.Height(34f)))
                    GeneratePrefabColliderMesh();
            }

            using (new EditorGUI.DisabledScope(!m_IsGenerating))
            {
                if (GUILayout.Button("Cancel", GUILayout.Width(90f), GUILayout.Height(34f)))
                    m_CancelRequested = true;
            }
        }
    }

    void OnSceneGUI(SceneView sceneView)
    {
        if (!m_UseBoundsFilter || !m_ShowBoundsPreview)
            return;

        Vector3 min = Vector3.Min(m_BoundsMinMeters, m_BoundsMaxMeters);
        Vector3 max = Vector3.Max(m_BoundsMinMeters, m_BoundsMaxMeters);
        Vector3 center = (min + max) * 0.5f;
        Vector3 size = max - min;

        Handles.zTest = CompareFunction.Always;
        using (new Handles.DrawingScope(m_BoundsPreviewColor))
        {
            Handles.DrawWireCube(center, size);
            Handles.Label(center, $"Bounds\n{size.x:F2} x {size.y:F2} x {size.z:F2} m");
        }
    }

    void GeneratePrefabColliderMesh()
    {
        if (m_IsGenerating)
            return;

        m_IsGenerating = true;
        m_CancelRequested = false;
        m_Progress01 = 0f;
        m_ProgressText = "Starting...";

        try
        {
            if (m_PointFile == null)
            {
                Debug.LogError("No XYZ file assigned.");
                return;
            }

            if (m_VoxelSizeMeters <= 0f || m_InputScale <= 0f)
            {
                Debug.LogError("Voxel Size and Input Scale must be > 0.");
                return;
            }

            m_MinPointsPerVoxel = Mathf.Max(1, m_MinPointsPerVoxel);
            m_SmoothIterations = Mathf.Max(0, m_SmoothIterations);

            float unitToMeters = m_InputUnit == InputUnit.Millimeters ? 0.001f : 1f;
            float inputToMetersScale = unitToMeters * m_InputScale;

            Vector3 min = Vector3.Min(m_BoundsMinMeters, m_BoundsMaxMeters);
            Vector3 max = Vector3.Max(m_BoundsMinMeters, m_BoundsMaxMeters);

            if (!ReportProgress(0.02f, "Parsing points..."))
                return;

            if (!TryParsePoints(m_PointFile.text, m_UseBoundsFilter, min, max, inputToMetersScale,
                    (i, total) => ReportProgress(0.02f + 0.33f * (float)i / Mathf.Max(1, total), "Parsing points..."),
                    out var pointsMeters, out var parsedCount, out var skippedCount))
            {
                if (m_CancelRequested)
                    Debug.LogWarning("Point cloud generation canceled during parsing.");
                return;
            }

            if (pointsMeters.Count == 0)
            {
                Debug.LogWarning("No points left after parsing/filtering.");
                return;
            }

            if (!ReportProgress(0.36f, "Voxelizing points..."))
                return;

            var voxelCounts = BuildVoxelCounts(pointsMeters, m_VoxelSizeMeters, out var origin, out var minIndex, out var dims,
                (i, total) => ReportProgress(0.36f + 0.19f * (float)i / Mathf.Max(1, total), "Voxelizing points..."));
            if (voxelCounts == null)
            {
                Debug.LogWarning("Point cloud generation canceled during voxelization.");
                return;
            }

            if (!ReportProgress(0.56f, "Filtering voxels..."))
                return;

            var occupied = FilterOccupiedVoxels(voxelCounts, m_MinPointsPerVoxel, m_MinNeighborVoxels,
                (i, total) => ReportProgress(0.56f + 0.10f * (float)i / Mathf.Max(1, total), "Filtering voxels..."));
            if (occupied == null)
            {
                Debug.LogWarning("Point cloud generation canceled during filtering.");
                return;
            }

            if (occupied.Count == 0)
            {
                Debug.LogWarning("No occupied voxels after filtering.");
                return;
            }

            if (!ReportProgress(0.67f, "Building mesh..."))
                return;

            Mesh mesh = m_MeshingMode == MeshingMode.MarchingCubes
                ? BuildMarchingCubesMesh(occupied, origin, minIndex, dims, m_VoxelSizeMeters, m_SmoothIterations, m_IsoLevel,
                    (i, total) => ReportProgress(0.67f + 0.23f * (float)i / Mathf.Max(1, total), "Building marching cubes mesh..."))
                : BuildGreedyVoxelMesh(occupied, origin, minIndex, dims, m_VoxelSizeMeters,
                    m_GreedyCloseHoles, m_GreedyCloseRadius, m_GreedyFillEnclosedVoids, m_GreedyGroundGapFill, m_GreedyGroundMaxGap,
                    (i, total) => ReportProgress(0.67f + 0.23f * (float)i / Mathf.Max(1, total), "Building greedy voxel mesh..."));

            if (mesh == null || mesh.vertexCount == 0)
            {
                if (m_CancelRequested)
                    Debug.LogWarning("Point cloud generation canceled during meshing.");
                else
                    Debug.LogWarning("Failed to create mesh.");
                return;
            }

            if (!ReportProgress(0.92f, "Saving prefab assets..."))
                return;

            string defaultName = $"PointCloudCollider_{m_PointFile.name}.prefab";
            if (!AssetDatabase.IsValidFolder(m_DefaultOutputFolder))
                EnsureFolders(m_DefaultOutputFolder);

            string prefabPath = EditorUtility.SaveFilePanelInProject("Save Collider Prefab", defaultName, "prefab", "Choose prefab output path", m_DefaultOutputFolder);
            if (string.IsNullOrEmpty(prefabPath))
                return;

            string dir = Path.GetDirectoryName(prefabPath)?.Replace('\\', '/');
            string baseName = Path.GetFileNameWithoutExtension(prefabPath);
            string meshPath = AssetDatabase.GenerateUniqueAssetPath($"{dir}/{baseName}_Mesh.asset");
            mesh.name = Path.GetFileNameWithoutExtension(meshPath);
            AssetDatabase.CreateAsset(mesh, meshPath);

            var tempGo = new GameObject(baseName);
            try
            {
                var filter = tempGo.AddComponent<MeshFilter>();
                filter.sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);

                if (m_AddMeshRenderer)
                {
                    var renderer = tempGo.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = GetOrCreateDefaultUrpMaterial(dir, baseName);
                }

                var collider = tempGo.AddComponent<MeshCollider>();
                collider.sharedMesh = filter.sharedMesh;
                collider.convex = false;

                PrefabUtility.SaveAsPrefabAsset(tempGo, prefabPath);
            }
            finally
            {
                DestroyImmediate(tempGo);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Selection.activeObject = prefab;

            if (m_LogStats)
            {
                var b = CalculateBounds(pointsMeters);
                Debug.Log($"Prefab created: {prefabPath}\n" +
                          $"Points={pointsMeters.Count}, InvalidLines={skippedCount}, Parsed={parsedCount}, InputToMeters={inputToMetersScale}\n" +
                          $"PointBoundsCenter={b.center}, PointBoundsSize={b.size}\n" +
                          $"CandidateVoxels={voxelCounts.Count}, OccupiedVoxels={occupied.Count}, MeshVerts={mesh.vertexCount}, Tris={mesh.triangles.Length / 3}");
            }

            ReportProgress(1f, "Done");
        }
        finally
        {
            m_IsGenerating = false;
            m_CancelRequested = false;
            EditorUtility.ClearProgressBar();
            Repaint();
        }
    }

    bool ReportProgress(float progress01, string text)
    {
        m_Progress01 = Mathf.Clamp01(progress01);
        m_ProgressText = text;
        Repaint();
        bool canceledFromDialog = EditorUtility.DisplayCancelableProgressBar("Point Cloud Collider", text, m_Progress01);
        return !(m_CancelRequested || canceledFromDialog);
    }

    static bool TryParsePoints(string text, bool useBounds, Vector3 boundsMinMeters, Vector3 boundsMaxMeters, float inputToMetersScale,
        Func<int, int, bool> progressCallback, out List<Vector3> pointsMeters, out int parsedCount, out int skippedCount)
    {
        pointsMeters = new List<Vector3>(4096);
        parsedCount = 0;
        skippedCount = 0;

        if (string.IsNullOrWhiteSpace(text))
            return true;

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        int total = lines.Length;

        for (int i = 0; i < lines.Length; i++)
        {
            if ((i & 2047) == 0 && progressCallback != null && !progressCallback(i, total))
                return false;

            var parts = lines[i].Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                skippedCount++;
                continue;
            }

            if (!TryParseFloat(parts[0], out float xRaw) || !TryParseFloat(parts[1], out float yRaw) || !TryParseFloat(parts[2], out float zRaw))
            {
                skippedCount++;
                continue;
            }

            Vector3 p = new Vector3(xRaw, yRaw, zRaw) * inputToMetersScale;
            if (useBounds)
            {
                if (p.x < boundsMinMeters.x || p.x > boundsMaxMeters.x ||
                    p.y < boundsMinMeters.y || p.y > boundsMaxMeters.y ||
                    p.z < boundsMinMeters.z || p.z > boundsMaxMeters.z)
                    continue;
            }

            pointsMeters.Add(p);
            parsedCount++;
        }

        return progressCallback == null || progressCallback(total, total);
    }

    static Dictionary<Vector3Int, int> BuildVoxelCounts(List<Vector3> pointsMeters, float voxelSizeMeters, out Vector3 origin, out Vector3Int minIndex, out Vector3Int dims,
        Func<int, int, bool> progressCallback)
    {
        Vector3 min = pointsMeters[0];
        Vector3 max = pointsMeters[0];
        for (int i = 1; i < pointsMeters.Count; i++)
        {
            min = Vector3.Min(min, pointsMeters[i]);
            max = Vector3.Max(max, pointsMeters[i]);
        }

        origin = min;
        var counts = new Dictionary<Vector3Int, int>(pointsMeters.Count);
        minIndex = new Vector3Int(int.MaxValue, int.MaxValue, int.MaxValue);
        Vector3Int maxIndex = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);

        int total = pointsMeters.Count;
        for (int i = 0; i < pointsMeters.Count; i++)
        {
            if ((i & 4095) == 0 && progressCallback != null && !progressCallback(i, total))
            {
                dims = Vector3Int.zero;
                return null;
            }

            var p = pointsMeters[i];
            int ix = Mathf.FloorToInt((p.x - origin.x) / voxelSizeMeters);
            int iy = Mathf.FloorToInt((p.y - origin.y) / voxelSizeMeters);
            int iz = Mathf.FloorToInt((p.z - origin.z) / voxelSizeMeters);
            var key = new Vector3Int(ix, iy, iz);

            if (counts.TryGetValue(key, out int c)) counts[key] = c + 1; else counts[key] = 1;

            minIndex = Vector3Int.Min(minIndex, key);
            maxIndex = Vector3Int.Max(maxIndex, key);
        }

        dims = new Vector3Int(maxIndex.x - minIndex.x + 1, maxIndex.y - minIndex.y + 1, maxIndex.z - minIndex.z + 1);
        return counts;
    }

    static HashSet<Vector3Int> FilterOccupiedVoxels(Dictionary<Vector3Int, int> counts, int minPointsPerVoxel, int minNeighborVoxels,
        Func<int, int, bool> progressCallback)
    {
        var dense = new HashSet<Vector3Int>();
        int idx = 0;
        int totalCount = counts.Count;
        foreach (var kvp in counts)
        {
            if ((idx & 2047) == 0 && progressCallback != null && !progressCallback(idx, totalCount))
                return null;
            if (kvp.Value >= minPointsPerVoxel)
                dense.Add(kvp.Key);
            idx++;
        }

        if (minNeighborVoxels <= 0)
            return dense;

        var filtered = new HashSet<Vector3Int>();
        idx = 0;
        int totalDense = dense.Count;
        foreach (var cell in dense)
        {
            if ((idx & 2047) == 0 && progressCallback != null && !progressCallback(idx, totalDense))
                return null;

            int neighbors = 0;
            for (int z = -1; z <= 1; z++)
            for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++)
            {
                if (x == 0 && y == 0 && z == 0)
                    continue;
                if (dense.Contains(new Vector3Int(cell.x + x, cell.y + y, cell.z + z)) && ++neighbors >= minNeighborVoxels)
                    goto Keep;
            }
            idx++;
            continue;
            Keep: filtered.Add(cell); idx++;
        }

        return filtered;
    }

    static Mesh BuildMarchingCubesMesh(HashSet<Vector3Int> occupied, Vector3 origin, Vector3Int minIndex, Vector3Int dims, float voxelSize, int smoothIterations, float isoLevel,
        Func<int, int, bool> progressCallback)
    {
        int nx = dims.x;
        int ny = dims.y;
        int nz = dims.z;

        float[,,] field = new float[nx + 1, ny + 1, nz + 1];

        int added = 0;
        int totalOcc = occupied.Count;
        foreach (var c in occupied)
        {
            if ((added & 1023) == 0 && progressCallback != null && !progressCallback(added, Mathf.Max(1, totalOcc) * 2))
                return null;

            int x = c.x - minIndex.x;
            int y = c.y - minIndex.y;
            int z = c.z - minIndex.z;
            for (int dz = 0; dz <= 1; dz++)
            for (int dy = 0; dy <= 1; dy++)
            for (int dx = 0; dx <= 1; dx++)
                field[x + dx, y + dy, z + dz] += 1f;
            added++;
        }

        NormalizeField(field);
        for (int i = 0; i < smoothIterations; i++)
            SmoothField(field);

        var vertices = new List<Vector3>(occupied.Count * 6);
        var triangles = new List<int>(occupied.Count * 12);

        Vector3 baseOffset = origin + new Vector3(minIndex.x * voxelSize, minIndex.y * voxelSize, minIndex.z * voxelSize);

        int totalCells = nx * ny * nz;
        int cellIndex = 0;
        for (int z = 0; z < nz; z++)
        for (int y = 0; y < ny; y++)
        for (int x = 0; x < nx; x++)
        {
            if ((cellIndex & 4095) == 0 && progressCallback != null && !progressCallback(totalOcc + cellIndex, Mathf.Max(1, totalOcc) + Mathf.Max(1, totalCells)))
                return null;
            PolygoniseCubeTetra(field, x, y, z, baseOffset, voxelSize, isoLevel, vertices, triangles);
            cellIndex++;
        }

        if (vertices.Count == 0)
            return null;

        var mesh = new Mesh { name = "PointCloudMarchingCubes" };
        mesh.indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0, true);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        MeshUtility.Optimize(mesh);
        return mesh;
    }

    static Mesh BuildGreedyVoxelMesh(HashSet<Vector3Int> occupied, Vector3 origin, Vector3Int minIndex, Vector3Int dims, float voxelSize,
        bool closeHoles, int closeRadius, bool fillEnclosedVoids, bool groundGapFill, int groundMaxGap,
        Func<int, int, bool> progressCallback)
    {
        int nx = dims.x;
        int ny = dims.y;
        int nz = dims.z;
        bool[,,] vox = new bool[nx, ny, nz];

        int fill = 0;
        int total = occupied.Count;
        foreach (var c in occupied)
        {
            if ((fill & 2047) == 0 && progressCallback != null && !progressCallback(fill, Mathf.Max(1, total) * 3))
                return null;

            int x = c.x - minIndex.x;
            int y = c.y - minIndex.y;
            int z = c.z - minIndex.z;
            if (x >= 0 && y >= 0 && z >= 0 && x < nx && y < ny && z < nz)
                vox[x, y, z] = true;
            fill++;
        }

        int stageBase = Mathf.Max(1, total);
        if (closeHoles && closeRadius > 0)
        {
            if (progressCallback != null && !progressCallback(stageBase, stageBase * 3))
                return null;
            MorphologicalClose(vox, closeRadius);
        }

        if (fillEnclosedVoids)
        {
            if (progressCallback != null && !progressCallback(stageBase + stageBase / 4, stageBase * 3))
                return null;
            FillEnclosedVoids(vox);
        }

        if (groundGapFill)
        {
            if (progressCallback != null && !progressCallback(stageBase + stageBase / 2, stageBase * 3))
                return null;
            FillGroundColumnGaps(vox, Mathf.Max(1, groundMaxGap));
        }

        var vertices = new List<Vector3>(occupied.Count * 8);
        var triangles = new List<int>(occupied.Count * 12);

        bool canceled = false;
        GreedyMesh(vox, origin + new Vector3(minIndex.x * voxelSize, minIndex.y * voxelSize, minIndex.z * voxelSize), voxelSize, vertices, triangles,
            (i, t) =>
            {
                if (progressCallback == null)
                    return true;
                bool ok = progressCallback(stageBase * 2 + i, stageBase * 2 + Mathf.Max(1, t));
                if (!ok) canceled = true;
                return ok;
            });
        if (canceled)
            return null;

        if (vertices.Count == 0)
            return null;

        var mesh = new Mesh { name = "PointCloudGreedyVoxel" };
        mesh.indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0, true);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        MeshUtility.Optimize(mesh);
        return mesh;
    }

    static void MorphologicalClose(bool[,,] vox, int radius)
    {
        if (radius <= 0)
            return;

        int nx = vox.GetLength(0);
        int ny = vox.GetLength(1);
        int nz = vox.GetLength(2);

        bool[,,] dilated = new bool[nx, ny, nz];
        bool[,,] result = new bool[nx, ny, nz];

        for (int z = 0; z < nz; z++)
        for (int y = 0; y < ny; y++)
        for (int x = 0; x < nx; x++)
        {
            bool on = false;
            for (int dz = -radius; dz <= radius && !on; dz++)
            for (int dy = -radius; dy <= radius && !on; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                int xx = x + dx;
                int yy = y + dy;
                int zz = z + dz;
                if (xx < 0 || yy < 0 || zz < 0 || xx >= nx || yy >= ny || zz >= nz)
                    continue;
                if (vox[xx, yy, zz])
                {
                    on = true;
                    break;
                }
            }
            dilated[x, y, z] = on;
        }

        for (int z = 0; z < nz; z++)
        for (int y = 0; y < ny; y++)
        for (int x = 0; x < nx; x++)
        {
            bool on = true;
            for (int dz = -radius; dz <= radius && on; dz++)
            for (int dy = -radius; dy <= radius && on; dy++)
            for (int dx = -radius; dx <= radius && on; dx++)
            {
                int xx = x + dx;
                int yy = y + dy;
                int zz = z + dz;
                if (xx < 0 || yy < 0 || zz < 0 || xx >= nx || yy >= ny || zz >= nz || !dilated[xx, yy, zz])
                {
                    on = false;
                    break;
                }
            }
            result[x, y, z] = on;
        }

        Array.Copy(result, vox, result.Length);
    }

    static void FillEnclosedVoids(bool[,,] vox)
    {
        int nx = vox.GetLength(0);
        int ny = vox.GetLength(1);
        int nz = vox.GetLength(2);
        bool[,,] visited = new bool[nx, ny, nz];
        var q = new Queue<Vector3Int>();

        void EnqueueIfEmpty(int x, int y, int z)
        {
            if (x < 0 || y < 0 || z < 0 || x >= nx || y >= ny || z >= nz)
                return;
            if (visited[x, y, z] || vox[x, y, z])
                return;
            visited[x, y, z] = true;
            q.Enqueue(new Vector3Int(x, y, z));
        }

        for (int x = 0; x < nx; x++)
        for (int y = 0; y < ny; y++)
        {
            EnqueueIfEmpty(x, y, 0);
            EnqueueIfEmpty(x, y, nz - 1);
        }

        for (int x = 0; x < nx; x++)
        for (int z = 0; z < nz; z++)
        {
            EnqueueIfEmpty(x, 0, z);
            EnqueueIfEmpty(x, ny - 1, z);
        }

        for (int y = 0; y < ny; y++)
        for (int z = 0; z < nz; z++)
        {
            EnqueueIfEmpty(0, y, z);
            EnqueueIfEmpty(nx - 1, y, z);
        }

        while (q.Count > 0)
        {
            var p = q.Dequeue();
            EnqueueIfEmpty(p.x - 1, p.y, p.z);
            EnqueueIfEmpty(p.x + 1, p.y, p.z);
            EnqueueIfEmpty(p.x, p.y - 1, p.z);
            EnqueueIfEmpty(p.x, p.y + 1, p.z);
            EnqueueIfEmpty(p.x, p.y, p.z - 1);
            EnqueueIfEmpty(p.x, p.y, p.z + 1);
        }

        for (int z = 0; z < nz; z++)
        for (int y = 0; y < ny; y++)
        for (int x = 0; x < nx; x++)
            if (!vox[x, y, z] && !visited[x, y, z])
                vox[x, y, z] = true;
    }

    static void FillGroundColumnGaps(bool[,,] vox, int maxGap)
    {
        int nx = vox.GetLength(0);
        int ny = vox.GetLength(1);
        int nz = vox.GetLength(2);

        for (int z = 0; z < nz; z++)
        for (int x = 0; x < nx; x++)
        {
            int prevSolidY = -1;
            for (int y = 0; y < ny; y++)
            {
                if (!vox[x, y, z])
                    continue;

                if (prevSolidY >= 0)
                {
                    int gap = y - prevSolidY - 1;
                    if (gap > 0 && gap <= maxGap)
                    {
                        for (int gy = prevSolidY + 1; gy < y; gy++)
                            vox[x, gy, z] = true;
                    }
                }

                prevSolidY = y;
            }
        }
    }

    void LoadPrefs()
    {
        m_InputUnit = (InputUnit)EditorPrefs.GetInt(PrefPrefix + "InputUnit", (int)m_InputUnit);
        m_InputScale = EditorPrefs.GetFloat(PrefPrefix + "InputScale", m_InputScale);

        m_MeshingMode = (MeshingMode)EditorPrefs.GetInt(PrefPrefix + "MeshingMode", (int)m_MeshingMode);
        m_VoxelSizeMeters = EditorPrefs.GetFloat(PrefPrefix + "VoxelSizeMeters", m_VoxelSizeMeters);
        m_MinPointsPerVoxel = EditorPrefs.GetInt(PrefPrefix + "MinPointsPerVoxel", m_MinPointsPerVoxel);
        m_MinNeighborVoxels = EditorPrefs.GetInt(PrefPrefix + "MinNeighborVoxels", m_MinNeighborVoxels);
        m_SmoothIterations = EditorPrefs.GetInt(PrefPrefix + "SmoothIterations", m_SmoothIterations);
        m_IsoLevel = EditorPrefs.GetFloat(PrefPrefix + "IsoLevel", m_IsoLevel);

        m_GreedyCloseHoles = EditorPrefs.GetBool(PrefPrefix + "GreedyCloseHoles", m_GreedyCloseHoles);
        m_GreedyCloseRadius = EditorPrefs.GetInt(PrefPrefix + "GreedyCloseRadius", m_GreedyCloseRadius);
        m_GreedyFillEnclosedVoids = EditorPrefs.GetBool(PrefPrefix + "GreedyFillEnclosedVoids", m_GreedyFillEnclosedVoids);
        m_GreedyGroundGapFill = EditorPrefs.GetBool(PrefPrefix + "GreedyGroundGapFill", m_GreedyGroundGapFill);
        m_GreedyGroundMaxGap = EditorPrefs.GetInt(PrefPrefix + "GreedyGroundMaxGap", m_GreedyGroundMaxGap);

        m_UseBoundsFilter = EditorPrefs.GetBool(PrefPrefix + "UseBoundsFilter", m_UseBoundsFilter);
        m_BoundsMinMeters = ReadVector3(PrefPrefix + "BoundsMinMeters", m_BoundsMinMeters);
        m_BoundsMaxMeters = ReadVector3(PrefPrefix + "BoundsMaxMeters", m_BoundsMaxMeters);
        m_ShowBoundsPreview = EditorPrefs.GetBool(PrefPrefix + "ShowBoundsPreview", m_ShowBoundsPreview);
        m_BoundsPreviewColor = ReadColor(PrefPrefix + "BoundsPreviewColor", m_BoundsPreviewColor);

        m_AddMeshRenderer = EditorPrefs.GetBool(PrefPrefix + "AddMeshRenderer", m_AddMeshRenderer);
        m_LogStats = EditorPrefs.GetBool(PrefPrefix + "LogStats", m_LogStats);
        m_DefaultOutputFolder = EditorPrefs.GetString(PrefPrefix + "DefaultOutputFolder", m_DefaultOutputFolder);

        string path = EditorPrefs.GetString(PrefPrefix + "PointFilePath", string.Empty);
        if (!string.IsNullOrEmpty(path))
            m_PointFile = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
    }

    void SavePrefs()
    {
        EditorPrefs.SetInt(PrefPrefix + "InputUnit", (int)m_InputUnit);
        EditorPrefs.SetFloat(PrefPrefix + "InputScale", m_InputScale);

        EditorPrefs.SetInt(PrefPrefix + "MeshingMode", (int)m_MeshingMode);
        EditorPrefs.SetFloat(PrefPrefix + "VoxelSizeMeters", m_VoxelSizeMeters);
        EditorPrefs.SetInt(PrefPrefix + "MinPointsPerVoxel", m_MinPointsPerVoxel);
        EditorPrefs.SetInt(PrefPrefix + "MinNeighborVoxels", m_MinNeighborVoxels);
        EditorPrefs.SetInt(PrefPrefix + "SmoothIterations", m_SmoothIterations);
        EditorPrefs.SetFloat(PrefPrefix + "IsoLevel", m_IsoLevel);

        EditorPrefs.SetBool(PrefPrefix + "GreedyCloseHoles", m_GreedyCloseHoles);
        EditorPrefs.SetInt(PrefPrefix + "GreedyCloseRadius", m_GreedyCloseRadius);
        EditorPrefs.SetBool(PrefPrefix + "GreedyFillEnclosedVoids", m_GreedyFillEnclosedVoids);
        EditorPrefs.SetBool(PrefPrefix + "GreedyGroundGapFill", m_GreedyGroundGapFill);
        EditorPrefs.SetInt(PrefPrefix + "GreedyGroundMaxGap", m_GreedyGroundMaxGap);

        EditorPrefs.SetBool(PrefPrefix + "UseBoundsFilter", m_UseBoundsFilter);
        WriteVector3(PrefPrefix + "BoundsMinMeters", m_BoundsMinMeters);
        WriteVector3(PrefPrefix + "BoundsMaxMeters", m_BoundsMaxMeters);
        EditorPrefs.SetBool(PrefPrefix + "ShowBoundsPreview", m_ShowBoundsPreview);
        WriteColor(PrefPrefix + "BoundsPreviewColor", m_BoundsPreviewColor);

        EditorPrefs.SetBool(PrefPrefix + "AddMeshRenderer", m_AddMeshRenderer);
        EditorPrefs.SetBool(PrefPrefix + "LogStats", m_LogStats);
        EditorPrefs.SetString(PrefPrefix + "DefaultOutputFolder", m_DefaultOutputFolder);

        string path = m_PointFile ? AssetDatabase.GetAssetPath(m_PointFile) : string.Empty;
        EditorPrefs.SetString(PrefPrefix + "PointFilePath", path);
    }

    static void EnsureFolders(string assetFolder)
    {
        string[] parts = assetFolder.Split('/');
        if (parts.Length == 0 || parts[0] != "Assets")
            return;

        string cur = "Assets";
        for (int i = 1; i < parts.Length; i++)
        {
            string next = $"{cur}/{parts[i]}";
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(cur, parts[i]);
            cur = next;
        }
    }

    static Material GetOrCreateDefaultUrpMaterial(string dir, string baseName)
    {
        string matPath = $"{dir}/{baseName}_Mat.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (mat != null)
            return mat;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        mat = new Material(shader) { name = Path.GetFileNameWithoutExtension(matPath) };
        AssetDatabase.CreateAsset(mat, matPath);
        return mat;
    }

    static void NormalizeField(float[,,] field)
    {
        int nx = field.GetLength(0);
        int ny = field.GetLength(1);
        int nz = field.GetLength(2);

        float max = 0f;
        for (int z = 0; z < nz; z++)
        for (int y = 0; y < ny; y++)
        for (int x = 0; x < nx; x++)
            if (field[x, y, z] > max)
                max = field[x, y, z];

        if (max <= 0f)
            return;

        float inv = 1f / max;
        for (int z = 0; z < nz; z++)
        for (int y = 0; y < ny; y++)
        for (int x = 0; x < nx; x++)
            field[x, y, z] *= inv;
    }

    static void SmoothField(float[,,] field)
    {
        int nx = field.GetLength(0);
        int ny = field.GetLength(1);
        int nz = field.GetLength(2);
        var tmp = new float[nx, ny, nz];

        for (int z = 0; z < nz; z++)
        for (int y = 0; y < ny; y++)
        for (int x = 0; x < nx; x++)
        {
            float sum = field[x, y, z] * 0.4f;
            float w = 0.4f;

            AddNeighbor(ref sum, ref w, field, x - 1, y, z);
            AddNeighbor(ref sum, ref w, field, x + 1, y, z);
            AddNeighbor(ref sum, ref w, field, x, y - 1, z);
            AddNeighbor(ref sum, ref w, field, x, y + 1, z);
            AddNeighbor(ref sum, ref w, field, x, y, z - 1);
            AddNeighbor(ref sum, ref w, field, x, y, z + 1);

            tmp[x, y, z] = sum / w;
        }

        Array.Copy(tmp, field, tmp.Length);
    }

    static void AddNeighbor(ref float sum, ref float w, float[,,] field, int x, int y, int z)
    {
        if (x < 0 || y < 0 || z < 0 || x >= field.GetLength(0) || y >= field.GetLength(1) || z >= field.GetLength(2))
            return;
        sum += field[x, y, z] * 0.1f;
        w += 0.1f;
    }

    static readonly int[,] CubeCorners =
    {
        { 0, 0, 0 }, { 1, 0, 0 }, { 1, 1, 0 }, { 0, 1, 0 },
        { 0, 0, 1 }, { 1, 0, 1 }, { 1, 1, 1 }, { 0, 1, 1 }
    };

    static readonly int[,] Tetrahedra =
    {
        { 0, 5, 1, 6 },
        { 0, 1, 2, 6 },
        { 0, 2, 3, 6 },
        { 0, 3, 7, 6 },
        { 0, 7, 4, 6 },
        { 0, 4, 5, 6 }
    };

    static void PolygoniseCubeTetra(float[,,] field, int gx, int gy, int gz, Vector3 baseOffset, float size, float iso, List<Vector3> vertices, List<int> triangles)
    {
        var p = new Vector3[8];
        var v = new float[8];

        for (int i = 0; i < 8; i++)
        {
            int x = gx + CubeCorners[i, 0];
            int y = gy + CubeCorners[i, 1];
            int z = gz + CubeCorners[i, 2];
            p[i] = baseOffset + new Vector3(x * size, y * size, z * size);
            v[i] = field[x, y, z];
        }

        for (int t = 0; t < 6; t++)
        {
            int a = Tetrahedra[t, 0];
            int b = Tetrahedra[t, 1];
            int c = Tetrahedra[t, 2];
            int d = Tetrahedra[t, 3];
            PolygoniseTetra(p[a], p[b], p[c], p[d], v[a], v[b], v[c], v[d], iso, vertices, triangles);
        }
    }

    static void PolygoniseTetra(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float v0, float v1, float v2, float v3, float iso, List<Vector3> vertices, List<int> triangles)
    {
        var p = new[] { p0, p1, p2, p3 };
        var v = new[] { v0, v1, v2, v3 };
        var inside = new List<int>(4);
        var outside = new List<int>(4);

        for (int i = 0; i < 4; i++)
        {
            if (v[i] >= iso) inside.Add(i); else outside.Add(i);
        }

        if (inside.Count == 0 || inside.Count == 4)
            return;

        if (inside.Count == 1 || inside.Count == 3)
        {
            bool invert = inside.Count == 3;
            int a = invert ? outside[0] : inside[0];
            int b = invert ? inside[0] : outside[0];
            int c = invert ? inside[1] : outside[1];
            int d = invert ? inside[2] : outside[2];

            Vector3 e0 = VertexInterp(iso, p[a], p[b], v[a], v[b]);
            Vector3 e1 = VertexInterp(iso, p[a], p[c], v[a], v[c]);
            Vector3 e2 = VertexInterp(iso, p[a], p[d], v[a], v[d]);

            AddTri(vertices, triangles, e0, invert ? e2 : e1, invert ? e1 : e2);
            return;
        }

        int i0 = inside[0], i1 = inside[1], o0 = outside[0], o1 = outside[1];

        Vector3 a0 = VertexInterp(iso, p[i0], p[o0], v[i0], v[o0]);
        Vector3 a1 = VertexInterp(iso, p[i1], p[o0], v[i1], v[o0]);
        Vector3 b0 = VertexInterp(iso, p[i0], p[o1], v[i0], v[o1]);
        Vector3 b1 = VertexInterp(iso, p[i1], p[o1], v[i1], v[o1]);

        AddTri(vertices, triangles, a0, a1, b1);
        AddTri(vertices, triangles, a0, b1, b0);
    }

    static Vector3 VertexInterp(float iso, Vector3 p1, Vector3 p2, float v1, float v2)
    {
        float denom = v2 - v1;
        if (Mathf.Abs(denom) < 1e-6f)
            return p1;
        float t = Mathf.Clamp01((iso - v1) / denom);
        return Vector3.LerpUnclamped(p1, p2, t);
    }

    static void AddTri(List<Vector3> vertices, List<int> triangles, Vector3 a, Vector3 b, Vector3 c)
    {
        int idx = vertices.Count;
        vertices.Add(a);
        vertices.Add(b);
        vertices.Add(c);
        triangles.Add(idx);
        triangles.Add(idx + 1);
        triangles.Add(idx + 2);
    }

    static bool TryParseFloat(string value, out float result)
    {
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
               || float.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result);
    }

    static Vector3 ReadVector3(string key, Vector3 fallback)
    {
        return new Vector3(
            EditorPrefs.GetFloat(key + ".x", fallback.x),
            EditorPrefs.GetFloat(key + ".y", fallback.y),
            EditorPrefs.GetFloat(key + ".z", fallback.z));
    }

    static void WriteVector3(string key, Vector3 value)
    {
        EditorPrefs.SetFloat(key + ".x", value.x);
        EditorPrefs.SetFloat(key + ".y", value.y);
        EditorPrefs.SetFloat(key + ".z", value.z);
    }

    static Color ReadColor(string key, Color fallback)
    {
        return new Color(
            EditorPrefs.GetFloat(key + ".r", fallback.r),
            EditorPrefs.GetFloat(key + ".g", fallback.g),
            EditorPrefs.GetFloat(key + ".b", fallback.b),
            EditorPrefs.GetFloat(key + ".a", fallback.a));
    }

    static void WriteColor(string key, Color value)
    {
        EditorPrefs.SetFloat(key + ".r", value.r);
        EditorPrefs.SetFloat(key + ".g", value.g);
        EditorPrefs.SetFloat(key + ".b", value.b);
        EditorPrefs.SetFloat(key + ".a", value.a);
    }

    static void GreedyMesh(bool[,,] vox, Vector3 origin, float size, List<Vector3> vertices, List<int> triangles, Func<int, int, bool> progressCallback)
    {
        int[] dims = { vox.GetLength(0), vox.GetLength(1), vox.GetLength(2) };
        int totalSlices = dims[0] + dims[1] + dims[2] + 3;
        int doneSlices = 0;

        int[] x = new int[3];
        int[] q = new int[3];

        for (int d = 0; d < 3; d++)
        {
            int u = (d + 1) % 3;
            int v = (d + 2) % 3;
            Array.Clear(q, 0, 3);
            q[d] = 1;

            int maskW = dims[u];
            int maskH = dims[v];
            int[] mask = new int[maskW * maskH];

            for (x[d] = -1; x[d] < dims[d];)
            {
                if (progressCallback != null && !progressCallback(doneSlices, totalSlices))
                    return;

                int n = 0;
                for (x[v] = 0; x[v] < dims[v]; x[v]++)
                for (x[u] = 0; x[u] < dims[u]; x[u]++)
                {
                    bool a = x[d] >= 0 && GetVoxel(vox, x[0], x[1], x[2]);
                    bool b = x[d] < dims[d] - 1 && GetVoxel(vox, x[0] + q[0], x[1] + q[1], x[2] + q[2]);
                    mask[n++] = a == b ? 0 : (a ? 1 : -1);
                }

                x[d]++;
                doneSlices++;
                n = 0;

                for (int j = 0; j < maskH; j++)
                {
                    for (int i = 0; i < maskW;)
                    {
                        int c = mask[n];
                        if (c == 0)
                        {
                            i++;
                            n++;
                            continue;
                        }

                        int w = 1;
                        while (i + w < maskW && mask[n + w] == c) w++;

                        int h = 1;
                        bool done = false;
                        while (j + h < maskH && !done)
                        {
                            for (int k = 0; k < w; k++)
                            {
                                if (mask[n + k + h * maskW] != c)
                                {
                                    done = true;
                                    break;
                                }
                            }
                            if (!done) h++;
                        }

                        x[u] = i;
                        x[v] = j;

                        int[] du = { 0, 0, 0 };
                        int[] dv = { 0, 0, 0 };
                        du[u] = w;
                        dv[v] = h;

                        int[] p = { x[0], x[1], x[2] };
                        if (c > 0)
                        {
                            p[d] = x[d];
                            AddQuad(p, du, dv, true, origin, size, vertices, triangles);
                        }
                        else
                        {
                            p[d] = x[d] - 1;
                            AddQuad(p, du, dv, false, origin, size, vertices, triangles);
                        }

                        for (int l = 0; l < h; l++)
                        for (int k = 0; k < w; k++)
                            mask[n + k + l * maskW] = 0;

                        i += w;
                        n += w;
                    }
                }
            }
        }

        progressCallback?.Invoke(totalSlices, totalSlices);
    }

    static bool GetVoxel(bool[,,] vox, int x, int y, int z)
    {
        if (x < 0 || y < 0 || z < 0 || x >= vox.GetLength(0) || y >= vox.GetLength(1) || z >= vox.GetLength(2))
            return false;
        return vox[x, y, z];
    }

    static void AddQuad(int[] p, int[] du, int[] dv, bool front, Vector3 origin, float size, List<Vector3> vertices, List<int> triangles)
    {
        Vector3 v0 = origin + new Vector3(p[0], p[1], p[2]) * size;
        Vector3 v1 = origin + new Vector3(p[0] + du[0], p[1] + du[1], p[2] + du[2]) * size;
        Vector3 v2 = origin + new Vector3(p[0] + du[0] + dv[0], p[1] + du[1] + dv[1], p[2] + du[2] + dv[2]) * size;
        Vector3 v3 = origin + new Vector3(p[0] + dv[0], p[1] + dv[1], p[2] + dv[2]) * size;

        int idx = vertices.Count;
        vertices.Add(v0);
        vertices.Add(v1);
        vertices.Add(v2);
        vertices.Add(v3);

        if (front)
        {
            triangles.Add(idx);
            triangles.Add(idx + 1);
            triangles.Add(idx + 2);
            triangles.Add(idx);
            triangles.Add(idx + 2);
            triangles.Add(idx + 3);
        }
        else
        {
            triangles.Add(idx);
            triangles.Add(idx + 2);
            triangles.Add(idx + 1);
            triangles.Add(idx);
            triangles.Add(idx + 3);
            triangles.Add(idx + 2);
        }
    }

    static Bounds CalculateBounds(List<Vector3> points)
    {
        Vector3 min = points[0];
        Vector3 max = points[0];
        for (int i = 1; i < points.Count; i++)
        {
            min = Vector3.Min(min, points[i]);
            max = Vector3.Max(max, points[i]);
        }

        var b = new Bounds();
        b.SetMinMax(min, max);
        return b;
    }
}
