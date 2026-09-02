using Microsoft.VisualStudio;
using Microsoft.VisualStudio.TextManager.Interop;

namespace AxialSqlTools.IntelliSense
{
    internal static class EditorSelectionHelper
    {
        public static bool HasTextSelection(IVsTextView textView)
        {
            if (textView == null) return false;
            if (textView.GetSelection(out int startLine, out int startCol, out int endLine, out int endCol) != VSConstants.S_OK)
                return false;
            return startLine != endLine || startCol != endCol;
        }

        /// <summary>一次 COM 取出全文，避免按行 GetLineText。</summary>
        public static string GetFullText(IVsTextView textView)
        {
            if (textView == null) return string.Empty;
            if (textView.GetBuffer(out IVsTextLines lines) != VSConstants.S_OK || lines == null)
                return string.Empty;
            if (lines.GetLastLineIndex(out int lastLine, out int lastCol) != VSConstants.S_OK)
                return string.Empty;
            if (lines.GetLineText(0, 0, lastLine, lastCol, out string text) != VSConstants.S_OK || text == null)
                return string.Empty;
            return text;
        }

        public static int LineColToOffset(string text, int line, int col)
        {
            if (string.IsNullOrEmpty(text) || line < 0) return 0;
            int i = 0, at = 0, n = text.Length;
            while (at < line && i < n)
            {
                if (text[i] == '\n') at++;
                i++;
            }
            int remain = 0;
            while (i + remain < n && remain < col && text[i + remain] != '\r' && text[i + remain] != '\n')
                remain++;
            return i + remain;
        }

        public static void OffsetToLineCol(string text, int offset, out int line, out int col)
        {
            line = 0;
            col = 0;
            if (string.IsNullOrEmpty(text) || offset <= 0) return;
            if (offset > text.Length) offset = text.Length;
            for (int i = 0; i < offset; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    col = 0;
                }
                else if (text[i] != '\r')
                    col++;
            }
        }
    }
}
