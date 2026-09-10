#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class SceneVFXBakerWindow : EditorWindow
{
    private const int FrameRate = 30;
    private const int CaptureSize = 1024;

    private const int CropPadding = 2;
    private const float AlphaThreshold = 1.0f / 255.0f;
    private const float CameraPadding = 1.02f;
    private const float DepthPadding = 1.0f;
    private const float MaximumDuration = 30.0f;
    private const string OutputRoot = "Assets/BakedVFX";
    private const string SharedQuadPath = OutputRoot + "/Shared/BakedVFX_UnitQuad.asset";

    private static readonly string[] AtlasTileSizeLabels =
        { "64 x 64", "128 x 128", "256 x 256", "512 x 512" };
    private static readonly int[] AtlasTileSizeValues = { 64, 128, 256, 512 };

    [SerializeField] private GameObject sourcePrefab;
    [SerializeField] private int atlasTileSize = 256;
    [SerializeField] private bool bakeChildGroups;
    [SerializeField] private GameObject assignmentSource;
    [SerializeField] private List<ChildGroupAssignment> childGroupAssignments =
        new List<ChildGroupAssignment>();

    [Serializable]
    private sealed class ChildGroupAssignment
    {
        public string hierarchyPath;
        public string displayPath;
        public string groupName;
    }

    private sealed class CapturedFrame
    {
        public Texture2D texture;
        public RectInt sourceRect;
        public RectInt atlasRect;
    }

    private sealed class CaptureDiagnostics
    {
        public int sampledFrames;
        public int framesWithParticles;
        public int maxLiveParticles;
        public int maxParticleCentersInView;
        public int bestFrame = -1;
        public int bestFrameLiveParticles;
        public float maxBlackSignal;
        public float maxCoverageSignal;
        public Color32[] bestBlackPixels;
        public Color32[] bestWhitePixels;
    }

    private struct CaptureLayout
    {
        public float worldSize;
        public Vector3 childLocalPosition;
        public Quaternion childLocalRotation;
    }

    private sealed class BakeGroup
    {
        public string name;
        public bool includesSourceRoot;
        public bool captureWholeEffect;
        public ParticleSystem[] systems;
    }

    private sealed class BakedGroupResult
    {
        public string name;
        public Material material;
        public Mesh quadMesh;
        public Bounds localBounds;
        public CaptureLayout layout;
        public GameObject groupPrefab;
        public float playDelay;
    }

    [MenuItem("Tools/VFX/VFX Prefab Baker")]
    public static void Open()
    {
        SceneVFXBakerWindow window = GetWindow<SceneVFXBakerWindow>("VFX Prefab Baker");
        window.minSize = new Vector2(430, 230);
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("VFX Baker", EditorStyles.boldLabel);
        EditorGUILayout.Space(4);
        sourcePrefab = (GameObject)EditorGUILayout.ObjectField(
            "Source Effect", sourcePrefab, typeof(GameObject), true);
        EnsureChildGroupAssignments();

        EditorGUILayout.Space(8);
        EditorGUILayout.HelpBox(
            "The baker creates and destroys an isolated orthographic camera automatically. " +
            "It samples from time 0, converts leading empty frames to playback delay, preserves middle gaps, " +
            "crops every frame to a power-of-two square, and packs the squares into a power-of-two atlas. " +
            "Output playback always uses Alpha blending and the shared 1x1 quad.",
            MessageType.Info);

        EditorGUILayout.LabelField("Bake Settings", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Frame Rate", FrameRate + " FPS");
        EditorGUILayout.LabelField("Capture Resolution", CaptureSize + " x " + CaptureSize);
        atlasTileSize = EditorGUILayout.IntPopup(
            "Atlas Frame Size", atlasTileSize, AtlasTileSizeLabels, AtlasTileSizeValues);
        bakeChildGroups = EditorGUILayout.Toggle("Bake Child Groups", bakeChildGroups);
        EditorGUILayout.LabelField("Crop Padding", CropPadding + " px");

        if (bakeChildGroups)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Child Effect Groups", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Only leaf ParticleSystem nodes are listed. Nodes with the same " +
                "group name are baked together; leave the name empty to exclude one.",
                MessageType.None);

            foreach (ChildGroupAssignment assignment in childGroupAssignments)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(assignment.displayPath, GUILayout.MinWidth(150));
                assignment.groupName = EditorGUILayout.TextField(
                    assignment.groupName, GUILayout.MinWidth(150));
                EditorGUILayout.EndHorizontal();
            }

            if (GUILayout.Button("Reset Child Groups"))
                RebuildChildGroupAssignments(true);
        }
        EditorGUILayout.Space(10);
        using (new EditorGUI.DisabledScope(sourcePrefab == null))
        {
            if (GUILayout.Button("Bake Effect", GUILayout.Height(36)))
                Bake();
        }
    }

    private void Bake()
    {
        if (!ValidateSource())
            return;

        GameObject instance = null;
        GameObject cameraObject = null;
        var results = new List<BakedGroupResult>();

        try
        {
            if (EditorUtility.IsPersistent(sourcePrefab))
                instance = PrefabUtility.InstantiatePrefab(sourcePrefab) as GameObject;
            else
                instance = Instantiate(sourcePrefab);

            if (instance == null)
                throw new InvalidOperationException("Unable to create a temporary copy of the source effect.");

            instance.name = sourcePrefab.name + "_BakeInstance";
            instance.hideFlags = HideFlags.HideAndDontSave;
            instance.SetActive(true);

            int isolationLayer = FindIsolationLayer(instance);
            SetLayerRecursively(instance, isolationLayer);

            ParticleSystem[] allSystems = instance.GetComponentsInChildren<ParticleSystem>(true);
            if (allSystems.Length == 0)
                throw new InvalidOperationException("The source prefab contains no ParticleSystem.");

            LockSeeds(allSystems);

            cameraObject = new GameObject("VFX Baker Camera", typeof(Camera));
            cameraObject.hideFlags = HideFlags.HideAndDontSave;
            Camera camera = cameraObject.GetComponent<Camera>();
            ConfigureCamera(camera);
            camera.cullingMask = 1 << isolationLayer;

            List<BakeGroup> groups = BuildBakeGroups(
                instance.transform, allSystems, bakeChildGroups, childGroupAssignments);
            Renderer[] allRenderers = instance.GetComponentsInChildren<Renderer>(true);
            var rendererStates = new Dictionary<Renderer, bool>();
            foreach (Renderer renderer in allRenderers)
                rendererStates[renderer] = renderer.enabled;

            string safeSourceName = MakeSafeName(sourcePrefab.name);
            string folder = OutputRoot + "/" + safeSourceName + "_Baked";
            EnsureFolder(OutputRoot);
            EnsureFolder(folder);

            for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                BakeGroup group = groups[groupIndex];
                var frames = new List<CapturedFrame>();

                try
                {
                    SetGroupRendererVisibility(
                        allRenderers, rendererStates, instance.transform, group);

                    float duration = EstimateDuration(group.systems);
                    CaptureLayout layout = FitCamera(
                        camera, instance.transform, group.systems, duration);
                    CaptureDiagnostics diagnostics = CaptureFrames(
                        camera, group.systems, duration, atlasTileSize, frames,
                        out int firstVisibleFrame);

                    if (frames.Count == 0)
                    {
                        string diagnosticFolder = SaveCaptureDiagnostics(
                            diagnostics, group.systems, group.name);
                        LogRendererDiagnostics(camera, group.systems);
                        Debug.LogWarning(
                            $"VFX Baker: skipped empty group '{group.name}'. " +
                            BuildCaptureFailureMessage(
                                diagnostics, camera, instance.transform, diagnosticFolder));
                        continue;
                    }

                    Vector2Int atlasSize = PackFrames(frames);
                    string assetBaseName = bakeChildGroups
                        ? safeSourceName + "_" + MakeSafeName(group.name)
                        : safeSourceName;
                    float automaticDelay = firstVisibleFrame / (float)FrameRate;
                    results.Add(SaveGroupResult(
                        frames, atlasSize, layout, folder, assetBaseName,
                        group.name, bakeChildGroups, automaticDelay));

                    Debug.Log(
                        $"VFX Baker: baked group '{group.name}' with " +
                        $"{CountVisibleFrames(frames)} visible frames across " +
                        $"{frames.Count} timeline frames into {atlasSize.x}x{atlasSize.y}. " +
                        $"Automatic delay: {automaticDelay:F3}s.");
                }
                finally
                {
                    foreach (CapturedFrame frame in frames)
                    {
                        if (frame.texture != null)
                            DestroyImmediate(frame.texture);
                    }
                }
            }

            if (results.Count == 0)
                throw new InvalidOperationException(
                    "No child group produced visible frames. Check the Console and diagnostic images.");

            string prefabPath = folder + "/" + safeSourceName + "_Baked.prefab";
            DeleteAsset(prefabPath);
            SaveOutputPrefab(prefabPath, safeSourceName, results);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            EditorGUIUtility.PingObject(Selection.activeObject);
            Debug.Log(
                $"VFX Baker: completed '{sourcePrefab.name}' with {results.Count} baked group(s).");
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorUtility.DisplayDialog("VFX Baker", exception.Message, "OK");
        }
        finally
        {
            if (cameraObject != null)
                DestroyImmediate(cameraObject);
            if (instance != null)
                DestroyImmediate(instance);

            EditorUtility.ClearProgressBar();
        }
    }
    private bool ValidateSource()
    {
        if (sourcePrefab == null)
            return false;

        if (EditorUtility.IsPersistent(sourcePrefab) &&
            PrefabUtility.GetPrefabAssetType(sourcePrefab) == PrefabAssetType.NotAPrefab)
        {
            EditorUtility.DisplayDialog("VFX Baker", "Source must be a prefab asset or a GameObject from the current scene.", "OK");
            return false;
        }

        if (sourcePrefab.GetComponentInChildren<ParticleSystem>(true) == null)
        {
            EditorUtility.DisplayDialog("VFX Baker", "The prefab contains no ParticleSystem.", "OK");
            return false;
        }

        return true;
    }

    private void EnsureChildGroupAssignments()
    {
        if (sourcePrefab == null)
        {
            assignmentSource = null;
            childGroupAssignments.Clear();
            return;
        }

        var particleNodes = new List<Transform>();
        CollectLeafParticleNodes(sourcePrefab.transform, sourcePrefab.transform, particleNodes);

        if (assignmentSource != sourcePrefab ||
            particleNodes.Count != childGroupAssignments.Count)
        {
            RebuildChildGroupAssignments(assignmentSource != sourcePrefab);
            return;
        }

        for (int i = 0; i < particleNodes.Count; i++)
        {
            string hierarchyPath = GetRelativeIndexPath(
                sourcePrefab.transform, particleNodes[i]);
            string displayPath = particleNodes[i].name;
            ChildGroupAssignment assignment = childGroupAssignments[i];
            if (assignment.hierarchyPath != hierarchyPath ||
                assignment.displayPath != displayPath)
            {
                RebuildChildGroupAssignments(false);
                return;
            }
        }
    }

    private void RebuildChildGroupAssignments(bool resetNames)
    {
        if (sourcePrefab == null)
            return;

        var previous = new Dictionary<string, ChildGroupAssignment>();
        if (!resetNames)
        {
            foreach (ChildGroupAssignment assignment in childGroupAssignments)
                previous[assignment.hierarchyPath] = assignment;
        }

        var particleNodes = new List<Transform>();
        CollectLeafParticleNodes(sourcePrefab.transform, sourcePrefab.transform, particleNodes);

        var rebuilt = new List<ChildGroupAssignment>();
        foreach (Transform node in particleNodes)
        {
            string hierarchyPath = GetRelativeIndexPath(sourcePrefab.transform, node);
            string displayPath = node.name;
            string groupName = node.name;

            if (previous.TryGetValue(
                    hierarchyPath, out ChildGroupAssignment oldAssignment) &&
                oldAssignment.displayPath == displayPath)
            {
                groupName = oldAssignment.groupName;
            }

            rebuilt.Add(new ChildGroupAssignment
            {
                hierarchyPath = hierarchyPath,
                displayPath = displayPath,
                groupName = groupName
            });
        }

        assignmentSource = sourcePrefab;
        childGroupAssignments = rebuilt;
        Repaint();
    }

    private static bool CollectLeafParticleNodes(
        Transform sourceRoot, Transform current, List<Transform> output)
    {
        bool descendantHasParticles = false;
        for (int i = 0; i < current.childCount; i++)
        {
            if (CollectLeafParticleNodes(
                    sourceRoot, current.GetChild(i), output))
            {
                descendantHasParticles = true;
            }
        }

        bool hasOwnParticleSystem =
            current != sourceRoot &&
            current.GetComponent<ParticleSystem>() != null;

        if (hasOwnParticleSystem && !descendantHasParticles)
            output.Add(current);

        return hasOwnParticleSystem || descendantHasParticles;
    }

    private static string GetRelativeIndexPath(
        Transform sourceRoot, Transform descendant)
    {
        if (descendant == sourceRoot)
            return string.Empty;

        var indices = new List<int>();
        Transform current = descendant;
        while (current != null && current != sourceRoot)
        {
            indices.Add(current.GetSiblingIndex());
            current = current.parent;
        }

        if (current != sourceRoot)
            return string.Empty;

        indices.Reverse();
        return string.Join("/", indices);
    }


    private static List<BakeGroup> BuildBakeGroups(
        Transform sourceRoot,
        ParticleSystem[] allSystems,
        bool groupChildren,
        List<ChildGroupAssignment> assignments)
    {
        if (!groupChildren)
        {
            return new List<BakeGroup>
            {
                new BakeGroup
                {
                    name = "Renderer",
                    captureWholeEffect = true,
                    systems = allSystems
                }
            };
        }

        var assignmentByPath = new Dictionary<string, ChildGroupAssignment>();
        foreach (ChildGroupAssignment assignment in assignments)
            assignmentByPath[assignment.hierarchyPath] = assignment;

        var groupByName = new Dictionary<string, List<ParticleSystem>>(
            StringComparer.OrdinalIgnoreCase);
        var orderedNames = new List<string>();

        foreach (ParticleSystem system in allSystems)
        {
            string hierarchyPath = GetRelativeIndexPath(
                sourceRoot, system.transform);
            if (!assignmentByPath.TryGetValue(
                    hierarchyPath, out ChildGroupAssignment assignment) ||
                string.IsNullOrWhiteSpace(assignment.groupName))
            {
                continue;
            }

            string groupName = assignment.groupName.Trim();

            if (!groupByName.TryGetValue(
                    groupName, out List<ParticleSystem> groupSystems))
            {
                groupSystems = new List<ParticleSystem>();
                groupByName.Add(groupName, groupSystems);
                orderedNames.Add(groupName);
            }

            groupSystems.Add(system);
        }

        var groups = new List<BakeGroup>();
        foreach (string groupName in orderedNames)
        {
            ParticleSystem[] systems = groupByName[groupName].ToArray();
            groups.Add(new BakeGroup
            {
                name = groupName,
                includesSourceRoot = Array.Exists(
                    systems, system => system.transform == sourceRoot),
                systems = systems
            });
        }

        return groups;
    }

    private static void SetGroupRendererVisibility(
        Renderer[] allRenderers,
        Dictionary<Renderer, bool> originalStates,
        Transform sourceRoot,
        BakeGroup group)
    {
        var includedSystems = new HashSet<ParticleSystem>(group.systems);

        foreach (Renderer renderer in allRenderers)
        {
            bool belongsToGroup = group.captureWholeEffect;
            if (!belongsToGroup)
            {
                ParticleSystem owner = renderer.GetComponent<ParticleSystem>();
                if (owner == null)
                    owner = renderer.GetComponentInParent<ParticleSystem>();

                belongsToGroup = owner != null
                    ? includedSystems.Contains(owner)
                    : group.includesSourceRoot && renderer.transform == sourceRoot;
            }

            renderer.enabled =
                belongsToGroup &&
                originalStates.TryGetValue(renderer, out bool wasEnabled) &&
                wasEnabled;
        }
    }
    private static int FindIsolationLayer(GameObject bakeInstance)
    {
        // This project's URP transparent mask includes layers 0-24.
        // Prefer an unused high layer so the temporary camera cannot capture scene renderers.
        var usedLayers = new bool[25];
        foreach (Renderer renderer in FindObjectsOfType<Renderer>(true))
        {
            if (renderer == null ||
                renderer.transform.IsChildOf(bakeInstance.transform) ||
                !renderer.gameObject.scene.IsValid())
            {
                continue;
            }

            int layer = renderer.gameObject.layer;
            if (layer >= 0 && layer < usedLayers.Length)
                usedLayers[layer] = true;
        }

        for (int layer = usedLayers.Length - 1; layer >= 0; layer--)
        {
            if (!usedLayers[layer])
                return layer;
        }

        // All compatible layers are occupied. Layer 24 remains renderable by the
        // active URP renderer; the camera framing still limits possible contamination.
        return 24;
    }

    private static void SetLayerRecursively(GameObject root, int layer)
    {
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            child.gameObject.layer = layer;
    }

    private static void ConfigureCamera(Camera camera)
    {
        Camera templateCamera = FindTemplateCamera(camera);
        if (templateCamera != null)
            camera.CopyFrom(templateCamera);

        camera.enabled = false;
        camera.orthographic = true;
        camera.aspect = 1.0f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.clear;
        camera.targetTexture = null;
        camera.rect = new Rect(0, 0, 1, 1);
        camera.allowHDR = false;
        camera.allowMSAA = false;
        camera.depthTextureMode = DepthTextureMode.Depth;
        camera.forceIntoRenderTexture = true;
        camera.useOcclusionCulling = false;
        camera.cullingMask = ~0;
        Quaternion viewRotation = templateCamera != null
            ? templateCamera.transform.rotation
            : Quaternion.identity;
        camera.transform.SetPositionAndRotation(Vector3.zero, viewRotation);
        ConfigurePipelineCamera(camera, templateCamera);
    }

    private static Camera FindTemplateCamera(Camera temporaryCamera)
    {
        Camera main = Camera.main;
        if (main != null && main != temporaryCamera)
            return main;

        foreach (Camera candidate in FindObjectsOfType<Camera>(true))
        {
            if (candidate != temporaryCamera && candidate.gameObject.scene.IsValid())
                return candidate;
        }

        return null;
    }

    private static void ConfigurePipelineCamera(Camera camera, Camera templateCamera)
    {
        Type dataType = Type.GetType(
            "UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, " +
            "Unity.RenderPipelines.Universal.Runtime");
        if (dataType == null)
            return;

        Component data = camera.GetComponent(dataType) ?? camera.gameObject.AddComponent(dataType);
        Component templateData = templateCamera != null ? templateCamera.GetComponent(dataType) : null;
        if (templateData != null)
            EditorUtility.CopySerialized(templateData, data);

        var stackProperty = dataType.GetProperty("cameraStack");
        object cameraStack = stackProperty != null ? stackProperty.GetValue(data, null) : null;
        cameraStack?.GetType().GetMethod("Clear")?.Invoke(cameraStack, null);

        SetEnumProperty(data, "renderType", "Base");
        SetBooleanProperty(data, "requiresDepthTexture", true);
        SetBooleanProperty(data, "requiresColorTexture", true);
    }

    private static void SetEnumProperty(Component component, string propertyName, string valueName)
    {
        var property = component.GetType().GetProperty(propertyName);
        if (property != null && property.CanWrite && property.PropertyType.IsEnum)
            property.SetValue(component, Enum.Parse(property.PropertyType, valueName), null);
    }

    private static void SetBooleanProperty(Component component, string propertyName, bool value)
    {
        var property = component.GetType().GetProperty(propertyName);
        if (property != null && property.CanWrite && property.PropertyType == typeof(bool))
            property.SetValue(component, value, null);
    }

    private static void LockSeeds(ParticleSystem[] systems)
    {
        unchecked
        {
            uint sessionSeed = (uint)Environment.TickCount;
            for (int i = 0; i < systems.Length; i++)
            {
                systems[i].useAutoRandomSeed = false;
                systems[i].randomSeed = sessionSeed + (uint)(i * 2654435761u);
            }
        }
    }

    private static float EstimateDuration(ParticleSystem[] systems)
    {
        float duration = 0.0f;
        foreach (ParticleSystem system in systems)
        {
            ParticleSystem.MainModule main = system.main;
            float candidate = main.startDelay.constantMax + main.duration + main.startLifetime.constantMax;
            duration = Mathf.Max(duration, candidate);
        }

        return Mathf.Clamp(duration + 1.0f / FrameRate, 1.0f / FrameRate, MaximumDuration);
    }

    private static CaptureLayout FitCamera(
        Camera camera, Transform sourceRoot, ParticleSystem[] systems, float duration)
    {
        bool found = false;
        Vector3 minimum = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 maximum = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        int sampleCount = Mathf.CeilToInt(duration * FrameRate) + 1;

        Quaternion cameraRotation = camera.transform.rotation;
        Quaternion worldToCameraRotation = Quaternion.Inverse(cameraRotation);
        var particleBuffers = new Dictionary<ParticleSystem, ParticleSystem.Particle[]>();

        for (int frame = 0; frame < sampleCount; frame++)
        {
            float time = Mathf.Min(frame / (float)FrameRate, duration);
            SimulateAt(systems, time);

            foreach (ParticleSystem system in systems)
            {
                int liveCount = system.particleCount;
                if (liveCount <= 0)
                    continue;

                var renderer = system.GetComponent<ParticleSystemRenderer>();
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;

                if (!particleBuffers.TryGetValue(system, out ParticleSystem.Particle[] particles) ||
                    particles.Length < liveCount)
                {
                    particles = new ParticleSystem.Particle[
                        Mathf.NextPowerOfTwo(Mathf.Max(1, liveCount))];
                    particleBuffers[system] = particles;
                }

                int particleCount = system.GetParticles(particles);
                for (int particleIndex = 0; particleIndex < particleCount; particleIndex++)
                {
                    ParticleSystem.Particle particle = particles[particleIndex];
                    Vector3 worldPosition = GetParticleWorldPosition(system, particle);
                    float radius = GetParticleWorldRadius(system, renderer, particle);
                    Vector3 cameraLocal = worldToCameraRotation * worldPosition;
                    Vector3 extent = Vector3.one * radius;
                    minimum = Vector3.Min(minimum, cameraLocal - extent);
                    maximum = Vector3.Max(maximum, cameraLocal + extent);
                    found = true;
                }
            }

            EditorUtility.DisplayProgressBar(
                "VFX Baker", "Measuring live particle bounds...",
                frame / (float)Mathf.Max(1, sampleCount - 1));
        }

        if (!found)
            throw new InvalidOperationException(
                "The particle systems produced no live particles during the detected duration.");

        Vector3 centerInCameraSpace = (minimum + maximum) * 0.5f;
        float halfSize = Mathf.Max(
            (maximum.x - minimum.x) * 0.5f,
            (maximum.y - minimum.y) * 0.5f);
        halfSize = Mathf.Max(halfSize * CameraPadding, 0.001f);

        float cameraZ = minimum.z - DepthPadding;
        Vector3 cameraPositionInViewSpace = new Vector3(
            centerInCameraSpace.x, centerInCameraSpace.y, cameraZ);
        camera.transform.SetPositionAndRotation(
            cameraRotation * cameraPositionInViewSpace, cameraRotation);
        camera.orthographicSize = halfSize;
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = Mathf.Max(
            DepthPadding * 2.0f, maximum.z - cameraZ + DepthPadding);

        float sourceDepth = (worldToCameraRotation * sourceRoot.position).z;
        Vector3 planeCenterWorld = cameraRotation * new Vector3(
            centerInCameraSpace.x, centerInCameraSpace.y, sourceDepth);
        Quaternion rootInverseRotation = Quaternion.Inverse(sourceRoot.rotation);

        return new CaptureLayout
        {
            worldSize = halfSize * 2.0f,
            childLocalPosition = rootInverseRotation * (planeCenterWorld - sourceRoot.position),
            childLocalRotation = rootInverseRotation * cameraRotation
        };
    }

    private static Vector3 GetParticleWorldPosition(
        ParticleSystem system, ParticleSystem.Particle particle)
    {
        ParticleSystem.MainModule main = system.main;
        switch (main.simulationSpace)
        {
            case ParticleSystemSimulationSpace.World:
                return particle.position;

            case ParticleSystemSimulationSpace.Custom:
                Transform customSpace = main.customSimulationSpace;
                return customSpace != null
                    ? customSpace.TransformPoint(particle.position)
                    : system.transform.TransformPoint(particle.position);

            default:
                return system.transform.TransformPoint(particle.position);
        }
    }

    private static float GetParticleWorldRadius(
        ParticleSystem system,
        ParticleSystemRenderer renderer,
        ParticleSystem.Particle particle)
    {
        Vector3 size = particle.GetCurrentSize3D(system);
        float scale = GetParticleScale(system);
        float radius;

        if (renderer.renderMode == ParticleSystemRenderMode.Mesh && renderer.mesh != null)
        {
            radius = renderer.mesh.bounds.extents.magnitude *
                     Mathf.Max(size.x, Mathf.Max(size.y, size.z));
        }
        else
        {
            radius = 0.5f * Mathf.Sqrt(size.x * size.x + size.y * size.y);
        }

        if (renderer.renderMode == ParticleSystemRenderMode.Stretch)
        {
            float velocityExtension =
                particle.velocity.magnitude * Mathf.Abs(renderer.velocityScale) / FrameRate;
            radius += velocityExtension + Mathf.Abs(renderer.lengthScale) * Mathf.Max(size.x, size.y) * 0.5f;
        }

        return Mathf.Max(radius * scale, 0.0001f);
    }

    private static float GetParticleScale(ParticleSystem system)
    {
        ParticleSystem.MainModule main = system.main;
        Vector3 scale;

        switch (main.scalingMode)
        {
            case ParticleSystemScalingMode.Hierarchy:
                scale = system.transform.lossyScale;
                break;

            case ParticleSystemScalingMode.Local:
                scale = system.transform.localScale;
                break;

            default:
                return 1.0f;
        }

        return Mathf.Max(
            Mathf.Abs(scale.x),
            Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
    }

    private static void SimulateAt(ParticleSystem[] systems, float time)
    {
        var includedSystems = new HashSet<ParticleSystem>(systems);

        foreach (ParticleSystem system in systems)
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        foreach (ParticleSystem system in systems)
        {
            ParticleSystem parent = system.transform.parent != null
                ? system.transform.parent.GetComponentInParent<ParticleSystem>()
                : null;
            if (parent == null || !includedSystems.Contains(parent))
                system.Simulate(time, true, true, true);
        }
    }

    private static CaptureDiagnostics CaptureFrames(
        Camera camera,
        ParticleSystem[] systems,
        float duration,
        int atlasTileSize,
        List<CapturedFrame> output,
        out int firstVisibleFrame)
    {
        int sampleCount = Mathf.CeilToInt(duration * FrameRate) + 1;
        var diagnostics = new CaptureDiagnostics { sampledFrames = sampleCount };
        var particleBuffers = new Dictionary<ParticleSystem, ParticleSystem.Particle[]>();
        var descriptor = new RenderTextureDescriptor(
            CaptureSize, CaptureSize, RenderTextureFormat.ARGB32, 24)
        {
            msaaSamples = 1,
            sRGB = false,
            useMipMap = false,
            autoGenerateMips = false
        };

        firstVisibleFrame = -1;
        int lastVisibleTimelineIndex = -1;
        RenderTexture target = RenderTexture.GetTemporary(descriptor);
        Texture2D black = new Texture2D(
            CaptureSize, CaptureSize, TextureFormat.RGBA32, false, true);
        Texture2D white = new Texture2D(
            CaptureSize, CaptureSize, TextureFormat.RGBA32, false, true);

        try
        {
            for (int frame = 0; frame < sampleCount; frame++)
            {
                float time = Mathf.Min(frame / (float)FrameRate, duration);
                SimulateAt(systems, time);

                int centersInView = CountParticleCentersInView(
                    camera, systems, particleBuffers, out int liveParticles);
                if (liveParticles > 0)
                    diagnostics.framesWithParticles++;
                diagnostics.maxLiveParticles = Mathf.Max(
                    diagnostics.maxLiveParticles, liveParticles);
                diagnostics.maxParticleCentersInView = Mathf.Max(
                    diagnostics.maxParticleCentersInView, centersInView);

                EditorUtility.DisplayProgressBar(
                    "VFX Baker", $"Capturing frame {frame + 1}/{sampleCount}",
                    frame / (float)Mathf.Max(1, sampleCount - 1));

                Render(camera, target, new Color(0, 0, 0, 0), black);
                Render(camera, target, new Color(1, 1, 1, 0), white);

                Color32[] blackPixels = black.GetPixels32();
                Color32[] whitePixels = white.GetPixels32();
                Color32[] reconstructed = ReconstructStraightAlpha(
                    blackPixels,
                    whitePixels,
                    out RectInt contentBounds,
                    out float blackSignal,
                    out float coverageSignal);

                float previousBestSignal = Mathf.Max(
                    diagnostics.maxBlackSignal, diagnostics.maxCoverageSignal);
                float currentSignal = Mathf.Max(blackSignal, coverageSignal);
                if (diagnostics.bestFrame < 0 && liveParticles > 0 ||
                    currentSignal > previousBestSignal)
                {
                    diagnostics.bestFrame = frame;
                    diagnostics.bestBlackPixels = (Color32[])blackPixels.Clone();
                    diagnostics.bestWhitePixels = (Color32[])whitePixels.Clone();
                }

                diagnostics.maxBlackSignal = Mathf.Max(
                    diagnostics.maxBlackSignal, blackSignal);
                diagnostics.maxCoverageSignal = Mathf.Max(
                    diagnostics.maxCoverageSignal, coverageSignal);

                bool visible =
                    contentBounds.width > 0 && contentBounds.height > 0;
                if (firstVisibleFrame < 0)
                {
                    if (!visible)
                        continue;

                    firstVisibleFrame = frame;
                }

                if (visible)
                {
                    output.Add(CropSquare(
                        reconstructed, contentBounds, atlasTileSize));
                    lastVisibleTimelineIndex = output.Count - 1;
                }
                else
                {
                    // Middle/trailing empty samples occupy time but no atlas area.
                    output.Add(new CapturedFrame());
                }
            }

            if (lastVisibleTimelineIndex >= 0 &&
                lastVisibleTimelineIndex + 1 < output.Count)
            {
                output.RemoveRange(
                    lastVisibleTimelineIndex + 1,
                    output.Count - lastVisibleTimelineIndex - 1);
            }
        }
        finally
        {
            camera.targetTexture = null;
            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(target);
            DestroyImmediate(black);
            DestroyImmediate(white);
        }

        return diagnostics;
    }

    private static int CountVisibleFrames(List<CapturedFrame> frames)
    {
        int count = 0;
        foreach (CapturedFrame frame in frames)
        {
            if (frame.texture != null)
                count++;
        }

        return count;
    }
    private static int CountParticleCentersInView(
        Camera camera,
        ParticleSystem[] systems,
        Dictionary<ParticleSystem, ParticleSystem.Particle[]> buffers,
        out int liveParticles)
    {
        liveParticles = 0;
        int centersInView = 0;

        foreach (ParticleSystem system in systems)
        {
            int requestedCount = system.particleCount;
            if (requestedCount <= 0)
                continue;

            if (!buffers.TryGetValue(system, out ParticleSystem.Particle[] particles) ||
                particles.Length < requestedCount)
            {
                particles = new ParticleSystem.Particle[
                    Mathf.NextPowerOfTwo(Mathf.Max(1, requestedCount))];
                buffers[system] = particles;
            }

            int count = system.GetParticles(particles);
            liveParticles += count;
            for (int i = 0; i < count; i++)
            {
                Vector3 viewport = camera.WorldToViewportPoint(
                    GetParticleWorldPosition(system, particles[i]));
                if (viewport.z >= camera.nearClipPlane &&
                    viewport.z <= camera.farClipPlane &&
                    viewport.x >= 0 && viewport.x <= 1 &&
                    viewport.y >= 0 && viewport.y <= 1)
                {
                    centersInView++;
                }
            }
        }

        return centersInView;
    }

    private static void Render(Camera camera, RenderTexture target, Color background, Texture2D destination)
    {
        RenderTexture previous = RenderTexture.active;
        Color previousBackground = camera.backgroundColor;
        RenderTexture previousTarget = camera.targetTexture;

        try
        {
            camera.backgroundColor = background;
            camera.targetTexture = target;
            RenderTexture.active = target;
            GL.Clear(true, true, background);
            camera.Render();
            destination.ReadPixels(new Rect(0, 0, CaptureSize, CaptureSize), 0, 0, false);
            destination.Apply(false, false);
        }
        finally
        {
            camera.backgroundColor = previousBackground;
            camera.targetTexture = previousTarget;
            RenderTexture.active = previous;
        }
    }

    private static Color32[] ReconstructStraightAlpha(
        Color32[] blackPixels,
        Color32[] whitePixels,
        out RectInt contentBounds,
        out float maxBlackSignal,
        out float maxCoverageSignal)
    {
        var result = new Color32[blackPixels.Length];
        maxBlackSignal = 0.0f;
        maxCoverageSignal = 0.0f;
        int minX = CaptureSize;
        int minY = CaptureSize;
        int maxX = -1;
        int maxY = -1;
        bool encodeGamma = QualitySettings.activeColorSpace == ColorSpace.Linear;

        for (int i = 0; i < result.Length; i++)
        {
            Color b = blackPixels[i];
            Color w = whitePixels[i];

            float transmission = Mathf.Clamp01(
                ((w.r - b.r) + (w.g - b.g) + (w.b - b.b)) / 3.0f);
            float coverageAlpha = 1.0f - transmission;

            // Additive shaders commonly write no useful alpha. Their black-background
            // emission is converted into equivalent straight-alpha energy.
            float emissionAlpha = Mathf.Max(b.r, Mathf.Max(b.g, b.b));
            maxBlackSignal = Mathf.Max(maxBlackSignal, emissionAlpha);
            maxCoverageSignal = Mathf.Max(maxCoverageSignal, coverageAlpha);
            float alpha = Mathf.Clamp01(Mathf.Max(coverageAlpha, emissionAlpha));

            if (alpha <= AlphaThreshold)
            {
                result[i] = new Color32(0, 0, 0, 0);
                continue;
            }

            float red = Mathf.Clamp01(b.r / alpha);
            float green = Mathf.Clamp01(b.g / alpha);
            float blue = Mathf.Clamp01(b.b / alpha);
            if (encodeGamma)
            {
                red = Mathf.LinearToGammaSpace(red);
                green = Mathf.LinearToGammaSpace(green);
                blue = Mathf.LinearToGammaSpace(blue);
            }

            result[i] = new Color(red, green, blue, alpha);

            int x = i % CaptureSize;
            int y = i / CaptureSize;
            minX = Mathf.Min(minX, x);
            minY = Mathf.Min(minY, y);
            maxX = Mathf.Max(maxX, x);
            maxY = Mathf.Max(maxY, y);
        }

        contentBounds = maxX < minX
            ? new RectInt()
            : new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
        return result;
    }

    private static CapturedFrame CropSquare(
        Color32[] pixels, RectInt content, int atlasTileSize)
    {
        int required = Mathf.Max(content.width, content.height) + CropPadding * 2;
        int side = Mathf.Min(CaptureSize, Mathf.NextPowerOfTwo(Mathf.Max(1, required)));

        int centerX = content.xMin + content.width / 2;
        int centerY = content.yMin + content.height / 2;
        int x = Mathf.Clamp(centerX - side / 2, 0, CaptureSize - side);
        int y = Mathf.Clamp(centerY - side / 2, 0, CaptureSize - side);
        var rect = new RectInt(x, y, side, side);
        var cropped = new Color32[side * side];

        for (int row = 0; row < side; row++)
            Array.Copy(pixels, (y + row) * CaptureSize + x, cropped, row * side, side);

        var texture = new Texture2D(side, side, TextureFormat.RGBA32, false, false)
        {
            name = "CapturedFrame",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        texture.SetPixels32(cropped);
        texture.Apply(false, false);

        Texture2D atlasTile = ResizeFrame(texture, atlasTileSize);
        if (atlasTile != texture)
            DestroyImmediate(texture);

        return new CapturedFrame { texture = atlasTile, sourceRect = rect };
    }

    private static Texture2D ResizeFrame(Texture2D source, int targetSize)
    {
        if (source.width == targetSize && source.height == targetSize)
            return source;

        RenderTexture previous = RenderTexture.active;
        RenderTexture target = RenderTexture.GetTemporary(
            targetSize,
            targetSize,
            0,
            RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.sRGB);
        target.filterMode = FilterMode.Bilinear;
        target.wrapMode = TextureWrapMode.Clamp;

        var result = new Texture2D(
            targetSize, targetSize, TextureFormat.RGBA32, false, false)
        {
            name = "CapturedFrame_256",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        try
        {
            Graphics.Blit(source, target);
            RenderTexture.active = target;
            result.ReadPixels(new Rect(0, 0, targetSize, targetSize), 0, 0, false);
            result.Apply(false, false);
            return result;
        }
        catch
        {
            DestroyImmediate(result);
            throw;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
        }
    }

    private static Vector2Int PackFrames(List<CapturedFrame> frames)
    {
        var order = new List<int>(frames.Count);
        long area = 0;
        int largest = 1;
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i].texture == null)
                continue;

            order.Add(i);
            int side = frames[i].texture.width;
            largest = Mathf.Max(largest, side);
            area += (long)side * side;
        }

        order.Sort((a, b) =>
            frames[b].texture.width.CompareTo(frames[a].texture.width));

        int maximumTextureSize = SystemInfo.maxTextureSize;
        int width = Mathf.NextPowerOfTwo(Mathf.Max(largest, Mathf.CeilToInt(Mathf.Sqrt(area))));
        width = Mathf.Min(width, maximumTextureSize);

        while (width <= maximumTextureSize)
        {
            int x = 0;
            int y = 0;
            int rowHeight = 0;

            foreach (int index in order)
            {
                int side = frames[index].texture.width;
                if (x + side > width)
                {
                    x = 0;
                    y += rowHeight;
                    rowHeight = 0;
                }

                frames[index].atlasRect = new RectInt(x, y, side, side);
                x += side;
                rowHeight = Mathf.Max(rowHeight, side);
            }

            int usedHeight = y + rowHeight;
            int height = Mathf.NextPowerOfTwo(Mathf.Max(1, usedHeight));
            if (height <= maximumTextureSize)
                return new Vector2Int(width, height);

            if (width == maximumTextureSize)
                break;
            width = Mathf.Min(width * 2, maximumTextureSize);
        }

        throw new InvalidOperationException(
            $"Packed atlas exceeds the GPU maximum texture size ({maximumTextureSize}).");
    }

    private string SaveCaptureDiagnostics(
        CaptureDiagnostics diagnostics, ParticleSystem[] systems, string groupName)
    {
        string folder = OutputRoot + "/Diagnostics";
        EnsureFolder(OutputRoot);
        EnsureFolder(folder);

        string safeName = MakeSafeName(sourcePrefab != null ? sourcePrefab.name : "UnknownVFX");
        safeName += "_" + MakeSafeName(groupName);
        if (diagnostics.bestBlackPixels != null && diagnostics.bestWhitePixels != null)
        {
            string prefix = folder + "/" + safeName;
            SaveDiagnosticPng(
                diagnostics.bestBlackPixels, prefix + "_Black.png", true);
            SaveDiagnosticPng(
                diagnostics.bestWhitePixels, prefix + "_White.png", true);

            Color32[] reconstructed = ReconstructStraightAlpha(
                diagnostics.bestBlackPixels,
                diagnostics.bestWhitePixels,
                out _,
                out _,
                out _);
            SaveDiagnosticPng(
                reconstructed, prefix + "_Reconstructed.png", false);
        }

        AssetDatabase.Refresh();
        return folder;
    }

    private static void SaveDiagnosticPng(
        Color32[] pixels, string assetPath, bool forceOpaque)
    {
        var copy = (Color32[])pixels.Clone();
        if (forceOpaque)
        {
            for (int i = 0; i < copy.Length; i++)
                copy[i].a = 255;
        }

        var texture = new Texture2D(
            CaptureSize, CaptureSize, TextureFormat.RGBA32, false, true);
        try
        {
            texture.SetPixels32(copy);
            texture.Apply(false, false);
            File.WriteAllBytes(ToAbsolutePath(assetPath), texture.EncodeToPNG());
        }
        finally
        {
            DestroyImmediate(texture);
        }

        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
    }

    private static string BuildCaptureFailureMessage(
        CaptureDiagnostics diagnostics,
        Camera camera,
        Transform sourceRoot,
        string diagnosticFolder)
    {
        string cause;
        if (diagnostics.maxLiveParticles <= 0)
        {
            cause = "Simulation mismatch: capture pass contained no live particles.";
        }
        else if (diagnostics.maxParticleCentersInView <= 0)
        {
            cause = "Camera/frustum error: live particle centers were outside the camera view.";
        }
        else if (diagnostics.maxBlackSignal <= AlphaThreshold &&
                 diagnostics.maxCoverageSignal <= AlphaThreshold)
        {
            cause =
                "Render-path error: particles were inside the camera, but the camera rendered only its backgrounds. " +
                "Check the Console renderer report, source shader passes, URP Renderer Features, and camera filtering.";
        }
        else
        {
            cause =
                "Alpha reconstruction error: the raw render contained a signal, but no usable frame was produced.";
        }

        return
            cause +
            $" Live max: {diagnostics.maxLiveParticles}, in-view max: " +
            $"{diagnostics.maxParticleCentersInView}, black signal: " +
            $"{diagnostics.maxBlackSignal:F5}, coverage signal: " +
            $"{diagnostics.maxCoverageSignal:F5}. " +
            $"Camera pos: {camera.transform.position}, rotation: " +
            $"{camera.transform.eulerAngles}, ortho: {camera.orthographicSize:F4}, " +
            $"clip: {camera.nearClipPlane:F3}-{camera.farClipPlane:F3}. " +
            $"Diagnostic images: {diagnosticFolder}.";
    }

    private static void LogRendererDiagnostics(
        Camera camera, ParticleSystem[] systems)
    {
        var report = new System.Text.StringBuilder();
        report.AppendLine("VFX Baker renderer diagnostics");
        report.AppendLine(
            $"Camera mask: 0x{camera.cullingMask:X8}, scene: {camera.gameObject.scene.name}");

        foreach (ParticleSystem system in systems)
        {
            var renderer = system.GetComponent<ParticleSystemRenderer>();
            if (renderer == null)
            {
                report.AppendLine($"{system.name}: missing ParticleSystemRenderer");
                continue;
            }

            report.AppendLine(
                $"{system.name}: active={renderer.gameObject.activeInHierarchy}, " +
                $"enabled={renderer.enabled}, forceOff={renderer.forceRenderingOff}, " +
                $"layer={renderer.gameObject.layer}, mode={renderer.renderMode}");

            Material[] materials = renderer.sharedMaterials;
            if (materials == null || materials.Length == 0)
            {
                report.AppendLine("  material: none");
                continue;
            }

            for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
            {
                Material material = materials[materialIndex];
                if (material == null)
                {
                    report.AppendLine($"  material[{materialIndex}]: null");
                    continue;
                }

                Shader shader = material.shader;
                report.AppendLine(
                    $"  material[{materialIndex}]={material.name}, " +
                    $"shader={(shader != null ? shader.name : "null")}, " +
                    $"supported={(shader != null && shader.isSupported)}, " +
                    $"queue={material.renderQueue}, passes={material.passCount}");
            }
        }

        Debug.LogError(report.ToString());
    }

    private static Texture2D BuildAtlas(List<CapturedFrame> frames, Vector2Int size, string atlasName)
    {
        var atlas = new Texture2D(size.x, size.y, TextureFormat.RGBA32, false, false)
        {
            name = atlasName,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        var clear = new Color32[size.x * size.y];
        atlas.SetPixels32(clear);
        foreach (CapturedFrame frame in frames)
        {
            if (frame.texture == null)
                continue;

            atlas.SetPixels32(
                frame.atlasRect.x, frame.atlasRect.y,
                frame.atlasRect.width, frame.atlasRect.height,
                frame.texture.GetPixels32());
        }

        atlas.Apply(false, false);
        return atlas;
    }

    private static BakedGroupResult SaveGroupResult(
        List<CapturedFrame> frames,
        Vector2Int atlasSize,
        CaptureLayout layout,
        string folder,
        string assetBaseName,
        string groupName,
        bool createGroupPrefab,
        float playDelay)
    {
        string atlasPath = folder + "/" + assetBaseName + "_Atlas.png";
        string animPath = folder + "/" + assetBaseName + "_Anim.asset";
        string materialPath = folder + "/" + assetBaseName + "_Material.mat";
        string legacyAssetPath = folder + "/" + assetBaseName + "_BakedVFX.asset";
        string safeGroupName = MakeSafeName(groupName);
        string groupPrefabPath = folder + "/" + safeGroupName + ".prefab";
        string legacyGroupPrefabPath =
            folder + "/" + assetBaseName + "_Baked.prefab";

        if (createGroupPrefab)
        {
            DeleteAsset(groupPrefabPath);
            if (legacyGroupPrefabPath != groupPrefabPath)
                DeleteAsset(legacyGroupPrefabPath);
        }
        DeleteAsset(animPath);
        DeleteAsset(materialPath);
        DeleteAsset(legacyAssetPath);

        Texture2D atlas = BuildAtlas(frames, atlasSize, assetBaseName + "_Atlas");
        File.WriteAllBytes(ToAbsolutePath(atlasPath), atlas.EncodeToPNG());
        DestroyImmediate(atlas);
        AssetDatabase.ImportAsset(atlasPath, ImportAssetOptions.ForceSynchronousImport);
        ConfigureAtlasImporter(atlasPath, atlasSize);
        Texture2D importedAtlas = AssetDatabase.LoadAssetAtPath<Texture2D>(atlasPath);

        Texture2D animTexture = BuildAnimationTexture(
            frames, atlasSize, layout, out Bounds localBounds);
        animTexture.name = assetBaseName + "_AnimationData";
        AssetDatabase.CreateAsset(animTexture, animPath);

        Shader shader = Shader.Find("Custom/BakedParticleAtlas");
        if (shader == null)
            throw new InvalidOperationException("Shader 'Custom/BakedParticleAtlas' was not found.");

        var material = new Material(shader) { name = assetBaseName + "_Material" };
        material.SetTexture("_MainTex", importedAtlas);
        material.SetTexture("_AnimTex", animTexture);
        material.SetFloat("_FrameNum", frames.Count);
        material.SetFloat("_FrameTime", 1.0f / FrameRate);
        material.SetFloat("_Cull", (float)CullMode.Off);
        material.SetFloat("_ZWrite", 0.0f);
        material.renderQueue = (int)RenderQueue.Transparent;
        AssetDatabase.CreateAsset(material, materialPath);

        Mesh sharedQuad = GetOrCreateSharedQuad();

        GameObject groupPrefab = null;
        if (createGroupPrefab)
        {
            SaveSingleGroupPrefab(
                groupPrefabPath,
                safeGroupName,
                sharedQuad,
                material,
                localBounds,
                layout,
                playDelay);
            groupPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(groupPrefabPath);
        }

        return new BakedGroupResult
        {
            name = groupName,
            material = material,
            quadMesh = sharedQuad,
            localBounds = localBounds,
            layout = layout,
            groupPrefab = groupPrefab,
            playDelay = playDelay
        };
    }
    private static Texture2D BuildAnimationTexture(
        List<CapturedFrame> frames, Vector2Int atlasSize, CaptureLayout layout, out Bounds localBounds)
    {
        var texture = new Texture2D(frames.Count, 2, TextureFormat.RGBAFloat, false, true)
        {
            name = "BakedVFX_AnimationData",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };

        float pixelWorld = layout.worldSize / CaptureSize;
        bool initialized = false;
        localBounds = new Bounds();

        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i].texture == null)
            {
                // A zero-sized quad represents an empty timeline frame.
                texture.SetPixel(i, 0, Color.clear);
                texture.SetPixel(i, 1, Color.clear);
                continue;
            }

            RectInt source = frames[i].sourceRect;
            float centerX = (source.x + source.width * 0.5f - CaptureSize * 0.5f) * pixelWorld;
            float centerY = (source.y + source.height * 0.5f - CaptureSize * 0.5f) * pixelWorld;
            float scaleX = source.width * pixelWorld;
            float scaleY = source.height * pixelWorld;

            texture.SetPixel(i, 0, new Color(centerX, centerY, scaleX, scaleY));

            RectInt atlas = frames[i].atlasRect;
            texture.SetPixel(i, 1, new Color(
                atlas.x / (float)atlasSize.x,
                atlas.y / (float)atlasSize.y,
                atlas.width / (float)atlasSize.x,
                atlas.height / (float)atlasSize.y));

            Bounds frameBounds = new Bounds(
                new Vector3(centerX, centerY, 0),
                new Vector3(scaleX, scaleY, 0.1f));
            if (!initialized)
            {
                localBounds = frameBounds;
                initialized = true;
            }
            else
            {
                localBounds.Encapsulate(frameBounds);
            }
        }

        texture.Apply(false, true);
        return texture;
    }

    private static void SaveSingleGroupPrefab(
        string path,
        string groupName,
        Mesh quadMesh,
        Material material,
        Bounds localBounds,
        CaptureLayout layout,
        float playDelay)
    {
        var root = new GameObject(groupName);
        try
        {
            root.transform.localPosition = layout.childLocalPosition;
            root.transform.localRotation = layout.childLocalRotation;
            root.transform.localScale = Vector3.one;
            ConfigureRenderer(root, quadMesh, material, localBounds, playDelay);
            PrefabUtility.SaveAsPrefabAsset(root, path);
        }
        finally
        {
            DestroyImmediate(root);
        }
    }

    private static void SaveOutputPrefab(
        string path, string safeName, List<BakedGroupResult> groups)
    {
        var root = new GameObject(safeName + "_Baked");
        try
        {
            foreach (BakedGroupResult group in groups)
            {
                if (group.groupPrefab != null)
                {
                    GameObject groupInstance =
                        PrefabUtility.InstantiatePrefab(group.groupPrefab) as GameObject;
                    if (groupInstance == null)
                        throw new InvalidOperationException(
                            $"Unable to instantiate baked child prefab '{group.name}'.");

                    groupInstance.transform.SetParent(root.transform, false);
                    groupInstance.transform.localPosition =
                        group.layout.childLocalPosition;
                    groupInstance.transform.localRotation =
                        group.layout.childLocalRotation;
                    groupInstance.transform.localScale = Vector3.one;
                }
                else
                {
                    AddRendererObject(
                        root.transform, "Renderer", group.quadMesh,
                        group.material, group.localBounds, group.layout,
                        group.playDelay);
                }
            }

            PrefabUtility.SaveAsPrefabAsset(root, path);
        }
        finally
        {
            DestroyImmediate(root);
        }
    }

    private static void AddRendererObject(
        Transform parent,
        string objectName,
        Mesh quadMesh,
        Material material,
        Bounds localBounds,
        CaptureLayout layout,
        float playDelay)
    {
        var rendererObject = new GameObject(objectName);
        rendererObject.transform.SetParent(parent, false);
        rendererObject.transform.localPosition = layout.childLocalPosition;
        rendererObject.transform.localRotation = layout.childLocalRotation;
        rendererObject.transform.localScale = Vector3.one;

        ConfigureRenderer(rendererObject, quadMesh, material, localBounds, playDelay);
    }

    private static void ConfigureRenderer(
        GameObject target,
        Mesh quadMesh,
        Material material,
        Bounds localBounds,
        float playDelay)
    {
        var filter = target.AddComponent<MeshFilter>();
        var renderer = target.AddComponent<MeshRenderer>();
        var player = target.AddComponent<BakedVFXPlayer>();

        filter.sharedMesh = quadMesh;
        renderer.sharedMaterial = material;
        renderer.localBounds = localBounds;
        player.PlayDelay = playDelay;
    }
    private static Mesh GetOrCreateSharedQuad()
    {
        EnsureFolder(OutputRoot);
        EnsureFolder(OutputRoot + "/Shared");
        Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(SharedQuadPath);
        bool create = mesh == null;
        if (create)
            mesh = new Mesh { name = "BakedVFX_UnitQuad" };
        else
            mesh.Clear();

        mesh.vertices = new[]
        {
            new Vector3(-0.5f, -0.5f, 0),
            new Vector3( 0.5f, -0.5f, 0),
            new Vector3( 0.5f,  0.5f, 0),
            new Vector3(-0.5f,  0.5f, 0)
        };
        mesh.uv = new[]
        {
            new Vector2(0, 0), new Vector2(1, 0),
            new Vector2(1, 1), new Vector2(0, 1)
        };
        mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
        mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one);

        if (create)
            AssetDatabase.CreateAsset(mesh, SharedQuadPath);
        else
            EditorUtility.SetDirty(mesh);

        return mesh;
    }

    private static void ConfigureAtlasImporter(string path, Vector2Int atlasSize)
    {
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null)
            throw new InvalidOperationException("Unable to configure the generated atlas importer.");

        importer.textureType = TextureImporterType.Default;
        importer.alphaSource = TextureImporterAlphaSource.FromInput;
        importer.alphaIsTransparency = true;
        importer.sRGBTexture = true;
        importer.mipmapEnabled = false;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.filterMode = FilterMode.Bilinear;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.npotScale = TextureImporterNPOTScale.None;
        importer.maxTextureSize = Mathf.Max(atlasSize.x, atlasSize.y);
        importer.SaveAndReimport();
    }

    private static void EnsureFolder(string assetFolder)
    {
        string[] segments = assetFolder.Split('/');
        string current = segments[0];
        for (int i = 1; i < segments.Length; i++)
        {
            string next = current + "/" + segments[i];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, segments[i]);
            current = next;
        }
    }

    private static void DeleteAsset(string path)
    {
        if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path) != null)
            AssetDatabase.DeleteAsset(path);
    }

    private static string ToAbsolutePath(string assetPath)
    {
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        return Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string MakeSafeName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(value) ? "BakedVFX" : value;
    }
}
#endif

