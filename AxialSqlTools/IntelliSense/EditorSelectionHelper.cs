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
    }
}
