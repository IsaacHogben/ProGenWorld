#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Paints one value per texel on a square grid (relief heights or alpha mask) in the Block
/// Studio. Left drag paints the brush value, right click picks the value under the cursor.
/// </summary>
public class TexelPainter
{
    public float[] values;
    public int size;
    public float brushValue = 0f;
    public int brushSize = 1;
    public bool dirty;               // painted since the last save

    readonly List<float[]> undo = new();
    Texture2D display;
    bool displayDirty = true;

    public bool CanUndo => undo.Count > 0;

    public void Load(float[] newValues, int newSize)
    {
        values = newValues;
        size = newSize;
        undo.Clear();
        dirty = false;
        displayDirty = true;
    }

    public void Set(float[] newValues)
    {
        PushUndo();
        values = newValues;
        dirty = true;
        displayDirty = true;
    }

    public void Undo()
    {
        if (undo.Count == 0)
            return;
        values = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        dirty = true;
        displayDirty = true;
    }

    public void MarkDisplayDirty() => displayDirty = true;

    void PushUndo()
    {
        if (values == null)
            return;
        undo.Add((float[])values.Clone());
        if (undo.Count > 64)
            undo.RemoveAt(0);
    }

    /// <summary>
    /// Draws the grid. colourOf maps (texel index, value) to the displayed colour. blockLines:
    /// texels per block, drawn as stronger lines (0 = none). Returns true when a stroke ended.
    /// </summary>
    public bool Draw(Rect rect, System.Func<int, float, Color> colourOf, float blockLines)
    {
        if (values == null || size <= 0)
            return false;

        if (display == null || display.width != size)
        {
            Object.DestroyImmediate(display);
            display = BlockStudioPreview.NewTexture(size);
            displayDirty = true;
        }
        if (displayDirty)
        {
            var pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = colourOf(i, values[i]);
            display.SetPixels(pixels);
            display.Apply(false);
            displayDirty = false;
        }

        int id = GUIUtility.GetControlID(FocusType.Passive);
        var e = Event.current;
        bool strokeEnded = false;

        switch (e.GetTypeForControl(id))
        {
            case EventType.Repaint:
                EditorGUI.DrawPreviewTexture(rect, display);
                float cell = rect.width / size;
                var thin = new Color(0f, 0f, 0f, 0.18f);
                var thick = new Color(1f, 1f, 1f, 0.55f);
                for (int i = 0; i <= size; i++)
                {
                    bool block = blockLines > 0f && Mathf.Abs(i / blockLines - Mathf.Round(i / blockLines)) < 1e-3f;
                    var col = block ? thick : thin;
                    float w = block ? 2f : 1f;
                    EditorGUI.DrawRect(new Rect(rect.x + i * cell - w * 0.5f, rect.y, w, rect.height), col);
                    EditorGUI.DrawRect(new Rect(rect.x, rect.y + i * cell - w * 0.5f, rect.width, w), col);
                }
                if (rect.Contains(e.mousePosition))
                {
                    var t = TexelAt(rect, e.mousePosition);
                    int half = (brushSize - 1) / 2;
                    var r = new Rect(rect.x + (t.x - half) * cell, rect.y + (size - 1 - t.y - half) * cell, brushSize * cell, brushSize * cell);
                    Handles.DrawSolidRectangleWithOutline(r, new Color(1f, 1f, 1f, 0.15f), Color.yellow);
                }
                break;

            case EventType.MouseDown:
                if (!rect.Contains(e.mousePosition))
                    break;
                if (e.button == 1)
                {
                    var t = TexelAt(rect, e.mousePosition);
                    brushValue = values[t.x + t.y * size];
                    e.Use();
                    break;
                }
                if (e.button != 0)
                    break;
                GUIUtility.hotControl = id;
                PushUndo();
                Paint(rect, e.mousePosition);
                e.Use();
                break;

            case EventType.MouseDrag:
                if (GUIUtility.hotControl != id)
                    break;
                Paint(rect, e.mousePosition);
                e.Use();
                break;

            case EventType.MouseUp:
                if (GUIUtility.hotControl != id)
                    break;
                GUIUtility.hotControl = 0;
                strokeEnded = true;
                e.Use();
                break;

            case EventType.MouseMove:
                if (rect.Contains(e.mousePosition))
                    HandleUtility.Repaint();
                break;
        }
        return strokeEnded;
    }

    Vector2Int TexelAt(Rect rect, Vector2 mouse)
    {
        int x = Mathf.Clamp((int)((mouse.x - rect.x) / rect.width * size), 0, size - 1);
        int row = Mathf.Clamp((int)((mouse.y - rect.y) / rect.height * size), 0, size - 1);
        return new Vector2Int(x, size - 1 - row);   // texture rows go bottom up
    }

    void Paint(Rect rect, Vector2 mouse)
    {
        var t = TexelAt(rect, mouse);
        int half = (brushSize - 1) / 2;
        for (int dy = 0; dy < brushSize; dy++)
            for (int dx = 0; dx < brushSize; dx++)
            {
                // Wraps around: the texture tiles, so strokes continue on the opposite edge.
                int x = ((t.x - half + dx) % size + size) % size;
                int y = ((t.y + half - dy) % size + size) % size;
                values[x + y * size] = brushValue;
            }
        dirty = true;
        displayDirty = true;
    }

    public void Dispose()
    {
        Object.DestroyImmediate(display);
    }
}
#endif
