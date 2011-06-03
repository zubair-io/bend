﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Microsoft.WindowsAPICodePack.DirectX.DirectWrite;
using Microsoft.WindowsAPICodePack.DirectX.Direct2D1;

namespace TextCoreControl
{
    public class VisualLine
    {
        public VisualLine(DWriteFactory dwriteFactory, 
            string lineText, 
            TextFormat defaultFormat, 
            int beginOrdinal, 
            int nextOrdinal)
        {
            textLayout = dwriteFactory.CreateTextLayout(lineText, 
                defaultFormat, 
                float.MaxValue, 
                float.MaxValue);

            height = 0;
            foreach (LineMetrics lm in textLayout.LineMetrics)
            {
                height += lm.Height;
                break;
            }

            this.beginOrdinal = beginOrdinal;
            this.nextOrdinal = nextOrdinal;
        }

        public void Draw(RenderTarget renderTarget)
        {
            SolidColorBrush blackBrush = renderTarget.CreateSolidColorBrush(new ColorF(0, 0, 0, 1));
            renderTarget.DrawTextLayout(this.position, this.textLayout, blackBrush);
        }

        public void DrawWhite(RenderTarget renderTarget)
        {
            SolidColorBrush whiteBrush = renderTarget.CreateSolidColorBrush(new ColorF(1, 1, 1, 1));
            renderTarget.DrawTextLayout(this.position, this.textLayout, whiteBrush);
        }

        public void HitTest(Point2F position, out uint offset)
        {
            HitTestInfo hitTestInfo = this.textLayout.HitTestPoint(position);
            if ((hitTestInfo.Metrics.Left + (hitTestInfo.Metrics.Width / 2)) > position.X)
            {
                // snap left
                offset = hitTestInfo.Metrics.TextPosition;
            }
            else
            {
                // snap right
                offset = hitTestInfo.Metrics.TextPosition + hitTestInfo.Metrics.Length;
            }
        }

        public float CharPosition(Document document, int ordinal)
        {
            // get local text position.
            uint localPosition = 0;
            int tempOrdinal = this.beginOrdinal;
            while (tempOrdinal != ordinal) { localPosition++; tempOrdinal = document.NextOrdinal(tempOrdinal); }

            HitTestInfo hitTestInfo = textLayout.HitTestTextPosition(localPosition, /*isTrailingHit*/false);
            return hitTestInfo.Location.X;
        }

        public List<RectF> GetRangeRectangles(Document document, int beginOrdinal, int endOrdinal)
        {
            List<RectF> rangeRectangles = new List<RectF>();

            if (beginOrdinal < this.nextOrdinal && endOrdinal > this.beginOrdinal)
            {
                // there is some intersection between line and range, inspect further.
                uint localBegin = 0;
                int tempOrdinal = this.beginOrdinal;
                if (this.beginOrdinal < beginOrdinal)
                {   while (tempOrdinal != beginOrdinal)
                    {
                        tempOrdinal = document.NextOrdinal(tempOrdinal);
                        localBegin++;
                    }
                }

                uint localEnd = 0;
                while (tempOrdinal != endOrdinal && tempOrdinal != this.nextOrdinal)
                {
                    tempOrdinal = document.NextOrdinal(tempOrdinal);
                    localEnd++;
                }

                HitTestMetrics[] hitTestMetrics = textLayout.HitTestTextRange(localBegin, localEnd, position.X, position.Y);

                // Merge all the mergeable rectangles to create a compact rectangle list.
                if (hitTestMetrics.Length > 0)
                {
                    RectF previousRectangle = new RectF(hitTestMetrics[0].Left,
                        hitTestMetrics[0].Top,
                        hitTestMetrics[0].Left + hitTestMetrics[0].Width,
                        hitTestMetrics[0].Top + hitTestMetrics[0].Height);

                    for (int i = 0; i < hitTestMetrics.Length; i++)
                    {
                        RectF hitRectangle = new RectF(hitTestMetrics[i].Left,
                            hitTestMetrics[i].Top,
                            hitTestMetrics[i].Left + hitTestMetrics[i].Width,
                            hitTestMetrics[i].Top + hitTestMetrics[i].Height);

                        if (previousRectangle.Right + 2 > hitRectangle.Left && previousRectangle.Left < hitRectangle.Left)
                        {
                            // Simply merge the rectangle with the previous
                            previousRectangle.Right = hitRectangle.Right;

                            // Expand height
                            previousRectangle.Top = hitRectangle.Top < previousRectangle.Top ? hitRectangle.Top : previousRectangle.Top;
                            previousRectangle.Bottom = hitRectangle.Bottom > previousRectangle.Bottom ? hitRectangle.Bottom : previousRectangle.Bottom;
                        }
                        else
                        {
                            // If we are the last prevent adding the last rectangle twice.
                            if (i + 1 <  hitTestMetrics.Length)
                                rangeRectangles.Add(previousRectangle);

                            previousRectangle = hitRectangle;
                        }

                        previousRectangle = hitRectangle;
                    }

                    rangeRectangles.Add(previousRectangle);
                }
            }

            return rangeRectangles;
        }

        public float Height
        {
            get { return this.height; }
        }

        public float Width
        {
            get { return this.textLayout.Metrics.Width;  }
        }

        public Point2F Position
        {
            get { return this.position; }
            set { this.position = value;}
        }

        public int BeginOrdinal
        {
            get { return this.beginOrdinal; }
        }

        public int NextOrdinal
        {
            get { return this.nextOrdinal; }
        }

        public void OrdinalShift(int shiftBeginOrdinal, int shift)
        {
            if (beginOrdinal > shiftBeginOrdinal) beginOrdinal += shift;
            if (nextOrdinal > shiftBeginOrdinal) nextOrdinal += shift;
        }

        private Point2F position;
        private TextLayout textLayout;
        private float height;
        private int beginOrdinal;
        private int nextOrdinal;
    }
}
