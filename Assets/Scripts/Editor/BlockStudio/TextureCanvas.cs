#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Colour painting canvas for the Block Studio. Works on raw texture pixels (data) shown through
/// toDisplay; a painted colour is turned back into data by fromDisplay, so the same canvas edits
/// full colour textures and palette surfaces (where data is shade + palette position).
///
/// Brush strokes blend each texel once per stroke at the brush opacity (no build-up while
/// dragging). Right click or Alt + click picks a colour; the view can tile the texture 3 x 3 so
/// strokes continue across the seams.
/// </summary>
public class TextureCanvas
{
    public enum Tool { Brush, Fill, Picker }

    public Color[] data;
    public int size;
    public Tool tool;
    public Color brushColour = new Color(0.5f, 0.5f, 0.5f, 1f);
    [Range(0f, 1f)] public float opacity = 1f;
    public int brushSize = 1;
    public float fillTolerance = 0.06f;
    public bool tiled;

    /// <summary>Data pixel to its displayed sRGB colour.</summary>
    public Func<Color, Color> toDisplay = c => c;
    /// <summary>(data before the stroke, wanted display colour) to the new data pixel.</summary>
    public Func<Color, Color, Color> fromDisplay = (b, want) => new Color(want.r, want.g, want.b, b.a);
    /// <summary>Called before a stroke starts (e.g. to refresh a colour solver).</summary>
    public Action beginStroke;

    public readonly List<Color> recent = new();

    readonly List<Color[]> undo = new();
    readonly List<Color[]> redo = new();
    Color[] strokeBase;
    bool[] touched;
    Texture2D display;
    bool displayDirty = true;

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public void Load(Color[] pixels, int newSize)
    {
        data = pixels;
        size = newSize;
        undo.Clear();
        redo.Clear();
        displayDirty = true;
    }

    /// <summary>Replaces the pixels keeping the undo history (edits made elsewhere).</summary>
    public void SetData(Color[] pixels)
    {
        data = pixels;
        displayDirty = true;
    }

    public void MarkDisplayDirty() => displayDirty = true;

    public void PushUndo()
    {
        undo.Add((Color[])data.Clone());
        if (undo.Count > 64)
            undo.RemoveAt(0);
        redo.Clear();
    }

    public void Undo()
    {
        if (undo.Count == 0) return;
        redo.Add(data);
        data = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        displayDirty = true;
    }

    public void Redo()
    {
        if (redo.Count == 0) return;
        undo.Add(data);
        data = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        displayDirty = true;
    }

    public void RememberColour(Color c)
    {
        c.a = 1f;
        recent.RemoveAll(r => ((Vector4)(r - c)).sqrMagnitude < 1e-6f);
        recent.Insert(0, c);
        if (recent.Count > 12)
            recent.RemoveAt(recent.Count - 1);
    }

    /// <summary>
    /// Draws and handles input. blockLines: texels per block, drawn as stronger lines (0 = none).
    /// Returns true when an edit finished (stroke released or area filled).
    /// </summary>
    public bool Draw(Rect rect, float blockLines)
    {
        if (data == null || size <= 0)
            return false;

        if (display == null || display.width != size)
        {
            Object.DestroyImmediate(display);
            display = BlockStudioPreview.NewTexture(size);
            display.wrapMode = TextureWrapMode.Repeat;
            displayDirty = true;
        }
        if (displayDirty)
        {
            var pixels = new Color[data.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = toDisplay(data[i]);
                pixels[i].a = 1f;
            }
            display.SetPixels(pixels);
            display.Apply(false);
            displayDirty = false;
        }

        int tiles = tiled ? 3 : 1;
        int texels = size * tiles;
        float cell = rect.width / texels;
        int id = GUIUtility.GetControlID(FocusType.Passive);
        var e = Event.current;
        bool finished = false;

        switch (e.GetTypeForControl(id))
        {
            case EventType.Repaint:
                GUI.DrawTextureWithTexCoords(rect, display, new Rect(0f, 0f, tiles, tiles));
                DrawGrid(rect, texels, cell, blockLines);
                if (tiled)
                {
                    var centre = new Rect(rect.x + size * cell, rect.y + size * cell, size * cell, size * cell);
                    Handles.DrawSolidRectangleWithOutline(centre, Color.clear, new Color(1f, 1f, 1f, 0.8f));
                }
                if (rect.Contains(e.mousePosition) && tool == Tool.Brush)
                    DrawBrushOutline(rect, cell, e.mousePosition);
                break;

            case EventType.MouseDown:
                if (!rect.Contains(e.mousePosition))
                    break;
                if (e.button == 1 || (e.button == 0 && (e.alt || tool == Tool.Picker)))
                {
                    brushColour = toDisplay(data[Index(TexelAt(rect, cell, e.mousePosition))]);
                    brushColour.a = 1f;
                    e.Use();
                    break;
                }
                if (e.button != 0)
                    break;
                beginStroke?.Invoke();
                PushUndo();
                RememberColour(brushColour);
                if (tool == Tool.Fill)
                {
                    Fill(TexelAt(rect, cell, e.mousePosition));
                    finished = true;
                    e.Use();
                    break;
                }
                GUIUtility.hotControl = id;
                strokeBase = (Color[])data.Clone();
                touched = new bool[data.Length];
                Stamp(TexelAt(rect, cell, e.mousePosition));
                e.Use();
                break;

            case EventType.MouseDrag:
                if (GUIUtility.hotControl != id)
                    break;
                Stamp(TexelAt(rect, cell, e.mousePosition));
                e.Use();
                break;

            case EventType.MouseUp:
                if (GUIUtility.hotControl != id)
                    break;
                GUIUtility.hotControl = 0;
                strokeBase = null;
                finished = true;
                e.Use();
                break;

            case EventType.MouseMove:
                if (rect.Contains(e.mousePosition))
                    HandleUtility.Repaint();
                break;
        }
        return finished;
    }

    void DrawGrid(Rect rect, int texels, float cell, float blockLines)
    {
        var thin = new Color(0f, 0f, 0f, 0.15f);
        var thick = new Color(1f, 1f, 1f, 0.45f);
        bool showThin = cell >= 6f;
        for (int i = 0; i <= texels; i++)
        {
            int local = i % size;
            bool block = blockLines > 0f && Mathf.Abs(local / blockLines - Mathf.Round(local / blockLines)) < 1e-3f;
            if (!block && !showThin)
                continue;
            var col = block ? thick : thin;
            float w = block ? 1.5f : 1f;
            EditorGUI.DrawRect(new Rect(rect.x + i * cell - w * 0.5f, rect.y, w, rect.height), col);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y + i * cell - w * 0.5f, rect.width, w), col);
        }
    }

    void DrawBrushOutline(Rect rect, float cell, Vector2 mouse)
    {
        // Screen texel under the mouse (not wrapped), so the outline follows the cursor in tiled view.
        int sx = Mathf.Clamp((int)((mouse.x - rect.x) / cell), 0, int.MaxValue);
        int sy = Mathf.Clamp((int)((mouse.y - rect.y) / cell), 0, int.MaxValue);
        var color = new Color(brushColour.r, brushColour.g, brushColour.b, 0.35f * Mathf.Max(opacity, 0.3f));
        foreach (var o in BrushOffsets())
        {
            var r = new Rect(rect.x + (sx + o.x) * cell, rect.y + (sy - o.y) * cell, cell, cell);
            if (r.xMax > rect.x && r.x < rect.xMax && r.yMax > rect.y && r.y < rect.yMax)
                EditorGUI.DrawRect(r, color);
        }
        Handles.color = Color.yellow;
        var c = new Rect(rect.x + sx * cell, rect.y + sy * cell, cell, cell);
        Handles.DrawSolidRectangleWithOutline(c, Color.clear, Color.yellow);
    }

    IEnumerable<Vector2Int> BrushOffsets()
    {
        int half = (brushSize - 1) / 2;
        float r2 = (brushSize * 0.5f) * (brushSize * 0.5f) + 0.25f;
        for (int dy = -half; dy < brushSize - half; dy++)
            for (int dx = -half; dx < brushSize - half; dx++)
            {
                // Round brushes from size 3 up.
                float cx = dx + (brushSize % 2 == 0 ? 0.5f : 0f), cy = dy + (brushSize % 2 == 0 ? 0.5f : 0f);
                if (brushSize >= 3 && cx * cx + cy * cy > r2)
                    continue;
                yield return new Vector2Int(dx, dy);
            }
    }

    Vector2Int TexelAt(Rect rect, float cell, Vector2 mouse)
    {
        int x = (int)((mouse.x - rect.x) / cell);
        int row = (int)((mouse.y - rect.y) / cell);
        x = ((x % size) + size) % size;
        row = ((row % size) + size) % size;
        return new Vector2Int(x, size - 1 - row);   // texture rows go bottom up
    }

    int Index(Vector2Int t) => t.x + t.y * size;

    Color Blend(Color before)
    {
        Color shown = toDisplay(before);
        Color want = Color.LerpUnclamped(shown, brushColour, opacity);
        want.a = 1f;
        return fromDisplay(before, want);
    }

    void Stamp(Vector2Int t)
    {
        foreach (var o in BrushOffsets())
        {
            int x = ((t.x + o.x) % size + size) % size;
            int y = ((t.y + o.y) % size + size) % size;
            int i = x + y * size;
            if (touched[i])
                continue;
            touched[i] = true;
            data[i] = Blend(strokeBase[i]);
        }
        displayDirty = true;
    }

    /// <summary>Flood fills texels connected to t (wrapping) whose displayed colour is within the tolerance.</summary>
    void Fill(Vector2Int t)
    {
        var seed = toDisplay(data[Index(t)]);
        var source = (Color[])data.Clone();
        var done = new bool[data.Length];
        var stack = new Stack<Vector2Int>();
        stack.Push(t);
        float tol2 = fillTolerance * fillTolerance * 3f;
        while (stack.Count > 0)
        {
            var p = stack.Pop();
            int i = Index(p);
            if (done[i])
                continue;
            done[i] = true;
            var c = toDisplay(source[i]);
            float d = (c.r - seed.r) * (c.r - seed.r) + (c.g - seed.g) * (c.g - seed.g) + (c.b - seed.b) * (c.b - seed.b);
            if (d > tol2)
                continue;
            data[i] = Blend(source[i]);
            stack.Push(new Vector2Int((p.x + 1) % size, p.y));
            stack.Push(new Vector2Int((p.x + size - 1) % size, p.y));
            stack.Push(new Vector2Int(p.x, (p.y + 1) % size));
            stack.Push(new Vector2Int(p.x, (p.y + size - 1) % size));
        }
        displayDirty = true;
    }

    public void Dispose() => Object.DestroyImmediate(display);
}
#endif
