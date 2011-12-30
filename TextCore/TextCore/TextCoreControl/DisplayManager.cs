﻿using System;
using System.Windows;
using System.Collections;
using System.Runtime.InteropServices;
using System.Windows.Controls.Primitives;
using System.Collections.Generic;
using System.Diagnostics;
using TextCoreControl.SyntaxHighlighting;

using Microsoft.WindowsAPICodePack.DirectX.Controls;
using Microsoft.WindowsAPICodePack.DirectX.Direct2D1;
using Microsoft.WindowsAPICodePack.DirectX.DirectWrite;
using Microsoft.WindowsAPICodePack.DirectX.WindowsImagingComponent;

namespace TextCoreControl
{
    public class DisplayManager
    {
        const int MOUSEWHEEL_WINDOWS_STEP_QUANTUM = 120;

        internal DisplayManager(RenderHost renderHost, 
            Document document, 
            ScrollBar vScrollBar, 
            ScrollBar hScrollBar)
        {
            this.renderHost = renderHost;
            renderHost.Loaded += new RoutedEventHandler(RenderHost_Loaded);
            renderHost.SizeChanged += new SizeChangedEventHandler(RenderHost_SizeChanged);
            renderHost.PreviewKeyDown += new System.Windows.Input.KeyEventHandler(renderHost_PreviewKeyDown);

            this.document = document;

            scrollOffset = new SizeF();
            this.d2dFactory = D2DFactory.CreateFactory();
            this.textLayoutBuilder = new TextLayoutBuilder();

            this.scrollBoundsManager = new ScrollBoundsManager(vScrollBar, hScrollBar, this, renderHost, this.document);
            vScrollBar.Scroll += new ScrollEventHandler(vScrollBar_Scroll);

            this.lastMouseWheelTime = System.DateTime.Now.Ticks;
            this.leftMargin = 0;

            this.syntaxHighlightingService = null;
            this.document.LanguageDetector.LanguageChange += new SyntaxHighlighting.LanguageDetector.LanguageChangeEventHandler(LanguageDetector_LanguageChange);

            DebugHUD.DisplayManager = this;
        }

        void CreateDeviceResources()
        {
            // Only calls if resources have not been initialize before
            if (hwndRenderTarget == null)
            {
                // Create the render target
                SizeU size = new SizeU((uint)renderHost.ActualWidth, (uint)renderHost.ActualHeight);
                RenderTargetProperties props = new RenderTargetProperties(
                    RenderTargetType.Hardware,
                    new PixelFormat(),
                    96,
                    96,
                    RenderTargetUsages.GdiCompatible,
                    Microsoft.WindowsAPICodePack.DirectX.Direct3D.FeatureLevel.Default);

                HwndRenderTargetProperties hwndProps = new HwndRenderTargetProperties(renderHost.Handle, size, PresentOptions.None);
                // Create the D2D Factory
                hwndRenderTarget = this.d2dFactory.CreateHwndRenderTarget(props, hwndProps);

                // Default rendering options
                defaultForegroundBrush = hwndRenderTarget.CreateSolidColorBrush(Settings.DefaultForegroundColor);
                defaultBackgroundBrush = hwndRenderTarget.CreateSolidColorBrush(Settings.DefaultBackgroundColor);

                // defaultSelectionBrush has to be solid color and not alpha
                defaultSelectionBrush = hwndRenderTarget.CreateSolidColorBrush(Settings.DefaultSelectionColor);

                document.ContentChange += this.Document_ContentChanged;
                document.OrdinalShift += this.Document_OrdinalShift;

                if (this.syntaxHighlightingService != null)
                    this.syntaxHighlightingService.InitDisplayResources(this.hwndRenderTarget);

                // Force create an empty line
                double maxVisualLineWidth;
                int changeStart, changeEnd;
                this.UpdateVisualLines(/*visualLineStartIndex*/ 0, /*forceRelayout*/ true, out maxVisualLineWidth, out changeStart, out changeEnd);
                System.Diagnostics.Debug.Assert(this.VisualLineCount != 0);

                this.caret = new Caret(this.hwndRenderTarget, (int)this.visualLines[0].Height);                
                this.document.OrdinalShift += this.caret.Document_OrdinalShift;

                this.selectionManager = new SelectionManager(hwndRenderTarget, this.d2dFactory);

                this.contentLineManager = new ContentLineManager(this.document, hwndRenderTarget, this.d2dFactory);
                this.LeftMargin = this.contentLineManager.LayoutWidth(this.textLayoutBuilder.AverageDigitWidth());
            }
        }

        #region WIN32 API references

        [DllImport("user32.dll")]
        static extern IntPtr SetFocus(IntPtr hWnd);

        #endregion

        #region Render host load / size / focus handling

        void RenderHost_Loaded(object sender, RoutedEventArgs e)
        {
            // Start rendering now
            this.renderHost.Render       = Render;
            this.renderHost.MouseHandler = MouseHandler;
            this.renderHost.KeyHandler = KeyHandler;
            this.renderHost.OtherHandler = this.OtherHandler;
            this.pageBeginOrdinal   = 0;
            this.visualLines        = new List<VisualLine>(50);
        }

        void RenderHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (hwndRenderTarget != null)
            {
                // Resize the render target to the actual host size
                hwndRenderTarget.Resize(new SizeU((uint)(renderHost.ActualWidth), (uint)(renderHost.ActualHeight)));

                double maxVisualLineWidth;
                int changeStart, changeEnd;
                this.UpdateVisualLines(/*visualLineStartIndex*/ 0, /*forceRelayout*/ true, out maxVisualLineWidth, out changeStart, out changeEnd);
                this.UpdateCaret(this.caret.Ordinal);

                this.scrollBoundsManager.InitializeVerticalScrollBounds(this.AvailbleWidth);
            }
        }

        private void OtherHandler(int type, int wparam, int lparam)
        {
            switch (type)
            {
                /*WM_SETFOCUS*/
                case 0x0007:
                    if (this.caret != null) this.caret.OnGetFocus();
                    break;
                /*WM_KILLFOCUS*/
                case 0x0008:
                    if (this.caret != null) this.caret.OnLostFocus();
                    break;
            }
        }

        #endregion

        #region Keyboard / Mouse Input handling

        private void MouseHandler(int x, int y, int type, int flags)
        {
            switch (type)
            {
                case 0x0201:
                    // WM_LBUTTONDOWN
                    {
                        SetFocus(renderHost.Handle);

                        int selectionBeginOrdinal;
                        int iLine;
                        if (this.HitTest(new Point2F(x, y), out selectionBeginOrdinal, out iLine))
                        {
                            VisualLine vl = this.visualLines[iLine];
                            this.caret.HideCaret();
                            this.caret.MoveCaretToLine(vl, this.document, scrollOffset, selectionBeginOrdinal);

                            this.hwndRenderTarget.BeginDraw();
                            this.selectionManager.ShouldUseHighlightColors = false;
                            this.selectionManager.ResetSelection(selectionBeginOrdinal, this.visualLines, this.document, this.scrollOffset, this.hwndRenderTarget);
                            this.hwndRenderTarget.EndDraw();
                            // OnGetFocus will also show the caret after recreating it.
                            this.caret.ShowCaret();
                        }
                    }
                    break;
                case 0x0202:
                    // WM_LBUTTONUP

                    break;
                case 0x0203:
                    // WM_LBUTTONDBLCLK                0x0203
                    {
                        int selectionBeginOrdinal;
                        int iLine;
                        if (this.HitTest(new Point2F(x, y), out selectionBeginOrdinal, out iLine))
                        {
                            VisualLine vl = this.visualLines[iLine];
                            this.caret.HideCaret();
                            this.caret.MoveCaretToLine(vl, this.document, scrollOffset, selectionBeginOrdinal);

                            int beginOrdinal, endOrdinal;
                            this.document.GetWordBoundary(selectionBeginOrdinal, out beginOrdinal, out endOrdinal);

                            this.hwndRenderTarget.BeginDraw();
                            this.selectionManager.ShouldUseHighlightColors = false;
                            this.selectionManager.ResetSelection(beginOrdinal, this.visualLines, this.document, this.scrollOffset, this.hwndRenderTarget);
                            this.selectionManager.ExpandSelection(endOrdinal, this.visualLines, this.document, this.scrollOffset, this.hwndRenderTarget);
                            this.hwndRenderTarget.EndDraw();
                            this.caret.ShowCaret();
                        }
                    }
                    break;
                case 0x0205:
                    // WM_RBUTTONUP

                    break;
                case 0X0204:
                    // WM_RBUTTONDOWN
                    break;
                case 0x0200:
                    // WM_MOUSEMOVE
                    if (flags == 1)
                    {
                        // Left mouse is down.
                        int selectionEndOrdinal;
                        int iLine;
                        if (this.HitTest(new Point2F(x, y), out selectionEndOrdinal, out iLine))
                        {
                            VisualLine vl = this.visualLines[iLine];
                            this.caret.HideCaret();
                            this.caret.MoveCaretToLine(vl, this.document, scrollOffset, selectionEndOrdinal);

                            this.hwndRenderTarget.BeginDraw();
                            this.selectionManager.ShouldUseHighlightColors = false;
                            this.selectionManager.ExpandSelection(selectionEndOrdinal, visualLines, document, this.scrollOffset, this.hwndRenderTarget);
                            this.hwndRenderTarget.EndDraw();
                            this.caret.ShowCaret();
                        }
                    }
                    break;
                case 0x020A:
                    {
                        // WM_MOUSEWHEEL
                        // wparam is passed in as flags
                        int highWord = flags >> 16;
                        int lowWord = flags & 0xFF;

                        // Read http://www.codeproject.com/KB/system/HiResScrollSupp.aspx?display=Mobile for info about
                        // highWord and how it corresponds to mouse type and speed.
                        long timeStamp = System.DateTime.Now.Ticks;
                        long deltaMS = (timeStamp - lastMouseWheelTime) / 10000;
                        lastMouseWheelTime = timeStamp;

                        // Emphrical constants that define the acceleration factor for mouse wheel scrolling.
                        int acceleration = Settings.MouseWheel_Normal_Step_LineCount;
                        if (deltaMS < Settings.MouseWheel_Fast1_Threshold_MS) acceleration = Settings.MouseWheel_Fast1_Step_LineCount;

                        int deltaAmount = (int)Math.Ceiling( acceleration * (double)highWord / DisplayManager.MOUSEWHEEL_WINDOWS_STEP_QUANTUM);
                        // Flip coordinates between windows and text core control.
                        deltaAmount = (0 - deltaAmount);

                        // When lowWord has MK_CONTROL 0x0008, the control key is down.
                        if ((0x0008 & lowWord) == 0)
                        {
                            this.ScrollBy(deltaAmount);
                        }
                        else
                        {
                            // TODO: Implement mouse wheel zooom
                        }
                    }
                    break;
            }
        }

        /// <summary>
        ///     Handle all non special key strokes by adding it to the document.
        ///     Special key strokes are handled in renderHost_PreviewKeyDown.
        ///     Other keys are handled here, in order to give windows the oppertunity
        ///     to translate and provide correct wParam.
        /// </summary>
        /// <param name="wparam"></param>
        /// <param name="lparam"></param>
        private void KeyHandler(int wparam, int lparam)
        {
            char key = (char)wparam;
            int insertOrdinal = this.caret.Ordinal;
            string content;
            if (key == '\r' && Settings.returnKeyInsertsNewLineCharacter)
                content = "\r\n";
            else 
                content = key.ToString();
            document.InsertAt(insertOrdinal, content);
        }

        void renderHost_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            bool adjustSelection = false;

            switch (e.Key)
            {
                case System.Windows.Input.Key.Left:
                    if (this.caret.Ordinal > this.document.FirstOrdinal())
                    {
                        int newCaretPosition = this.document.PreviousOrdinal(this.caret.Ordinal);
                        if (newCaretPosition != Document.UNDEFINED_ORDINAL && document.CharacterAt(newCaretPosition) == '\n')
                        {
                            int previousOrdinal = document.PreviousOrdinal(newCaretPosition);
                            if (previousOrdinal != Document.BEFOREBEGIN_ORDINAL && document.CharacterAt(previousOrdinal) == '\r')
                            {
                                newCaretPosition = previousOrdinal;
                            }                         
                        }

                        if (this.VisualLineCount > 0 && this.visualLines[0].BeginOrdinal > newCaretPosition && this.pageBeginOrdinal != document.FirstOrdinal())
                        {
                            this.ScrollBy(/*numberOfLines*/ -1);
                        }

                        this.UpdateCaret(newCaretPosition);
                        adjustSelection = true;
                    }
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.Right:
                    if (this.document.NextOrdinal(this.caret.Ordinal) != Document.UNDEFINED_ORDINAL)
                    {                        
                        int newCaretPosition = this.document.NextOrdinal(this.caret.Ordinal);
                        char charAt = this.document.CharacterAt(this.caret.Ordinal);
                        if (charAt == '\r' && newCaretPosition != Document.UNDEFINED_ORDINAL && document.CharacterAt(newCaretPosition) == '\n')
                        {
                            newCaretPosition = this.document.NextOrdinal(newCaretPosition);
                        }

                        if (this.VisualLineCount > 0 && this.visualLines[this.VisualLineCount - 1].NextOrdinal <= newCaretPosition)
                        {
                            this.ScrollBy(/*numberOfLines*/ +1);
                        }

                        this.UpdateCaret(newCaretPosition);
                        adjustSelection = true;
                    }
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.Up:
                    {
                        if (this.VisualLineCount > 0)
                        {
                            int firstVisibleLine = this.FirstVisibleLine();
                            if (firstVisibleLine >= 0 && 
                                this.visualLines[firstVisibleLine].BeginOrdinal <= this.caret.Ordinal && 
                                this.visualLines[firstVisibleLine].NextOrdinal > this.caret.Ordinal)
                            {
                                // Moving up from the first line, we need to scroll up - if possible.
                                this.ScrollBy(/*numberOfLines*/ -1);
                            }
                        }
                        this.caret.MoveCaretVertical(this.visualLines, document, scrollOffset, Caret.CaretStep.LineUp);
                        adjustSelection = true;
                    }
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.Down:
                    {
                        if (this.VisualLineCount > 0)
                        {
                            int lastVisibleLine = this.LastVisibleLine();
                            if (lastVisibleLine >= 0 && 
                                this.visualLines[lastVisibleLine].BeginOrdinal <= this.caret.Ordinal && 
                                this.visualLines[lastVisibleLine].NextOrdinal > this.caret.Ordinal)
                            {
                                // Moving down from the last visible line, we need to scroll down - if possible.
                                this.ScrollBy(/*numberOfLines*/ +1);
                            }
                        }
                        this.caret.MoveCaretVertical(this.visualLines, document, scrollOffset, Caret.CaretStep.LineDown);
                        adjustSelection = true;
                    }
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.End:
                    {
                        int lineIndex;
                        int ordinal;
                        Point2F caretPosition = this.caret.PositionInScreenCoOrdinates();
                        if (this.HitTest(caretPosition, out ordinal, out lineIndex))
                        {
                            VisualLine vl = this.visualLines[lineIndex];
                            int newCaretOrdinal = this.document.PreviousOrdinal(vl.NextOrdinal);
                            if (newCaretOrdinal == Document.UNDEFINED_ORDINAL)
                            {
                                int tempOrdinal = vl.BeginOrdinal;
                                while (tempOrdinal != Document.UNDEFINED_ORDINAL)
                                {
                                    newCaretOrdinal = tempOrdinal;
                                    tempOrdinal = document.NextOrdinal(tempOrdinal);
                                }
                            }

                            if (newCaretOrdinal >= vl.BeginOrdinal)
                            {
                                if (document.CharacterAt(newCaretOrdinal) == '\n')
                                {
                                    int previousOrdinal = document.PreviousOrdinal(newCaretOrdinal);
                                    if (previousOrdinal != Document.BEFOREBEGIN_ORDINAL && document.CharacterAt(previousOrdinal) == '\r')
                                        newCaretOrdinal = previousOrdinal;
                                }

                                this.caret.MoveCaretToLine(vl, this.document, this.scrollOffset, newCaretOrdinal);
                                adjustSelection = true;
                            }
                        }
                    }
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.Home:
                    {
                        int lineIndex;
                        int ordinal;
                        Point2F caretPosition = this.caret.PositionInScreenCoOrdinates();
                        if (this.HitTest(caretPosition, out ordinal, out lineIndex))
                        {
                            VisualLine vl = this.visualLines[lineIndex];
                            int newCaretOrdinal = vl.BeginOrdinal;
                            this.caret.MoveCaretToLine(vl, this.document, this.scrollOffset, newCaretOrdinal);
                            adjustSelection = true;
                        }
                    }
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.PageUp:
                    if (this.VisualLineCount > 1)
                    {
                        int lineIndex;
                        Point2F caretPosition = this.caret.PositionInScreenCoOrdinates();
                        if (this.pageBeginOrdinal > document.FirstOrdinal())
                        {
                            this.scrollBoundsManager.ScrollBy(-this.AvailableHeight);
                        }
                        else
                        {
                            // Already at the first page
                            // Set to 0 to move to the top of the page.
                            caretPosition.Y = 0;
                        }
                        int ordinal;
                        if (this.HitTest(caretPosition, out ordinal, out lineIndex))
                        {
                            this.caret.MoveCaretToLine(this.visualLines[lineIndex], this.document, this.scrollOffset, ordinal);
                            adjustSelection = true;
                        }
                    }
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.PageDown:
                    if (this.VisualLineCount > 1)
                    {
                        int lineIndex;
                        Point2F caretPosition = this.caret.PositionInScreenCoOrdinates();

                        if (this.visualLines[this.VisualLineCount - 1].NextOrdinal != Document.UNDEFINED_ORDINAL)
                        {
                            this.scrollBoundsManager.ScrollBy(this.AvailableHeight);
                        }
                        else
                        {
                            // already at the last page
                            caretPosition.Y = this.visualLines[this.VisualLineCount - 1].Position.Y - scrollOffset.Height;
                        }

                        int ordinal;
                        if (this.HitTest(caretPosition, out ordinal, out lineIndex))
                        {
                            this.caret.MoveCaretToLine(this.visualLines[lineIndex], this.document, this.scrollOffset, ordinal);
                            adjustSelection = true;
                        }
                    }
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.Back:
                    {
                        int selectionBeginTemp;
                        string selectedText = this.GetSelectedText(out selectionBeginTemp);
                        if (selectedText != null && selectedText.Length != 0)
                        {
                            // We have some text selected. Need to delete that instead
                            document.DeleteAt(selectionBeginTemp, selectedText.Length);
                        }
                        else if (this.caret.Ordinal > document.FirstOrdinal())
                        {
                            this.DeleteSingleCharRespectingLineBreak(document.PreviousOrdinal(this.caret.Ordinal));
                        }
                        e.Handled = true;
                    }
                    break;
                case System.Windows.Input.Key.Delete:
                    {
                        int selectionBeginTemp;
                        string selectedText = this.GetSelectedText(out selectionBeginTemp);
                        if (selectedText != null && selectedText.Length != 0)
                        {
                            // We have some text selected. Need to delete that instead
                            document.DeleteAt(selectionBeginTemp, selectedText.Length);
                        }
                        else if (this.caret.Ordinal != Document.UNDEFINED_ORDINAL)
                        {
                            this.DeleteSingleCharRespectingLineBreak(this.caret.Ordinal);
                        }
                        e.Handled = true;
                    }
                    break;
                case System.Windows.Input.Key.A:
                    if (e.KeyboardDevice.Modifiers == System.Windows.Input.ModifierKeys.Control)
                    {
                        //  Control A was pressed - select all
                        this.caret.HideCaret();
                        this.hwndRenderTarget.BeginDraw();
                        this.selectionManager.ShouldUseHighlightColors = false;
                        this.selectionManager.ResetSelection(this.document.FirstOrdinal(), this.visualLines, this.document, this.scrollOffset, this.hwndRenderTarget);
                        this.selectionManager.ExpandSelection(this.document.LastOrdinal(), this.visualLines, this.document, this.scrollOffset, this.hwndRenderTarget);
                        this.hwndRenderTarget.EndDraw();
                        this.caret.ShowCaret();
                        e.Handled = true;
                    }
                    break;
                case System.Windows.Input.Key.Tab:
                    {
                        int insertOrdinal = this.caret.Ordinal;
                        if (Settings.useStringForTab)
                            document.InsertAt(insertOrdinal, Settings.tabString);
                        else
                            document.InsertAt(insertOrdinal, "\t");
                        e.Handled = true;
                    }
                    break;
            }

            if (adjustSelection)
            {
                this.caret.HideCaret();
                this.hwndRenderTarget.BeginDraw();
                this.selectionManager.ShouldUseHighlightColors = false;
                if (e.KeyboardDevice.Modifiers == System.Windows.Input.ModifierKeys.Shift)
                {
                    this.selectionManager.ExpandSelection(this.CaretOrdinal, this.visualLines, this.document, this.scrollOffset, this.hwndRenderTarget);
                }
                else
                {
                    this.selectionManager.ResetSelection(this.CaretOrdinal, this.visualLines, this.document, this.scrollOffset, this.hwndRenderTarget);
                }
                this.hwndRenderTarget.EndDraw();
                this.caret.ShowCaret();
            }
        }

        /// <summary>
        ///     Used by backspace and delete, removes a single character while handling 
        ///     '\r\n' '\r' '\n' dualities.
        /// </summary>
        /// <param name="ordinalToBeginDelete">Valid ordinal to delete</param>
        private void DeleteSingleCharRespectingLineBreak(int ordinalToBeginDelete)
        {
            System.Diagnostics.Debug.Assert(Document.BEFOREBEGIN_ORDINAL != ordinalToBeginDelete);
            System.Diagnostics.Debug.Assert(Document.UNDEFINED_ORDINAL != ordinalToBeginDelete);

            int length = 1;
            if (Settings.returnKeyInsertsNewLineCharacter)
            {
                char ch = document.CharacterAt(ordinalToBeginDelete);
                if (ch == '\n')
                {
                    int previousOrdinal = document.PreviousOrdinal(ordinalToBeginDelete);
                    if (previousOrdinal != Document.BEFOREBEGIN_ORDINAL)
                    {
                        char chBefore = document.CharacterAt(previousOrdinal);
                        if (chBefore == '\r')
                        {
                            ordinalToBeginDelete = previousOrdinal;
                            length = 2;
                        }
                    }
                }
                else if (ch == '\r')
                {
                    int nextOrdinal = document.NextOrdinal(ordinalToBeginDelete);
                    if (nextOrdinal != Document.UNDEFINED_ORDINAL)
                    {
                        char chAfter = document.CharacterAt(nextOrdinal);
                        if (chAfter == '\n')
                        {
                            length = 2;
                        }
                    }
                }
            }
            document.DeleteAt(ordinalToBeginDelete, length);
        }
        #endregion

        #region Scrolling

        internal void vScrollBar_Scroll(object sender, ScrollEventArgs e)
        {
            int initialLineCount = this.VisualLineCount;
            this.scrollOffset.Height = (float)e.NewValue;

            this.AddLinesAbove(/*minLinesToAdd*/0);
            this.AddLinesBelow(/*minLinesToAdd*/0);

            // Remove lines going offscreen
            int indexLastLineAboveScreenWithHardBreak = -1;
            int indexFirstLineBelowScreenWithHardBreak = int.MaxValue;
            for (int j = 0; j < visualLines.Count; j++)
            {
                VisualLine vl = visualLines[j];
                float lineTop = vl.Position.Y - scrollOffset.Height;
                float lineBottom = vl.Position.Y + vl.Height - scrollOffset.Height;

                if (lineTop > this.renderHost.ActualHeight)
                {
                    // Line is below screen
                    if (vl.HasHardBreak)
                    {
                        indexFirstLineBelowScreenWithHardBreak = Math.Min(indexFirstLineBelowScreenWithHardBreak, j);
                    }
                }
                else if (lineBottom <= 0)
                {
                    // Line is above screen
                    if (vl.HasHardBreak)
                    {
                        indexLastLineAboveScreenWithHardBreak = Math.Max(indexLastLineAboveScreenWithHardBreak, j);
                    }
                }
            }
            if (indexFirstLineBelowScreenWithHardBreak != int.MaxValue)
            {
                indexFirstLineBelowScreenWithHardBreak++;
                if (indexFirstLineBelowScreenWithHardBreak < this.visualLines.Count)
                {
                    this.visualLines.RemoveRange(indexFirstLineBelowScreenWithHardBreak, this.visualLines.Count - indexFirstLineBelowScreenWithHardBreak);
                }
            }
            if (indexLastLineAboveScreenWithHardBreak > 0)
            {
                this.visualLines.RemoveRange(0, indexLastLineAboveScreenWithHardBreak + 1);
            }

            // Update page begin ordinal
            if (this.VisualLineCount > 0)
            {
                this.pageBeginOrdinal = this.visualLines[0].BeginOrdinal;
                this.pageTop = this.visualLines[0].Position.Y;
            }

            // Update caret
            this.UpdateCaret(this.caret.Ordinal);

            if (this.scrollOffset.Height != 0 || this.scrollOffset.Width != 0)
            {
                hwndRenderTarget.Transform = Matrix3x2F.Translation(new SizeF(-scrollOffset.Width, -scrollOffset.Height));
            }
            else
            {
                hwndRenderTarget.Transform = Matrix3x2F.Identity;
            }

            this.caret.HideCaret();
            this.Render();
            this.caret.ShowCaret();
        }

        private void AddLinesAbove(int minLinesToAdd)
        {
            float pageBottom = this.scrollOffset.Height + (float)renderHost.ActualHeight;
            // add lines coming in at the top.
            int nextOrdinalBack = this.pageBeginOrdinal;
            double yBottom = this.pageTop;
            if (this.VisualLineCount > 0)
            {
                nextOrdinalBack = this.visualLines[0].BeginOrdinal;
                yBottom = this.visualLines[0].Position.Y;
            }

            while ((yBottom > scrollOffset.Height || minLinesToAdd > 0) && nextOrdinalBack > Document.BEFOREBEGIN_ORDINAL)
            {
                List<VisualLine> previousVisualLines = this.textLayoutBuilder.GetPreviousLines(this.document, nextOrdinalBack, this.AvailbleWidth, out nextOrdinalBack);
                if (previousVisualLines.Count == 0)
                    break;

                for (int i = previousVisualLines.Count - 1; i >= 0; i--)
                {
                    VisualLine vl = previousVisualLines[i];
                    if (this.syntaxHighlightingService != null) this.syntaxHighlightingService.HighlightLine(ref vl);

                    yBottom -= vl.Height;
                    vl.Position = new Point2F(this.LeftMargin, (float)yBottom);
                    this.visualLines.Insert(0, vl);
                    minLinesToAdd--;
                }
            }
        }

        private void AddLinesBelow(int minLinesToAdd)
        {
            // add lines coming in at the bottom.
            int nextOrdinalFwd = this.pageBeginOrdinal;
            double yTop = this.pageTop;
            if (this.VisualLineCount > 0)
            {
                int lastLine = this.VisualLineCount - 1;
                nextOrdinalFwd = this.visualLines[lastLine].NextOrdinal;
                yTop = this.visualLines[lastLine].Position.Y + this.visualLines[lastLine].Height;
            }

            float pageBottom = this.scrollOffset.Height + (float)renderHost.ActualHeight;
            // add lines making sure, we end with a line that has a hard break.
            while ((yTop < pageBottom || this.VisualLineCount == 0 || !this.visualLines[this.VisualLineCount - 1].HasHardBreak || minLinesToAdd > 0) &&
                nextOrdinalFwd != Document.UNDEFINED_ORDINAL)
            {
                VisualLine vl = this.textLayoutBuilder.GetNextLine(this.document, nextOrdinalFwd, this.AvailbleWidth, out nextOrdinalFwd);
                if (this.syntaxHighlightingService != null) this.syntaxHighlightingService.HighlightLine(ref vl);

                vl.Position = new Point2F(this.LeftMargin, (float)yTop);
                if (yTop >= scrollOffset.Height)
                {
                    this.visualLines.Add(vl);
                    minLinesToAdd--;
                }
                yTop += vl.Height;
            }
        }

        /// <summary>
        ///     Adjusts for new lines being added / removed above the current first visual line
        /// </summary>
        /// <param name="delta">Number of lines added</param>
        /// <param name="newScrollOffset">Resulting new scroll offset</param>
        internal void AdjustVScrollPositionForResize(int newPageBeginOrdinal, double newPageTop)
        {
            if (this.scrollOffset.Height != newPageTop)
            {
                // Scrolloffset actually changed.
                this.visualLines.RemoveRange(0, this.VisualLineCount);
                this.pageTop = newPageTop;
                this.pageBeginOrdinal = newPageBeginOrdinal;
                this.vScrollBar_Scroll(this, new ScrollEventArgs(ScrollEventType.EndScroll, newPageTop));
            }
        }

        public void ScrollBy(int numberOfLines)
        {
            double offset = 0;

            if (numberOfLines > 0)
            {
                int lastVisibleLine = this.LastVisibleLine();
                if (lastVisibleLine < 0)
                {
                    this.AddLinesBelow(numberOfLines);
                    lastVisibleLine = 0;
                }
                else if (this.VisualLineCount - lastVisibleLine - 1 < numberOfLines)
                {
                    this.AddLinesBelow(numberOfLines - (this.VisualLineCount - lastVisibleLine - 1));
                }

                for (int i = lastVisibleLine; i < this.VisualLineCount && numberOfLines > 0; i++)
                {
                    numberOfLines--;
                    offset += this.visualLines[i].Height;
                }
            }
            else if (numberOfLines < 0)
            {
                int absNumberOfLines = Math.Abs(numberOfLines);
                int firstVisibleLine = this.FirstVisibleLine();
                if (firstVisibleLine < 0)
                {
                    this.AddLinesAbove(-numberOfLines);
                    firstVisibleLine = this.VisualLineCount - 1;
                }
                else if (firstVisibleLine < absNumberOfLines)
                {
                    int originalLineCount = this.VisualLineCount;
                    this.AddLinesAbove(absNumberOfLines - firstVisibleLine);
                    firstVisibleLine = this.VisualLineCount - originalLineCount;
                }

                for (int i = firstVisibleLine - 1; i >= 0 && absNumberOfLines > 0; i--)
                {
                    absNumberOfLines--;
                    offset -= this.visualLines[i].Height;
                }
            }

            if (offset == 0)
            {
                if (numberOfLines < 0)
                {
                    offset = -int.MaxValue;
                }
                else if (numberOfLines > 0)
                {
                    offset = int.MaxValue;
                }
            }

            this.scrollBoundsManager.ScrollBy(offset);
        }
        
        public void ScrollToContentLineNumber(int contentLineNumber, bool moveCaret)
        {
            if (this.contentLineManager != null)
            {
                this.caret.HideCaret();

                // Internally we are zero based, so subtract one.
                contentLineNumber--;
                int currentLineNumber = this.contentLineManager.GetLineNumber(document, this.FirstVisibleOrdinal());
                
                bool success = true;
                while (contentLineNumber != currentLineNumber && success)
                {
                    int delta = contentLineNumber - currentLineNumber;
                    float oldScrollOffset = this.scrollOffset.Height;
                    this.ScrollBy(delta);
                    success = (this.scrollOffset.Height != oldScrollOffset);
                    currentLineNumber = this.contentLineManager.GetLineNumber(document, this.FirstVisibleOrdinal());
                }
                int index = this.FirstVisibleLine();
                if (index >= 0 && index < this.VisualLineCount)
                {
                    float delta = this.visualLines[index].Position.Y - this.scrollOffset.Height;
                    this.scrollBoundsManager.ScrollBy(delta);
                    if (VisualLineCount != 0 && moveCaret)
                        this.caret.MoveCaretToLine(this.visualLines[0], this.document, this.scrollOffset, this.visualLines[0].BeginOrdinal);
                }

                this.caret.ShowCaret();
            }
        }

        public bool ScrollOrdinalIntoView(int ordinal, bool ignoreScrollBounds = false)
        {
            bool didScroll = false;

            int firstVisibleLine = this.FirstVisibleLine();
            int lastVisibleLine  = this.LastVisibleLine();
            if (this.VisualLineCount > 0 && firstVisibleLine != -1 && lastVisibleLine != -1)
            {
                // Scroll Down
                int lineToVerify = lastVisibleLine;
                int startLineToVerify = lineToVerify;
                double startBottom = this.visualLines[startLineToVerify].Position.Y + this.visualLines[this.VisualLineCount - 1].Height;                
                while (this.visualLines[lineToVerify].NextOrdinal <= ordinal)
                {
                    this.AddLinesBelow(1);
                    lineToVerify++;
                    if (lineToVerify == this.VisualLineCount)
                    {
                        // We failed to add a line.
                        lineToVerify = this.VisualLineCount - 1; 
                        break;
                    }
                }
                if (startLineToVerify != lineToVerify)
                {
                    if (ignoreScrollBounds)
                    {
                        double delta = this.visualLines[lineToVerify].Position.Y + this.visualLines[lineToVerify].Height - startBottom;
                        this.scrollBoundsManager.UpdateVerticalScrollBoundsDueToContentChange(delta);
                    }
                    this.ScrollBy(lineToVerify - startLineToVerify);
                    didScroll = true;
                }

                // Scroll Up
                int countMinusCheckIndex = this.VisualLineCount - firstVisibleLine;
                int startCountMinusCheckIndex = countMinusCheckIndex;
                while (this.visualLines[this.VisualLineCount - countMinusCheckIndex].BeginOrdinal > ordinal)
                {
                    this.AddLinesAbove(+1);
                    countMinusCheckIndex++;
                    if (this.VisualLineCount - countMinusCheckIndex < 0)
                    {
                        // we failed to add a line
                        countMinusCheckIndex = this.VisualLineCount;
                        break;
                    }
                }
                if (countMinusCheckIndex != startCountMinusCheckIndex)
                {
                    this.ScrollBy(-(countMinusCheckIndex - startCountMinusCheckIndex));
                    didScroll = true;
                }
            }
            return didScroll;
        }

        #endregion

        #region Selection

        public void SetHighlightMode(bool shouldUseHighlightColors)
        {
            this.selectionManager.ShouldUseHighlightColors = shouldUseHighlightColors;
        }

        public void SelectRange(int beginAtOrdinal, int endBeforeOrdinal)
        {
            this.caret.HideCaret();
            this.hwndRenderTarget.BeginDraw();
            this.selectionManager.ResetSelection(beginAtOrdinal, this.visualLines, this.document, this.scrollOffset, this.hwndRenderTarget);
            if (beginAtOrdinal != endBeforeOrdinal)
            {
                this.selectionManager.ExpandSelection(endBeforeOrdinal, this.visualLines, this.document, this.scrollOffset, this.hwndRenderTarget);
            }
            this.hwndRenderTarget.EndDraw();
            this.UpdateCaret(endBeforeOrdinal);
            this.caret.ShowCaret();
        }

        public string GetSelectedText(out int selectionBeginOrdinal)
        {
            string selectedString = "";

            selectionBeginOrdinal = this.SelectionBegin;
            int selectionEndOrdinal = this.SelectionEnd;

            if (selectionBeginOrdinal < selectionEndOrdinal &&
                selectionBeginOrdinal != Document.BEFOREBEGIN_ORDINAL &&
                selectionBeginOrdinal != Document.UNDEFINED_ORDINAL &&
                selectionEndOrdinal != Document.BEFOREBEGIN_ORDINAL &&
                selectionEndOrdinal != Document.UNDEFINED_ORDINAL)
            {
                // Valid selection range exists
                int tempOrdinal = selectionBeginOrdinal;
                while (tempOrdinal != selectionEndOrdinal)
                {
                    selectedString += document.CharacterAt(tempOrdinal);
                    tempOrdinal = document.NextOrdinal(tempOrdinal);
                }
            }

            return selectedString;
        }

        #endregion

        #region Content change handling

        void Document_ContentChanged(int beginOrdinal, int endOrdinal, string content)
        {
            if (this.syntaxHighlightingService != null)
                this.syntaxHighlightingService.NotifyOfContentChange(beginOrdinal, endOrdinal, content);

            if (beginOrdinal == Document.UNDEFINED_ORDINAL)
            {
                // Full reset, most likely a new file was loaded.
                this.pageBeginOrdinal = document.FirstOrdinal();
                this.pageTop = 0;
                this.SelectRange(this.pageBeginOrdinal, this.pageBeginOrdinal);

                double maxVisualLineWidth;
                int changeStart, changeEnd;
                this.UpdateVisualLines(/*visualLineStartIndex*/ 0, /*forceRelayout*/ true, out maxVisualLineWidth, out changeStart, out changeEnd);
                this.UpdateCaret(endOrdinal);

                this.scrollBoundsManager.InitializeVerticalScrollBounds(this.AvailbleWidth);
            }
            else
            {
                // ScrollBounds Prep for estimate scroll bounds delta due to change.
                int lastVisualLineNextOrdinal = Document.UNDEFINED_ORDINAL;
                float trackedVisualLineTop = 0f;
                bool trackableLineFound = false;
                if (this.VisualLineCount > 0)
                {
                    Debug.Assert(this.visualLines[this.VisualLineCount - 1].HasHardBreak);
                    lastVisualLineNextOrdinal = this.visualLines[this.VisualLineCount - 1].NextOrdinal;
                    if (lastVisualLineNextOrdinal > endOrdinal)
                    {
                        trackedVisualLineTop = this.visualLines[this.VisualLineCount - 1].Position.Y;
                        trackableLineFound = true;
                    }
                }
                                
                // Ensure that pageBegin is outside the change region
                if (this.pageBeginOrdinal == Document.BEFOREBEGIN_ORDINAL || 
                    this.pageBeginOrdinal >= beginOrdinal && this.pageBeginOrdinal <= endOrdinal)
                {
                    this.pageBeginOrdinal = this.document.FirstOrdinal();
                    this.pageTop = 0;
                }
                
                int visualLineStartIndex = -1;
                for (int i = 0; i < visualLines.Count; i++)
                {
                    VisualLine vl = visualLines[i];

                    // Null out all lines that intersect with the change region
                    bool lineIsOutsideChange = vl.NextOrdinal < beginOrdinal || vl.BeginOrdinal > endOrdinal;
                    if (!lineIsOutsideChange)
                    {
                        visualLines[i] = null;
                        if (visualLineStartIndex == -1)
                        {
                            visualLineStartIndex = i;
                        }
                    }
                }
                                
                visualLineStartIndex = (visualLineStartIndex > 0) ? visualLineStartIndex - 1 : 0;
                double maxVisualLineWidth;
                int changeStart, changeEnd;
                this.UpdateVisualLines(visualLineStartIndex, /*forceRelayout*/ false, out maxVisualLineWidth, out changeStart, out changeEnd);

                // Scrollbounds: Estimate delta due to change (only works when change is above the last ordinal on page).
                //               forces full document scroll bounds computation otherwise.
                float scrollBoundsDelta = 0f;
                bool forceDocumentBoundsMeasure = true;
                int lastLineIndex = this.VisualLineCount - 1;
                if (trackableLineFound && this.VisualLineCount > 0)
                {
                    Debug.Assert(this.visualLines[lastLineIndex].HasHardBreak);
                    int newLastVisualLineNextOrdinal = this.visualLines[lastLineIndex].NextOrdinal;

                    if (newLastVisualLineNextOrdinal == lastVisualLineNextOrdinal)
                    {
                        // Matching lines, the scroll bounds delta can be calculated.
                        scrollBoundsDelta = this.visualLines[lastLineIndex].Position.Y - trackedVisualLineTop;
                        forceDocumentBoundsMeasure = false;
                    }
                    else if (newLastVisualLineNextOrdinal < lastVisualLineNextOrdinal)
                    {
                        // try generating 5 more lines to see if lastVisualLineNextOrdinal can be found
                        float tempLinesDelta = 0;
                        for (int i = 0; i < 5 && newLastVisualLineNextOrdinal != Document.UNDEFINED_ORDINAL; i++)
                        {
                            VisualLine visualLine = textLayoutBuilder.GetNextLine(this.document, newLastVisualLineNextOrdinal, this.AvailbleWidth, out newLastVisualLineNextOrdinal);
                            if (this.syntaxHighlightingService != null) this.syntaxHighlightingService.HighlightLine(ref visualLine);

                            tempLinesDelta += visualLine.Height;
                            if (visualLine.NextOrdinal == lastVisualLineNextOrdinal)
                            {
                                scrollBoundsDelta = this.visualLines[lastLineIndex].Position.Y + tempLinesDelta - trackedVisualLineTop;
                                forceDocumentBoundsMeasure = false;
                                break;
                            }
                        }
                    }
                    else if (newLastVisualLineNextOrdinal > lastVisualLineNextOrdinal)
                    {
                        // Look up the array to find the line again.
                        for (int i = this.VisualLineCount - 1; i >= 0; i--)
                        {
                            if (this.visualLines[i].NextOrdinal == lastVisualLineNextOrdinal)
                            {
                                // We can safely assert this, since the change is completely before lastVisualLineNextOrdinal
                                // and this invariant should not have changed.
                                Debug.Assert(this.visualLines[i].HasHardBreak);
                                scrollBoundsDelta = this.visualLines[i].Position.Y - trackedVisualLineTop;
                                forceDocumentBoundsMeasure = false;
                            }
                        }
                    }
                }

                if (!forceDocumentBoundsMeasure)
                {
                    // scrollBoundsDelta is accurate and the scrollBoundsManager has to be updated with it.
                    if (scrollBoundsDelta != 0)
                    {
                        this.scrollBoundsManager.UpdateVerticalScrollBoundsDueToContentChange(scrollBoundsDelta, maxVisualLineWidth);
                    }
                }

                // Scroll the endOrdinal into view
                // Since the scroll bounds are not correct at this point, simply increment it so that scrollby can
                // scroll to the next line. The async scrollbounds estimator will fix the scroll bounds up later.
                bool contentRendered = this.ScrollOrdinalIntoView(endOrdinal, /*ignoreScrollBounds*/true);

                // Render
                this.caret.HideCaret();
                this.UpdateCaret(endOrdinal);
                hwndRenderTarget.BeginDraw();
                this.selectionManager.ShouldUseHighlightColors = false;
                this.selectionManager.ResetSelection(endOrdinal, this.visualLines, this.document, this.scrollOffset, this.hwndRenderTarget);
                if (!contentRendered)
                {
                    // Nothing to render if scrolling already rendered content on screen.
                    this.RenderToRenderTarget(changeStart, changeEnd, hwndRenderTarget);
                }
                hwndRenderTarget.Flush();
                hwndRenderTarget.EndDraw();
                this.caret.ShowCaret();

                if (forceDocumentBoundsMeasure)
                {
                    Debug.WriteLine("Initiating full document scroll bounds measure due to change.");
                    this.scrollBoundsManager.InitializeVerticalScrollBounds(this.AvailbleWidth);
                }
            }
        }
        
        void Document_OrdinalShift(Document document, int beginOrdinal, int shift)
        {
            for (int i = 0; i < visualLines.Count; i++)
            {
                VisualLine vl = visualLines[i];
                vl.OrdinalShift(beginOrdinal, shift);
            }

            Document.AdjustOrdinalForShift(beginOrdinal, shift, ref this.pageBeginOrdinal);

            if (this.selectionManager != null)            
                this.selectionManager.NotifyOfOrdinalShift(beginOrdinal, shift);

            if (this.syntaxHighlightingService != null)
                this.syntaxHighlightingService.NotifyOfOrdinalShift(document, beginOrdinal, shift);
        }

        internal void NotifyOfSettingsChange()
        {
            if (this.syntaxHighlightingService != null)
                this.syntaxHighlightingService.NotifyOfContentChange(Document.UNDEFINED_ORDINAL, Document.UNDEFINED_ORDINAL, "");

            if (this.contentLineManager != null)
            {
                this.contentLineManager.NotifyOfSettingsChange();
                this.contentLineManager.MaxContentLines = 0;
            }

            this.scrollBoundsManager.InitializeVerticalScrollBounds(this.AvailbleWidth);

            double maxVisualLineWidth;
            int changeStart, changeEnd;
            this.UpdateVisualLines(/*visualLineStartIndex*/ 0, /*forceRelayout*/ true, out maxVisualLineWidth, out changeStart, out changeEnd);
            this.UpdateCaret(this.caret.Ordinal);

            this.caret.HideCaret();
            this.Render();
            this.caret.ShowCaret();
        }

        private void UpdateVisualLines(
            int visualLineStartIndex, 
            bool forceRelayout,
            out double maxVisualLineWidth, 
            out int changeStartIndex,
            out int changeEndIndex)
        {
            int ordinal = this.pageBeginOrdinal;
            double y = this.pageTop;
            maxVisualLineWidth = 0;
            if (forceRelayout)
            {
                this.visualLines.Clear();
                visualLineStartIndex = 0;
            }
            else
            {
                if (this.visualLines.Count > visualLineStartIndex && this.visualLines[visualLineStartIndex] != null)
                {
                    ordinal = this.visualLines[visualLineStartIndex].BeginOrdinal;
                    y = this.visualLines[visualLineStartIndex].Position.Y;
                }
                else
                {
                    visualLineStartIndex = 0;
                }
            }

            changeStartIndex = -1;
            changeEndIndex = -1;
            bool previousLineHasHardBreak = true;
            while (ordinal != Document.UNDEFINED_ORDINAL && (y < (renderHost.ActualHeight + scrollOffset.Height) || !previousLineHasHardBreak))
            {
                VisualLine visualLine = textLayoutBuilder.GetNextLine(this.document, ordinal, this.AvailbleWidth, out ordinal);
                if (this.syntaxHighlightingService != null) this.syntaxHighlightingService.HighlightLine(ref visualLine);

                visualLine.Position = new Point2F(this.LeftMargin, (float)y);
                y += visualLine.Height;
                previousLineHasHardBreak = visualLine.HasHardBreak;
                maxVisualLineWidth = Math.Max(maxVisualLineWidth, visualLine.Width);

                changeEndIndex = visualLineStartIndex;
                if (changeStartIndex == -1)
                {
                    changeStartIndex = changeEndIndex;
                }

                if (visualLineStartIndex + 1 > this.visualLines.Count)
                {
                    this.visualLines.Add(visualLine);
                    visualLineStartIndex++;
                }
                else
                {
                    this.visualLines[visualLineStartIndex] = visualLine;
                    visualLineStartIndex++;
                    if (!forceRelayout && visualLineStartIndex < this.visualLines.Count && this.visualLines[visualLineStartIndex] != null)
                    {
                        if (visualLine.NextOrdinal == this.visualLines[visualLineStartIndex].BeginOrdinal &&
                            (this.syntaxHighlightingService == null || this.syntaxHighlightingService.CanReuseLine(this.visualLines[visualLineStartIndex])))
                        {
                            // We have reflowed enough, things are the same from here on.
                           
                            // Update position
                            for (int p = visualLineStartIndex; p < visualLines.Count; p++)
                            {
                                Point2F position = this.visualLines[p].Position;
                                position.Y = (float)y;
                                y += this.visualLines[p].Height;
                                this.visualLines[p].Position = position;
                                changeEndIndex = p;
                            }
                            
                            // Continue from the last ordinal.
                            ordinal = this.visualLines[this.visualLines.Count - 1].NextOrdinal;
                            visualLineStartIndex = this.visualLines.Count;
                        }
                    }
                }
            }

            if (changeEndIndex >= 0)
            {
                if (!(ordinal != Document.UNDEFINED_ORDINAL && (y < (renderHost.ActualHeight + scrollOffset.Height) || !previousLineHasHardBreak)))
                {
                    // Ran out of content delete everything after changeEndIndex
                    if (changeEndIndex + 1 < this.visualLines.Count)
                    {
                        this.visualLines.RemoveRange(changeEndIndex + 1, (this.visualLines.Count - changeEndIndex) - 1);
                    }
                }
                else
                {
                    // Remove any trailing null lines.
                    for (int d = changeEndIndex; d < this.visualLines.Count; d++)
                    {
                        if (this.visualLines[d] == null)
                        {
                            // everything after this must go.
                            this.visualLines.RemoveRange(d, this.visualLines.Count - d);
                            break;
                        }
                    }
                }
            }

#if DEBUG
            // Verify the invariant that there are no null lines after UpdateVisualLines call.
            for (int i = 0; i < this.visualLines.Count; i++)
                Debug.Assert(this.visualLines[i] != null);

            // Verify that last line has a hard break
            Debug.Assert(this.visualLines.Count > 0);
            Debug.Assert(this.visualLines[this.visualLines.Count - 1].HasHardBreak);
#endif
        }

        private void UpdateCaret(int newCaretOrdinal)
        {
            // Update caret
            if (this.caret != null && newCaretOrdinal != Document.UNDEFINED_ORDINAL)
            {
                for (int i = 0; i < this.visualLines.Count; i++)
                {
                    VisualLine vl = (VisualLine)this.visualLines[i];
                    if (vl.BeginOrdinal <= newCaretOrdinal && vl.NextOrdinal > newCaretOrdinal)
                    {
                        this.caret.MoveCaretToLine(vl, this.document, scrollOffset, newCaretOrdinal);
                        break;
                    }
                }
            }
        }

        #endregion

        #region Render and hit testing
        private void Render()
        {
            CreateDeviceResources();
            if (hwndRenderTarget.IsOccluded)
                return;

            this.caret.HideCaret();
            hwndRenderTarget.BeginDraw();
            this.RenderToRenderTarget(/*redrawBegin*/ 0, /*redrawEnd*/ this.visualLines.Count - 1, hwndRenderTarget);
            hwndRenderTarget.Flush();
            hwndRenderTarget.EndDraw();
            this.caret.ShowCaret();
        }

        private void RenderToRenderTarget(int redrawBegin, int redrawEnd, RenderTarget renderTarget)
        {
            RectF wipeBounds;
            if (redrawBegin == 0 && redrawEnd == visualLines.Count - 1)
            {
                renderTarget.Clear(defaultBackgroundBrush.Color);
            }
            else
            {
                VisualLine beginLine = this.visualLines[redrawBegin];
                VisualLine endLine = this.visualLines[redrawEnd];
                wipeBounds = new RectF(0.0f, beginLine.Position.Y, renderTarget.Size.Width, endLine.Position.Y + endLine.Height);
                renderTarget.FillRectangle(wipeBounds, defaultBackgroundBrush);
            }

            for (int i = redrawBegin; i <= redrawEnd; i++)
            {
                VisualLine visualLine = this.visualLines[i];
                visualLine.Draw(renderTarget);
            }

            this.selectionManager.DrawSelection(
                selectionManager.GetSelectionBeginOrdinal(),
                selectionManager.GetSelectionEndOrdinal(), 
                this.visualLines,
                this.document, 
                this.scrollOffset, 
                renderTarget);

            this.contentLineManager.DrawLineNumbers(
                redrawBegin,
                redrawEnd,
                this.visualLines, 
                this.document,
                this.scrollOffset,
                this.LeftMargin, 
                renderTarget);

            DebugHUD.Draw(renderTarget, this.scrollOffset);

#if DEBUG
            // Verify that last line has a hard break
            Debug.Assert(this.visualLines.Count == 0 || this.visualLines[this.visualLines.Count - 1].HasHardBreak);
#endif
        }

        private bool HitTest(Point2F point, out int ordinal, out int lineIndex)
        {
            point = new Point2F(point.X + scrollOffset.Width, point.Y + scrollOffset.Height);
            for (int i = 0; i < visualLines.Count; i++)
            {
                VisualLine visualLine = visualLines[i];
                if (visualLine.Position.Y <= point.Y && visualLine.Position.Y + visualLine.Height > point.Y)
                {
                    point.Y -= visualLine.Position.Y;
                    point.X -= visualLine.Position.X;
                    uint offset;
                    visualLine.HitTest(point, out offset);
                    ordinal = document.NextOrdinal(visualLine.BeginOrdinal, (uint)offset);
                    if (ordinal == Document.UNDEFINED_ORDINAL)
                    {
                        // If we cannot snap to the right, try the character to the left.
                        ordinal = document.NextOrdinal(visualLine.BeginOrdinal, (uint)offset - 1);
                    }
                    lineIndex = i;

                    return true;
                }
            }

            ordinal = Document.UNDEFINED_ORDINAL;
            lineIndex = -1;
            return false;
        }

        #endregion

        #region Rasterize
        [DllImport("gdi32.dll", SetLastError = true)]
        static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);
        
        [DllImport("gdi32.dll", SetLastError = true)]
        static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
        
        /// <summary>
        ///    Performs a bit-block transfer of the color data corresponding to a
        ///    rectangle of pixels from the specified source device context into
        ///    a destination device context.
        /// </summary>
        /// <param name="hdc">Handle to the destination device context.</param>
        /// <param name="nXDest">The leftmost x-coordinate of the destination rectangle (in pixels).</param>
        /// <param name="nYDest">The topmost y-coordinate of the destination rectangle (in pixels).</param>
        /// <param name="nWidth">The width of the source and destination rectangles (in pixels).</param>
        /// <param name="nHeight">The height of the source and the destination rectangles (in pixels).</param>
        /// <param name="hdcSrc">Handle to the source device context.</param>
        /// <param name="nXSrc">The leftmost x-coordinate of the source rectangle (in pixels).</param>
        /// <param name="nYSrc">The topmost y-coordinate of the source rectangle (in pixels).</param>
        /// <param name="dwRop">A raster-operation code.</param>
        /// <returns>
        ///    <c>true</c> if the operation succeeded, <c>false</c> otherwise.
        /// </returns>
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool BitBlt(IntPtr hdc, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, TernaryRasterOperations dwRop);

        public enum TernaryRasterOperations : uint
        {
            SRCCOPY = 0x00CC0020,
            SRCPAINT = 0x00EE0086,
            SRCAND = 0x008800C6,
            SRCINVERT = 0x00660046,
            SRCERASE = 0x00440328,
            NOTSRCCOPY = 0x00330008,
            NOTSRCERASE = 0x001100A6,
            MERGECOPY = 0x00C000CA,
            MERGEPAINT = 0x00BB0226,
            PATCOPY = 0x00F00021,
            PATPAINT = 0x00FB0A09,
            PATINVERT = 0x005A0049,
            DSTINVERT = 0x00550009,
            BLACKNESS = 0x00000042,
            WHITENESS = 0x00FF0062
        }

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        static extern bool DeleteDC(IntPtr hdc);

        internal System.Windows.Media.Imaging.BitmapSource Rasterize()
        {
            IntPtr hBitmap = IntPtr.Zero;

            this.caret.HideCaret();
            hwndRenderTarget.BeginDraw();

            this.RenderToRenderTarget(0, this.visualLines.Count - 1, hwndRenderTarget);

            IntPtr windowDC = hwndRenderTarget.GdiInteropRenderTarget.GetDC(DCInitializeMode.Copy);
            IntPtr compatibleDC = CreateCompatibleDC(windowDC);

            int nWidth = (int)Math.Ceiling(hwndRenderTarget.Size.Width);
            int nHeight = (int)Math.Ceiling(hwndRenderTarget.Size.Height);
            hBitmap = CreateCompatibleBitmap(windowDC,
                nWidth,
                nHeight);

            IntPtr hOld = SelectObject(compatibleDC, hBitmap);
            //	blit bits from screen to target buffer
            BitBlt(compatibleDC, 0, 0, nWidth, nHeight, windowDC, 0, 0, TernaryRasterOperations.SRCCOPY);
            //	de-select bitmap	
            SelectObject(compatibleDC, hOld);

            //	free DCs	
            DeleteDC(compatibleDC);
            hwndRenderTarget.GdiInteropRenderTarget.ReleaseDC();

            hwndRenderTarget.EndDraw();
            this.caret.ShowCaret();

            System.Windows.Media.Imaging.BitmapSource bmpSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                 hBitmap,
                 IntPtr.Zero,
                 Int32Rect.Empty,
                 System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions()
             );

            return bmpSource;
        }
        #endregion

        #region Accessors

        internal int VisualLineCount
        {
            get { return this.visualLines == null ? 0 : this.visualLines.Count; }
        }
        
        internal int FirstVisibleOrdinal() 
        {
            int firstVisibleLine = this.FirstVisibleLine();
            if (firstVisibleLine >= 0)
            {
                // We have a cached line that is visible on screen
                return visualLines[firstVisibleLine].BeginOrdinal;
            }

            return this.pageBeginOrdinal;
        }

        internal int FirstVisibleLine()
        {
            for (int i = 0; i < this.VisualLineCount; i++)
            {
                if (visualLines[i].Position.Y + this.visualLines[i].Height > this.scrollOffset.Height)
                {
                    // We have a cached line that is visible on screen
                    return i;
                }
            }

            return -1;
        }

        internal int LastVisibleLine()
        {
            double screenBottom = this.scrollOffset.Height + this.renderHost.ActualHeight;
            for (int i = this.VisualLineCount - 1; i >= 0; i--)
            {
                if (visualLines[i].Position.Y < screenBottom)
                {
                    // We have a cached line that is visible on screen
                    return i;
                }
            }

            return -1;
        }

        internal int MaxContentLines 
        { 
            set 
            { 
                if (this.contentLineManager != null) 
                {
                    if (this.contentLineManager.MaxContentLines != value)
                    {
                        this.contentLineManager.MaxContentLines = value;
                        this.LeftMargin = this.contentLineManager.LayoutWidth(this.textLayoutBuilder.AverageDigitWidth());
                    }
                }
            } 
        }

        internal int LeftMargin
        {
            get { return this.leftMargin; }
            set
            {
                if (this.leftMargin != value)
                {
                    this.leftMargin = value;

                    // Need to update all lines and recompute scroll bounds.
                    double maxVisualLineWidth;
                    int changeStart, changeEnd;
                    this.UpdateVisualLines(/*visualLineStartIndex*/ 0, /*forceRelayout*/ true, out maxVisualLineWidth, out changeStart, out changeEnd);
                    this.UpdateCaret(this.caret.Ordinal);
                    this.scrollBoundsManager.InitializeVerticalScrollBounds(this.AvailbleWidth);

                    // Render the newly shifted lines.
                    this.renderHost.InvalidateVisual();
                }
            }
        }

        internal float AvailbleWidth
        {
            get { return (float)renderHost.ActualWidth - this.LeftMargin; }
        }

        internal float AvailableHeight
        {
            get { return (float)renderHost.ActualHeight; }
        }

        internal int CaretOrdinal { get { return this.caret.Ordinal; } }
        public int SelectionBegin   { get { return this.selectionManager.GetSelectionBeginOrdinal(); } }
        public int SelectionEnd     { get { return this.selectionManager.GetSelectionEndOrdinal(); } }

        #endregion

        #region Syntax Highlighting

        void LanguageDetector_LanguageChange(SyntaxHighlighting.SyntaxHighlighterService syntaxHighlightingService)
        {
            this.syntaxHighlightingService = syntaxHighlightingService;
            if (this.hwndRenderTarget != null)
            {
                this.syntaxHighlightingService.InitDisplayResources(this.hwndRenderTarget);
                this.NotifyOfSettingsChange();
            }
        }

        #endregion

        #region Member Data
        D2DFactory                   d2dFactory;
        HwndRenderTarget             hwndRenderTarget;
        SolidColorBrush              defaultBackgroundBrush;
        SolidColorBrush              defaultForegroundBrush;
        SolidColorBrush              defaultSelectionBrush;
        readonly RenderHost          renderHost;
        readonly Document            document;
        TextLayoutBuilder            textLayoutBuilder;
        SelectionManager             selectionManager;
        Caret                        caret;
        SizeF                        scrollOffset;
        ScrollBoundsManager          scrollBoundsManager;
        ContentLineManager           contentLineManager;
        SyntaxHighlighterService     syntaxHighlightingService;

        int                          pageBeginOrdinal;
        double                       pageTop;

        // Cache of sequential visual lines such that the last line has a hard break and
        // that all the lines on screen are present in the collection.
        List<VisualLine>             visualLines;

        long                         lastMouseWheelTime;
        int                          leftMargin;
        #endregion
    }
}
