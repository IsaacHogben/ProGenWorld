using System;
using UnityEditor;
using UnityEngine;

/// <summary>
/// A pannable, zoomable region generator map for editor windows: drag (left or middle mouse) to
/// move, scroll to zoom about the cursor. The last render is moved and scaled at once while the
/// view changes, and the map is rendered again once the view has been still for a moment.
/// The host window calls Tick from its update and repaints when it returns true.
/// </summary>
public sealed class RegionMapView : IDisposable
{
    public RegionGenSettingsSO settings;
    public BiomeDataManager biomeDataManager;
    public int seed = 1337;

    public Vector2 center;
    public float extent = 100000f;
    public int resolution = 512;
    public RegionMapWindow.Overlay overlay = RegionMapWindow.Overlay.Shapes3D;
    public int onlyBiome = -1;   // >= 0: every climate cell holds just this biome
    public bool autoUpdate = true;

    public const float MinExtent = 64f, MaxExtent = 4000000f;
    const double SettleSeconds = 0.35;

    RegionMapWindow.MapResult result;
    Vector2 renderedCenter;
    float renderedExtent;
    double changedAt = -1;
    bool dragging;
    bool mouseOver;
    Vector2 mouseWorld;
    readonly int controlHint = "RegionMapView".GetHashCode();

    public bool CanRender => settings != null && biomeDataManager != null;
    public bool HasMap => result?.texture != null;
    public string Stats => result?.stats ?? "";

    /// <summary>Something that changes the map changed: render again once things settle.</summary>
    public void Changed() => changedAt = EditorApplication.timeSinceStartup;

    /// <summary>Renders when a change has settled. True when the host should repaint.</summary>
    public bool Tick()
    {
        if (changedAt < 0 || dragging || !autoUpdate || !CanRender)
            return false;
        if (EditorApplication.timeSinceStartup - changedAt < SettleSeconds)
            return false;
        changedAt = -1;
        Generate();
        return true;
    }

    public void Generate()
    {
        if (!CanRender)
            return;
        changedAt = -1;
        var next = RegionMapWindow.RenderMap(settings, biomeDataManager, seed, center, extent, resolution, overlay, onlyBiome);
        result?.Dispose();
        result = next;
        renderedCenter = center;
        renderedExtent = extent;
    }

    public void ResetView(float width = 100000f)
    {
        center = Vector2.zero;
        extent = width;
        Changed();
    }

    // World x, z at a point in the map rect (screen up = +z)
    Vector2 ToWorld(Rect rect, Vector2 p)
    {
        float blocksPerPixel = extent / rect.width;
        return new Vector2(center.x - extent * 0.5f + (p.x - rect.x) * blocksPerPixel,
                           center.y + extent * 0.5f - (p.y - rect.y) * blocksPerPixel);
    }

    /// <summary>Handles input in rect and draws the map there (rect should be square).</summary>
    public void OnGUI(Rect rect, EditorWindow host)
    {
        Input(rect, host);
        Draw(rect);
    }

    void Input(Rect rect, EditorWindow host)
    {
        var e = Event.current;
        int id = GUIUtility.GetControlID(controlHint, FocusType.Passive, rect);
        bool inside = rect.Contains(e.mousePosition);
        if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag)
        {
            bool over = inside || dragging;
            if (over)
                mouseWorld = ToWorld(rect, e.mousePosition);
            if (over || mouseOver)
                host.Repaint();
            mouseOver = over;
        }

        switch (e.GetTypeForControl(id))
        {
            case EventType.MouseDown when inside && (e.button == 0 || e.button == 2):
                dragging = true;
                GUIUtility.hotControl = id;
                e.Use();
                break;

            case EventType.MouseDrag when dragging && GUIUtility.hotControl == id:
            {
                float blocksPerPixel = extent / rect.width;
                center.x -= e.delta.x * blocksPerPixel;
                center.y += e.delta.y * blocksPerPixel;
                e.Use();
                host.Repaint();
                break;
            }

            case EventType.MouseUp when dragging && GUIUtility.hotControl == id:
                dragging = false;
                GUIUtility.hotControl = 0;
                Changed();
                e.Use();
                break;

            case EventType.ScrollWheel when inside:
            {
                // Zoom about the point under the cursor
                Vector2 anchor = ToWorld(rect, e.mousePosition);
                float newExtent = Mathf.Clamp(extent * Mathf.Pow(1.15f, e.delta.y), MinExtent, MaxExtent);
                center = anchor + (center - anchor) * (newExtent / extent);
                extent = newExtent;
                Changed();
                e.Use();
                host.Repaint();
                break;
            }
        }
    }

    void Draw(Rect rect)
    {
        if (Event.current.type != EventType.Repaint)
            return;
        EditorGUI.DrawRect(rect, new Color(0.13f, 0.13f, 0.13f));
        if (!HasMap)
        {
            GUI.Label(rect, CanRender ? "Generate to see the map." : "Assign Settings and Biome Data.", EditorStyles.centeredGreyMiniLabel);
            return;
        }

        // The rendered view placed in the current one, so moving and zooming show at once
        float pixelsPerBlock = rect.width / extent;
        float x = (renderedCenter.x - renderedExtent * 0.5f - (center.x - extent * 0.5f)) * pixelsPerBlock;
        float y = ((center.y + extent * 0.5f) - (renderedCenter.y + renderedExtent * 0.5f)) * pixelsPerBlock;
        float w = renderedExtent * pixelsPerBlock;
        GUI.BeginClip(rect);
        GUI.DrawTexture(new Rect(x, y, w, w), result.texture, ScaleMode.StretchToFill, false);
        GUI.EndClip();
    }

    /// <summary>Position, biome, height (and sub-biome) under the cursor, or a usage hint.</summary>
    public string Readout()
    {
        if (!mouseOver)
            return "Drag to move, scroll to zoom.";
        string s = $"x {mouseWorld.x:F0}, z {mouseWorld.y:F0}";
        if (result == null || result.resolution <= 0)
            return s;
        float spacing = renderedExtent / result.resolution;
        int px = Mathf.FloorToInt((mouseWorld.x - (renderedCenter.x - renderedExtent * 0.5f)) / spacing);
        int pz = Mathf.FloorToInt((mouseWorld.y - (renderedCenter.y - renderedExtent * 0.5f)) / spacing);
        if (px < 0 || pz < 0 || px >= result.resolution || pz >= result.resolution)
            return s;
        int i = px + pz * result.resolution;
        s = $"{s}:  {(BiomeType)result.palette[i]}, height {result.heights[i]:F0}";
        if (result.layerNames != null && result.layer[i] >= 0)
            s += $", {result.layerNames[result.layer[i]]} {result.layerMask[i]:F2}";
        return s;
    }

    public void Dispose()
    {
        result?.Dispose();
        result = null;
    }
}
