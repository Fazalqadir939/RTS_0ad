#if UNITY_EDITOR
using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

/// <summary>
/// Editor tool: reads the manifest.json + mask PNGs produced by
/// pmp_to_splatmap.py and paints them onto a Unity Terrain as alphamap
/// layers.
///
/// Each texture referenced in the .pmp gets its own TerrainLayer. If you
/// haven't imported the real 0 A.D. terrain texture yet for a given name,
/// this generates a solid-color placeholder so you can validate tile
/// boundaries/pattern correctness before swapping in real art.
///
/// Usage: Tools > RTS > Import Terrain Splatmap
/// Must live in a folder named "Editor" anywhere under Assets.
/// </summary>
public class TerrainSplatmapImporter : EditorWindow
{
    private Terrain targetTerrain;
    private DefaultAsset manifestFolder; // folder containing splatmap_manifest.json + mask_*.png
    private DefaultAsset realTextureFolder; // optional: folder to look for real diffuse textures by name

    [Serializable]
    private class LayerEntry
    {
        public int index;
        public string name;
        public string mask_file;
        public int tile_count;
        public float coverage_pct;
    }

    [Serializable]
    private class Manifest
    {
        public int tiles_per_side;
        public float world_size_metres;
        public List<LayerEntry> layers;
    }

    [MenuItem("Tools/RTS/Import Terrain Splatmap")]
    public static void ShowWindow()
    {
        GetWindow<TerrainSplatmapImporter>("Terrain Splatmap Importer");
    }

    private void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Reads splatmap_manifest.json + mask_*.png (from pmp_to_splatmap.py) " +
            "and paints them onto a Terrain. Textures without a matching real " +
            "asset get a solid-color placeholder so you can validate the tile " +
            "pattern before real art is ready.",
            MessageType.Info);

        targetTerrain = (Terrain)EditorGUILayout.ObjectField(
            "Target Terrain", targetTerrain, typeof(Terrain), true);

        manifestFolder = (DefaultAsset)EditorGUILayout.ObjectField(
            new GUIContent("Manifest Folder", "Folder containing splatmap_manifest.json and mask_*.png"),
            manifestFolder, typeof(DefaultAsset), false);

        realTextureFolder = (DefaultAsset)EditorGUILayout.ObjectField(
            new GUIContent("Real Textures Folder (optional)", "If a texture named <texname>.png/.jpg exists here, it's used instead of a placeholder color"),
            realTextureFolder, typeof(DefaultAsset), false);

        EditorGUI.BeginDisabledGroup(targetTerrain == null || manifestFolder == null);
        if (GUILayout.Button("Import and Paint Terrain"))
        {
            Import();
        }
        EditorGUI.EndDisabledGroup();
    }

    private void Import()
    {
        string manifestFolderPath = AssetDatabase.GetAssetPath(manifestFolder);
        string manifestPath = Path.Combine(manifestFolderPath, "splatmap_manifest.json");

        if (!File.Exists(manifestPath))
        {
            EditorUtility.DisplayDialog("Not found",
                $"Couldn't find splatmap_manifest.json in {manifestFolderPath}", "OK");
            return;
        }

        Manifest manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(manifestPath));
        if (manifest.layers == null || manifest.layers.Count == 0)
        {
            EditorUtility.DisplayDialog("Empty manifest", "No layers found in manifest.", "OK");
            return;
        }

        TerrainData terrainData = targetTerrain.terrainData;
        int tilesPerSide = manifest.tiles_per_side;

        // --- Build / find a TerrainLayer per texture ---
        TerrainLayer[] terrainLayers = new TerrainLayer[manifest.layers.Count];
        string layerAssetFolder = "Assets/Materials/TerrainLayers";
        if (!AssetDatabase.IsValidFolder(layerAssetFolder))
        {
            if (!AssetDatabase.IsValidFolder("Assets/Materials"))
                AssetDatabase.CreateFolder("Assets", "Materials");
            AssetDatabase.CreateFolder("Assets/Materials", "TerrainLayers");
        }

        for (int li = 0; li < manifest.layers.Count; li++)
        {
            LayerEntry entry = manifest.layers[li];
            string layerAssetPath = $"{layerAssetFolder}/{SanitizeFileName(entry.name)}.terrainlayer";

            TerrainLayer layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(layerAssetPath);
            if (layer == null)
            {
                layer = new TerrainLayer();
                layer.tileSize = new Vector2(4f, 4f); // matches TERRAIN_TILE_SIZE from the source
                Texture2D diffuse = FindRealTexture(entry.name) ?? MakePlaceholderTexture(entry.name, li);
                layer.diffuseTexture = diffuse;
                AssetDatabase.CreateAsset(layer, layerAssetPath);
            }
            terrainLayers[li] = layer;
        }
        terrainData.terrainLayers = terrainLayers;

        // --- Build the alphamap from the mask PNGs ---
        int alphamapRes = terrainData.alphamapResolution;
        float[,,] alphamaps = new float[alphamapRes, alphamapRes, manifest.layers.Count];

        // Load all masks first
        Texture2D[] masks = new Texture2D[manifest.layers.Count];
        for (int li = 0; li < manifest.layers.Count; li++)
        {
            string maskPath = Path.Combine(manifestFolderPath, manifest.layers[li].mask_file);
            byte[] bytes = File.ReadAllBytes(maskPath);
            Texture2D tex = new Texture2D(2, 2, TextureFormat.R8, false);
            tex.LoadImage(bytes);
            masks[li] = tex;
        }

        // Nearest-neighbour resample from tile grid resolution to the
        // terrain's alphamap resolution, then normalise per-pixel so
        // weights sum to 1 (required by SetAlphamaps).
        for (int y = 0; y < alphamapRes; y++)
        {
            for (int x = 0; x < alphamapRes; x++)
            {
                int srcX = Mathf.Min(tilesPerSide - 1, x * tilesPerSide / alphamapRes);
                int srcY = Mathf.Min(tilesPerSide - 1, y * tilesPerSide / alphamapRes);

                float sum = 0f;
                float[] weights = new float[manifest.layers.Count];
                for (int li = 0; li < manifest.layers.Count; li++)
                {
                    // masks are stored [row0=top ... ] matching Python's
                    // top-to-bottom PNG write order, y=0 at top.
                    float w = masks[li].GetPixel(srcX, tilesPerSide - 1 - srcY).r;
                    weights[li] = w;
                    sum += w;
                }

                for (int li = 0; li < manifest.layers.Count; li++)
                {
                    alphamaps[y, x, li] = sum > 0.0001f ? weights[li] / sum : (li == 0 ? 1f : 0f);
                }
            }
        }

        terrainData.SetAlphamaps(0, 0, alphamaps);

        foreach (Texture2D mask in masks)
            DestroyImmediate(mask);

        AssetDatabase.SaveAssets();
        Debug.Log($"Painted terrain with {manifest.layers.Count} layers at {alphamapRes}x{alphamapRes} alphamap resolution.");
        EditorUtility.DisplayDialog("Done",
            $"Painted {manifest.layers.Count} layers onto {targetTerrain.name}.\n\n" +
            "Any texture without a matching real asset was given a solid " +
            "placeholder color - check the Console for which ones.", "OK");
    }

    private Texture2D FindRealTexture(string textureName)
    {
        if (realTextureFolder == null) return null;
        string folderPath = AssetDatabase.GetAssetPath(realTextureFolder);
        string[] guids = AssetDatabase.FindAssets(textureName, new[] { folderPath });
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (tex != null)
                return tex;
        }
        return null;
    }

    private static readonly Color[] PlaceholderPalette = new[]
    {
        new Color(0.35f, 0.55f, 0.25f), // grass-ish green
        new Color(0.55f, 0.45f, 0.30f), // dirt-ish brown
        new Color(0.50f, 0.50f, 0.50f), // rock-ish gray
        new Color(0.75f, 0.70f, 0.45f), // sand-ish tan
        new Color(0.30f, 0.35f, 0.45f), // stone-ish blue-gray
        new Color(0.25f, 0.40f, 0.20f), // dark forest green
    };

    private Texture2D MakePlaceholderTexture(string textureName, int paletteIndex)
    {
        Debug.LogWarning($"No real texture found for '{textureName}' - using a placeholder color. " +
                          "Swap this in later once the real 0 A.D. texture is imported.");
        Color c = PlaceholderPalette[paletteIndex % PlaceholderPalette.Length];
        Texture2D tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        Color[] pixels = new Color[16];
        for (int i = 0; i < 16; i++) pixels[i] = c;
        tex.SetPixels(pixels);
        tex.Apply();

        string texFolder = "Assets/Materials/TerrainLayers/Placeholders";
        if (!AssetDatabase.IsValidFolder(texFolder))
            AssetDatabase.CreateFolder("Assets/Materials/TerrainLayers", "Placeholders");
        string texPath = $"{texFolder}/{SanitizeFileName(textureName)}_placeholder.asset";

        Texture2D existing = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
        if (existing != null) return existing;

        AssetDatabase.CreateAsset(tex, texPath);
        return tex;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
#endif