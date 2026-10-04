using UnityEngine;
using UnityEngine.UI;

namespace LightSide
{
    // Building: the generated hierarchy, key layout, labels, background mesh, key states and the preview.
    public partial class UniTextKeyboard
    {
        private RectTransform contentRoot;

        private bool Shifted => shift != KeyboardShiftState.Off;

        private void EnsureBuilt()
        {
            if (built && contentRoot != null) return;
            built = true;
            isWorld = m_Mode == KeyboardSurfaceMode.World || (m_Mode == KeyboardSurfaceMode.Auto && GetComponentInParent<Canvas>() == null);
            BuildObjects();
            ApplyLayout(layout != null ? layout : TextLayout, layout != null ? page : 0);
            if (visibilityApplied) ApplyVisibility();
        }

        private GameObject Make(string objectName, Transform parent)
        {
            var go = new GameObject(objectName, typeof(RectTransform)) { hideFlags = HideFlags.DontSave, layer = gameObject.layer };
            go.transform.SetParent(parent, false);
            return go;
        }

        private static void Stretch(RectTransform rt, Vector2 pivot)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = pivot;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            rt.localPosition = Vector3.zero;
        }

        private void BuildObjects()
        {
            // Generated objects are DontSave: remove any left from a previous build (mode change, domain reload).
            for (var i = transform.childCount - 1; i >= 0; i--)
            {
                var c = transform.GetChild(i).gameObject;
                if ((c.hideFlags & HideFlags.DontSave) != 0 && c.name == "Keyboard Content") ObjectUtils.SafeDestroy(c);
            }
            keys.Clear();
            keyCount = 0;
            repeatKey = null;
            previewKey = null;

            var pivot = ((RectTransform)transform).pivot;
            var content = Make("Keyboard Content", transform);
            contentRoot = (RectTransform)content.transform;
            Stretch(contentRoot, pivot);

            var surface = Make("Surface", contentRoot);
            Stretch((RectTransform)surface.transform, pivot);
            var keysGo = Make("Keys", contentRoot);
            keysRoot = (RectTransform)keysGo.transform;
            Stretch(keysRoot, pivot);
            var preview = Make("Key Preview", contentRoot);
            previewRoot = (RectTransform)preview.transform;
            previewRoot.anchorMin = previewRoot.anchorMax = previewRoot.pivot = new Vector2(0.5f, 0.5f);
            var previewSurface = Make("Surface", previewRoot);
            Stretch((RectTransform)previewSurface.transform, new Vector2(0.5f, 0.5f));
            var previewText = Make("Label", previewRoot);
            Stretch((RectTransform)previewText.transform, new Vector2(0.5f, 0.5f));

            worldSurface = worldPreviewSurface = null;
            canvasSurface = canvasPreviewSurface = null;
            canvasGroup = null;
            if (isWorld)
            {
                worldSurface = surface.AddComponent<UniTextKeyboardWorldSurface>();
                worldSurface.Renderer.sortingOrder = 0;
                worldPreviewSurface = previewSurface.AddComponent<UniTextKeyboardWorldSurface>();
                worldPreviewSurface.Renderer.sortingOrder = 2;
                var pl = previewText.AddComponent<UniTextWorld>();
                UniTextWorld.ConfigureNewWorldText(pl, (RectTransform)previewText.transform, pl.WorldRenderer);
                pl.Collider = WorldTextCollider.None;
                pl.SortingOrder = 3;
                previewLabel = pl;
            }
            else
            {
                canvasGroup = content.AddComponent<CanvasGroup>();
                canvasSurface = surface.AddComponent<UniTextKeyboardCanvasSurface>();
                canvasPreviewSurface = previewSurface.AddComponent<UniTextKeyboardCanvasSurface>();
                previewLabel = previewText.AddComponent<UniText>();
            }
            ConfigureLabel(previewLabel);
            previewLabel.raycastTarget = false;
            preview.SetActive(false);
        }

        private void ConfigureLabel(UniText t)
        {
            t.Highlighter = null;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Middle;
            t.WordWrap = false;
            t.Overflow = TextOverflow.Overflow;
            ApplyFont(t);
        }

        private void ApplyFont(UniText t)
        {
            if (m_FontStack != null && t.FontStack != m_FontStack) t.FontStack = m_FontStack;
        }

        private UniTextKeyboardKey GetKey(int index)
        {
            if (index < keys.Count && keys[index] != null)
            {
                var existing = keys[index];
                if (!existing.gameObject.activeSelf) existing.gameObject.SetActive(true);
                return existing;
            }
            var go = Make("Key", keysRoot);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            UniText label;
            if (isWorld)
            {
                var w = go.AddComponent<UniTextWorld>();
                UniTextWorld.ConfigureNewWorldText(w, rt, w.WorldRenderer);
                w.SortingOrder = 1;
                w.Collider = visible ? WorldTextCollider.Always : WorldTextCollider.None;
                label = w;
            }
            else label = go.AddComponent<UniText>();
            ConfigureLabel(label);
            label.raycastTarget = true;
            var key = go.AddComponent<UniTextKeyboardKey>();
            key.keyboard = this;
            key.label = label;
            if (index < keys.Count) keys[index] = key; else keys.Add(key);
            return key;
        }

        /// <summary>Re-lays out the current page (after a size or colour change).</summary>
        private void Relayout()
        {
            if (!built || contentRoot == null) return;
            ApplyLayout(layout, page);
        }

        private Color BaseColor(KeyboardKey def) => def.action switch
        {
            KeyboardKeyAction.Enter => m_AccentKeyColor,
            KeyboardKeyAction.Text or KeyboardKeyAction.Space => m_KeyColor,
            _ => m_SpecialKeyColor,
        };

        private void ApplyLayout(UniTextKeyboardLayout l, int pageIndex)
        {
            if (contentRoot == null) return;
            layout = l != null ? l : UniTextKeyboardLayout.Qwerty;
            var pageCount = layout.pages != null ? layout.pages.Count : 0;
            page = pageCount == 0 ? 0 : Mathf.Clamp(pageIndex, 0, pageCount - 1);
            HidePreview();
            repeatKey = null;

            // The panel fits the largest page of the layout, so it keeps its size when switching pages.
            var maxUnits = 1f;
            var maxRows = 1;
            for (var p = 0; p < pageCount; p++)
            {
                var pg0 = layout.pages[p];
                if (pg0?.rows == null) continue;
                maxRows = Mathf.Max(maxRows, pg0.rows.Count);
                for (var r = 0; r < pg0.rows.Count; r++) if (pg0.rows[r] != null) maxUnits = Mathf.Max(maxUnits, pg0.rows[r].Units);
            }

            var unit = m_KeySize;
            var pad = m_Padding;
            var rt = (RectTransform)transform;
            rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, maxUnits * unit + 2f * pad);
            rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, maxRows * unit + 2f * pad);
            var rect = rt.rect;
            var pivot = rt.pivot;
            contentRoot.pivot = pivot;
            ((RectTransform)worldOrCanvasSurfaceTransform).pivot = pivot;
            keysRoot.pivot = pivot;
            contentRoot.localPosition = Vector3.zero;

            mesh.Clear();
            mesh.linear = isWorld && QualitySettings.activeColorSpace == ColorSpace.Linear;
            mesh.RoundedRect(rect, m_CornerRadius * 1.4f, m_PanelColor);

            var multi = CycleCount > 1;
            var ki = 0;
            var pg = layout.Page(page);
            if (pg?.rows != null)
            {
                var yTop = rect.yMax - pad - (maxRows - pg.rows.Count) * unit * 0.5f;
                for (var r = 0; r < pg.rows.Count; r++)
                {
                    var row = pg.rows[r];
                    if (row?.keys == null) continue;
                    // Without several layouts the layout key is left out and the space bar takes its width.
                    var hidden = 0f;
                    var hasSpace = false;
                    for (var i = 0; i < row.keys.Count; i++)
                    {
                        var d = row.keys[i];
                        if (d == null) continue;
                        if (!multi && d.action == KeyboardKeyAction.NextLayout) hidden += d.Width;
                        if (d.action == KeyboardKeyAction.Space) hasSpace = true;
                    }
                    var rowUnits = row.Units - (hasSpace ? 0f : hidden);
                    var x = rect.xMin + pad + (maxUnits - rowUnits) * 0.5f * unit + Mathf.Max(0f, row.indent) * unit;
                    var y1 = yTop - r * unit;
                    var spaceExtra = hasSpace ? hidden : 0f;
                    for (var i = 0; i < row.keys.Count; i++)
                    {
                        var def = row.keys[i];
                        if (def == null) continue;
                        if (!multi && def.action == KeyboardKeyAction.NextLayout) continue;
                        var wUnits = def.Width;
                        if (def.action == KeyboardKeyAction.Space) { wUnits += spaceExtra; spaceExtra = 0f; }
                        var w = wUnits * unit;
                        var kr = new Rect(x + m_KeyGap * 0.5f, y1 - unit + m_KeyGap * 0.5f, Mathf.Max(1f, w - m_KeyGap), Mathf.Max(1f, unit - m_KeyGap));
                        x += w;
                        var k = GetKey(ki++);
                        k.def = def;
                        k.hiddenByLayout = false;
                        k.localRect = kr;
                        var krt = (RectTransform)k.transform;
                        krt.anchoredPosition = kr.center - rect.center;
                        krt.sizeDelta = kr.size;
                        k.bgStart = mesh.RoundedRect(kr, m_CornerRadius, BaseColor(def));
                        k.bgCount = KeyboardMesh.RoundedRectVertexCount;
                        k.capsStart = k.capsCount = 0;
                        if (KeyboardMesh.HasIcon(def.action))
                        {
                            k.iconStart = mesh.Icon(def.action, kr, IconColor(def), out k.capsStart, out k.capsCount);
                            k.iconCount = mesh.VertexCount - k.iconStart;
                        }
                        else k.iconStart = k.iconCount = 0;
                        SetLabel(k);
                    }
                }
            }
            keyCount = ki;
            for (var i = ki; i < keys.Count; i++)
            {
                var k = keys[i];
                if (k == null) continue;
                k.pressCount = 0;
                k.hovered = false;
                if (k.gameObject.activeSelf) k.gameObject.SetActive(false);
            }
            for (var i = 0; i < keyCount; i++) SetKeyLook(keys[i]);
            ApplyShiftIcons();
            if (worldSurface != null) worldSurface.Apply(mesh);
            if (canvasSurface != null) canvasSurface.Apply(mesh);
            if (visibilityApplied) ApplyVisibility();
        }

        private Transform worldOrCanvasSurfaceTransform =>
            worldSurface != null ? worldSurface.transform : canvasSurface != null ? canvasSurface.transform : contentRoot;

        private Color IconColor(KeyboardKey def)
        {
            if (def.action == KeyboardKeyAction.Shift) return Shifted ? m_LabelColor : m_SpecialLabelColor;
            return def.action == KeyboardKeyAction.Enter ? Color.white : m_LabelColor;
        }

        private void SetLabel(UniTextKeyboardKey k)
        {
            var t = k.label;
            var def = k.def;
            var unit = m_KeySize;
            string s;
            float size;
            var color = m_SpecialLabelColor;
            var lang = string.Empty;
            switch (def.action)
            {
                case KeyboardKeyAction.Text:
                    s = def.Label(Shifted);
                    // Word labels (".com") are smaller than single letters / clusters.
                    size = unit * (s.Length >= 3 && s[0] < 0x0300 ? m_LabelScale * 0.66f : m_LabelScale);
                    color = m_LabelColor;
                    lang = layout != null ? layout.language ?? string.Empty : string.Empty;
                    break;
                case KeyboardKeyAction.Space:
                    s = CycleCount > 1 && layout != null ? layout.displayName ?? string.Empty : string.Empty;
                    size = unit * 0.27f;
                    lang = layout != null ? layout.language ?? string.Empty : string.Empty;
                    break;
                default:
                    s = KeyboardMesh.HasIcon(def.action) ? string.Empty : def.label ?? string.Empty;
                    size = unit * 0.3f;
                    break;
            }
            if (t.Language != lang) t.Language = lang;
            if (!Mathf.Approximately(t.FontSize, size)) t.FontSize = size;
            if (t.color != color) t.color = color;
            if (t.Text != s) t.Text = s;
        }

        private void RefreshShift()
        {
            if (!built || contentRoot == null) return;
            for (var i = 0; i < keyCount; i++)
                if (keys[i].def.action == KeyboardKeyAction.Text) SetLabel(keys[i]);
            ApplyShiftIcons();
            UploadColors();
        }

        private void ApplyShiftIcons()
        {
            for (var i = 0; i < keyCount; i++)
            {
                var k = keys[i];
                if (k.def.action != KeyboardKeyAction.Shift) continue;
                var c = IconColor(k.def);
                mesh.Recolor(k.iconStart, k.capsStart - k.iconStart, c);
                var bar = c;
                if (shift != KeyboardShiftState.Locked) bar.a = 0f;
                mesh.Recolor(k.capsStart, k.capsCount, bar);
            }
        }

        private void SetKeyLook(UniTextKeyboardKey k)
        {
            var c = k.pressCount > 0 ? m_PressedColor : k.hovered ? m_HoverColor : BaseColor(k.def);
            mesh.Recolor(k.bgStart, k.bgCount, c);
            var s = k.pressCount > 0 ? 0.94f : 1f;
            var ls = k.transform.localScale;
            if (!Mathf.Approximately(ls.x, s)) k.transform.localScale = new Vector3(s, s, 1f);
        }

        private void ApplyKeyState(UniTextKeyboardKey k)
        {
            SetKeyLook(k);
            UploadColors();
        }

        private void UploadColors()
        {
            if (worldSurface != null) worldSurface.ApplyColors(mesh);
            if (canvasSurface != null) canvasSurface.ApplyColors(mesh);
        }

        // ---- key preview ---------------------------------------------------------------------------

        private void ShowPreview(UniTextKeyboardKey k)
        {
            if (previewRoot == null || previewLabel == null) return;
            previewKey = k;
            var unit = m_KeySize;
            var rect = ((RectTransform)transform).rect;
            var kr = k.localRect;
            var pw = Mathf.Max(kr.width, unit * 0.9f) * 1.2f;
            var ph = unit * 1.3f;
            var cx = Mathf.Clamp(kr.center.x, rect.xMin + pw * 0.5f, rect.xMax - pw * 0.5f);
            var center = new Vector2(cx, kr.yMax + m_KeyGap + ph * 0.5f);
            previewRoot.sizeDelta = new Vector2(pw, ph);
            previewRoot.anchoredPosition = center - rect.center;
            var lp = previewRoot.localPosition;
            // In world space the popup floats in front of the keys.
            previewRoot.localPosition = new Vector3(lp.x, lp.y, isWorld ? -unit * 0.3f : 0f);
            previewMesh.Clear();
            previewMesh.linear = mesh.linear;
            previewMesh.RoundedRect(new Rect(-pw * 0.5f, -ph * 0.5f, pw, ph), m_CornerRadius * 1.2f, m_HoverColor);
            if (worldPreviewSurface != null) worldPreviewSurface.Apply(previewMesh);
            if (canvasPreviewSurface != null) canvasPreviewSurface.Apply(previewMesh);
            var s = k.def.Label(Shifted);
            var lang = layout != null ? layout.language ?? string.Empty : string.Empty;
            if (previewLabel.Language != lang) previewLabel.Language = lang;
            var size = unit * 0.7f;
            if (!Mathf.Approximately(previewLabel.FontSize, size)) previewLabel.FontSize = size;
            if (previewLabel.color != m_LabelColor) previewLabel.color = m_LabelColor;
            if (previewLabel.Text != s) previewLabel.Text = s;
            if (!previewRoot.gameObject.activeSelf) previewRoot.gameObject.SetActive(true);
        }

        private void HidePreview()
        {
            previewKey = null;
            if (previewRoot != null && previewRoot.gameObject.activeSelf) previewRoot.gameObject.SetActive(false);
        }
    }
}
