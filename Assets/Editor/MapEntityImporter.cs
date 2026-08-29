#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;

/// <summary>
/// Editor tool: reads an entities.json (environment/camera/entity list, as
/// produced from a 0 A.D. map's XML) and places one placeholder primitive
/// per entity on the given Terrain, colour-and-shape coded by category.
///
/// This is a Phase 1 validation tool - it does NOT map templates to real
/// prefabs (units/buildings/trees etc aren't imported yet). The goal is to
/// confirm entity POSITIONS and the overall layout (base locations, resource
/// distribution) are correct before spending time matching every template
/// to real art/prefabs.
///
/// Usage: Tools > RTS > Import Map Entities
/// Must live in a folder named "Editor" anywhere under Assets.
/// </summary>
public class MapEntityImporter : EditorWindow
{
    private Terrain targetTerrain;
    private TextAsset entitiesJson;
    private bool skipParticleAnchors = true;
    private bool stripColliders = true;

    [Serializable] private class Position { public float x; public float z; }

    [Serializable]
    private class EntityEntry
    {
        public string uid;
        public string template;
        public int player;
        public Position position;
        public float rotationY;
    }

    [Serializable]
    private class EntitiesFile
    {
        public int totalEntities;
        public List<EntityEntry> entities;
    }

    private struct CategoryStyle
    {
        public PrimitiveType shape;
        public Color color;
        public Vector3 scale;
        public CategoryStyle(PrimitiveType shape, Color color, Vector3 scale)
        {
            this.shape = shape; this.color = color; this.scale = scale;
        }
    }

    [MenuItem("Tools/RTS/Import Map Entities")]
    public static void ShowWindow()
    {
        GetWindow<MapEntityImporter>("Map Entity Importer");
    }

    private void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Places one placeholder primitive per entity, colour/shape-coded " +
            "by category, snapped to terrain height. This validates LAYOUT " +
            "(base positions, resource distribution) - it does not create " +
            "real game units or buildings yet.",
            MessageType.Info);

        targetTerrain = (Terrain)EditorGUILayout.ObjectField(
            "Target Terrain", targetTerrain, typeof(Terrain), true);

        entitiesJson = (TextAsset)EditorGUILayout.ObjectField(
            new GUIContent("Entities JSON", "The *_entities.json file, imported as a TextAsset"),
            entitiesJson, typeof(TextAsset), false);

        skipParticleAnchors = EditorGUILayout.Toggle(
            new GUIContent("Skip Particle Anchors", "actor|particle/* entries are FX spawn points, not visible objects - usually safe to skip"),
            skipParticleAnchors);

        stripColliders = EditorGUILayout.Toggle(
            new GUIContent("Strip Colliders", "Removes auto-added colliders from placeholders for performance (recommended at this object count)"),
            stripColliders);

        EditorGUI.BeginDisabledGroup(targetTerrain == null || entitiesJson == null);
        if (GUILayout.Button("Import and Place Entities"))
        {
            Import();
        }
        EditorGUI.EndDisabledGroup();
    }

    private static string CategoryFor(string template, int player)
    {
        if (template.StartsWith("skirmish/structures/")) return $"Player{player}_Structures";
        if (template.StartsWith("skirmish/units/")) return $"Player{player}_Units";
        if (template.StartsWith("gaia/flora_")) return "Gaia_Flora";
        if (template.StartsWith("gaia/fauna_")) return "Gaia_Fauna";
        if (template.StartsWith("gaia/geology_")) return "Gaia_Geology_Resources";
        if (template.StartsWith("actor|geology")) return "Decorative_Geology";
        if (template.StartsWith("actor|particle")) return "Particle_Anchors";
        return "Other";
    }

    private static CategoryStyle StyleFor(string category)
    {
        switch (category)
        {
            case "Gaia_Flora":
                return new CategoryStyle(PrimitiveType.Cylinder, new Color(0.2f, 0.5f, 0.2f), new Vector3(0.8f, 2.5f, 0.8f));
            case "Gaia_Fauna":
                return new CategoryStyle(PrimitiveType.Capsule, new Color(0.6f, 0.4f, 0.25f), new Vector3(0.7f, 0.7f, 0.7f));
            case "Gaia_Geology_Resources":
                return new CategoryStyle(PrimitiveType.Cube, new Color(0.55f, 0.55f, 0.6f), new Vector3(2f, 2f, 2f));
            case "Decorative_Geology":
                return new CategoryStyle(PrimitiveType.Cube, new Color(0.4f, 0.4f, 0.42f), new Vector3(1.2f, 1f, 1.2f));
            case "Particle_Anchors":
                return new CategoryStyle(PrimitiveType.Sphere, new Color(1f, 0f, 1f), new Vector3(0.3f, 0.3f, 0.3f));
            default:
                if (category.StartsWith("Player1"))
                    return category.EndsWith("Structures")
                        ? new CategoryStyle(PrimitiveType.Cube, new Color(0.2f, 0.4f, 0.9f), new Vector3(4f, 4f, 4f))
                        : new CategoryStyle(PrimitiveType.Capsule, new Color(0.3f, 0.5f, 0.95f), new Vector3(0.6f, 1f, 0.6f));
                if (category.StartsWith("Player2"))
                    return category.EndsWith("Structures")
                        ? new CategoryStyle(PrimitiveType.Cube, new Color(0.9f, 0.25f, 0.2f), new Vector3(4f, 4f, 4f))
                        : new CategoryStyle(PrimitiveType.Capsule, new Color(0.95f, 0.35f, 0.3f), new Vector3(0.6f, 1f, 0.6f));
                if (category.StartsWith("Player"))
                    return category.EndsWith("Structures")
                        ? new CategoryStyle(PrimitiveType.Cube, new Color(0.8f, 0.8f, 0.2f), new Vector3(4f, 4f, 4f))
                        : new CategoryStyle(PrimitiveType.Capsule, new Color(0.85f, 0.85f, 0.3f), new Vector3(0.6f, 1f, 0.6f));
                return new CategoryStyle(PrimitiveType.Sphere, Color.white, Vector3.one);
        }
    }

    private void Import()
    {
        EntitiesFile file = JsonUtility.FromJson<EntitiesFile>(entitiesJson.text);
        if (file == null || file.entities == null)
        {
            EditorUtility.DisplayDialog("Parse failed", "Couldn't parse the entities JSON - check its structure.", "OK");
            return;
        }

        GameObject root = new GameObject("MapEntities");
        Dictionary<string, Transform> categoryParents = new Dictionary<string, Transform>();
        Dictionary<string, Material> materialCache = new Dictionary<string, Material>();

        int total = file.entities.Count;
        int placed = 0, skipped = 0;

        try
        {
            for (int i = 0; i < total; i++)
            {
                if (i % 50 == 0)
                {
                    bool cancel = EditorUtility.DisplayCancelableProgressBar(
                        "Placing entities", $"{i}/{total}", (float)i / total);
                    if (cancel) break;
                }

                EntityEntry e = file.entities[i];
                string category = CategoryFor(e.template, e.player);

                if (skipParticleAnchors && category == "Particle_Anchors")
                {
                    skipped++;
                    continue;
                }

                if (!categoryParents.TryGetValue(category, out Transform parent))
                {
                    GameObject catGo = new GameObject(category);
                    catGo.transform.SetParent(root.transform);
                    parent = catGo.transform;
                    categoryParents[category] = parent;
                }

                CategoryStyle style = StyleFor(category);

                Vector3 worldPos = new Vector3(e.position.x, 0f, e.position.z);
                float terrainY = targetTerrain.SampleHeight(worldPos) + targetTerrain.GetPosition().y;
                worldPos.y = terrainY;

                GameObject go = GameObject.CreatePrimitive(style.shape);
                go.name = $"{e.uid}_{SanitizeTemplateName(e.template)}";
                go.transform.SetParent(parent);
                go.transform.position = worldPos;
                go.transform.localScale = style.scale;
                go.transform.rotation = Quaternion.Euler(0f, e.rotationY, 0f);

                if (stripColliders)
                {
                    Collider col = go.GetComponent<Collider>();
                    if (col != null) DestroyImmediate(col);
                }

                Renderer rend = go.GetComponent<Renderer>();
                if (rend != null)
                {
                    if (!materialCache.TryGetValue(category, out Material mat))
                    {
                        mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                        mat.color = style.color;
                        materialCache[category] = mat;
                    }
                    rend.sharedMaterial = mat;
                }

                placed++;
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        Debug.Log($"Placed {placed} entities ({skipped} skipped) across {categoryParents.Count} categories under '{root.name}'.");
        EditorUtility.DisplayDialog("Done",
            $"Placed {placed} entities, skipped {skipped}.\n\n" +
            "Check the Scene view top-down: you should see two colour-coded " +
            "player bases (blue/red) plus scattered green (trees), tan (animals) " +
            "and gray (resources).", "OK");
    }

    private static string SanitizeTemplateName(string template)
    {
        int lastSlash = template.LastIndexOfAny(new[] { '/', '|' });
        string tail = lastSlash >= 0 ? template.Substring(lastSlash + 1) : template;
        foreach (char c in Path.GetInvalidFileNameChars())
            tail = tail.Replace(c, '_');
        return tail;
    }
}
#endif
