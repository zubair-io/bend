﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Controls.Primitives;
using System.Threading;

using Microsoft.WindowsAPICodePack.DirectX.Controls;
using Microsoft.WindowsAPICodePack.DirectX.DirectWrite;

namespace TextCoreControl
{
    class ScrollBoundsManager
    {
        public ScrollBoundsManager(ScrollBar vScrollBar, 
            ScrollBar hScrollBar, 
            DisplayManager displayManager, 
            RenderHost renderHost,
            Document document)
        {
            vScrollBar.Loaded += new System.Windows.RoutedEventHandler(vScrollBar_Loaded);
            this.vScrollBar = vScrollBar;
            this.hScrollBar = hScrollBar;
            this.asyncScrollLengthEstimater = null;
            this.document = document;
            renderHost.SizeChanged += new System.Windows.SizeChangedEventHandler(renderHost_SizeChanged);
            this.dwriteFactory = DWriteFactory.CreateFactory();
            this.displayManager = displayManager;
        }

        #region Event handler (Renderhost size change / loaded)
        void renderHost_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
        {
            RenderHost renderHost = (RenderHost) sender;
            this.InitializeVerticalScrollBounds((float)renderHost.ActualWidth);
        }

        void vScrollBar_Loaded(object sender, System.Windows.RoutedEventArgs e)
        {
            DisableVScrollbar();
            DisableHScrollbar();
        }
        #endregion

        #region Vertical scroll bounds estimation
        public void InitializeVerticalScrollBounds(float width)
        {
            GlyphTable glyphTable = new GlyphTable(Settings.defaultTextFormat);
            object[] paramaterArray = { this.document, glyphTable, this, width, this.displayManager.PageBeginOrdinal};

            if (asyncScrollLengthEstimater != null)
            {
                asyncScrollLengthEstimater.Abort();
            }
            asyncScrollLengthEstimater = new Thread(AsyncScrollLengthEstimater);
            asyncScrollLengthEstimater.Start(paramaterArray);
        }

        static void AsyncScrollLengthEstimater(object paramaterArrayIn)
        {
            object[] paramaterArray = (object[])paramaterArrayIn;
            Document document = (Document)paramaterArray[0];
            GlyphTable glyphTable = (GlyphTable)paramaterArray[1];
            ScrollBoundsManager scrollManager = (ScrollBoundsManager)paramaterArray[2];
            float layoutWidth = (float)paramaterArray[3];
            int pageBeginOrdinal = (int)paramaterArray[4];

            int ordinal = document.FirstOrdinal();
            int lineCount = 0;
            float lineWidth = 0;
            int firstLineIndex = -1;
            while (ordinal != Document.UNDEFINED_ORDINAL)
            {
                bool lineEnds = false;

                char letter = document.CharacterAt(ordinal);
                if (letter == '\n' || letter == '\v')
                {
                    ordinal = document.NextOrdinal(ordinal);
                    lineEnds = true;
                }
                else if (letter == '\r')
                {
                    int tempNextOrdinal = document.NextOrdinal(ordinal);
                    if (document.CharacterAt(tempNextOrdinal) != '\n' || document.NextOrdinal(tempNextOrdinal) == Document.UNDEFINED_ORDINAL)
                    {
                        // The file terminating \n gets its own line.
                        ordinal = tempNextOrdinal;
                        lineEnds = true;
                    }
                }

                if (!lineEnds && Settings.autoWrap)
                {
                    lineWidth += glyphTable.GetCharacterWidth(letter);
                    if (lineWidth > layoutWidth)
                    {
                        lineEnds = true;
                        ordinal = document.NextOrdinal(ordinal);
                    }
                }

                if (lineEnds)
                {
                    if (firstLineIndex == -1 && ordinal > pageBeginOrdinal)
                    {
                        firstLineIndex = lineCount;
                    }

                    lineCount++;
                    lineWidth = 0;
                }
                else
                {
                    ordinal = document.NextOrdinal(ordinal);
                }
            }

            scrollManager.InitializeVerticalScrollBounds(lineCount, firstLineIndex);
        }

        private void InitializeVerticalScrollBounds(int totalLineCount, int firstLineIndex)
        {
            int visualLineCount = displayManager.VisualLineCount;
            if (visualLineCount < totalLineCount && visualLineCount != 0)
            {
                // Need to show scrollbars
                this.vScrollBar.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Normal,
                    new Action
                    (
                        delegate()
                        {
                            this.vScrollBar.IsEnabled = true;
                            this.vScrollBar.Minimum = 0;
                            this.vScrollBar.Maximum = totalLineCount - visualLineCount;
                            this.vScrollBar.Track.Thumb.Visibility = System.Windows.Visibility.Visible;

                            // Guesstimate the thumb hieght
                            if (visualLineCount < totalLineCount)
                            {
                                this.vScrollBar.ViewportSize = totalLineCount * visualLineCount / (totalLineCount - visualLineCount);
                            }
                            else
                            {
                                this.vScrollBar.ViewportSize = double.MaxValue;
                            }

                            if (firstLineIndex >= this.vScrollBar.Minimum && firstLineIndex <= this.vScrollBar.Maximum)
                            {
                                int oldScrollPosition = (int)Math.Floor(this.vScrollBar.Value);
                                int delta = firstLineIndex - oldScrollPosition;
                                this.vScrollBar.Value = (this.vScrollBar.Value + delta);
                                this.displayManager.AdjustVScrollPositionForResize(delta, this.vScrollBar.Value);
                            }
                        }
                    )
                );
            }
            else
            {
                // Vertical scrollbar not needed
                DisableVScrollbar();
            }
        }
        #endregion

        #region Horizonatal scroll bounds setter API
        public void InitializeHorizontalScrollBounds(double maxLineWidth, double renderHostWidth)
        {
            if (maxLineWidth > renderHostWidth)
            {
                // Need to show scrollbars
                this.vScrollBar.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Normal,
                    new Action
                    (
                        delegate()
                        {
                            this.hScrollBar.IsEnabled = true;
                            this.hScrollBar.Minimum = 0;
                            this.hScrollBar.Maximum = maxLineWidth;
                            this.hScrollBar.Track.Thumb.Visibility = System.Windows.Visibility.Visible;

                            // Guesstimate the thumb hieght
                            if (renderHostWidth < maxLineWidth)
                            {
                                this.hScrollBar.ViewportSize = maxLineWidth * renderHostWidth / (maxLineWidth - renderHostWidth);
                            }
                            else
                            {
                                this.hScrollBar.ViewportSize = double.MaxValue;
                            }
                        }
                    )
                );
            }
            else
            {
                DisableHScrollbar();
            }
        }
        #endregion

        #region Scroll bar offset computation / Hide - Show Scroll bar API

        public void ScrollBy(int numberOfLines)
        {
            double newScrollValue = this.vScrollBar.Value + numberOfLines;
            if (newScrollValue < 0) newScrollValue = 0;
            if (newScrollValue > this.vScrollBar.Maximum) newScrollValue = this.vScrollBar.Maximum;

            this.vScrollBar.Value = newScrollValue;
            this.displayManager.vScrollBar_Scroll(this, new ScrollEventArgs(
                numberOfLines > 0 ? ScrollEventType.SmallIncrement : ScrollEventType.SmallDecrement, 
                this.vScrollBar.Value));
        }

        private void DisableVScrollbar()
        {
            this.vScrollBar.Dispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.Normal,
                new Action
                (
                    delegate()
                    {
                        this.vScrollBar.IsEnabled = false;
                        this.vScrollBar.Track.Thumb.Visibility = System.Windows.Visibility.Hidden;
                    }
                )
            );
        }

        private void DisableHScrollbar()
        {
            this.hScrollBar.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Normal,
                new Action
                (
                    delegate()
                    {
                        this.hScrollBar.IsEnabled = false;
                        this.hScrollBar.Track.Thumb.Height = 0;
                    }
                )
            );
        }

        #endregion

        #region Member data
        ScrollBar hScrollBar;
        ScrollBar vScrollBar;
        Thread asyncScrollLengthEstimater;
        Document document;
        DisplayManager displayManager;

        DWriteFactory dwriteFactory;
        #endregion
    }
}