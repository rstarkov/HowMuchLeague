using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Nito.Collections;

namespace LeagueOfStats.Downloader
{
    public class ConsoleBox : FrameworkElement
    {
        private double _dpiZoom;
        private Deque<int> _lineCounts = new Deque<int>();
        private Deque<Visual> _visuals = new Deque<Visual>();
        private double _curX = 0;
        private double _curY = 0;
        private double _translateY = 0;
        private double _lineHeight = 0;
        private GlyphTypeface _glyphTypeface;
        private Regex _regexLines = new Regex(@"\r\n|\r|\n", RegexOptions.Compiled);

        public ConsoleBox()
        {
            _dpiZoom = VisualTreeHelper.GetDpi(this).DpiScaleX;
            _lineCounts.AddToBack(0);
            new Typeface("Consolas").TryGetGlyphTypeface(out _glyphTypeface);
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, -_translateY, ActualWidth, ActualHeight));
        }

        protected override int VisualChildrenCount => _visuals.Count;

        protected override Visual GetVisualChild(int index) => _visuals[index];

        public void AddText(string text, Brush brush)
        {
            var lines = _regexLines.Split(text);
            for (int i = 0; i < lines.Length; i++)
            {
                addText(lines[i], brush);
                if (i != lines.Length - 1)
                    addNewline();
            }
        }

        private void addText(string text, Brush brush)
        {
            if (text == "")
                return;
            var visual = new DrawingVisual();
            var dc = visual.RenderOpen();

            var g = new Glyphs();
            g.UnicodeString = text;
            g.FontUri = _glyphTypeface.FontUri;
            g.FontRenderingEmSize = 12;
            g.Fill = brush;
            g.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _lineHeight = Math.Max(_lineHeight, Math.Round(g.DesiredSize.Height * _dpiZoom) / _dpiZoom);
            var newX = _curX + g.DesiredSize.Width;
            if (newX > ActualWidth)
            {
                addNewline();
                newX = _curX + g.DesiredSize.Width;
            }
            g.OriginX = Math.Round(_curX * _dpiZoom) / _dpiZoom;
            g.OriginY = Math.Round((_curY + _glyphTypeface.Baseline * g.FontRenderingEmSize) * _dpiZoom) / _dpiZoom;
            _curX = newX;

            dc.DrawGlyphRun(brush, g.ToGlyphRun());
            dc.Close();

            _visuals.AddToBack(visual);
            _lineCounts[_lineCounts.Count - 1]++;
            AddVisualChild(visual);
            InvalidateVisual();
        }

        private void addNewline()
        {
            _curX = 0;
            _curY += _lineHeight;
            _lineCounts.AddToBack(0);

            if (_curY + _translateY >= ActualHeight - _lineHeight)
            {
                var toRemove = _lineCounts.RemoveFromFront();
                for (int i = 0; i < toRemove; i++)
                    RemoveVisualChild(_visuals.RemoveFromFront());
                _translateY = Math.Round((ActualHeight - _lineHeight - _curY) * _dpiZoom) / _dpiZoom;
                RenderTransform = new TranslateTransform(0, _translateY);
            }
        }
    }
}
