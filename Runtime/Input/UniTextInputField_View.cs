using System.Collections.Generic;
using System.Text;
using OpenGlyph;
using UnityEngine;

namespace LightSide
{
    // View: what the text component shows (mask, composition), the caret map, scrolling, caret/selection drawing.
    public partial class UniTextInputField
    {
        private const string RawTagName = "og-input-raw";
        private const string RawOpen = "<" + RawTagName + ">";
        private const string RawClose = "</" + RawTagName + ">";

        private string display = string.Empty;
        private string lastPushed;
        private int[] srcToDisp = new int[16];
        private int[] dispToSrc = new int[16];
        private int compDispStart, compDispEnd;
        private string layoutText = string.Empty;
        private bool layIdentity = true;
        private int[] dispToLay = new int[16];
        private int[] layToDisp = new int[16];
        private bool mapDirty = true;
        private bool inLayoutCallback;
        private readonly StringBuilder displayBuilder = new();

        private Vector2 scroll;
        private Vector2 textBase;
        private bool textBaseCaptured;

        private UniTextInputOverlay selectionCanvas, caretCanvas;
        private UniTextInputWorldOverlay selectionWorld, caretWorld;
        private Rect caretLocalRect;
        private bool caretRectValid;
        private Vector2 lastImeCursor = new(float.NaN, float.NaN);

        /// <summary>The string given to the text component (mask characters for passwords, the IME composition inline).</summary>
        public string DisplayedText => display;

        /// <summary>The scroll offset in the text's local units (x: content moved left, y: content moved up).</summary>
        public Vector2 ScrollOffset => scroll;

        /// <summary>The caret rectangle in the text component's local space (valid while focused, no selection).</summary>
        public Rect CaretLocalRect => caretLocalRect;

        /// <summary>Whether a caret rectangle is currently drawn (focused, editable, no selection), ignoring blink.</summary>
        public bool HasCaretRect => caretRectValid;

        /// <summary>Whether the caret is in the visible phase of its blink.</summary>
        public bool CaretBlinkVisible => caretVisibleState == 1;

        /// <summary>The selection highlight rectangles (text local space) currently drawn.</summary>
        public void GetSelectionRects(List<Rect> results)
        {
            results.Clear();
            if (selectionCanvas != null) results.AddRange(selectionCanvas.Rects);
            else if (selectionWorld != null) results.AddRange(selectionWorld.Rects);
        }

        /// <summary>
        /// Rebuilds what the text component shows from the current text now (normally automatic). Mirrors
        /// <c>TMP_InputField.ForceLabelUpdate</c>.
        /// </summary>
        public void ForceLabelUpdate()
        {
            lastPushed = null;
            UpdateDisplay();
        }

        private bool LayoutPending =>
            !inLayoutCallback && m_TextComponent != null && m_TextComponent.isActiveAndEnabled &&
            m_TextComponent.CurrentDirtyFlags != UniText.DirtyFlags.None;

        // ---- display ------------------------------------------------------------------------------

        private void UpdateDisplay()
        {
            if (srcSeg.Text != m_Text) srcSeg.Build(m_Text);
            var n = m_Text.Length;
            var password = m_InputType == InputFieldInputType.Password;
            var comp = composition;
            EnsureArray(ref srcToDisp, n + 1);

            if (!password && comp.Length == 0)
            {
                display = m_Text;
                EnsureArray(ref dispToSrc, n + 1);
                for (var i = 0; i <= n; i++) { srcToDisp[i] = i; dispToSrc[i] = i; }
                compDispStart = compDispEnd = -1;
            }
            else
            {
                var sb = displayBuilder;
                sb.Clear();
                var compAt = comp.Length > 0 ? ClampSnap(caret) : -1;
                // Display positions of every source boundary.
                var pos = 0;
                while (true)
                {
                    srcToDisp[pos] = sb.Length;
                    if (pos == compAt)
                    {
                        compDispStart = sb.Length;
                        if (password)
                        {
                            scratchSeg.Build(comp);
                            sb.Append(m_AsteriskChar, scratchSeg.GraphemeCount);
                        }
                        else sb.Append(comp);
                        compDispEnd = sb.Length;
                    }
                    if (pos >= n) break;
                    var next = srcSeg.Next(pos);
                    if (password) sb.Append(m_AsteriskChar);
                    else sb.Append(m_Text, pos, next - pos);
                    for (var k = pos + 1; k < next; k++) srcToDisp[k] = srcToDisp[pos];
                    pos = next;
                }
                if (compAt < 0) compDispStart = compDispEnd = -1;
                display = sb.ToString();
                var dn = display.Length;
                EnsureArray(ref dispToSrc, dn + 1);
                for (var j = 0; j <= dn; j++) dispToSrc[j] = -1;
                for (var i = 0; i <= n; i++)
                    if (srcSeg.IsBoundary(i) && dispToSrc[srcToDisp[i]] < 0) dispToSrc[srcToDisp[i]] = i;
                if (compAt >= 0)
                    for (var j = compDispStart; j <= compDispEnd; j++) dispToSrc[j] = compAt;
                // Forward fill (inside a grapheme / the composition).
                var last = 0;
                for (var j = 0; j <= dn; j++)
                {
                    if (dispToSrc[j] < 0) dispToSrc[j] = last;
                    else last = dispToSrc[j];
                }
            }

            mapDirty = true;
            UpdatePlaceholder();
            PushDisplay(display);
            if (!LayoutPending)
            {
                if (mapDirty) RebuildMap();
                UpdateScroll();
                RefreshVisuals();
            }
        }

        private static void EnsureArray(ref int[] a, int n)
        {
            if (a.Length < n) a = new int[Mathf.NextPowerOfTwo(n + 1)];
        }

        private void UpdatePlaceholder()
        {
            if (m_Placeholder == null) return;
            var show = m_Text.Length == 0 && composition.Length == 0;
            if (m_Placeholder.enabled != show) m_Placeholder.enabled = show;
        }

        private void PushDisplay(string s)
        {
            var t = m_TextComponent;
            if (t == null || !OwnsText) return;
            if (t is IGlyphMeshText g)
            {
                if (g.richText != m_RichText) g.richText = m_RichText;
                if (g.text != s) g.text = s;
                lastPushed = s;
                return;
            }
            var feed = s;
            if (!m_RichText && s.Length > 0 && HasMarkupRules(t))
            {
                EnsureRawRule(t);
                feed = RawOpen + s + RawClose;
            }
            if (lastPushed == feed && t.Text == feed) return;
            if (t.Text != feed) t.Text = feed;
            lastPushed = feed;
        }

        private static bool HasMarkupRules(UniText t)
        {
            if (t.ModRegisters.Count > 0) return true;
            var cfgs = t.ModRegisterConfigs;
            for (var i = 0; i < cfgs.Count; i++)
                if (cfgs[i] != null && cfgs[i].modRegisters is { Count: > 0 }) return true;
            return false;
        }

        private static void EnsureRawRule(UniText t)
        {
            var mods = t.ModRegisters;
            for (var i = 0; i < mods.Count; i++)
                if (mods[i]?.Rule is NoParseParseRule r && string.Equals(r.TagName, RawTagName, System.StringComparison.OrdinalIgnoreCase))
                    return;
            t.RegisterModifier(new ModRegister
            {
                Modifier = new EmptyModifier(),
                Rule = new NoParseParseRule { TagName = RawTagName, CloseAtLastTag = true },
            });
        }

        // ---- caret map -----------------------------------------------------------------------------

        private void OnTextLayoutApplied()
        {
            if (this == null || !isActiveAndEnabled) return;
            inLayoutCallback = true;
            try
            {
                if (!OwnsText) SyncExternalText();
                RebuildMap();
                UpdateScroll();
                RefreshVisuals();
            }
            finally { inLayoutCallback = false; }
        }

        /// <summary>Makes sure the caret map describes the current text (lays the text out now if it is pending).</summary>
        private void EnsureMap()
        {
            if (!mapDirty) return;
            if (LayoutPending && display.Length > 0) Canvas.ForceUpdateCanvases();
            if (mapDirty) RebuildMap();
        }

        private void RebuildMap()
        {
            var t = m_TextComponent;
            var lay = display;
            if (t != null && t.isActiveAndEnabled && t.CurrentDirtyFlags == UniText.DirtyFlags.None && display.Length > 0)
            {
                var clean = t.CleanText;
                if (!string.IsNullOrEmpty(clean) && clean != display) lay = clean;
            }
            BuildAlignment(display, lay);
            map.Build(t, lay, t != null ? t.TextAreaRect : default);
            mapDirty = false;
        }

        /// <summary>display -> layout text: identity, or a greedy alignment where the layout text is the display with markup removed.</summary>
        private void BuildAlignment(string disp, string lay)
        {
            layoutText = lay;
            layIdentity = ReferenceEquals(disp, lay) || disp == lay;
            if (layIdentity) return;
            int dn = disp.Length, ln = lay.Length;
            EnsureArray(ref dispToLay, dn + 1);
            EnsureArray(ref layToDisp, ln + 1);
            int i = 0, j = 0;
            while (i < dn && j < ln)
            {
                if (disp[i] == lay[j]) { dispToLay[i] = j; layToDisp[j] = i; i++; j++; }
                else { dispToLay[i] = j; i++; }
            }
            for (; i < dn; i++) dispToLay[i] = j;
            for (; j < ln; j++) layToDisp[j] = dn;
            dispToLay[dn] = ln;
            layToDisp[ln] = dn;
        }

        private int SourceToLayout(int src)
        {
            src = Mathf.Clamp(src, 0, m_Text.Length);
            var d = Mathf.Clamp(srcToDisp[src], 0, display.Length);
            return layIdentity ? d : dispToLay[d];
        }

        private int DisplayToLayout(int d)
        {
            d = Mathf.Clamp(d, 0, display.Length);
            return layIdentity ? d : dispToLay[d];
        }

        private int LayoutToSource(int lay)
        {
            var d = layIdentity ? Mathf.Clamp(lay, 0, display.Length) : layToDisp[Mathf.Clamp(lay, 0, layoutText.Length)];
            d = Mathf.Clamp(d, 0, display.Length);
            var s = dispToSrc[d];
            return srcSeg.Floor(Mathf.Clamp(s, 0, m_Text.Length));
        }

        /// <summary>Layout-space caret position used for drawing and scrolling (after an open composition).</summary>
        private int CaretLayoutPos() => compDispEnd >= 0 ? DisplayToLayout(compDispEnd) : SourceToLayout(caret);

        // ---- scrolling -----------------------------------------------------------------------------

        private void CaptureTextBase()
        {
            if (textBaseCaptured || m_TextComponent == null) return;
            textBase = m_TextComponent.rectTransform.anchoredPosition;
            textBaseCaptured = true;
            scroll = Vector2.zero;
        }

        private void RestoreTextBase()
        {
            if (!textBaseCaptured || m_TextComponent == null) { textBaseCaptured = false; return; }
            m_TextComponent.rectTransform.anchoredPosition = textBase;
            textBaseCaptured = false;
            scroll = Vector2.zero;
        }

        private void UpdateScroll()
        {
            var t = m_TextComponent;
            if (t == null || m_TextViewport == null) return;
            if (mapDirty) { if (LayoutPending) return; RebuildMap(); }
            CaptureTextBase();
            var area = t.TextAreaRect;
            var w = area.width;
            var h = area.height;
            var content = map.ContentBounds();
            var margin = Mathf.Max(1f, m_CaretWidth);
            var s = scroll;

            if (focused && map.CaretGeometry(CaretLayoutPos(), caretUpstream, out var x, out var top, out var bottom, out _))
            {
                if (x - margin < s.x) s.x = x - margin;
                if (x + margin > s.x + w) s.x = x + margin - w;
                if (MultiLine)
                {
                    if (bottom > s.y + h) s.y = bottom - h;
                    if (top < s.y) s.y = top;
                }
            }
            var minX = Mathf.Min(0f, content.xMin);
            var maxX = Mathf.Max(0f, content.xMax + margin - w);
            s.x = Mathf.Clamp(s.x, minX, maxX);
            if (MultiLine)
            {
                var minY = Mathf.Min(0f, content.yMin);
                var maxY = Mathf.Max(0f, content.yMax - h);
                s.y = Mathf.Clamp(s.y, minY, maxY);
            }
            else s.y = 0f;

            if (s != scroll || t.rectTransform.anchoredPosition != textBase + new Vector2(-s.x, s.y))
            {
                scroll = s;
                t.rectTransform.anchoredPosition = textBase + new Vector2(-s.x, s.y);
            }
        }

        // ---- drawing -------------------------------------------------------------------------------

        private Rect LayoutToLocal(Rect area, float x0, float top, float x1, float bottom) =>
            Rect.MinMaxRect(area.xMin + x0, area.yMax - bottom, area.xMin + x1, area.yMax - top);

        private void RefreshVisuals()
        {
            var t = m_TextComponent;
            if (t == null) return;
            if (mapDirty) { if (LayoutPending) return; RebuildMap(); }
            var show = focused && isActiveAndEnabled;
            if (!show)
            {
                // Not editing: nothing to draw; never create helper objects here (this also runs from OnDisable).
                HideOverlays();
                caretVisibleState = -1;
                UpdateBlink();
                return;
            }
            EnsureOverlays();
            SyncOverlayTransforms();

            var area = t.TextAreaRect;
            var world = t.IsWorldText;
            var clipRect = default(Rect);
            var clip = world && ViewportRectInTextLocal(out clipRect);
            if (world) t.SetInputClip(clip, clipRect);

            // Selection + composition underline (behind the text).
            BeginLayer(true);
            if (show && HasSelection)
            {
                map.SelectionRects(SourceToLayout(SelectionStart), SourceToLayout(SelectionEnd), rectScratch);
                for (var i = 0; i < rectScratch.Count; i++)
                {
                    var r = rectScratch[i];
                    AddRect(true, LayoutToLocal(area, r.xMin, r.yMin, r.xMax, r.yMax), m_SelectionColor, clip, clipRect);
                }
            }
            if (show && compDispStart >= 0 && compDispEnd > compDispStart)
            {
                map.UnderlineRects(DisplayToLayout(compDispStart), DisplayToLayout(compDispEnd), rectScratch);
                var c = t.color;
                for (var i = 0; i < rectScratch.Count; i++)
                {
                    var r = rectScratch[i];
                    AddRect(true, LayoutToLocal(area, r.xMin, r.yMin, r.xMax, r.yMax), c, clip, clipRect);
                }
            }
            ApplyLayer(true);

            // Caret.
            BeginLayer(false);
            caretRectValid = false;
            if (show && !m_ReadOnly && !HasSelection && map.CaretGeometry(CaretLayoutPos(), caretUpstream, out var x, out var top, out var bottom, out _))
            {
                float x0, x1, y0 = top, y1 = bottom;
                switch (m_CaretShape)
                {
                    case InputFieldCaretShape.Block:
                        map.NextGraphemeBox(CaretLayoutPos(), caretUpstream, out x0, out x1);
                        break;
                    case InputFieldCaretShape.Underline:
                        map.NextGraphemeBox(CaretLayoutPos(), caretUpstream, out x0, out x1);
                        y0 = bottom - Mathf.Max(1f, m_CaretWidth);
                        break;
                    default:
                        var half = m_CaretWidth * 0.5f;
                        x0 = x - half;
                        x1 = x + half;
                        break;
                }
                caretLocalRect = LayoutToLocal(area, x0, y0, x1, y1);
                caretRectValid = true;
                if (m_CustomCaret == null) AddRect(false, caretLocalRect, CaretColor, clip, clipRect);
            }
            ApplyLayer(false);
            PlaceCustomCaret();
            caretVisibleState = -1;
            UpdateBlink();
            UpdateImeCursor();
        }

        private void PlaceCustomCaret()
        {
            if (m_CustomCaret == null || m_TextComponent == null) return;
            if (!caretRectValid) return;
            var tt = m_TextComponent.transform;
            m_CustomCaret.position = tt.TransformPoint(caretLocalRect.center);
            m_CustomCaret.rotation = tt.rotation;
            if (m_CustomCaret is RectTransform crt) crt.sizeDelta = caretLocalRect.size;
        }

        private bool ViewportRectInTextLocal(out Rect r)
        {
            r = default;
            if (m_TextViewport == null || m_TextComponent == null) return false;
            var vr = m_TextViewport.rect;
            var tt = m_TextComponent.transform;
            var a = tt.InverseTransformPoint(m_TextViewport.TransformPoint(new Vector3(vr.xMin, vr.yMin)));
            var b = tt.InverseTransformPoint(m_TextViewport.TransformPoint(new Vector3(vr.xMax, vr.yMax)));
            r = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
            return true;
        }

        private void BeginLayer(bool selection)
        {
            if (selection) { selectionCanvas?.BeginRects(); selectionWorld?.BeginRects(); }
            else { caretCanvas?.BeginRects(); caretWorld?.BeginRects(); }
        }

        private void AddRect(bool selection, Rect r, Color c, bool clip, Rect clipRect)
        {
            if (selection)
            {
                if (selectionCanvas != null) selectionCanvas.Add(r, c);
                if (selectionWorld != null) selectionWorld.Add(r, c, clip, clipRect);
            }
            else
            {
                if (caretCanvas != null) caretCanvas.Add(r, c);
                if (caretWorld != null) caretWorld.Add(r, c, clip, clipRect);
            }
        }

        private void ApplyLayer(bool selection)
        {
            if (selection) { if (selectionCanvas != null) selectionCanvas.Apply(); if (selectionWorld != null) selectionWorld.Apply(); }
            else { if (caretCanvas != null) caretCanvas.Apply(); if (caretWorld != null) caretWorld.Apply(); }
        }

        private void ResetBlink()
        {
            blinkStart = Now;
            caretVisibleState = -1;
        }

        private void UpdateBlink()
        {
            var on = caretRectValid && focused &&
                     (m_CaretBlinkRate <= 0f || (Now - blinkStart) * m_CaretBlinkRate % 1f < 0.5f);
            var st = on ? 1 : 0;
            if (st == caretVisibleState) return;
            caretVisibleState = st;
            if (caretCanvas != null) caretCanvas.SetVisible(on);
            if (caretWorld != null) caretWorld.SetVisible(on);
            if (m_CustomCaret != null && m_CustomCaret.gameObject.activeSelf != on) m_CustomCaret.gameObject.SetActive(on);
        }

        private void UpdateImeCursor()
        {
            if (!keyboardBegun || keyboardSource == null || !caretRectValid || m_TextComponent == null) return;
            var t = m_TextComponent;
            var world = t.transform.TransformPoint(new Vector3(caretLocalRect.xMin, caretLocalRect.yMin));
            Camera cam = null;
            if (!t.IsWorldText && t.canvas != null) cam = t.canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : t.canvas.worldCamera;
            else cam = Camera.main;
            if (t.IsWorldText && cam == null) return;
            var sp = RectTransformUtility.WorldToScreenPoint(cam, world);
            if (sp == lastImeCursor) return;
            lastImeCursor = sp;
            keyboardSource.SetImeCursor(sp);
        }

        // ---- overlay objects -----------------------------------------------------------------------

        private void EnsureOverlays()
        {
            var t = m_TextComponent;
            if (t == null || !t.gameObject.activeInHierarchy || !gameObject.activeInHierarchy) return;
            if (t.IsWorldText)
            {
                if (selectionCanvas != null || caretCanvas != null) DestroyOverlays();
                if (selectionWorld == null) selectionWorld = CreateOverlay("Selection", true).AddComponent<UniTextInputWorldOverlay>();
                if (caretWorld == null) caretWorld = CreateOverlay("Caret", false).AddComponent<UniTextInputWorldOverlay>();
                var tr = t.WorldRenderer;
                if (tr != null)
                {
                    var sr = selectionWorld.Renderer;
                    var cr = caretWorld.Renderer;
                    sr.sortingLayerID = cr.sortingLayerID = tr.sortingLayerID;
                    sr.sortingOrder = tr.sortingOrder - 1;
                    cr.sortingOrder = tr.sortingOrder + 1;
                }
            }
            else
            {
                if (selectionWorld != null || caretWorld != null) DestroyOverlays();
                if (selectionCanvas == null) selectionCanvas = CreateOverlay("Selection", true).AddComponent<UniTextInputOverlay>();
                if (caretCanvas == null) caretCanvas = CreateOverlay("Caret", false).AddComponent<UniTextInputOverlay>();
            }
        }

        private GameObject CreateOverlay(string name, bool behindText)
        {
            var t = m_TextComponent;
            var go = new GameObject(name, typeof(RectTransform)) { hideFlags = HideFlags.DontSave | HideFlags.NotEditable };
            go.layer = t.gameObject.layer;
            var tt = t.transform;
            var parent = tt.parent != null ? tt.parent : tt;
            go.transform.SetParent(parent, false);
            if (parent != tt)
            {
                var idx = tt.GetSiblingIndex();
                go.transform.SetSiblingIndex(behindText ? idx : idx + 1);
            }
            return go;
        }

        private void SyncOverlayTransforms()
        {
            if (m_TextComponent == null) return;
            var trt = m_TextComponent.rectTransform;
            Sync(selectionCanvas != null ? selectionCanvas.rectTransform : selectionWorld != null ? (RectTransform)selectionWorld.transform : null, trt);
            Sync(caretCanvas != null ? caretCanvas.rectTransform : caretWorld != null ? (RectTransform)caretWorld.transform : null, trt);
        }

        private static void Sync(RectTransform o, RectTransform text)
        {
            if (o == null) return;
            if (o.parent == text)
            {
                o.anchorMin = Vector2.zero; o.anchorMax = Vector2.one; o.pivot = text.pivot;
                o.sizeDelta = Vector2.zero; o.anchoredPosition3D = Vector3.zero;
                o.localRotation = Quaternion.identity; o.localScale = Vector3.one;
                return;
            }
            o.anchorMin = text.anchorMin;
            o.anchorMax = text.anchorMax;
            o.pivot = text.pivot;
            o.sizeDelta = text.sizeDelta;
            o.anchoredPosition3D = text.anchoredPosition3D;
            o.localRotation = text.localRotation;
            o.localScale = text.localScale;
        }

        private void HideOverlays()
        {
            caretRectValid = false;
            BeginLayer(true); ApplyLayerSafe(true);
            BeginLayer(false); ApplyLayerSafe(false);
            if (m_TextComponent != null && m_TextComponent.IsWorldText) m_TextComponent.SetInputClip(false, default);
        }

        private void ApplyLayerSafe(bool selection)
        {
            if (selection)
            {
                if (selectionCanvas != null && selectionCanvas.isActiveAndEnabled) selectionCanvas.Apply();
                if (selectionWorld != null) selectionWorld.Apply();
            }
            else
            {
                if (caretCanvas != null && caretCanvas.isActiveAndEnabled) caretCanvas.Apply();
                if (caretWorld != null) caretWorld.Apply();
            }
        }

        private void DestroyOverlays()
        {
            Kill(selectionCanvas != null ? selectionCanvas.gameObject : null);
            Kill(caretCanvas != null ? caretCanvas.gameObject : null);
            Kill(selectionWorld != null ? selectionWorld.gameObject : null);
            Kill(caretWorld != null ? caretWorld.gameObject : null);
            selectionCanvas = caretCanvas = null;
            selectionWorld = caretWorld = null;

            static void Kill(GameObject go)
            {
                if (go == null) return;
                if (Application.isPlaying) { Destroy(go); return; }
#if UNITY_EDITOR
                // Edit mode: the overlay may be part of a hierarchy that is being destroyed right now
                // (DestroyImmediate inside OnDestroy is not allowed then); destroy it after this call.
                UnityEditor.EditorApplication.delayCall += () => { if (go != null) DestroyImmediate(go); };
#else
                DestroyImmediate(go);
#endif
            }
        }
    }
}
