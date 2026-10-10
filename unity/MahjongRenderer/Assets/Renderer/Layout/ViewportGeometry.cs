using Mahjong.Renderer.Contract;
using UnityEngine;

namespace Mahjong.Renderer.Layout
{
    public readonly struct ViewportGeometry
    {
        public readonly Rect SafeCss;
        public readonly Rect BoardCss;
        public readonly Rect HandCss;
        public readonly Rect BoardNormalized;
        public readonly Rect HandNormalized;

        public ViewportGeometry(Viewport viewport, bool separate)
        {
            Coherence.Viewport(viewport);
            float width = SourceCoordinates.Float(viewport.WidthCssPx);
            float height = SourceCoordinates.Float(viewport.HeightCssPx);
            Insets inset = viewport.InsetsCssPx;
            SafeCss = new Rect(SourceCoordinates.Float(inset.Left), SourceCoordinates.Float(inset.Bottom),
                width - SourceCoordinates.Float(inset.Left + inset.Right),
                height - SourceCoordinates.Float(inset.Top + inset.Bottom));
            float band = separate ? SourceCoordinates.Float(viewport.HandBandCssPx) : 0;
            HandCss = new Rect(SafeCss.x, SafeCss.y, SafeCss.width, band);
            BoardCss = new Rect(SafeCss.x, SafeCss.y + band, SafeCss.width, SafeCss.height - band);
            BoardNormalized = Normalize(BoardCss, width, height);
            HandNormalized = Normalize(HandCss, width, height);
        }

        public static Vector2 ScreenToCss(Vector2 screen, Vector2 actualBuffer, Viewport viewport) =>
            new Vector2(screen.x / actualBuffer.x * (float)viewport.WidthCssPx,
                screen.y / actualBuffer.y * (float)viewport.HeightCssPx);

        private static Rect Normalize(Rect rect, float width, float height) =>
            new Rect(rect.x / width, rect.y / height, rect.width / width, rect.height / height);
    }
}
