using CarDiagnosticApp.Models;
using Microsoft.Maui.Graphics;
using PointF = Microsoft.Maui.Graphics.PointF;
using RectF = Microsoft.Maui.Graphics.RectF;

namespace CarDiagnosticApp.Views
{
    /// <summary>
    /// IDrawable для рендеринга 2D-схемы двигателя/узла.
    /// Поддерживает 3 уровня подсветки, масштабирование, панорамирование.
    /// </summary>
    public class DiagramDrawable : IDrawable
    {
        public DiagramView? View { get; set; }

        /// <summary>Ключ = Component.Id, значение = уровень подсветки (1–3)</summary>
        public Dictionary<string, int> HighlightLevels { get; set; } = new();

        /// <summary>Смещение пульсации (0..1), обновляется таймером</summary>
        public float PulseOffset { get; set; }

        // Transform state
        public float Scale { get; set; } = 1f;
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }

        #region Colors
        private static readonly Color Level3Glow     = Color.FromArgb("#44FF1744"); // fault — red
        private static readonly Color Level3Stroke   = Color.FromArgb("#FF1744");
        private static readonly Color Level2Glow     = Color.FromArgb("#33FF9100"); // warn — orange
        private static readonly Color Level2Stroke   = Color.FromArgb("#FF9100");
        private static readonly Color Level1Glow     = Color.FromArgb("#221976D2"); // related — blue
        private static readonly Color Level1Stroke   = Color.FromArgb("#1976D2");

        private static readonly Color BackgroundFill = Color.FromArgb("#ECEFF1");
        private static readonly Color GridColor      = Color.FromArgb("#CFD8DC");
        private static readonly Color TextColor      = Color.FromArgb("#37474F");
        private static readonly Color BorderColor    = Color.FromArgb("#FFFFFF");
        #endregion

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            // Защита от WinUI 0xc000027b: NaN/нулевой размер/битые path
            try
            {
                var w = dirtyRect.Width;
                var h = dirtyRect.Height;
                if (w < 2 || h < 2 || float.IsNaN(w) || float.IsNaN(h) ||
                    float.IsInfinity(w) || float.IsInfinity(h))
                    return;

                // ── Фон + сетка ──
                canvas.FillColor = BackgroundFill;
                canvas.FillRectangle(dirtyRect);
                DrawGrid(canvas, w, h);

                if (View == null || View.Components == null || View.Components.Count == 0)
                    return;

                float scale = Scale;
                if (scale < 0.05f || scale > 20f || float.IsNaN(scale) || float.IsInfinity(scale))
                    scale = 1f;

                float ox = float.IsFinite(OffsetX) ? OffsetX : 0f;
                float oy = float.IsFinite(OffsetY) ? OffsetY : 0f;

                // ── Трансформация ──
                canvas.SaveState();
                canvas.Translate(ox + w * 0.5f, oy + h * 0.5f);
                canvas.Scale(scale, scale);
                canvas.Translate(-w * 0.5f, -h * 0.5f);

                float margin = 24;
                float dx = margin;
                float dy = margin + 28;
                float dw = w - 2 * margin;
                float dh = h - 2 * margin - 28;
                if (dw < 4 || dh < 4)
                {
                    canvas.RestoreState();
                    return;
                }

                // Фон диаграммы
                canvas.FillColor = Colors.White;
                canvas.StrokeColor = Color.FromArgb("#B0BEC5");
                canvas.StrokeSize = 1;
                canvas.FillRoundedRectangle(dx, dy, dw, dh, 8);
                canvas.DrawRoundedRectangle(dx, dy, dw, dh, 8);

                // ── СНАЧАЛА рисуем невыделенные, ПОТОМ выделенные (поверх) ──
                var ordered = View.Components
                    .Where(c => c != null)
                    .OrderBy(c => GetHighlightLevel(c))
                    .ToList();

                foreach (var comp in ordered)
                {
                    try { DrawComponent(canvas, comp, dx, dy, dw, dh); }
                    catch { /* один битый компонент не валит всю схему */ }
                }

                // Подпись
                canvas.FontColor = TextColor;
                canvas.FontSize = 12;
                var label = View.BackgroundLabel ?? "";
                if (label.Length > 0)
                {
                    canvas.DrawString(label, margin, 12, dw, 20,
                        HorizontalAlignment.Center, VerticalAlignment.Center);
                }

                canvas.RestoreState();
            }
            catch
            {
                // Managed-исключение в Draw на Windows → STOWED 0xc000027b
                try
                {
                    canvas.FillColor = BackgroundFill;
                    canvas.FillRectangle(dirtyRect);
                }
                catch { }
            }
        }

        int GetHighlightLevel(DiagramComponent comp)
        {
            // Сначала проверяем явный HighlightLevel > 0
            if (comp.HighlightLevel > 0) return comp.HighlightLevel;

            // Затем проверяем словарь HighlightLevels
            if (HighlightLevels.TryGetValue(comp.Id, out var level) && level > 0)
                return level;

            return 0;
        }

        void DrawGrid(ICanvas canvas, float w, float h)
        {
            canvas.StrokeColor = GridColor;
            canvas.StrokeSize = 0.5f;
            float step = 40;
            for (float x = step; x < w; x += step) canvas.DrawLine(x, 0, x, h);
            for (float y = step; y < h; y += step) canvas.DrawLine(0, y, w, y);
        }

        void DrawComponent(ICanvas canvas, DiagramComponent comp, float dx, float dy, float dw, float dh)
        {
            if (comp.Outline == null || comp.Outline.Count < 3) return;

            var pts = comp.Outline
                .Select(p => new PointF(dx + p.X * dw, dy + p.Y * dh))
                .Where(p => float.IsFinite(p.X) && float.IsFinite(p.Y))
                .ToArray();
            if (pts.Length < 3) return;

            int level = GetHighlightLevel(comp);

            // Цвет категории
            Color baseColor;
            try { baseColor = Color.FromArgb(comp.DefaultColor); }
            catch { baseColor = Color.FromArgb("#78909C"); }

            // ── Glow + Stroke по уровню ──
            Color glowColor, strokeColor;
            float strokeSize;
            float glowExpand;

            switch (level)
            {
                case 3: // 🔴 Неисправность — красный + пульсация
                    glowColor = Level3Glow;
                    strokeColor = Level3Stroke;
                    strokeSize = 3f;
                    glowExpand = 6f + PulseOffset * 8f; // пульсирует 6..14 px
                    break;
                case 2: // 🟠 Проверить — оранжевый
                    glowColor = Level2Glow;
                    strokeColor = Level2Stroke;
                    strokeSize = 2.5f;
                    glowExpand = 5f;
                    break;
                case 1: // 🔵 Связан — синий пунктир
                    glowColor = Level1Glow;
                    strokeColor = Level1Stroke;
                    strokeSize = 2f;
                    glowExpand = 4f;
                    break;
                default:
                    glowColor = Colors.Transparent;
                    strokeColor = BorderColor;
                    strokeSize = 1.5f;
                    glowExpand = 0;
                    break;
            }

            // Glow
            if (glowExpand > 0)
            {
                var glowPts = ExpandPolygon(pts, glowExpand);
                var glowPath = PtsToPath(glowPts);
                canvas.FillColor = glowColor;
                canvas.FillPath(glowPath);
            }

            // Основная фигура
            canvas.FillColor = baseColor;
            canvas.StrokeColor = strokeColor;
            canvas.StrokeSize = strokeSize;

            if (level == 1)
            {
                // Синий пунктир для уровня 1
                canvas.StrokeDashPattern = new float[] { 4, 3 };
            }

            var path = PtsToPath(pts);
            canvas.FillPath(path);
            canvas.DrawPath(path);

            // Возвращаем сплошную линию
            canvas.StrokeDashPattern = null;

            // ── Пульсирующая точка неисправности (level 3) ──
            if (level == 3)
            {
                var cx = pts.Average(p => p.X);
                var cy = pts.Average(p => p.Y);

                // Радарная пульсация: 3 кольца с разной фазой
                float phase1 = PulseOffset;                           // 0..1, основная
                float phase2 = (PulseOffset + 0.33f) % 1f;           // +120°
                float phase3 = (PulseOffset + 0.66f) % 1f;           // +240°

                DrawPingRing(canvas, cx, cy, phase1, 18, 28);
                DrawPingRing(canvas, cx, cy, phase2, 18, 28);
                DrawPingRing(canvas, cx, cy, phase3, 18, 28);

                // Центральная точка
                canvas.FillColor = Level3Stroke;
                canvas.FillCircle(cx, cy, 5);
                canvas.StrokeColor = Colors.White;
                canvas.StrokeSize = 1.5f;
                canvas.DrawCircle(cx, cy, 5);

                // Яркая искра в центре
                canvas.FillColor = Colors.White;
                canvas.FillCircle(cx, cy, 2.5f);
            }

            // ── Подпись ──
            var lcx = pts.Average(p => p.X);
            var lcy = pts.Average(p => p.Y);
            canvas.FontColor = level > 0 ? strokeColor : TextColor;
            canvas.FontSize = level > 0 ? 10 : 9;

            var name = comp.Name;
            if (name.Length > 18)
            {
                var idx = name.IndexOf(' ', name.Length / 2);
                if (idx < 0) idx = name.Length / 2;
                canvas.DrawString(name[..idx], lcx - 40, lcy - 8, 80, 14,
                    HorizontalAlignment.Center, VerticalAlignment.Center);
                canvas.DrawString(name[(idx + 1)..], lcx - 40, lcy + 4, 80, 14,
                    HorizontalAlignment.Center, VerticalAlignment.Center);
            }
            else
            {
                canvas.DrawString(name, lcx - 40, lcy - 6, 80, 14,
                    HorizontalAlignment.Center, VerticalAlignment.Center);
            }
        }

        static PathF PtsToPath(PointF[] pts)
        {
            var path = new PathF();
            path.MoveTo(pts[0]);
            for (int i = 1; i < pts.Length; i++) path.LineTo(pts[i]);
            path.Close();
            return path;
        }

        static PointF[] ExpandPolygon(PointF[] pts, float amount)
        {
            var cx = pts.Average(p => p.X);
            var cy = pts.Average(p => p.Y);
            return pts.Select(p =>
            {
                var dx = p.X - cx;
                var dy = p.Y - cy;
                var len = MathF.Sqrt(dx * dx + dy * dy);
                if (len < 0.001f) return p;
                var scale = 1 + amount / len;
                return new PointF(cx + dx * scale, cy + dy * scale);
            }).ToArray();
        }

        /// <summary>
        /// Рисует одно кольцо радарной пульсации.
        /// phase: 0→1 (центр → край), minR→maxR — диапазон радиуса.
        /// </summary>
        static void DrawPingRing(ICanvas canvas, float cx, float cy,
            float phase, float minR, float maxR)
        {
            // Радиус растёт от minR до maxR
            float r = minR + phase * (maxR - minR);

            // Непрозрачность: максимальна в середине, падает к краям
            float alpha = phase < 0.5f
                ? phase * 2f        // 0→1 на первой половине
                : (1f - phase) * 2f; // 1→0 на второй

            float strokeAlpha = alpha * 0.9f;
            float fillAlpha   = alpha * 0.25f;

            canvas.StrokeColor = Level3Stroke.WithAlpha(strokeAlpha);
            canvas.StrokeSize = 1.5f + alpha * 1.5f;
            canvas.DrawCircle(cx, cy, r);

            canvas.FillColor = Level3Stroke.WithAlpha(fillAlpha);
            canvas.FillCircle(cx, cy, r);
        }
    }
}
